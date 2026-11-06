using System.Text.RegularExpressions;

namespace DcsVoiceCom.Phrases;

public enum SlotKind { Number, Integer, Range, Choice, Text }

/// <summary>
/// A typed slot in a phrase. The name sets the type: {number} (whole number), {freq} (number with decimals) and
/// {text} (words joined without spaces, NATO letters and number words spelled: "tango golf tango two" → "TGT2").
/// Any other name needs a type after a colon: {n:number}, {f:freq}, {name:text}, {channel:1-126} (whole number
/// in a range) or {band:X|Y} (one of the listed options). An option can carry the value steps receive:
/// {band:X=0|Y=1} is spoken "x-ray" but yields "0".
/// </summary>
public sealed record SlotSpec(string Name, SlotKind Kind, int Min = 0, int Max = 0, IReadOnlyList<string>? Options = null,
    IReadOnlyDictionary<string, string>? OptionValues = null)
{
    public string ValueOf(string option) => OptionValues?.GetValueOrDefault(option) ?? option;

    public static SlotSpec Parse(string body)
    {
        var colon = body.IndexOf(':');
        var name = (colon < 0 ? body : body[..colon]).Trim();
        if (name.Length == 0) throw new FormatException($"Slot without a name: {{{body}}}");
        if (colon < 0)
        {
            return TypeOf(name) is { } kind
                ? new SlotSpec(name, kind)
                : throw new FormatException(
                    $"{{{name}}} has no type. Use {{number}}, {{freq}} or {{text}}, or name a type: {{{name}:number}}, {{{name}:freq}}, {{{name}:text}}, {{{name}:1-20}} or {{{name}:A|B}}");
        }

        var spec = body[(colon + 1)..].Trim();
        if (TypeOf(spec) is { } named) return new SlotSpec(name, named);
        var range = Regex.Match(spec, @"^(-?\d+)\s*-\s*(-?\d+)$");
        if (range.Success)
        {
            var min = int.Parse(range.Groups[1].Value);
            var max = int.Parse(range.Groups[2].Value);
            if (min > max) throw new FormatException($"Slot range {spec} is reversed");
            return new SlotSpec(name, SlotKind.Range, min, max);
        }
        var labels = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = option.IndexOf('=');
            var label = eq < 0 ? option : option[..eq].Trim();
            if (label.Length == 0) throw new FormatException($"Slot {{{body}}} has an option without a name");
            labels.Add(label);
            if (eq >= 0) values[label] = option[(eq + 1)..].Trim();
        }
        if (labels.Count == 0) throw new FormatException($"Slot {{{body}}} has an empty spec");
        return new SlotSpec(name, SlotKind.Choice, Options: labels, OptionValues: values);
    }

    /// <summary>The kind a type word names: "number", "freq" or "text"; null for anything else.</summary>
    private static SlotKind? TypeOf(string word) => word.ToLowerInvariant() switch
    {
        "number" => SlotKind.Integer,
        "freq" => SlotKind.Number,
        "text" => SlotKind.Text,
        _ => null,
    };
}

public abstract record PhraseSegment;
public sealed record LiteralSegment(string Word) : PhraseSegment;
public sealed record SlotSegment(SlotSpec Slot) : PhraseSegment;

/// <summary>A normalized phrase split into literal words and slots.</summary>
public sealed class PhraseTemplate
{
    private static readonly Regex SlotRegex = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    private PhraseTemplate(string text, IReadOnlyList<PhraseSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    /// <summary>The normalized phrase as the matcher loads it and returns it on a match.</summary>
    public string Text { get; }

    public IReadOnlyList<PhraseSegment> Segments { get; }

    public IEnumerable<SlotSpec> Slots => Segments.OfType<SlotSegment>().Select(s => s.Slot);

    public bool HasSlots => Segments.Any(s => s is SlotSegment);

    public static PhraseTemplate Parse(string phrase)
    {
        var text = PhraseText.Normalize(phrase);
        var segments = new List<PhraseSegment>();
        var last = 0;
        foreach (Match match in SlotRegex.Matches(text))
        {
            AddLiterals(text[last..match.Index], segments);
            segments.Add(new SlotSegment(SlotSpec.Parse(match.Groups[1].Value)));
            last = match.Index + match.Length;
        }
        AddLiterals(text[last..], segments);

        var names = segments.OfType<SlotSegment>().Select(s => s.Slot.Name).ToList();
        if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new FormatException($"Duplicate slot name in \"{phrase}\"");
        return new PhraseTemplate(text, segments);
    }

    private static void AddLiterals(string text, List<PhraseSegment> segments)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            segments.Add(new LiteralSegment(word));
    }

    public override string ToString() => Text;
}
