using DcsVoiceCom.Diagnostics;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public sealed class CpuDiagnosticsTests
{
    [Fact]
    public void Proc_stat_counts_idle_and_iowait_as_not_busy()
    {
        var times = CpuTimes.ParseProcStat("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4\n");
        Assert.Equal(new CpuTimes(150, 1000), times);
        Assert.Null(CpuTimes.ParseProcStat("intr 1 2 3"));
    }

    [Fact]
    public void Utilization_is_the_busy_share_between_two_readings()
    {
        Assert.Equal(75, CpuTimes.Utilization(new CpuTimes(100, 1000), new CpuTimes(250, 1200)));
        Assert.Null(CpuTimes.Utilization(new CpuTimes(100, 1000), new CpuTimes(100, 1000)));
        Assert.Null(CpuTimes.Utilization(null, new CpuTimes(1, 2)));
    }

    [Fact]
    public void The_cpu_description_names_threads_and_priority()
    {
        var text = CpuInfo.Describe();
        Assert.Contains(" threads", text);
        Assert.Contains($"app may use {Environment.ProcessorCount}", text);
        Assert.Contains("priority ", text);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) Assert.NotNull(CpuTimes.ReadSystem());
    }

    [Fact]
    public void A_boost_puts_the_priority_back_afterwards()
    {
        var before = ProcessPriority.Current();
        var priority = new ProcessPriority();
        using (priority.Boost())
        using (priority.Boost())
        {
            // Raising needs a privilege on Linux; where it is missing the boost does nothing.
        }
        Assert.Equal(before, ProcessPriority.Current());
    }

    [Fact]
    public void Cpu_logging_writes_a_line_per_command()
    {
        var lines = new List<string>();
        using var speech = new SpeechService(new Recognizer(), new Microphone(), log: lines.Add, priority: new ProcessPriority(), logCpu: true);
        speech.Start();
        speech.Stop();
        var line = Assert.Single(lines, l => l.StartsWith("[cpu] ", StringComparison.Ordinal));
        Assert.Contains("while transcribing", line);
        Assert.Contains("Whistle ", line);
    }

    private sealed class Recognizer : ISpeechToText
    {
        public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords) => "gear up";
    }

    private sealed class Microphone : IMicrophone
    {
        public string DeviceName => "test";
        public void Start() { }
        public float[] Stop() => new float[8000];
        public void Dispose() { }
    }
}
