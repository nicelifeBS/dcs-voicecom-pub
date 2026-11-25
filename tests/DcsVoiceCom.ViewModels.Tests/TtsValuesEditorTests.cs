using System.Text;
using DcsVoiceCom.Config;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels.Tests;

public class TtsValuesEditorTests
{
    private const int ThrottleAddress = 0x2000, ClockAddress = 0x2100;

    private static ControlCatalog Catalog() => new("TestJet", [],
    [
        new ControlDefinition
        {
            Identifier = "THROTTLE", Category = "Engine",
            Outputs = [new OutputDefinition { Type = "integer", Address = ThrottleAddress, Mask = 0xFFFF, MaxValue = 65535 }],
        },
        new ControlDefinition
        {
            Identifier = "CLOCK_DISP", Category = "Displays",
            Outputs = [new OutputDefinition { Type = "string", Address = ClockAddress, MaxLength = 6 }],
        },
    ]);

    private static StateBuffer Cockpit(int throttle = 52428, string clock = "203422")
    {
        var buffer = new StateBuffer();
        buffer.Write(ThrottleAddress, [(byte)(throttle & 0xFF), (byte)(throttle >> 8)]);
        buffer.Write(ClockAddress, Encoding.ASCII.GetBytes(clock));
        return buffer;
    }

    private static Profile Jet(params TtsValue[] values) => new()
    {
        Aircraft = "TestJet",
        Values = values.Length == 0 ? null : [.. values],
        Commands =
        [
            new Command { Name = "Time", Phrases = ["time check"], Steps = [new SayStep { Text = "Time {clock}" }] },
            new Command { Name = "Both", Phrases = ["status"], Steps = [new IfStep { Then = [new SayStep { Text = "{clock} and {throttle}" }] }] },
            new Command { Name = "Clash", Phrases = ["set clock {clock:1-12}"], Steps = [new SayStep { Text = "Clock {clock}" }] },
        ],
    };

    private static TtsValue Clock() => new() { Name = "clock", Output = "CLOCK_DISP", Template = "{0-1}:{2-3}:{4-5}" };

    private static TtsValue Throttle() => new()
    {
        Name = "throttle", Output = "THROTTLE", Mode = TtsValueMode.Remap, InMin = 0, InMax = 65535, OutMin = 0, OutMax = 100,
    };

    private static TtsValuesEditorViewModel Create(Profile profile, StateBuffer? live = null, FakeDialogs? dialogs = null, SayContext? say = null, bool pick = false) =>
        new(profile, new EnterValueContext(Catalog(), () => live, null), say ?? SayContext.Offline, dialogs ?? new FakeDialogs(), pick);

    [Fact]
    public void Shows_the_live_value_sliced_with_usage_and_slot_clash()
    {
        var vm = Create(Jet(Clock(), Throttle()), Cockpit());

        Assert.Equal(["{clock}", "{throttle}"], vm.Items.Select(i => i.Label));
        Assert.True(vm.IsLive);
        Assert.Equal("203422", vm.Sample);
        Assert.Equal(["2", "0", "3", "4", "2", "2"], vm.Cells.Select(c => c.Text));
        Assert.Equal("20:34:22", vm.Result);
        Assert.Equal("Used in 2 commands", vm.UsageText); // "Clash" means its spoken slot
        Assert.Contains("spoken slot in 1 command", vm.ClashText);
        Assert.False(vm.CanRemap); // a display has only text
    }

    [Fact]
    public void Remaps_the_live_throttle_and_follows_it()
    {
        var buffer = Cockpit();
        var vm = Create(Jet(Clock(), Throttle()), buffer);
        vm.Selected = vm.Items[1];

        Assert.True(vm.IsRemap);
        Assert.Equal("80", vm.Result);
        vm.Decimals = 1;
        Assert.Equal("80.0", vm.Result);

        buffer.Write(ThrottleAddress, [0xFF, 0xFF]);
        vm.RefreshLive();
        Assert.Equal("100.0", vm.Result);
    }

    [Fact]
    public void Builds_a_template_by_clicking_characters_on_a_typed_sample_without_dcs()
    {
        var dialogs = new FakeDialogs();
        dialogs.PromptAnswers.Enqueue("time");
        var vm = Create(Jet(), dialogs: dialogs);

        vm.NewCommand.Execute(null);
        Assert.Equal("time", vm.Name);
        Assert.True(vm.IsOffline);
        vm.Output = "THROTTLE";
        vm.Pad = "4";
        vm.Sample = "509";
        Assert.Equal(["0", "5", "0", "9"], vm.Cells.Select(c => c.Text));

        Assert.False(vm.AddPieceCommand.CanExecute(null));
        vm.SelectCell(0, extend: false);
        vm.SelectCell(1, extend: true);
        Assert.Equal("{0-1}", vm.PieceText);
        vm.AddPieceCommand.Execute(null);
        vm.Template += ":";
        vm.SelectCell(2, extend: false);
        vm.SelectCell(3, extend: true);
        vm.AddPieceCommand.Execute(null);

        Assert.Equal("{0-1}:{2-3}", vm.Template);
        Assert.Equal("05:09", vm.Result);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Switching_to_remap_fills_the_cockpit_range_from_dcs_bios()
    {
        var vm = Create(Jet(new TtsValue { Name = "t", Output = "THROTTLE", Template = "{0}" }));

        vm.IsRemap = true;

        Assert.Equal("0", vm.InMin);
        Assert.Equal("65535", vm.InMax);
        Assert.Equal("100", vm.OutMax);
        Assert.Contains("65535", vm.MaxValueText);
    }

    [Fact]
    public void Shows_errors_and_blocks_ok_for_bad_values()
    {
        var vm = Create(Jet(Clock()), Cockpit(clock: "2034"));

        Assert.Contains("{4-5}", vm.Error);

        vm.Template = "";
        Assert.False(vm.CanSave);
        Assert.Contains("template", vm.SaveProblem);

        vm.Template = "{0-1}";
        Assert.True(vm.CanSave);
        vm.Name = "two words";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Rename_and_delete_apply_to_the_profile_on_ok()
    {
        var profile = Jet(Clock(), Throttle());
        var dialogs = new FakeDialogs();
        dialogs.PromptAnswers.Enqueue("time");
        dialogs.ConfirmAnswers.Enqueue(true);
        var vm = Create(profile, dialogs: dialogs);

        vm.RenameCommand.Execute(null);
        vm.Selected = vm.Items[1];
        vm.DeleteCommand.Execute(null);
        Assert.Contains("used in 1 command", dialogs.Asked.Last());

        vm.ApplyTo(profile);
        Assert.Equal(["time"], profile.Values!.Select(v => v.Name));
        Assert.Equal("Time {time}", ((SayStep)profile.Commands[0].Steps[0]).Text);
        Assert.Equal("{time} and {throttle}", ((SayStep)((IfStep)profile.Commands[1].Steps[0]).Then[0]).Text);
        Assert.Equal("Clock {clock}", ((SayStep)profile.Commands[2].Steps[0]).Text); // the spoken slot stays
    }

    [Fact]
    public void Play_speaks_the_result()
    {
        var voice = new FakeVoice(new VoiceInfo("en1", "David", "en-US"));
        var vm = Create(Jet(Clock()), Cockpit(), say: new SayContext(voice, () => new TtsOptions("en1")));

        vm.PlayCommand.Execute(null);

        Assert.Equal("20:34:22", Assert.Single(voice.Spoken).Text);
    }
}

public class TtsValuesInMainWindowTests
{
    private static Command TimeCheck() => new() { Name = "Time", Phrases = ["time check"], Steps = [new SayStep { Text = "Time {clock}" }] };

    [Fact]
    public void The_tts_button_edits_the_profile_values_and_marks_it_changed()
    {
        using var ws = new Workspace();
        ws.Store.Save(new Profile
        {
            Aircraft = "TestJet", Commands = [TimeCheck()],
            Values = [new TtsValue { Name = "clock", Output = "CLOCK", Template = "{0-1}" }],
        });
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(ws.Store, ws.Definitions, dialogs, InlineUiThread.Instance);
        Assert.False(vm.OpenTtsValuesCommand.CanExecute(null));
        vm.SelectedProfile = vm.Profiles[0];

        dialogs.OnTtsValues = editor => false;
        vm.OpenTtsValuesCommand.Execute(null);
        Assert.False(vm.IsDirty);

        dialogs.OnTtsValues = editor =>
        {
            editor.Name = "time";
            return true;
        };
        vm.OpenTtsValuesCommand.Execute(null);

        Assert.True(vm.IsDirty);
        Assert.Equal("say \"Time {time}\"", vm.Editor!.Steps[0].Text);
    }

    [Fact]
    public void Insert_value_in_the_say_window_picks_one_and_applies_renames_to_the_text()
    {
        using var ws = new Workspace();
        ws.Store.Save(new Profile
        {
            Aircraft = "TestJet", Commands = [TimeCheck()],
            Values = [new TtsValue { Name = "clock", Output = "CLOCK", Template = "{0-1}" }],
        });
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(ws.Store, ws.Definitions, dialogs, InlineUiThread.Instance);
        vm.SelectedProfile = vm.Profiles[0];

        string? inserted = null;
        dialogs.OnTtsValues = editor =>
        {
            Assert.True(editor.PickMode);
            Assert.Equal("Insert {clock}", editor.PrimaryLabel);
            editor.Name = "zulu";
            return true;
        };
        dialogs.OnSay = say =>
        {
            Assert.True(say.CanPickValue);
            Assert.Equal("Time clock", say.PreviewSpoken); // no DCS: the name
            inserted = say.PickValue();
            say.Text += " " + inserted;
            return true;
        };
        vm.Editor!.EditStepCommand.Execute(vm.Editor.Steps[0]);

        Assert.Equal("{zulu}", inserted);
        Assert.Equal("Time {zulu} {zulu}", ((SayStep)vm.Editor.Command.Steps[0]).Text);
        Assert.True(vm.IsDirty);
    }
}
