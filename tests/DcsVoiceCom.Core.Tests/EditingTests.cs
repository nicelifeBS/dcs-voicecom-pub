using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class EditingTests
{
    [Fact]
    public void Describes_each_step_type()
    {
        Assert.Equal("GEAR_LEVER 0", StepText.Describe(new BiosStep { Control = "GEAR_LEVER", Arg = "0" }));
        Assert.Equal("MASTER_ARM 1", StepText.Describe(new BiosStep { Control = "MASTER_ARM", Arg = "1", SkipIfAlready = true }));
        Assert.Equal("key RCtrl+G", StepText.Describe(new KeyStep { Keys = "RCtrl+G" }));
        Assert.Equal("wait 500 ms", StepText.Describe(new WaitStep { Ms = 500 }));
        Assert.Equal("wait for APU_READY_LT = 1 (60 s)", StepText.Describe(new WaitForStep { Until = new Condition { Output = "APU_READY_LT", Value = "1" }, TimeoutMs = 60_000 }));
        Assert.Equal("turn HDG_KNOB until HDG_KNOB = {deg}", StepText.Describe(new BiosUntilStep { Control = "HDG_KNOB", Target = "{deg}" }));
        Assert.Equal("if MASTER_ARM = 0: 1 step, else 2 steps", StepText.Describe(new IfStep
        {
            Condition = new Condition { Output = "MASTER_ARM", Value = "0" },
            Then = [new WaitStep()],
            Else = [new WaitStep(), new WaitStep()],
        }));
        Assert.Equal("cue refused", StepText.Describe(new CueStep { Cue = Cue.Refused }));
    }

    [Fact]
    public void Summarizes_long_sequences()
    {
        Assert.Equal("no steps", StepText.Summarize([]));
        Step[] steps = [new BiosStep { Control = "A", Arg = "1" }, new BiosStep { Control = "B", Arg = "0" }, new WaitStep { Ms = 10 }, new WaitStep { Ms = 20 }];
        Assert.Equal("A 1, B 0 +2 more", StepText.Summarize(steps));
    }

    [Fact]
    public void Search_matches_every_word_and_ranks_identifier_hits_first()
    {
        var catalog = TestCatalog.Load();
        Assert.Equal(["GEAR_LEVER"], ControlSearch.Search(catalog, "gear lev").Select(c => c.Identifier));
        Assert.Equal(["MASTER_ARM"], ControlSearch.Search(catalog, "master arm switch").Select(c => c.Identifier));
        Assert.Equal("HDG_KNOB", ControlSearch.Search(catalog, "heading").Single().Identifier);
        Assert.Empty(ControlSearch.Search(catalog, "gear banana"));
        Assert.Equal(5, ControlSearch.Search(catalog, "").Count);
        Assert.Equal(["APU_READY_LT", "MASTER_ARM"], ControlSearch.Search(catalog, null, "Weapons").Select(c => c.Identifier));
        Assert.Equal(["Gear", "Nav", "UFC", "Weapons"], ControlSearch.Categories(catalog));
    }

    [Fact]
    public void Suggests_arguments_from_the_inputs()
    {
        var catalog = TestCatalog.Load();
        Assert.Equal(["DEC", "INC", "0", "1", "TOGGLE", "1"], ControlArguments.Suggest(catalog.Find("GEAR_LEVER")!).Select(c => c.Arg));
        Assert.Equal(["DEC", "INC", "0 UP", "1 DN", "TOGGLE", "BUTTON"], ControlArguments.Suggest(catalog.Find("GEAR_LEVER")!).Select(c => c.Label));
        Assert.True(ControlArguments.Suggest(catalog.Find("GEAR_LEVER")!)[^1].Press);
        Assert.Equal(["0", "1", "2"], ControlArguments.Suggest(catalog.Find("MASTER_ARM")!).Select(c => c.Arg));
        Assert.Equal(["-3200", "+3200"], ControlArguments.Suggest(catalog.Find("HDG_KNOB")!).Select(c => c.Arg));
        Assert.Empty(ControlArguments.Suggest(catalog.Find("APU_READY_LT")!));
    }

    [Fact]
    public void Describes_a_button_press()
    {
        Assert.Equal("press MFCD_L_U1 1/0 (50 ms)", StepText.Describe(ControlArguments.Press("MFCD_L_U1")));
        Assert.Equal("press SW 2/1 (120 ms)", StepText.Describe(new BiosStep { Control = "SW", Arg = "2", PressMs = 120, Release = "1" }));
    }

    [Fact]
    public void Reads_live_values_with_position_names()
    {
        var catalog = TestCatalog.Load();
        var buffer = new StateBuffer();
        buffer.Write(4608, [0b0101, 0]); // gear 1, master arm 2
        buffer.Write(4620, "AB12  "u8);
        Assert.Equal("1 DN", LiveValue.Read(buffer, catalog.Find("GEAR_LEVER")!));
        Assert.Equal("2", LiveValue.Read(buffer, catalog.Find("MASTER_ARM")!));
        Assert.Equal("AB12", LiveValue.Read(buffer, catalog.Find("UFC_DISPLAY")!));
    }
}
