using DcsVoiceCom.Config;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

/// <summary>Records what was spoken. <see cref="Gate"/> holds the speech open until released.</summary>
internal sealed class RecordingTextToSpeech(params VoiceInfo[] voices) : ITextToSpeech
{
    public IReadOnlyList<VoiceInfo> Voices { get; } = voices;
    public IReadOnlyList<string> OutputDevices { get; set; } = [];
    public List<(string Text, TtsOptions Options)> Spoken { get; } = [];
    public TaskCompletionSource Gate { get; } = new();
    public int Stops;

    public async Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default)
    {
        lock (Spoken) Spoken.Add((text, options));
        await Gate.Task;
    }

    public void Stop() => Interlocked.Increment(ref Stops);
}

public class VoiceChooserTests
{
    private static readonly VoiceInfo[] Voices =
    [
        new("de1", "Hedda", "de-DE"),
        new("en1", "David", "en-US"),
        new("en2", "George", "en-GB"),
    ];

    [Theory]
    [InlineData("en", "en2", "en2")]  // the saved voice, when installed
    [InlineData("en", "gone", "en1")] // else the first voice for the language
    [InlineData("de-DE", null, "de1")]
    [InlineData("fr", null, null)]    // no voice for the language
    public void Picks_the_saved_voice_else_the_first_for_the_language(string language, string? saved, string? expected) =>
        Assert.Equal(expected, VoiceChooser.Pick(Voices, language, saved)?.Id);

    [Fact]
    public void Picks_nothing_without_voices() => Assert.Null(VoiceChooser.Pick([], "en", null));

    [Theory]
    [InlineData("en-US", "en", true)]
    [InlineData("en-US", "EN-us", true)]
    [InlineData("end-XX", "en", false)]
    public void Language_matches_whole_tags_only(string voiceLanguage, string language, bool expected) =>
        Assert.Equal(expected, VoiceChooser.MatchesLanguage(voiceLanguage, language));
}

public class TtsSettingsTests
{
    [Fact]
    public void Tts_block_defaults_when_missing_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "language": "de" }""");
            Assert.Equal(new TtsSettings(), AppSettings.Load(path).Tts);

            var saved = new TtsSettings { Enabled = false, Voice = "v", Rate = 1.5, Volume = 0.4, Device = "Headset" };
            new AppSettings { Tts = saved }.Save(path);
            Assert.Contains("\"tts\"", File.ReadAllText(path));
            Assert.Equal(saved, AppSettings.Load(path).Tts);
        }
        finally { File.Delete(path); }
    }
}

public class SayStepTests
{
    private static readonly VoiceInfo Voice = new("en1", "David", "en-US");

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(ITextToSpeech? tts, Func<TtsOptions?>? options, params Step[] steps) =>
        Create("test", tts, options, steps);

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(string phrase, ITextToSpeech? tts, Func<TtsOptions?>? options, params Step[] steps)
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit,
            new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, tts, options)
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Aircraft = "TestJet",
                Commands = [new Command { Name = "Test", Phrases = [phrase], Steps = [.. steps] }],
            }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard() => new() { Phrase = "test", Score = 100, Raw = "test" };

    [Fact]
    public void Say_step_round_trips_as_json_and_reads_as_say_text()
    {
        var profile = new Profile
        {
            Aircraft = "TestJet",
            Commands = [new Command { Name = "Gear", Phrases = ["gear up"], Steps = [new SayStep { Text = "Gear up" }] }],
        };
        var json = ProfileSerializer.Serialize(profile);
        Assert.Contains("\"type\": \"say\"", json);
        Assert.Contains("\"text\": \"Gear up\"", json);
        var back = Assert.IsType<SayStep>(Assert.Single(ProfileSerializer.Deserialize(json).Commands[0].Steps));
        Assert.Equal("Gear up", back.Text);
        Assert.Equal("say \"Gear up\"", DcsVoiceCom.Editing.StepText.Describe(back));
    }

    [Fact]
    public async Task Queues_the_text_and_does_not_wait_for_the_speech()
    {
        var tts = new RecordingTextToSpeech(Voice); // never finishes: the gate stays closed
        var (engine, cockpit) = Create(tts, () => new TtsOptions("en1", 1.2, 0.5),
            new SayStep { Text = "Gear up" }, new BiosStep { Control = "GEAR_LEVER", Arg = "0" });

        var outcome = await engine.HandleAsync(Heard()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["GEAR_LEVER 0"], cockpit.Sent);
        Assert.Contains("say \"Gear up\"", outcome.Trace);
        var spoken = Assert.Single(tts.Spoken);
        Assert.Equal("Gear up", spoken.Text);
        Assert.Equal(new TtsOptions("en1", 1.2, 0.5), spoken.Options);
        tts.Gate.SetResult();
    }

    [Fact]
    public async Task Fills_in_placeholders()
    {
        var tts = new RecordingTextToSpeech(Voice);
        tts.Gate.SetResult();
        var (engine, _) = Create("set radio to {channel:1-20}", tts, () => new TtsOptions("en1"),
            new SayStep { Text = "Channel {channel+1}" });

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "set radio to {channel:1-20}", Score = 100, Slots = "five" });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("Channel 6", Assert.Single(tts.Spoken).Text);
    }

    [Theory]
    [InlineData(false, "voice reply skipped (no voice)")] // no TTS at all
    [InlineData(true, "voice reply skipped (voice replies off, or no voice for the language)")] // TTS present, options null
    public async Task Skips_without_a_voice_and_still_completes(bool withEngine, string expectedTrace)
    {
        var tts = withEngine ? new RecordingTextToSpeech() : null;
        var (engine, cockpit) = Create(tts, () => null, new SayStep { Text = "Gear up" }, new BiosStep { Control = "GEAR_LEVER", Arg = "0" });

        var outcome = await engine.HandleAsync(Heard());

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Contains(expectedTrace, outcome.Trace);
        Assert.Equal(["GEAR_LEVER 0"], cockpit.Sent);
        if (tts != null) Assert.Empty(tts.Spoken);
    }

    [Fact]
    public async Task Dry_run_traces_but_does_not_speak()
    {
        var tts = new RecordingTextToSpeech(Voice);
        var (engine, _) = Create(tts, () => new TtsOptions("en1"), new SayStep { Text = "Gear up" });

        var outcome = await engine.HandleAsync(Heard(), dryRun: true);

        Assert.Contains("say \"Gear up\"", outcome.Trace);
        Assert.Empty(tts.Spoken);
    }

    [Fact]
    public async Task A_failing_voice_does_not_break_the_command()
    {
        var (engine, _) = Create(new ThrowingTts(), () => new TtsOptions("x"), new SayStep { Text = "Hi" });
        var outcome = await engine.HandleAsync(Heard());
        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
    }

    [Fact]
    public void Stop_speech_stops_the_voice()
    {
        var tts = new RecordingTextToSpeech(Voice);
        var (engine, _) = Create(tts, () => new TtsOptions("en1"));
        engine.StopSpeech();
        Assert.Equal(1, tts.Stops);
    }

    [Fact]
    public void Slot_names_replace_placeholders_for_a_preview() =>
        Assert.Equal("Radio set to freq, channel channel", Placeholders.ToSlotNames("Radio set to {freq}, channel {channel+1}"));

    private sealed class ThrowingTts : ITextToSpeech
    {
        public IReadOnlyList<VoiceInfo> Voices => [];
        public IReadOnlyList<string> OutputDevices => [];
        public Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public void Stop() { }
    }
}
