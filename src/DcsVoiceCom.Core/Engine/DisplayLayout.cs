using System.Globalization;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Engine;

/// <summary>What a wheel field can show: a span of numbers ("22-39") or a list in click order ("X,Y", "00,25,50,75").</summary>
public sealed class ValueRange
{
    private ValueRange(int low, int high, IReadOnlyList<string>? items)
    {
        Low = low;
        High = high;
        Items = items;
    }

    public int Low { get; }
    public int High { get; }

    /// <summary>The list, in click order; null for a span of numbers.</summary>
    public IReadOnlyList<string>? Items { get; }

    public static ValueRange Span(int low, int high) => new(low, high, null);
    public static ValueRange List(IReadOnlyList<string> items) => new(0, items.Count - 1, items);

    /// <summary>A field's position in the range: its number, or its index in the list; null when it shows something else.</summary>
    public int? IndexOf(string text)
    {
        var shown = text.Trim();
        if (Items is null)
        {
            if (shown.Length == 0) return Low == 0 ? 0 : null; // a display that hides leading zeros
            return int.TryParse(shown, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= Low && n <= High ? n : null;
        }
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i], shown, StringComparison.OrdinalIgnoreCase)) return i;
        }
        // "0" on a display that the list calls "00".
        if (int.TryParse(shown, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            for (var i = 0; i < Items.Count; i++)
            {
                if (int.TryParse(Items[i], NumberStyles.None, CultureInfo.InvariantCulture, out var item) && item == number) return i;
            }
        }
        return null;
    }

    /// <summary>What a position looks like in messages: the number, or the list item.</summary>
    public string Show(int index) => Items is null
        ? index.ToString(CultureInfo.InvariantCulture)
        : index >= 0 && index < Items.Count ? Items[index] : index.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Items is null ? $"{Low}-{High}" : string.Join(",", Items);
}

/// <summary>The text one field of the display must show.</summary>
public sealed record FieldTarget(DisplayField Field, string Text);

/// <summary>
/// The pure parts of an <see cref="EnterValueStep"/> with <see cref="EnterValueStep.Fields"/>: where the value goes
/// on the display, what each field must show, and whether the layout is valid. Shared by the engine and the editor.
/// </summary>
public static class DisplayLayout
{
    public const int DefaultTimeoutMs = 1500;

    /// <summary>"0-9", "22-39", "X,Y", "00,25,50,75" → a range; blank or not readable → null.</summary>
    public static ValueRange? ParseRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Contains(','))
        {
            var items = trimmed.Split(',').Select(i => i.Trim()).ToList();
            return items.Count >= 2 && items.All(i => i.Length > 0) && items.Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count
                ? ValueRange.List(items)
                : null;
        }
        var dash = trimmed.IndexOf('-', 1);
        if (dash > 0
            && int.TryParse(trimmed[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var low)
            && int.TryParse(trimmed[(dash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var high)
            && low < high)
        {
            return ValueRange.Span(low, high);
        }
        return null;
    }

    /// <summary>A wheel field's range, or 0 to 9…9 for its width when none is set.</summary>
    public static ValueRange RangeOf(DisplayField field) =>
        ParseRange(field.Range) ?? ValueRange.Span(0, (int)Math.Pow(10, Math.Clamp(field.Len, 1, 6)) - 1);

    /// <summary>The characters of <paramref name="display"/> in <paramref name="field"/>, padded with spaces past its end.</summary>
    public static string Read(string display, DisplayField field)
    {
        var padded = display.Length < field.At + field.Len ? display.PadRight(field.At + field.Len) : display;
        return field.At < 0 ? "" : padded.Substring(field.At, field.Len);
    }

    /// <summary>True when the field shows the target: wheels by position in their range ("09" = " 9"), keypad text ignoring case and padding.</summary>
    public static bool Shows(string display, FieldTarget target)
    {
        var shown = Read(display, target.Field);
        if (target.Field.Kind == FieldKind.Wheel)
        {
            var range = RangeOf(target.Field);
            var want = range.IndexOf(target.Text);
            return want is not null && range.IndexOf(shown) == want;
        }
        return string.Equals(shown.Trim(), target.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShowsAll(string display, IEnumerable<FieldTarget> targets) => targets.All(t => Shows(display, t));

    /// <summary>
    /// What every wheel or keypad field must show for <paramref name="value"/>, or why the value can't be entered
    /// (<paramref name="problem"/>): too long, more decimals than the display, outside a wheel's range.
    /// </summary>
    public static IReadOnlyList<FieldTarget> Targets(EnterValueStep step, string value, out string? problem)
    {
        problem = null;
        var fields = step.Fields ?? [];
        var text = value.Trim().ToUpperInvariant();

        if (step.Method == EnterMethod.Keypad)
        {
            var field = fields.FirstOrDefault(f => f.Kind == FieldKind.Keypad);
            if (field is null)
            {
                problem = "enter_value has no keypad field";
                return [];
            }
            if (text.Length > field.Len)
            {
                problem = $"\"{text}\" is too long for the field ({field.Len} characters)";
                return [];
            }
            var placed = field.Align == TextAlign.Right ? text.PadLeft(field.Len) : text.PadRight(field.Len);
            return [new FieldTarget(field, placed)];
        }

        var wheels = fields.Where(f => f.Kind == FieldKind.Wheel).OrderBy(f => f.At).ToList();
        if (wheels.Count == 0)
        {
            problem = "enter_value has no wheels";
            return [];
        }
        var line = Place(step, wheels, text, out problem);
        if (line is null) return [];

        var targets = new List<FieldTarget>();
        foreach (var wheel in wheels)
        {
            var part = line.Substring(wheel.At, wheel.Len);
            var range = RangeOf(wheel);
            if (range.IndexOf(part) is null)
            {
                var shown = part.Trim().Length == 0 ? "nothing" : $"\"{part.Trim()}\"";
                problem = $"{text}: {wheel.Control} would need {shown}, outside {range}";
                return [];
            }
            targets.Add(new FieldTarget(wheel, part));
        }
        return targets;
    }

    /// <summary>The value laid out over the whole display width (other cells are spaces), or null with a problem.</summary>
    private static string? Place(EnterValueStep step, List<DisplayField> wheels, string text, out string? problem)
    {
        problem = null;
        var sample = step.Sample ?? "";
        var start = wheels.Min(w => w.At);
        var end = wheels.Max(w => w.At + w.Len);
        var width = Math.Max(sample.Length, end);
        var line = new char[width];
        Array.Fill(line, ' ');
        var point = PointOf(sample, start, end);
        var isNumber = EnterValue.TryParseNumber(text, out var number) && number >= 0;

        if (isNumber && point >= 0)
        {
            // Line up the decimal points: 251.325 on "225.025".
            // As many decimals as the display shows after its point, even where no wheel is set up yet.
            var shown = 0;
            while (point + 1 + shown < sample.Length && char.IsDigit(sample[point + 1 + shown])) shown++;
            var decimals = Math.Max(end - point - 1, shown);
            if (point + decimals + 1 > line.Length)
            {
                var wider = new char[point + decimals + 1];
                Array.Fill(wider, ' ');
                line.CopyTo(wider, 0);
                line = wider;
            }
            if (decimal.Round(number, decimals) != number)
            {
                problem = $"{text} has more decimals than the display ({decimals})";
                return null;
            }
            var formatted = number.ToString("F" + decimals, CultureInfo.InvariantCulture);
            var whole = decimals > 0 ? formatted[..formatted.IndexOf('.')] : formatted;
            if (whole.Length > (start < point ? point - start : point))
            {
                problem = $"{text} has too many digits for the display";
                return null;
            }
            for (var i = 0; i < whole.Length; i++) line[point - whole.Length + i] = whole[i];
            line[point] = '.';
            for (var i = 0; i < decimals; i++) line[point + 1 + i] = formatted[formatted.Length - decimals + i];
            return new string(line);
        }

        var digits = text;
        if (isNumber)
        {
            // No decimal point on the display: shift by the implied decimals (251.3 with 1 → "2513").
            var implied = Math.Max(0, step.Decimals ?? 0);
            var scaled = number * (decimal)Math.Pow(10, implied);
            if (decimal.Truncate(scaled) != scaled)
            {
                problem = implied == 0
                    ? $"{text}: the display has no decimal point (set its implied decimals)"
                    : $"{text} has more decimals than the display ({implied})";
                return null;
            }
            digits = decimal.Truncate(scaled).ToString(CultureInfo.InvariantCulture);
        }

        // Right-aligned over the wheels' cells, skipping cells no wheel uses: "73Y" on "109X" → " 73Y".
        var cells = wheels.SelectMany(w => Enumerable.Range(w.At, w.Len)).Distinct().OrderBy(i => i).ToList();
        if (digits.Length > cells.Count)
        {
            problem = $"\"{text}\" is too long for the display ({cells.Count} characters)";
            return null;
        }
        for (var i = 0; i < digits.Length; i++) line[cells[cells.Count - digits.Length + i]] = digits[i];
        return new string(line);
    }

    /// <summary>
    /// The sample's decimal point: the one between the first and last wheel, else the nearest one (while only some
    /// wheels are set up, e.g. just the 100+10 MHz wheel of "251.000"), or -1.
    /// </summary>
    private static int PointOf(string sample, int start, int end)
    {
        var best = -1;
        for (var i = 0; i < sample.Length; i++)
        {
            if (sample[i] != '.') continue;
            if (i >= start && i < end) return i;
            if (best < 0 || Distance(i) < Distance(best)) best = i;
        }
        return best;

        int Distance(int i) => i < start ? start - i : i - end + 1;
    }

    /// <summary>
    /// The value of a wheel field's last digit, for the editor: 10, 1, 0.1, 0.001. Null for a field that is not a
    /// digit (a letter wheel).
    /// </summary>
    public static decimal? PlaceOf(EnterValueStep step, DisplayField field)
    {
        var sample = step.Sample ?? "";
        var wheels = (step.Fields ?? []).Where(f => f.Kind == FieldKind.Wheel).ToList();
        if (!wheels.Contains(field)) wheels.Add(field);
        var start = wheels.Min(w => w.At);
        var end = wheels.Max(w => w.At + w.Len);
        var last = field.At + field.Len - 1;
        if (IsLetterWheel(field)) return null;

        var point = PointOf(sample, start, end);
        int power;
        if (point >= 0)
        {
            power = last < point ? point - 1 - last : point - last;
        }
        else
        {
            // Digit cells to the right of this field; a letter wheel (X/Y) is not a digit.
            var digitsAfter = wheels.Where(w => w.At > last && !IsLetterWheel(w)).Sum(w => w.Len);
            power = digitsAfter - Math.Max(0, step.Decimals ?? 0);
        }
        return power >= 0 ? (decimal)Math.Pow(10, power) : 1m / (decimal)Math.Pow(10, -power);
    }

    private static bool IsLetterWheel(DisplayField field) =>
        ParseRange(field.Range)?.Items is { } items && items.Any(i => !i.All(char.IsDigit));

    /// <summary>What is wrong with the step's layout: overlaps, fields past the end, the wrong kind of field for the method.</summary>
    public static IReadOnlyList<string> Validate(EnterValueStep step)
    {
        var problems = new List<string>();
        var fields = step.Fields ?? [];
        var sample = step.Sample ?? "";
        if (string.IsNullOrWhiteSpace(step.Display)) problems.Add("needs a display");
        if (fields.Count == 0) problems.Add("has no fields");

        foreach (var f in fields)
        {
            var name = Name(f);
            if (f.At < 0 || f.Len < 1) problems.Add($"{name} has no characters");
            else if (sample.Length > 0 && f.At + f.Len > sample.Length) problems.Add($"{name} goes past the end of the display ({sample.Length} characters)");
            if (f.Kind == FieldKind.Wheel)
            {
                if (string.IsNullOrWhiteSpace(f.Control)) problems.Add($"a wheel at {f.At + 1} has no control");
                if (!string.IsNullOrWhiteSpace(f.Range) && ParseRange(f.Range) is null) problems.Add($"{name}: range \"{f.Range}\" is not like 0-9 or X,Y");
                if (f.Len > 6) problems.Add($"{name} is wider than 6 characters");
                if (f.At >= 0 && sample.Length > f.At && Read(sample, f).Contains('.')) problems.Add($"{name} spans the decimal point");
            }
        }

        var ordered = fields.Where(f => f.Len > 0).OrderBy(f => f.At).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].At < ordered[i - 1].At + ordered[i - 1].Len) problems.Add($"{Name(ordered[i])} overlaps {Name(ordered[i - 1])}");
        }

        var wheels = fields.Count(f => f.Kind == FieldKind.Wheel);
        var keypads = fields.Count(f => f.Kind == FieldKind.Keypad);
        if (step.Method == EnterMethod.Keypad)
        {
            if (wheels > 0) problems.Add("a keypad step can't have wheels");
            if (keypads != 1) problems.Add(keypads == 0 ? "has no keypad field" : "can type into only one keypad field (add another step for the next)");
            if (step.Keys is not { Count: > 0 }) problems.Add("has no keys");
        }
        else
        {
            if (keypads > 0) problems.Add("a wheels step can't have a keypad field");
            if (wheels == 0) problems.Add("has no wheels");
        }
        return problems;
    }

    private static string Name(DisplayField f) => f.Kind switch
    {
        FieldKind.Wheel when !string.IsNullOrWhiteSpace(f.Control) => f.Control!,
        FieldKind.Keypad => $"the keypad field at {f.At + 1}",
        FieldKind.ReadOnly => $"the read-only field at {f.At + 1}",
        _ => $"the field at {f.At + 1}",
    };
}
