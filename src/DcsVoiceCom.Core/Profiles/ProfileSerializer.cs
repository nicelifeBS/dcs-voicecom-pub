using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DcsVoiceCom.Profiles;

public static class ProfileSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new StepJsonConverter());
        return options;
    }

    public static Profile Deserialize(string json) =>
        JsonSerializer.Deserialize<Profile>(json, Options) ?? throw new JsonException("Empty profile");

    public static string Serialize(Profile profile) => JsonSerializer.Serialize(profile, Options);
}

/// <summary>Reads and writes steps by their "type" property, which may appear anywhere in the object.</summary>
public sealed class StepJsonConverter : JsonConverter<Step>
{
    private static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bios"] = typeof(BiosStep),
        ["bios_until"] = typeof(BiosUntilStep),
        ["key"] = typeof(KeyStep),
        ["wait"] = typeof(WaitStep),
        ["wait_for"] = typeof(WaitForStep),
        ["if"] = typeof(IfStep),
        ["cue"] = typeof(CueStep),
        ["foreach_char"] = typeof(ForEachCharStep),
        ["enter_value"] = typeof(EnterValueStep),
    };

    public static IEnumerable<string> TypeNames => Types.Keys;

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(Step);

    public override Step Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A step must be an object");

        string? typeName = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                typeName = property.Value.GetString();
        }
        if (typeName is null) throw new JsonException("Step is missing \"type\"");
        if (!Types.TryGetValue(typeName, out var type))
            throw new JsonException($"Unknown step type \"{typeName}\" (known: {string.Join(", ", Types.Keys)})");

        return (Step)(root.Deserialize(type, options) ?? throw new JsonException($"Empty {typeName} step"));
    }

    public override void Write(Utf8JsonWriter writer, Step value, JsonSerializerOptions options)
    {
        var body = JsonSerializer.SerializeToNode(value, value.GetType(), options)?.AsObject() ?? [];
        var ordered = new JsonObject { ["type"] = value.Type };
        foreach (var (key, node) in body.ToList())
        {
            body.Remove(key);
            ordered[key] = node;
        }
        ordered.WriteTo(writer, options);
    }
}
