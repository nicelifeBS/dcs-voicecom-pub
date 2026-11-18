using System.Security.Cryptography;
using System.Text;
using DcsVoiceCom.Speech;
using SherpaOnnx;
using Whisper.net;

namespace SttBench;

/// <summary>The engines under test, each as <see cref="ISpeechToText"/>, the seam the app already uses.</summary>
internal static class Engines
{
    // k2-fsa/sherpa-onnx-zipformer-gigaspeech-2023-12-12 (Apache-2.0): offline Zipformer transducer, int8,
    // trained on GigaSpeech (10k h of podcasts and YouTube, English).
    private const string ZipformerUrl = "https://huggingface.co/k2-fsa/sherpa-onnx-zipformer-gigaspeech-2023-12-12/resolve/main/";
    private static readonly (string Name, string Sha256)[] ZipformerFiles =
    [
        ("encoder-epoch-30-avg-1.int8.onnx", "62be226a7b28a38e82cc522184ee01c46751df06bdd070dc9e1572a836ebbe78"),
        ("decoder-epoch-30-avg-1.int8.onnx", "2b0580feb757696fa929922670b7126aaee609630349a1809fd5a2aee926d56e"),
        ("joiner-epoch-30-avg-1.int8.onnx", "80160e45cca71dd52f6b0a6d3d12be18126f5308b2d4ba03f001300fea377c64"),
        ("tokens.txt", "0ef7d736bf4de3ef947292e4b119ef13f6808cd5f3aec225a843a7135ac1c2ce"),
        ("bpe.model", "c8a2a0129c4ab8e463164c142f82d25649661b122c8cd0b7aab5c9e80b90ad24"),
    ];

    // ggerganov/whisper.cpp (MIT): Whisper small.en, 5-bit quantised.
    private const string WhisperUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private static readonly (string Name, string Sha256) WhisperFile =
        ("ggml-small.en-q5_1.bin", "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30");

    public static ISpeechToText Whistle(string repo)
    {
        var files = ModelLocator.Find(Path.Combine(repo, "model", "cactus-needle"))
            ?? throw new FileNotFoundException("no Whistle model in model/cactus-needle");
        return new WhistleRecognizer(WhistleModel.Load(files.Weights, files.Engine));
    }

    public static async Task<ISpeechToText> ZipformerAsync(string cache, HttpClient http, int threads, float boost, Action<string> log)
    {
        var folder = Path.Combine(cache, "zipformer-gigaspeech");
        foreach (var (name, sha) in ZipformerFiles) await FetchAsync(http, ZipformerUrl + name, folder, name, sha, log);
        return new ZipformerRecognizer(folder, threads, boost, log);
    }

    public static async Task<ISpeechToText> WhisperAsync(string cache, HttpClient http, int threads, Action<string> log)
    {
        var folder = Path.Combine(cache, "whisper");
        var path = await FetchAsync(http, WhisperUrl + WhisperFile.Name, folder, WhisperFile.Name, WhisperFile.Sha256, log);
        return new WhisperRecognizer(path, threads);
    }

    /// <summary>Downloads a file once into the cache; a file whose SHA-256 is not the pinned one is never used.</summary>
    private static async Task<string> FetchAsync(HttpClient http, string url, string folder, string name, string sha256, Action<string> log)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        if (File.Exists(path) && Sha256(path) == sha256) return path;
        Console.WriteLine($"downloading {url}");
        var part = path + ".part";
        await using (var target = File.Create(part))
        await using (var source = await http.GetStreamAsync(url))
            await source.CopyToAsync(target);
        if (Sha256(part) != sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"{url}: SHA-256 is not the pinned one");
        }
        File.Move(part, path, overwrite: true);
        log($"saved {path}");
        return path;
    }

    private static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}

/// <summary>
/// Zipformer transducer through sherpa-onnx, biased towards the profile words with hotwords (modified beam search
/// plus an Aho-Corasick graph of the words). sherpa-onnx takes hotwords per recognizer, so the recognizer is
/// rebuilt when the word list changes (once per profile load).
/// </summary>
internal sealed class ZipformerRecognizer : ISpeechToText, IDisposable
{
    private readonly string _folder;
    private readonly int _threads;
    private readonly float _boost;
    private readonly Action<string> _log;
    private OfflineRecognizer? _recognizer;
    private string? _keywordKey;

    public ZipformerRecognizer(string folder, int threads, float boost, Action<string> log)
    {
        (_folder, _threads, _boost, _log) = (folder, threads, boost, log);
        BpeVocab.Write(Path.Combine(folder, "bpe.model"), Path.Combine(folder, "bpe.vocab"));
        Build([]);
    }

    public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
    {
        if (samples.Length < WhistleRecognizer.MinAudioSeconds * WhistleModel.SampleRate) return "";
        Build(keywords);
        using var stream = _recognizer!.CreateStream();
        stream.AcceptWaveform(WhistleModel.SampleRate, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim().ToLowerInvariant();
    }

    private void Build(IReadOnlyList<string> keywords)
    {
        // GigaSpeech's tokens are upper case; hotwords must be too.
        var words = keywords.Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct().ToList();
        var key = string.Join('\n', words);
        if (_recognizer is not null && key == _keywordKey) return;

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = WhistleModel.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(_folder, "encoder-epoch-30-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(_folder, "decoder-epoch-30-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(_folder, "joiner-epoch-30-avg-1.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(_folder, "tokens.txt");
        config.ModelConfig.NumThreads = _threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "transducer";
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;
        if (words.Count > 0)
        {
            var file = Path.Combine(_folder, "hotwords.txt");
            File.WriteAllLines(file, words);
            config.ModelConfig.ModelingUnit = "bpe";
            config.ModelConfig.BpeVocab = Path.Combine(_folder, "bpe.vocab");
            config.HotwordsFile = file;
            config.HotwordsScore = _boost;
        }
        _recognizer?.Dispose();
        _recognizer = new OfflineRecognizer(config);
        _keywordKey = key;
        _log($"zipformer: {words.Count} hotwords, boost {_boost}");
    }

    public void Dispose() => _recognizer?.Dispose();
}

/// <summary>Whisper through whisper.cpp (Whisper.net), biased with the profile words as the initial prompt.</summary>
internal sealed class WhisperRecognizer(string modelPath, int threads) : ISpeechToText, IDisposable
{
    // Whisper's prompt holds at most 224 tokens; this many characters stays well inside it.
    private const int MaxPromptChars = 600;

    private readonly WhisperFactory _factory = WhisperFactory.FromPath(modelPath);
    private WhisperProcessor? _processor;
    private string? _prompt;

    public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
    {
        if (samples.Length < WhistleRecognizer.MinAudioSeconds * WhistleModel.SampleRate) return "";
        var prompt = string.Join(", ", keywords);
        if (prompt.Length > MaxPromptChars) prompt = prompt[..prompt.LastIndexOf(',', MaxPromptChars)];
        if (_processor is null || prompt != _prompt)
        {
            _processor?.Dispose();
            var builder = _factory.CreateBuilder().WithLanguage("en").WithThreads(threads).WithNoContext().WithSingleSegment();
            if (prompt.Length > 0) builder = builder.WithPrompt(prompt);
            _processor = builder.Build();
            _prompt = prompt;
        }
        // Whisper wants at least 1 s of audio; pad short clips with silence.
        var audio = samples.Length >= WhistleModel.SampleRate ? samples : [.. samples, .. new float[WhistleModel.SampleRate - samples.Length]];
        var text = new StringBuilder();
        foreach (var segment in ProcessSync(audio)) text.Append(segment.Text);
        return text.ToString().Trim();
    }

    private List<SegmentData> ProcessSync(float[] audio)
    {
        var segments = new List<SegmentData>();
        var enumerator = _processor!.ProcessAsync(audio).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) segments.Add(enumerator.Current);
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return segments;
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory.Dispose();
    }
}

/// <summary>
/// Writes sentencepiece's bpe.vocab ("piece\tscore" per line) from bpe.model, which sherpa-onnx needs to turn
/// hotwords into tokens. bpe.model is a protobuf ModelProto: field 1 repeats SentencePiece {1: piece, 2: score}.
/// </summary>
internal static class BpeVocab
{
    public static void Write(string model, string vocab)
    {
        if (File.Exists(vocab)) return;
        var data = File.ReadAllBytes(model);
        var lines = new List<string>();
        var pos = 0;
        while (pos < data.Length)
        {
            var (field, wire) = Tag(data, ref pos);
            if (field == 1 && wire == 2)
            {
                var length = (int)Varint(data, ref pos);
                lines.Add(Piece(data.AsSpan(pos, length)));
                pos += length;
            }
            else Skip(data, ref pos, wire);
        }
        File.WriteAllLines(vocab, lines);
    }

    private static string Piece(ReadOnlySpan<byte> message)
    {
        string piece = "";
        float score = 0;
        var data = message.ToArray();
        var pos = 0;
        while (pos < data.Length)
        {
            var (field, wire) = Tag(data, ref pos);
            if (field == 1 && wire == 2)
            {
                var length = (int)Varint(data, ref pos);
                piece = Encoding.UTF8.GetString(data, pos, length);
                pos += length;
            }
            else if (field == 2 && wire == 5)
            {
                score = BitConverter.ToSingle(data, pos);
                pos += 4;
            }
            else Skip(data, ref pos, wire);
        }
        return $"{piece}\t{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static (int Field, int Wire) Tag(byte[] data, ref int pos)
    {
        var tag = Varint(data, ref pos);
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    private static ulong Varint(byte[] data, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80) return value;
        }
    }

    private static void Skip(byte[] data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: Varint(data, ref pos); break;
            case 1: pos += 8; break;
            case 2:
                var length = (int)Varint(data, ref pos); // read first: "pos += Varint(ref pos)" would add to the old pos
                pos += length;
                break;
            case 5: pos += 4; break;
            default: throw new InvalidDataException($"bad protobuf wire type {wire}");
        }
    }
}

internal static class Repo
{
    /// <summary>The checkout: the first folder above the program holding model/cactus-needle.</summary>
    public static string Root()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "model", "cactus-needle"))) return dir.FullName;
        throw new DirectoryNotFoundException("run stt-bench from inside the repository");
    }
}
