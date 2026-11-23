using System.Diagnostics;
using DcsVoiceCom.Diagnostics;
using DcsVoiceCom.Engine;

namespace DcsVoiceCom.Speech;

public sealed record SpeechSettings
{
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
    /// <summary>
    /// Run Whistle at above-normal priority while it transcribes, and keep Windows from power-throttling the app.
    /// Without it a command can take seconds when DCS keeps every core busy.
    /// </summary>
    public bool BoostPriority { get; init; } = true;
    /// <summary>Debugging: log the CPU at start, and per command how busy it was and how long Whistle took.</summary>
    public bool LogCpu { get; init; }
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

    /// <summary>The last transcription with its timings; null before the first or after a too-short clip.</summary>
    public WhistleTranscript? Last { get; private set; }

    public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
    {
        Last = samples.Length < MinAudioSeconds * WhistleModel.SampleRate ? null : model.Transcribe(samples, language, keywords);
        return Last?.Text ?? "";
    }

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
    private readonly ProcessPriority? _priority;
    private readonly bool _logCpu;
    private CpuTimes? _talkStart;
    private long _talkStartAt;

    public SpeechService(ISpeechToText? recognizer, IMicrophone? microphone, string language = "en", Action<string>? log = null,
        bool phraseKeywords = true, double margin = PhraseMatcher.DefaultMargin, string? unavailableReason = null,
        IMicrophoneProvider? devices = null, ProcessPriority? priority = null, bool logCpu = false)
    {
        _priority = priority;
        _logCpu = logCpu;
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
    /// Loads Whistle and opens the microphone. Failures are logged and leave speech off; the service is still returned,
    /// so typed matching works and the reason shows on push-to-talk.
    /// </summary>
    public static async Task<SpeechService> OpenAsync(SpeechSettings settings, string language, IMicrophoneProvider? microphones,
        Action<string> log, HttpClient? http = null, CancellationToken ct = default)
    {
        WhistleRecognizer? recognizer = null;
        IMicrophone? microphone = null;
        string? error = null;
        ProcessPriority? priority = null;
        if (settings.BoostPriority)
        {
            priority = new ProcessPriority(log);
            if (ProcessPriority.DisablePowerThrottling()) log("Power throttling off for the app");
        }
        if (settings.LogCpu) log("[cpu] " + CpuInfo.Describe());
        try
        {
            var files = await FindOrDownloadModelAsync(settings, log, http, ct).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var model = await Task.Run(() => WhistleModel.Load(files.Weights, files.Engine), ct).ConfigureAwait(false);
            recognizer = new WhistleRecognizer(model);
            log($"Whistle loaded in {clock.ElapsedMilliseconds} ms ({language}): {files.Weights}");
            if (microphones is null) throw new InvalidOperationException("no microphone support on this platform");
            microphone = microphones.Open(settings.Device, settings.PreRollMs);
            log($"Microphone: {microphone.DeviceName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"speech unavailable: {ex.Message}";
            log(error);
        }
        return new SpeechService(recognizer, microphone, language, log, settings.PhraseKeywords, settings.Margin, error, microphones,
            priority, settings.LogCpu);
    }

    private static async Task<ModelFiles> FindOrDownloadModelAsync(SpeechSettings settings, Action<string> log, HttpClient? http, CancellationToken ct)
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
        using var owned = http is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : null;
        return await ModelDownloader.DownloadAsync(ModelLocator.DefaultDirectory, http ?? owned!, log, ct).ConfigureAwait(false);
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
        lock (_lock)
        {
            Language = string.IsNullOrEmpty(language) ? Language : language;
            _matcher = matcher;
            _keywords = all.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        }
        _log($"Loaded {count} phrases, {all.Count} keywords ({Language})");
        return count;
    }

    /// <summary>Push-to-talk pressed: starts keeping the microphone's audio.</summary>
    public void Start()
    {
        if (_logCpu) (_talkStart, _talkStartAt) = (CpuTimes.ReadSystem(), Stopwatch.GetTimestamp());
        _microphone?.Start();
    }

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
        var cpuBefore = _logCpu ? CpuTimes.ReadSystem() : null;
        var appBefore = _logCpu ? AppCpuTime() : TimeSpan.Zero;
        var transcribeStarted = Stopwatch.GetTimestamp();
        string text;
        string? priority = null;
        using (_priority?.Boost())
        {
            if (_logCpu) priority = ProcessPriority.Current();
            text = _recognizer.Transcribe(samples, language, keywords);
        }
        if (_logCpu) LogCpu(cpuBefore, appBefore, transcribeStarted, priority!);
        var result = Result(matcher, text, started) with { Audio = samples };
        _log($"Heard \"{text}\" → {result.Phrase ?? "-"} ({result.Score}) in {result.Ms} ms" + (result.Reject is null ? "" : $", rejected: {result.Reject}"));
        return result;
    }

    /// <summary>
    /// One line per command: how busy the CPU was while the button was held (that's DCS and the rest, not Whistle)
    /// and while Whistle ran, how many cores' worth the app got, and Whistle's own timings.
    /// </summary>
    private void LogCpu(CpuTimes? before, TimeSpan appBefore, long transcribeStarted, string priority)
    {
        var after = CpuTimes.ReadSystem();
        var elapsed = Stopwatch.GetElapsedTime(transcribeStarted);
        var talking = _talkStartAt != 0 && Stopwatch.GetElapsedTime(_talkStartAt) - elapsed >= TimeSpan.FromMilliseconds(100)
            ? CpuTimes.Utilization(_talkStart, before) : null;
        var transcribing = CpuTimes.Utilization(before, after);
        var cores = elapsed > TimeSpan.Zero ? (AppCpuTime() - appBefore) / elapsed : 0;
        var whistle = (_recognizer as WhistleRecognizer)?.Last;
        _log($"[cpu] busy while talking {Percent(talking)}, while transcribing {Percent(transcribing)}; app used {cores:0.0} of " +
            $"{Environment.ProcessorCount} cores; Whistle {elapsed.TotalMilliseconds:0} ms" +
            (whistle is null ? "" : $" (first token {whistle.TtftMs:0} ms, {whistle.DecodeTps:0.#} tokens/s)") +
            $"; priority {priority}{(_priority is null ? " (boost off)" : "")}");
        _talkStartAt = 0;
    }

    private static string Percent(double? value) => value is { } v ? $"{v:0}%" : "n/a";

    private static TimeSpan AppCpuTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
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
