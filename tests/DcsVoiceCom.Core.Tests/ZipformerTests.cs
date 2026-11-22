using System.Runtime.InteropServices;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public class ZipformerTests
{
    [Theory]
    [InlineData("gear up", new[] { "gear up" })]
    [InlineData("tacan channel 73", new[] { "tacan channel seventy three", "tacan channel seven three" })]
    [InlineData("radio 251", new[] { "radio two hundred and fifty one", "radio two hundred fifty one", "radio two five one", "radio two fifty one" })]
    [InlineData("decimal 000", new[] { "decimal zero zero zero" })]
    public void Hotwords_spell_numbers_the_ways_they_are_spoken(string keyword, string[] forms) =>
        Assert.Equal(forms, Hotwords.Forms(keyword));

    [Fact]
    public void Hotwords_with_many_numbers_stay_bounded() =>
        Assert.True(Hotwords.Forms("251 251 251 251").Count() <= 8);

    [Fact]
    public void Reads_pieces_and_scores_from_a_sentencepiece_model()
    {
        // ModelProto { pieces: [{ piece: "▁GEAR", score: -1.5 }, { piece: "UP", type: 1 }], trainer_spec: { 1: 1 } }
        byte[] gear = [0x0a, .. Bytes("▁GEAR"), 0x15, .. BitConverter.GetBytes(-1.5f)];
        byte[] up = [0x0a, .. Bytes("UP"), 0x18, 0x01];
        byte[] model = [0x0a, (byte)gear.Length, .. gear, 0x0a, (byte)up.Length, .. up, 0x12, 0x02, 0x08, 0x01];
        Assert.Equal([("▁GEAR", -1.5f), ("UP", 0f)], BpeVocab.Read(model));

        static byte[] Bytes(string text)
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            return [(byte)utf8.Length, .. utf8];
        }
    }

    [Fact]
    public async Task Zipformer_for_another_language_falls_back_to_whistle()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) return;
        var log = new List<string>();
        var settings = new SpeechSettings { Engine = SpeechEngines.Zipformer, ModelDirectory = BundledModel(), AutoDownload = false };
        using var speech = await SpeechService.OpenAsync(settings, "de", null, log.Add);
        Assert.Contains(log, l => l.Contains("Zipformer only knows English"));
        Assert.Contains(log, l => l.StartsWith("Whistle loaded"));
    }

    private static string BundledModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var model = Path.Combine(dir.FullName, "model", "cactus-needle");
            if (Directory.Exists(model)) return model;
        }
        return Path.Combine(AppContext.BaseDirectory, "model", "cactus-needle");
    }
}
