using System.Text;

namespace DcsVoiceCom.Phrases;

public static class PhraseText
{
    /// <summary>
    /// Lower-cases and strips punctuation outside {slots}; collapses whitespace.
    /// Slot specs keep their case, since enum options are compared case-insensitively anyway.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inSlot = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '{') inSlot = true;
            if (inSlot)
            {
                sb.Append(ch);
                if (ch == '}') inSlot = false;
                continue;
            }
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if ((ch == '.' || ch == ',') && IsDigitAt(text, i - 1) && IsDigitAt(text, i + 1)) sb.Append(ch); // "251.0"
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string[] Tokenize(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsDigitAt(string text, int index) => index >= 0 && index < text.Length && char.IsDigit(text[index]);
}
