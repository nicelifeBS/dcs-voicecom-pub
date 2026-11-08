using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;
using DcsVoiceCom.Runtime;

namespace DcsVoiceCom.Tests;

/// <summary>
/// The A-10C II FM radio (issue #5): its first dial goes 0-15 on one cell, so "90.000" becomes "100.000" and the
/// decimal point and every cell after it move one place to the right.
/// </summary>
public class ShiftingDialTests
{
    private static EnterValueStep FmStep() => new()
    {
        Value = "{freq}",
        Method = EnterMethod.Wheels,
        Display = "VHF_FM_FREQUENCY_S",
        Sample = "90.000 ",
        DelayMs = 0,
        TimeoutMs = 200,
        Fields =
        [
            new() { At = 0, Len = 1, Kind = FieldKind.Wheel, Control = "VHFFM_FREQ1", Range = "0-15" },
            new() { At = 1, Len = 1, Kind = FieldKind.Wheel, Control = "VHFFM_FREQ2", Range = "0-9" },
            new() { At = 2, Len = 1, Kind = FieldKind.ReadOnly },
            new() { At = 3, Len = 1, Kind = FieldKind.Wheel, Control = "VHFFM_FREQ3", Range = "0-9" },
            new() { At = 4, Len = 2, Kind = FieldKind.Wheel, Control = "VHFFM_FREQ4", Range = "00,25,50,75" },
        ],
    };

    private static List<(int At, int Len, string Text)> Targets(string value)
    {
        var targets = DisplayLayout.Targets(FmStep(), value, out var problem);
        Assert.Null(problem);
        return targets.Select(t => (t.Field.At, t.Field.Len, t.Text)).ToList();
    }

    [Fact]
    public void A_two_digit_value_takes_a_second_cell_and_pushes_the_rest_along()
    {
        Assert.Equal([(0, 1, "9"), (1, 1, "0"), (3, 1, "0"), (4, 2, "25")], Targets("90.025"));
        Assert.Equal([(0, 2, "10"), (2, 1, "0"), (4, 1, "0"), (5, 2, "00")], Targets("100.000"));
        Assert.Equal([(0, 2, "15"), (2, 1, "9"), (4, 1, "5"), (5, 2, "75")], Targets("159.575"));
    }

    [Fact]
    public void Too_many_digits_and_values_outside_the_range_are_still_refused()
    {
        DisplayLayout.Targets(FmStep(), "1000.000", out var tooLong);
        Assert.Contains("too many digits", tooLong);
        DisplayLayout.Targets(FmStep(), "160.000", out var outside);
        Assert.Contains("outside 0-15", outside);
    }

    [Fact]
    public void Shows_reads_each_field_where_the_live_display_has_it()
    {
        var wide = DisplayLayout.Targets(FmStep(), "100.000", out _);
        Assert.True(DisplayLayout.ShowsAll("100.000", wide));
        Assert.False(DisplayLayout.ShowsAll("90.000 ", wide));
        Assert.False(DisplayLayout.ShowsAll("101.000", wide));

        var narrow = DisplayLayout.Targets(FmStep(), "90.000", out _);
        Assert.True(DisplayLayout.ShowsAll("90.000 ", narrow));
        Assert.False(DisplayLayout.ShowsAll("100.000", narrow));
    }

    [Fact]
    public void Fixed_layouts_do_not_change()
    {
        var uhf = DisplaySteps.Uhf();
        Assert.Equal(0, DisplayLayout.GrowCap(uhf.Fields![0]));
        Assert.Null(DisplayLayout.Grower(uhf.Fields));
        Assert.Equal(1, DisplayLayout.GrowCap(FmStep().Fields![0]));
    }

    /// <summary>The radio: dial 1 0-15, dial 2 0-9, 0.1 and 25 kHz dials; the display text moves with dial 1.</summary>
    private sealed class FmRadio
    {
        public int Tens = 9, One, Tenth, Quarter;
        public string Text => $"{Tens}{One}.{Tenth}{Quarter * 25:00}".PadRight(7);

        public void Attach(FakeCockpit cockpit)
        {
            cockpit.Strings["VHF_FM_FREQUENCY_S"] = Text;
            cockpit.OnSend = (c, id, arg) =>
            {
                if (arg is not ("2" or "0")) return;
                var dir = arg == "2" ? 1 : -1;
                switch (id)
                {
                    case "VHFFM_FREQ1": Tens = (Tens + dir + 16) % 16; break;
                    case "VHFFM_FREQ2": One = (One + dir + 10) % 10; break;
                    case "VHFFM_FREQ3": Tenth = (Tenth + dir + 10) % 10; break;
                    case "VHFFM_FREQ4": Quarter = (Quarter + dir + 4) % 4; break;
                }
                c.Strings["VHF_FM_FREQUENCY_S"] = Text;
            };
        }
    }

    private static (CommandEngine Engine, FakeCockpit Cockpit) Create()
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(2) })
        {
            Profile = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "FM", Phrases = ["fm {freq}"], Steps = [FmStep()] }] }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard(string slots) => new() { Phrase = "fm {freq}", Score = 100, Slots = slots, Raw = "fm" };

    [Theory]
    [InlineData("one one two decimal five", "112.500", 11, 2)]
    [InlineData("one zero zero decimal zero", "100.000", 10, 0)]
    [InlineData("nine five decimal two five", "95.250", 9, 5)]
    public async Task Enters_a_frequency_across_the_nine_to_ten_jump(string spoken, string shown, int tens, int one)
    {
        var (engine, cockpit) = Create();
        var radio = new FmRadio();
        radio.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard(spoken));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(shown.PadRight(7), cockpit.Strings["VHF_FM_FREQUENCY_S"]);
        Assert.Equal((tens, one), (radio.Tens, radio.One));
    }

    [Fact]
    public async Task Comes_back_down_across_the_jump()
    {
        var (engine, cockpit) = Create();
        var radio = new FmRadio { Tens = 12, One = 5, Tenth = 3, Quarter = 1 };
        radio.Attach(cockpit);

        var outcome = await engine.HandleAsync(Heard("eight seven decimal zero"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("87.000 ", cockpit.Strings["VHF_FM_FREQUENCY_S"]);
    }
}
