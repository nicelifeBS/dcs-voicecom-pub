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

    /// <summary>The parsed aliases file and its groups, kept until the file changes.</summary>
    private sealed record AliasCache(DateTime WriteTimeUtc, long Length,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases, IReadOnlyDictionary<string, string> BaseNames);

    private volatile AliasCache? _aliasCache;

    private static readonly AliasCache NoAliases = new(default, -1,
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string>());

    /// <summary>The aliases file, read again only when its time or size changes (a DCS-BIOS update).</summary>
    private AliasCache Aliases()
    {
        var file = new FileInfo(Path.Combine(JsonDirectory, AliasesFile));
        if (!file.Exists) return _aliasCache = NoAliases;
        if (_aliasCache is { } cached && cached.WriteTimeUtc == file.LastWriteTimeUtc && cached.Length == file.Length) return cached;
        using var stream = file.OpenRead();
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream, JsonOptions) ?? [];
        var aliases = raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        return _aliasCache = new AliasCache(file.LastWriteTimeUtc, file.Length, aliases, AircraftFamilies.BaseNames(aliases));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LoadAliases() => Aliases().Aliases;

    /// <summary>One name per jet: variants with the same control files are left out (see <see cref="AircraftFamilies"/>).</summary>
    public IReadOnlyList<string> AvailableAircraft() => AircraftFamilies.Main(Aliases().BaseNames);

    /// <summary>The profile name shared by this aircraft's variants, e.g. F-16D_52 → F-16C_50, F-14BU → F-14. The name itself when unknown.</summary>
    public string BaseAircraft(string aircraft) =>
        Aliases().BaseNames.TryGetValue(aircraft, out var main) ? main : aircraft;

    public ControlCatalog LoadAircraft(string aircraft)
    {
        var (_, _, aliases, baseNames) = Aliases();
        // A shared profile like "F-14" isn't a name in the aliases: use the files of a plane in its group.
        var modules = aliases.TryGetValue(aircraft, out var m) ? m.ToList()
            : baseNames.FirstOrDefault(kv => kv.Value.Equals(aircraft, StringComparison.OrdinalIgnoreCase)).Key
                is { } member ? aliases[member].ToList()
            : [aircraft];
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
