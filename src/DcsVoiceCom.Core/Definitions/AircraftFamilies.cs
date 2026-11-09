namespace DcsVoiceCom.Definitions;

/// <summary>
/// Groups the names in AircraftAliases.json that are the same jet. DCS-BIOS lists every name DCS can report
/// (F-16C_50, F-16D_52, F-16I, …), but names with the same control files share one profile.
/// Some variants stay apart because their cockpits differ too much: F-14A, F-14B, F-14B(U),
/// and Mirage F1CE, F1EE, F1BE.
/// </summary>
public static class AircraftFamilies
{
    /// <summary>Modules every aircraft carries; they don't tell aircraft apart.</summary>
    private static readonly HashSet<string> SharedModules =
        new(["CommonData", "MetadataStart", "MetadataEnd"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Name prefixes that keep their own profile although the modules are the same.</summary>
    private static readonly string[] SeparateVariants =
        new[] { "F-14A", "F-14B", "F-14BU", "Mirage-F1CE", "Mirage-F1EE", "Mirage-F1BE" }
            .OrderByDescending(p => p.Length).ToArray(); // longest first, so F-14BU wins over F-14B

    /// <summary>Each name → the profile name of its group.</summary>
    public static IReadOnlyDictionary<string, string> BaseNames(IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in aliases.GroupBy(kv => GroupKey(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(kv => kv.Key).ToList();
            var modules = group.First().Value;
            var main = names.FirstOrDefault(n => modules.Contains(n, StringComparer.OrdinalIgnoreCase))
                       ?? names.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).First();
            foreach (var name in names) result[name] = main;
        }
        return result;
    }

    /// <summary>One name per group, sorted.</summary>
    public static IReadOnlyList<string> Main(IReadOnlyDictionary<string, IReadOnlyList<string>> aliases) =>
        BaseNames(aliases).Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static string GroupKey(string name, IReadOnlyList<string> modules)
    {
        var own = modules.Where(m => !SharedModules.Contains(m)).Select(m => m.ToUpperInvariant()).Order(StringComparer.Ordinal);
        var key = string.Join("|", own);
        if (key.Length == 0) key = "=" + name; // nothing but shared modules: keep it alone
        var variant = SeparateVariants.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return variant is null ? key : key + "#" + variant.ToUpperInvariant();
    }
}
