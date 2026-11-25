using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DcsVoiceCom.Phrases;
using SherpaOnnx;

namespace DcsVoiceCom.Speech;

/// <summary>The speech engines the app can run. <see cref="Whistle"/> is the default.</summary>
public static class SpeechEngines
{
    public const string Whistle = "whistle";
    public const string Zipformer = "zipformer";

    public static readonly IReadOnlyList<string> All = [Whistle, Zipformer];

    /// <summary>The languages an engine knows.</summary>
    public static IReadOnlyList<string> Languages(string engine) =>
        engine == Zipformer ? ZipformerModel.Languages : WhistleModel.Languages;
}

/// <summary>
/// A recognizer that biases towards words given up front, when the profile loads, instead of with every clip
/// (building the bias takes about a second).
/// </summary>
public interface IKeywordBiased
{
    void Prepare(IReadOnlyList<string> keywords);
}

/// <summary>
/// The Zipformer model: k2-fsa/sherpa-onnx-zipformer-gigaspeech-2023-12-12 (Apache-2.0), an offline transducer
/// trained on GigaSpeech (10,000 h of English podcasts and videos), int8. Downloaded once, SHA-256 pinned.
/// </summary>
public static class ZipformerModel
{
    public const string Name = "zipformer-gigaspeech-2023-12-12";
    public static readonly IReadOnlyList<string> Languages = ["en"];

    // A fixed commit, not main: the repo's files are re-uploaded now and then (the encoder changed on 2026-11-22),
    // and the SHA-256 pins below are for this version, the one benchmarked in docs/plans/stt-engines.md.
    private const string Revision = "a284ea43c73bb4b22e4cef6f86a38ce03cbd0ff3";
    private const string Url = "https://huggingface.co/k2-fsa/sherpa-onnx-zipformer-gigaspeech-2023-12-12/resolve/" + Revision + "/";
    internal const string Encoder = "encoder-epoch-30-avg-1.int8.onnx";
    internal const string Decoder = "decoder-epoch-30-avg-1.int8.onnx";
    internal const string Joiner = "joiner-epoch-30-avg-1.int8.onnx";
    internal const string Tokens = "tokens.txt";
    internal const string Bpe = "bpe.model";

    // The same files and hashes are in src/DcsVoiceCom.App/DcsVoiceCom.App.csproj (BundleZipformer), which puts
    // them into the installer; change both together.
    private static readonly (string Name, string Sha256)[] Files =
    [
        (Encoder, "62be226a7b28a38e82cc522184ee01c46751df06bdd070dc9e1572a836ebbe78"),
        (Decoder, "2b0580feb757696fa929922670b7126aaee609630349a1809fd5a2aee926d56e"),
        (Joiner, "80160e45cca71dd52f6b0a6d3d12be18126f5308b2d4ba03f001300fea377c64"),
        (Tokens, "0ef7d736bf4de3ef947292e4b119ef13f6808cd5f3aec225a843a7135ac1c2ce"),
        (Bpe, "c8a2a0129c4ab8e463164c142f82d25649661b122c8cd0b7aab5c9e80b90ad24"),
    ];

    /// <summary>
    /// Where the app downloads it, and keeps the files it writes for any copy (bpe.vocab, hotwords.txt):
    /// %LOCALAPPDATA%\DcsVoiceCom\model\zipformer-gigaspeech-2023-12-12.
    /// </summary>
    public static string DefaultDirectory => Path.Combine(ModelLocator.DefaultDirectory, Name);

    /// <summary>The copy the installer puts next to the app: model\zipformer-gigaspeech-2023-12-12.</summary>
    public static string BundledDirectory(string appDirectory) => Path.Combine(appDirectory, "model", Name);

    /// <summary>True when <paramref name="folder"/> holds every file with its pinned SHA-256.</summary>
    public static bool IsComplete(string folder) =>
        Files.All(f => File.Exists(Path.Combine(folder, f.Name)) && ModelLocator.Sha256(Path.Combine(folder, f.Name)) == f.Sha256);

    /// <summary>Downloads the files <paramref name="folder"/> lacks (or holds with another hash); about 74 MB.</summary>
    public static async Task DownloadAsync(string folder, HttpClient http, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, sha256) in Files)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && ModelLocator.Sha256(path) == sha256) continue;
            log?.Invoke($"Downloading the Zipformer model: {name} to {folder}");
            var part = path + ".part";
            await using (var target = File.Create(part))
            await using (var source = await http.GetStreamAsync(Url + name, ct).ConfigureAwait(false))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            var actual = ModelLocator.Sha256(part);
            if (actual != sha256)
            {
                File.Delete(part);
                throw new InvalidDataException($"{name}: SHA-256 {actual}, expected {sha256}");
            }
            File.Move(part, path, overwrite: true);
        }
    }
}

/// <summary>
/// Zipformer through sherpa-onnx, biased towards the profile's words with hotwords (modified beam search plus an
/// Aho-Corasick graph of the words). sherpa-onnx takes hotwords per recognizer, so <see cref="Prepare"/> builds a
/// new one when the word list changes; a clip waits for that under the same lock.
/// </summary>
public sealed class ZipformerRecognizer : ISpeechToText, IKeywordBiased, IDisposable
{
    /// <summary>Hotword boost: 1.5 got the most right on the F-14 Jester set; 4 started to force words onto noise.</summary>
    public const float DefaultBoost = 1.5f;

    private readonly object _lock = new();
    private readonly string _folder;
    private readonly string _work;
    private readonly int _threads;
    private readonly float _boost;
    private readonly Action<string> _log;
    private OfflineRecognizer? _recognizer;
    private string? _keywordKey;

    /// <param name="folder">The model files; only read, so it may be the installed copy under Program Files.</param>
    /// <param name="workFolder">Where bpe.vocab and hotwords.txt are written (default <paramref name="folder"/>).</param>
    public ZipformerRecognizer(string folder, int threads = 0, float boost = DefaultBoost, Action<string>? log = null, string? workFolder = null)
    {
        _folder = folder;
        _work = workFolder ?? folder;
        Directory.CreateDirectory(_work);
        _threads = threads > 0 ? threads : DefaultThreads;
        _boost = boost > 0 ? boost : DefaultBoost;
        _log = log ?? (_ => { });
        BpeVocab.Write(Path.Combine(folder, ZipformerModel.Bpe), Path.Combine(_work, "bpe.vocab"));
        Prepare([]);
        // The first decode pays one-off setup; do it now instead of on the first command.
        Transcribe(new float[WhistleModel.SampleRate / 2], "en", []);
    }

    /// <summary>Half the logical cores, 2 to 4: enough for a command clip, and DCS keeps the rest.</summary>
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
    {
        if (samples.Length < WhistleRecognizer.MinAudioSeconds * WhistleModel.SampleRate) return "";
        lock (_lock)
        {
            using var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(WhistleModel.SampleRate, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text.Trim().ToLowerInvariant();
        }
    }

    public void Prepare(IReadOnlyList<string> keywords)
    {
        // GigaSpeech's tokens are upper case and have no digits: hotwords are upper case, with numbers spelled out.
        var words = keywords.SelectMany(Hotwords.Forms).Select(k => k.ToUpperInvariant()).Distinct().ToList();
        var key = string.Join('\n', words);
        lock (_lock)
        {
            if (_recognizer is not null && key == _keywordKey) return;
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = WhistleModel.SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = Path.Combine(_folder, ZipformerModel.Encoder);
            config.ModelConfig.Transducer.Decoder = Path.Combine(_folder, ZipformerModel.Decoder);
            config.ModelConfig.Transducer.Joiner = Path.Combine(_folder, ZipformerModel.Joiner);
            config.ModelConfig.Tokens = Path.Combine(_folder, ZipformerModel.Tokens);
            config.ModelConfig.NumThreads = _threads;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.ModelType = "transducer";
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
            if (words.Count > 0)
            {
                var file = Path.Combine(_work, "hotwords.txt");
                File.WriteAllLines(file, words);
                config.ModelConfig.ModelingUnit = "bpe";
                config.ModelConfig.BpeVocab = Path.Combine(_work, "bpe.vocab");
                config.HotwordsFile = file;
                config.HotwordsScore = _boost;
            }
            _recognizer?.Dispose();
            _recognizer = new OfflineRecognizer(config);
            _keywordKey = key;
        }
        if (words.Count > 0) _log($"Zipformer: {words.Count} hotwords, boost {_boost.ToString(CultureInfo.InvariantCulture)}");
    }

    public void Dispose()
    {
        lock (_lock) _recognizer?.Dispose();
    }
}

/// <summary>
/// Spells the numbers in a keyword the ways they are spoken, since a model without digits cannot be biased towards
/// "251": "two hundred and fifty one", "two hundred fifty one", "two five one" and, for three digits, "two fifty
/// one". "000" gives "zero zero zero". A keyword with several numbers gives every combination, up to
/// <see cref="MaxForms"/>.
/// </summary>
public static class Hotwords
{
    private const int MaxForms = 8;

    public static IEnumerable<string> Forms(string keyword)
    {
        var forms = new List<string> { "" };
        foreach (var word in keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var spoken = word.All(char.IsAsciiDigit) ? Number(word) : [word];
            forms = forms.SelectMany(f => spoken.Select(w => f.Length == 0 ? w : f + " " + w)).Take(MaxForms).ToList();
        }
        return forms.Where(f => f.Length > 0);
    }

    internal static List<string> Number(string digits)
    {
        var forms = new List<string>();
        var whole = digits.Length <= 4 && (digits.Length == 1 || digits[0] != '0');
        if (whole)
        {
            var words = NumberWords.English(int.Parse(digits, CultureInfo.InvariantCulture));
            forms.Add(words.Replace(" hundred ", " hundred and "));
            forms.Add(words);
        }
        if (digits.Length > 1) forms.Add(string.Join(' ', digits.Select(d => NumberWords.English(d - '0'))));
        if (digits.Length == 3 && digits[0] != '0' && digits[1] != '0')
            forms.Add(NumberWords.English(digits[0] - '0') + " " + NumberWords.English(int.Parse(digits[1..], CultureInfo.InvariantCulture)));
        return forms.Distinct().ToList();
    }
}

/// <summary>
/// Writes sentencepiece's bpe.vocab ("piece\tscore" per line) from bpe.model, which sherpa-onnx needs to turn
/// hotwords into tokens. bpe.model is a protobuf ModelProto: field 1 repeats SentencePiece {1: piece, 2: score}.
/// </summary>
public static class BpeVocab
{
    public static void Write(string model, string vocab)
    {
        if (File.Exists(vocab)) return;
        var lines = Read(File.ReadAllBytes(model)).Select(p => $"{p.Piece}\t{p.Score.ToString(CultureInfo.InvariantCulture)}");
        var part = vocab + ".part";
        File.WriteAllLines(part, lines);
        File.Move(part, vocab, overwrite: true);
    }

    internal static List<(string Piece, float Score)> Read(byte[] data)
    {
        var pieces = new List<(string, float)>();
        var pos = 0;
        while (pos < data.Length)
        {
            var (field, wire) = Tag(data, ref pos);
            if (field == 1 && wire == 2)
            {
                var length = (int)Varint(data, ref pos);
                pieces.Add(Piece(data.AsSpan(pos, length).ToArray()));
                pos += length;
            }
            else Skip(data, ref pos, wire);
        }
        return pieces;
    }

    private static (string, float) Piece(byte[] data)
    {
        var piece = "";
        float score = 0;
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
        return (piece, score);
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
