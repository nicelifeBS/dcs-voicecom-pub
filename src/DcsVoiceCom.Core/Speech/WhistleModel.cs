using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DcsVoiceCom.Speech;

/// <summary>One transcription: the text, the language Whistle used or detected, and its timings.</summary>
public sealed record WhistleTranscript(string Text, string? Language, double TtftMs, double DecodeTps);

/// <summary>
/// Whistle speech-to-text, in process, through needle's native engine. Load once and keep it: loading takes a
/// few hundred milliseconds, a command clip then takes tens. The engine holds one model per process and is not
/// thread-safe, so every instance shares one lock.
/// </summary>
public sealed unsafe class WhistleModel : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>The languages Whistle 2.0.0 knows.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "de", "fr", "es", "it", "nl", "pl"];

    // Same sizing as the Python wrapper: 256 KB, or 4 KB of JSON per second of audio if that is more.
    private const int MinOutputBytes = 1 << 18;
    private const int OutputBytesPerSecond = 4096;

    private static readonly object s_engineLock = new();
    private static WhistleModel? s_loaded;
    // The weights the engine currently holds. It may keep pointers into them, so they live in native memory
    // until the next successful needle_load replaces them.
    private static byte* s_weights;
    // The engine's JSON answer. One buffer, reused under s_engineLock, as it is large enough for the Large Object Heap.
    private static byte[] s_output = [];

    private bool _disposed;

    private WhistleModel(string weightsPath) => WeightsPath = weightsPath;

    public string WeightsPath { get; }

    /// <summary>
    /// Loads <paramref name="weightsPath"/> (whistle.cact). <paramref name="enginePath"/> points at
    /// libneedle.dll / libneedle.so; without it the engine is looked up next to the app. Telemetry is switched
    /// off before the engine first runs.
    /// </summary>
    public static WhistleModel Load(string weightsPath, string? enginePath = null)
    {
        if (enginePath is not null) NeedleNative.UseLibrary(enginePath);
        NeedleNative.SetCrtEnvironment("NEEDLE_TELEMETRY", "0");
        NeedleNative.SetCrtEnvironment("DO_NOT_TRACK", "1");

        lock (s_engineLock)
        {
            if (s_loaded is not null)
                throw new InvalidOperationException($"Whistle is already loaded from {s_loaded.WeightsPath}; dispose it first.");
            var weights = ReadNative(weightsPath, out var length);
            if (NeedleNative.needle_load(weights, (ulong)length) < 0)
            {
                var error = LastError();
                NativeMemory.Free(weights);
                throw new WhistleException($"Could not load {weightsPath}: {error}");
            }
            NativeMemory.Free(s_weights);
            s_weights = weights;
            var model = new WhistleModel(Path.GetFullPath(weightsPath));
            s_loaded = model;
            // The first call pays one-off setup; do it now instead of on the first command.
            model.Transcribe(new float[SampleRate / 2], "en");
            return model;
        }
    }

    /// <summary>
    /// Reads a file straight into native memory, so the weights are not held twice (a managed copy on the Large
    /// Object Heap, then the native one). The caller frees the block.
    /// </summary>
    private static byte* ReadNative(string path, out int length)
    {
        using var file = File.OpenHandle(path);
        var size = RandomAccess.GetLength(file);
        if (size > int.MaxValue) throw new WhistleException($"{path} is too large ({size} bytes)");
        length = (int)size;
        var block = (byte*)NativeMemory.Alloc((nuint)Math.Max(length, 1));
        try
        {
            var span = new Span<byte>(block, length);
            for (var read = 0; read < length;)
            {
                var n = RandomAccess.Read(file, span[read..], read);
                if (n == 0) throw new EndOfStreamException($"{path} ended after {read} of {length} bytes");
                read += n;
            }
            return block;
        }
        catch
        {
            NativeMemory.Free(block);
            throw;
        }
    }

    /// <summary>
    /// Transcribes 16 kHz mono samples in [-1, 1]. <paramref name="language"/> forces a language (null detects
    /// it); <paramref name="keywords"/> bias Whistle towards words and phrases, such as the command phrases.
    /// </summary>
    public WhistleTranscript Transcribe(ReadOnlySpan<float> samples, string? language = null, IEnumerable<string>? keywords = null)
    {
        var languageBytes = language is null ? null : Utf8Z(language);
        var keywordText = keywords is null ? "" : string.Join('\n', keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
        var keywordBytes = keywordText.Length == 0 ? null : Utf8Z(keywordText);
        var outputSize = Math.Max(MinOutputBytes, samples.Length / SampleRate * OutputBytesPerSecond);
        // The engine is not given an empty buffer: the Python wrapper passes one zero sample in that case too.
        ReadOnlySpan<float> audio = samples.IsEmpty ? stackalloc float[1] : samples;
        lock (s_engineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (s_output.Length < outputSize) s_output = new byte[outputSize];
            var output = s_output.AsSpan(0, outputSize);
            output.Clear(); // the answer ends at its first NUL, so no older answer may show through
            int code;
            fixed (float* audioPtr = audio)
            fixed (byte* languagePtr = languageBytes)
            fixed (byte* keywordPtr = keywordBytes)
            fixed (byte* outputPtr = output)
            {
                code = NeedleNative.needle_transcribe(audioPtr, samples.Length, languagePtr, keywordPtr, 0, outputPtr, output.Length);
            }
            if (code < 0) throw new WhistleException(LastError());
            return Parse(s_output.AsMemory(0, outputSize));
        }
    }

    /// <summary>Reads the engine's JSON answer in place: up to the first NUL, or all of it.</summary>
    internal static WhistleTranscript Parse(ReadOnlyMemory<byte> output)
    {
        var end = output.Span.IndexOf((byte)0);
        using var json = JsonDocument.Parse(end < 0 ? output : output[..end]);
        var root = json.RootElement;
        return new WhistleTranscript(
            (root.TryGetProperty("text", out var text) ? text.GetString() : null)?.Trim() ?? "",
            root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null,
            Number(root, "ttft_ms"),
            Number(root, "decode_tps"));
    }

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static byte[] Utf8Z(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    private static string LastError()
    {
        var error = NeedleNative.needle_last_error();
        return error is null ? "unknown needle engine error" : Marshal.PtrToStringUTF8((IntPtr)error) ?? "";
    }

    /// <summary>
    /// Releases this model's slot. The engine has no unload call, so the weights stay in memory until a later
    /// <see cref="Load"/> replaces them.
    /// </summary>
    public void Dispose()
    {
        lock (s_engineLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (s_loaded == this) s_loaded = null;
        }
    }
}

public sealed class WhistleException(string message) : Exception(message);
