using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class DefinitionLoaderTests
{
    [Fact]
    public void Loads_controls_through_aircraft_aliases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        TestCatalog.WriteDefinitions(dir, aircraft: "F-14B", module: "F-14");
        var loader = new DefinitionLoader(dir);

        var catalog = loader.LoadAircraft("F-14B");

        Assert.Equal(["F-14"], catalog.Modules);
        Assert.Equal(["F-14B"], loader.AvailableAircraft());
        var gear = catalog.Find("gear_lever");
        Assert.NotNull(gear);
        Assert.Equal("Gear", gear.Category);
        Assert.Equal("F-14", gear.Module);
        Assert.Equal(["UP", "DN"], gear.Positions!);
        Assert.Equal(1, gear.FindInput(InputInterface.SetState)!.MaxValue);
        Assert.Equal("TOGGLE", gear.FindInput(InputInterface.Action)!.Argument);
        Assert.Equal(3200, catalog.Find("HDG_KNOB")!.FindInput(InputInterface.VariableStep)!.SuggestedStep);
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData("../x")]
    [InlineData(@"C:\a\b")]
    [InlineData(@"\\host\s\x")]
    [InlineData("..")]
    public void Ignores_aircraft_names_that_leave_the_json_folder(string aircraft)
    {
        var root = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "json");
        TestCatalog.WriteDefinitions(dir, aircraft: "F-14B", module: "F-14");
        File.Copy(Path.Combine(dir, "F-14.json"), Path.Combine(root, "x.json"));

        var catalog = new DefinitionLoader(dir).LoadAircraft(aircraft);

        Assert.DoesNotContain(aircraft, catalog.Modules);
        Assert.Null(catalog.Find("gear_lever"));
    }

    [Fact]
    public void State_reader_uses_address_mask_and_shift()
    {
        var catalog = TestCatalog.Load();
        var buffer = new StateBuffer();
        var reader = new CatalogStateReader(buffer, () => catalog);
        buffer.Write(4608, [0b0000_1101, 0]);
        buffer.Write(4620, "NAV1\0\0"u8);

        Assert.True(reader.TryReadInteger("GEAR_LEVER", out var gear));
        Assert.Equal(1, gear);
        Assert.True(reader.TryReadInteger("MASTER_ARM", out var arm));
        Assert.Equal(2, arm);
        Assert.True(reader.TryReadInteger("APU_READY_LT", out var apu));
        Assert.Equal(1, apu);
        Assert.True(reader.TryReadString("UFC_DISPLAY", out var text));
        Assert.Equal("NAV1", text);
        Assert.False(reader.TryReadInteger("NOPE", out _));
    }

    [Fact]
    public void Finds_controls_a_profile_uses_that_definitions_lack()
    {
        var profile = new Profile
        {
            Commands =
            [
                new Command
                {
                    Name = "test",
                    Steps =
                    [
                        new BiosStep { Control = "GEAR_LEVER", Arg = "0" },
                        new BiosStep { Control = "OLD_SWITCH", Arg = "1" },
                        new IfStep { Condition = new Condition { Output = "GONE_LIGHT", Value = "1" } },
                    ],
                },
            ],
        };

        Assert.Equal(["GONE_LIGHT", "OLD_SWITCH"], CompiledProfile.FindMissingControls(profile, TestCatalog.Load()));
    }
}
