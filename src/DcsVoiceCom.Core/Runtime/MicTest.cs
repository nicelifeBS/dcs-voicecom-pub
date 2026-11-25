using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Runtime;

/// <summary>
/// A running Test Coms check. While it is open, push-to-talk records for it and no command runs.
/// Events arrive on background threads. Dispose to end it.
/// </summary>
public interface IMicTest : IDisposable
{
    /// <summary>The microphone being tested.</summary>
    string DeviceName { get; }

    /// <summary>The phrase the user should say.</summary>
    string Phrase { get; set; }

    /// <summary>True on push-to-talk press, false on release.</summary>
    event Action<bool>? ListeningChanged;

    /// <summary>The speech engine is working on the clip.</summary>
    event Action? Transcribing;

    event Action<MicCheckResult>? Completed;

    /// <summary>The clip could not be checked (too short, the speech engine failed).</summary>
    event Action<string>? Failed;
}

/// <summary>Runs one push-to-talk clip at a time through the speech engine and <see cref="MicCheck"/>.</summary>
internal sealed class MicTestSession(SpeechService speech, IMicrophone microphone, bool ownsMicrophone, string language, int threshold,
    Action<MicTestSession> closed) : IMicTest
{
    private readonly object _lock = new();
    private bool _listening;
    private bool _disposed;
    private string _phrase = "";

    public string DeviceName => microphone.DeviceName;

    public string Phrase
    {
        get
        {
            lock (_lock) return _phrase;
        }
        set
        {
            lock (_lock) _phrase = value ?? "";
        }
    }

    public event Action<bool>? ListeningChanged;
    public event Action? Transcribing;
    public event Action<MicCheckResult>? Completed;
    public event Action<string>? Failed;

    public void Press()
    {
        lock (_lock)
        {
            if (_disposed) return;
            microphone.Start();
            _listening = true;
        }
        ListeningChanged?.Invoke(true);
    }

    public void Release()
    {
        float[] samples;
        string phrase;
        lock (_lock)
        {
            if (_disposed || !_listening) return;
            _listening = false;
            samples = microphone.Stop();
            phrase = _phrase;
        }
        ListeningChanged?.Invoke(false);
        if (samples.Length < WhistleModel.SampleRate / 10)
        {
            Failed?.Invoke("Too short. Hold push-to-talk while you speak.");
            return;
        }
        Transcribing?.Invoke();
        _ = Task.Run(() =>
        {
            try
            {
                var heard = speech.Transcribe(samples, language, MicCheck.Keywords(phrase));
                Completed?.Invoke(MicCheck.Grade(phrase, heard, samples, threshold));
            }
            catch (Exception ex)
            {
                Failed?.Invoke($"Speech engine failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_listening) microphone.Stop();
            _listening = false;
            if (ownsMicrophone) microphone.Dispose();
        }
        closed(this);
    }
}
