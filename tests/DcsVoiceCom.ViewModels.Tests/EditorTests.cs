using DcsVoiceCom.Config;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels.Tests;

// Note: CommandEditorViewModel.LastWaitMs is static, so every test that adds or edits a wait step changes it for
// the tests that run after. That is safe today because all such tests are in this class, and xUnit runs the tests
// of one class one after another. If a test in another class adds or edits a wait, the two classes can run at the
// same time and the "+ Wait" tests here may see the wrong value and fail now and then. Then either reset LastWaitMs
// at the start of that test too, or put both classes in one [Collection] so they never run at the same time.
public class EditorTests
{
    private static (CommandEditorViewModel Editor, Command Command, FakeDialogs Dialogs, Func<int> Changes) Create(params Step[] steps)
    {
        var command = new Command { Name = "Test", Phrases = ["test"], Steps = steps.ToList() };
        var dialogs = new FakeDialogs();
        var changes = 0;
        var editor = new CommandEditorViewModel(command, dialogs, () => changes++);
        return (editor, command, dialogs, () => changes);
    }

    [Fact]
    public void Settings_build_keeps_matching_scores_and_rejects_bad_ones()
    {
        var vm = new SettingsViewModel(new AppSettings());
        Assert.Equal("85", vm.Threshold);
        Assert.Equal("95", vm.CriticalThreshold);

        vm.Threshold = "70";
        vm.CriticalThreshold = "90";
        var built = vm.Build();
        Assert.Equal(70, built!.Threshold);
        Assert.Equal(90, built.CriticalThreshold);

        vm.Threshold = "20";
        Assert.Null(vm.Build());
        Assert.Contains("50 to 100", vm.Error);
    }

    [Fact]
    public void Reports_phrase_problems()
    {
        var (editor, command, _, _) = Create();
        editor.VoiceLine = "[gear;landing gear up";
        Assert.True(editor.HasProblems);
        editor.VoiceLine = "";
        Assert.Contains("Enter the voice line.", editor.Problems);
        editor.VoiceLine = "tacan {channel:1-126}";
        Assert.False(editor.HasProblems);
        Assert.Equal(["tacan {channel:1-126}"], command.Phrases);
        Assert.Equal("tacan {channel:1-126}", command.Name);
    }

    [Fact]
    public void Voice_line_is_the_first_phrase_and_names_the_command()
    {
        var command = new Command { Name = "Gear up", Phrases = ["gear up", "wheels up", "raise gear"] };
        var editor = new CommandEditorViewModel(command, new FakeDialogs(), () => { });
        Assert.Equal("gear up", editor.VoiceLine);
        Assert.Equal("wheels up" + Environment.NewLine + "raise gear", editor.AlternativesText);
        Assert.Equal("Gear up", command.Name); // unchanged until the voice line is edited

        editor.AlternativesText = "wheels up\n\n gear up \n";
        Assert.Equal(["gear up", "wheels up"], command.Phrases);
        editor.VoiceLine = "landing gear up";
        Assert.Equal("landing gear up", command.Name);
        Assert.Equal(["landing gear up", "wheels up", "gear up"], command.Phrases);
    }

    [Fact]
    public void Adds_moves_edits_and_removes_steps()
    {
        var (editor, command, dialogs, changes) = Create(new BiosStep { Control = "A", Arg = "1" });
        dialogs.PromptAnswers.Enqueue("rctrl+rshift+g");
        editor.AddKeyCommand.Execute(null);
        dialogs.PromptAnswers.Enqueue("250");
        editor.AddWaitCommand.Execute(null);
        Assert.Equal(["A 1", "key RCtrl+RShift+G", "wait 250 ms"], editor.Steps.Select(s => s.Text));

        editor.MoveUpCommand.Execute(editor.Steps[2]);
        Assert.Equal(["A 1", "wait 250 ms", "key RCtrl+RShift+G"], editor.Steps.Select(s => s.Text));

        dialogs.PromptAnswers.Enqueue("TOGGLE");
        editor.EditStepCommand.Execute(editor.Steps[0]);
        editor.Steps[0].SkipIfAlready = true;
        Assert.Equal("A TOGGLE", editor.Steps[0].Text);

        editor.RemoveStepCommand.Execute(editor.Steps[1]);
        Assert.Equal(2, command.Steps.Count);
        Assert.True(changes() >= 6);
    }

    [Fact]
    public void A_bios_step_turns_into_a_press_and_back()
    {
        var (editor, command, _, changes) = Create(new BiosStep { Control = "U1", Arg = "1", SkipIfAlready = true });
        var row = editor.Steps[0];

        row.IsPress = true;
        var bios = (BiosStep)command.Steps[0];
        Assert.Equal(BiosStep.DefaultPressMs, bios.PressMs);
        Assert.Equal("0", bios.Release);
        Assert.False(bios.SkipIfAlready);
        Assert.False(row.CanSkipIfAlready);
        Assert.Equal("press U1 1/0 (50 ms)", row.Text);

        row.PressValue = "2";
        row.ReleaseValue = "1";
        row.HoldMs = "120";
        Assert.Equal("press U1 2/1 (120 ms)", row.Text);
        row.HoldMs = "abc";
        Assert.Equal(120, bios.PressMs);

        row.IsPress = false;
        Assert.Null(bios.PressMs);
        Assert.Null(bios.Release);
        Assert.Equal("U1 2", row.Text);
        Assert.True(changes() >= 5);
    }

    [Fact]
    public void A_press_keeps_in_and_out_different()
    {
        var (editor, command, _, _) = Create(new BiosStep { Control = "U1", Arg = "1" });
        var row = editor.Steps[0];
        row.IsPress = true;
        var bios = (BiosStep)command.Steps[0];

        row.PressValue = "0";
        Assert.Equal("1", bios.Arg);
        row.ReleaseValue = "1";
        Assert.Equal("0", bios.Release);
    }

    [Theory]
    [InlineData("TOGGLE")]
    [InlineData("{value}")]
    [InlineData("0")]
    public void Unticking_a_press_puts_the_old_argument_back(string arg)
    {
        var (editor, command, _, _) = Create(new BiosStep { Control = "U1", Arg = arg });
        var row = editor.Steps[0];
        var bios = (BiosStep)command.Steps[0];

        row.IsPress = true;
        Assert.Equal("1", bios.Arg);
        row.IsPress = false;
        Assert.Equal(arg, bios.Arg);
    }

    [Fact]
    public void Wait_offers_100_ms_then_the_last_value_entered()
    {
        CommandEditorViewModel.LastWaitMs = CommandEditorViewModel.DefaultWaitMs;
        var (editor, command, dialogs, _) = Create();

        dialogs.PromptAnswers.Enqueue("250");
        editor.AddWaitCommand.Execute(null);
        Assert.Equal("100", dialogs.LastInitial);

        // A new editor (another command) offers what was entered last.
        var (other, _, otherDialogs, _) = Create();
        otherDialogs.PromptAnswers.Enqueue(null);
        other.AddWaitCommand.Execute(null);
        Assert.Equal("250", otherDialogs.LastInitial);

        // Editing a wait step counts too; a cancelled prompt changes nothing.
        dialogs.PromptAnswers.Enqueue("75");
        editor.EditStepCommand.Execute(editor.Steps[0]);
        dialogs.PromptAnswers.Enqueue(null);
        editor.AddWaitCommand.Execute(null);
        Assert.Equal("75", dialogs.LastInitial);
        Assert.Single(command.Steps);
        CommandEditorViewModel.LastWaitMs = CommandEditorViewModel.DefaultWaitMs;
    }

    [Fact]
    public void Add_press_adds_one_step()
    {
        var (editor, command, _, _) = Create();
        editor.AddPress("MFCD_L_U1");
        var bios = Assert.IsType<BiosStep>(Assert.Single(command.Steps));
        Assert.Equal(50, bios.PressMs);
        Assert.Equal("press MFCD_L_U1 1/0 (50 ms)", editor.Steps[0].Text);
    }

    [Fact]
    public void Unknown_key_asks_again_or_gives_up()
    {
        var (editor, command, dialogs, _) = Create();
        dialogs.PromptAnswers.Enqueue("Hyper+G");
        dialogs.ConfirmAnswers.Enqueue(false);
        editor.AddKeyCommand.Execute(null);
        Assert.Empty(command.Steps);
    }

    [Fact]
    public void Steps_round_trip_through_json()
    {
        var (editor, command, _, _) = Create(new BiosStep { Control = "A", Arg = "1" });
        editor.ToggleJsonCommand.Execute(null);
        Assert.Contains("\"type\": \"bios\"", editor.StepsJson);

        editor.StepsJson = """[{"type":"wait_for","until":{"output":"APU_READY_LT","value":"1"},"timeout_ms":60000}]""";
        Assert.True(editor.ApplyJson());
        Assert.IsType<WaitForStep>(Assert.Single(command.Steps));
        Assert.Equal("wait for APU_READY_LT = 1 (60 s)", editor.Steps[0].Text);

        editor.StepsJson = "[{";
        Assert.False(editor.ApplyJson());
        Assert.NotNull(editor.JsonError);
        Assert.Single(command.Steps);
    }

    [Fact]
    public async Task Settings_validate_numbers_and_bind_push_to_talk()
    {
        var vm = new SettingsViewModel(new AppSettings(), new FakeBinder(new PttSettings { DeviceName = "Warthog Stick", Button = 4 }));
        Assert.Equal("not bound", vm.PttText);
        await vm.BindCommand.ExecuteAsync();
        Assert.Equal("Warthog Stick · button 5", vm.PttText);

        vm.InputPort = "99999";
        Assert.Null(vm.Build());
        Assert.Contains("Command port", vm.Error);

        vm.InputPort = "7778";
        vm.Microphone = " Headset ";
        vm.ModelDirectory = "";
        var built = vm.Build()!;
        Assert.Equal("Headset", built.Speech.Device);
        Assert.Null(built.Speech.ModelDirectory);
        Assert.Equal(4, built.Ptt.Button);
    }

    [Fact]
    public void Settings_search_and_pick_a_microphone()
    {
        string[] mics = ["Microphone (Yeti Stereo Microphone)", "Mic (2- Arctis Nova 7p)", "Headset Microphone (Arctis Nova 7p Chat)"];
        var vm = new SettingsViewModel(new AppSettings(), microphones: mics);
        Assert.Equal([SettingsViewModel.DefaultMicrophone, .. mics], vm.Microphones);
        Assert.Equal(SettingsViewModel.DefaultMicrophone, vm.SelectedMicrophone);

        vm.MicrophoneSearch = "nova CHAT";
        Assert.Equal(["Headset Microphone (Arctis Nova 7p Chat)"], vm.Microphones);
        vm.SelectedMicrophone = vm.Microphones[0];
        Assert.Equal("Headset Microphone (Arctis Nova 7p Chat)", vm.Build()!.Speech.Device);

        vm.MicrophoneSearch = "yeti"; // the choice is filtered out of view but stays
        vm.SelectedMicrophone = null;
        Assert.Equal("Headset Microphone (Arctis Nova 7p Chat)", vm.MicrophoneText);

        vm.MicrophoneSearch = "";
        vm.SelectedMicrophone = SettingsViewModel.DefaultMicrophone;
        Assert.Null(vm.Build()!.Speech.Device);
    }

    [Fact]
    public void Settings_keep_a_microphone_windows_did_not_list()
    {
        var settings = new AppSettings { Speech = new() { Device = "Arctis" } };
        var vm = new SettingsViewModel(settings);
        Assert.Equal([SettingsViewModel.DefaultMicrophone, "Arctis"], vm.Microphones);
        Assert.Equal("Arctis", vm.SelectedMicrophone);
    }
}
