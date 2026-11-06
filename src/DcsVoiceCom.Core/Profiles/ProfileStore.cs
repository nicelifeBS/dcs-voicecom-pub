namespace DcsVoiceCom.Profiles;

/// <summary>Loads and saves profiles from a folder (%LOCALAPPDATA%\DcsVoiceCom\profiles by default).</summary>
public sealed class ProfileStore
{
    public const string CommonProfileName = "common";

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

    public IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(n => !n.Equals(CommonProfileName, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

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
    /// The aircraft profile with common.json merged under it: common commands are added unless the
    /// aircraft profile has a command of the same name; keywords are combined.
    /// Returns null when neither file exists.
    /// </summary>
    public Profile? LoadMerged(string aircraft)
    {
        var own = TryLoad(aircraft);
        var common = TryLoad(CommonProfileName);
        if (own is null && common is null) return null;
        return Merge(own ?? new Profile { Aircraft = aircraft }, common);
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
