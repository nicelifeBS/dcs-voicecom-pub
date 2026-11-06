using DcsVoiceCom.Phrases;

namespace DcsVoiceCom.Tests;

public class PhraseTests
{
    [Fact]
    public void Expands_groups_and_top_level_alternatives()
    {
        Assert.Equal(["gear up", "landing gear up"], PhraseExpander.Expand("[gear;landing gear] up"));
        Assert.Equal(["gear up", "landing gear up"], PhraseExpander.Expand("Gear up; Landing gear up."));
        Assert.Equal(["please gear up", "gear up"], PhraseExpander.Expand("[please;] gear up"));
        Assert.Equal(
            ["master arm on", "master arm hot", "arm on", "arm hot"],
            PhraseExpander.Expand("[master arm;arm] [on;hot]"));
    }

    [Fact]
    public void Keeps_slots_intact()
    {
        Assert.Equal(["tacan {channel:1-126} {band:X|Y}"], PhraseExpander.Expand("TACAN {channel:1-126} {band:X|Y}"));
        Assert.Equal(["radio one {freq}", "comm one {freq}"], PhraseExpander.Expand("[radio;comm] one {freq}"));
    }

    [Fact]
    public void Rejects_unclosed_group() => Assert.Throws<FormatException>(() => PhraseExpander.Expand("[gear up"));

    [Fact]
    public void Parses_template_segments()
    {
        var template = PhraseTemplate.Parse("TACAN {channel:1-126} {band:X|Y}");

        Assert.Equal("tacan {channel:1-126} {band:X|Y}", template.Text);
        var slots = template.Slots.ToList();
        Assert.Equal(new SlotSpec("channel", SlotKind.Range, 1, 126), slots[0]);
        Assert.Equal(SlotKind.Choice, slots[1].Kind);
        Assert.Equal(["X", "Y"], slots[1].Options!);
        Assert.Equal(SlotKind.Number, PhraseTemplate.Parse("radio one {freq}").Slots.Single().Kind);
    }

    [Fact]
    public void Rejects_duplicate_slot_names() =>
        Assert.Throws<FormatException>(() => PhraseTemplate.Parse("set {a} and {a}"));

    [Fact]
    public void Normalize_keeps_decimal_points_only_between_digits()
    {
        Assert.Equal("radio 251.0", PhraseText.Normalize("Radio 251.0."));
        Assert.Equal("gear up", PhraseText.Normalize("Gear, up!"));
        Assert.Equal("x ray", PhraseText.Normalize("X-ray"));
    }
}

public class NumberParserTests
{
    [Theory]
    [InlineData("two five one decimal zero", "251.0")]
    [InlineData("two five one point seven five", "251.75")]
    [InlineData("one two six", "126")]
    [InlineData("seventy three", "73")]
    [InlineData("one hundred twenty six", "126")]
    [InlineData("one hundred and five", "105")]
    [InlineData("one twenty six", "126")]
    [InlineData("two seventy", "270")]
    [InlineData("zero niner zero", "090")]
    [InlineData("two thousand five hundred", "2500")]
    [InlineData("twenty five thousand", "25000")]
    [InlineData("fifteen", "15")]
    [InlineData("251.0", "251.0")]
    [InlineData("251 point 5", "251.5")]
    [InlineData("point five", "0.5")]
    // German
    [InlineData("zwei fünf eins komma null", "251.0")]
    [InlineData("dreiundsiebzig", "73")]
    [InlineData("drei und siebzig", "73")]
    [InlineData("einhundertsechsundzwanzig", "126")]
    [InlineData("zwo sieben null", "270")]
    [InlineData("251,0", "251.0")]
    public void Parses(string spoken, string expected)
    {
        Assert.True(NumberParser.TryParse(spoken, out var value), spoken);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData("two banana")]
    [InlineData("one point two point three")]
    [InlineData("two decimal")]
    public void Rejects(string spoken) => Assert.False(NumberParser.TryParse(spoken, out _));
}

public class SlotParserTests
{
    [Fact]
    public void Parses_frequency()
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("radio one {freq}"), "two five one decimal zero");
        Assert.True(result.Success, result.Error);
        Assert.Equal("251.0", result.Values["freq"]);
    }

    [Theory]
    [InlineData("seventy three x-ray", "73", "X")]
    [InlineData("seven three yankee", "73", "Y")]
    [InlineData("one hundred twenty six x", "126", "X")]
    [InlineData("12 xray", "12", "X")]
    public void Parses_range_and_choice(string slots, string channel, string band)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("tacan {channel:1-126} {band:X|Y}"), slots);
        Assert.True(result.Success, result.Error);
        Assert.Equal(channel, result.Values["channel"]);
        Assert.Equal(band, result.Values["band"]);
    }

    [Theory]
    [InlineData("one two seven x-ray")] // out of range
    [InlineData("zero x-ray")]
    [InlineData("seventy three zulu")] // not an option
    [InlineData("seventy three")] // missing band
    [InlineData("seventy three x-ray please")] // trailing words
    [InlineData("seven point three x-ray")] // not whole
    public void Rejects_invalid_values(string slots)
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("tacan {channel:1-126} {band:X|Y}"), slots);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Literal_between_slots_separates_numbers()
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("set {a:number} by {b:number}"), "two five by three");
        Assert.True(result.Success, result.Error);
        Assert.Equal("25", result.Values["a"]);
        Assert.Equal("3", result.Values["b"]);
    }

    [Fact]
    public void Numeric_choice_accepts_spoken_number()
    {
        var result = SlotParser.Parse(PhraseTemplate.Parse("seat {seat:1|2}"), "two");
        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.Values["seat"]);
    }
}

public class ChoiceValueTests
{
    [Fact]
    public void Option_values_replace_the_spoken_label()
    {
        var template = PhraseTemplate.Parse("tacan {channel:1-126} {band:X=0|Y=1}");
        var band = template.Slots.Last();

        Assert.Equal(["X", "Y"], band.Options!);
        var result = SlotParser.Parse(template, "seventy three x-ray");
        Assert.True(result.Success, result.Error);
        Assert.Equal("0", result.Values["band"]);
    }
}
