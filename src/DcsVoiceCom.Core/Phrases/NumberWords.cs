using System.Globalization;

namespace DcsVoiceCom.Phrases;

/// <summary>
/// Spells a number the way it is spoken, so a phrase written with digits ("jester range 50") also matches a
/// recognizer that writes the word ("jester range fifty"). The reverse of <see cref="NumberParser"/>.
/// </summary>
public static class NumberWords
{
    private static readonly NumberVocabulary En = NumberVocabulary.English;
    private static readonly NumberVocabulary De = NumberVocabulary.German;

    /// <summary>
    /// The English and German words for a whole number written in digits ("25" gives "twenty five" and
    /// "fünfundzwanzig"); empty for anything else, such as "251.0", "007" or numbers above 9999.
    /// </summary>
    public static IReadOnlyList<string> Spell(string digits)
    {
        if (digits.Length is 0 or > 4 || digits.Length > 1 && digits[0] == '0' || !digits.All(char.IsAsciiDigit)) return [];
        var n = int.Parse(digits, CultureInfo.InvariantCulture);
        return new[] { English(n), German(n) }.Distinct().ToList();
    }

    /// <summary>
    /// A number of two or more digits read out one digit at a time, as on the radio: "000" gives "zero zero zero",
    /// "251" gives "two five one". Null for anything else.
    /// </summary>
    public static string? EnglishDigits(string digits) =>
        digits.Length < 2 || !digits.All(char.IsAsciiDigit) ? null : string.Join(' ', digits.Select(d => En.Ones[d - '0']));

    public static string English(int n)
    {
        if (n < 10) return En.Ones[n];
        if (n < 20) return En.Teens[n - 10];
        if (n < 100) return En.Tens[n / 10] + (n % 10 == 0 ? "" : " " + En.Ones[n % 10]);
        if (n < 1000) return En.Ones[n / 100] + " " + En.Hundred + (n % 100 == 0 ? "" : " " + English(n % 100));
        return English(n / 1000) + " " + En.Thousand + (n % 1000 == 0 ? "" : " " + English(n % 1000));
    }

    public static string German(int n)
    {
        if (n < 10) return De.Ones[n];
        if (n < 20) return De.Teens[n - 10];
        if (n < 100) return (n % 10 == 0 ? "" : Unit(n % 10) + "und") + De.Tens[n / 10];
        if (n < 1000) return Unit(n / 100) + De.Hundred + (n % 100 == 0 ? "" : German(n % 100));
        return (n / 1000 == 1 ? "ein" : German(n / 1000)) + De.Thousand + (n % 1000 == 0 ? "" : German(n % 1000));

        static string Unit(int d) => d == 1 ? "ein" : De.Ones[d]; // "einundzwanzig", "einhundert"
    }
}
