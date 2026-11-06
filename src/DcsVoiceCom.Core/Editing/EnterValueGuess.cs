using System.Text.RegularExpressions;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>
/// "Fill from radio…" and "Fill from keypad…": guesses an <see cref="EnterValueStep"/>'s wheels or keys from
/// DCS-BIOS control names. Only a starting point; the user checks it with live values.
/// </summary>
public static partial class EnterValueGuess
{
    [GeneratedRegex(@"^(?<prefix>.+)_(?<digits>\d+)_DIAL$", RegexOptions.IgnoreCase)]
    private static partial Regex WheelName();

    /// <summary>Prefixes with two or more digit wheels, e.g. "PLT_UHF1" for PLT_UHF1_110_DIAL, PLT_UHF1_1_DIAL….</summary>
    public static IReadOnlyList<string> RadioPrefixes(ControlCatalog catalog) =>
        catalog.Controls
            .Select(c => WheelName().Match(c.Identifier))
            .Where(m => m.Success && PlaceOf(m.Groups["digits"].Value) is not null)
            .GroupBy(m => m.Groups["prefix"].Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The wheels of a radio, biggest place first, with mode and read-back guessed.</summary>
    public static List<Wheel> Wheels(ControlCatalog catalog, string prefix)
    {
        var wheels = new List<(Wheel Wheel, ControlDefinition Control)>();
        foreach (var control in catalog.Controls)
        {
            var m = WheelName().Match(control.Identifier);
            if (!m.Success || !string.Equals(m.Groups["prefix"].Value, prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (PlaceOf(m.Groups["digits"].Value) is not { } place) continue;
            wheels.Add((new Wheel { Control = control.Identifier, Place = place.Place, Width = place.Width, Mode = ModeOf(control, place.Width) }, control));
        }
        wheels.Sort((a, b) => b.Wheel.Place.CompareTo(a.Wheel.Place));

        var readBacks = ReadBackCandidates(catalog, prefix, wheels.Select(w => w.Control.Category)).ToList();
        foreach (var (wheel, _) in wheels.Where(w => w.Wheel.Mode != WheelMode.Set)) AssignReadBack(wheel, readBacks);
        return wheels.Select(w => w.Wheel).ToList();
    }

    /// <summary>Radios with a preset channel selector: "PLT_UHF1" for PLT_UHF1_PRESETS (it needs INC/DEC).</summary>
    public static IReadOnlyList<string> PresetPrefixes(ControlCatalog catalog) =>
        catalog.Controls
            .Where(c => c.Identifier.EndsWith("_PRESETS", StringComparison.OrdinalIgnoreCase) && c.FindInput(InputInterface.FixedStep) != null)
            .Select(c => c.Identifier[..^"_PRESETS".Length])
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The preset selector as one two-digit "wheel" in step mode (INC/DEC), read back from the radio's display,
    /// which shows the channel number in preset mode.
    /// </summary>
    public static Wheel? PresetWheel(ControlCatalog catalog, string prefix)
    {
        var id = prefix + "_PRESETS";
        if (catalog.Find(id) is null) return null;
        return new Wheel { Control = id, Place = 1, Width = 2, Mode = WheelMode.Step, Read = Display(catalog, prefix, [id]), ReadScale = 1 };
    }

    /// <summary>A string output next to the wheels or keys, preferring names with DISP or LINE (not REMOTE).</summary>
    public static string? Display(ControlCatalog catalog, string prefix, IEnumerable<string> controls)
    {
        var categories = controls.Select(id => catalog.Find(id)?.Category).Where(c => c != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = Stem(prefix);
        var candidates = catalog.Controls
            .Where(c => c.PrimaryStringOutput != null && (categories.Contains(c.Category) || c.Identifier.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.Identifier.Contains("DISP", StringComparison.OrdinalIgnoreCase) || c.Identifier.Contains("LINE", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Identifier.Contains("REMOTE", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault()?.Identifier;
    }

    /// <summary>Prefixes with ten or more one-character keys, e.g. "CDU" for CDU_A, CDU_1….</summary>
    public static IReadOnlyList<string> KeypadPrefixes(ControlCatalog catalog) =>
        catalog.Controls
            .Where(c => c.Inputs.Count > 0)
            .Select(c => (c.Identifier, Cut: c.Identifier.LastIndexOf('_')))
            .Where(x => x.Cut > 0 && x.Identifier.Length - x.Cut - 1 == 1 && char.IsLetterOrDigit(x.Identifier[^1]))
            .GroupBy(x => x.Identifier[..x.Cut], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 10)
            .Select(g => g.Key)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POINT"] = ".", ["DOT"] = ".", ["DECIMAL"] = ".", ["DP"] = ".",
        ["SLASH"] = "/", ["DASH"] = "-", ["MINUS"] = "-", ["NEG"] = "-",
        ["SPC"] = " ", ["SPACE"] = " ", ["SP"] = " ",
    };

    /// <summary>Character → key for a keypad, plus its clear key (CLR) for "before".</summary>
    public static (Dictionary<string, string> Keys, string? Clear) Keys(ControlCatalog catalog, string prefix)
    {
        var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? clear = null;
        foreach (var control in catalog.Controls.Where(c => c.Inputs.Count > 0))
        {
            var id = control.Identifier;
            if (!id.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)) continue;
            var name = id[(prefix.Length + 1)..];
            if (name.Length == 1 && char.IsLetterOrDigit(name[0])) keys[name.ToUpperInvariant()] = id;
            else if (NamedKeys.TryGetValue(name, out var ch)) keys.TryAdd(ch, id);
            else if (name.Equals("CLR", StringComparison.OrdinalIgnoreCase) || name.Equals("CLEAR", StringComparison.OrdinalIgnoreCase)) clear ??= id;
        }
        keys.TryAdd(" ", "");
        return (new Dictionary<string, string>(keys), clear);
    }

    /// <summary>
    /// What a wheel's digits in its name mean: "110" the 100 and 10 MHz pair, "1"/"10"/"100" that place,
    /// "01" 0.1, "001" 0.01, "025" the 25 kHz pair (0.001, two digits).
    /// </summary>
    public static (decimal Place, int Width)? PlaceOf(string digits)
    {
        if (digits.Length == 0 || !digits.All(char.IsDigit)) return null;
        if (digits == "110") return (10, 2);
        if (digits[0] == '0' && digits.Length >= 2)
        {
            var significant = digits.TrimStart('0');
            if (significant.Length == 0) return null;
            if (significant.Length >= 2) return (Pow10(-digits.Length), significant.Length); // "025" → 0.001 × 2 digits
            return (Pow10(-(digits.Length - 1)), 1); // "01" → 0.1, "001" → 0.01
        }
        if (digits.TrimStart('1').Trim('0').Length == 0 && digits[0] == '1' && digits.Count(c => c == '1') == 1)
            return (Pow10(digits.Length - 1), 1); // "1", "10", "100"
        return null;
    }

    /// <summary>
    /// Set mode only when set_state takes exactly the wheel's digits (max 9 for one digit, 99 for two).
    /// Any other max is a position index (0-17, 0-3…), so the wheel is clicked or stepped instead.
    /// </summary>
    private static WheelMode ModeOf(ControlDefinition control, int width)
    {
        var set = control.FindInput(InputInterface.SetState);
        if (set?.MaxValue is { } max && max == (int)Math.Pow(10, width) - 1) return WheelMode.Set;
        if (set?.MaxValue is <= 2) return WheelMode.Click; // a spring switch: down / rest / up
        return control.FindInput(InputInterface.FixedStep) != null || control.FindInput(InputInterface.VariableStep) != null ? WheelMode.Step : WheelMode.Click;
    }

    /// <summary>Output-only integers in the wheels' panel, or named like the radio ("PLT_UHF" for "PLT_UHF1").</summary>
    private static IEnumerable<(string Id, int Max)> ReadBackCandidates(ControlCatalog catalog, string prefix, IEnumerable<string> categories)
    {
        var cats = categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = Stem(prefix);
        return catalog.Controls
            .Where(c => c.Inputs.Count == 0 && c.PrimaryIntegerOutput is { MaxValue: > 0 }
                        && (cats.Contains(c.Category) || c.Identifier.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c.Identifier, c.PrimaryIntegerOutput!.MaxValue!.Value));
    }

    /// <summary>
    /// Whole-number wheels read the output with the biggest range (the MHz part, scale 1). A fractional wheel
    /// reads an output whose range fits its digits (max 9-10 for one digit, 99-100 for two), scaled by its place.
    /// </summary>
    private static void AssignReadBack(Wheel wheel, List<(string Id, int Max)> candidates)
    {
        if (candidates.Count == 0) return;
        if (wheel.Place >= 1)
        {
            var whole = candidates.Where(c => c.Max >= 100).OrderByDescending(c => c.Max).FirstOrDefault();
            if (whole.Id != null) (wheel.Read, wheel.ReadScale) = (whole.Id, 1);
            return;
        }
        var top = (int)Math.Pow(10, wheel.Width);
        var fit = candidates.FirstOrDefault(c => c.Max >= top - 1 && c.Max <= top);
        if (fit.Id != null) (wheel.Read, wheel.ReadScale) = (fit.Id, wheel.Place);
    }

    /// <summary>"PLT_UHF1" → "PLT_UHF": the radio's name without its number.</summary>
    private static string Stem(string prefix) => prefix.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++) value = exponent > 0 ? value * 10 : value / 10;
        return value;
    }
}
