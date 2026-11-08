using System.Text;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels.Tests;

public class EnterValueEditorTests
{
    private const int DispAddress = 0x150c, CduAddress = 0x1600;

    private static ControlDefinition Dial(string id, string description) => new()
    {
        Identifier = id, Category = "UHF 1", Description = description,
        Inputs = [new InputDefinition { Interface = InputInterface.FixedStep }, new InputDefinition { Interface = InputInterface.SetState, MaxValue = 2 }],
        Outputs = [new OutputDefinition { Type = "integer", Address = 0x1240, Mask = 0x3, MaxValue = 2 }],
    };

    private static ControlDefinition Display(string id, int address, int length) => new()
    {
        Identifier = id, Category = "Displays",
        Outputs = [new OutputDefinition { Type = "string", Address = address, MaxLength = length }],
    };

    private static ControlDefinition Key(string id) => new()
    {
        Identifier = id, Category = "CDU",
        Inputs = [new InputDefinition { Interface = InputInterface.SetState, MaxValue = 1 }],
    };

    /// <summary>The F-14 pilot UHF (wheels and its 7-character display) and an A-10 style CDU line of 24 characters.</summary>
    private static ControlCatalog Catalog()
    {
        var controls = new List<ControlDefinition>
        {
            Dial("PLT_UHF1_110_DIAL", "PILOT UHF ARC-159 100MHz & 10MHz Dial"),
            Dial("PLT_UHF1_1_DIAL", "PILOT UHF ARC-159 1MHz Dial"),
            Dial("PLT_UHF1_01_DIAL", "PILOT UHF ARC-159 0.1MHz Dial"),
            Dial("PLT_UHF1_025_DIAL", "PILOT UHF ARC-159 0.025MHz Dial"),
            Display("PLT_UHF_DISP", DispAddress, 7),
            Display("CDU_LINE9", CduAddress, 24),
            Key("CDU_CLR"), Key("CDU_POINT"),
        };
        controls.AddRange("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => Key($"CDU_{c}")));
        return new ControlCatalog("F-14B", [], controls);
    }

    /// <summary>The radio and CDU as the export buffer shows them: the UHF display only, no wheel read-backs.</summary>
    private sealed class Cockpit
    {
        public StateBuffer Buffer { get; } = new();
        public int High = 251, Tenth, Quarter;
        public bool InvertTenth;
        public string Scratch = "N4126.5";

        public Cockpit() => Publish();

        public Task Send(string control, string arg)
        {
            if (arg is "2" or "0")
            {
                var dir = arg == "2" ? 1 : -1;
                switch (control)
                {
                    case "PLT_UHF1_110_DIAL": High += dir * 10; break;
                    case "PLT_UHF1_1_DIAL": High += dir; break;
                    case "PLT_UHF1_01_DIAL": Tenth = (Tenth + (InvertTenth ? -dir : dir) + 10) % 10; break;
                    case "PLT_UHF1_025_DIAL": Quarter = (Quarter + dir + 4) % 4; break;
                }
            }
            Publish();
            return Task.CompletedTask;
        }

        public void Publish()
        {
            Buffer.Write(DispAddress, Encoding.ASCII.GetBytes($"{High}.{Tenth}{Quarter * 25:00}"));
            Buffer.Write(CduAddress, Encoding.ASCII.GetBytes("[" + Scratch.PadRight(22) + "]"));
        }
    }

    private static EnterValueEditorViewModel Create(EnterValueStep step, Cockpit? cockpit = null, FakeDialogs? dialogs = null, string phrase = "uhf {freq}") =>
        new(step, [phrase], new EnterValueContext(Catalog(), () => cockpit?.Buffer, cockpit is null ? null : cockpit.Send), dialogs ?? new FakeDialogs())
        {
            TestDelay = TimeSpan.Zero,
        };

    private static EnterValueStep NewUhf() => new() { Value = "{freq}", Display = "PLT_UHF_DISP" };

    private static void Pick(EnterValueEditorViewModel vm, int from, int to)
    {
        vm.PickCell(from, shift: false);
        if (to != from) vm.PickCell(to, shift: true);
    }

    [Fact]
    public void Shows_the_live_display_cell_by_cell()
    {
        var vm = Create(NewUhf(), new Cockpit());

        Assert.True(vm.IsLive);
        Assert.Equal(["2", "5", "1", ".", "0", "0", "0"], vm.Cells.Select(c => c.Text));
        Assert.StartsWith("●", vm.LiveStatus);
    }

    [Fact]
    public void Without_dcs_shows_empty_cells_and_refuses_new_fields()
    {
        var vm = Create(NewUhf());

        Assert.False(vm.IsLive);
        Assert.Equal(7, vm.Cells.Count);
        Assert.All(vm.Cells, c => Assert.Equal("", c.Text));
        Assert.Contains("No live connection", vm.LiveStatus);

        vm.PickCell(1, shift: false);
        vm.AddWheelCommand.Execute(null);
        Assert.Empty(vm.Fields);
        Assert.Equal(EnterValueEditorViewModel.NeedsDcs, vm.Message);
    }

    [Fact]
    public void Place_and_width_come_from_the_selection()
    {
        var vm = Create(NewUhf(), new Cockpit());

        Pick(vm, 0, 1);
        Assert.True(vm.CanAddField);
        vm.AddWheelCommand.Execute(null);

        var field = Assert.Single(vm.Fields);
        Assert.Equal(("W1", 0, 2, "10", "0-99"), (field.Name, field.At, field.Width, field.Place, field.Range));
        Assert.Same(field, vm.SelectedField);

        Pick(vm, 5, 6);
        vm.AddWheelCommand.Execute(null);
        Assert.Equal("0.001", vm.Fields[1].Place);
    }

    [Fact]
    public void Shift_click_widens_the_selected_field()
    {
        var vm = Create(NewUhf(), new Cockpit());
        Pick(vm, 0, 0);
        vm.AddWheelCommand.Execute(null);

        vm.PickCell(1, shift: true);

        var field = Assert.Single(vm.Fields);
        Assert.Equal((0, 2), (field.At, field.Len));
        Assert.Equal("10", field.Place);
    }

    [Fact]
    public void A_wheel_cannot_span_the_point_or_overlap()
    {
        var vm = Create(NewUhf(), new Cockpit());

        Pick(vm, 2, 4);
        Assert.Contains("decimal point", vm.Message);

        Pick(vm, 0, 1);
        vm.AddWheelCommand.Execute(null);
        vm.PickCell(2, shift: false);
        vm.PickCell(1, shift: true);
        Assert.Contains("overlaps W1", vm.Message);
    }

    [Fact]
    public void Fill_from_radio_places_the_wheels_on_the_live_display()
    {
        var vm = Create(NewUhf(), new Cockpit());

        vm.SuggestCommand.Execute(null);

        Assert.Equal(["W1", "W2", "R/O", "W3", "W4"], vm.Fields.Select(f => f.Name));
        Assert.Equal(["10", "1", "—", "0.1", "0.001"], vm.Fields.Select(f => f.Place));
        Assert.Equal("PLT_UHF1_110_DIAL", vm.Fields[0].Control);
        Assert.Contains("Suggested 4 fields", vm.Message);
        Assert.Empty(vm.Problems);
    }

    [Fact]
    public void Fill_from_radio_without_dcs_only_finds_the_controls()
    {
        var vm = Create(new EnterValueStep { Value = "{freq}" });

        vm.SuggestCommand.Execute(null);

        Assert.Empty(vm.Fields);
        Assert.Equal("PLT_UHF_DISP", vm.Display);
        Assert.Contains("needs DCS", vm.Message);
    }

    [Fact]
    public void The_try_value_shows_targets_and_clicks_for_the_selected_wheel()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);

        vm.Try = "263.175";
        vm.PickCell(2, shift: false);

        Assert.Equal(["2", "6", "3", "", "1", "7", "5"], vm.Cells.Select(c => c.Target));
        Assert.Equal([false, true, true, false, true, true, true], vm.Cells.Select(c => c.Changes));
        Assert.Equal("‘1’ → ‘3’ · 2 clicks up", vm.SelectedPreview);
    }

    [Fact]
    public void A_try_value_the_wheels_cannot_show_is_a_problem()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);
        vm.Fields[0].Range = "22-39";

        vm.Try = "400";

        Assert.Contains(vm.Problems, p => p.Contains("outside 22-39"));
    }

    [Fact]
    public async Task A_test_click_that_turns_the_wrong_way_flips_the_wheel()
    {
        var cockpit = new Cockpit { InvertTenth = true };
        var vm = Create(NewUhf(), cockpit);
        vm.SuggestCommand.Execute(null);
        var tenth = vm.Fields.Single(f => f.Control == "PLT_UHF1_01_DIAL");

        await vm.TestClickAsync(tenth, up: true);

        Assert.True(tenth.Flip);
        Assert.Equal("⇅ flipped", tenth.Status);
        Assert.True(vm.ToStep().Fields!.Single(f => f.Control == "PLT_UHF1_01_DIAL").Flip);
    }

    [Fact]
    public void Saves_the_display_layout()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);
        vm.Fields[0].Range = "22-39";
        vm.Fields[4].Range = "00,25,50,75";

        var step = vm.ToStep();

        Assert.Equal(EnterMethod.Wheels, step.Method);
        Assert.Equal("PLT_UHF_DISP", step.Display);
        Assert.Equal("251.000", step.Sample);
        Assert.Equal([0, 2, 3, 4, 5], step.Fields!.Select(f => f.At));
        Assert.Equal(FieldKind.ReadOnly, step.Fields![2].Kind);
        Assert.Null(step.Fields![2].Control);
        Assert.Equal("00,25,50,75", step.Fields![4].Range);
        Assert.Null(step.Wheels);
        Assert.Null(step.Verify);
    }

    [Fact]
    public void A_keypad_step_has_one_field()
    {
        var vm = Create(new EnterValueStep { Value = "{text}", Method = EnterMethod.Keypad, Display = "CDU_LINE9" }, new Cockpit(), phrase: "type {text:text}");
        vm.FillKeysCommand.Execute(null);

        Pick(vm, 0, 0);
        vm.AddReadOnlyCommand.Execute(null);
        Pick(vm, 1, 22);
        vm.AddKeypadCommand.Execute(null);
        Pick(vm, 23, 23);
        vm.AddKeypadCommand.Execute(null);

        Assert.Contains("one field", vm.Message);
        Assert.Equal(["R/O", "K"], vm.Fields.Select(f => f.Name));

        vm.Try = "november four one";
        vm.PickCell(5, shift: false);
        Assert.Equal("types ‘N41’", vm.SelectedPreview);
        Assert.True(vm.Keys.Single(k => k.Character == "N").Used);
        Assert.Empty(vm.Problems);

        vm.Fields[1].AlignRight = true;
        Assert.Equal(TextAlign.Right, vm.ToStep().Fields![1].Align);
    }

    [Fact]
    public void Switching_the_method_turns_wheels_into_the_keypad_field()
    {
        var vm = Create(NewUhf(), new Cockpit());
        Pick(vm, 0, 2);
        vm.AddWheelCommand.Execute(null);
        Pick(vm, 3, 3);
        vm.AddReadOnlyCommand.Execute(null);

        vm.IsKeypad = true;

        Assert.Equal([FieldKind.Keypad, FieldKind.ReadOnly], vm.Fields.Select(f => f.Kind));
        vm.Fields[0].Kind = FieldKind.Wheel;
        Assert.Equal(FieldKind.Keypad, vm.Fields[0].Kind);
        Assert.Contains("Switches / dials", vm.Message);
    }

    [Fact]
    public void Switching_several_wheels_to_keypad_asks_for_the_one_field()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);

        vm.IsKeypad = true;

        Assert.Equal([FieldKind.ReadOnly], vm.Fields.Select(f => f.Kind));
        Assert.Contains("one field", vm.Message);
    }

    [Fact]
    public void An_old_wheel_step_is_converted_and_says_when_the_layout_was_guessed()
    {
        var old = new EnterValueStep
        {
            Value = "{freq}", Min = 225, Max = 399.975m,
            Wheels =
            [
                new() { Control = "PLT_UHF1_110_DIAL", Place = 10, Width = 2, Read = "PLT_UHF_HIGH_FREQ" },
                new() { Control = "PLT_UHF1_1_DIAL", Place = 1, Read = "PLT_UHF_HIGH_FREQ" },
            ],
            Verify = new() { Output = "PLT_UHF_DISP", TimeoutMs = 1500 },
        };

        var offline = Create(old);
        Assert.True(offline.HasLegacyNote);
        Assert.Contains("guessed", offline.LegacyNote);
        Assert.Equal(["W1", "W2"], offline.Fields.Select(f => f.Name));
        Assert.Equal("22-39", offline.Fields[0].Range);

        var live = Create(old, new Cockpit());
        Assert.Contains("what the display shows now", live.LegacyNote);
        Assert.Equal("251.000", live.ToStep().Sample);
    }

    [Fact]
    public void An_old_keypad_step_is_kept_until_its_field_is_picked()
    {
        var old = new EnterValueStep
        {
            Value = "{text}", Method = EnterMethod.Keypad,
            Keys = new() { ["N"] = "CDU_N", ["4"] = "CDU_4", ["1"] = "CDU_1" },
            Before = ["CDU_CLR"],
            Verify = new() { Output = "CDU_LINE9" },
        };
        var vm = Create(old, new Cockpit(), phrase: "type {text:text}");

        Assert.True(vm.HasLegacyNote);
        Assert.True(vm.HasHiddenKeys);
        Assert.Contains(vm.Problems, p => p.Contains("old layout"));
        var kept = vm.ToStep();
        Assert.Null(kept.Fields);
        Assert.Equal("CDU_LINE9", kept.Verify!.Output);

        Pick(vm, 1, 22);
        vm.AddKeypadCommand.Execute(null);
        var step = vm.ToStep();
        Assert.Equal("CDU_LINE9", step.Display);
        Assert.Equal(["CDU_CLR"], step.Before!);
        Assert.Equal(FieldKind.Keypad, Assert.Single(step.Fields!).Kind);
    }

    [Fact]
    public void Opens_a_saved_step_unchanged()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);
        vm.Fields[0].Range = "22-39";
        var saved = vm.ToStep();

        var reopened = Create(EnterValueEditorViewModel.Copy(saved), new Cockpit()).ToStep();

        Assert.Equal(ProfileSerializer.Serialize(Wrap(saved)), ProfileSerializer.Serialize(Wrap(reopened)));
    }

    [Fact]
    public void Add_enter_value_adds_one_step_and_edit_opens_the_window()
    {
        var command = new Command { Name = "UHF", Phrases = ["uhf [frequency;] {freq}"] };
        var dialogs = new FakeDialogs();
        var cockpit = new Cockpit();
        var editor = new CommandEditorViewModel(command, dialogs, () => { }, () => new EnterValueContext(Catalog(), () => cockpit.Buffer, cockpit.Send));

        editor.AddEnterValueCommand.Execute(null);
        var added = Assert.IsType<EnterValueStep>(Assert.Single(command.Steps));
        Assert.Equal("{freq}", added.Value);
        Assert.Equal("enter {freq} on 0 wheels", editor.Steps[0].Text);

        dialogs.OnEnterValue = vm =>
        {
            vm.SuggestCommand.Execute(null);
            return false; // Cancel
        };
        editor.EditStepCommand.Execute(editor.Steps[0]);
        Assert.Null(((EnterValueStep)command.Steps[0]).Fields);

        dialogs.OnEnterValue = vm =>
        {
            vm.SuggestCommand.Execute(null);
            return true; // OK
        };
        editor.EditStepCommand.Execute(editor.Steps[0]);
        Assert.Equal("enter {freq} on 4 wheels, check PLT_UHF_DISP", editor.Steps[0].Text);
    }

    [Fact]
    public void A_text_slot_makes_a_keypad_step()
    {
        var command = new Command { Name = "WP", Phrases = ["waypoint name {name:text}"] };
        var editor = new CommandEditorViewModel(command, new FakeDialogs(), () => { });
        editor.AddEnterValueCommand.Execute(null);
        var step = Assert.IsType<EnterValueStep>(command.Steps[0]);
        Assert.Equal(EnterMethod.Keypad, step.Method);
        Assert.Equal("{name}", step.Value);
    }

    [Fact]
    public void Control_names_get_the_catalogs_spelling_and_unknown_ones_are_flagged()
    {
        var vm = Create(NewUhf(), new Cockpit());
        vm.SuggestCommand.Execute(null);

        vm.Fields[1].Control = "plt_uhf1_1_dial";
        Assert.Equal("PLT_UHF1_1_DIAL", vm.Fields[1].Control);
        Assert.Contains("PLT_UHF1_1_DIAL", vm.InputControls);
        Assert.Contains("PLT_UHF_DISP", vm.StringOutputs);
        Assert.DoesNotContain(vm.Problems, p => p.Contains("not a DCS-BIOS control"));

        vm.Fields[1].Control = "PLT_UHF1_1_DAIL";
        Assert.Contains(vm.Problems, p => p.Contains("\"PLT_UHF1_1_DAIL\" is not a DCS-BIOS control"));
    }

    // ---- Learn from display ----

    /// <summary>The UHF wheels (wrapping) and the CDU scratchpad, as the export buffer shows them.</summary>
    private sealed class ProbeCockpit
    {
        public StateBuffer Buffer { get; } = new();
        public int Pair = 25, One = 1, Tenth = 3, Quarter = 2;
        public string Scratch = "";
        public List<string> Sent { get; } = [];
        public Action? OnSend;

        public ProbeCockpit() => Publish();

        public string Uhf => $"{Pair}{One}.{Tenth}{Quarter * 25:00}";

        public Task Send(string control, string arg)
        {
            Sent.Add(control + " " + arg);
            if (arg is "2" or "0")
            {
                var dir = arg == "2" ? 1 : -1;
                switch (control)
                {
                    case "PLT_UHF1_110_DIAL": Pair = 22 + (Pair - 22 + dir + 18) % 18; break;
                    case "PLT_UHF1_1_DIAL": One = (One + dir + 10) % 10; break;
                    case "PLT_UHF1_01_DIAL": Tenth = (Tenth + dir + 10) % 10; break;
                    case "PLT_UHF1_025_DIAL": Quarter = (Quarter + dir + 4) % 4; break;
                }
            }
            if (arg == "1" && control.StartsWith("CDU_"))
            {
                var name = control[4..];
                if (name == "CLR") Scratch = "";
                else if (Scratch.Length < 22) Scratch += name == "POINT" ? "." : name;
            }
            Publish();
            OnSend?.Invoke();
            return Task.CompletedTask;
        }

        private void Publish()
        {
            Buffer.Write(DispAddress, Encoding.ASCII.GetBytes(Uhf));
            Buffer.Write(CduAddress, Encoding.ASCII.GetBytes("[" + Scratch.PadRight(22) + "]"));
        }
    }

    private static EnterValueEditorViewModel CreateProbe(EnterValueStep step, ProbeCockpit? cockpit, FakeDialogs? dialogs = null) =>
        new(step, ["uhf {freq}"], new EnterValueContext(Catalog(), () => cockpit?.Buffer, cockpit is null ? null : cockpit.Send), dialogs ?? new FakeDialogs())
        {
            TestDelay = TimeSpan.Zero,
        };

    [Fact]
    public void Opening_the_editor_and_refreshing_sends_nothing()
    {
        var cockpit = new ProbeCockpit();
        var vm = CreateProbe(NewUhf(), cockpit);
        vm.RefreshLive();
        vm.RefreshLive();

        Assert.Empty(cockpit.Sent);
        Assert.False(vm.IsChoosing);
        Assert.False(vm.IsLearning);
    }

    [Fact]
    public void Learn_needs_dcs()
    {
        var vm = CreateProbe(NewUhf(), null);
        Assert.False(vm.LearnCommand.CanExecute(null));
    }

    [Fact]
    public async Task Learn_wheels_lists_candidates_asks_first_and_puts_the_wheels_back()
    {
        var cockpit = new ProbeCockpit();
        var dialogs = new FakeDialogs();
        var vm = CreateProbe(NewUhf(), cockpit, dialogs);
        var before = cockpit.Uhf;

        vm.LearnCommand.Execute(null);
        Assert.True(vm.IsChoosing);
        Assert.Equal(4, vm.LearnCandidates.Count(c => c.IsTicked));
        Assert.Empty(cockpit.Sent); // listing sends nothing

        dialogs.ConfirmAnswers.Enqueue(false);
        await vm.StartLearnCommand.ExecuteAsync();
        Assert.Empty(cockpit.Sent); // declined

        dialogs.ConfirmAnswers.Enqueue(true);
        await vm.StartLearnCommand.ExecuteAsync();

        Assert.Contains(dialogs.Asked, a => a.Contains("puts them back"));
        Assert.False(vm.IsLearning);
        Assert.False(vm.IsChoosing);
        Assert.Equal(
            [(0, 2, "22-39"), (2, 1, "0-9"), (3, 1, null), (4, 1, "0-9"), (5, 2, "00,25,50,75")],
            vm.Fields.Select(f => (f.At, f.Len, f.IsReadOnly ? null : f.Range)).ToList());
        Assert.Equal("PLT_UHF1_110_DIAL", vm.Fields[0].Control);
        Assert.Contains("Check them before saving", vm.Message);
        Assert.Equal(before, cockpit.Uhf);
        Assert.DoesNotContain(vm.Problems, p => p.Contains("not a DCS-BIOS control"));
    }

    [Fact]
    public async Task Cancel_stops_the_probe_and_puts_the_wheels_back()
    {
        var cockpit = new ProbeCockpit { Pair = 31, One = 6, Tenth = 8, Quarter = 3 };
        var dialogs = new FakeDialogs();
        var vm = CreateProbe(NewUhf(), cockpit, dialogs);
        var before = cockpit.Uhf;
        vm.LearnCommand.Execute(null);
        dialogs.ConfirmAnswers.Enqueue(true);
        cockpit.OnSend = () => { if (cockpit.Sent.Count == 60) vm.StopLearning(); };

        await vm.StartLearnCommand.ExecuteAsync();

        Assert.Equal(before, cockpit.Uhf);
        Assert.StartsWith("Cancelled", vm.Message);
        Assert.False(vm.IsLearning);
    }

    [Fact]
    public async Task Cancel_in_the_list_sends_nothing()
    {
        var cockpit = new ProbeCockpit();
        var vm = CreateProbe(NewUhf(), cockpit);
        vm.LearnCommand.Execute(null);
        vm.CancelLearnCommand.Execute(null);

        Assert.False(vm.IsChoosing);
        Assert.Empty(cockpit.Sent);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Learn_keypad_builds_the_keys_the_clear_key_and_the_entry_field()
    {
        var cockpit = new ProbeCockpit();
        var dialogs = new FakeDialogs();
        var vm = CreateProbe(new EnterValueStep { Value = "{name}", Display = "CDU_LINE9", Method = EnterMethod.Keypad }, cockpit, dialogs);

        vm.LearnCommand.Execute(null);
        dialogs.ConfirmAnswers.Enqueue(true);
        await vm.StartLearnCommand.ExecuteAsync();

        Assert.True(vm.IsKeypad);
        var field = Assert.Single(vm.Fields, f => f.IsKeypad);
        Assert.Equal((1, 22, TextAlign.Left), (field.At, field.Len, field.Align));
        Assert.Equal("CDU_A", vm.Keys.First(k => k.Character == "A").Control);
        Assert.Equal("CDU_POINT", vm.Keys.First(k => k.Character == ".").Control);
        Assert.Equal(["CDU_CLR"], vm.ToStep().Before);
        Assert.Equal("", cockpit.Scratch);
        Assert.Contains("Learned", vm.Message);
    }

    private static Profile Wrap(Step step) => new() { Commands = [new Command { Name = "C", Steps = [step] }] };
}
