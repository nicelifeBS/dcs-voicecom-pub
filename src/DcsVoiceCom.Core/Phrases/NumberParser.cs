using System.Globalization;
using System.Text;

namespace DcsVoiceCom.Phrases;

/// <summary>
/// Turns spoken numbers into digit strings, in English and German.
/// Digit-by-digit speech is concatenated ("two five one decimal zero" → "251.0"),
/// compound numbers are combined ("seventy three" → "73", "one hundred twenty six" → "126",
/// "dreiundsiebzig" → "73"), and mixed forms work too ("one twenty six" → "126").
/// Recognizers that already emit digits ("251.0", "251,0") pass through.
/// </summary>
public static class NumberParser
{
    private enum AtomKind { Digit, Teen, Tens, Hundred, Thousand, Separator, And, Digits }

    private readonly record struct Atom(AtomKind Kind, int Value = 0, string? Text = null);

    private static readonly Dictionary<string, Atom> Words = BuildWords();

    private static Dictionary<string, Atom> BuildWords()
    {
        var w = new Dictionary<string, Atom>(StringComparer.OrdinalIgnoreCase);
        void Add(AtomKind kind, int value, params string[] words)
        {
            foreach (var word in words) w[word] = new Atom(kind, value);
        }

        // English
        Add(AtomKind.Digit, 0, "zero", "oh", "o");
        Add(AtomKind.Digit, 1, "one");
        Add(AtomKind.Digit, 2, "two");
        Add(AtomKind.Digit, 3, "three", "tree");
        Add(AtomKind.Digit, 4, "four", "fower");
        Add(AtomKind.Digit, 5, "five", "fife");
        Add(AtomKind.Digit, 6, "six");
        Add(AtomKind.Digit, 7, "seven");
        Add(AtomKind.Digit, 8, "eight");
        Add(AtomKind.Digit, 9, "nine", "niner");
        string[] teens = ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        for (var i = 0; i < teens.Length; i++) Add(AtomKind.Teen, 10 + i, teens[i]);
        string[] tens = ["twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        for (var i = 0; i < tens.Length; i++) Add(AtomKind.Tens, 20 + 10 * i, tens[i]);
        Add(AtomKind.Hundred, 100, "hundred");
        Add(AtomKind.Thousand, 1000, "thousand");
        Add(AtomKind.Separator, 0, "decimal", "point", "dot");
        Add(AtomKind.And, 0, "and");

        // German
        Add(AtomKind.Digit, 0, "null");
        Add(AtomKind.Digit, 1, "eins", "ein", "eine");
        Add(AtomKind.Digit, 2, "zwei", "zwo");
        Add(AtomKind.Digit, 3, "drei");
        Add(AtomKind.Digit, 4, "vier");
        Add(AtomKind.Digit, 5, "fünf", "fuenf");
        Add(AtomKind.Digit, 6, "sechs");
        Add(AtomKind.Digit, 7, "sieben");
        Add(AtomKind.Digit, 8, "acht");
        Add(AtomKind.Digit, 9, "neun");
        Add(AtomKind.Teen, 10, "zehn");
        Add(AtomKind.Teen, 11, "elf");
        Add(AtomKind.Teen, 12, "zwölf", "zwoelf");
        Add(AtomKind.Teen, 13, "dreizehn");
        Add(AtomKind.Teen, 14, "vierzehn");
        Add(AtomKind.Teen, 15, "fünfzehn", "fuenfzehn");
        Add(AtomKind.Teen, 16, "sechzehn");
        Add(AtomKind.Teen, 17, "siebzehn");
        Add(AtomKind.Teen, 18, "achtzehn");
        Add(AtomKind.Teen, 19, "neunzehn");
        Add(AtomKind.Tens, 20, "zwanzig");
        Add(AtomKind.Tens, 30, "dreißig", "dreissig");
        Add(AtomKind.Tens, 40, "vierzig");
        Add(AtomKind.Tens, 50, "fünfzig", "fuenfzig");
        Add(AtomKind.Tens, 60, "sechzig");
        Add(AtomKind.Tens, 70, "siebzig");
        Add(AtomKind.Tens, 80, "achtzig");
        Add(AtomKind.Tens, 90, "neunzig");
        Add(AtomKind.Hundred, 100, "hundert");
        Add(AtomKind.Thousand, 1000, "tausend");
        Add(AtomKind.Separator, 0, "komma", "punkt");
        Add(AtomKind.And, 0, "und");
        return w;
    }

    /// <summary>True when the token can be part of a spoken number.</summary>
    public static bool IsNumberToken(string token) => TryAtoms(token, out _);

    public static bool TryParse(IEnumerable<string> tokens, out string value)
    {
        value = "";
        var atoms = new List<Atom>();
        foreach (var token in tokens)
        {
            if (!TryAtoms(token, out var parts)) return false;
            atoms.AddRange(parts);
        }
        atoms = CombineGermanUnitsAndTens(atoms);
        if (atoms.Count == 0) return false;

        var integerPart = new StringBuilder();
        var fractionPart = new StringBuilder();
        var target = integerPart;
        var sawSeparator = false;
        var i = 0;
        while (i < atoms.Count)
        {
            var atom = atoms[i];
            if (atom.Kind == AtomKind.Separator)
            {
                if (sawSeparator) return false;
                sawSeparator = true;
                target = fractionPart;
                i++;
                continue;
            }
            if (atom.Kind == AtomKind.Digits)
            {
                var text = atom.Text!;
                var dot = text.IndexOf('.');
                if (dot >= 0)
                {
                    if (sawSeparator) return false;
                    target.Append(text[..dot]);
                    sawSeparator = true;
                    target = fractionPart;
                    target.Append(text[(dot + 1)..]);
                }
                else
                {
                    target.Append(text);
                }
                i++;
                continue;
            }
            if (atom.Kind == AtomKind.And) return false; // "and" only valid inside a compound, consumed there
            var chunk = ParseChunk(atoms, ref i);
            if (chunk is null) return false;
            target.Append(chunk);
        }

        if (integerPart.Length == 0 && fractionPart.Length == 0) return false;
        if (sawSeparator && fractionPart.Length == 0) return false;
        if (integerPart.Length == 0) integerPart.Append('0');
        value = sawSeparator ? $"{integerPart}.{fractionPart}" : integerPart.ToString();
        return true;
    }

    public static bool TryParse(string text, out string value) => TryParse(PhraseText.Tokenize(text), out value);

    /// <summary>
    /// Parses one compound number at <paramref name="i"/>. A lone digit not followed by
    /// "hundred"/"thousand" is a single digit, so digit-by-digit speech concatenates.
    /// </summary>
    private static string? ParseChunk(List<Atom> atoms, ref int i)
    {
        var start = i;
        var value = ParseBelowThousand(atoms, ref i, allowBareDigit: true, out var bareDigit);
        if (value is null) return null;

        if (i < atoms.Count && atoms[i].Kind == AtomKind.Thousand)
        {
            i++;
            var total = value.Value * 1000;
            SkipAnd(atoms, ref i);
            var rest = ParseBelowThousand(atoms, ref i, allowBareDigit: true, out _);
            if (rest is not null) total += rest.Value;
            return total.ToString(CultureInfo.InvariantCulture);
        }
        if (bareDigit) return atoms[start].Value.ToString(CultureInfo.InvariantCulture);
        return value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static int? ParseBelowThousand(List<Atom> atoms, ref int i, bool allowBareDigit, out bool bareDigit)
    {
        bareDigit = false;
        if (i >= atoms.Count) return null;
        var atom = atoms[i];
        var next = i + 1 < atoms.Count ? atoms[i + 1] : default;

        int hundreds = 0;
        if (atom.Kind == AtomKind.Digit && next.Kind == AtomKind.Hundred && atom.Value > 0)
        {
            hundreds = atom.Value * 100;
            i += 2;
        }
        else if (atom.Kind == AtomKind.Hundred)
        {
            hundreds = 100;
            i++;
        }
        else if (atom.Kind == AtomKind.Thousand)
        {
            return 1; // a bare "thousand": leave the atom for the caller to multiply
        }

        if (hundreds > 0)
        {
            SkipAnd(atoms, ref i);
            var tail = ParseTensOrUnit(atoms, ref i);
            return hundreds + (tail ?? 0);
        }

        if (atom.Kind == AtomKind.Digit)
        {
            i++;
            if (i < atoms.Count && atoms[i].Kind == AtomKind.Thousand) return atom.Value;
            bareDigit = allowBareDigit;
            return atom.Value;
        }
        return ParseTensOrUnit(atoms, ref i);
    }

    private static int? ParseTensOrUnit(List<Atom> atoms, ref int i)
    {
        if (i >= atoms.Count) return null;
        var atom = atoms[i];
        switch (atom.Kind)
        {
            case AtomKind.Teen:
                i++;
                return atom.Value;
            case AtomKind.Tens:
                i++;
                if (i < atoms.Count && atoms[i].Kind == AtomKind.Digit && atoms[i].Value > 0
                    && !(i + 1 < atoms.Count && atoms[i + 1].Kind == AtomKind.Hundred))
                {
                    return atom.Value + atoms[i++].Value;
                }
                return atom.Value;
            case AtomKind.Digit:
                i++;
                return atom.Value;
            default:
                return null;
        }
    }

    private static void SkipAnd(List<Atom> atoms, ref int i)
    {
        if (i < atoms.Count && atoms[i].Kind == AtomKind.And) i++;
    }

    /// <summary>German puts units before tens: "drei und siebzig" → 73.</summary>
    private static List<Atom> CombineGermanUnitsAndTens(List<Atom> atoms)
    {
        var result = new List<Atom>(atoms.Count);
        for (var i = 0; i < atoms.Count; i++)
        {
            if (atoms[i].Kind == AtomKind.Digit && atoms[i].Value > 0
                && i + 2 < atoms.Count && atoms[i + 1].Kind == AtomKind.And && atoms[i + 2].Kind == AtomKind.Tens)
            {
                result.Add(new Atom(AtomKind.Teen, atoms[i + 2].Value + atoms[i].Value)); // a complete two-digit value
                i += 2;
                continue;
            }
            result.Add(atoms[i]);
        }
        return result;
    }

    private static bool TryAtoms(string token, out List<Atom> atoms)
    {
        atoms = [];
        if (token.Length == 0) return false;

        var numeric = token.Replace(',', '.');
        if (numeric.All(c => char.IsDigit(c) || c == '.') && numeric.Count(c => c == '.') <= 1 && char.IsDigit(numeric[0]))
        {
            atoms.Add(new Atom(AtomKind.Digits, Text: numeric));
            return true;
        }
        if (Words.TryGetValue(token, out var atom))
        {
            atoms.Add(atom);
            return true;
        }
        // German writes compounds as one word: "zweihundertdreiundsiebzig".
        return TrySplitCompound(token.ToLowerInvariant(), atoms);
    }

    private static bool TrySplitCompound(string word, List<Atom> atoms)
    {
        if (word.Length == 0) return true;
        for (var length = word.Length; length >= 3; length--)
        {
            if (!Words.TryGetValue(word[..length], out var atom)) continue;
            if (atom.Kind == AtomKind.Separator) continue;
            atoms.Add(atom);
            if (TrySplitCompound(word[length..], atoms)) return true;
            atoms.RemoveAt(atoms.Count - 1);
        }
        return false;
    }
}
