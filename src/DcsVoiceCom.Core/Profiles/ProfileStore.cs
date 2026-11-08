using DcsVoiceCom.DcsHook;

namespace DcsVoiceCom.Profiles;

/// <summary>Loads and saves profiles from a folder (%LOCALAPPDATA%\DcsVoiceCom\profiles by default).</summary>
public sealed class ProfileStore
{
    public const string CommonProfileName = "common";

    /// <summary>Server profiles are stored as server-&lt;name&gt;.json.</summary>
    public const string ServerPrefix = "server-";

    public static bool IsServerProfile(string name) => name.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsCommon(string name) => name.Equals(CommonProfileName, StringComparison.OrdinalIgnoreCase);

    public ProfileStore(string directory) => Directory = directory;

    public string Directory { get; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcsVoiceCom", "profiles");

    public string PathFor(string aircraft) => Path.Combine(Directory, FileNameFor(aircraft));

    public static string FileNameFor(string aircraft)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(aircraft.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
        return safe + ".json";
    }

    /// <summary>Aircraft profiles: every file but common and the server profiles.</summary>
    public IReadOnlyList<string> List() => AllNames().Where(n => !IsCommon(n) && !IsServerProfile(n)).ToList();

    /// <summary>Server profile names, with their "server-" prefix.</summary>
    public IReadOnlyList<string> ListServerProfiles() => AllNames().Where(IsServerProfile).ToList();

    private IEnumerable<string> AllNames() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>
    /// The server profiles whose <c>servers</c> match the session, loaded, in name order. Files that can't be read
    /// are skipped and reported in <paramref name="problems"/>.
    /// </summary>
    public IReadOnlyList<Profile> MatchingServerProfiles(DcsSession? session, List<string>? problems = null) =>
        session is null || !session.OnServer
            ? []
            : ListServerProfiles()
                .Select(name => TryLoadServerProfile(name, problems))
                .OfType<Profile>()
                .Where(p => ServerPattern.MatchesAny(p.Servers, session))
                .ToList();

    /// <summary>A server profile, named after its file; null (reported in <paramref name="problems"/>) when it can't be read.</summary>
    public Profile? TryLoadServerProfile(string name, List<string>? problems = null)
    {
        try
        {
            var profile = TryLoad(name);
            if (profile != null) profile.Aircraft = name;
            return profile;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            problems?.Add($"Could not read server profile {name}: {ex.Message}");
            return null;
        }
    }

    public void Delete(string aircraft) => File.Delete(PathFor(aircraft));

    public bool Exists(string aircraft) => File.Exists(PathFor(aircraft));

    public Profile? TryLoad(string aircraft)
    {
        var path = PathFor(aircraft);
        if (!File.Exists(path)) return null;
        var profile = ProfileSerializer.Deserialize(File.ReadAllText(path));
        if (string.IsNullOrEmpty(profile.Aircraft)) profile.Aircraft = aircraft;
        return profile;
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(profile.Aircraft);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ProfileSerializer.Serialize(profile));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The aircraft profile with the server profiles, then common.json, merged under it: a lower layer's command is
    /// added unless a higher one has a command of the same name; keywords are combined.
    /// Order: aircraft &gt; server profiles (in the order given) &gt; common. Returns null when no file exists.
    /// </summary>
    public Profile? LoadMerged(string aircraft, IReadOnlyList<Profile>? serverProfiles = null)
    {
        var own = TryLoad(aircraft);
        var layers = (serverProfiles ?? []).Append(TryLoad(CommonProfileName)).ToList();
        if (own is null && layers.All(l => l is null)) return null;
        return layers.Aggregate(own ?? new Profile { Aircraft = aircraft }, Merge);
    }

    public static Profile Merge(Profile aircraft, Profile? common)
    {
        if (common is null) return aircraft;
        var names = new HashSet<string>(aircraft.Commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return new Profile
        {
            Aircraft = aircraft.Aircraft,
            ModuleVersion = aircraft.ModuleVersion,
            Threshold = aircraft.Threshold,
            CriticalThreshold = aircraft.CriticalThreshold,
            Keywords = aircraft.Keywords.Concat(common.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Commands = aircraft.Commands.Concat(common.Commands.Where(c => !names.Contains(c.Name))).ToList(),
        };
    }
}
