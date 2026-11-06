using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcsVoiceCom.Definitions;

/// <summary>One control from a DCS-BIOS module JSON (doc/json/&lt;module&gt;.json).</summary>
public sealed class ControlDefinition
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("control_type")] public string ControlType { get; set; } = "";
    [JsonPropertyName("api_variant")] public string? ApiVariant { get; set; }
    [JsonPropertyName("positions")] public List<string>? Positions { get; set; }
    [JsonPropertyName("inputs")] public List<InputDefinition> Inputs { get; set; } = [];
    [JsonPropertyName("outputs")] public List<OutputDefinition> Outputs { get; set; } = [];

    /// <summary>Set by DCS-BIOS on controls kept only for old panels ({since, description, use_instead}).</summary>
    [JsonPropertyName("deprecated")] public JsonElement? Deprecated { get; set; }

    [JsonIgnore] public bool IsDeprecated => Deprecated is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) };

    /// <summary>Module the control was loaded from (not part of the JSON).</summary>
    [JsonIgnore] public string Module { get; set; } = "";

    public InputDefinition? FindInput(string interfaceName) =>
        Inputs.FirstOrDefault(i => string.Equals(i.Interface, interfaceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The output read for state checks: the first integer output without a suffix, else the first integer output.</summary>
    public OutputDefinition? PrimaryIntegerOutput =>
        Outputs.FirstOrDefault(o => o.IsInteger && string.IsNullOrEmpty(o.Suffix)) ?? Outputs.FirstOrDefault(o => o.IsInteger);

    public OutputDefinition? PrimaryStringOutput => Outputs.FirstOrDefault(o => o.IsString);
}

public static class InputInterface
{
    public const string SetState = "set_state";
    public const string FixedStep = "fixed_step";
    public const string VariableStep = "variable_step";
    public const string Action = "action";
    public const string SetString = "set_string";
}

public sealed class InputDefinition
{
    [JsonPropertyName("interface")] public string Interface { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("max_value")] public int? MaxValue { get; set; }
    [JsonPropertyName("suggested_step")] public int? SuggestedStep { get; set; }
    [JsonPropertyName("argument")] public string? Argument { get; set; }
}

public sealed class OutputDefinition
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("address")] public int Address { get; set; }
    [JsonPropertyName("mask")] public int? Mask { get; set; }
    [JsonPropertyName("shift_by")] public int? ShiftBy { get; set; }
    [JsonPropertyName("max_value")] public int? MaxValue { get; set; }
    [JsonPropertyName("max_length")] public int? MaxLength { get; set; }
    [JsonPropertyName("suffix")] public string? Suffix { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonIgnore] public bool IsInteger => Type == "integer";
    [JsonIgnore] public bool IsString => Type == "string";
}
