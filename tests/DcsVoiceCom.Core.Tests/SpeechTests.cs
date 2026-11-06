using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public sealed class ModelLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("locator").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Model(params string[] folder)
    {
        var dir = Path.Combine([_root, .. folder]);
        Touch(Path.Combine(dir, "whistle", "2.0.0", "whistle.cact"));
        Touch(Path.Combine(dir, "v3", "3.3.3", ModelLocator.EngineFileName));
        return dir;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private string AppFolder()
    {
        var app = Path.Combine(_root, "src", "dcs-voicecom", "dist", "DcsVoiceCom");
        Directory.CreateDirectory(app);
        return app;
    }

    // The test model files are empty; trust only that content, like the app trusts only the pinned files.
    private static readonly IReadOnlySet<string> Trusted =
        new HashSet<string> { Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])).ToLowerInvariant() };

    private string? Resolve(string? configured, string app) => ModelLocator.ResolveDirectory(configured, app, Home, Downloads, Trusted);

    private string Home => Path.Combine(_root, "home");
    private string Downloads => Path.Combine(_root, "appdata", "model");

    [Fact]
    public void Prefers_the_configured_folder() =>
        Assert.Equal(@"D:\models", Resolve(@" D:\models ", AppFolder()));

    [Fact]
    public void Prefers_the_bundled_copy_then_downloads_then_the_checkout_then_whistleattack_then_the_needle_cache()
    {
        var app = AppFolder();
        var cache = Model("home", ".cache", "cactus-needle");
        Assert.Equal(cache, Resolve(null, app));
        var whistleAttack = Model("src", "WhistleAttack", "model", "cactus-needle");
        Assert.Equal(whistleAttack, Resolve(null, app));
        var checkout = Model("src", "dcs-voicecom", "model", "cactus-needle");
        Assert.Equal(checkout, Resolve(null, app));
        var downloads = Model("appdata", "model");
        Assert.Equal(downloads, Resolve(null, app));
        var bundled = Model("src", "dcs-voicecom", "dist", "DcsVoiceCom", "model", "cactus-needle");
        Assert.Equal(bundled, Resolve(null, app));
    }

    [Fact]
    public void Needs_both_the_weights_and_the_engine()
    {
        Touch(Path.Combine(Home, ".cache", "cactus-needle", "whistle", "2.0.0", "whistle.cact"));
        Assert.Null(Resolve(null, AppFolder()));
    }

    [Fact]
    public void Skips_found_files_that_are_not_pinned()
    {
        var app = AppFolder();
        var cache = Model("home", ".cache", "cactus-needle");
        var planted = Path.Combine(cache, "v3", "99.0", ModelLocator.EngineFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, "not the pinned engine");

        Assert.Equal(cache, Resolve(null, app));
        Assert.Equal(Path.Combine(cache, "v3", "3.3.3", ModelLocator.EngineFileName), ModelLocator.Find(cache, Trusted)!.Engine);
        Assert.Null(ModelLocator.ResolveDirectory(null, app, Home, Downloads));   // empty files aren't the pinned ones
    }

    [Fact]
    public void Finds_the_newest_versions()
    {
        var dir = Model("m");
        Touch(Path.Combine(dir, "whistle", "10.0.0", "whistle.cact"));
        Directory.CreateDirectory(Path.Combine(dir, "v3", "9.0.0")); // empty: skipped
        var files = ModelLocator.Find(dir)!;
        Assert.Equal(Path.Combine(dir, "whistle", "10.0.0", "whistle.cact"), files.Weights);
        Assert.Equal(Path.Combine(dir, "v3", "3.3.3", ModelLocator.EngineFileName), files.Engine);
    }
}

public class PushToTalkBufferTests
{
    [Fact]
    public void Keeps_the_pre_roll_and_everything_until_stop()
    {
        var buffer = new PushToTalkBuffer(sampleRate: 1000, preRollMs: 3);   // 3 samples of pre-roll
        buffer.Write([1, 2, 3, 4, 5]);
        buffer.Start();
        buffer.Write([6, 7]);
        Assert.Equal([3, 4, 5, 6, 7], buffer.Stop());
        buffer.Write([8]);
        Assert.Empty(buffer.Stop());
    }

    [Fact]
    public void Pre_roll_wraps_and_restarts_after_each_press()
    {
        var buffer = new PushToTalkBuffer(sampleRate: 1000, preRollMs: 2);
        buffer.Write([1, 2]);
        buffer.Write([3]);
        buffer.Start();
        Assert.Equal([2, 3], buffer.Stop());
        buffer.Write([9]);
        buffer.Start();
        Assert.Equal([9], buffer.Stop());
    }

    [Fact]
    public void Caps_the_recording_length()
    {
        var buffer = new PushToTalkBuffer(sampleRate: 1000, preRollMs: 2, maxRecordingMs: 5);   // 5 samples at most
        buffer.Write([1, 2]);
        buffer.Start();
        buffer.Write([3, 4]);
        buffer.Write([5, 6, 7]);
        buffer.Write([8]);
        Assert.Equal([1, 2, 3, 4, 5], buffer.Stop());
    }
}

public class SpeechServiceTests
{
    private sealed class FakeMicrophone(float[] clip) : IMicrophone
    {
        public bool Started;
        public string DeviceName => "Fake mic";
        public void Start() => Started = true;
        public float[] Stop() => clip;
        public void Dispose() { }
    }

    private sealed class FakeRecognizer(string text) : ISpeechToText
    {
        public (string Language, IReadOnlyList<string> Keywords)? Last;

        public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
        {
            Last = (language, keywords);
            return text;
        }
    }

    private static readonly (string, string)[] Phrases =
        [("gear up", "Gear up"), ("landing gear up", "Gear up"), ("gear down", "Gear down"), ("tacan mode {mode:off|on}", "TACAN mode")];

    [Fact]
    public void Push_to_talk_transcribes_with_the_profile_words_as_keywords_and_matches()
    {
        var recognizer = new FakeRecognizer("Keer up.");
        var mic = new FakeMicrophone(new float[8000]);
        using var speech = new SpeechService(recognizer, mic);
        Assert.Equal(4, speech.LoadPhrases("de", Phrases, ["Fahrwerk"]));

        speech.Start();
        var result = speech.Stop();

        Assert.True(mic.Started);
        Assert.Equal(("gear up", 100.0, "Keer up.", (string?)null), (result.Phrase, result.Score, result.Raw, result.Reject));
        Assert.Equal("de", recognizer.Last!.Value.Language);
        Assert.Equal(["Fahrwerk", "gear up", "landing gear up", "gear down", "tacan mode"], recognizer.Last.Value.Keywords);
    }

    [Fact]
    public void Typed_text_matches_without_a_model_and_push_to_talk_says_why()
    {
        using var speech = new SpeechService(null, null, unavailableReason: "speech unavailable: no model");
        speech.LoadPhrases("en", Phrases, []);

        var typed = speech.Match("tacan mode off");
        Assert.Equal(("tacan mode {mode:off|on}", "off"), (typed.Phrase, typed.Slots));
        Assert.Equal("speech unavailable: no model", speech.Stop().Error);
        Assert.False(speech.IsReady);
    }

    [Fact]
    public void Short_clips_are_not_transcribed()
    {
        using var speech = new SpeechService(new FakeRecognizer("gear up"), new FakeMicrophone(new float[1000]));
        Assert.Equal("too short", speech.Stop().Error);
    }

    [Fact]
    public void Near_ties_come_back_with_a_reason_and_the_candidates()
    {
        using var speech = new SpeechService(null, null);
        speech.LoadPhrases("en", [("radar on", "Radar on"), ("radar one", "Radar one")], []);
        var result = speech.Match("radar won");
        Assert.StartsWith("too close to call", result.Reject);
        Assert.Single(result.Candidates);
    }
}

public sealed class SpeechSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Directory.CreateTempSubdirectory("settings").FullName, "settings.json");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    [Fact]
    public void Old_sidecar_settings_keep_their_microphone_and_model_folder()
    {
        File.WriteAllText(_path, """
            {
              // written while speech ran in the Python sidecar
              "language": "de",
              "sidecar": { "pythonPath": "C:\\py\\python.exe", "modelDirectory": "D:\\models", "device": "Arctis", "port": 65434 },
            }
            """);

        var settings = Config.AppSettings.Load(_path);

        Assert.Equal("de", settings.Language);
        Assert.Equal(@"D:\models", settings.Speech.ModelDirectory);
        Assert.Equal("Arctis", settings.Speech.Device);
        settings.Save(_path);
        Assert.DoesNotContain("sidecar", File.ReadAllText(_path));
        Assert.Equal(settings.Speech, Config.AppSettings.Load(_path).Speech);
    }

    [Fact]
    public void Speech_settings_win_over_leftover_sidecar_ones()
    {
        File.WriteAllText(_path, """{ "speech": { "device": "Headset" }, "sidecar": { "device": "Arctis" } }""");
        Assert.Equal("Headset", Config.AppSettings.Load(_path).Speech.Device);
    }
}
