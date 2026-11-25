using System.Buffers.Binary;

namespace DcsVoiceCom.Speech;

/// <summary>Reads PCM (8/16/24/32-bit) and float WAV files as mono float samples in [-1, 1].</summary>
public static class WavFile
{
    public static float[] ReadMono(string path, out int sampleRate) => ReadMono(File.ReadAllBytes(path), out sampleRate);

    public static float[] ReadMono(ReadOnlySpan<byte> data, out int sampleRate)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV file.");
        ReadOnlySpan<byte> format = default, samples = default;
        var hasData = false;
        var at = 12;
        while (at + 8 <= data.Length)
        {
            var id = data.Slice(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]), (uint)(data.Length - at - 8));
            var body = data.Slice(at + 8, size);
            if (id.SequenceEqual("fmt "u8) && format.IsEmpty) format = body;
            else if (id.SequenceEqual("data"u8) && !hasData)
            {
                samples = body;
                hasData = true;
            }
            at += 8 + size + (size & 1);
        }
        if (format.Length < 16 || !hasData)
            throw new InvalidDataException("WAV file has no fmt or data chunk.");

        int tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
        int align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        if (tag == 0xFFFE && format.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(format[24..]);
        if (channels == 0 || align == 0) throw new InvalidDataException("WAV file has no channels.");
        var width = align / channels;
        if (!(tag == 1 && width is 1 or 2 or 3 or 4 || tag == 3 && width is 4 or 8))
            throw new InvalidDataException($"Unsupported WAV encoding (format {tag}, {width * 8}-bit).");

        var frames = samples.Length / align;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
                sum += Sample(samples.Slice(f * align + c * width, width), tag);
            mono[f] = (float)(sum / channels);
        }
        return mono;
    }

    /// <summary>Writes mono float samples in [-1, 1] as a 16-bit PCM WAV file.</summary>
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate) =>
        File.WriteAllBytes(path, EncodeMono16(samples, sampleRate));

    /// <summary>Mono float samples in [-1, 1] as the bytes of a 16-bit PCM WAV file.</summary>
    public static byte[] EncodeMono16(ReadOnlySpan<float> samples, int sampleRate)
    {
        var bytes = new byte[44 + samples.Length * 2];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767.0);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)v);
        }
        return bytes;
    }

    private static double Sample(ReadOnlySpan<byte> s, int tag) => (tag, s.Length) switch
    {
        (3, 4) => BinaryPrimitives.ReadSingleLittleEndian(s),
        (3, _) => BinaryPrimitives.ReadDoubleLittleEndian(s),
        (_, 1) => (s[0] - 128) / 128.0,
        (_, 2) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0,
        (_, 3) => ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 8388608.0,
        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0,
    };
}
