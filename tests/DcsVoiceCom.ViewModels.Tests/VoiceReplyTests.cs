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

public class SettingsVoiceCardTests
{
    private static readonly VoiceInfo[] Voices =
    [
        new("en1", "David", "en-US"),
        new("en2", "George", "en-GB"),
        new("de1", "Hedda", "de-DE"),
    ];

    [Theory]
    [InlineData(false, "en", "No Windows voices found. Voice replies are off.")]
    [InlineData(true, "fr", "No voice for French (fr) is installed.")]
    [InlineData(true, "en", "")]
    public void The_note_says_why_no_voice_can_be_picked(bool withVoices, string language, string expected)
    {
        var vm = new SettingsViewModel(new AppSettings { Language = language }, tts: withVoices ? new FakeVoice(Voices) : new FakeVoice());
        Assert.Equal(expected, vm.TtsNote);
        Assert.Equal(expected.Length > 0, vm.HasTtsNote);
        Assert.Equal(expected.Length == 0, vm.CanPlayPreview);
    }

    [Fact]
    public void Voices_and_languages_follow_the_recognition_language_until_one_is_picked()
    {
        var vm = new SettingsViewModel(new AppSettings { Language = "en" }, tts: new FakeVoice(Voices));
        Assert.Equal(["Same as recognition (en)", "de-DE", "en-GB", "en-US"], vm.TtsLanguages.Select(l => l.Text));
        Assert.Equal(["David", "George"], vm.TtsVoices.Select(v => v.Name));
        Assert.Equal("David", vm.SelectedVoice?.Name);

        vm.Language = "de";
        Assert.Equal(["Hedda"], vm.TtsVoices.Select(v => v.Name));
        Assert.Equal("Same as recognition (de)", vm.TtsLanguages[0].Text);
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

        Assert.Equal(new TtsSettings { Enabled = false, Language = "en-GB", Voice = "en2", Rate = 1.5, Volume = 0.4 }, tts);
    }

    [Fact]
    public void Switching_the_language_away_and_back_keeps_the_picked_voice()
    {
        var vm = new SettingsViewModel(new AppSettings { Language = "en" }, tts: new FakeVoice(Voices));
        vm.SelectedVoice = vm.TtsVoices.Single(v => v.Id == "en2");

        vm.SelectedTtsLanguage = vm.TtsLanguages.Single(l => l.Tag == "de-DE");
        Assert.Equal("Hedda", vm.SelectedVoice?.Name);
        Assert.Null(vm.Build()!.Tts.Voice); // saved now: the first German voice, not George

        vm.SelectedTtsLanguage = vm.TtsLanguages[0]; // back to "same as recognition"
        Assert.Equal("George", vm.SelectedVoice?.Name);
        Assert.Equal("en2", vm.Build()!.Tts.Voice);
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
    public void Output_list_starts_with_the_default_filters_by_words_and_keeps_an_unplugged_device()
    {
        var settings = new AppSettings { Tts = new TtsSettings { Device = "Old DAC" } };
        var vm = new SettingsViewModel(settings, tts: new FakeVoice { OutputDevices = ["Speakers (Realtek)", "Headset (Arctis)"] });
        Assert.Equal([SettingsViewModel.DefaultOutput, "Speakers (Realtek)", "Headset (Arctis)", "Old DAC"], vm.TtsOutputs);
        Assert.Equal("Old DAC", vm.SelectedTtsOutput);
        Assert.Equal("Old DAC", vm.Build()!.Tts.Device);

        vm.TtsOutputSearch = "arctis head";
        Assert.Equal(["Headset (Arctis)"], vm.TtsOutputs);
        vm.SelectedTtsOutput = "Headset (Arctis)";
        Assert.Equal("Headset (Arctis)", vm.Build()!.Tts.Device);
        vm.SelectedTtsOutput = SettingsViewModel.DefaultOutput;
        Assert.Null(vm.Build()!.Tts.Device);
    }

    [Fact]
    public void Preview_plays_with_the_values_in_the_dialog_and_stops_on_close()
    {
        var voice = new FakeVoice(Voices);
        var vm = new SettingsViewModel(new AppSettings(), tts: voice);
        vm.SelectedVoice = vm.TtsVoices[1];
        vm.TtsRate = 1.8;
        vm.TtsVolume = 0.5;
        Assert.Equal(SettingsViewModel.DefaultPreviewText, vm.PreviewText);

        vm.PlayPreviewCommand.Execute(null);

        Assert.Equal((SettingsViewModel.DefaultPreviewText, new TtsOptions("en2", 1.8, 0.5)), Assert.Single(voice.Spoken));
        Assert.Equal(1, voice.Stops);
        vm.StopPreview();
        Assert.Equal(2, voice.Stops);
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
        Assert.Contains("Manage voices", SettingsViewModel.AddVoiceHelp);
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

    [Theory]
    [InlineData(false, "No Windows voices found.")] // no voices at all
    [InlineData(true, "Settings")]                  // voices, but replies off or none for the language
    public void Without_a_usable_voice_play_is_off_with_a_note_but_the_step_can_be_saved(bool withVoices, string noteSays)
    {
        var editor = new SayEditorViewModel("Gear up", withVoices ? Context(new FakeVoice(Voice), null) : null);
        Assert.False(editor.PlayCommand.CanExecute(null));
        Assert.True(editor.CanSave);
        Assert.Contains(noteSays, editor.Note);
        if (!withVoices) Assert.Contains("Manage voices", editor.Note);

        editor.Text = "  ";
        Assert.False(editor.CanSave);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Add_say_adds_the_step_on_ok_and_always_stops_the_preview(bool ok)
    {
        var voice = new FakeVoice(Voice);
        var dialogs = new FakeDialogs { OnSay = e => { e.Text = " Gear up "; return ok; } };
        var command = new Command { Name = "Gear", Phrases = ["gear up"] };
        var changed = 0;
        var vm = new CommandEditorViewModel(command, dialogs, () => changed++, say: () => Context(voice, null));

        vm.AddSayCommand.Execute(null);

        Assert.Equal(1, voice.Stops);
        if (ok)
        {
            Assert.Equal("Gear up", Assert.IsType<SayStep>(Assert.Single(command.Steps)).Text);
            Assert.True(changed > 0);
        }
        else
        {
            Assert.Empty(command.Steps);
        }
    }

    [Fact]
    public void Edit_opens_the_same_window_updates_the_text_and_stops_the_preview()
    {
        var voice = new FakeVoice(Voice);
        string? seen = null;
        var dialogs = new FakeDialogs { OnSay = e => { seen = e.Text; e.Text = "Gear down"; return true; } };
        var command = new Command { Name = "Gear", Phrases = ["gear"], Steps = [new SayStep { Text = "Gear up" }] };
        var vm = new CommandEditorViewModel(command, dialogs, () => { }, say: () => Context(voice, null));

        vm.EditStepCommand.Execute(vm.Steps[0]);

        Assert.Equal("Gear up", seen);
        Assert.Equal("Gear down", ((SayStep)command.Steps[0]).Text);
        Assert.Equal(1, voice.Stops);
    }
}
