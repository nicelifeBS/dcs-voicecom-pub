using DcsVoiceCom.Definitions;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Seeding;

namespace DcsVoiceCom.Tests;

public sealed class SeedingTests
{
    /// <summary>Controls shaped like real DCS-BIOS definitions (F-14, Hornet, A-10C).</summary>
    private const string ModuleJson = """
    {
      "Gear": {
        "PLT_HOOK_LEVER": { "category": "Gear", "control_type": "selector", "description": "PILOT Hook Extension Handle",
          "positions": ["UP", "DN"],
          "inputs": [ { "interface": "fixed_step" }, { "interface": "set_state", "max_value": 1 }, { "interface": "action", "argument": "TOGGLE" } ] }
      },
      "Cockpit Mechanics": {
        "PLT_CANOPY_JETT": { "category": "Cockpit Mechanics", "control_type": "selector", "description": "PILOT Canopy Jettison",
          "inputs": [ { "interface": "set_state", "max_value": 1 } ] },
        "RIO_CANOPY_JETT": { "category": "Cockpit Mechanics", "control_type": "selector", "description": "RIO Canopy Jettison",
          "inputs": [ { "interface": "set_state", "max_value": 1 } ] }
      },
      "Map Gain/Spin Recovery Panel": {
        "SPIN_RECOVERY_SW": { "category": "Map Gain/Spin Recovery Panel", "control_type": "selector", "description": "Spin Recovery Switch, RCVY/NORM",
          "inputs": [ { "interface": "set_state", "max_value": 1 } ] }
      },
      "Up Front Controller (UFC)": {
        "UFC_1": { "category": "Up Front Controller (UFC)", "control_type": "selector", "api_variant": "momentary_last_position",
          "description": "UFC Keyboard Pushbutton, 1", "inputs": [ { "interface": "set_state", "max_value": 1 } ] },
        "UFC_AP": { "category": "Up Front Controller (UFC)", "control_type": "selector", "api_variant": "momentary_last_position",
          "description": "Function Selector Pushbutton, A/P", "inputs": [ { "interface": "set_state", "max_value": 1 } ] },
        "UFC_ADF": { "category": "Up Front Controller (UFC)", "control_type": "selector", "description": "ADF Function Select Switch",
          "positions": ["1", "OFF", "2"], "inputs": [ { "interface": "set_state", "max_value": 2 } ] }
      },
      "CDNU": {
        "RIO_CDNU_NUM_3": { "category": "CDNU", "control_type": "selector", "api_variant": "momentary_last_position",
          "description": "3", "inputs": [ { "interface": "set_state", "max_value": 1 } ] }
      },
      "Armament Panel": {
        "RIO_WEAPON_QUANTR_1": { "category": "Armament Panel", "control_type": "selector", "description": "RIO Weapon Quantity 1s",
          "positions": ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], "inputs": [ { "interface": "set_state", "max_value": 9 } ] },
        "RIO_CMDS_FLARE_QUAN": { "category": "Armament Panel", "control_type": "selector", "description": "RIO Flare Quantity",
          "positions": ["2", "3", "4", "6"], "inputs": [ { "interface": "set_state", "max_value": 3 } ] },
        "TACAN_MODE": { "category": "Armament Panel", "control_type": "selector", "description": "TACAN Mode Dial",
          "inputs": [ { "interface": "set_state", "max_value": 4 } ] },
        "OLD_SWITCH": { "category": "Armament Panel", "control_type": "selector", "description": "Old Switch",
          "deprecated": { "since": "0.8", "use_instead": "NEW_SWITCH" }, "inputs": [ { "interface": "set_state", "max_value": 1 } ] },
        "EMER_JETT_BTN": { "category": "Armament Panel", "control_type": "action", "description": "Emergency Jettison Button",
          "inputs": [ { "interface": "action", "argument": "TOGGLE" } ] }
      },
      "Lights": {
        "CONSOLE_BRT": { "category": "Lights", "control_type": "limited_dial", "description": "Console Brightness",
          "inputs": [ { "interface": "set_state", "max_value": 65535 }, { "interface": "variable_step", "max_value": 65535, "suggested_step": 3200 } ] },
        "MASTER_CAUTION_LT": { "category": "Lights", "control_type": "led", "description": "Master Caution Light", "inputs": [] }
      }
    }
    """;

    private static ControlCatalog Catalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Jet.json"), ModuleJson);
        File.WriteAllText(Path.Combine(dir, DefinitionLoader.AliasesFile), """{ "Jet": ["Jet"] }""");
        return new DefinitionLoader(dir).LoadAircraft("Jet");
    }

    private static Dictionary<string, Command> Seed(Profile? profile = null) =>
        CommandSeeder.Generate(Catalog(), profile ?? new Profile()).Candidates.ToDictionary(c => c.VoiceLine, c => c.Command);

    [Fact]
    public void Named_positions_become_one_command_each()
    {
        var commands = Seed();
        var up = commands["Hook extension handle up"];
        var down = commands["Hook extension handle down"];

        var step = Assert.IsType<BiosStep>(Assert.Single(down.Steps));
        Assert.Equal(("PLT_HOOK_LEVER", "1", true), (step.Control, step.Arg, step.SkipIfAlready));
        Assert.Equal("0", ((BiosStep)up.Steps[0]).Arg);
        Assert.Equal(["ADF function select 1", "ADF function select off", "ADF function select 2"],
            commands.Keys.Where(k => k.StartsWith("ADF", StringComparison.Ordinal)));
    }

    [Fact]
    public void Seeded_commands_start_disabled_and_marked()
    {
        foreach (var command in Seed().Values)
        {
            Assert.False(command.Enabled);
            Assert.True(command.Seeded);
            Assert.StartsWith("Seeded from ", command.Description);
            Assert.Equal(command.Name, command.Phrases[0]);
        }
    }

    [Fact]
    public void Two_seat_duplicates_get_their_crew_station()
    {
        var commands = Seed();
        Assert.Contains("Pilot canopy jettison on", commands.Keys);
        Assert.Contains("RIO canopy jettison off", commands.Keys);
        Assert.True(commands["RIO canopy jettison on"].Critical);
        Assert.False(commands["Hook extension handle up"].Critical);
    }

    [Fact]
    public void Unnamed_two_position_switches_get_on_and_off_with_a_note()
    {
        var commands = Seed();
        var on = commands["Spin recovery on"];
        Assert.Equal("1", ((BiosStep)on.Steps[0]).Arg);
        Assert.Contains("check that 1 is on", on.Description);
        Assert.Equal("0", ((BiosStep)commands["Spin recovery off"].Steps[0]).Arg);
    }

    [Fact]
    public void Push_buttons_press_and_release()
    {
        var commands = Seed();
        var press = commands["Press UFC keyboard 1"];
        Assert.Equal(["Press UFC keyboard 1", "Press UFC keyboard one"], press.Phrases);
        var step = Assert.IsType<BiosStep>(Assert.Single(press.Steps));
        Assert.Equal("1", step.Arg);
        Assert.Equal(BiosStep.DefaultPressMs, step.PressMs);
        Assert.Equal("0", step.ReleaseArg);
        Assert.Contains("Press function A P", commands.Keys);
        Assert.Contains("Press CDNU 3", commands.Keys);

        var jettison = commands["Press emergency jettison"];
        Assert.Equal("TOGGLE", ((BiosStep)Assert.Single(jettison.Steps)).Arg);
        Assert.True(jettison.Critical);
    }

    [Fact]
    public void Number_wheels_take_the_number_as_a_parameter()
    {
        var commands = Seed();
        var quantity = commands["Weapon quantity 1s {position:0-9}"];
        Assert.Equal("{position}", ((BiosStep)quantity.Steps[0]).Arg);

        var flares = commands["Flare quantity {position:2=0|3=1|4=2|6=3}"];
        var template = PhraseTemplate.Parse(PhraseExpander.Expand(flares.Phrases[0])[0]);
        var parsed = SlotParser.Parse(template, "four");
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal("2", parsed.Values["position"]);

        Assert.Contains("TACAN mode position {position:0-4}", commands.Keys);
    }

    [Fact]
    public void Knobs_lights_and_deprecated_controls_are_skipped()
    {
        var result = CommandSeeder.Generate(Catalog(), new Profile());
        var sources = result.Candidates.Select(c => c.Control.Identifier).ToHashSet();
        Assert.DoesNotContain("CONSOLE_BRT", sources);
        Assert.DoesNotContain("MASTER_CAUTION_LT", sources);
        Assert.DoesNotContain("OLD_SWITCH", sources);
        Assert.Equal(3, result.SkippedControls);
    }

    [Fact]
    public void Every_seeded_phrase_compiles()
    {
        var profile = new Profile { Commands = Seed().Values.ToList() };
        foreach (var command in profile.Commands) command.Enabled = true;
        Assert.Empty(CompiledProfile.Compile(profile).Problems);
    }

    [Fact]
    public void Commands_you_already_have_are_covered()
    {
        var mine = new Command { Name = "Gear hook down", Steps = [new BiosStep { Control = "PLT_HOOK_LEVER", Arg = "1" }] };
        var common = new Profile { Commands = [new Command { Name = "Spin recovery on" }] };
        var result = CommandSeeder.Generate(Catalog(), new Profile { Commands = [mine] }, common);

        var hook = result.Candidates.Single(c => c.VoiceLine == "Hook extension handle down");
        Assert.Equal((SeedStatus.Covered, "Gear hook down"), (hook.Status, hook.CoveredBy));
        Assert.Equal(SeedStatus.Covered, result.Candidates.Single(c => c.VoiceLine == "Spin recovery on").Status);
        Assert.Equal(SeedStatus.New, result.Candidates.Single(c => c.VoiceLine == "Hook extension handle up").Status);

        var profile = new Profile { Commands = [mine] };
        CommandSeeder.Apply(profile, result.Candidates);
        Assert.DoesNotContain(profile.Commands, c => c.Name == "Hook extension handle down");
        Assert.Contains(profile.Commands, c => c.Name == "Hook extension handle up");
    }

    [Fact]
    public void Seeding_again_replaces_seeded_commands_but_keeps_yours()
    {
        var profile = new Profile();
        var first = CommandSeeder.Generate(Catalog(), profile).Candidates;
        Assert.Equal(first.Count, CommandSeeder.Apply(profile, first));

        // The user adopts one command: editing or enabling it clears Seeded.
        var adopted = profile.Commands.Single(c => c.Name == "Spin recovery on");
        adopted.Seeded = false;
        adopted.Enabled = true;

        var second = CommandSeeder.Generate(Catalog(), profile).Candidates;
        Assert.All(second.Where(c => c.VoiceLine != "Spin recovery on"), c => Assert.Equal(SeedStatus.Update, c.Status));
        Assert.Equal(SeedStatus.Covered, second.Single(c => c.VoiceLine == "Spin recovery on").Status);

        CommandSeeder.Apply(profile, second);
        Assert.Equal(first.Count, profile.Commands.Count);
        Assert.Same(adopted, profile.Commands.Single(c => c.Name == "Spin recovery on"));

        Assert.Equal(first.Count - 1, CommandSeeder.RemoveSeeded(profile));
        Assert.Same(adopted, Assert.Single(profile.Commands));
    }

    [Theory]
    [InlineData("Master Arm Switch", "Master arm")]
    [InlineData("HUD Video BIT Initiate Pushbutton - Push to initiate BIT", "HUD video BIT initiate")]
    [InlineData("Radar Altimeter Control Knob", "Radar altimeter")]
    [InlineData("BSK 1 (PB13)", "BSK 1")]
    [InlineData("PILOT Master Test Selector (pull)", "Master test pull")]
    [InlineData("Left Engine/AMAD Fire Warning", "Left engine AMAD fire warning")]
    public void Descriptions_become_spoken_names(string description, string expected)
    {
        Assert.Equal(expected, VoiceLines.ControlName(description, "X").Name);
    }

    [Theory]
    [InlineData("DN", "down")]
    [InlineData("REJ 1", "reject 1")]
    [InlineData("X", "X")]
    [InlineData("STBY", "standby")]
    public void Positions_are_spoken(string position, string expected) => Assert.Equal(expected, VoiceLines.Position(position));
}
