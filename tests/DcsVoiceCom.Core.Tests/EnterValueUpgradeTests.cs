using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class EnterValueUpgradeTests
{
    /// <summary>The F-14 UHF step as profiles wrote it before the display layout.</summary>
    private static EnterValueStep OldUhf() => new()
    {
        Value = "{freq}",
        Method = EnterMethod.Wheels,
        Min = 225,
        Max = 399.975m,
        DelayMs = 0,
        Wheels =
        [
            new() { Control = "PLT_UHF1_110_DIAL", Place = 10, Width = 2, Read = "PLT_UHF_HIGH_FREQ" },
            new() { Control = "PLT_UHF1_1_DIAL", Place = 1, Read = "PLT_UHF_HIGH_FREQ" },
            new() { Control = "PLT_UHF1_01_DIAL", Place = 0.1m, Read = "PLT_UHF_DIAL3_FREQ", ReadScale = 0.1m, Flip = true },
            new() { Control = "PLT_UHF1_025_DIAL", Place = 0.001m, Width = 2, Read = "PLT_UHF_DIAL4_FREQ", ReadScale = 0.001m },
        ],
        Verify = new() { Output = "PLT_UHF_DISP", TimeoutMs = 300 },
    };

    private static ControlCatalog Catalog(string display, int length) => new("Test", [],
    [
        new ControlDefinition { Identifier = display, Outputs = [new OutputDefinition { Type = "string", MaxLength = length }] },
        new ControlDefinition { Identifier = "PLT_UHF_HIGH_FREQ", Outputs = [new OutputDefinition { Type = "integer" }] },
    ]);

    private static string Layout(EnterValueStep step) =>
        string.Join(" ", step.Fields!.Select(f => $"{f.At}+{f.Len}:{(f.Kind == FieldKind.Wheel ? f.Range : f.Kind.ToString())}"));

    [Fact]
    public void Lines_up_old_wheels_on_the_live_display()
    {
        var step = EnterValueUpgrade.TryUpgrade(OldUhf(), Catalog("PLT_UHF_DISP", 7), "251.000", out var reason)!;

        Assert.Null(reason);
        Assert.Equal("PLT_UHF_DISP", step.Display);
        Assert.Equal("251.000", step.Sample);
        Assert.Equal("0+2:22-39 2+1:0-9 3+1:ReadOnly 4+1:0-9 5+2:0-99", Layout(step));
        Assert.Equal(300, step.TimeoutMs);
        Assert.True(step.Fields![3].Flip);
        Assert.Null(step.Wheels);
        Assert.Null(step.Verify);
        Assert.Empty(DisplayLayout.Validate(step));
    }

    [Fact]
    public void Without_dcs_guesses_the_layout_from_max_and_says_so()
    {
        var step = EnterValueUpgrade.TryUpgrade(OldUhf(), Catalog("PLT_UHF_DISP", 7), null, out var reason)!;

        Assert.Equal("399.975", step.Sample);
        Assert.Equal("0+2:22-39 2+1:0-9 3+1:ReadOnly 4+1:0-9 5+2:0-99", Layout(step));
        Assert.Contains("guessed", reason);
    }

    [Fact]
    public void Pads_the_guess_to_the_display_length()
    {
        var preset = new EnterValueStep
        {
            Value = "{channel}", Min = 1, Max = 20,
            Wheels = [new() { Control = "PLT_UHF1_PRESETS", Place = 1, Width = 2, Read = "PLT_UHF_DISP", Mode = WheelMode.Step }],
        };

        var step = EnterValueUpgrade.TryUpgrade(preset, Catalog("PLT_UHF_DISP", 7), null, out _)!;

        Assert.Equal("PLT_UHF_DISP", step.Display);
        Assert.Equal("     20", step.Sample);
        Assert.Equal("5+2:1-20", Layout(step));
        Assert.Equal(WheelMode.Step, step.Fields![0].Mode);
    }

    [Fact]
    public void Leaves_a_step_that_already_has_fields()
    {
        var step = DisplaySteps.Uhf();
        Assert.Same(step, EnterValueUpgrade.TryUpgrade(step, null, null, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Keypad_steps_need_the_field_picked_live()
    {
        var keypad = new EnterValueStep { Method = EnterMethod.Keypad, Keys = new() { ["A"] = "CDU_A" }, Verify = new() { Output = "CDU_LINE9" } };
        Assert.Null(EnterValueUpgrade.TryUpgrade(keypad, null, "[A ]", out var reason));
        Assert.Contains("keypad field", reason);
    }

    [Fact]
    public void Needs_a_display()
    {
        var step = OldUhf();
        step.Verify = null;
        Assert.Null(EnterValueUpgrade.TryUpgrade(step, Catalog("PLT_UHF_DISP", 7), "251.000", out var reason));
        Assert.Contains("no display", reason);
    }

    [Fact]
    public void A_wheel_that_falls_off_the_display_is_not_converted()
    {
        Assert.Null(EnterValueUpgrade.PlaceWheels("51.000", [new Wheel { Control = "W", Place = 10, Width = 2 }], out var reason));
        Assert.Contains("does not fit", reason);
    }

    [Fact]
    public async Task The_converted_step_runs()
    {
        var step = EnterValueUpgrade.TryUpgrade(OldUhf(), Catalog("PLT_UHF_DISP", 7), "251.000", out _)!;
        step.Fields![3].Flip = false;
        var cockpit = new FakeCockpit();
        new FakeUhf().Attach(cockpit);
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(2) })
        {
            Profile = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "UHF", Phrases = ["uhf {freq}"], Steps = [step] }] }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "uhf {freq}", Score = 100, Slots = "three zero five decimal seven five", Raw = "uhf" });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("305.750", cockpit.Strings["PLT_UHF_DISP"]);
    }
}

public class F14UhfProfileTests
{
    [Fact]
    public async Task The_sample_profile_enters_a_frequency_from_the_display()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles", "F-14.json");
        var compiled = CompiledProfile.Compile(ProfileSerializer.Deserialize(File.ReadAllText(path)));

        var cockpit = new FakeCockpit();
        var uhf = new FakeUhf();
        uhf.Attach(cockpit);
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit, new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(2) })
        {
            Profile = compiled,
        };
        cockpit.Integers["PLT_UHF1_FREQ_MODE"] = 1;

        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = "uhf {freq}", Score = 100, Slots = "two six three decimal one seven five", Raw = "uhf" });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("263.175", cockpit.Strings["PLT_UHF_DISP"]);
    }
}
