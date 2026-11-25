using System.Security.Cryptography;
using System.Text;
using DcsVoiceCom.Speech;
using Whisper.net;

namespace SttBench;

/// <summary>The engines under test, each as <see cref="ISpeechToText"/>, the seam the app already uses.</summary>
internal static class Engines
{
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
        if (!ZipformerModel.IsComplete(folder)) await ZipformerModel.DownloadAsync(folder, http, m => Console.WriteLine(m));
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
