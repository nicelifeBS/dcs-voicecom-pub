using System.Buffers.Binary;
using System.Text;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public class WhistleTests
{
    [Fact]
    public void ParsesTheEngineJson()
    {
        var output = new byte[64 + 32];
        Encoding.UTF8.GetBytes("""{"text":" Gear down. ","language":"en","ttft_ms":9.5,"decode_tps":120}""", output);

        var transcript = WhistleModel.Parse(output);

        Assert.Equal(new WhistleTranscript("Gear down.", "en", 9.5, 120), transcript);
    }

    [Fact]
    public void ParsesSilenceWithNoLanguage()
    {
        var transcript = WhistleModel.Parse("""{"text":"","language":null,"ttft_ms":0,"decode_tps":null}"""u8.ToArray());

        Assert.Equal(new WhistleTranscript("", null, 0, 0), transcript);
    }

    [Fact]
    public void ReadsPcm16Mono()
    {
        var wav = Wav(format: 1, channels: 1, rate: 16000, bits: 16, [0, 0x40, 0x00, 0xC0]);   // 0x4000, -0x4000

        var samples = WavFile.ReadMono(wav, out var rate);

        Assert.Equal(16000, rate);
        Assert.Equal([0.5f, -0.5f], samples);
    }

    [Fact]
    public void AveragesStereoAndReadsFloat()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(data, 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), -0.75f);

        var samples = WavFile.ReadMono(Wav(format: 3, channels: 2, rate: 48000, bits: 32, data), out var rate);

        Assert.Equal(48000, rate);
        Assert.Equal([0.5f, -0.5f], samples);
    }

    [Fact]
    public void ReadsPcm24AndSkipsOtherChunks()
    {
        var wav = Wav(format: 1, channels: 1, rate: 16000, bits: 24, [0x00, 0x00, 0xC0], extraChunk: true);   // -0x400000

        Assert.Equal([-0.5f], WavFile.ReadMono(wav, out _));
    }

    [Fact]
    public void RejectsNonWav()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.ReadMono("RIFF----AVI LIST"u8, out _));
    }

    private static byte[] Wav(int format, int channels, int rate, int bits, byte[] data, bool extraChunk = false)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        if (extraChunk) { w.Write("LIST"u8); w.Write(3); w.Write(new byte[4]); }   // odd size, padded
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)format); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        return stream.ToArray();
    }
}
