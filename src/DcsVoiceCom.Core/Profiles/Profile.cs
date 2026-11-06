using System.Text.Json.Serialization;

namespace DcsVoiceCom.Profiles;

/// <summary>
/// One aircraft's commands, stored as &lt;_ACFT_NAME&gt;.json so it can be shared as-is.
/// common.json uses the same format and is merged under every aircraft profile.
/// </summary>
public sealed class Profile
{
    public const int DefaultThreshold = 85;
    public const int DefaultCriticalThreshold = 95;

    /// <summary>The DCS-BIOS _ACFT_NAME this profile is for ("common" for the shared file).</summary>
    public string Aircraft { get; set; } = "";

    /// <summary>DCS-BIOS version the control IDs were checked against.</summary>
    public string? ModuleVersion { get; set; }

    public int Threshold { get; set; } = DefaultThreshold;
    public int CriticalThreshold { get; set; } = DefaultCriticalThreshold;

    /// <summary>Domain words for the recognizer: add only what it gets wrong.</summary>
    public List<string> Keywords { get; set; } = [];

    public List<Command> Commands { get; set; } = [];
}

public sealed class Command
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Phrase specs with VoiceAttack-style alternatives and {slots}: "[gear;landing gear] up".</summary>
    public List<string> Phrases { get; set; } = [];

    public List<Step> Steps { get; set; } = [];

    /// <summary>Critical commands need the stricter <see cref="Profile.CriticalThreshold"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Critical { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>True for commands generated from DCS-BIOS definitions (they start disabled).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Seeded { get; set; }
}
