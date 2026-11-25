using DcsVoiceCom.Config;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels.Tests;

public class SpeechEngineSettingsTests
{
    [Fact]
    public void Whistle_is_the_default_and_the_boost_shows_only_for_zipformer()
    {
        var vm = new SettingsViewModel(new AppSettings());
        Assert.Equal((SpeechEngines.Whistle, false), (vm.SpeechEngine, vm.IsZipformer));
        vm.SpeechEngine = SpeechEngines.Zipformer;
        Assert.True(vm.IsZipformer);
        Assert.Contains("150 MB", vm.EngineNote);
    }

    [Fact]
    public void The_engine_list_shows_labels() // the app's ComboBox template shows the item's text, not DisplayMemberPath
    {
        Assert.Equal(["Whistle (default)", "Zipformer (stronger on call signs)"], SettingsViewModel.Engines.Select(e => e.ToString()));
    }

    [Fact]
    public void Saves_the_engine_and_boost()
    {
        var vm = new SettingsViewModel(new AppSettings()) { SpeechEngine = SpeechEngines.Zipformer, HotwordBoost = "2.5" };
        var saved = vm.Build()!;
        Assert.Equal((SpeechEngines.Zipformer, 2.5f), (saved.Speech.Engine, saved.Speech.HotwordBoost));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("9")]
    public void Rejects_a_boost_out_of_range(string boost)
    {
        var vm = new SettingsViewModel(new AppSettings()) { HotwordBoost = boost };
        Assert.Null(vm.Build());
        Assert.Contains("boost", vm.Error);
    }

    [Fact]
    public void An_unknown_engine_in_the_file_shows_as_whistle()
    {
        var settings = new AppSettings { Speech = new SpeechSettings { Engine = "vosk" } };
        Assert.Equal(SpeechEngines.Whistle, new SettingsViewModel(settings).SpeechEngine);
    }
}
