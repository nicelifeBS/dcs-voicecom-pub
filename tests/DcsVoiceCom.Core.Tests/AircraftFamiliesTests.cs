using DcsVoiceCom.Definitions;

namespace DcsVoiceCom.Tests;

public class AircraftFamiliesTests
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F-16C_50"] = ["CommonData", "F-16C_50"],
        ["F-16D_52"] = ["CommonData", "F-16C_50", "NS430"],
        ["F-16I"] = ["CommonData", "F-16C_50", "NS430"],
        ["F-14A-135-GR"] = ["CommonData", "F-14", "NS430"],
        ["F-14A-135-GR-Early"] = ["CommonData", "F-14", "NS430"],
        ["F-14B"] = ["CommonData", "F-14", "NS430"],
        ["F-14BU"] = ["CommonData", "F-14", "NS430"],
        ["Mirage-F1CE"] = ["CommonData", "MirageF1", "NS430"],
        ["Mirage-F1EE"] = ["CommonData", "MirageF1", "NS430"],
        ["Mirage-F1BE"] = ["CommonData", "MirageF1", "NS430"],
        ["F-15C"] = ["CommonData", "FC3", "NS430"],
        ["Su-27"] = ["CommonData", "FC3", "NS430"],
        ["NS430"] = ["CommonData", "NS430"],
        ["Supercarrier"] = ["CommonData", "NS430"],
        ["VSN_F22"] = ["CommonData", "FC3", "NS430"],
        ["AH-64D_BLK_II"] = ["CommonData", "AH-64D"],
    };

    [Fact]
    public void Lists_one_name_per_jet_and_hides_VSN_mods()
    {
        Assert.Equal(
            ["AH-64D_BLK_II", "F-14", "F-15C", "F-16C_50", "MirageF1", "NS430", "Su-27", "Supercarrier"],
            AircraftFamilies.Main(Aliases));
    }

    [Theory]
    [InlineData("F-16D_52", "F-16C_50")]
    [InlineData("F-16I", "F-16C_50")]
    [InlineData("F-16C_50", "F-16C_50")]
    [InlineData("Su-27", "Su-27")]
    [InlineData("VSN_F22", "VSN_F22")]
    [InlineData("F-14A-135-GR-Early", "F-14")]
    [InlineData("F-14B", "F-14")]
    [InlineData("F-14BU", "F-14")]
    [InlineData("Mirage-F1EE", "MirageF1")]
    [InlineData("AH-64D_BLK_II", "AH-64D_BLK_II")]
    public void Variants_share_the_main_name(string aircraft, string expected) =>
        Assert.Equal(expected, AircraftFamilies.BaseNames(Aliases)[aircraft]);

    [Fact]
    public void Loader_maps_a_variant_and_leaves_unknown_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DefinitionLoader.AliasesFile),
            """{ "F-16C_50": ["CommonData", "F-16C_50"], "F-16D_52": ["CommonData", "F-16C_50"] }""");
        var loader = new DefinitionLoader(dir);

        Assert.Equal(["F-16C_50"], loader.AvailableAircraft());
        Assert.Equal("F-16C_50", loader.BaseAircraft("F-16D_52"));
        Assert.Equal("Unknown", loader.BaseAircraft("Unknown"));
    }

    [Fact]
    public void A_shared_profile_loads_the_controls_of_its_group()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        TestCatalog.WriteDefinitions(dir, aircraft: "F-14B", module: "F-14");
        File.WriteAllText(Path.Combine(dir, DefinitionLoader.AliasesFile),
            """{ "F-14B": ["CommonData", "F-14", "NS430"], "F-14BU": ["CommonData", "F-14", "NS430"] }""");
        var loader = new DefinitionLoader(dir);

        Assert.Equal(["F-14"], loader.AvailableAircraft());
        Assert.Equal("F-14", loader.BaseAircraft("F-14BU"));
        Assert.Equal(["F-14"], loader.LoadAircraft("F-14").Modules);
        Assert.NotNull(loader.LoadAircraft("F-14").Find("gear_lever"));
    }

    [Fact]
    public void Aliases_are_read_again_when_the_file_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DefinitionLoader.AliasesFile);
        File.WriteAllText(path, """{ "F-16C_50": ["F-16C_50"] }""");
        var loader = new DefinitionLoader(dir);

        Assert.Same(loader.LoadAliases(), loader.LoadAliases());
        Assert.Equal(["F-16C_50"], loader.AvailableAircraft());

        File.WriteAllText(path, """{ "F-16C_50": ["F-16C_50"], "A-10C": ["A-10C"] }""");
        Assert.Equal(["A-10C", "F-16C_50"], loader.AvailableAircraft());

        File.Delete(path);
        Assert.Empty(loader.AvailableAircraft());
    }
}
