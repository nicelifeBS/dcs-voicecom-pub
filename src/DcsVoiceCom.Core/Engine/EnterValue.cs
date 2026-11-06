using System.Globalization;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Engine;

/// <summary>The pure parts of <see cref="EnterValueStep"/>: digit math, key lookup and display checks. Shared by the engine and the editor's preview.</summary>
public static class EnterValue
{
    /// <summary>"251.075" or "251,075" as an exact decimal.</summary>
    public static bool TryParseNumber(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);

    /// <summary>A display's text as a number: "12", " 5", "251.075", "251,07 " → true; blank or "--" → false.</summary>
    public static bool TryParseDisplay(string text, out decimal value)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        value = 0;
        return compact.Length > 0 && compact.Any(char.IsDigit) && TryParseNumber(compact, out value);
    }

    /// <summary>The <paramref name="width"/> digits of <paramref name="value"/> ending at <paramref name="place"/>: 251.075 at 10 width 2 → 25, at 0.001 width 2 → 75.</summary>
    public static int Digits(decimal value, decimal place, int width)
    {
        if (place <= 0) throw new StepException($"Wheel place must be above 0, got {place.ToString(CultureInfo.InvariantCulture)}");
        if (width < 1 || width > 6) throw new StepException($"Wheel width must be 1-6, got {width}");
        var modulus = (decimal)Math.Pow(10, width);
        var units = decimal.Floor(Math.Abs(value) / place);
        return (int)(units % modulus);
    }

    /// <summary>
    /// The lowest and highest digits a wheel can show. The top wheel is limited by the step's min/max
    /// (22-39 for the F-14 100/10 MHz wheel, 1-20 for a preset selector); other wheels go 0 to 9…9.
    /// </summary>
    public static (int Low, int High) Range(EnterValueStep step, Wheel wheel)
    {
        var full = (0, (int)Math.Pow(10, wheel.Width) - 1);
        if (wheel.Place <= 0 || wheel.Width is < 1 or > 6 || step.Max is not { } max || max < 0) return full;
        var top = (step.Wheels ?? []).Where(w => w.Place > 0).Select(w => w.Place).DefaultIfEmpty(wheel.Place).Max();
        if (wheel.Place < top || decimal.Floor(max / wheel.Place) > full.Item2) return full;
        var high = Digits(max, wheel.Place, wheel.Width);
        var low = step.Min is { } min && min >= 0 ? Digits(min, wheel.Place, wheel.Width) : 0;
        return low < high ? (low, high) : full;
    }

    /// <summary>
    /// Which way a wheel turned: +1 up, -1 down, 0 not at all. A jump over more than half its range means it
    /// wrapped around the end (39 → 22 is one click up), so the sign flips.
    /// </summary>
    public static int TurnDirection(int before, int after, (int Low, int High) range)
    {
        var moved = after - before;
        if (moved == 0) return 0;
        return Math.Abs(moved) * 2 > range.High - range.Low ? -Math.Sign(moved) : Math.Sign(moved);
    }

    public static string Format(int digits, int width) => digits.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    /// <summary>The number to enter, or why it can't be entered (not a number, outside min/max).</summary>
    public static string? CheckNumber(EnterValueStep step, string text, out decimal value)
    {
        if (!TryParseNumber(text, out value)) return $"\"{text}\" is not a number";
        var shown = value.ToString(CultureInfo.InvariantCulture);
        if (step.Min is { } min && value < min || step.Max is { } max && value > max)
            return $"{shown} is outside {step.Min?.ToString(CultureInfo.InvariantCulture) ?? "…"}-{step.Max?.ToString(CultureInfo.InvariantCulture) ?? "…"}";
        return null;
    }

    /// <summary>The key control for a character, matched case-insensitively; null when it has none, "" when it is skipped.</summary>
    public static string? KeyFor(EnterValueStep step, char ch)
    {
        if (step.Keys is null) return null;
        foreach (var (key, control) in step.Keys)
        {
            if (string.Equals(key, ch.ToString(), StringComparison.OrdinalIgnoreCase)) return control.Trim();
        }
        return null;
    }

    /// <summary>The characters of <paramref name="text"/> without a key, in order of first use.</summary>
    public static IReadOnlyList<char> MissingKeys(EnterValueStep step, string text) =>
        text.Where(ch => KeyFor(step, ch) is null).Distinct().ToList();

    /// <summary>The key presses for <paramref name="text"/>: before keys, one per character (skipped ones left out), after keys.</summary>
    public static IReadOnlyList<string> KeyPresses(EnterValueStep step, string text)
    {
        var presses = new List<string>();
        presses.AddRange((step.Before ?? []).Where(k => k.Trim().Length > 0).Select(k => k.Trim()));
        foreach (var ch in text)
        {
            if (KeyFor(step, ch) is { Length: > 0 } control) presses.Add(control);
        }
        presses.AddRange((step.After ?? []).Where(k => k.Trim().Length > 0).Select(k => k.Trim()));
        return presses;
    }

    /// <summary>
    /// True when a display shows the value, on its own or as a whole word among other text: numbers as numbers
    /// ("251.000" = "251.0", ".    127" has 127), text case-insensitive ("NN552~      " has "nn552").
    /// A part of a longer word or number doesn't count: "AB1" doesn't show "AB", "251.325" doesn't show 251.
    /// </summary>
    public static bool DisplayMatches(string display, string expected)
    {
        var shown = display.Trim();
        var wanted = expected.Trim();
        if (wanted.Length == 0) return shown.Length == 0;
        var wantedIsNumber = TryParseNumber(wanted, out var number);
        if (wantedIsNumber && TryParseNumber(shown, out var a)) return a == number;
        if (string.Equals(shown, wanted, StringComparison.OrdinalIgnoreCase)) return true;

        if (wantedIsNumber)
        {
            // Any number on the display: "COM1 251.300 AM" shows 251.3.
            foreach (System.Text.RegularExpressions.Match m in NumberToken.Matches(shown))
            {
                if (TryParseNumber(m.Value, out var found) && found == number) return true;
            }
            return false;
        }
        for (var at = shown.IndexOf(wanted, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = shown.IndexOf(wanted, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!JoinsWord(shown, at - 1, -1) && !JoinsWord(shown, at + wanted.Length, +1)) return true;
        }
        return false;
    }

    /// <summary>"251.300", "127", "12,5": a number with an optional decimal part, not part of a longer word.</summary>
    private static readonly System.Text.RegularExpressions.Regex NumberToken =
        new(@"(?<![\p{L}\d.,])\d+(?:[.,]\d+)?(?![\p{L}\d]|[.,]\d)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when the character at <paramref name="i"/> continues the word: a letter or digit, or a decimal point before/after a digit.</summary>
    private static bool JoinsWord(string text, int i, int direction)
    {
        if (i < 0 || i >= text.Length) return false;
        var c = text[i];
        if (char.IsLetterOrDigit(c)) return true;
        var next = i + direction;
        return c is '.' or ',' && next >= 0 && next < text.Length && char.IsDigit(text[next]);
    }

    /// <summary>A short description for the step list: "enter {freq} on 4 wheels, check PLT_UHF_DISP".</summary>
    public static string Describe(EnterValueStep step)
    {
        var check = step.Verify is { Output.Length: > 0 } v ? $", check {v.Output}" : "";
        if (step.Fields is { Count: > 0 } fields)
        {
            check = step.Display is { Length: > 0 } d ? $", check {d}" : "";
            if (step.Method == EnterMethod.Keypad) return $"type {step.Value} on keypad{check}";
            var wheels = fields.Count(f => f.Kind == FieldKind.Wheel);
            return $"enter {step.Value} on {wheels} wheel{(wheels == 1 ? "" : "s")}{check}";
        }
        if (step.Method == EnterMethod.Keypad) return $"type {step.Value} on keypad{check}";
        var count = step.Wheels?.Count ?? 0;
        return $"enter {step.Value} on {count} wheel{(count == 1 ? "" : "s")}{check}";
    }
}
