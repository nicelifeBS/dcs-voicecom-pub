using DcsVoiceCom.Phrases;

namespace DcsVoiceCom.Tests;

public class NumberWordsTests
{
    [Theory]
    [InlineData("0", "zero", "null")]
    [InlineData("1", "one", "eins")]
    [InlineData("25", "twenty five", "fünfundzwanzig")]
    [InlineData("50", "fifty", "fünfzig")]
    [InlineData("200", "two hundred", "zweihundert")]
    [InlineData("126", "one hundred twenty six", "einhundertsechsundzwanzig")]
    [InlineData("1000", "one thousand", "eintausend")]
    public void Spells_english_and_german(string digits, string english, string german) =>
        Assert.Equal([english, german], NumberWords.Spell(digits));

    [Theory]
    [InlineData("251.0")]
    [InlineData("007")]
    [InlineData("10000")]
    [InlineData("tacan")]
    public void Leaves_other_words_alone(string word) => Assert.Empty(NumberWords.Spell(word));

    [Theory]
    [InlineData(25)]
    [InlineData(73)]
    [InlineData(126)]
    [InlineData(4321)]
    public void Spelled_numbers_parse_back(int n)
    {
        Assert.True(NumberParser.TryParse(NumberWords.English(n), out var en));
        Assert.True(NumberParser.TryParse(NumberWords.German(n), out var de));
        Assert.Equal((n.ToString(), n.ToString()), (en, de));
    }
}
