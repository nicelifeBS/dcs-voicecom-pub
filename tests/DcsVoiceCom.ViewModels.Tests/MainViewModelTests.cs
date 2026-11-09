using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels.Tests;

public class MainViewModelTests
{
    private static Command Gear(string name = "Gear up", string arg = "0") =>
        new() { Name = name, Phrases = ["gear up"], Steps = [new BiosStep { Control = "GEAR_LEVER", Arg = arg }] };

    private static MainViewModel Create(Workspace ws, FakeDialogs dialogs, FakeRuntime? runtime = null)
    {
        var vm = new MainViewModel(ws.Store, ws.Definitions, dialogs, InlineUiThread.Instance);
        if (runtime != null) vm.AttachRuntime(runtime);
        return vm;
    }

    [Fact]
    public void Lists_profiles_with_common_last_and_loads_commands_and_controls()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear(), Gear("Gear down", "1"));
        var vm = Create(ws, new FakeDialogs());

        Assert.Equal(["TestJet", "common"], vm.Profiles.Select(p => p.Name));
        Assert.Equal(2, vm.Profiles[0].CommandCount);
        Assert.Equal(["Auto", "TestJet"], vm.OverrideOptions);

        vm.SelectedProfile = vm.Profiles[0];
        Assert.Equal(["Gear up", "Gear down"], vm.Commands.Select(c => c.Name));
        Assert.Equal("GEAR_LEVER 0", vm.Commands[0].Summary);
        Assert.Same(vm.Commands[0], vm.SelectedCommand);
        Assert.NotNull(vm.Editor);
        Assert.Equal(5, vm.Browser.Results.Count);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Editing_marks_dirty_and_save_writes_the_file_and_reloads_the_active_profile()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var runtime = new FakeRuntime { DcsAircraft = "TestJet" };
        var vm = Create(ws, new FakeDialogs(), runtime);
        vm.SelectedProfile = vm.Profiles[0];
        Assert.True(vm.Profiles[0].IsActive);

        vm.Editor!.VoiceLine = "[gear;landing gear] up";
        vm.Editor.AlternativesText = "wheels up";
        vm.Editor.Critical = true;
        Assert.True(vm.IsDirty);
        Assert.Equal(["gear up", "landing gear up", "wheels up"], vm.Editor.PhrasePreview);
        Assert.Equal("[gear;landing gear] up", vm.Commands[0].Phrase);
        Assert.Equal("[gear;landing gear] up", vm.Commands[0].Name);

        await vm.SaveCommand.ExecuteAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal(1, runtime.Reloads);
        var saved = ws.Store.TryLoad("TestJet")!;
        Assert.Equal(["[gear;landing gear] up", "wheels up"], saved.Commands[0].Phrases);
        Assert.True(saved.Commands[0].Critical);
    }

    private static Command Say(string phrase) => new() { Name = phrase, Phrases = [phrase] };

    [Fact]
    public async Task Command_list_is_sorted_by_voice_line_but_the_file_keeps_its_order()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Say("lights on"), Say("Canopy close"), Say("gear up"));
        var vm = Create(ws, new FakeDialogs());
        vm.SelectedProfile = vm.Profiles[0];
        Assert.Equal(["Canopy close", "gear up", "lights on"], vm.Commands.Select(c => c.Phrase));
        Assert.Equal("Canopy close", vm.SelectedCommand!.Phrase);

        // A duplicate goes to its sorted place, right after the original.
        vm.DuplicateCommandCommand.Execute(vm.Commands[1]);
        Assert.Equal(["Canopy close", "gear up", "gear up", "lights on"], vm.Commands.Select(c => c.Phrase));

        // A changed voice line keeps its place while editing and moves on save, staying selected.
        vm.SelectedCommand = vm.Commands[3];
        vm.Editor!.VoiceLine = "arm guns";
        Assert.Equal("arm guns", vm.Commands[3].Phrase);
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal(["arm guns", "Canopy close", "gear up", "gear up"], vm.Commands.Select(c => c.Phrase));
        Assert.Same(vm.Commands[0], vm.SelectedCommand);

        // The file keeps the order the commands were entered in; new ones are appended.
        var saved = ws.Store.TryLoad("TestJet")!;
        Assert.Equal(["arm guns", "Canopy close", "gear up", "gear up"], saved.Commands.Select(c => c.Phrases[0]));
        Assert.Equal(["arm guns", "Canopy close", "gear up", "gear up copy"], saved.Commands.Select(c => c.Name));
    }

    [Fact]
    public void Delete_asks_first_and_keeps_the_command_on_no()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear(), Gear("Gear down", "1"));
        var dialogs = new FakeDialogs();
        var vm = Create(ws, dialogs);
        vm.SelectedProfile = vm.Profiles[0];

        dialogs.ConfirmAnswers.Enqueue(false);
        vm.DeleteCommandCommand.Execute(vm.Commands[0]);
        Assert.Equal(2, vm.Commands.Count);
        Assert.Contains("Delete “Gear up” from TestJet?", dialogs.Asked);

        dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteCommandCommand.Execute(vm.Commands[0]);
        Assert.Equal(["Gear down"], vm.Commands.Select(c => c.Name));
        Assert.Same(vm.Commands[0], vm.SelectedCommand);
        Assert.True(vm.IsDirty);
        Assert.Equal(1, vm.Profiles[0].CommandCount);
    }

    [Fact]
    public void Delete_profile_asks_first_removes_the_file_and_never_deletes_common()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var dialogs = new FakeDialogs();
        var vm = Create(ws, dialogs);
        vm.SelectedProfile = vm.Profiles[0];
        vm.Editor!.VoiceLine = "unsaved edit";
        Assert.False(vm.DeleteProfileCommand.CanExecute(vm.Profiles[1]));

        dialogs.ConfirmAnswers.Enqueue(false);
        vm.DeleteProfileCommand.Execute(null);
        Assert.True(ws.Store.Exists("TestJet"));

        dialogs.ConfirmAnswers.Enqueue(true);
        vm.DeleteProfileCommand.Execute(null);
        Assert.False(ws.Store.Exists("TestJet"));
        Assert.Equal(["common"], vm.Profiles.Select(p => p.Name));
        Assert.Same(vm.Profiles[0], vm.SelectedProfile);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Leaving_a_dirty_profile_asks_to_save_and_cancel_stays()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var dialogs = new FakeDialogs();
        var vm = Create(ws, dialogs);
        vm.SelectedProfile = vm.Profiles[0];
        vm.Editor!.Name = "Wheels up";

        dialogs.ConfirmOrCancelAnswers.Enqueue(null);
        vm.SelectedProfile = vm.Profiles[1];
        Assert.Equal("TestJet", vm.SelectedProfile!.Name);
        Assert.True(vm.IsDirty);

        dialogs.ConfirmOrCancelAnswers.Enqueue(false);
        vm.SelectedProfile = vm.Profiles[1];
        Assert.Equal("common", vm.SelectedProfile!.Name);
        Assert.Equal("Gear up", ws.Store.TryLoad("TestJet")!.Commands[0].Name);

        vm.SelectedProfile = vm.Profiles[0];
        vm.Editor!.Name = "Wheels up";
        dialogs.ConfirmOrCancelAnswers.Enqueue(true);
        Assert.True(vm.ConfirmClose());
        Assert.Equal("Wheels up", ws.Store.TryLoad("TestJet")!.Commands[0].Name);
    }

    [Fact]
    public void Picking_a_control_argument_adds_a_step_or_a_new_command()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var vm = Create(ws, new FakeDialogs());
        vm.SelectedProfile = vm.Profiles[0];

        vm.Browser.Query = "master arm";
        vm.Browser.Results.Single().Arguments.Single(a => a.Arg == "1" && !a.Press).Pick.Execute(null);
        Assert.Equal("GEAR_LEVER 0, MASTER_ARM 1", vm.Commands[0].Summary);

        vm.SelectedCommand = null;
        vm.Browser.Query = "gear";
        vm.Browser.Results.Single().Arguments.Single(a => a.Arg == "1" && !a.Press).Pick.Execute(null);
        Assert.Equal(2, vm.Commands.Count);
        Assert.Equal("Landing Gear Lever 1 DN", vm.SelectedCommand!.Name);
        Assert.Equal("landing gear lever dn", vm.SelectedCommand.Phrase);
        Assert.Equal("GEAR_LEVER 1", vm.SelectedCommand.Summary);

        vm.SelectedCommand = null;
        vm.Browser.Results.Single().Arguments.Single(a => a.Press).Pick.Execute(null);
        Assert.Equal(3, vm.Commands.Count);
        Assert.Equal("Press Landing Gear Lever", vm.SelectedCommand!.Name);
        Assert.Equal("press landing gear lever", vm.SelectedCommand.Phrase);
        Assert.Equal("press GEAR_LEVER 1/0 (50 ms)", vm.SelectedCommand.Summary);
    }

    [Fact]
    public void New_profile_offers_known_aircraft_and_prefers_the_one_flying()
    {
        using var ws = new Workspace();
        var dialogs = new FakeDialogs();
        var vm = Create(ws, dialogs, new FakeRuntime { DcsAircraft = "TestJet" });
        dialogs.PromptAnswers.Enqueue("TestJet");
        vm.NewProfileCommand.Execute(null);

        Assert.Equal(["TestJet"], dialogs.LastSuggestions);
        Assert.True(ws.Store.Exists("TestJet"));
        Assert.Equal("TestJet", vm.SelectedProfile!.Name);
    }

    [Fact]
    public async Task Test_runs_through_the_runtime_and_logs_the_outcome_and_dry_run_steps()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var runtime = new FakeRuntime { DcsAircraft = "TestJet" };
        var vm = Create(ws, new FakeDialogs(), runtime);
        vm.SelectedProfile = vm.Profiles[0];
        runtime.NextOutcome = new CommandOutcome(OutcomeStatus.Completed,
            new RecognitionResult { Raw = "gear up", Phrase = "gear up", Score = 100 }, "Gear up",
            new Dictionary<string, string>(), ["bios GEAR_LEVER 0"], null);

        await vm.TestSelectedCommand.ExecuteAsync();
        Assert.Equal([("gear up", true)], runtime.Tests);
        Assert.Contains(vm.Log.Entries, e => e.Tag == "DRY" && e.Message.Contains("→ Gear up"));
        Assert.Contains(vm.Log.Entries, e => e.Tag == "STEP" && e.Message == "bios GEAR_LEVER 0");

        vm.DryRun = false;
        vm.TestText = "gear down";
        await vm.TestCommand.ExecuteAsync();
        Assert.Equal(("gear down", false), runtime.Tests[^1]);
    }

    [Fact]
    public void Profile_override_goes_to_the_runtime_and_is_reported()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var runtime = new FakeRuntime();
        var vm = Create(ws, new FakeDialogs(), runtime);
        string? reported = "unset";
        vm.ProfileOverrideChanged += p => reported = p;

        vm.SelectedOverride = "TestJet";
        Assert.Equal("TestJet", runtime.ProfileOverride);
        Assert.Equal("TestJet", reported);
        Assert.True(vm.Profiles[0].IsActive);

        vm.SelectedOverride = MainViewModel.AutoProfile;
        Assert.Null(runtime.ProfileOverride);
        Assert.Null(reported);
    }

    [Fact]
    public void Status_and_live_values_follow_the_runtime()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(Gear());
        var runtime = new FakeRuntime();
        var vm = Create(ws, new FakeDialogs(), runtime);
        vm.SelectedProfile = vm.Profiles[0];
        vm.Browser.Query = "gear";
        runtime.State.Write(4608, [1, 0]);

        vm.Tick();
        Assert.Equal("DCS-BIOS · waiting for DCS", vm.DcsStatus);
        Assert.Equal("DCS-BIOS", vm.DcsText);
        Assert.Equal("", vm.Browser.Results[0].LiveValue);

        runtime.DcsAircraft = "TestJet";
        runtime.IsLive = true;
        runtime.FrameCount = 10;
        vm.Tick();
        Assert.Equal("DCS-BIOS · TestJet", vm.DcsStatus);
        Assert.Equal("DCS-BIOS · TestJet", vm.DcsText);
        Assert.Equal("Speech · ready", vm.SpeechStatus);
        Assert.True(vm.SpeechReady);
        Assert.Equal("1 DN", vm.Browser.Results[0].LiveValue);

        runtime.RaiseListening(true);
        Assert.Equal("Listening", vm.ListeningText);
        Assert.Equal("Listening: speak now", vm.ListeningTip);
        Assert.Contains(vm.Log.Entries, e => e.Tag == "PTT");
    }
}
