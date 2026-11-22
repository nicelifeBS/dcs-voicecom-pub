using DcsVoiceCom.Engine;
using DcsVoiceCom.Input;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class DcsWindowTests
{
    [Theory]
    [InlineData("DCS", true)]
    [InlineData("dcs", true)]
    [InlineData(" DCS ", true)]
    [InlineData("explorer", false)]
    [InlineData("DCS_server", false)]
    [InlineData("DcsVoiceCom", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_DCS_process_counts(string? name, bool expected) => Assert.Equal(expected, DcsWindow.IsDcsProcess(name));

    [Fact]
    public void Names_the_active_window_when_refusing()
    {
        var ex = Assert.Throws<StepException>(() => DcsWindow.EnsureActive("notepad", KeyChord.Parse("LWin+R")));
        Assert.Contains("LWin+R", ex.Message);
        Assert.Contains("notepad", ex.Message);
    }

    [Fact]
    public async Task Key_step_fails_and_stops_the_command_when_DCS_is_not_active()
    {
        var command = new Command
        {
            Name = "Run box",
            Phrases = ["run box"],
            Steps = [new KeyStep { Keys = "LWin+R" }, new BiosStep { Control = "GEAR_LEVER", Arg = "0" }],
        };
        var cockpit = new FakeCockpit { ActiveProcess = "explorer" };
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions())
        {
            Profile = CompiledProfile.Compile(new Profile { Aircraft = "TestJet", Commands = [command] }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "run box", Score = 100, Raw = "run box" });

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Contains("DCS is not the active window", outcome.Message);
        Assert.Empty(cockpit.Keys);
        Assert.Empty(cockpit.Sent);
        Assert.Equal(Cue.Refused, cockpit.Cues[^1]);
    }
}
