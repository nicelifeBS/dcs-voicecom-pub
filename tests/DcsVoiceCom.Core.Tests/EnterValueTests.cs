using System.Text.Json;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class EnterValueMathTests
{
    [Theory]
    [InlineData("251.075", "10", 2, 25)]
    [InlineData("251.075", "1", 1, 1)]
    [InlineData("251.075", "0.1", 1, 0)]
    [InlineData("251.075", "0.001", 2, 75)]
    [InlineData("251", "0.001", 2, 0)]
    [InlineData("399.975", "0.001", 2, 75)]
    [InlineData("399.975", "0.1", 1, 9)]
    [InlineData("126", "10", 2, 12)]
    public void Digits_takes_the_digits_at_a_place(string value, string place, int width, int expected)
    {
        Assert.True(EnterValue.TryParseNumber(value, out var v));
        Assert.True(EnterValue.TryParseNumber(place, out var p));
        Assert.Equal(expected, EnterValue.Digits(v, p, width));
    }

    [Theory]
    [InlineData("251.000", "251.0", true)]
    [InlineData(" 251.00 ", "251", true)]
    [InlineData("251.025", "251.0", false)]
    [InlineData("ab1", "AB1", true)]
    [InlineData("AB1 ", "AB", false)]
    [InlineData("NN552~                      ", "NN552", true)]
    [InlineData("NN552~", "nn552", true)]
    [InlineData("NN5520", "NN552", false)]
    [InlineData("XNN552", "NN552", false)]
    [InlineData(".    127", "127", true)]
    [InlineData(".    12~", "122", false)]
    [InlineData("COM1 251.300 AM", "251.3", true)]
    [InlineData("251.325", "251", false)]
    [InlineData("251.325", "325", false)]
    [InlineData("1251.3", "251.3", false)]
    [InlineData("WP 12 N41 23", "N41 23", true)]
    public void Display_matches_numbers_as_numbers_and_text_ignoring_case(string display, string expected, bool matches) =>
        Assert.Equal(matches, EnterValue.DisplayMatches(display, expected));

    [Fact]
    public void Check_number_reports_range_and_bad_input()
    {
        var step = new EnterValueStep { Min = 225, Max = 399.975m };
        Assert.Null(EnterValue.CheckNumber(step, "251.0", out _));
        Assert.Contains("outside", EnterValue.CheckNumber(step, "400", out _));
        Assert.Contains("not a number", EnterValue.CheckNumber(step, "abc", out _));
    }

    [Fact]
    public void Range_of_the_top_wheel_comes_from_min_and_max()
    {
        var step = new EnterValueStep
        {
            Min = 225, Max = 399.975m,
            Wheels = [new() { Place = 10, Width = 2 }, new() { Place = 1 }, new() { Place = 0.001m, Width = 2 }],
        };
        Assert.Equal((22, 39), EnterValue.Range(step, step.Wheels[0]));
        Assert.Equal((0, 9), EnterValue.Range(step, step.Wheels[1]));
        Assert.Equal((0, 99), EnterValue.Range(step, step.Wheels[2]));

        var presets = new EnterValueStep { Min = 1, Max = 20, Wheels = [new() { Place = 1, Width = 2 }] };
        Assert.Equal((1, 20), EnterValue.Range(presets, presets.Wheels[0]));
    }

    [Theory]
    [InlineData(25, 26, 22, 39, 1)]
    [InlineData(39, 22, 22, 39, 1)]   // wrapped past the top
    [InlineData(22, 39, 22, 39, -1)]  // wrapped past the bottom
    [InlineData(20, 1, 1, 20, 1)]
    [InlineData(9, 0, 0, 9, 1)]
    [InlineData(75, 0, 0, 99, 1)]
    [InlineData(0, 25, 0, 99, 1)]
    [InlineData(5, 5, 0, 9, 0)]
    public void Turn_direction_sees_a_wrap_around(int before, int after, int low, int high, int expected) =>
        Assert.Equal(expected, EnterValue.TurnDirection(before, after, (low, high)));

    [Fact]
    public void Key_presses_skip_empty_keys_and_add_before_and_after()
    {
        var step = new EnterValueStep
        {
            Method = EnterMethod.Keypad,
            Before = ["CDU_CLR"],
            After = ["CDU_LSK_3L"],
            Keys = new() { ["A"] = "CDU_A", ["1"] = "CDU_1", [" "] = "" },
        };
        Assert.Equal(["CDU_CLR", "CDU_A", "CDU_1", "CDU_LSK_3L"], EnterValue.KeyPresses(step, "a 1"));
        Assert.Equal(['B'], EnterValue.MissingKeys(step, "AB1"));
    }
}

public class TextSlotTests
{
    [Theory]
    [InlineData("alpha bravo one", "AB1")]
    [InlineData("november four one decimal two", "N41.2")]
    [InlineData("x ray yankee slash two five", "XY/25")]
    [InlineData("w p three", "WP3")]
    public void Spells_letters_and_digits(string spoken, string expected)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("waypoint name {name:text}"), spoken);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Values["name"]);
    }

    [Fact]
    public void Stops_at_the_next_literal_word()
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("waypoint {name:text} channel {n:1-9}"), "alpha one channel five");
        Assert.True(result.Success, result.Error);
        Assert.Equal("A1", result.Values["name"]);
        Assert.Equal("5", result.Values["n"]);
    }

    [Theory]
    [InlineData("callsign {cs:text} {chan:number}", "alpha bravo two five", "AB", "25")]
    [InlineData("callsign {cs:text} {chan:1-20}", "x ray one two", "X", "12")]
    public void Leaves_trailing_numbers_to_a_number_slot_right_after(string phrase, string spoken, string text, string number)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse(phrase), spoken);
        Assert.True(result.Success, result.Error);
        Assert.Equal(text, result.Values["cs"]);
        Assert.Equal(number, result.Values["chan"]);
    }

    [Theory]
    [InlineData("TGT2.", "TGT2")]
    [InlineData("TGT 2.", "TGT2")]
    [InlineData("tango golf tango two", "TGT2")]
    [InlineData("NN552", "NN552")]
    [InlineData("alpha banana", "ABANANA")]
    public void Text_takes_words_as_heard_without_spaces(string spoken, string expected)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("enter {text}"), spoken);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Values["text"]);
    }

    [Theory]
    [InlineData("go {number}", "two five", true, "25")]
    [InlineData("go {number}", "two five decimal five", false, null)]
    [InlineData("go {freq}", "two five five decimal eight", true, "255.8")]
    [InlineData("go {n:number}", "seven", true, "7")]
    [InlineData("go {f:freq}", "one decimal five", true, "1.5")]
    public void Number_is_whole_and_freq_has_decimals(string phrase, string spoken, bool success, string? expected)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse(phrase), spoken);
        Assert.Equal(success, result.Success);
        if (success) Assert.Equal(expected, Assert.Single(result.Values).Value);
    }

    [Fact]
    public void A_slot_without_a_known_type_is_an_error()
    {
        var error = Assert.Throws<FormatException>(() => PhraseTemplate.Parse("push {num}"));
        Assert.Contains("{number}, {freq} or {text}", error.Message);
    }
}

public class EnterValueEngineTests
{
    /// <summary>A pretend F-14 pilot UHF: four spring wheels (2 = click up, 0 = click down) and their read-backs.</summary>
    private sealed class Uhf
    {
        public int Th = 25, One = 1, D3, Q;
        public bool InvertTenth;
        public bool Stuck;

        public void Attach(FakeCockpit cockpit)
        {
            Publish(cockpit);
            cockpit.OnSend = (c, id, arg) =>
            {
                if (arg is not ("2" or "0") || Stuck) return;
                var dir = arg == "2" ? 1 : -1;
                switch (id)
                {
                    case "PLT_UHF1_110_DIAL": Th = Wrap(Th + dir, 22, 39); break;
                    case "PLT_UHF1_1_DIAL": One = Wrap(One + dir, 0, 9); break;
                    case "PLT_UHF1_01_DIAL": D3 = Wrap(D3 + (InvertTenth ? -dir : dir), 0, 9); break;
                    case "PLT_UHF1_025_DIAL": Q = Wrap(Q + dir, 0, 3); break;
                }
                Publish(c);
            };
        }

        private void Publish(FakeCockpit c)
        {
            c.Integers["PLT_UHF_HIGH_FREQ"] = Th * 10 + One;
            c.Integers["PLT_UHF_DIAL3_FREQ"] = D3;
            c.Integers["PLT_UHF_DIAL4_FREQ"] = Q * 25;
            c.Strings["PLT_UHF_DISP"] = $"{Th * 10 + One}.{D3}{Q * 25:00}";
        }

        private static int Wrap(int v, int min, int max) => v > max ? min : v < min ? max : v;
    }

    private static EnterValueStep UhfStep() => new()
    {
        Value = "{freq}",
        Method = EnterMethod.Wheels,
        Min = 225,
        Max = 399.975m,
        DelayMs = 0,
        Wheels =
        [
            new() { Control = "PLT_UHF1_110_DIAL", Place = 10, Width = 2, Read = "PLT_UHF_HIGH_FREQ" },
            new() { Control = "PLT_UHF1_1_DIAL", Place = 1, Read = "PLT_UHF_HIGH_FREQ" },
            new() { Control = "PLT_UHF1_01_DIAL", Place = 0.1m, Read = "PLT_UHF_DIAL3_FREQ", ReadScale = 0.1m },
            new() { Control = "PLT_UHF1_025_DIAL", Place = 0.001m, Width = 2, Read = "PLT_UHF_DIAL4_FREQ", ReadScale = 0.001m },
        ],
        Verify = new() { Output = "PLT_UHF_DISP", TimeoutMs = 200 },
    };

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(Step step, string phrase = "uhf {freq}")
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(2) })
        {
            Profile = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "Enter", Phrases = [phrase], Steps = [step] }] }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard(string phrase, string slots) => new() { Phrase = phrase, Score = 100, Slots = slots, Raw = phrase };

    [Fact]
    public async Task Clicks_each_wheel_to_the_spoken_frequency_and_checks_the_display()
    {
        var (engine, cockpit) = Create(UhfStep());
        var uhf = new Uhf();
        uhf.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two six three decimal one seven five"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("263.175", cockpit.Strings["PLT_UHF_DISP"]);
        Assert.Contains(outcome.Trace, t => t.StartsWith("PLT_UHF_DISP = "));
    }

    [Fact]
    public async Task Swaps_up_and_down_when_a_wheel_turns_the_other_way()
    {
        var (engine, cockpit) = Create(UhfStep());
        var uhf = new Uhf { InvertTenth = true };
        uhf.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two five one decimal three"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(3, uhf.D3);
        Assert.Contains(outcome.Trace, t => t.Contains("swapping up and down"));
    }

    [Fact]
    public async Task Skips_everything_when_the_display_already_shows_the_value()
    {
        var (engine, cockpit) = Create(UhfStep());
        new Uhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two five one decimal zero"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task A_stuck_wheel_is_refused()
    {
        var (engine, cockpit) = Create(UhfStep());
        new Uhf { Stuck = true }.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two six one decimal zero"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Contains("did not move", outcome.Message);
    }

    [Fact]
    public async Task A_value_the_wheel_cannot_show_is_refused_after_passing_it()
    {
        var (engine, cockpit) = Create(UhfStep());
        new Uhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two five one decimal zero one"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Contains("cannot show", outcome.Message);
    }

    [Fact]
    public async Task A_wrap_on_a_22_to_39_wheel_is_seen_as_a_wrong_way_turn()
    {
        var step = UhfStep();
        step.Wheels![0].Flip = true; // wrong: the first click goes up, from 39 around to 22
        var (engine, cockpit) = Create(step);
        var uhf = new Uhf { Th = 39 };
        uhf.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "three zero one decimal zero"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("301.000", cockpit.Strings["PLT_UHF_DISP"]);
        Assert.Contains(outcome.Trace, t => t.Contains("swapping up and down"));
    }

    [Theory]
    [InlineData(false, OutcomeStatus.Completed)]
    [InlineData(true, OutcomeStatus.Refused)]
    public async Task Set_mode_checks_the_read_back(bool positionIndex, OutcomeStatus expected)
    {
        var step = new EnterValueStep
        {
            Value = "{n}", DelayMs = 0,
            Wheels = [new() { Control = "DIGIT", Place = 1, Mode = WheelMode.Set, Read = "DIGIT_POS" }],
        };
        var (engine, cockpit) = Create(step, "digit {n:number}");
        cockpit.Integers["DIGIT_POS"] = 0;
        cockpit.OnSend = (c, id, arg) => c.Integers["DIGIT_POS"] = positionIndex ? int.Parse(arg) / 2 : int.Parse(arg);

        var outcome = await engine.HandleAsync(Heard("digit {n:number}", "six"));

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(["DIGIT 6"], cockpit.Sent);
        if (positionIndex) Assert.Contains("set_state", outcome.Message);
    }

    [Fact]
    public async Task Out_of_range_sends_nothing()
    {
        var (engine, cockpit) = Create(UhfStep());
        new Uhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "one two one decimal five"));

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task Dry_run_lists_the_wheel_targets()
    {
        var (engine, cockpit) = Create(UhfStep());
        new Uhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two six three decimal one seven five"), dryRun: true);

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Contains(outcome.Trace, t => t.Contains("PLT_UHF1_110_DIAL → 26") && t.Contains("PLT_UHF1_025_DIAL → 75"));
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task Types_spelled_text_on_a_keypad()
    {
        var step = new EnterValueStep
        {
            Value = "{name}",
            Method = EnterMethod.Keypad,
            DelayMs = 0,
            Before = ["CDU_CLR"],
            Keys = new() { ["A"] = "CDU_A", ["B"] = "CDU_B", ["1"] = "CDU_1" },
        };
        var (engine, cockpit) = Create(step, "waypoint name {name:text}");
        cockpit.OnSend = null;

        var outcome = await engine.HandleAsync(Heard("waypoint name {name:text}", "alpha bravo one"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["CDU_CLR 1", "CDU_CLR 0", "CDU_A 1", "CDU_A 0", "CDU_B 1", "CDU_B 0", "CDU_1 1", "CDU_1 0"], cockpit.Sent);
    }

    [Fact]
    public async Task A_character_without_a_key_sends_nothing()
    {
        var step = new EnterValueStep { Value = "{name}", Method = EnterMethod.Keypad, Keys = new() { ["A"] = "CDU_A" } };
        var (engine, cockpit) = Create(step, "waypoint name {name:text}");

        var outcome = await engine.HandleAsync(Heard("waypoint name {name:text}", "alpha zulu"));

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Contains("\"Z\"", outcome.Message);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var json = JsonSerializer.Serialize<Step>(UhfStep(), ProfileSerializer.Options);
        Assert.Contains("\"type\": \"enter_value\"", json);
        Assert.Contains("\"method\": \"wheels\"", json);
        Assert.DoesNotContain("\"keys\"", json);

        var back = Assert.IsType<EnterValueStep>(JsonSerializer.Deserialize<Step>(json, ProfileSerializer.Options));
        Assert.Equal(4, back.Wheels!.Count);
        Assert.Equal(0.001m, back.Wheels[3].Place);
        Assert.Equal("PLT_UHF_DISP", back.Verify!.Output);
    }

    [Fact]
    public void Compile_reports_a_click_wheel_without_read_back()
    {
        var step = UhfStep();
        step.Wheels![0].Read = null;
        var compiled = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "UHF", Phrases = ["uhf {freq}"], Steps = [step] }] });
        Assert.Contains(compiled.Problems, p => p.Contains("needs a read-back"));
    }
}

public class PresetChannelTests
{
    /// <summary>A preset selector: INC/DEC move the channel 1-20 (wrapping), the display shows it as text.</summary>
    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(int channel, string? display = null)
    {
        var step = new EnterValueStep
        {
            Value = "{channel}",
            Min = 1,
            Max = 20,
            DelayMs = 0,
            Wheels = [new() { Control = "PLT_UHF1_PRESETS", Place = 1, Width = 2, Mode = WheelMode.Step, Read = "PLT_UHF_DISP" }],
            Verify = new() { Output = "PLT_UHF_DISP", TimeoutMs = 100 },
        };
        var cockpit = new FakeCockpit();
        cockpit.Strings["PLT_UHF_DISP"] = display ?? $"{channel,2}";
        cockpit.OnSend = (c, id, arg) =>
        {
            if (id != "PLT_UHF1_PRESETS" || display != null) return;
            channel = arg == "INC" ? channel % 20 + 1 : (channel + 18) % 20 + 1;
            c.Strings["PLT_UHF_DISP"] = $"{channel,2}";
        };
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(2) })
        {
            Profile = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "Preset", Phrases = ["push {channel:1-20}"], Steps = [step] }] }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard(string slots) => new() { Phrase = "push {channel:1-20}", Score = 100, Slots = slots, Raw = "push " + slots };

    [Theory]
    [InlineData(3)]
    [InlineData(18)]
    public async Task Steps_the_selector_until_the_display_shows_the_channel(int start)
    {
        var (engine, cockpit) = Create(start);

        var outcome = await engine.HandleAsync(Heard("twelve"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("12", cockpit.Strings["PLT_UHF_DISP"].Trim());
        Assert.Equal(Math.Abs(12 - start), cockpit.Sent.Count);
        Assert.All(cockpit.Sent, s => Assert.Matches("^PLT_UHF1_PRESETS (INC|DEC)$", s));
    }

    [Fact]
    public async Task A_display_without_a_number_is_refused()
    {
        var (engine, cockpit) = Create(3, display: "--");

        var outcome = await engine.HandleAsync(Heard("five"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Contains("cannot read", outcome.Message);
        Assert.Empty(cockpit.Sent);
    }

    [Theory]
    [InlineData("12", true, "12")]
    [InlineData(" 5", true, "5")]
    [InlineData("05", true, "5")]
    [InlineData("251,07 ", true, "251.07")]
    [InlineData("--", false, "0")]
    [InlineData("   ", false, "0")]
    public void Reads_a_display_as_a_number(string text, bool ok, string expected)
    {
        Assert.Equal(ok, EnterValue.TryParseDisplay(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }
}

public class EnterValueGuessTests
{
    private static ControlDefinition Dial(string id, int setMax) => new()
    {
        Identifier = id, Category = "Radio",
        Inputs = [new InputDefinition { Interface = InputInterface.FixedStep }, new InputDefinition { Interface = InputInterface.SetState, MaxValue = setMax }],
    };

    [Fact]
    public void Set_mode_only_when_set_state_takes_the_digits()
    {
        var catalog = new ControlCatalog("Test", [], [Dial("R_10_DIAL", 9), Dial("R_1_DIAL", 17), Dial("R_01_DIAL", 3), Dial("R_025_DIAL", 99)]);

        var modes = EnterValueGuess.Wheels(catalog, "R").ToDictionary(w => w.Control, w => w.Mode);

        Assert.Equal(WheelMode.Set, modes["R_10_DIAL"]);
        Assert.Equal(WheelMode.Step, modes["R_1_DIAL"]);  // 0-17 is a position, not a digit
        Assert.Equal(WheelMode.Step, modes["R_01_DIAL"]);
        Assert.Equal(WheelMode.Set, modes["R_025_DIAL"]); // two digits, 0-99
    }

    [Theory]
    [InlineData("CDU_CLR")]
    [InlineData("CDU_Clr")]
    [InlineData("CDU_Clear")]
    public void Finds_the_clear_key_in_any_case(string clear)
    {
        var keys = "ABCDEFGHIJ".Select(c => $"CDU_{c}").Append(clear)
            .Select(id => new ControlDefinition { Identifier = id, Inputs = [new InputDefinition { Interface = InputInterface.SetState, MaxValue = 1 }] });
        var catalog = new ControlCatalog("Test", [], [.. keys]);

        Assert.Equal(clear, EnterValueGuess.Keys(catalog, "CDU").Clear);
    }
}
