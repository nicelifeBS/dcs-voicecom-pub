using System.Globalization;
using System.Text;
using System.Threading.Channels;
using DcsVoiceCom.Engine;

namespace DcsVoiceCom.Speech;

/// <summary>
/// Debug aid: saves the audio of a missed command (WAV plus a text note) so you can tell a poor microphone from
/// weak recognition. <see cref="Save"/> only queues the clip; a background task writes it, so a miss never delays
/// the next command. If the writer falls behind, new clips are dropped. Old clips are deleted past
/// <see cref="MaxClips"/>.
/// </summary>
public sealed class MissRecorder : IAsyncDisposable
{
    public const int MaxClips = 200;
    private const int QueueSize = 16;

    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly Channel<Clip> _queue = Channel.CreateBounded<Clip>(
        new BoundedChannelOptions(QueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _writer;
    private readonly int _maxClips;

    private sealed record Clip(float[] Samples, DateTime At, RecognitionResult Recognition, string Reason);

    public MissRecorder(string directory, Action<string>? log = null, int maxClips = MaxClips)
    {
        _directory = directory;
        _log = log ?? (_ => { });
        _maxClips = maxClips;
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>Queues a missed clip. Returns at once and never throws.</summary>
    public void Save(RecognitionResult recognition, string reason)
    {
        if (recognition.Audio is not { Length: > 0 } samples) return;
        _queue.Writer.TryWrite(new Clip(samples, DateTime.Now, recognition, reason));
    }

    private async Task WriteLoop()
    {
        await foreach (var clip in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Write(clip);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"Saving the missed audio failed: {ex.Message}");
            }
        }
    }

    private void Write(Clip clip)
    {
        Directory.CreateDirectory(_directory);
        var name = Path.Combine(_directory, clip.At.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        WavFile.WriteMono16(name + ".wav", clip.Samples, WhistleModel.SampleRate);
        File.WriteAllText(name + ".txt", Describe(clip), Encoding.UTF8);
        _log($"Saved missed audio: {name}.wav");
        Prune();
    }

    private static string Describe(Clip clip)
    {
        double peak = 0, sum = 0;
        foreach (var s in clip.Samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
            sum += (double)s * s;
        }
        var rms = Math.Sqrt(sum / clip.Samples.Length);
        var r = clip.Recognition;
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(inv, $"Heard:    {r.Raw}");
        text.AppendLine(inv, $"Reason:   {clip.Reason}");
        text.AppendLine(inv, $"Best:     {r.Phrase ?? "-"} (score {r.Score})");
        foreach (var c in r.Candidates.Take(3)) text.AppendLine(inv, $"Next:     {c.Phrase} (score {c.Score})");
        text.AppendLine(inv, $"Length:   {(double)clip.Samples.Length / WhistleModel.SampleRate:0.00} s");
        text.AppendLine(inv, $"Peak:     {Db(peak):0.0} dBFS (near 0 = clipping, below -30 = too quiet)");
        text.AppendLine(inv, $"Level:    {Db(rms):0.0} dBFS RMS");
        return text.ToString();
    }

    private static double Db(double value) => value <= 0 ? -120 : 20 * Math.Log10(value);

    private void Prune()
    {
        var old = Directory.GetFiles(_directory, "*.wav").Order(StringComparer.Ordinal).SkipLast(_maxClips);
        foreach (var wav in old)
        {
            try
            {
                File.Delete(wav);
                File.Delete(Path.ChangeExtension(wav, ".txt"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (an audio editor has it open): the next miss tries again.
            }
        }
    }

    /// <summary>Writes what is queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}
