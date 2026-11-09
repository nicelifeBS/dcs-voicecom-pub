using System.Text.Json;

namespace DcsVoiceCom.Definitions;

/// <summary>
/// Reads the control reference JSON from the user's own DCS-BIOS install
/// (Saved Games\DCS\Scripts\DCS-BIOS\doc\json by default). Nothing from DCS-BIOS is bundled.
/// Layout: AircraftAliases.json maps _ACFT_NAME → module names; each &lt;module&gt;.json maps
/// category → identifier → control.
/// </summary>
public sealed class DefinitionLoader
{
    public const string AliasesFile = "AircraftAliases.json";

    /// <summary>Metadata modules carried by every aircraft.</summary>
    private static readonly string[] SharedModules = ["MetadataStart", "MetadataEnd"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DefinitionLoader(string jsonDirectory) => JsonDirectory = jsonDirectory;

    public string JsonDirectory { get; }

    public static string DefaultJsonDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "DCS", "Scripts", "DCS-BIOS", "doc", "json");

    public bool Exists => Directory.Exists(JsonDirectory);

    /// <summary>Latest write time of any JSON file, so callers can detect a DCS-BIOS update and reload.</summary>
    public DateTime LastModifiedUtc =>
        Exists ? Directory.EnumerateFiles(JsonDirectory, "*.json").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() : default;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LoadAliases()
    {
        var path = Path.Combine(JsonDirectory, AliasesFile);
        if (!File.Exists(path)) return new Dictionary<string, IReadOnlyList<string>>();
        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream, JsonOptions) ?? [];
        return raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One name per jet: variants with the same control files are left out (see <see cref="AircraftFamilies"/>).</summary>
    public IReadOnlyList<string> AvailableAircraft() => AircraftFamilies.Main(LoadAliases());

    /// <summary>The profile name shared by this aircraft's variants, e.g. F-16D_52 → F-16C_50. The name itself when unknown.</summary>
    public string BaseAircraft(string aircraft) =>
        AircraftFamilies.BaseNames(LoadAliases()).TryGetValue(aircraft, out var main) ? main : aircraft;

    public ControlCatalog LoadAircraft(string aircraft)
    {
        var aliases = LoadAliases();
        var modules = aliases.TryGetValue(aircraft, out var m) ? m.ToList() : [aircraft];
        foreach (var shared in SharedModules)
        {
            if (!modules.Contains(shared, StringComparer.OrdinalIgnoreCase)) modules.Add(shared);
        }

        var controls = new List<ControlDefinition>();
        var loaded = new List<string>();
        foreach (var module in modules)
        {
            // The name can come from the network (_ACFT_NAME), so it must stay a plain file name in JsonDirectory.
            if (!IsModuleName(module)) continue;
            var path = Path.Combine(JsonDirectory, module + ".json");
            if (!File.Exists(path)) continue;
            controls.AddRange(LoadModule(path, module));
            loaded.Add(module);
        }
        return new ControlCatalog(aircraft, loaded, controls);
    }

    /// <summary>True for a bare file name: no folder, drive, "..", or characters a file name can't hold.</summary>
    internal static bool IsModuleName(string module) =>
        !string.IsNullOrWhiteSpace(module)
        && module != "." && module != ".."
        && module.IndexOfAny(['/', '\\', ':']) < 0
        && module.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static IEnumerable<ControlDefinition> LoadModule(string path, string moduleName)
    {
        using var stream = File.OpenRead(path);
        var categories = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ControlDefinition>>>(stream, JsonOptions) ?? [];
        var result = new List<ControlDefinition>();
        foreach (var (category, controls) in categories)
        {
            foreach (var (identifier, control) in controls)
            {
                if (string.IsNullOrEmpty(control.Identifier)) control.Identifier = identifier;
                if (string.IsNullOrEmpty(control.Category)) control.Category = category;
                control.Module = moduleName;
                result.Add(control);
            }
        }
        return result;
    }
}
