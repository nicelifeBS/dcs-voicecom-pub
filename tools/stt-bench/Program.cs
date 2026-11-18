using System.Diagnostics;
using System.Globalization;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Speech;
using SttBench;

// Scores one speech engine on a folder of clips, the same way WhistleEndToEndTests does (English rows only):
// right, wrong (another command ran) and missed, plus load time, latency and peak memory.
// One engine per run, so the peak working set belongs to that engine alone.
//
//   dotnet run -c Release --project tools/stt-bench -- --engine zipformer [--clips <folder>] [--boost 1.5] [--threads 4]

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("""
        usage: stt-bench --engine whistle|zipformer|whisper [--clips <folder>] [--threads N] [--boost X]
                         [--no-keywords] [--models <cache folder>] [--verbose]
          --clips        folder of <slug>__NN.wav with commands.csv (default: the repo's synthetic clips;
                         a folder without commands.csv uses the repo's one, e.g. DCS_REAL_CLIPS)
          --threads      CPU threads for the engine (default: half the logical cores, at least 2)
          --boost        hotword score for zipformer (default 1.5)
          --no-keywords  no biasing towards the profile words
        """);
    return 2;
}

var repo = Repo.Root();
var clips = options.Clips ?? Path.Combine(repo, "tests", "DcsVoiceCom.Core.Tests", "data", "clips");
var commandsFile = File.Exists(Path.Combine(clips, "commands.csv")) ? Path.Combine(clips, "commands.csv")
    : Path.Combine(repo, "tests", "DcsVoiceCom.Core.Tests", "data", "clips", "commands.csv");
var commands = File.ReadAllLines(commandsFile).Skip(1).Select(l => l.Split(','))
    .Where(r => r.Length >= 3 && r[1] == "en")
    .ToDictionary(r => r[0], r => r[2]);
var files = Directory.GetFiles(clips, "*.wav").Order(StringComparer.Ordinal)
    .Where(f => commands.ContainsKey(Path.GetFileName(f).Split("__")[0])).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"no English clips of {commandsFile} in {clips}");
    return 1;
}

var cache = options.Models ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcsVoiceCom", "bench-models");
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var log = options.Verbose ? (Action<string>)(m => Console.WriteLine("  " + m)) : _ => { };

var baseline = Process.GetCurrentProcess();
baseline.Refresh();
var before = baseline.WorkingSet64;

var load = Stopwatch.StartNew();
ISpeechToText engine = options.Engine switch
{
    "whistle" => Engines.Whistle(repo),
    "zipformer" => await Engines.ZipformerAsync(cache, http, options.Threads, options.Boost, log),
    "whisper" => await Engines.WhisperAsync(cache, http, options.Threads, log),
    _ => throw new ArgumentException($"unknown engine {options.Engine}"),
};
var loadMs = load.ElapsedMilliseconds;

var microphone = new ClipMicrophone();
using var speech = new SpeechService(engine, microphone, "en", log, phraseKeywords: options.Keywords);
speech.LoadPhrases("en", commands.Values.Select(p => (PhraseText.Normalize(p), p)), []);

var right = 0;
var missed = new List<string>();
var wrong = new List<string>();
var latencies = new List<double>();
foreach (var clip in files)
{
    var said = commands[Path.GetFileName(clip).Split("__")[0]];
    microphone.Clip = WavFile.ReadMono(clip, out _);
    speech.Start();
    var result = speech.Stop();
    latencies.Add(result.Ms);
    var line = $"{Path.GetFileName(clip)}: \"{said}\" heard \"{result.Raw}\" → {result.Phrase ?? "-"} ({result.Score}) {result.Reject ?? result.Error}";
    if (options.Verbose) Console.WriteLine("  " + line);
    if (result.Error is not null || result.Reject is not null || result.Score < 85) missed.Add(line);
    else if (result.Phrase == PhraseText.Normalize(said)) right++;
    else wrong.Add(line);
}

var process = Process.GetCurrentProcess();
process.Refresh();
latencies.Sort();
var mb = (long bytes) => (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
Console.WriteLine($"""
    engine      {options.Engine}{(options.Keywords ? "" : " (no keywords)")}, {options.Threads} threads
    clips       {files.Count} from {clips}
    right       {right}
    wrong       {wrong.Count}
    missed      {missed.Count}
    load        {loadMs} ms
    latency     mean {latencies.Average():0} ms, p95 {latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1]:0} ms, max {latencies[^1]:0} ms
    memory      peak working set {mb(process.PeakWorkingSet64)} MB (process at start {mb(before)} MB)
    """);
if (wrong.Count > 0) Console.WriteLine("WRONG:\n  " + string.Join("\n  ", wrong));
if (missed.Count > 0) Console.WriteLine("MISSED:\n  " + string.Join("\n  ", missed));
(engine as IDisposable)?.Dispose();
return 0;

internal sealed class ClipMicrophone : IMicrophone
{
    public float[] Clip = [];
    public string DeviceName => "recorded clips";
    public void Start() { }
    public float[] Stop() => Clip;
    public void Dispose() { }
}

internal sealed record Options(string Engine, string? Clips, int Threads, float Boost, bool Keywords, string? Models, bool Verbose)
{
    public static Options? Parse(string[] args)
    {
        string? engine = null, clips = null, models = null;
        var threads = Math.Max(2, Environment.ProcessorCount / 2);
        var boost = 1.5f;
        var keywords = true;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--engine": engine = Next(); break;
                case "--clips": clips = Next(); break;
                case "--threads": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--boost": boost = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-keywords": keywords = false; break;
                case "--models": models = Next(); break;
                case "--verbose": verbose = true; break;
                default: return null;
            }
        }
        return engine is "whistle" or "zipformer" or "whisper" ? new Options(engine, clips, threads, boost, keywords, models, verbose) : null;
    }
}
