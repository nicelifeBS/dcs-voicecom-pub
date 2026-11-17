using System.Globalization;

namespace DcsVoiceCom.Phrases;

/// <summary>
/// Spells a number the way it is spoken, so a phrase written with digits ("jester range 50") also matches a
/// recognizer that writes the word ("jester range fifty"). The reverse of <see cref="NumberParser"/>.
/// </summary>
public static class NumberWords
{
    private static readonly string[] EnOnes = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    private static readonly string[] EnTeens = ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] EnTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly string[] DeOnes = ["null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun"];
    private static readonly string[] DeTeens = ["zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"];
    private static readonly string[] DeTens = ["", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig"];

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

    public static string English(int n)
    {
        if (n < 10) return EnOnes[n];
        if (n < 20) return EnTeens[n - 10];
        if (n < 100) return EnTens[n / 10] + (n % 10 == 0 ? "" : " " + EnOnes[n % 10]);
        if (n < 1000) return EnOnes[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + English(n % 100));
        return English(n / 1000) + " thousand" + (n % 1000 == 0 ? "" : " " + English(n % 1000));
    }

    public static string German(int n)
    {
        if (n < 10) return DeOnes[n];
        if (n < 20) return DeTeens[n - 10];
        if (n < 100) return (n % 10 == 0 ? "" : Unit(n % 10) + "und") + DeTens[n / 10];
        if (n < 1000) return Unit(n / 100) + "hundert" + (n % 100 == 0 ? "" : German(n % 100));
        return (n / 1000 == 1 ? "ein" : German(n / 1000)) + "tausend" + (n % 1000 == 0 ? "" : German(n % 1000));

        static string Unit(int d) => d == 1 ? "ein" : DeOnes[d]; // "einundzwanzig", "einhundert"
    }
}
