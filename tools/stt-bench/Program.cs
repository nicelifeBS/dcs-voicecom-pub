using System.Diagnostics;
using System.Globalization;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;
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
        usage: stt-bench --engine whistle|zipformer|whisper [--real | --clips <folder>] [--threads N] [--boost X]
                         [--no-keywords] [--models <cache folder>] [--verbose]
          --profile      profile JSON for a clip set with clips.csv (file,lang,command,said)
          --real         your real-voice clips: DCS_REAL_CLIPS, else <repo>/private-clips
          --clips        folder of <slug>__NN.wav with commands.csv (default: the repo's synthetic clips;
                         a folder without commands.csv uses the repo's one, e.g. DCS_REAL_CLIPS)
          --threads      CPU threads for the engine (default: half the logical cores, at least 2)
          --boost        hotword score for zipformer (default 1.5)
          --no-keywords  no biasing towards the profile words
        """);
    return 2;
}

var repo = Repo.Root();
var clips = ClipFolder(options, repo);
if (!Directory.Exists(clips))
{
    Console.Error.WriteLine($"no clip folder {clips}");
    return 1;
}
// Two kinds of clip set:
// - clips.csv (file,lang,command,said) plus --profile: each clip names the profile command it should run, and the
//   whole profile is loaded, as in the app, so its other commands can be confused with it.
// - commands.csv (slug,lang,phrase) and <slug>__NN.wav: the profile is just those phrases.
// Subfolders count too (e.g. a recordings\ folder).
var wavs = Directory.GetFiles(clips, "*.wav", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
var labels = Path.Combine(clips, "clips.csv");
List<(string File, string Command, string Said)> tests;
List<(string Phrase, string Command)> phrases;
IReadOnlyList<string> profileKeywords = [];
if (File.Exists(labels))
{
    if (options.Profile is null)
    {
        Console.Error.WriteLine($"{labels} names profile commands: pass --profile <F-14.json>");
        return 1;
    }
    var profileFile = Path.GetFullPath(options.Profile);
    var profile = new ProfileStore(Path.GetDirectoryName(profileFile)!).LoadMerged(Path.GetFileNameWithoutExtension(profileFile))
        ?? throw new FileNotFoundException(profileFile);
    var compiled = CompiledProfile.Compile(profile);
    phrases = compiled.Entries.ToList();
    profileKeywords = compiled.Keywords;
    var known = phrases.Select(p => p.Command).ToHashSet();
    var byName = wavs.ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
    tests = [];
    foreach (var row in File.ReadAllLines(labels).Skip(1).Select(l => l.Split(',')).Where(r => r.Length >= 4 && r[1] == "en"))
    {
        if (!known.Contains(row[2])) Console.Error.WriteLine($"warning: {row[0]}: no command \"{row[2]}\" in the profile");
        else if (!byName.TryGetValue(row[0], out var file)) Console.Error.WriteLine($"warning: {row[0]} not found");
        else tests.Add((file, row[2], row[3]));
    }
}
else
{
    var commandsFile = File.Exists(Path.Combine(clips, "commands.csv")) ? Path.Combine(clips, "commands.csv")
        : Path.Combine(repo, "tests", "DcsVoiceCom.Core.Tests", "data", "clips", "commands.csv");
    var commands = File.ReadAllLines(commandsFile).Skip(1).Select(l => l.Split(','))
        .Where(r => r.Length >= 3 && r[1] == "en")
        .ToDictionary(r => r[0], r => r[2]);
    phrases = commands.Values.Select(p => (PhraseText.Normalize(p), p)).ToList();
    tests = [];
    foreach (var f in wavs)
        if (commands.TryGetValue(Path.GetFileName(f).Split("__")[0], out var phrase)) tests.Add((f, phrase, phrase));
    if (tests.Count == 0)
    {
        Console.Error.WriteLine($"no English clips of {commandsFile} in {clips}");
        Console.Error.WriteLine($"  {wavs.Count} .wav files found{(wavs.Count == 0 ? "" : ", e.g. " + string.Join(", ", wavs.Take(5).Select(f => Path.GetRelativePath(clips, f))))}");
        Console.Error.WriteLine($"  expected names like {commands.Keys.First()}__01.wav; slugs: {string.Join(", ", commands.Keys)}");
        return 1;
    }
}
var commandOf = phrases.GroupBy(p => p.Phrase).ToDictionary(g => g.Key, g => g.First().Command);

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
speech.LoadPhrases("en", phrases, profileKeywords);

var right = 0;
var missed = new List<string>();
var wrong = new List<string>();
var latencies = new List<double>();
foreach (var (clip, expected, said) in tests)
{
    microphone.Clip = WavFile.ReadMono(clip, out _);
    speech.Start();
    var result = speech.Stop();
    latencies.Add(result.Ms);
    var line = $"{Path.GetFileName(clip)}: \"{said}\" heard \"{result.Raw}\" → {result.Phrase ?? "-"} ({result.Score}) {result.Reject ?? result.Error}";
    if (options.Verbose) Console.WriteLine("  " + line);
    if (result.Error is not null || result.Reject is not null || result.Score < 85) missed.Add(line);
    else if (result.Phrase is { } heard && commandOf.GetValueOrDefault(heard) == expected) right++;
    else wrong.Add(line);
}

var process = Process.GetCurrentProcess();
process.Refresh();
latencies.Sort();
var mb = (long bytes) => (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
Console.WriteLine($"""
    engine      {options.Engine}{(options.Keywords ? "" : " (no keywords)")}, {options.Threads} threads
    clips       {tests.Count} from {clips}
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

// --real: DCS_REAL_CLIPS, else <repo>/private-clips (where tools/eval/record.py saves). --clips: a folder, relative to
// the current folder or else to the repository. Neither: the repo's synthetic clips.
static string ClipFolder(Options options, string repo)
{
    if (options.Real)
    {
        var real = Environment.GetEnvironmentVariable("DCS_REAL_CLIPS");
        return Path.GetFullPath(string.IsNullOrEmpty(real) ? Path.Combine(repo, "private-clips") : real);
    }
    if (options.Clips is null) return Path.Combine(repo, "tests", "DcsVoiceCom.Core.Tests", "data", "clips");
    if (Path.IsPathRooted(options.Clips) || Directory.Exists(options.Clips)) return Path.GetFullPath(options.Clips);
    return Path.GetFullPath(Path.Combine(repo, options.Clips));
}

internal sealed class ClipMicrophone : IMicrophone
{
    public float[] Clip = [];
    public string DeviceName => "recorded clips";
    public void Start() { }
    public float[] Stop() => Clip;
    public void Dispose() { }
}

internal sealed record Options(string Engine, string? Clips, int Threads, float Boost, bool Keywords, string? Models, bool Verbose, bool Real, string? Profile)
{
    public static Options? Parse(string[] args)
    {
        string? engine = null, clips = null, models = null;
        var threads = Math.Max(2, Environment.ProcessorCount / 2);
        var boost = 1.5f;
        var keywords = true;
        var verbose = false;
        var real = false;
        string? profile = null;
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
                case "--real": real = true; break;
                case "--profile": profile = Next(); break;
                default: return null;
            }
        }
        return engine is "whistle" or "zipformer" or "whisper" ? new Options(engine, clips, threads, boost, keywords, models, verbose, real, profile) : null;
    }
}
