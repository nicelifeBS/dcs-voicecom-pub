using DcsVoiceCom.Config;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public class TtsValueFormatTests
{
    private static TtsValue Slice(string template, int pad = 0) => new() { Name = "v", Output = "X", Mode = TtsValueMode.Slice, Template = template, Pad = pad };

    private static TtsValue Remap(double inMin = 0, double inMax = 65535, double outMin = 0, double outMax = 100, int decimals = 0) =>
        new() { Name = "v", Output = "X", Mode = TtsValueMode.Remap, InMin = inMin, InMax = inMax, OutMin = outMin, OutMax = outMax, Decimals = decimals };

    [Theory]
    [InlineData("{0-1}:{2-3}:{4-5}", 6, "203422", "20:34:22")]
    [InlineData("{0-1}:{2-3}:{4-5}", 6, "509", "00:05:09")] // padded first
    [InlineData("{0-1}:{2-3}", 0, "2034", "20:34")]
    [InlineData("channel {1}", 0, "42", "channel 2")]       // one character
    [InlineData("no pieces", 0, "42", "no pieces")]
    public void Slices_integers(string template, int pad, string raw, string expected) =>
        Assert.Equal(expected, TtsValueFormat.Format(Slice(template, pad), raw, isInteger: true));

    [Fact]
    public void Slices_strings_as_they_are_without_padding() =>
        Assert.Equal("8450", TtsValueFormat.Format(Slice("{2-5}", pad: 8), "  8450", isInteger: false));

    [Fact]
    public void A_slice_past_the_end_is_an_error()
    {
        var ex = Assert.Throws<StepException>(() => TtsValueFormat.Format(Slice("{0-1}:{2-3}:{4-5}"), "2034", isInteger: true));
        Assert.Contains("{4-5}", ex.Message);
    }

    [Theory]
    [InlineData(52428, 0, "80")]
    [InlineData(0, 0, "0")]
    [InlineData(65535, 0, "100")]
    [InlineData(32768, 1, "50.0")]
    [InlineData(100, 2, "0.15")]
    public void Remaps_linearly_and_rounds(int raw, int decimals, string expected) =>
        Assert.Equal(expected, TtsValueFormat.Format(Remap(decimals: decimals), raw.ToString(), isInteger: true));

    [Theory]
    [InlineData(70000, "100")]
    [InlineData(-5, "0")]
    public void Remap_clamps_to_the_spoken_range(int raw, string expected) =>
        Assert.Equal(expected, TtsValueFormat.Format(Remap(), raw.ToString(), isInteger: true));

    [Fact]
    public void Remap_works_on_a_reversed_range() =>
        Assert.Equal("25", TtsValueFormat.Format(Remap(0, 100, 100, 0), "75", isInteger: true));

    [Fact]
    public void Remap_uses_defaults_for_missing_ranges() =>
        Assert.Equal("80", TtsValueFormat.Format(new TtsValue { Name = "t", Output = "X", Mode = TtsValueMode.Remap }, "52428", isInteger: true));

    [Fact]
    public void Remap_of_text_is_an_error() =>
        Assert.Throws<StepException>(() => TtsValueFormat.Format(Remap(), "ABC", isInteger: false));

    [Fact]
    public void Validate_finds_setup_mistakes()
    {
        Assert.Empty(TtsValueFormat.Validate(Slice("{0-1}")));
        Assert.Contains(TtsValueFormat.Validate(new TtsValue { Name = "two words", Output = "X", Template = "{0}" }), p => p.Contains("name"));
        Assert.Contains(TtsValueFormat.Validate(new TtsValue { Name = "a", Output = "", Template = "{0}" }), p => p.Contains("output"));
        Assert.Contains(TtsValueFormat.Validate(Slice("")), p => p.Contains("template"));
        Assert.Contains(TtsValueFormat.Validate(Slice("{3-1}")), p => p.Contains("{3-1}"));
        Assert.Contains(TtsValueFormat.Validate(Remap(5, 5)), p => p.Contains("range"));
    }

    [Fact]
    public void Reads_integer_then_string_outputs_and_needs_a_live_cockpit()
    {
        var cockpit = new FakeCockpit();
        cockpit.Integers["CLOCK"] = 509;
        cockpit.Strings["FUEL"] = "  8450";
        var clock = new TtsValue { Name = "clock", Output = "CLOCK", Template = "{0-1}:{2-3}:{4-5}", Pad = 6 };
        var fuel = new TtsValue { Name = "fuel", Output = "FUEL", Template = "{2-5}" };

        Assert.True(TtsValueFormat.TryRead(clock, cockpit, out var text, out _));
        Assert.Equal("00:05:09", text);
        Assert.True(TtsValueFormat.TryRead(fuel, cockpit, out text, out _));
        Assert.Equal("8450", text);
        Assert.False(TtsValueFormat.TryRead(new TtsValue { Name = "x", Output = "NOPE", Template = "{0}" }, cockpit, out _, out var why));
        Assert.Contains("NOPE", why);

        cockpit.IsLive = false;
        Assert.False(TtsValueFormat.TryRead(clock, cockpit, out _, out why));
        Assert.Equal("no DCS data", why);
    }
}

public class TtsValueProfileTests
{
    private static readonly VoiceInfo Voice = new("en1", "David", "en-US");

    private static readonly TtsValue Throttle = new()
    {
        Name = "throttle", Output = "THROTTLE", Mode = TtsValueMode.Remap, InMin = 0, InMax = 65535, OutMin = 0, OutMax = 100,
    };

    private static (CommandEngine Engine, FakeCockpit Cockpit, RecordingTextToSpeech Tts) Create(string phrase, string say, params TtsValue[] values)
    {
        var cockpit = new FakeCockpit();
        var tts = new RecordingTextToSpeech(Voice);
        tts.Gate.SetResult();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions(), tts, () => new TtsOptions("en1"))
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Aircraft = "TestJet",
                Commands = [new Command { Name = "Test", Phrases = [phrase], Steps = [new SayStep { Text = say }] }],
                Values = [.. values],
            }),
        };
        return (engine, cockpit, tts);
    }

    [Fact]
    public async Task Say_speaks_a_live_value()
    {
        var (engine, cockpit, tts) = Create("throttle check", "Throttle {throttle} percent", Throttle);
        cockpit.Integers["THROTTLE"] = 52428;

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "throttle check", Score = 100 });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("Throttle 80 percent", Assert.Single(tts.Spoken).Text);
    }

    [Fact]
    public async Task Say_is_skipped_and_logged_without_DCS_data()
    {
        var (engine, cockpit, tts) = Create("throttle check", "Throttle {throttle} percent", Throttle);
        cockpit.IsLive = false;

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "throttle check", Score = 100 });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Contains("voice reply skipped ({throttle}: no DCS data)", outcome.Trace);
        Assert.Empty(tts.Spoken);
    }

    [Fact]
    public async Task A_spoken_slot_wins_over_a_value_of_the_same_name()
    {
        var value = new TtsValue { Name = "channel", Output = "CHAN", Template = "{0}" };
        var (engine, cockpit, tts) = Create("channel {channel:1-20}", "Channel {channel}", value);
        cockpit.Integers["CHAN"] = 9;

        await engine.HandleAsync(new RecognitionResult { Phrase = "channel {channel:1-20}", Score = 100, Slots = "five" });

        Assert.Equal("Channel 5", Assert.Single(tts.Spoken).Text);
    }

    [Fact]
    public void Values_round_trip_as_json_and_are_left_out_when_empty()
    {
        var profile = new Profile
        {
            Aircraft = "TestJet",
            Values = [new TtsValue { Name = "clock", Output = "CLOCK", Pad = 6, Template = "{0-1}:{2-3}" }, Throttle],
        };
        var json = ProfileSerializer.Serialize(profile);
        Assert.Contains("\"mode\": \"remap\"", json);
        Assert.Contains("\"pad\": 6", json);
        var back = ProfileSerializer.Deserialize(json).Values!;
        Assert.Equal(2, back.Count);
        Assert.Equal("{0-1}:{2-3}", back[0].Template);
        Assert.Equal(TtsValueMode.Remap, back[1].Mode);
        Assert.Equal(65535, back[1].InMax);

        Assert.DoesNotContain("values", ProfileSerializer.Serialize(new Profile { Aircraft = "TestJet" }));
    }

    [Fact]
    public void Merge_keeps_the_aircraft_value_of_the_same_name()
    {
        var aircraft = new Profile { Aircraft = "TestJet", Values = [new TtsValue { Name = "clock", Output = "A" }] };
        var common = new Profile { Aircraft = "common", Values = [new TtsValue { Name = "CLOCK", Output = "B" }, new TtsValue { Name = "fuel", Output = "C" }] };

        var merged = ProfileStore.Merge(aircraft, common).Values!;

        Assert.Equal(["A", "C"], merged.Select(v => v.Output));
    }

    [Fact]
    public void Compile_reports_bad_and_duplicate_values()
    {
        var compiled = CompiledProfile.Compile(new Profile
        {
            Aircraft = "TestJet",
            Values = [new TtsValue { Name = "a", Output = "X", Template = "" }, new TtsValue { Name = "a", Output = "X", Template = "{0}" }],
        });
        Assert.Contains(compiled.Problems, p => p.Contains("{a}") && p.Contains("template"));
        Assert.Contains(compiled.Problems, p => p.Contains("twice"));
    }
}
