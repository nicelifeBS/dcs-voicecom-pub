using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class DisplayProbeTests
{
    // ---- pure helpers ----

    [Fact]
    public void NoisyCells_are_the_ones_that_change_on_their_own()
    {
        var noisy = DisplayProbe.NoisyCells(["1 [AB]", "2 [AB]", "3 [AB]", "1 [AB]", "4 [AB]"]);
        Assert.Equal([0], noisy);
        Assert.Empty(DisplayProbe.NoisyCells(["same", "same"]));
    }

    [Fact]
    public void DiffSpan_ignores_noisy_cells()
    {
        Assert.Equal((5, 2), DisplayProbe.DiffSpan("251.000", "251.025", null));
        Assert.Null(DisplayProbe.DiffSpan("1:23", "2:23", new HashSet<int> { 0 }));
        Assert.Equal((3, 1), DisplayProbe.DiffSpan("1:23", "2:24", new HashSet<int> { 0 }));
    }

    [Theory]
    [InlineData("22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39", "22-39", false)]
    [InlineData("0,1,2,3,4,5,6,7,8,9", "0-9", false)]
    [InlineData("00,25,50,75", "00,25,50,75", false)]
    [InlineData("X,Y", "X,Y", false)]
    [InlineData("A,B,C", "A,B,C", false)]
    [InlineData("9,8,7,6,5,4,3,2,1,0", "0-9", true)]
    [InlineData("3,2,1", "1-3", true)]
    public void RangeFromValues_writes_runs_and_lists(string values, string range, bool flip)
    {
        var result = DisplayProbe.RangeFromValues(values.Split(','));
        Assert.Equal((range, flip), result);
        Assert.NotNull(DisplayLayout.ParseRange(result!.Value.Range));
    }

    [Fact]
    public void RangeFromValues_rejects_repeats_and_single_values()
    {
        Assert.Null(DisplayProbe.RangeFromValues(["1"]));
        Assert.Null(DisplayProbe.RangeFromValues(["1", "2", "1"]));
    }

    [Fact]
    public void KeyFromDiff_tells_a_key_from_nothing_and_a_page()
    {
        var key = DisplayProbe.KeyFromDiff("[    ]", "[a   ]");
        Assert.Equal(new KeyDiff(1, 1, 'A'), key);
        Assert.Equal(0, DisplayProbe.KeyFromDiff("[    ]", "[    ]").Changed);
        Assert.Equal(4, DisplayProbe.KeyFromDiff("[    ]", "[MENU]").Changed);
        Assert.Equal(0, DisplayProbe.KeyFromDiff("1[    ]", "2[    ]", new HashSet<int> { 0 }).Changed);
    }

    // ---- candidates ----

    private static ControlDefinition Input(string id, string category, string description, params InputDefinition[] inputs) =>
        new() { Identifier = id, Category = category, Description = description, Inputs = [.. inputs] };

    private static InputDefinition Step() => new() { Interface = InputInterface.FixedStep };
    private static InputDefinition Set(int max) => new() { Interface = InputInterface.SetState, MaxValue = max };
    private static InputDefinition Act() => new() { Interface = InputInterface.Action };

    private static ControlDefinition Display(string id, string category, int length) => new()
    {
        Identifier = id, Category = category,
        Outputs = [new OutputDefinition { Type = "string", Address = 0x2000, MaxLength = length }],
    };

    [Fact]
    public void Candidates_rank_names_and_drop_the_never_touch_list()
    {
        var catalog = new ControlCatalog("T", [],
        [
            Display("UFC_DISP", "UFC", 8),
            Input("UFC_BTN_1", "UFC", "UFC key 1", Set(1)),
            Input("UFC_BTN_ENT", "UFC", "Enter", Set(1)),
            Input("UFC_BTN_CLR", "UFC", "Clear", Set(1)),
            Input("UFC_POWER", "UFC", "UFC power", Set(1)),
            Input("UFC_MASTER_ARM", "UFC", "Master arm", Set(1)),
            Input("UFC_SELF_TEST", "UFC", "Built in test", Set(1)),
            Input("UFC_BRT", "UFC", "Brightness", Step(), Set(65535)),
            Input("UFC_MODE_1_DIAL", "UFC", "Mode", Step()),
            Input("OTHER_KEY", "Other", "Not related", Set(1)),
            new ControlDefinition { Identifier = "UFC_LAMP", Category = "UFC", Outputs = [new OutputDefinition { Type = "integer", Address = 1 }] },
        ]);

        var result = DisplayProbe.Candidates(catalog, "UFC_DISP");
        var ids = result.Candidates.Select(c => c.Control).ToList();

        Assert.DoesNotContain("OTHER_KEY", ids);
        Assert.DoesNotContain("UFC_LAMP", ids);
        Assert.Contains("UFC_BRT", ids);
        Assert.Equal(["UFC_BTN_1", "UFC_BTN_CLR", "UFC_MODE_1_DIAL"], ids.Take(3).ToList()); // likely names first
        Assert.Equal(["UFC_BTN_ENT", "UFC_MASTER_ARM", "UFC_POWER", "UFC_SELF_TEST"], result.Dropped.Select(d => d.Control).Order().ToList());
        Assert.All(result.Dropped, d => Assert.Equal("never-touch", d.Reason));
        Assert.False(result.Candidates.First(c => c.Control == "UFC_MODE_1_DIAL").IsKey);
        Assert.True(result.Candidates.First(c => c.Control == "UFC_BTN_1").IsKey);
    }

    [Fact]
    public void Candidates_include_other_categories_when_asked()
    {
        var catalog = new ControlCatalog("T", [],
        [
            Display("CDU_LINE1", "CDU", 24),
            Input("KEYS_A", "Keys", "A", Set(1)),
        ]);
        Assert.Empty(DisplayProbe.Candidates(catalog, "CDU_LINE1").Candidates);
        Assert.Single(DisplayProbe.Candidates(catalog, "CDU_LINE1", ["Keys"]).Candidates);
    }

    // ---- fake cockpits ----

    /// <summary>A cockpit: a display text built from state, and a reaction to each line sent.</summary>
    private abstract class Cockpit
    {
        public List<string> Sent { get; } = [];
        public abstract string Text { get; }
        public int Reads;

        public string? Read(string id)
        {
            Reads++;
            return Text;
        }

        public Task Send(string control, string arg)
        {
            Sent.Add($"{control} {arg}");
            React(control, arg);
            return Task.CompletedTask;
        }

        protected abstract void React(string control, string arg);
    }

    private static ProbeContext Context(ControlCatalog catalog, Cockpit cockpit) =>
        new(catalog, cockpit.Read, cockpit.Send) { Delay = TimeSpan.Zero };

    private static Task<ProbeResult> Learn(ProbeContext ctx, string display, ControlCatalog catalog, CancellationToken cancel = default) =>
        DisplayProbe.LearnAsync(ctx, display, DisplayProbe.Candidates(catalog, display).Candidates, null, cancel);

    /// <summary>The F-14 UHF: 22-39 pair, 0-9, 0-9, 00/25/50/75; all spring switches (set 0-2, click 2,1 / 0,1).</summary>
    private sealed class Uhf : Cockpit
    {
        public int Pair = 25, One = 1, Tenth, Quarter;
        public override string Text => $"{Pair}{One}.{Tenth}{Quarter * 25:00}";

        protected override void React(string control, string arg)
        {
            if (arg is not ("2" or "0")) return;
            var dir = arg == "2" ? 1 : -1;
            switch (control)
            {
                case "PLT_UHF1_110_DIAL": Pair = 22 + (Pair - 22 + dir + 18) % 18; break;
                case "PLT_UHF1_1_DIAL": One = (One + dir + 10) % 10; break;
                case "PLT_UHF1_01_DIAL": Tenth = (Tenth + dir + 10) % 10; break;
                case "PLT_UHF1_025_DIAL": Quarter = (Quarter + dir + 4) % 4; break;
            }
        }
    }

    private static ControlDefinition Dial(string id) => Input(id, "UHF", id, Step(), Set(2));

    private static ControlCatalog UhfCatalog() => new("F-14B", [],
    [
        Display("PLT_UHF_DISP", "UHF", 7),
        Dial("PLT_UHF1_110_DIAL"), Dial("PLT_UHF1_1_DIAL"), Dial("PLT_UHF1_01_DIAL"), Dial("PLT_UHF1_025_DIAL"),
        Input("PLT_UHF1_POWER", "UHF", "Power", Set(2)),
    ]);

    [Fact]
    public async Task Learns_the_F14_UHF_and_puts_the_wheels_back()
    {
        var catalog = UhfCatalog();
        var cockpit = new Uhf { Pair = 28, One = 4, Tenth = 3, Quarter = 2 };
        var before = cockpit.Text;

        var result = await Learn(Context(catalog, cockpit), "PLT_UHF_DISP", catalog);

        Assert.Equal(
            [(0, 2, "22-39"), (2, 1, "0-9"), (4, 1, "0-9"), (5, 2, "00,25,50,75")],
            result.Wheels.Select(w => (w.At, w.Len, w.Range!)).ToList());
        Assert.All(result.Wheels, w => { Assert.Equal(FieldKind.Wheel, w.Kind); Assert.Equal(WheelMode.Click, w.Mode); Assert.False(w.Flip); });
        Assert.Equal("PLT_UHF1_110_DIAL", result.Wheels[0].Control);
        Assert.Equal(before, cockpit.Text);
        Assert.DoesNotContain(cockpit.Sent, s => s.StartsWith("PLT_UHF1_POWER"));
    }

    /// <summary>A TACAN-like display with odd names, no decimal point, a letter wheel and a wheel that stops at its ends.</summary>
    private sealed class Tacan : Cockpit
    {
        public int Tens = 5, Ones = 3, XY; // Tens 0-12 does not wrap; Ones 0-9 wraps, XY X/Y wraps
        public bool OnesReversed;
        public override string Text => $"{Tens:00}{Ones}{(XY == 0 ? 'X' : 'Y')}";

        protected override void React(string control, string arg)
        {
            var dir = arg == "INC" ? 1 : arg == "DEC" ? -1 : 0;
            if (dir == 0) return;
            switch (control)
            {
                case "TCN_TENS": Tens = Math.Clamp(Tens + dir, 0, 12); break;
                case "TCN_ONES": Ones = (Ones + (OnesReversed ? -dir : dir) + 10) % 10; break;
                case "TCN_BAND": XY = (XY + dir + 2) % 2; break;
            }
        }
    }

    [Fact]
    public async Task Learns_odd_names_letters_flipped_and_non_wrapping_wheels()
    {
        var catalog = new ControlCatalog("T", [],
        [
            Display("TCN_DISP", "TACAN", 4),
            Input("TCN_TENS", "TACAN", "Channel tens", Step()),
            Input("TCN_ONES", "TACAN", "Channel ones", Step()),
            Input("TCN_BAND", "TACAN", "X/Y", Step()),
        ]);
        var cockpit = new Tacan { OnesReversed = true };

        var result = await Learn(Context(catalog, cockpit), "TCN_DISP", catalog);

        var byControl = result.Wheels.ToDictionary(w => w.Control!);
        Assert.Equal((0, 2, "0-12", WheelMode.Step, false), (byControl["TCN_TENS"].At, byControl["TCN_TENS"].Len, byControl["TCN_TENS"].Range, byControl["TCN_TENS"].Mode, byControl["TCN_TENS"].Flip));
        Assert.Equal((2, 1, "0-9", true), (byControl["TCN_ONES"].At, byControl["TCN_ONES"].Len, byControl["TCN_ONES"].Range, byControl["TCN_ONES"].Flip));
        Assert.Equal((3, 1, "X,Y"), (byControl["TCN_BAND"].At, byControl["TCN_BAND"].Len, byControl["TCN_BAND"].Range));
        Assert.Equal("05" + "3X", cockpit.Text); // all back where they started
    }

    /// <summary>A scratchpad: clock cell (noise), page digit, "[" 8 entry characters "]".</summary>
    private sealed class Scratchpad(bool rightAlign = false) : Cockpit
    {
        public string Entry = "";
        public int Page = 1;
        public string Menu = "";
        public List<string> Pressed { get; } = [];
        private int _clock;

        public override string Text
        {
            get
            {
                var shown = Menu.Length > 0 ? Menu : Entry;
                var padded = rightAlign ? shown.PadLeft(8) : shown.PadRight(8);
                return $"{_clock++ % 10}{Page}[{padded}]";
            }
        }

        protected override void React(string control, string arg)
        {
            if (arg != "1") return;
            Pressed.Add(control);
            Menu = control == "UFC_PAGE" ? "MENU" : Menu;
            if (control == "UFC_PAGE") { Page++; return; }
            Menu = "";
            switch (control)
            {
                case "UFC_BTN_ENT": throw new InvalidOperationException("ENT must never be pressed");
                case "UFC_BTN_CLR": Entry = ""; break;
                case "UFC_NOP": break;
                default:
                    if (Entry.Length < 8) Entry += control[^1];
                    break;
            }
        }
    }

    private static ControlCatalog PadCatalog() => new("T", [],
    [
        Display("UFC_DISP", "UFC", 12),
        Input("UFC_BTN_7", "UFC", "7", Set(1)), Input("UFC_BTN_B", "UFC", "B", Set(1)), Input("UFC_BTN_3", "UFC", "3", Set(1)),
        Input("UFC_BTN_CLR", "UFC", "Clear", Set(1)),
        Input("UFC_PAGE", "UFC", "Page", Act()), Input("UFC_NOP", "UFC", "Does nothing", Act()),
        Input("UFC_ENT", "UFC", "Enter", Set(1)),
    ]);

    [Fact]
    public async Task Learns_a_keypad_with_noise_a_page_key_and_never_presses_ENT()
    {
        var catalog = PadCatalog();
        var cockpit = new Scratchpad();

        var result = await Learn(Context(catalog, cockpit), "UFC_DISP", catalog);

        Assert.Equal(new Dictionary<string, string> { ["7"] = "UFC_BTN_7", ["B"] = "UFC_BTN_B", ["3"] = "UFC_BTN_3" }, result.Keys);
        Assert.Equal("UFC_BTN_CLR", result.Clear);
        Assert.Equal((3, 8, FieldKind.Keypad, TextAlign.Left), (result.KeypadField!.At, result.KeypadField.Len, result.KeypadField.Kind, result.KeypadField.Align!.Value));
        Assert.Contains(result.Dropped, d => d.Control == "UFC_PAGE" && d.Reason == "changes the page");
        Assert.Contains(result.Dropped, d => d.Control == "UFC_NOP" && d.Reason == "no change");
        Assert.DoesNotContain("UFC_ENT", cockpit.Pressed);
        Assert.Equal("", cockpit.Entry);
        Assert.Empty(result.Wheels);
    }

    [Fact]
    public async Task Learns_a_right_aligned_entry()
    {
        var catalog = PadCatalog();
        var cockpit = new Scratchpad(rightAlign: true);

        var result = await Learn(Context(catalog, cockpit), "UFC_DISP", catalog);

        Assert.Equal((3, 8, TextAlign.Right), (result.KeypadField!.At, result.KeypadField.Len, result.KeypadField.Align!.Value));
        Assert.Equal("", cockpit.Entry);
    }

    [Fact]
    public async Task Cancel_still_puts_the_wheel_back()
    {
        var catalog = UhfCatalog();
        var cockpit = new Uhf { Pair = 30, One = 7, Tenth = 5, Quarter = 1 };
        var before = cockpit.Text;
        using var cts = new CancellationTokenSource();
        var ctx = Context(catalog, cockpit) with
        {
            Send = (control, arg) =>
            {
                var task = cockpit.Send(control, arg);
                if (cockpit.Sent.Count == 40) cts.Cancel(); // part way through the pair wheel
                return task;
            },
        };

        var result = await Learn(ctx, "PLT_UHF_DISP", catalog, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(before, cockpit.Text);
    }

    [Fact]
    public async Task Keypad_without_a_clear_key_warns()
    {
        var catalog = new ControlCatalog("T", [],
        [
            Display("UFC_DISP", "UFC", 12),
            Input("UFC_BTN_7", "UFC", "7", Set(1)), Input("UFC_BTN_B", "UFC", "B", Set(1)),
        ]);
        var cockpit = new Scratchpad();

        var result = await Learn(Context(catalog, cockpit), "UFC_DISP", catalog);

        Assert.Null(result.Clear);
        Assert.Contains(result.Notes, n => n.Contains("No clear key"));
    }

    [Fact]
    public void Fixed_characters_between_wheels_become_read_only_fields()
    {
        var fields = DisplayProbe.WithFixedGaps(
        [
            new DisplayField { At = 0, Len = 3, Kind = FieldKind.Wheel, Control = "A" },
            new DisplayField { At = 4, Len = 3, Kind = FieldKind.Wheel, Control = "B" },
        ], "251.000");
        Assert.Equal((3, 1, FieldKind.ReadOnly), (fields[1].At, fields[1].Len, fields[1].Kind));
        Assert.Equal(3, fields.Count);
    }
}
