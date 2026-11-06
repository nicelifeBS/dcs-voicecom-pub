namespace DcsVoiceCom.Speech;

/// <summary>
/// Keeps microphone audio only between <see cref="Start"/> and <see cref="Stop"/>, plus a short pre-roll from just
/// before the press so a word started with the button is not clipped. The capture thread calls <see cref="Write"/>
/// with every block while the stream stays open, so pressing push-to-talk costs no device latency. A recording is
/// capped (15 s by default), so a missed release cannot grow it without limit.
/// </summary>
public sealed class PushToTalkBuffer
{
    private readonly object _lock = new();
    private readonly float[] _preRoll;
    private int _preRollStart, _preRollCount;
    private List<float>? _recording;

    public PushToTalkBuffer(int sampleRate = WhistleModel.SampleRate, int preRollMs = 200, int maxRecordingMs = 15_000)
    {
        SampleRate = sampleRate;
        _preRoll = new float[Math.Max(0, sampleRate * preRollMs / 1000)];
        MaxSamples = Math.Max(_preRoll.Length, (int)((long)sampleRate * maxRecordingMs / 1000));
    }

    public int SampleRate { get; }

    /// <summary>The most samples one recording keeps; audio past it is dropped.</summary>
    public int MaxSamples { get; }

    public bool IsRecording
    {
        get { lock (_lock) return _recording is not null; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (_recording is not null)
            {
                var room = MaxSamples - _recording.Count;
                if (samples.Length > room) samples = samples[..Math.Max(0, room)];
                foreach (var s in samples) _recording.Add(s);
                return;
            }
            if (_preRoll.Length == 0) return;
            if (samples.Length > _preRoll.Length) samples = samples[^_preRoll.Length..];
            foreach (var s in samples)
            {
                _preRoll[(_preRollStart + _preRollCount) % _preRoll.Length] = s;
                if (_preRollCount < _preRoll.Length) _preRollCount++;
                else _preRollStart = (_preRollStart + 1) % _preRoll.Length;
            }
        }
    }

    /// <summary>Push-to-talk pressed: starts keeping audio, beginning with the pre-roll.</summary>
    public void Start()
    {
        lock (_lock)
        {
            var recording = new List<float>(Math.Min(SampleRate * 4, MaxSamples));
            for (var i = 0; i < _preRollCount; i++) recording.Add(_preRoll[(_preRollStart + i) % _preRoll.Length]);
            _preRollStart = _preRollCount = 0;
            _recording = recording;
        }
    }

    /// <summary>Push-to-talk released: the audio since <see cref="Start"/>; empty if it was never started.</summary>
    public float[] Stop()
    {
        lock (_lock)
        {
            var samples = _recording?.ToArray() ?? [];
            _recording = null;
            return samples;
        }
    }
}
