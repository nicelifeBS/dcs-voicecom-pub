using System.Text.Json.Serialization;

namespace DcsVoiceCom.Profiles;

/// <summary>How a <see cref="TtsValue"/> turns the raw cockpit value into text.</summary>
public enum TtsValueMode
{
    /// <summary>Picks characters with a template: "{0-1}:{2-3}" turns 2034 (padded to 4) into "20:34".</summary>
    Slice,

    /// <summary>Scales a number linearly, e.g. 0-65535 to 0-100, clamped and rounded.</summary>
    Remap,
}

/// <summary>
/// A live cockpit value a Say step can speak as {name}: read from one DCS-BIOS output and made speakable
/// by slicing its characters or remapping its number. Stored per profile in <see cref="Profile.Values"/>.
/// </summary>
public sealed class TtsValue
{
    public const double DefaultInMax = 65535;
    public const double DefaultOutMax = 100;

    /// <summary>The placeholder name, used as {name} in Say text.</summary>
    public string Name { get; set; } = "";

    /// <summary>The DCS-BIOS control whose output is read.</summary>
    public string Output { get; set; } = "";

    public TtsValueMode Mode { get; set; } = TtsValueMode.Slice;

    /// <summary>Slice: an integer is padded with zeros to this many digits first (509 → 000509). 0 = no padding.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pad { get; set; }

    /// <summary>Slice: {a-b} (0-based, b included) or {a} picks characters; everything else is kept as typed.</summary>
    public string? Template { get; set; }

    /// <summary>Remap: the cockpit range (default 0 to 65535).</summary>
    public double? InMin { get; set; }
    public double? InMax { get; set; }

    /// <summary>Remap: the spoken range (default 0 to 100). The result is clamped to it.</summary>
    public double? OutMin { get; set; }
    public double? OutMax { get; set; }

    /// <summary>Remap: decimals in the spoken number, 0-2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Decimals { get; set; }

    public TtsValue Clone() => (TtsValue)MemberwiseClone();
}
