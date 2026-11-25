using System.Diagnostics;
using DcsVoiceCom.Engine;

namespace DcsVoiceCom.Speech;

public sealed record SpeechSettings
{
    /// <summary>
    /// The speech engine: <see cref="SpeechEngines.Whistle"/> (default, 7 languages) or <see cref="SpeechEngines.Zipformer"/>
    /// (English only, downloaded on first use). If the chosen one can't load, Whistle runs instead.
    /// </summary>
    public string Engine { get; init; } = SpeechEngines.Whistle;
    /// <summary>Zipformer only: how strongly it leans towards the profile's words.</summary>
    public float HotwordBoost { get; init; } = ZipformerRecognizer.DefaultBoost;
    /// <summary>
    /// Folder holding the Whistle model in the cactus-needle cache layout (whistle\..., v3\...). Empty: found by
    /// <see cref="ModelLocator.ResolveDirectory"/>, and downloaded to <see cref="ModelLocator.DefaultDirectory"/> if missing.
    /// </summary>
    public string? ModelDirectory { get; init; }
    /// <summary>Microphone: part of its name. Empty: the Windows default input.</summary>
    public string? Device { get; init; }
    /// <summary>Audio kept from just before the push-to-talk press.</summary>
    public int PreRollMs { get; init; } = 200;
    /// <summary>Bias Whistle towards the profile's phrase words, not only its keywords.</summary>
    public bool PhraseKeywords { get; init; } = true;
    /// <summary>Lead the best command needs over every other one before its own words must decide.</summary>
    public double Margin { get; init; } = PhraseMatcher.DefaultMargin;
    /// <summary>Download the model when no folder has it.</summary>
    public bool AutoDownload { get; init; } = true;
}

/// <summary>Push-to-talk capture: 16 kHz mono float samples between <see cref="Start"/> and <see cref="Stop"/>.</summary>
public interface IMicrophone : IDisposable
{
    string DeviceName { get; }
    void Start();
    float[] Stop();
}

public interface IMicrophoneProvider
{
    IReadOnlyList<string> ListDevices();

    /// <summary>Opens <paramref name="device"/> (part of a name; null or empty for the default input) and keeps it open.</summary>
    IMicrophone Open(string? device, int preRollMs);
}

public interface ISpeechToText
{
    string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords);
}

/// <summary>Whistle as <see cref="ISpeechToText"/>.</summary>
public sealed class WhistleRecognizer(WhistleModel model) : ISpeechToText, IDisposable
{
    /// <summary>Shorter clips cannot hold a word; Whistle tends to invent one.</summary>
    public const double MinAudioSeconds = 0.15;

    public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords) =>
        samples.Length < MinAudioSeconds * WhistleModel.SampleRate ? "" : model.Transcribe(samples, language, keywords).Text;

    public void Dispose() => model.Dispose();
}

/// <summary>
/// Speech recognition in process: the microphone, Whistle and the phrase matcher. Push-to-talk calls
/// <see cref="Start"/> and <see cref="Stop"/>; the test panel calls <see cref="Match"/> with typed text. Without a
/// model or microphone, <see cref="Stop"/> returns <see cref="UnavailableReason"/> as the error and typed matching
/// still works.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly object _lock = new();
    private readonly ISpeechToText? _recognizer;
    private readonly IMicrophone? _microphone;
    private readonly IMicrophoneProvider? _devices;
    private readonly Action<string> _log;
    private readonly bool _phraseKeywords;
    private PhraseMatcher _matcher;
    private IReadOnlyList<string> _keywords = [];

    public SpeechService(ISpeechToText? recognizer, IMicrophone? microphone, string language = "en", Action<string>? log = null,
        bool phraseKeywords = true, double margin = PhraseMatcher.DefaultMargin, string? unavailableReason = null,
        IMicrophoneProvider? devices = null)
    {
        _recognizer = recognizer;
        _microphone = microphone;
        _devices = devices;
        Language = language;
        _log = log ?? (_ => { });
        _phraseKeywords = phraseKeywords;
        _matcher = new PhraseMatcher(margin);
        UnavailableReason = recognizer is not null && microphone is not null ? null
            : unavailableReason ?? "speech unavailable: no recognizer or microphone";
    }

    /// <summary>
    /// Loads the chosen speech engine (Whistle, or Zipformer with Whistle as the fallback) and opens the microphone. Failures are logged and leave speech off; the service is still returned,
    /// so typed matching works and the reason shows on push-to-talk.
    /// </summary>
    public static async Task<SpeechService> OpenAsync(SpeechSettings settings, string language, IMicrophoneProvider? microphones,
        Action<string> log, HttpClient? http = null, CancellationToken ct = default)
    {
        ISpeechToText? recognizer = null;
        IMicrophone? microphone = null;
        string? error = null;
        using var owned = http is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : null;
        http ??= owned!;
        try
        {
            recognizer = await OpenEngineAsync(settings, language, log, http, ct).ConfigureAwait(false);
            if (microphones is null) throw new InvalidOperationException("no microphone support on this platform");
            microphone = microphones.Open(settings.Device, settings.PreRollMs);
            log($"Microphone: {microphone.DeviceName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"speech unavailable: {ex.Message}";
            log(error);
        }
        return new SpeechService(recognizer, microphone, language, log, settings.PhraseKeywords, settings.Margin, error, microphones);
    }

    /// <summary>The chosen engine, or Whistle when that one can't run (another language, no download, no library).</summary>
    private static async Task<ISpeechToText> OpenEngineAsync(SpeechSettings settings, string language, Action<string> log, HttpClient http, CancellationToken ct)
    {
        if (settings.Engine == SpeechEngines.Zipformer)
        {
            if (!ZipformerModel.Languages.Contains(language))
                log($"Zipformer only knows English, not {language}; using Whistle");
            else
            {
                try
                {
                    // The installer's copy, else a downloaded one.
                    var folder = ZipformerModel.BundledDirectory(AppContext.BaseDirectory);
                    if (!ZipformerModel.IsComplete(folder)) folder = ZipformerModel.DefaultDirectory;
                    if (!ZipformerModel.IsComplete(folder))
                    {
                        if (!settings.AutoDownload) throw new FileNotFoundException($"no Zipformer model in {folder}");
                        await ZipformerModel.DownloadAsync(folder, http, log, ct).ConfigureAwait(false);
                    }
                    var clock = Stopwatch.StartNew();
                    var zipformer = await Task.Run(() => new ZipformerRecognizer(folder, boost: settings.HotwordBoost, log: log,
                        workFolder: ZipformerModel.DefaultDirectory), ct).ConfigureAwait(false);
                    log($"Zipformer loaded in {clock.ElapsedMilliseconds} ms ({language}, {ZipformerRecognizer.DefaultThreads} threads): {folder}");
                    return zipformer;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"Zipformer can't run ({ex.Message}); using Whistle");
                }
            }
        }
        else if (settings.Engine != SpeechEngines.Whistle)
            log($"Unknown speech engine \"{settings.Engine}\"; using Whistle");

        var files = await FindOrDownloadModelAsync(settings, log, http, ct).ConfigureAwait(false);
        var whistleClock = Stopwatch.StartNew();
        var model = await Task.Run(() => WhistleModel.Load(files.Weights, files.Engine), ct).ConfigureAwait(false);
        log($"Whistle loaded in {whistleClock.ElapsedMilliseconds} ms ({language}): {files.Weights}");
        return new WhistleRecognizer(model);
    }

    private static async Task<ModelFiles> FindOrDownloadModelAsync(SpeechSettings settings, Action<string> log, HttpClient http, CancellationToken ct)
    {
        var folder = ModelLocator.ResolveDirectory(settings.ModelDirectory, AppContext.BaseDirectory);
        if (folder is not null)
        {
            var trusted = string.IsNullOrWhiteSpace(settings.ModelDirectory) ? ModelDownloader.PinnedSha256 : null;
            if (ModelLocator.Find(folder, trusted) is { } found)
            {
                if (string.IsNullOrWhiteSpace(settings.ModelDirectory)) log($"Whistle model folder (found): {folder}");
                return found;
            }
            throw new FileNotFoundException($"no whistle\\<version>\\whistle.cact and v3\\<version>\\{ModelLocator.EngineFileName} in {folder}");
        }
        if (!settings.AutoDownload)
            throw new FileNotFoundException("Whistle model not found; set the model folder in Settings");
        return await ModelDownloader.DownloadAsync(ModelLocator.DefaultDirectory, http, log, ct).ConfigureAwait(false);
    }

    public string Language { get; private set; }

    /// <summary>Why push-to-talk can't work (no model, no microphone), or null when it can.</summary>
    public string? UnavailableReason { get; }

    public bool IsReady => UnavailableReason is null;

    /// <summary>Loads the active profile's phrases; each comes with its command's name, and phrases of one command never compete.</summary>
    public int LoadPhrases(string language, IEnumerable<(string Phrase, string Command)> phrases, IEnumerable<string> keywords)
    {
        var list = phrases.ToList();
        var matcher = new PhraseMatcher(_matcher.Margin);
        var count = matcher.Load(list.Select(p => p.Phrase).ToList(), list.Select(p => p.Command).ToList());
        var all = keywords.ToList();
        if (_phraseKeywords) all = all.Concat(matcher.LiteralRuns()).Distinct().ToList();
        var cleaned = all.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        (_recognizer as IKeywordBiased)?.Prepare(cleaned);
        lock (_lock)
        {
            Language = string.IsNullOrEmpty(language) ? Language : language;
            _matcher = matcher;
            _keywords = cleaned;
        }
        _log($"Loaded {count} phrases, {all.Count} keywords ({Language})");
        return count;
    }

    /// <summary>Push-to-talk pressed: starts keeping the microphone's audio.</summary>
    public void Start() => _microphone?.Start();

    /// <summary>Push-to-talk released: transcribes the clip and matches it against the profile.</summary>
    public RecognitionResult Stop()
    {
        var started = Stopwatch.GetTimestamp();
        if (_recognizer is null || _microphone is null) return new RecognitionResult { Error = UnavailableReason };
        var samples = _microphone.Stop();
        if (samples.Length < WhistleModel.SampleRate / 10) return new RecognitionResult { Error = "too short" };
        PhraseMatcher matcher;
        IReadOnlyList<string> keywords;
        string language;
        lock (_lock) (matcher, keywords, language) = (_matcher, _keywords, Language);
        var text = _recognizer.Transcribe(samples, language, keywords);
        var result = Result(matcher, text, started) with { Audio = samples };
        _log($"Heard \"{text}\" → {result.Phrase ?? "-"} ({result.Score}) in {result.Ms} ms" + (result.Reject is null ? "" : $", rejected: {result.Reject}"));
        return result;
    }

    /// <summary>Matches typed text as if it had been spoken (test panel and replay).</summary>
    public RecognitionResult Match(string text)
    {
        var started = Stopwatch.GetTimestamp();
        PhraseMatcher matcher;
        lock (_lock) matcher = _matcher;
        return Result(matcher, text, started);
    }

    /// <summary>Microphones that can be opened (empty without microphone support).</summary>
    public IReadOnlyList<string> ListDevices() => _devices?.ListDevices() ?? [];

    private static RecognitionResult Result(PhraseMatcher matcher, string text, long started)
    {
        var decision = matcher.Decide(text);
        var best = decision.Matches.Count > 0 ? decision.Matches[0] : null;
        return new RecognitionResult
        {
            Phrase = best?.Phrase,
            Score = best is null ? 0 : Math.Round(best.Score, 1),
            Slots = best?.Slots ?? "",
            Raw = text,
            Ms = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            Candidates = decision.Matches.Skip(1)
                .Select(m => new RecognitionCandidate { Phrase = m.Phrase, Score = Math.Round(m.Score, 1), Slots = m.Slots }).ToList(),
            Reject = decision.Reject,
        };
    }

    public void Dispose()
    {
        _microphone?.Dispose();
        (_recognizer as IDisposable)?.Dispose();
    }
}
