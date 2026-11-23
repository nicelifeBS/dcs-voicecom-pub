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
    [InlineData("000", "zero zero zero")]
    [InlineData("251", "two five one")]
    [InlineData("7", null)]
    [InlineData("25.1", null)]
    public void Reads_digits_one_by_one(string digits, string? english) => Assert.Equal(english, NumberWords.EnglishDigits(digits));

    [Fact]
    public void Spelled_numbers_parse_back()
    {
        for (var n = 0; n <= 9999; n++)
        {
            Assert.True(NumberParser.TryParse(NumberWords.English(n), out var en), NumberWords.English(n));
            Assert.True(NumberParser.TryParse(NumberWords.German(n), out var de), NumberWords.German(n));
            Assert.Equal((n.ToString(), n.ToString()), (en, de));
        }
    }

    [Theory]
    [InlineData("fuenf", "5")]
    [InlineData("zwoelf", "12")]
    [InlineData("dreissig", "30")]
    [InlineData("fuenfundzwanzig", "25")]
    public void German_words_without_umlauts_parse(string word, string digits)
    {
        Assert.True(NumberParser.TryParse(word, out var value));
        Assert.Equal(digits, value);
    }
}
