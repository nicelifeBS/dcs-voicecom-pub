using System.Text.Json;
using System.Text.Json.Nodes;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.DcsHook;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Config;

/// <summary>Push-to-talk binding: a DirectInput device (by instance GUID or name substring) and a 0-based button.</summary>
public sealed record PttSettings
{
    public string? DeviceGuid { get; init; }
    public string? DeviceName { get; init; }
    public int Button { get; init; }
}

/// <summary>The DCS hook that reports the multiplayer server (for server profiles).</summary>
public sealed record DcsHookSettings
{
    /// <summary>UDP port on 127.0.0.1 the hook sends to. Changing it needs the hook installed again.</summary>
    public int Port { get; init; } = DcsHookListener.DefaultPort;
}

public sealed record CueSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>WAV paths; relative paths resolve against the app folder. Empty uses the bundled cues.</summary>
    public string? Accepted { get; init; }
    public string? NoMatch { get; init; }
    public string? Refused { get; init; }
}

/// <summary>All app settings, stored as %LOCALAPPDATA%\DcsVoiceCom\settings.json. Every path is configurable.</summary>
public sealed record AppSettings
{
    /// <summary>Recognition language: "en" (default), "de", or another language Whistle knows.</summary>
    public string Language { get; init; } = "en";
    public string DcsBiosJsonPath { get; init; } = DefinitionLoader.DefaultJsonDirectory;
    public string ProfilesPath { get; init; } = ProfileStore.DefaultDirectory;
    public DcsBiosEndpoints DcsBios { get; init; } = new();
    public DcsHookSettings DcsHook { get; init; } = new();
    public SpeechSettings Speech { get; init; } = new();
    public PttSettings Ptt { get; init; } = new();
    public CueSettings Cues { get; init; } = new();
    public int KeyHoldMs { get; init; } = 50;

    /// <summary>Pinned profile; null follows the aircraft DCS-BIOS reports ("Auto").</summary>
    public string? ProfileOverride { get; init; }

    /// <summary>Pinned server profile (server-…); null follows the server the DCS hook reports ("Auto").</summary>
    public string? ServerProfileOverride { get; init; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcsVoiceCom");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();
        var json = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })?.AsObject();
        if (json is null) return new AppSettings();
        MigrateSidecar(json);
        return json.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
    }

    /// <summary>
    /// Settings written while speech ran in the Python sidecar keep their microphone and model folder: "sidecar"
    /// becomes "speech" (its Python, port and process entries no longer mean anything).
    /// </summary>
    private static void MigrateSidecar(JsonObject json)
    {
        var sidecar = json.FirstOrDefault(p => p.Key.Equals("sidecar", StringComparison.OrdinalIgnoreCase));
        if (sidecar.Value is not JsonObject old) return;
        json.Remove(sidecar.Key);
        if (json.Any(p => p.Key.Equals("speech", StringComparison.OrdinalIgnoreCase))) return;
        var speech = new JsonObject();
        foreach (var name in new[] { "device", "modelDirectory" })
        {
            var value = old.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null) speech[name] = value.DeepClone();
        }
        json["speech"] = speech;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
