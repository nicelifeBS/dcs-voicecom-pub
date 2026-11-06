using System.Reflection;
using System.Runtime.InteropServices;

namespace DcsVoiceCom.Speech;

/// <summary>
/// The C entry points of Cactus needle's engine (libneedle.dll / libneedle.so), the runtime Whistle runs on.
/// Mirrors what the cactus-needle Python package binds with ctypes (needle/agent/whistle.py, engine 3.3.3).
/// </summary>
internal static unsafe partial class NeedleNative
{
    public const string Library = "libneedle";

    private static string? s_libraryPath;

    /// <summary>
    /// Loads the engine from <paramref name="path"/> instead of the default probing (next to the app).
    /// Must be called before the first engine call; later calls with another path are rejected.
    /// </summary>
    public static void UseLibrary(string path)
    {
        path = Path.GetFullPath(path);
        lock (typeof(NeedleNative))
        {
            if (s_libraryPath is not null)
            {
                if (!string.Equals(s_libraryPath, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The needle engine is already bound to {s_libraryPath}.");
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(NeedleNative).Assembly, Resolve);
            s_libraryPath = path;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && s_libraryPath is not null ? NativeLibrary.Load(s_libraryPath) : IntPtr.Zero;

    /// <summary>Loads a .cact file's bytes. Negative on failure (see <see cref="needle_last_error"/>).</summary>
    [LibraryImport(Library)]
    public static partial int needle_load(byte* data, ulong size);

    /// <summary>
    /// Transcribes 16 kHz mono float samples. language and keywords are UTF-8, null-terminated, or null
    /// (detect the language / no biasing); keywords are newline-separated. Writes a null-terminated JSON object
    /// into output. Negative on failure.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int needle_transcribe(float* samples, int count, byte* language, byte* keywords,
        int wordTimestamps, byte* output, int outputSize);

    [LibraryImport(Library)]
    public static partial byte* needle_last_error();

    /// <summary>
    /// Sets an environment variable where the engine's C runtime will see it. Environment.SetEnvironmentVariable
    /// alone is not enough: the UCRT on Windows and libc on Linux keep their own copy of the environment.
    /// </summary>
    public static void SetCrtEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (OperatingSystem.IsWindows())
            _ = ucrt_putenv_s(name, value);
        else
            _ = libc_setenv(name, value, 1);
    }

    [LibraryImport("ucrtbase", EntryPoint = "_putenv_s", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ucrt_putenv_s(string name, string value);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int libc_setenv(string name, string value, int overwrite);
}
