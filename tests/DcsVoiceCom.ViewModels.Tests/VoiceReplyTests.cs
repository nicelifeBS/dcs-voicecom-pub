using DcsVoiceCom.Config;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels.Tests;

internal sealed class FakeVoice(params VoiceInfo[] voices) : ITextToSpeech
{
    public IReadOnlyList<VoiceInfo> Voices { get; } = voices;
    public IReadOnlyList<string> OutputDevices { get; set; } = [];
    public List<(string Text, TtsOptions Options)> Spoken { get; } = [];
    public int Stops;

    public Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default)
    {
        Spoken.Add((text, options));
        return Task.CompletedTask;
    }

    public void Stop() => Stops++;
}

/// <summary>Dialogs that only know the Say window.</summary>
internal sealed class SayDialogs : IDialogs
{
    public Func<SayEditorViewModel, bool>? OnSay { get; set; }
    public bool Confirm(string title, string message) => false;
    public bool? ConfirmOrCancel(string title, string message) => null;
    public string? Prompt(string title, string label, string initial = "", IReadOnlyList<string>? suggestions = null) => null;
    public bool ShowSeed(SeedViewModel seed) => false;
    public bool ShowEnterValue(EnterValueEditorViewModel editor) => false;
    public bool ShowSay(SayEditorViewModel editor) => OnSay?.Invoke(editor) ?? false;
}

public class SettingsVoiceCardTests
{
    private static readonly VoiceInfo[] Voices =
    [
        new("en1", "David", "en-US"),
        new("en2", "George", "en-GB"),
        new("de1", "Hedda", "de-DE"),
    ];

    [Fact]
    public void Without_voices_the_note_says_so()
    {
        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice());
        Assert.Equal("No Windows voices found. Voice replies are off.", vm.TtsNote);
        Assert.True(vm.HasTtsNote);
        Assert.False(vm.CanPlayPreview);
        Assert.Contains("Manage voices", SettingsViewModel.AddVoiceHelp);
    }

    [Fact]
    public void Without_a_voice_for_the_language_the_note_names_it()
    {
        var vm = new SettingsViewModel(new AppSettings { Language = "fr" }, tts: new FakeVoice(Voices));
        Assert.Equal("No voice for French (fr) is installed.", vm.TtsNote);
        Assert.Empty(vm.TtsVoices);
    }

    [Fact]
    public void Voices_follow_the_recognition_language_until_one_is_picked()
    {
        var vm = new SettingsViewModel(new AppSettings { Language = "en" }, tts: new FakeVoice(Voices));
        Assert.Equal(["David", "George"], vm.TtsVoices.Select(v => v.Name));
        Assert.Equal("David", vm.SelectedVoice?.Name);
        Assert.Equal("", vm.TtsNote);

        vm.Language = "de";
        Assert.Equal(["Hedda"], vm.TtsVoices.Select(v => v.Name));
        Assert.Equal("Same as recognition (de)", vm.TtsLanguages[0].Text);
    }

    [Fact]
    public void Language_list_has_same_as_recognition_then_each_installed_language()
    {
        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice(Voices));
        Assert.Equal(["Same as recognition (en)", "de-DE", "en-GB", "en-US"], vm.TtsLanguages.Select(l => l.Text));
    }

    [Fact]
    public void Picking_a_language_and_voice_is_saved()
    {
        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice(Voices));
        vm.SelectedTtsLanguage = vm.TtsLanguages.Single(l => l.Tag == "en-GB");
        Assert.Equal(["George"], vm.TtsVoices.Select(v => v.Name));
        vm.SelectedVoice = vm.TtsVoices[0];
        vm.TtsRate = 1.5;
        vm.TtsVolume = 0.4;
        vm.TtsEnabled = false;

        var tts = vm.Build()!.Tts;

        Assert.False(tts.Enabled);
        Assert.Equal("en-GB", tts.Language);
        Assert.Equal("en2", tts.Voice);
        Assert.Equal(1.5, tts.Rate);
        Assert.Equal(0.4, tts.Volume);
    }

    [Fact]
    public void Untouched_settings_keep_the_defaults()
    {
        var tts = new SettingsViewModel(new AppSettings(), tts: new FakeVoice(Voices)).Build()!.Tts;
        Assert.Equal(new TtsSettings(), tts);
    }

    [Fact]
    public void A_saved_voice_that_is_gone_falls_back_to_the_first_for_the_language()
    {
        var settings = new AppSettings { Tts = new TtsSettings { Voice = "uninstalled" } };
        var vm = new SettingsViewModel(settings, tts: new FakeVoice(Voices));
        Assert.Equal("David", vm.SelectedVoice?.Name);
        Assert.Null(vm.Build()!.Tts.Voice);
    }

    [Fact]
    public void Output_list_starts_with_the_default_and_filters_by_words()
    {
        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice { OutputDevices = ["Speakers (Realtek)", "Headset (Arctis)"] });
        Assert.Equal([SettingsViewModel.DefaultOutput, "Speakers (Realtek)", "Headset (Arctis)"], vm.TtsOutputs);
        Assert.Equal(SettingsViewModel.DefaultOutput, vm.SelectedTtsOutput);

        vm.TtsOutputSearch = "arctis head";
        Assert.Equal(["Headset (Arctis)"], vm.TtsOutputs);
        vm.SelectedTtsOutput = "Headset (Arctis)";
        Assert.Equal("Headset (Arctis)", vm.Build()!.Tts.Device);
        vm.SelectedTtsOutput = SettingsViewModel.DefaultOutput;
        Assert.Null(vm.Build()!.Tts.Device);
    }

    [Fact]
    public void An_unplugged_output_stays_listed_and_saved()
    {
        var settings = new AppSettings { Tts = new TtsSettings { Device = "Old DAC" } };
        var vm = new SettingsViewModel(settings, tts: new FakeVoice(Voices));
        Assert.Contains("Old DAC", vm.TtsOutputs);
        Assert.Equal("Old DAC", vm.Build()!.Tts.Device);
    }

    [Fact]
    public void Preview_plays_with_the_values_in_the_dialog_before_saving()
    {
        var voice = new FakeVoice(Voices);
        var vm = new SettingsViewModel(new AppSettings(), tts: voice);
        vm.SelectedVoice = vm.TtsVoices[1];
        vm.TtsRate = 1.8;
        vm.TtsVolume = 0.5;
        Assert.Equal(SettingsViewModel.DefaultPreviewText, vm.PreviewText);

        vm.PlayPreviewCommand.Execute(null);

        var (text, options) = Assert.Single(voice.Spoken);
        Assert.Equal(SettingsViewModel.DefaultPreviewText, text);
        Assert.Equal(new TtsOptions("en2", 1.8, 0.5), options);
        Assert.Equal(1, voice.Stops);
    }

    [Fact]
    public void Preview_does_nothing_without_a_voice_or_text()
    {
        var none = new FakeVoice();
        new SettingsViewModel(new AppSettings(), tts: none).PlayPreviewCommand.Execute(null);
        Assert.Empty(none.Spoken);

        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice(Voices)) { PreviewText = "  " };
        Assert.False(vm.PlayPreviewCommand.CanExecute(null));
    }

    [Fact]
    public void The_speech_settings_button_opens_the_windows_page()
    {
        var opened = new List<string>();
        var vm = new SettingsViewModel(new AppSettings(), tts: new FakeVoice(), openUri: opened.Add);
        vm.OpenSpeechSettingsCommand.Execute(null);
        Assert.Equal(["ms-settings:speech"], opened);
    }
}

public class SayEditorTests
{
    private static readonly VoiceInfo Voice = new("en1", "David", "en-US");

    private static SayContext Context(FakeVoice voice, TtsOptions? options) => new(voice, () => options);

    [Fact]
    public void Preview_turns_placeholders_into_slot_names()
    {
        var editor = new SayEditorViewModel("Radio set to {freq}, channel {channel+1}");
        Assert.Equal("Radio set to freq, channel channel", editor.PreviewSpoken);
    }

    [Fact]
    public void Play_speaks_the_preview_with_the_saved_options()
    {
        var voice = new FakeVoice(Voice);
        var options = new TtsOptions("en1", 1.2, 0.8);
        var editor = new SayEditorViewModel("Radio set to {freq}", Context(voice, options));

        editor.PlayCommand.Execute(null);

        Assert.Equal(("Radio set to freq", options), Assert.Single(voice.Spoken));
        Assert.Equal("", editor.Note);
    }

    [Fact]
    public void Without_a_voice_play_is_off_with_a_note_but_the_step_can_be_saved()
    {
        var editor = new SayEditorViewModel("Gear up");
        Assert.False(editor.PlayCommand.CanExecute(null));
        Assert.True(editor.CanSave);
        Assert.StartsWith("No Windows voices found.", editor.Note);
        Assert.Contains("Manage voices", editor.Note);
    }

    [Fact]
    public void With_voices_but_replies_off_the_note_points_to_settings()
    {
        var editor = new SayEditorViewModel("Gear up", Context(new FakeVoice(Voice), null));
        Assert.False(editor.CanPlay);
        Assert.Contains("Settings", editor.Note);
    }

    [Fact]
    public void Empty_text_cannot_be_saved()
    {
        var editor = new SayEditorViewModel("  ");
        Assert.False(editor.CanSave);
        editor.Text = "Hi";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void Add_say_adds_a_step_with_the_entered_text()
    {
        var dialogs = new SayDialogs { OnSay = e => { e.Text = " Gear up "; return true; } };
        var command = new Command { Name = "Gear", Phrases = ["gear up"] };
        var changed = 0;
        var vm = new CommandEditorViewModel(command, dialogs, () => changed++);

        vm.AddSayCommand.Execute(null);

        Assert.Equal("Gear up", Assert.IsType<SayStep>(Assert.Single(command.Steps)).Text);
        Assert.True(changed > 0);
    }

    [Fact]
    public void Cancelling_add_say_adds_nothing()
    {
        var command = new Command { Name = "Gear", Phrases = ["gear up"] };
        var vm = new CommandEditorViewModel(command, new SayDialogs { OnSay = _ => false }, () => { });
        vm.AddSayCommand.Execute(null);
        Assert.Empty(command.Steps);
    }

    [Fact]
    public void Edit_opens_the_same_window_and_updates_the_text()
    {
        string? seen = null;
        var dialogs = new SayDialogs { OnSay = e => { seen = e.Text; e.Text = "Gear down"; return true; } };
        var command = new Command { Name = "Gear", Phrases = ["gear"], Steps = [new SayStep { Text = "Gear up" }] };
        var vm = new CommandEditorViewModel(command, dialogs, () => { });

        vm.EditStepCommand.Execute(vm.Steps[0]);

        Assert.Equal("Gear up", seen);
        Assert.Equal("Gear down", ((SayStep)command.Steps[0]).Text);
    }

    [Fact]
    public void Closing_the_window_stops_a_running_preview()
    {
        var voice = new FakeVoice(Voice);
        var dialogs = new SayDialogs { OnSay = _ => false };
        var vm = new CommandEditorViewModel(new Command { Name = "X", Phrases = ["x"] }, dialogs, () => { }, say: () => Context(voice, null));
        vm.AddSayCommand.Execute(null);
        Assert.Equal(1, voice.Stops);
    }
}
