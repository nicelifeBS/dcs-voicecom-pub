using DcsVoiceCom.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace DcsVoiceCom.Windows;

/// <summary>
/// Text to speech with the Windows OneCore voices (<see cref="SpeechSynthesizer"/>), played through WASAPI on the
/// chosen output device. Replies play one after another; <see cref="Stop"/> cuts off the current one and clears the queue.
/// </summary>
public sealed class OneCoreTextToSpeech : ITextToSpeech
{
    private sealed record Item(string Text, TtsOptions Options, TaskCompletionSource Done, CancellationToken Caller);

    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Queue<Item> _queue = new();
    private CancellationTokenSource? _playing;
    private bool _running;
    private IReadOnlyList<VoiceInfo>? _voices;
    private string? _warnedDevice;

    public OneCoreTextToSpeech(Action<string>? log = null) => _log = log ?? (_ => { });

    public IReadOnlyList<VoiceInfo> Voices
    {
        get
        {
            lock (_lock) return _voices ??= LoadVoices();
        }
    }

    public IReadOnlyList<string> OutputDevices
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(d => d.FriendlyName).Distinct().ToList();
            }
            catch (Exception ex)
            {
                _log($"[tts] can't list output devices: {ex.Message}");
                return [];
            }
        }
    }

    private IReadOnlyList<VoiceInfo> LoadVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language, v.Gender.ToString()))
                .ToList();
        }
        catch (Exception ex)
        {
            // A missing feature or a stripped-down Windows: no voices, everything else keeps working.
            _log($"[tts] no Windows voices available: {ex.Message}");
            return [];
        }
    }

    public Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default)
    {
        var item = new Item(text, options, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        lock (_lock)
        {
            _queue.Enqueue(item);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(PumpAsync);
            }
        }
        return item.Done.Task;
    }

    public void Stop()
    {
        List<Item> dropped;
        lock (_lock)
        {
            dropped = [.. _queue];
            _queue.Clear();
            _playing?.Cancel();
        }
        foreach (var item in dropped) item.Done.TrySetResult();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Item item;
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out item!))
                {
                    _running = false;
                    return;
                }
                cts = CancellationTokenSource.CreateLinkedTokenSource(item.Caller);
                _playing = cts;
            }
            try
            {
                await PlayAsync(item, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cut off
            }
            catch (Exception ex)
            {
                _log($"[tts] can't speak: {ex.Message}");
            }
            finally
            {
                lock (_lock) _playing = null;
                cts.Dispose();
                item.Done.TrySetResult();
            }
        }
    }

    private async Task PlayAsync(Item item, CancellationToken ct)
    {
        using var synth = new SpeechSynthesizer();
        if (SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == item.Options.VoiceId) is { } voice) synth.Voice = voice;
        synth.Options.SpeakingRate = Math.Clamp(item.Options.Rate, 0.5, 2.0);
        synth.Options.AudioVolume = Math.Clamp(item.Options.Volume, 0.0, 1.0);

        using var wav = new MemoryStream();
        using (var stream = await synth.SynthesizeTextToStreamAsync(item.Text).AsTask(ct).ConfigureAwait(false))
        using (var read = stream.AsStreamForRead())
            await read.CopyToAsync(wav, ct).ConfigureAwait(false);
        wav.Position = 0;
        ct.ThrowIfCancellationRequested();

        using var reader = new WaveFileReader(wav);
        using var enumerator = new MMDeviceEnumerator();
        using var device = FindDevice(enumerator, item.Options.Device);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => finished.TrySetResult();
        output.Init(reader);
        using (ct.Register(() => output.Stop()))
        {
            output.Play();
            await finished.Task.ConfigureAwait(false);
        }
    }

    /// <summary>The named render device, else the Windows default (with one log line when a saved device is gone).</summary>
    private MMDevice FindDevice(MMDeviceEnumerator enumerator, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var wanted = name.Trim();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var hit = devices.FirstOrDefault(d => d.FriendlyName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            if (_warnedDevice != wanted)
            {
                _warnedDevice = wanted;
                _log($"[tts] output device '{wanted}' not found, using the Windows default");
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }
}
