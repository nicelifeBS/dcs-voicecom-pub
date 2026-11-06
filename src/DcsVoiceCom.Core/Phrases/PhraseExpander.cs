using System.Text;

namespace DcsVoiceCom.Phrases;

/// <summary>
/// Expands VoiceAttack-style phrase syntax into plain phrases:
/// "gear up;landing gear up" (top-level alternatives) and "[gear;landing gear] up" (inline groups,
/// an empty option makes the group optional: "[please;]gear up").
/// </summary>
public static class PhraseExpander
{
    public const int MaxExpansions = 256;

    public static IReadOnlyList<string> Expand(string spec)
    {
        var results = new List<string>();
        foreach (var alternative in SplitTopLevel(spec))
        {
            foreach (var phrase in ExpandGroups(alternative))
            {
                var normalized = PhraseText.Normalize(phrase);
                if (normalized.Length > 0 && !results.Contains(normalized)) results.Add(normalized);
                if (results.Count >= MaxExpansions) return results;
            }
        }
        return results;
    }

    private static IEnumerable<string> SplitTopLevel(string spec)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            switch (spec[i])
            {
                case '[' or '{': depth++; break;
                case ']' or '}': depth = Math.Max(0, depth - 1); break;
                case ';' when depth == 0:
                    yield return spec[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return spec[start..];
    }

    private static List<string> ExpandGroups(string text)
    {
        var partial = new List<StringBuilder> { new() };
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close < 0) throw new FormatException($"Unclosed '[' in phrase \"{text}\"");
                var options = text[(i + 1)..close].Split(';');
                var next = new List<StringBuilder>();
                foreach (var prefix in partial)
                {
                    foreach (var option in options)
                    {
                        next.Add(new StringBuilder(prefix.ToString()).Append(' ').Append(option).Append(' '));
                        if (next.Count >= MaxExpansions) break;
                    }
                }
                partial = next;
                i = close + 1;
            }
            else
            {
                foreach (var sb in partial) sb.Append(text[i]);
                i++;
            }
        }
        return partial.Select(sb => sb.ToString()).ToList();
    }
}
