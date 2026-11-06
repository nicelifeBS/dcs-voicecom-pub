using DcsVoiceCom.Definitions;

namespace DcsVoiceCom.Tests;

/// <summary>A small hand-written module in the DCS-BIOS JSON layout, used instead of real definitions.</summary>
internal static class TestCatalog
{
    public const string ModuleJson = """
    {
      "Gear": {
        "GEAR_LEVER": {
          "category": "Gear", "control_type": "selector", "description": "Landing Gear Lever", "identifier": "GEAR_LEVER",
          "positions": ["UP", "DN"],
          "inputs": [
            { "interface": "fixed_step", "description": "switch to previous or next state" },
            { "interface": "set_state", "max_value": 1, "description": "set position" },
            { "interface": "action", "argument": "TOGGLE", "description": "toggle switch state" }
          ],
          "outputs": [ { "type": "integer", "address": 4608, "mask": 1, "shift_by": 0, "max_value": 1, "suffix": "", "description": "position" } ]
        }
      },
      "Weapons": {
        "MASTER_ARM": {
          "category": "Weapons", "control_type": "selector", "description": "Master Arm Switch", "identifier": "MASTER_ARM",
          "inputs": [ { "interface": "set_state", "max_value": 2, "description": "set position" } ],
          "outputs": [ { "type": "integer", "address": 4608, "mask": 6, "shift_by": 1, "max_value": 2, "suffix": "", "description": "position" } ]
        },
        "APU_READY_LT": {
          "category": "Weapons", "control_type": "led", "description": "APU Ready", "identifier": "APU_READY_LT",
          "inputs": [],
          "outputs": [ { "type": "integer", "address": 4608, "mask": 8, "shift_by": 3, "max_value": 1, "suffix": "", "description": "light" } ]
        },
        "HDG_KNOB": {
          "category": "Nav", "control_type": "variable_step_dial", "description": "Heading Knob", "identifier": "HDG_KNOB",
          "inputs": [ { "interface": "variable_step", "max_value": 65535, "suggested_step": 3200, "description": "turn" } ],
          "outputs": [ { "type": "integer", "address": 4610, "mask": 65535, "shift_by": 0, "max_value": 65535, "suffix": "", "description": "position" } ]
        },
        "UFC_DISPLAY": {
          "category": "UFC", "control_type": "display", "description": "UFC Display", "identifier": "UFC_DISPLAY",
          "inputs": [],
          "outputs": [ { "type": "string", "address": 4620, "max_length": 6, "suffix": "", "description": "text" } ]
        }
      }
    }
    """;

    public static string WriteDefinitions(string directory, string aircraft = "TestJet", string module = "TestJet")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, module + ".json"), ModuleJson);
        File.WriteAllText(Path.Combine(directory, DefinitionLoader.AliasesFile), $$"""{ "{{aircraft}}": ["{{module}}"] }""");
        return directory;
    }

    public static ControlCatalog Load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N"));
        WriteDefinitions(dir);
        return new DefinitionLoader(dir).LoadAircraft("TestJet");
    }
}
