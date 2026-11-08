using System.Reflection;

namespace DcsVoiceCom.DcsHook;

public enum HookStatus
{
    /// <summary>No DCS folder in Saved Games.</summary>
    NoDcs,
    NotInstalled,
    /// <summary>Installed, but an older version or another port.</summary>
    OutOfDate,
    Installed,
}

/// <summary>
/// Copies the DCS VoiceCom hook into every DCS folder in Saved Games (DCS, DCS.openbeta, …): Scripts\Hooks.
/// </summary>
public sealed class DcsHookInstaller
{
    public const string FileName = "DcsVoiceCom.GameGUI.lua";

    private readonly string _savedGames;
    private readonly int _port;

    /// <param name="savedGames">The Saved Games folder; null for the user's.</param>
    public DcsHookInstaller(int port = DcsHookListener.DefaultPort, string? savedGames = null)
    {
        _port = port;
        _savedGames = savedGames ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    /// <summary>The hook as it would be installed, with the port filled in.</summary>
    public string Script => Template.Replace("{{PORT}}", _port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Template { get; } = LoadTemplate();

    /// <summary>Saved Games\DCS and Saved Games\DCS.* folders.</summary>
    public IReadOnlyList<string> DcsFolders() =>
        Directory.Exists(_savedGames)
            ? Directory.EnumerateDirectories(_savedGames)
                .Where(d => Path.GetFileName(d) is { } n &&
                            (n.Equals("DCS", StringComparison.OrdinalIgnoreCase) || n.StartsWith("DCS.", StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public IReadOnlyList<string> HookPaths() => DcsFolders().Select(d => Path.Combine(d, "Scripts", "Hooks", FileName)).ToList();

    public HookStatus Status()
    {
        var paths = HookPaths();
        if (paths.Count == 0) return HookStatus.NoDcs;
        var present = paths.Where(File.Exists).ToList();
        if (present.Count == 0) return HookStatus.NotInstalled;
        var script = Script;
        return present.Count == paths.Count && present.All(p => File.ReadAllText(p) == script) ? HookStatus.Installed : HookStatus.OutOfDate;
    }

    /// <summary>Writes the hook into every DCS folder; returns the files written.</summary>
    public IReadOnlyList<string> Install()
    {
        var paths = HookPaths();
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Script);
        }
        return paths;
    }

    /// <summary>Deletes the hook from every DCS folder; returns the files removed.</summary>
    public IReadOnlyList<string> Remove()
    {
        var removed = HookPaths().Where(File.Exists).ToList();
        foreach (var path in removed) File.Delete(path);
        return removed;
    }

    private static string LoadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FileName)
                           ?? throw new InvalidOperationException($"{FileName} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\r\n");
    }
}
