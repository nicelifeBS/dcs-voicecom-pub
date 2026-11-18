namespace DcsVoiceCom.Phrases;

/// <summary>
/// The proper number words of one language, shared by <see cref="NumberWords"/> (digits to words) and
/// <see cref="NumberParser"/> (words to digits). Extra spellings the parser accepts ("niner", "zwo") live there.
/// </summary>
internal sealed record NumberVocabulary(string[] Ones, string[] Teens, string[] Tens, string Hundred, string Thousand)
{
    public static readonly NumberVocabulary English = new(
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"],
        ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"],
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"],
        "hundred", "thousand");

    public static readonly NumberVocabulary German = new(
        ["null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun"],
        ["zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"],
        ["", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig"],
        "hundert", "tausend");

    public static readonly NumberVocabulary[] All = [English, German];
}
