using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DcsVoiceCom.Seeding;

/// <summary>Turns DCS-BIOS control descriptions and position names into words a pilot would say.</summary>
public static partial class VoiceLines
{
    /// <summary>Words that name the kind of control, not the control: "Master Arm Switch" → "master arm".</summary>
    private static readonly HashSet<string> ControlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "switch", "selector", "knob", "pushbutton", "button", "dial", "sw", "btn", "pb",
    };

    /// <summary>Dropped only at the end: "Radar Altimeter Control" → "radar altimeter", but "flight control mode" stays.</summary>
    private static readonly HashSet<string> TrailingWords = new(StringComparer.OrdinalIgnoreCase) { "control", "toggle" };

    /// <summary>Crew station prefixes of two-seat modules ("PILOT Canopy Jettison", "RIO Canopy Jettison").</summary>
    private static readonly HashSet<string> CrewWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "pilot", "plt", "rio", "wso", "cpg", "copilot", "gunner", "operator",
    };

    /// <summary>Panel abbreviations in position names, as spoken.</summary>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dn"] = "down", ["dwn"] = "down", ["brt"] = "bright", ["med"] = "medium", ["stby"] = "standby",
        ["norm"] = "normal", ["emer"] = "emergency", ["man"] = "manual", ["ovrd"] = "override", ["oride"] = "override",
        ["rcvy"] = "recovery", ["ret"] = "retract", ["fwd"] = "forward", ["aft"] = "aft",
        ["l"] = "left", ["r"] = "right", ["lt"] = "left", ["rt"] = "right", ["ctr"] = "center", ["cntr"] = "center",
        ["pos"] = "position", ["sel"] = "select", ["xmit"] = "transmit", ["rej"] = "reject",
        ["inc"] = "increase", ["dec"] = "decrease", ["hi"] = "high", ["lo"] = "low", ["tst"] = "test",
    };

    private static readonly string[] SmallNumbers =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    /// <summary>
    /// The spoken name of a control: crew prefix split off, panel lists after a comma joined in
    /// ("UFC Keyboard Pushbutton, 1" → "UFC keyboard 1"), a trailing position list dropped
    /// ("Spin Recovery Switch, RCVY/NORM" → "spin recovery"), control-kind words removed.
    /// Acronyms keep their capitals; everything else is lower case.
    /// </summary>
    public static (string Name, string? Crew) ControlName(string description, string identifier, int? positionCount = null)
    {
        var text = description.Trim();
        if (text.Length == 0) text = identifier.Replace('_', ' ');

        // "Pushbutton - Push to initiate BIT": the instruction is not part of the name.
        var hint = HowToUse().Match(text);
        if (hint.Success && hint.Index > 0) text = text[..hint.Index];
        // "Spin Recovery Switch, RCVY/NORM" lists the positions; "Function Selector Pushbutton, A/P" names a key.
        var comma = text.LastIndexOf(',');
        if (comma > 0 && positionCount is { } count && text[(comma + 1)..].Split('/').Length == count) text = text[..comma];
        text = Parenthesis().Replace(text, m => m.Groups[1].Value.Any(char.IsDigit) ? " " : $" {m.Groups[1].Value} ");

        var words = Words(text);
        string? crew = null;
        if (words.Count > 1 && CrewWords.Contains(words[0]))
        {
            crew = words[0].ToLowerInvariant() switch
            {
                "plt" or "pilot" => "pilot",
                "copilot" or "gunner" or "operator" => words[0].ToLowerInvariant(),
                _ => words[0].ToUpperInvariant(),
            };
            words.RemoveAt(0);
        }
        var kept = words.Where(w => !ControlWords.Contains(w)).ToList();
        if (kept.Count == 0) kept = words;
        while (kept.Count > 1 && TrailingWords.Contains(kept[^1])) kept.RemoveAt(kept.Count - 1);
        return (Sentence(kept.Select(Case)), crew);
    }

    /// <summary>A category as a prefix that tells two same-named controls apart: "Up Front Controller (UFC)" → "UFC".</summary>
    public static string CategoryName(string category)
    {
        var inner = Parenthesis().Match(category);
        if (inner.Success && !inner.Groups[1].Value.Any(char.IsDigit)) return string.Join(' ', Words(inner.Groups[1].Value).Select(Case));
        return string.Join(' ', Words(category).Where(w => !ControlWords.Contains(w)).Select(Case));
    }

    /// <summary>A position as spoken: "DN" → "down", "REJ 1" → "reject 1", "X" → "X" (letters stay capitals).</summary>
    public static string Position(string position) =>
        string.Join(' ', Words(position).Select(w =>
            Abbreviations.TryGetValue(w, out var spoken) ? spoken : w.Length == 1 && char.IsUpper(w[0]) ? w : w.ToLowerInvariant()));

    /// <summary>The same line with numbers 0-20 written as words, or null when it has none.</summary>
    public static string? NumbersAsWords(string line)
    {
        var changed = false;
        var words = line.Split(' ').Select(w =>
        {
            if (int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < SmallNumbers.Length)
            {
                changed = true;
                return SmallNumbers[n];
            }
            return w;
        });
        var result = string.Join(' ', words);
        return changed ? result : null;
    }

    /// <summary>Words joined with the first letter capitalized; a word said twice in a row once ("flash flash" → "flash").</summary>
    public static string Sentence(IEnumerable<string> words)
    {
        var all = string.Join(' ', words).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = string.Join(' ', all.Where((w, i) => i == 0 || !w.Equals(all[i - 1], StringComparison.OrdinalIgnoreCase)));
        return line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>Letters and digits only, split on everything else ("Fuel/Engine" → fuel, engine).</summary>
    private static List<string> Words(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Acronyms (UFC, TACAN, DDI) and key letters (F) stay capitals; other words go lower case.</summary>
    private static string Case(string word) =>
        word.Any(char.IsLetter) && word.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? word : word.ToLowerInvariant();

    [GeneratedRegex(@"\s+-\s+(push|press|pull|lift|rotate|turn|click)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex HowToUse();

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex Parenthesis();
}
