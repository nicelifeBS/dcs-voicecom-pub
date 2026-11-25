using System.Globalization;
using System.Text.RegularExpressions;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Engine;

/// <summary>Turns a raw cockpit value into speakable text with a <see cref="TtsValue"/>, and reads it from the live state.</summary>
public static partial class TtsValueFormat
{
    [GeneratedRegex(@"\{(\d+)(?:-(\d+))?\}")]
    private static partial Regex PieceRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    /// <summary>A valid {name}: a letter or _, then letters, digits or _.</summary>
    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    /// <summary>
    /// The text to speak. <paramref name="raw"/> is the integer as digits, or the display string;
    /// <paramref name="isInteger"/> says which (only integers are padded or remapped).
    /// </summary>
    /// <exception cref="StepException">The value doesn't fit the setup (a slice past the end, a remap of text).</exception>
    public static string Format(TtsValue value, string raw, bool isInteger)
    {
        return value.Mode == TtsValueMode.Remap ? Remap(value, raw) : Slice(value, Padded(value, raw, isInteger));
    }

    /// <summary>The characters a slice template picks from: the integer padded with zeros, or the string as it is.</summary>
    public static string Padded(TtsValue value, string raw, bool isInteger) =>
        isInteger && value.Mode == TtsValueMode.Slice && value.Pad > 0 ? raw.PadLeft(value.Pad, '0') : raw;

    private static string Slice(TtsValue value, string text)
    {
        var template = value.Template ?? "";
        return PieceRegex().Replace(template, m =>
        {
            var from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : from;
            if (to < from) throw new StepException($"{m.Value} ends before it starts");
            if (to >= text.Length) throw new StepException($"{m.Value} is past the end of \"{text}\" ({text.Length} characters)");
            return text.Substring(from, to - from + 1);
        });
    }

    private static string Remap(TtsValue value, string raw)
    {
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            throw new StepException($"Remap needs a number, got \"{raw.Trim()}\"");
        var (inMin, inMax, outMin, outMax) = Ranges(value);
        if (inMin == inMax) throw new StepException("Remap: the cockpit range needs two different numbers");
        var y = outMin + (x - inMin) * (outMax - outMin) / (inMax - inMin);
        y = Math.Clamp(y, Math.Min(outMin, outMax), Math.Max(outMin, outMax));
        var decimals = Math.Clamp(value.Decimals, 0, 2);
        var text = Math.Round(y, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);
        return text.StartsWith('-') && text.Trim('-', '0', '.').Length == 0 ? text[1..] : text; // no "-0"
    }

    public static (double InMin, double InMax, double OutMin, double OutMax) Ranges(TtsValue value) =>
        (value.InMin ?? 0, value.InMax ?? TtsValue.DefaultInMax, value.OutMin ?? 0, value.OutMax ?? TtsValue.DefaultOutMax);

    /// <summary>Setup mistakes that show without a cockpit value: name, output, template, ranges.</summary>
    public static IReadOnlyList<string> Validate(TtsValue value)
    {
        var problems = new List<string>();
        if (!IsValidName(value.Name)) problems.Add("the name needs letters, digits or _ (no spaces), starting with a letter");
        if (value.Output.Trim().Length == 0) problems.Add("pick an output");
        if (value.Mode == TtsValueMode.Slice)
        {
            if (string.IsNullOrWhiteSpace(value.Template)) problems.Add("the template is empty: pick characters and press Add");
            foreach (Match m in PieceRegex().Matches(value.Template ?? ""))
            {
                if (m.Groups[2].Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                    problems.Add($"{m.Value} ends before it starts");
            }
            if (value.Pad is < 0 or > 10) problems.Add("pad must be 0-10 digits");
        }
        else
        {
            var (inMin, inMax, _, _) = Ranges(value);
            if (inMin == inMax) problems.Add("the cockpit range needs two different numbers");
            if (value.Decimals is < 0 or > 2) problems.Add("decimals must be 0-2");
        }
        return problems;
    }

    /// <summary>
    /// Reads the value's output and formats it. False with <paramref name="why"/> when there is no DCS data,
    /// the output is unknown, or the value doesn't fit the setup.
    /// </summary>
    public static bool TryRead(TtsValue value, IStateReader state, out string text, out string why)
    {
        text = "";
        if (!state.IsLive)
        {
            why = "no DCS data";
            return false;
        }
        string raw;
        bool isInteger;
        if (state.TryReadInteger(value.Output, out var number))
        {
            raw = number.ToString(CultureInfo.InvariantCulture);
            isInteger = true;
        }
        else if (value.Mode == TtsValueMode.Slice && state.TryReadString(value.Output, out var str))
        {
            raw = str;
            isInteger = false;
        }
        else
        {
            why = $"cannot read {value.Output}";
            return false;
        }
        try
        {
            text = Format(value, raw, isInteger);
            why = "";
            return true;
        }
        catch (StepException ex)
        {
            why = ex.Message;
            return false;
        }
    }
}
