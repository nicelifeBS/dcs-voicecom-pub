using DcsVoiceCom.Config;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

/// <summary>Records what was spoken. <see cref="Gate"/> holds the speech open until released.</summary>
internal sealed class RecordingTextToSpeech(params VoiceInfo[] voices) : ITextToSpeech
{
    public IReadOnlyList<VoiceInfo> Voices { get; } = voices;
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

    [Fact]
    public void Picks_the_saved_voice_when_installed() =>
        Assert.Equal("en2", VoiceChooser.Pick(Voices, "en", "en2")?.Id);

    [Fact]
    public void Falls_back_to_the_first_voice_for_the_language()
    {
        Assert.Equal("en1", VoiceChooser.Pick(Voices, "en", "gone")?.Id);
        Assert.Equal("de1", VoiceChooser.Pick(Voices, "de-DE", null)?.Id);
    }

    [Fact]
    public void Language_matches_whole_tags_only()
    {
        Assert.True(VoiceChooser.MatchesLanguage("en-US", "en"));
        Assert.False(VoiceChooser.MatchesLanguage("end-XX", "en"));
    }

    [Fact]
    public void Returns_null_without_a_match()
    {
        Assert.Null(VoiceChooser.Pick(Voices, "fr", null));
        Assert.Null(VoiceChooser.Pick([], "en", null));
    }
}

public class TtsSettingsTests
{
    [Fact]
    public void Settings_without_a_tts_block_get_the_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "language": "de" }""");
        try
        {
            var tts = AppSettings.Load(path).Tts;
            Assert.True(tts.Enabled);
            Assert.Null(tts.Language);
            Assert.Null(tts.Voice);
            Assert.Null(tts.Device);
            Assert.Equal(1.0, tts.Rate);
            Assert.Equal(1.0, tts.Volume);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tts_block_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { Tts = new TtsSettings { Enabled = false, Voice = "v", Rate = 1.5 } }.Save(path);
            Assert.Contains("\"tts\"", File.ReadAllText(path));
            var tts = AppSettings.Load(path).Tts;
            Assert.False(tts.Enabled);
            Assert.Equal("v", tts.Voice);
            Assert.Equal(1.5, tts.Rate);
        }
        finally { File.Delete(path); }
    }
}

public class SayStepTests
{
    private static readonly VoiceInfo Voice = new("en1", "David", "en-US");

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(ITextToSpeech? tts, Func<TtsOptions?>? options, params Step[] steps)
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit,
            new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, tts, options)
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Aircraft = "TestJet",
                Commands = [new Command { Name = "Test", Phrases = ["test"], Steps = [.. steps] }],
            }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard() => new() { Phrase = "test", Score = 100, Raw = "test" };

    [Fact]
    public void Say_step_round_trips_as_json()
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
    }

    [Fact]
    public void Say_step_reads_in_the_editor_as_say_text() =>
        Assert.Equal("say \"Gear up\"", DcsVoiceCom.Editing.StepText.Describe(new SayStep { Text = "Gear up" }));

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
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, null, tts, () => new TtsOptions("en1"))
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Aircraft = "TestJet",
                Commands =
                [
                    new Command
                    {
                        Name = "Radio",
                        Phrases = ["set radio to {channel:1-20}"],
                        Steps = [new SayStep { Text = "Channel {channel+1}" }],
                    },
                ],
            }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "set radio to {channel:1-20}", Score = 100, Slots = "five" });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("Channel 6", Assert.Single(tts.Spoken).Text);
    }

    [Theory]
    [InlineData(false)] // no TTS at all
    [InlineData(true)]  // TTS present, but voice replies off / no voice: options are null
    public async Task Skips_without_a_voice_and_still_completes(bool withEngine)
    {
        var tts = withEngine ? new RecordingTextToSpeech() : null;
        var (engine, cockpit) = Create(tts, () => null, new SayStep { Text = "Gear up" }, new BiosStep { Control = "GEAR_LEVER", Arg = "0" });

        var outcome = await engine.HandleAsync(Heard());

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Contains("voice reply skipped (no voice)", outcome.Trace);
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

    private sealed class ThrowingTts : ITextToSpeech
    {
        public IReadOnlyList<VoiceInfo> Voices => [];
        public Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public void Stop() { }
    }
}
