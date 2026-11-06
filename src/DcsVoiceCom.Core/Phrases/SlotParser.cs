using System.Globalization;

namespace DcsVoiceCom.Phrases;

public sealed record SlotParseResult(bool Success, IReadOnlyDictionary<string, string> Values, string? Error)
{
    public static SlotParseResult Fail(string error) => new(false, new Dictionary<string, string>(), error);
}

/// <summary>
/// Parses the slot words the matcher split off a matched phrase into typed values.
/// Literal words between slots act as separators. An out-of-range or unparseable value fails
/// the whole match, so nothing runs with a wrong value.
/// </summary>
public static class SlotParser
{
    private static readonly Dictionary<string, string[]> Nato = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ["alfa", "alpha"], ["b"] = ["bravo"], ["c"] = ["charlie"], ["d"] = ["delta"], ["e"] = ["echo"],
        ["f"] = ["foxtrot"], ["g"] = ["golf"], ["h"] = ["hotel"], ["i"] = ["india"], ["j"] = ["juliett", "juliet"],
        ["k"] = ["kilo"], ["l"] = ["lima"], ["m"] = ["mike"], ["n"] = ["november"], ["o"] = ["oscar"],
        ["p"] = ["papa"], ["q"] = ["quebec"], ["r"] = ["romeo"], ["s"] = ["sierra"], ["t"] = ["tango"],
        ["u"] = ["uniform"], ["v"] = ["victor"], ["w"] = ["whiskey", "whisky"], ["x"] = ["xray", "x ray"],
        ["y"] = ["yankee"], ["z"] = ["zulu"],
    };

    public static SlotParseResult Parse(PhraseTemplate template, string slotText)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!template.HasSlots) return new SlotParseResult(true, values, null);

        var tokens = PhraseText.Tokenize(slotText);
        var segments = template.Segments;
        var first = segments.ToList().FindIndex(s => s is SlotSegment);
        var last = segments.ToList().FindLastIndex(s => s is SlotSegment);
        var inner = segments.Skip(first).Take(last - first + 1).ToList();

        var pos = 0;
        for (var s = 0; s < inner.Count; s++)
        {
            switch (inner[s])
            {
                case LiteralSegment literal:
                    // Separators are optional: the recognizer may drop short words.
                    if (pos < tokens.Length && tokens[pos] == literal.Word) pos++;
                    break;

                case SlotSegment { Slot.Kind: SlotKind.Choice } choice:
                {
                    var (option, length) = MatchChoice(choice.Slot, tokens, pos);
                    if (option is null) return SlotParseResult.Fail($"\"{Remaining(tokens, pos)}\" is not one of {string.Join("|", choice.Slot.Options!)} for {{{choice.Slot.Name}}}");
                    values[choice.Slot.Name] = choice.Slot.ValueOf(option);
                    pos += length;
                    break;
                }

                case SlotSegment { Slot.Kind: SlotKind.Text } text:
                {
                    var stopWord = s + 1 < inner.Count && inner[s + 1] is LiteralSegment next ? next.Word : null;
                    var (spelled, end) = Spell(tokens, pos, stopWord, anyWord: true);
                    if (s + 1 < inner.Count && inner[s + 1] is SlotSegment { Slot.Kind: SlotKind.Number or SlotKind.Integer or SlotKind.Range })
                    {
                        // A number slot follows: leave the trailing number words to it ("alpha bravo two five" → AB, 25).
                        var cut = end;
                        while (cut > pos && IsPlainNumberWord(tokens[cut - 1])) cut--;
                        if (cut > pos && cut < end) (spelled, end) = Spell(tokens[..cut], pos, stopWord, anyWord: true);
                    }
                    if (spelled.Length == 0) return SlotParseResult.Fail($"no text for {{{text.Slot.Name}}}");
                    values[text.Slot.Name] = spelled;
                    pos = end;
                    break;
                }

                case SlotSegment numeric:
                {
                    var stopWord = s + 1 < inner.Count && inner[s + 1] is LiteralSegment next ? next.Word : null;
                    var start = pos;
                    while (pos < tokens.Length && tokens[pos] != stopWord && NumberParser.IsNumberToken(tokens[pos])) pos++;
                    if (pos == start) return SlotParseResult.Fail($"no number for {{{numeric.Slot.Name}}}");
                    if (!NumberParser.TryParse(tokens[start..pos], out var number))
                        return SlotParseResult.Fail($"\"{string.Join(' ', tokens[start..pos])}\" is not a number for {{{numeric.Slot.Name}}}");

                    if (numeric.Slot.Kind is SlotKind.Range or SlotKind.Integer)
                    {
                        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
                            return SlotParseResult.Fail($"{number} is not a whole number for {{{numeric.Slot.Name}}}");
                        if (numeric.Slot.Kind == SlotKind.Range && (integer < numeric.Slot.Min || integer > numeric.Slot.Max))
                            return SlotParseResult.Fail($"{integer} is outside {numeric.Slot.Min}-{numeric.Slot.Max} for {{{numeric.Slot.Name}}}");
                        number = integer.ToString(CultureInfo.InvariantCulture);
                    }
                    values[numeric.Slot.Name] = number;
                    break;
                }
            }
        }

        if (pos != tokens.Length) return SlotParseResult.Fail($"unexpected words \"{Remaining(tokens, pos)}\"");
        return new SlotParseResult(true, values, null);
    }

    private static readonly Dictionary<string, string> Letters = Nato
        .SelectMany(p => p.Value.Where(w => !w.Contains(' ')).Select(w => (w, p.Key)))
        .Concat([("ray", ""), ("dash", "-"), ("minus", "-"), ("slash", "/"), ("space", " "), ("strich", "/"), ("leerzeichen", " ")])
        .ToDictionary(p => p.Item1, p => p.Item2.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Spoken text the way a {text} slot reads it ("alpha bravo one" → "AB1", "TGT 2" → "TGT2"); null when empty.</summary>
    public static string? SpellText(string spoken)
    {
        var tokens = PhraseText.Tokenize(spoken);
        var (text, end) = Spell(tokens, 0, null, anyWord: true);
        return end == tokens.Length && text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Spelled text from <paramref name="pos"/> up to <paramref name="stopWord"/>: NATO words and single letters
    /// become letters, runs of number words become digits ("four one decimal five" → "41.5").
    /// "x ray" reads as X; "slash", "dash" and "space" give "/", "-" and " ". With <paramref name="anyWord"/>, any
    /// other word is taken as it is, upper-cased ("TGT 2" → "TGT2"); without it, such a word ends the text.
    /// </summary>
    internal static (string Text, int End) Spell(string[] tokens, int pos, string? stopWord, bool anyWord = false)
    {
        var sb = new System.Text.StringBuilder();
        while (pos < tokens.Length && tokens[pos] != stopWord)
        {
            var token = tokens[pos];
            if (token.Length == 1 && char.IsLetter(token[0]))
            {
                sb.Append(char.ToUpperInvariant(token[0]));
                pos++;
                if (token == "x" && pos < tokens.Length && tokens[pos] == "ray") pos++;
                continue;
            }
            if (Letters.TryGetValue(token, out var letter))
            {
                sb.Append(letter);
                pos++;
                continue;
            }
            if (anyWord && !NumberParser.IsNumberToken(token))
            {
                sb.Append(token.ToUpperInvariant());
                pos++;
                continue;
            }
            var start = pos;
            while (pos < tokens.Length && tokens[pos] != stopWord && !Letters.ContainsKey(tokens[pos])
                   && !(tokens[pos].Length == 1 && char.IsLetter(tokens[pos][0])) && NumberParser.IsNumberToken(tokens[pos])) pos++;
            if (pos == start || !NumberParser.TryParse(tokens[start..pos], out var digits)) return (sb.ToString(), start);
            sb.Append(digits);
        }
        return (sb.ToString(), pos);
    }

    private static bool IsPlainNumberWord(string token) =>
        !Letters.ContainsKey(token) && !(token.Length == 1 && char.IsLetter(token[0])) && NumberParser.IsNumberToken(token);

    private static (string? Option, int Length) MatchChoice(SlotSpec slot, string[] tokens, int pos)
    {
        string? best = null;
        var bestLength = 0;
        foreach (var option in slot.Options!)
        {
            foreach (var spoken in SpokenForms(option))
            {
                if (spoken.Length > bestLength && spoken.Length <= tokens.Length - pos
                    && spoken.SequenceEqual(tokens.Skip(pos).Take(spoken.Length), StringComparer.OrdinalIgnoreCase))
                {
                    best = option;
                    bestLength = spoken.Length;
                }
            }
        }
        if (best is null && pos < tokens.Length && NumberParser.TryParse([tokens[pos]], out var spokenNumber))
        {
            // Numeric options ("{seat:1|2}") also accept the spoken number.
            best = slot.Options!.FirstOrDefault(o => o == spokenNumber);
            bestLength = best is null ? 0 : 1;
        }
        return (best, bestLength);
    }

    private static IEnumerable<string[]> SpokenForms(string option)
    {
        yield return PhraseText.Tokenize(option);
        if (Nato.TryGetValue(option, out var words))
        {
            foreach (var word in words) yield return word.Split(' ');
        }
    }

    private static string Remaining(string[] tokens, int pos) => string.Join(' ', tokens.Skip(pos));
}
