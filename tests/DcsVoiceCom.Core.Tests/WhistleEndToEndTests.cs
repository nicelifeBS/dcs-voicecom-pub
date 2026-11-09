using System.Runtime.InteropServices;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

/// <summary>
/// Push-to-talk on recorded commands through the real Whistle engine (the model bundled in model\cactus-needle)
/// and the phrase matcher. 20 English and 10 German commands.
/// The public run uses synthetic text-to-speech clips (data\clips, made by tools\make_tts_clips.py, 4 takes each).
/// The author's real-voice clips are private: they live in private-clips\ or in the folder named by the
/// DCS_REAL_CLIPS environment variable (record them with tools\eval\record.py), and that test skips without them.
/// </summary>
public class WhistleEndToEndTests
{
    private sealed class ClipMicrophone : IMicrophone
    {
        public float[] Clip = [];
        public string DeviceName => "recorded clips";
        public void Start() { }
        public float[] Stop() => Clip;
        public void Dispose() { }
    }

    private sealed class ClipMicrophones(ClipMicrophone microphone) : IMicrophoneProvider
    {
        public IReadOnlyList<string> ListDevices() => [microphone.DeviceName];
        public IMicrophone Open(string? device, int preRollMs) => microphone;
    }

    [Fact]
    public async Task Synthetic_commands_run_the_right_command_and_never_a_wrong_one()
    {
        // 4 takes x 30 commands; voice, speed, filter and noise vary per take (see MinRightSynthetic).
        await RunClips(Path.Combine(AppContext.BaseDirectory, "data", "clips"), MinRightSynthetic);
    }

    [SkippableFact]
    public async Task Real_voice_commands_run_the_right_command_and_never_a_wrong_one()
    {
        var clips = RealClipsFolder();
        Skip.If(clips is null, "no real-voice clips: record them with tools/eval/record.py into private-clips/ "
            + "or set DCS_REAL_CLIPS to a folder of <slug>__NN.wav (the clips are private, see issue #12)");
        // Linux engine 3.3.3, one headset take of each command: 28 of 30; the misses are "gear up" heard as "Kirapa"
        // (refused, 'up' not heard) and a garbled AWACS call.
        await RunClips(clips!, MinRightReal);
    }

    // Measured 98 of 120 with engine 3.3.3 on Linux (0 wrong). The misses are mostly the sped-up or filtered takes and
    // hard words (AWACS, Shkval, "Funk Kanal 5"). Loose enough for the Windows engine, tight enough to catch a regression.
    private const double MinRightSynthetic = 0.75;
    private const double MinRightReal = 0.9;

    private static string? RealClipsFolder()
    {
        var folder = Environment.GetEnvironmentVariable("DCS_REAL_CLIPS");
        if (string.IsNullOrEmpty(folder))
            folder = Path.Combine(RepositoryRoot(), "private-clips");
        return Directory.Exists(folder) && Directory.GetFiles(folder, "*.wav").Length > 0 ? folder : null;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "model", "cactus-needle"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    /// <summary>Runs every clip in the folder; no wrong command is ever acceptable, and at least minRight of the clips must be right.</summary>
    private static async Task RunClips(string clips, double minRight)
    {
        // The bundled engines are Windows x64 and Linux x64; anywhere else there is nothing to run.
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) return;
        var model = BundledModel();
        Assert.True(ModelLocator.Find(model) is not null, $"no Whistle model and {ModelLocator.EngineFileName} in {model}");

        var commands = File.ReadAllLines(CommandsFile(clips)).Skip(1).Select(l => l.Split(','))
            .ToDictionary(r => r[0], r => (Lang: r[1], Phrase: r[2]));
        var microphone = new ClipMicrophone();
        var settings = new SpeechSettings { ModelDirectory = model, AutoDownload = false };
        var log = new List<string>();
        var right = new List<string>();
        var missed = new List<string>();
        var wrong = new List<string>();

        foreach (var language in new[] { "en", "de" })
        {
            using var speech = await SpeechService.OpenAsync(settings, language, new ClipMicrophones(microphone), log.Add);
            Assert.True(speech.IsReady, string.Join("\n", log));
            var phrases = commands.Values.Where(c => c.Lang == language).Select(c => (PhraseText.Normalize(c.Phrase), c.Phrase));
            speech.LoadPhrases(language, phrases, []);

            foreach (var clip in Directory.GetFiles(clips, "*.wav").Order(StringComparer.Ordinal))
            {
                if (!commands.TryGetValue(Path.GetFileName(clip).Split("__")[0], out var said)) continue;
                if (said.Lang != language) continue;
                microphone.Clip = WavFile.ReadMono(clip, out _);
                speech.Start();
                var result = speech.Stop();
                Assert.Null(result.Error);

                var line = $"{said.Phrase}: heard \"{result.Raw}\" → {result.Phrase} ({result.Score}) {result.Reject}";
                if (result.Reject is not null || result.Score < 85) missed.Add(line);
                else if (result.Phrase == PhraseText.Normalize(said.Phrase)) right.Add(line);
                else wrong.Add(line);
            }
        }

        var total = right.Count + missed.Count + wrong.Count;
        Assert.True(wrong.Count == 0, "wrong commands:\n" + string.Join("\n", wrong));
        Assert.True(right.Count >= Math.Ceiling(total * minRight),
            $"only {right.Count} of {total} right; missed:\n" + string.Join("\n", missed));
    }

    /// <summary>The folder's own commands.csv, else the one next to the public clips.</summary>
    private static string CommandsFile(string clips)
    {
        var own = Path.Combine(clips, "commands.csv");
        return File.Exists(own) ? own : Path.Combine(AppContext.BaseDirectory, "data", "clips", "commands.csv");
    }

    /// <summary>The repository's model\cactus-needle, found above the test's output folder.</summary>
    private static string BundledModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var model = Path.Combine(dir.FullName, "model", "cactus-needle");
            if (Directory.Exists(model)) return model;
        }
        return Path.Combine(AppContext.BaseDirectory, "model", "cactus-needle");
    }
}
