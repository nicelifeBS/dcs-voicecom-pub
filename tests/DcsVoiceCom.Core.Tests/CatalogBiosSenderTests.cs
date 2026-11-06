using DcsVoiceCom.Definitions;

namespace DcsVoiceCom.Tests;

public class CatalogBiosSenderTests
{
    private static readonly ControlCatalog Catalog = new("A-10C_2", [],
        [new ControlDefinition { Identifier = "ARC210_100KHZ_SEL", Inputs = [new InputDefinition { Interface = InputInterface.FixedStep }] }]);

    [Fact]
    public async Task Sends_the_catalogs_spelling_because_DCS_BIOS_is_case_sensitive()
    {
        var cockpit = new FakeCockpit();
        var sender = new CatalogBiosSender(cockpit, () => Catalog);

        await sender.SendAsync("ARC210_100kHZ_SEL", "INC");
        await sender.SendAsync("NOT_IN_CATALOG", "1");

        Assert.Equal(["ARC210_100KHZ_SEL INC", "NOT_IN_CATALOG 1"], cockpit.Sent);
    }
}

public class CatalogBiosSenderKnobTests
{
    [Fact]
    public async Task Turns_INC_and_DEC_into_the_knobs_step_when_it_has_no_fixed_step()
    {
        var catalog = new ControlCatalog("A-10C_2", [],
        [
            new ControlDefinition { Identifier = "ARC210_CHN_KNB", Inputs = [new InputDefinition { Interface = InputInterface.VariableStep, SuggestedStep = 3200, MaxValue = 65535 }] },
            new ControlDefinition { Identifier = "ARC210_100KHZ_SEL", Inputs = [new InputDefinition { Interface = InputInterface.FixedStep }] },
        ]);
        var cockpit = new FakeCockpit();
        var sender = new CatalogBiosSender(cockpit, () => catalog);

        await sender.SendAsync("ARC210_CHN_KNB", "INC");
        await sender.SendAsync("ARC210_CHN_KNB", "DEC");
        await sender.SendAsync("ARC210_CHN_KNB", "+1000");
        await sender.SendAsync("ARC210_100KHZ_SEL", "INC");

        Assert.Equal(["ARC210_CHN_KNB +3200", "ARC210_CHN_KNB -3200", "ARC210_CHN_KNB +1000", "ARC210_100KHZ_SEL INC"], cockpit.Sent);
    }
}
