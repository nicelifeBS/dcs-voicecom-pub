using System.Text.Json.Serialization;

namespace DcsVoiceCom.Profiles;

/// <summary>One step of a command. Serialized with a "type" discriminator (see <see cref="StepJsonConverter"/>).</summary>
public abstract class Step
{
    [JsonIgnore] public abstract string Type { get; }
}

/// <summary>
/// Sends one DCS-BIOS input line: a value, INC, DEC, TOGGLE or "+3200". With <see cref="PressMs"/> set it is a
/// button press instead: <see cref="Arg"/> pushes the button in, and after that many ms <see cref="Release"/> lets it out.
/// </summary>
public sealed class BiosStep : Step
{
    /// <summary>How long a new button press holds the button in.</summary>
    public const int DefaultPressMs = 50;

    /// <summary>The value that lets a button out when <see cref="Release"/> is not set.</summary>
    public const string DefaultRelease = "0";

    public override string Type => "bios";
    public string Control { get; set; } = "";
    public string Arg { get; set; } = "";

    /// <summary>Skip when the control's output already equals a numeric <see cref="Arg"/>. Not used for a press.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SkipIfAlready { get; set; }

    /// <summary>Set: a button press held this long, then released with <see cref="Release"/>. Null: a plain line.</summary>
    public int? PressMs { get; set; }

    /// <summary>A press's release value; null is <see cref="DefaultRelease"/>.</summary>
    public string? Release { get; set; }

    [JsonIgnore] public bool IsPress => PressMs != null;

    [JsonIgnore] public string ReleaseArg => string.IsNullOrWhiteSpace(Release) ? DefaultRelease : Release;
}

/// <summary>Steps a knob with INC/DEC until its output reaches the target, with a step limit.</summary>
public sealed class BiosUntilStep : Step
{
    public override string Type => "bios_until";
    public string Control { get; set; } = "";

    /// <summary>Output to read back; defaults to <see cref="Control"/>.</summary>
    public string? Output { get; set; }

    /// <summary>Target value, may be a placeholder ("{deg}").</summary>
    public string Target { get; set; } = "";

    /// <summary>Raw output units per target unit, e.g. 65535/360 for a heading knob exported as 0-65535.</summary>
    public double Scale { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Tolerance { get; set; }
    public string Inc { get; set; } = "INC";
    public string Dec { get; set; } = "DEC";
    public int MaxSteps { get; set; } = 60;

    /// <summary>Delay after each step before reading back, so the export catches up.</summary>
    public int DelayMs { get; set; } = 80;
}

/// <summary>Presses a key chord such as "RCtrl+RShift+G".</summary>
public sealed class KeyStep : Step
{
    public override string Type => "key";
    public string Keys { get; set; } = "";

    /// <summary>Hold time; null uses the app setting.</summary>
    public int? HoldMs { get; set; }
}

public sealed class WaitStep : Step
{
    public override string Type => "wait";
    public int Ms { get; set; }
}

/// <summary>Waits until a cockpit output satisfies a condition; on timeout the sequence stops with "refused".</summary>
public sealed class WaitForStep : Step
{
    public override string Type => "wait_for";
    public Condition Until { get; set; } = new();
    public int TimeoutMs { get; set; } = 10_000;
}

public sealed class IfStep : Step
{
    public override string Type => "if";
    public Condition Condition { get; set; } = new();
    public List<Step> Then { get; set; } = [];
    public List<Step> Else { get; set; } = [];
}

/// <summary>Plays an audio cue mid-sequence.</summary>
public sealed class CueStep : Step
{
    public override string Type => "cue";
    public Cue Cue { get; set; }
}

/// <summary>
/// Runs its steps once per character of <see cref="Value"/> (usually a spoken parameter),
/// with {char} bound to the character, or to its <see cref="Map"/> entry ("." → "DOT").
/// </summary>
public sealed class ForEachCharStep : Step
{
    public override string Type => "foreach_char";
    public string Value { get; set; } = "";
    public Dictionary<string, string> Map { get; set; } = [];
    public List<Step> Steps { get; set; } = [];
}

/// <summary>How <see cref="EnterValueStep"/> puts the value into the cockpit.</summary>
public enum EnterMethod { Wheels, Keypad }

/// <summary>How a wheel is moved: spring-switch clicks, INC/DEC, or its digit sent directly.</summary>
public enum WheelMode { Click, Step, Set }

/// <summary>What a <see cref="DisplayField"/> is: a wheel or switch, the one keypad field, or text that never changes.</summary>
public enum FieldKind { Wheel, Keypad, ReadOnly }

/// <summary>Where typed text sits in a keypad field.</summary>
public enum TextAlign { Left, Right }

/// <summary>
/// Puts a spoken value into the cockpit. Wheels: the number is split by digit place and each wheel is
/// moved until its read-back shows its digit (the F-14 UHF wheels are spring switches, not digits).
/// Keypad: each character is pressed on its key (a CDU). Optionally checks a display afterwards.
/// </summary>
public sealed class EnterValueStep : Step
{
    public static readonly IReadOnlyList<string> DefaultUp = ["2", "1"];
    public static readonly IReadOnlyList<string> DefaultDown = ["0", "1"];
    public static readonly IReadOnlyList<string> DefaultPress = ["1", "0"];

    public override string Type => "enter_value";

    /// <summary>The value, usually a spoken slot: "{freq}" or "{name}".</summary>
    public string Value { get; set; } = "";
    public EnterMethod Method { get; set; }

    /// <summary>Wheels: the allowed range; a value outside it sends nothing.</summary>
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public List<Wheel>? Wheels { get; set; }

    /// <summary>Wheels in click mode: the arguments of one click up / down (default 2,1 and 0,1).</summary>
    public List<string>? Up { get; set; }
    public List<string>? Down { get; set; }

    /// <summary>Most clicks per wheel before giving up.</summary>
    public int MaxSteps { get; set; } = 40;

    /// <summary>Delay after every line sent, so the cockpit and the export catch up.</summary>
    public int DelayMs { get; set; } = 80;

    /// <summary>
    /// The display that shows the value. With <see cref="Fields"/> it is both the read-back of every wheel and the
    /// check afterwards (it replaces <see cref="Verify"/> and each wheel's <see cref="Wheel.Read"/>).
    /// </summary>
    public string? Display { get; set; }

    /// <summary>The display's text when the step was set up: its length, decimal point and read-only characters.</summary>
    public string? Sample { get; set; }

    /// <summary>Parts of <see cref="Display"/>, by character position. Set: the step uses the display layout, not <see cref="Wheels"/>.</summary>
    public List<DisplayField>? Fields { get; set; }

    /// <summary>Implied decimals of a display without a decimal point: 1 when it shows 2513 for 251.3.</summary>
    public int? Decimals { get; set; }

    /// <summary>How long to wait for <see cref="Display"/> to show the value (default 1500 ms).</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Keypad: keys pressed before / after the text (e.g. CLR, a line select key). Kept for older profiles; the editor no longer offers them.</summary>
    public List<string>? Before { get; set; }
    public List<string>? After { get; set; }

    /// <summary>Keypad: the arguments of one key press (default 1,0).</summary>
    public List<string>? Press { get; set; }

    /// <summary>Keypad: character → key control; an empty control skips the character.</summary>
    public Dictionary<string, string>? Keys { get; set; }

    public Verify? Verify { get; set; }
}

/// <summary>One wheel: which digits of the value it shows and where to read them back.</summary>
public sealed class Wheel
{
    public string Control { get; set; } = "";

    /// <summary>Value of the wheel's last digit: 10 for a 100+10 MHz wheel, 0.1, 0.001 for a 25 kHz wheel.</summary>
    public decimal Place { get; set; } = 1;

    /// <summary>Digits on the wheel: 2 for "25" of 251.000 or "75" of .075.</summary>
    public int Width { get; set; } = 1;

    /// <summary>
    /// Output that shows where the wheel is: an integer, or a display whose text is a number ("12" for a preset channel).
    /// Needed unless <see cref="Mode"/> is set.
    /// </summary>
    public string? Read { get; set; }

    /// <summary>The read-back times this is in the value's units: 0.1 when the output is the 0.1 MHz digit.</summary>
    public decimal ReadScale { get; set; } = 1;

    public WheelMode Mode { get; set; }

    /// <summary>Swap up and down for this wheel (set by the editor's test clicks).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flip { get; set; }
}

/// <summary>
/// One part of an <see cref="EnterValueStep.Display"/>: the characters from <see cref="At"/> (0-based) for
/// <see cref="Len"/>. A wheel field is turned until it shows its part of the value.
/// </summary>
public sealed class DisplayField
{
    public int At { get; set; }
    public int Len { get; set; } = 1;
    public FieldKind Kind { get; set; }

    /// <summary>Wheel: the control to turn.</summary>
    public string? Control { get; set; }

    /// <summary>Wheel: what it can show, in click order: "0-9", "22-39", or a list such as "X,Y" or "00,25,50,75".</summary>
    public string? Range { get; set; }

    /// <summary>Wheel: how it is moved (default click).</summary>
    public WheelMode? Mode { get; set; }

    /// <summary>Wheel: swap up and down (set by the editor's test clicks).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flip { get; set; }

    /// <summary>Keypad: where the text sits in the field (default left).</summary>
    public TextAlign? Align { get; set; }
}

/// <summary>A display to check after entering a value; numbers compare as numbers ("251.000" = "251.0").</summary>
public sealed class Verify
{
    public string Output { get; set; } = "";
    public int TimeoutMs { get; set; } = 1500;
}

/// <summary>Audio cues; written as "accepted", "no_match", "refused" in profiles.</summary>
public enum Cue { Accepted, NoMatch, Refused }

/// <summary>A test on a cockpit output: integers compare numerically, strings with = and != only.</summary>
public sealed class Condition
{
    public string Output { get; set; } = "";
    public string Op { get; set; } = "=";
    public string Value { get; set; } = "";

    public override string ToString() => $"{Output} {Op} {Value}";
}
