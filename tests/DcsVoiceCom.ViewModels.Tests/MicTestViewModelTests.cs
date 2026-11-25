using DcsVoiceCom.Config;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels.Tests;

public class MicTestViewModelTests
{
    private static MicCheckResult Result(MicGrade grade) => new()
    {
        Expected = MicTestViewModel.DefaultPhrase,
        Heard = "jester set radar range to 200",
        Score = 97,
        Threshold = 85,
        Grade = grade,
        PeakDb = -6,
        RmsDb = -22,
        Seconds = 2.1,
        Hints = grade == MicGrade.Good ? [] : ["Very quiet."],
        Audio = new float[16000],
    };

    [Fact]
    public async Task Test_uses_the_picked_microphone_shows_the_grade_and_plays_back()
    {
        var runtime = new FakeRuntime();
        var test = runtime.NextMicTest!;
        var player = new FakePlayer();
        var settings = new SettingsViewModel(new AppSettings { Language = "de" }, microphones: ["Headset", "Desk mic"], runtime: runtime, player: player);
        settings.SelectedMicrophone = "Desk mic";

        await settings.MicTest.ToggleCommand.ExecuteAsync();
        Assert.True(settings.MicTest.IsTesting);
        Assert.Equal("Stop test", settings.MicTest.ToggleText);
        Assert.Equal(("Desk mic", "de"), runtime.MicTests.Single());
        Assert.Equal(MicTestViewModel.DefaultPhrase, test.Phrase);

        test.Say(Result(MicGrade.Good));
        Assert.Equal("GOOD", settings.MicTest.GradeText);
        Assert.Equal("Match 97 (needs 85)", settings.MicTest.ScoreText);
        Assert.Equal("jester set radar range to 200", settings.MicTest.HeardText);
        Assert.Equal([16000], player.Played);

        settings.MicTest.ReplayCommand.Execute(null);
        Assert.Equal(2, player.Played.Count);

        settings.MicTest.Phrase = "gear up";
        Assert.Equal("gear up", test.Phrase);

        settings.Dispose(); // the dialog closed
        Assert.True(test.Disposed);
        Assert.False(settings.MicTest.IsTesting);
    }

    [Fact]
    public async Task Changing_the_microphone_restarts_the_test_on_it()
    {
        var runtime = new FakeRuntime();
        var first = runtime.NextMicTest!;
        var settings = new SettingsViewModel(new AppSettings(), microphones: ["Headset", "Desk mic"], runtime: runtime);
        await settings.MicTest.ToggleCommand.ExecuteAsync();

        settings.SelectedMicrophone = "Desk mic";
        await Task.Yield();

        Assert.True(first.Disposed);
        Assert.Equal("Desk mic", runtime.MicTests.Last().Device);
        Assert.True(settings.MicTest.IsTesting);
    }

    [Fact]
    public async Task A_test_that_cannot_start_shows_why()
    {
        var runtime = new FakeRuntime { NextMicTest = null };
        var settings = new SettingsViewModel(new AppSettings(), runtime: runtime);

        await settings.MicTest.ToggleCommand.ExecuteAsync();

        Assert.False(settings.MicTest.IsTesting);
        Assert.Equal("No push-to-talk button is bound.", settings.MicTest.Status);
    }

    [Fact]
    public async Task Events_from_a_stopped_test_are_ignored()
    {
        var runtime = new FakeRuntime();
        var test = runtime.NextMicTest!;
        var settings = new SettingsViewModel(new AppSettings(), runtime: runtime);
        await settings.MicTest.ToggleCommand.ExecuteAsync();
        await settings.MicTest.ToggleCommand.ExecuteAsync(); // stop

        test.Say(Result(MicGrade.Poor));

        Assert.False(settings.MicTest.HasResult);
        Assert.Equal("Test stopped.", settings.MicTest.Status);
    }

    [Fact]
    public void Without_a_runtime_the_button_is_off()
    {
        var settings = new SettingsViewModel(new AppSettings());

        Assert.False(settings.MicTest.ToggleCommand.CanExecute(null));
    }
}
