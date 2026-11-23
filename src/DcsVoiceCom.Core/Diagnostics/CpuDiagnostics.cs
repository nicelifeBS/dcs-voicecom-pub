using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DcsVoiceCom.Diagnostics;

/// <summary>System-wide CPU time, busy and total, in the OS's own units. Two readings give the utilisation between them.</summary>
public readonly record struct CpuTimes(long Busy, long Total)
{
    /// <summary>The CPU time of the whole machine now, or null where it can't be read.</summary>
    public static CpuTimes? ReadSystem()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CpuNative.GetSystemTimes(out var idle, out var kernel, out var user)
                    // Kernel time includes idle time.
                    ? new CpuTimes(kernel + user - idle, kernel + user)
                    : null;
            if (OperatingSystem.IsLinux()) return ParseProcStat(File.ReadAllText("/proc/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
        }
        return null;
    }

    /// <summary>The first line of /proc/stat: "cpu user nice system idle iowait irq softirq steal …" in clock ticks.</summary>
    internal static CpuTimes? ParseProcStat(string text)
    {
        var parts = text.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu") return null;
        var values = new long[Math.Min(8, parts.Length - 1)];
        for (var i = 0; i < values.Length; i++)
            if (!long.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
        var total = values.Sum();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        return new CpuTimes(total - idle, total);
    }

    /// <summary>How busy the CPU was between two readings, 0–100, or null when either is missing or no time passed.</summary>
    public static double? Utilization(CpuTimes? from, CpuTimes? to)
    {
        if (from is not { } a || to is not { } b || b.Total <= a.Total) return null;
        return Math.Clamp(100.0 * (b.Busy - a.Busy) / (b.Total - a.Total), 0, 100);
    }
}

/// <summary>A one-line description of the processor, for the log: model, threads, cores and what the app may use.</summary>
public static class CpuInfo
{
    public static string Describe()
    {
        var parts = new List<string> { Name() ?? "unknown CPU" };
        var (threads, cores, performance, efficiency) = Topology();
        var layout = threads > 0 ? $"{threads} threads" : $"{Environment.ProcessorCount} threads";
        if (cores > 0) layout += $" on {cores} cores";
        if (performance > 0 && efficiency > 0) layout += $" ({performance} performance, {efficiency} efficiency)";
        parts.Add(layout);
        parts.Add($"app may use {Environment.ProcessorCount}");
        parts.Add($"priority {ProcessPriority.Current()}");
        return string.Join(" · ", parts);
    }

    private static string? Name()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            if (OperatingSystem.IsLinux())
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))
                    ?.Split(':', 2)[1].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return null;
    }

    /// <summary>Logical processors, physical cores, and on hybrid CPUs the performance and efficiency cores. Zero when unknown.</summary>
    private static (int Threads, int Cores, int Performance, int Efficiency) Topology()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsTopology();
            if (OperatingSystem.IsLinux()) return LinuxTopology();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
        }
        return (0, 0, 0, 0);
    }

    private static (int, int, int, int) LinuxTopology()
    {
        int threads = 0;
        var cores = new HashSet<string>();
        string physical = "", core = "";
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
            var pair = line.Split(':', 2);
            if (pair.Length < 2) continue;
            var (key, value) = (pair[0].Trim(), pair[1].Trim());
            if (key == "processor") threads++;
            else if (key == "physical id") physical = value;
            else if (key == "core id")
            {
                core = value;
                cores.Add(physical + "/" + core);
            }
        }
        return (threads, cores.Count, 0, 0);
    }

    private static unsafe (int, int, int, int) WindowsTopology()
    {
        const int RelationProcessorCore = 0;
        var threads = (int)CpuNative.GetActiveProcessorCount(0xFFFF); // ALL_PROCESSOR_GROUPS
        uint length = 0;
        CpuNative.GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
        if (length == 0) return (threads, 0, 0, 0);
        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!CpuNative.GetLogicalProcessorInformationEx(RelationProcessorCore, start, ref length)) return (threads, 0, 0, 0);
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (4), Size (4), then PROCESSOR_RELATIONSHIP:
            // Flags (1), EfficiencyClass (1), … A higher efficiency class is a faster core; all zero on non-hybrid CPUs.
            var classes = new List<byte>();
            for (uint offset = 0; offset + 10 <= length;)
            {
                var size = *(uint*)(start + offset + 4);
                if (size == 0) break;
                classes.Add(start[offset + 9]);
                offset += size;
            }
            if (classes.Count == 0) return (threads, 0, 0, 0);
            var fastest = classes.Max();
            var performance = classes.Count(c => c == fastest);
            return (threads, classes.Count, performance, classes.Count - performance);
        }
    }
}

/// <summary>
/// Raises the process to above-normal priority while Whistle transcribes, then sets it back. Whistle runs one thread
/// per core and they wait on each other, so when DCS keeps every core busy a command can take seconds instead of
/// tens of milliseconds. A priority the user set higher (High, Realtime) is never lowered.
/// </summary>
public sealed class ProcessPriority
{
    private readonly object _lock = new();
    private readonly Action<string> _log;
    private int _depth;
    private ProcessPriorityClass? _restore;
    private bool _failed;

    public ProcessPriority(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>The process's priority class now, or "unknown".</summary>
    public static string Current()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PriorityClass.ToString();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return "unknown";
        }
    }

    /// <summary>Raises the priority until the result is disposed. Nested and concurrent boosts share one raise.</summary>
    public IDisposable Boost()
    {
        lock (_lock)
        {
            if (_depth++ == 0 && !_failed)
            {
                try
                {
                    using var process = Process.GetCurrentProcess();
                    var now = process.PriorityClass;
                    if (now is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
                    {
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                        _restore = now;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
                {
                    // Raising needs a privilege on Linux; Windows allows above-normal for its own process.
                    _failed = true;
                    _log($"can't raise the speech priority: {ex.Message}");
                }
            }
        }
        return new Release(this);
    }

    private void End()
    {
        lock (_lock)
        {
            if (--_depth > 0 || _restore is not { } restore) return;
            _restore = null;
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = restore;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                _log($"can't restore the process priority: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Asks Windows not to throttle the app to save power (EcoQoS), which can put a background app on efficiency
    /// cores at a low clock. Returns false where it isn't supported (older Windows, other systems).
    /// </summary>
    public static bool DisablePowerThrottling()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var state = new CpuNative.PowerThrottlingState
            {
                Version = 1, // PROCESS_POWER_THROTTLING_CURRENT_VERSION
                ControlMask = 1, // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                StateMask = 0, // off: always run at full speed
            };
            return CpuNative.SetProcessInformation(CpuNative.GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state,
                (uint)Marshal.SizeOf<CpuNative.PowerThrottlingState>());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private sealed class Release(ProcessPriority owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.End();
        }
    }
}

internal static unsafe partial class CpuNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32")]
    public static partial uint GetActiveProcessorCount(ushort group);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, uint size);
}
