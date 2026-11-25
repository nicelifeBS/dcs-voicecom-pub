using System.Runtime.InteropServices;
using DcsVoiceCom.Speech;
using static DcsVoiceCom.Windows.NativeMethods;

namespace DcsVoiceCom.Windows;

/// <summary>Plays a recorded clip through winmm (Test Coms playback). The WAV stays pinned until the next clip or <see cref="Stop"/>.</summary>
public sealed class WavClipPlayer : IAudioPlayer, IDisposable
{
    private readonly object _lock = new();
    private GCHandle _pinned;

    public void Play(float[] samples, int sampleRate)
    {
        var wav = WavFile.EncodeMono16(samples, sampleRate);
        lock (_lock)
        {
            StopLocked();
            _pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
            PlaySound(_pinned.AddrOfPinnedObject(), 0, SndMemory | SndAsync | SndNoDefault);
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        if (!_pinned.IsAllocated) return;
        PlaySound(0, 0, 0); // stops the sound before its memory is freed
        _pinned.Free();
    }

    public void Dispose() => Stop();
}
