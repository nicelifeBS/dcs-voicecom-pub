using DcsVoiceCom.Definitions;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

/// <summary>Steps laid out on one display, shared by the layout and engine tests.</summary>
internal static class DisplaySteps
{
    /// <summary>The F-14 UHF on "225.025": 100+10 MHz wheel (22-39), 1 MHz, 0.1 MHz, and the 25 kHz wheel.</summary>
    public static EnterValueStep Uhf() => new()
    {
        Value = "{freq}",
        Method = EnterMethod.Wheels,
        Display = "PLT_UHF_DISP",
        Sample = "225.025",
        DelayMs = 0,
        TimeoutMs = 200,
        Fields =
        [
            new() { At = 0, Len = 2, Kind = FieldKind.Wheel, Control = "PLT_UHF1_110_DIAL", Range = "22-39" },
            new() { At = 2, Len = 1, Kind = FieldKind.Wheel, Control = "PLT_UHF1_1_DIAL", Range = "0-9" },
            new() { At = 3, Len = 1, Kind = FieldKind.ReadOnly },
            new() { At = 4, Len = 1, Kind = FieldKind.Wheel, Control = "PLT_UHF1_01_DIAL", Range = "0-9" },
            new() { At = 5, Len = 2, Kind = FieldKind.Wheel, Control = "PLT_UHF1_025_DIAL", Range = "00,25,50,75" },
        ],
    };

    /// <summary>A TACAN on "109X": tens 0-12 and ones stepped with INC/DEC, and an X/Y switch.</summary>
    public static EnterValueStep Tacan() => new()
    {
        Value = "{ch}",
        Method = EnterMethod.Wheels,
        Display = "TACAN_DISP",
        Sample = "109X",
        DelayMs = 0,
        TimeoutMs = 200,
        Fields =
        [
            new() { At = 0, Len = 2, Kind = FieldKind.Wheel, Control = "TACAN_TENS", Range = "0-12", Mode = WheelMode.Step },
            new() { At = 2, Len = 1, Kind = FieldKind.Wheel, Control = "TACAN_ONES", Range = "0-9", Mode = WheelMode.Step },
            new() { At = 3, Len = 1, Kind = FieldKind.Wheel, Control = "TACAN_XY", Range = "X,Y" },
        ],
    };

    /// <summary>An A-10 CDU line "[…22 characters…]": one keypad field between read-only brackets.</summary>
    public static EnterValueStep Cdu(TextAlign align = TextAlign.Left) => new()
    {
        Value = "{text}",
        Method = EnterMethod.Keypad,
        Display = "CDU_LINE9",
        Sample = "[N4126.5E04155.3       ]",
        DelayMs = 0,
        TimeoutMs = 200,
        Keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToDictionary(c => c.ToString(), c => "CDU_" + c),
        Fields =
        [
            new() { At = 0, Len = 1, Kind = FieldKind.ReadOnly },
            new() { At = 1, Len = 22, Kind = FieldKind.Keypad, Align = align },
            new() { At = 23, Len = 1, Kind = FieldKind.ReadOnly },
        ],
    };

    public static string[] Targets(EnterValueStep step, string value)
    {
        var targets = DisplayLayout.Targets(step, value, out var problem);
        Assert.Null(problem);
        return targets.Select(t => t.Text).ToArray();
    }
}

public class DisplayLayoutTests
{
    [Theory]
    [InlineData("0-9", 0, 9)]
    [InlineData(" 22 - 39 ", 22, 39)]
    [InlineData("1-20", 1, 20)]
    public void Parses_a_span(string text, int low, int high)
    {
        var range = DisplayLayout.ParseRange(text)!;
        Assert.Null(range.Items);
        Assert.Equal((low, high), (range.Low, range.High));
    }

    [Fact]
    public void Parses_a_list_in_click_order()
    {
        var range = DisplayLayout.ParseRange("X, Y")!;
        Assert.Equal(["X", "Y"], range.Items!);
        Assert.Equal(1, range.IndexOf("y"));
        Assert.Equal(0, DisplayLayout.ParseRange("00,25,50,75")!.IndexOf("0"));
        Assert.Equal(3, DisplayLayout.ParseRange("00,25,50,75")!.IndexOf("75"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9-0")]
    [InlineData("abc")]
    [InlineData("X,")]
    [InlineData("X,X")]
    public void Rejects_a_range_it_cannot_read(string text) => Assert.Null(DisplayLayout.ParseRange(text));

    [Theory]
    [InlineData("0-9", "09", 9)]
    [InlineData("0-9", " ", 0)]
    [InlineData("22-39", " ", null)]
    [InlineData("22-39", "40", null)]
    [InlineData("X,Y", "Z", null)]
    public void Finds_a_position_in_a_range(string range, string shown, int? expected) =>
        Assert.Equal(expected, DisplayLayout.ParseRange(range)!.IndexOf(shown));

    [Theory]
    [InlineData("251.325", "25", "1", "3", "25")]
    [InlineData("263.175", "26", "3", "1", "75")]
    [InlineData("251", "25", "1", "0", "00")]
    [InlineData("399.975", "39", "9", "9", "75")]
    public void Lines_up_the_decimal_points(string value, string tens, string ones, string tenths, string khz) =>
        Assert.Equal([tens, ones, tenths, khz], DisplaySteps.Targets(DisplaySteps.Uhf(), value));

    [Theory]
    [InlineData("251.0125", "more decimals")]
    [InlineData("400", "outside 22-39")]
    [InlineData("251.010", "outside 00,25,50,75")]
    [InlineData("1251", "too many digits")]
    public void Refuses_a_value_the_display_cannot_show(string value, string message)
    {
        DisplayLayout.Targets(DisplaySteps.Uhf(), value, out var problem);
        Assert.Contains(message, problem);
    }

    [Theory]
    [InlineData("73Y", " 7", "3", "Y")]
    [InlineData("109x", "10", "9", "X")]
    [InlineData("1X", "  ", "1", "X")]
    public void Right_aligns_text_over_the_wheels(string value, string tens, string ones, string xy) =>
        Assert.Equal([tens, ones, xy], DisplaySteps.Targets(DisplaySteps.Tacan(), value));

    [Fact]
    public void Shifts_by_the_implied_decimals_on_a_display_without_a_point()
    {
        var step = new EnterValueStep
        {
            Display = "D",
            Sample = "2513",
            Decimals = 1,
            Fields = [.. Enumerable.Range(0, 4).Select(i => new DisplayField { At = i, Kind = FieldKind.Wheel, Control = $"W{i}", Range = "0-9" })],
        };
        Assert.Equal(["2", "5", "1", "3"], DisplaySteps.Targets(step, "251.3"));
        Assert.Equal(["2", "5", "1", "0"], DisplaySteps.Targets(step, "251"));

        DisplayLayout.Targets(step, "251.35", out var problem);
        Assert.Contains("more decimals", problem);

        step.Decimals = null;
        DisplayLayout.Targets(step, "251.3", out problem);
        Assert.Contains("no decimal point", problem);
    }

    [Theory]
    [InlineData(TextAlign.Left, "AB1                   ")]
    [InlineData(TextAlign.Right, "                   AB1")]
    public void Places_keypad_text_left_or_right(TextAlign align, string expected) =>
        Assert.Equal([expected], DisplaySteps.Targets(DisplaySteps.Cdu(align), "ab1"));

    [Fact]
    public void Refuses_keypad_text_longer_than_the_field()
    {
        DisplayLayout.Targets(DisplaySteps.Cdu(), new string('A', 23), out var problem);
        Assert.Contains("too long", problem);
    }

    [Fact]
    public void Wheels_compare_by_position_and_keypad_text_ignoring_padding()
    {
        var uhf = DisplaySteps.Uhf();
        var targets = DisplayLayout.Targets(uhf, "251.0", out _);
        Assert.True(DisplayLayout.ShowsAll("251.000", targets));
        Assert.False(DisplayLayout.ShowsAll("251.025", targets));

        var cdu = DisplaySteps.Cdu();
        var typed = DisplayLayout.Targets(cdu, "n41", out _);
        Assert.True(DisplayLayout.ShowsAll("[N41                   ]", typed));
        Assert.False(DisplayLayout.ShowsAll("[N412                  ]", typed));
    }

    [Fact]
    public void Place_comes_from_where_the_field_sits()
    {
        var uhf = DisplaySteps.Uhf();
        var wheels = uhf.Fields!.Where(f => f.Kind == FieldKind.Wheel).ToList();
        Assert.Equal([10m, 1m, 0.1m, 0.001m], wheels.Select(w => DisplayLayout.PlaceOf(uhf, w)!.Value));

        var tacan = DisplaySteps.Tacan();
        Assert.Equal(10m, DisplayLayout.PlaceOf(tacan, tacan.Fields![0]));
        Assert.Null(DisplayLayout.PlaceOf(tacan, tacan.Fields![2]));
    }

    [Fact]
    public void Uses_the_nearest_point_while_only_some_wheels_are_set_up()
    {
        var top = new EnterValueStep
        {
            Display = "D", Sample = "251.000",
            Fields = [new() { At = 0, Len = 2, Kind = FieldKind.Wheel, Control = "T", Range = "22-39" }],
        };
        Assert.Equal(10m, DisplayLayout.PlaceOf(top, top.Fields[0]));
        Assert.Equal(["26"], DisplaySteps.Targets(top, "263.175"));

        var khz = new EnterValueStep
        {
            Display = "D", Sample = "251.000",
            Fields = [new() { At = 5, Len = 2, Kind = FieldKind.Wheel, Control = "Q", Range = "00,25,50,75" }],
        };
        Assert.Equal(0.001m, DisplayLayout.PlaceOf(khz, khz.Fields[0]));
        Assert.Equal(["75"], DisplaySteps.Targets(khz, "263.175"));
    }

    [Fact]
    public void A_valid_layout_has_no_problems()
    {
        Assert.Empty(DisplayLayout.Validate(DisplaySteps.Uhf()));
        Assert.Empty(DisplayLayout.Validate(DisplaySteps.Tacan()));
        Assert.Empty(DisplayLayout.Validate(DisplaySteps.Cdu()));
    }

    [Fact]
    public void Validate_reports_layout_mistakes()
    {
        var overlap = DisplaySteps.Uhf();
        overlap.Fields![1].At = 1;
        Assert.Contains(DisplayLayout.Validate(overlap), p => p.Contains("overlaps"));

        var past = DisplaySteps.Uhf();
        past.Fields![4].Len = 3;
        Assert.Contains(DisplayLayout.Validate(past), p => p.Contains("past the end"));

        var point = DisplaySteps.Uhf();
        point.Fields!.RemoveAt(2);
        point.Fields[1].Len = 2;
        Assert.Contains(DisplayLayout.Validate(point), p => p.Contains("decimal point"));

        var range = DisplaySteps.Tacan();
        range.Fields![2].Range = "X,";
        Assert.Contains(DisplayLayout.Validate(range), p => p.Contains("range"));

        var noDisplay = DisplaySteps.Uhf();
        noDisplay.Display = null;
        Assert.Contains(DisplayLayout.Validate(noDisplay), p => p.Contains("needs a display"));
    }

    [Fact]
    public void Validate_keeps_wheels_and_keypad_apart()
    {
        var twoFields = DisplaySteps.Cdu();
        twoFields.Fields![1].Len = 10;
        twoFields.Fields.Add(new DisplayField { At = 12, Len = 10, Kind = FieldKind.Keypad });
        Assert.Contains(DisplayLayout.Validate(twoFields), p => p.Contains("only one keypad field"));

        var mixed = DisplaySteps.Uhf();
        mixed.Fields![2].Kind = FieldKind.Keypad;
        Assert.Contains(DisplayLayout.Validate(mixed), p => p.Contains("can't have a keypad field"));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var profile = new Profile { Commands = [new Command { Name = "UHF", Phrases = ["uhf {freq}"], Steps = [DisplaySteps.Uhf()] }] };
        var json = ProfileSerializer.Serialize(profile);
        Assert.Contains("\"read_only\"", json);
        Assert.Contains("\"display\": \"PLT_UHF_DISP\"", json);
        Assert.DoesNotContain("\"verify\"", json);

        var step = Assert.IsType<EnterValueStep>(ProfileSerializer.Deserialize(json).Commands[0].Steps[0]);
        Assert.Equal("225.025", step.Sample);
        Assert.Equal(5, step.Fields!.Count);
        Assert.Equal(FieldKind.ReadOnly, step.Fields[2].Kind);
        Assert.Equal("00,25,50,75", step.Fields[4].Range);
    }

    [Fact]
    public void Compile_checks_the_layout_and_the_catalog_sees_display_and_controls()
    {
        var step = DisplaySteps.Cdu();
        step.Fields![1].Len = 10;
        step.Fields.Add(new DisplayField { At = 12, Len = 10, Kind = FieldKind.Keypad });
        var profile = new Profile { Commands = [new Command { Name = "CDU", Phrases = ["type {text:text}"], Steps = [step] }] };
        Assert.Contains(CompiledProfile.Compile(profile).Problems, p => p.Contains("only one keypad field"));

        var uhf = new Profile { Commands = [new Command { Name = "UHF", Phrases = ["uhf {freq}"], Steps = [DisplaySteps.Uhf()] }] };
        Assert.Empty(CompiledProfile.Compile(uhf).Problems);
        var missing = CompiledProfile.FindMissingControls(uhf, new ControlCatalog("Test", [], []));
        Assert.Contains("PLT_UHF_DISP", missing);
        Assert.Contains("PLT_UHF1_025_DIAL", missing);
    }
}

public class DisplayFieldEngineTests
{
    internal static int Wrap(int v, int min, int max) => v > max ? min : v < min ? max : v;

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(Step step, string phrase)
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
    public async Task Turns_each_wheel_from_its_part_of_the_display()
    {
        var (engine, cockpit) = Create(DisplaySteps.Uhf(), "uhf {freq}");
        new FakeUhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two six three decimal one seven five"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("263.175", cockpit.Strings["PLT_UHF_DISP"]);
        Assert.Contains(outcome.Trace, t => t == "PLT_UHF_DISP = \"263.175\"");
    }

    [Fact]
    public async Task Swaps_up_and_down_when_a_wheel_turns_the_other_way()
    {
        var (engine, cockpit) = Create(DisplaySteps.Uhf(), "uhf {freq}");
        var uhf = new FakeUhf { InvertTenth = true };
        uhf.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two five one decimal three"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(3, uhf.D3);
        Assert.Contains(outcome.Trace, t => t.Contains("swapping up and down"));
    }

    [Fact]
    public async Task Skips_everything_when_the_display_already_shows_the_value()
    {
        var (engine, cockpit) = Create(DisplaySteps.Uhf(), "uhf {freq}");
        new FakeUhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two five one decimal zero"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task A_value_outside_a_wheel_range_sends_nothing()
    {
        var (engine, cockpit) = Create(DisplaySteps.Uhf(), "uhf {freq}");
        new FakeUhf().Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "four zero zero decimal zero"));

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Contains("outside 22-39", outcome.Message);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task A_stuck_wheel_is_refused()
    {
        var (engine, cockpit) = Create(DisplaySteps.Uhf(), "uhf {freq}");
        new FakeUhf { Stuck = true }.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("uhf {freq}", "two six one decimal zero"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Contains("did not move", outcome.Message);
    }

    [Fact]
    public async Task Steps_a_tacan_and_clicks_its_x_y_switch()
    {
        var (engine, cockpit) = Create(DisplaySteps.Tacan(), "tacan {ch:text}");
        int tens = 10, ones = 9, xy = 0;
        void Publish(FakeCockpit c) => c.Strings["TACAN_DISP"] = $"{tens * 10 + ones,3}{(xy == 0 ? "X" : "Y")}";
        Publish(cockpit);
        cockpit.OnSend = (c, id, arg) =>
        {
            switch (id, arg)
            {
                case ("TACAN_TENS", "INC"): tens = Math.Min(12, tens + 1); break;
                case ("TACAN_TENS", "DEC"): tens = Math.Max(0, tens - 1); break;
                case ("TACAN_ONES", "INC"): ones = Wrap(ones + 1, 0, 9); break;
                case ("TACAN_ONES", "DEC"): ones = Wrap(ones - 1, 0, 9); break;
                case ("TACAN_XY", "2"): xy = 1; break;
                case ("TACAN_XY", "0"): xy = 0; break;
            }
            Publish(c);
        };

        var outcome = await engine.HandleAsync(Heard("tacan {ch:text}", "seven three yankee"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(" 73Y", cockpit.Strings["TACAN_DISP"]);
        Assert.Contains("TACAN_TENS DEC", cockpit.Sent);
    }

    [Fact]
    public async Task Types_on_the_keypad_and_checks_its_field()
    {
        var (engine, cockpit) = Create(DisplaySteps.Cdu(), "type {text:text}");
        var scratch = "";
        void Publish(FakeCockpit c) => c.Strings["CDU_LINE9"] = "[" + scratch.PadRight(22) + "]";
        Publish(cockpit);
        cockpit.OnSend = (c, id, arg) =>
        {
            if (arg == "1" && id.StartsWith("CDU_")) scratch += id[4..];
            Publish(c);
        };

        var outcome = await engine.HandleAsync(Heard("type {text:text}", "november four one two"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("N412", scratch);
        Assert.Contains(outcome.Trace, t => t.StartsWith("CDU_LINE9 = "));
    }

    [Fact]
    public async Task Keypad_text_that_does_not_fit_sends_nothing()
    {
        var step = DisplaySteps.Cdu();
        step.Fields![1].Len = 3;
        var (engine, cockpit) = Create(step, "type {text:text}");
        cockpit.Strings["CDU_LINE9"] = "[   ]";

        var outcome = await engine.HandleAsync(Heard("type {text:text}", "november four one two"));

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Contains("too long", outcome.Message);
        Assert.Empty(cockpit.Sent);
    }
}
