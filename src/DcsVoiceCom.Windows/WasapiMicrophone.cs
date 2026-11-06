using DcsVoiceCom.Speech;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DcsVoiceCom.Windows;

/// <summary>Lists and opens microphones through WASAPI.</summary>
public sealed class WasapiMicrophoneProvider : IMicrophoneProvider
{
    public IReadOnlyList<string> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => d.FriendlyName).Distinct().ToList();
    }

    public IMicrophone Open(string? device, int preRollMs)
    {
        using var enumerator = new MMDeviceEnumerator();
        return new WasapiMicrophone(Find(enumerator, device), preRollMs);
    }

    private static MMDevice Find(MMDeviceEnumerator enumerator, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new InvalidOperationException($"Windows has no default microphone; plug one in or pick one in Settings ({ex.Message})", ex);
            }
        }
        var wanted = spec.Trim();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        // Exact name, else part of one. A setting saved by the old Python sidecar may hold an MME name cut at 31 characters,
        // which is the start of the full one.
        return devices.FirstOrDefault(d => d.FriendlyName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                           || wanted.Length >= 20 && d.FriendlyName.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"no microphone matches '{spec}'; available: {string.Join(", ", devices.Select(d => d.FriendlyName).DefaultIfEmpty("none"))}");
    }
}

/// <summary>
/// A WASAPI shared-mode capture stream (so SRS and Discord keep the mic) that stays open, mixed down to mono and
/// resampled to 16 kHz into a <see cref="PushToTalkBuffer"/>.
/// </summary>
public sealed class WasapiMicrophone : IMicrophone
{
    private readonly WasapiCapture _capture;
    private readonly PushToTalkBuffer _buffer;
    private readonly WdlResampler? _resampler;
    private readonly WaveFormat _format;
    private float[] _mono = [];
    private float[] _resampled = [];

    public WasapiMicrophone(MMDevice device, int preRollMs = 200)
    {
        DeviceName = device.FriendlyName;
        _buffer = new PushToTalkBuffer(WhistleModel.SampleRate, preRollMs);
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        _format = _capture.WaveFormat;
        if (_format.SampleRate != WhistleModel.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true); // driven by the input we have
            _resampler.SetRates(_format.SampleRate, WhistleModel.SampleRate);
        }
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    public string DeviceName { get; }

    public void Start() => _buffer.Start();

    public float[] Stop() => _buffer.Stop();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var frames = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (frames == 0) return;
        if (_resampler is null)
        {
            _buffer.Write(_mono.AsSpan(0, frames));
            return;
        }
        _resampler.ResamplePrepare(frames, 1, out var input, out var inputOffset);
        Array.Copy(_mono, 0, input, inputOffset, frames);
        var capacity = (int)((long)frames * WhistleModel.SampleRate / _format.SampleRate) + 64;
        if (_resampled.Length < capacity) _resampled = new float[capacity];
        var produced = _resampler.ResampleOut(_resampled, 0, frames, _resampled.Length, 1);
        _buffer.Write(_resampled.AsSpan(0, produced));
    }

    /// <summary>Decodes one block (float or 16/24/32-bit PCM, any channel count) into <see cref="_mono"/>.</summary>
    private int ToMono(ReadOnlySpan<byte> data)
    {
        var channels = _format.Channels;
        var width = _format.BitsPerSample / 8;
        var frames = data.Length / (width * channels);
        if (_mono.Length < frames) _mono = new float[frames];
        var isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat
                      || _format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var s = data.Slice((f * channels + c) * width, width);
                sum += isFloat ? BitConverter.ToSingle(s)
                    : width switch
                    {
                        2 => BitConverter.ToInt16(s) / 32768f,
                        3 => ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 8388608f,
                        _ => BitConverter.ToInt32(s) / 2147483648f,
                    };
            }
            _mono[f] = sum / channels;
        }
        return frames;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        _capture.StopRecording();
        _capture.Dispose();
    }
}
