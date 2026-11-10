namespace DcsVoiceCom.Definitions;

/// <summary>
/// Groups the names in AircraftAliases.json that are the same jet. DCS-BIOS lists every name DCS can report
/// (F-16C_50, F-16D_52, F-16I, …), but names with the same control files share one profile.
/// The group's name is the name that matches its control file (F-16C_50), else the control file itself
/// (F-14 for every Tomcat, MirageF1). A variant's own profile (F-14BU) still comes first.
/// </summary>
public static class AircraftFamilies
{
    /// <summary>Modules many aircraft carry; they don't tell aircraft apart. Some variants list NS430, others don't.</summary>
    private static readonly HashSet<string> SharedModules =
        new(["CommonData", "MetadataStart", "MetadataEnd", "NS430"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules used by many different jets (Flaming Cliffs style): every name keeps its own profile.</summary>
    private static readonly HashSet<string> GenericModules = new(["FC3"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Each name → the profile name of its group.</summary>
    public static IReadOnlyDictionary<string, string> BaseNames(IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in aliases.GroupBy(kv => GroupKey(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(kv => kv.Key).ToList();
            var own = OwnModules(group.First().Value);
            var main = names.Count == 1 ? names[0]
                : names.FirstOrDefault(n => own.Contains(n, StringComparer.OrdinalIgnoreCase))
                  ?? (own.Count == 1 ? own[0] : names.Order(StringComparer.OrdinalIgnoreCase).First());
            foreach (var name in names) result[name] = main;
        }
        return result;
    }

    /// <summary>Name prefixes left out of the list: the VSN mods, which DCS-BIOS lists whether they're installed or not.</summary>
    private static readonly string[] HiddenPrefixes = ["VSN_"];

    /// <summary>One name per group, sorted, without the hidden mods. A hidden name still works when typed.</summary>
    public static IReadOnlyList<string> Main(IReadOnlyDictionary<string, IReadOnlyList<string>> aliases) => Main(BaseNames(aliases));

    /// <summary>The same, from groups already made with <see cref="BaseNames"/>.</summary>
    public static IReadOnlyList<string> Main(IReadOnlyDictionary<string, string> baseNames) =>
        baseNames.Values
            .Where(n => !HiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> OwnModules(IReadOnlyList<string> modules) =>
        modules.Where(m => !SharedModules.Contains(m)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static string GroupKey(string name, IReadOnlyList<string> modules)
    {
        var own = OwnModules(modules);
        // Nothing but shared or generic modules: keep it alone.
        if (own.All(GenericModules.Contains)) return "=" + name.ToUpperInvariant();
        return string.Join("|", own.Select(m => m.ToUpperInvariant()));
    }
}
