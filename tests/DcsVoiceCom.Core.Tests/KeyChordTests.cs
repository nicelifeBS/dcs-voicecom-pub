using DcsVoiceCom.Input;

namespace DcsVoiceCom.Tests;

public class KeyChordTests
{
    [Fact]
    public void Left_and_right_modifiers_differ_by_extended_flag_or_code()
    {
        var chord = KeyChord.Parse("RCtrl+RShift+G");

        Assert.Equal(["RCtrl", "RShift", "G"], chord.Keys.Select(k => k.Name));
        Assert.Equal(new ScanKey("RCtrl", 0x1D, true, true), chord.Keys[0]);
        Assert.Equal(0x36, chord.Keys[1].ScanCode);
        Assert.Equal(0x22, chord.Keys[2].ScanCode);
        Assert.Equal(KeyChord.Parse("LCtrl+G").Keys[0].ScanCode, chord.Keys[0].ScanCode);
        Assert.False(KeyChord.Parse("LCtrl+G").Keys[0].Extended);
        Assert.Equal(2, chord.Modifiers.Count());
        Assert.Single(chord.MainKeys);
    }

    [Fact]
    public void Accepts_aliases_and_case() =>
        Assert.Equal("LAlt+F10", KeyChord.Parse("alt+f10").ToString());

    [Fact]
    public void Rejects_unknown_keys() => Assert.Throws<FormatException>(() => KeyChord.Parse("Ctrl+Hyper"));
}
