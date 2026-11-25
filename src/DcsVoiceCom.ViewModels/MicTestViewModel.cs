using System.Globalization;
using DcsVoiceCom.Runtime;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels;

/// <summary>
/// Test Coms in the Settings dialog: press push-to-talk, say <see cref="Phrase"/>, then see what the speech engine heard, a grade,
/// the mic level, and hear the clip played back.
/// </summary>
public sealed class MicTestViewModel : ObservableObject, IDisposable
{
    public const string DefaultPhrase = "Jester set radar range to 200";

    private readonly IVoiceComRuntime? _runtime;
    private readonly IAudioPlayer? _player;
    private readonly IUiThread _ui;
    private readonly Func<string> _device;
    private readonly Func<string> _language;
    private IMicTest? _test;
    private MicCheckResult? _result;
    private bool _starting;
    private bool _listening;
    private string _status = "";
    private string _phrase = DefaultPhrase;

    /// <param name="device">The microphone picked in the dialog (empty for the Windows default).</param>
    /// <param name="language">The language picked in the dialog.</param>
    public MicTestViewModel(IVoiceComRuntime? runtime, IAudioPlayer? player, IUiThread? ui, Func<string> device, Func<string> language)
    {
        _runtime = runtime;
        _player = player;
        _ui = ui ?? InlineUiThread.Instance;
        _device = device;
        _language = language;
        ToggleCommand = new AsyncCommand(ToggleAsync, ex => Status = ex.Message, () => _runtime != null && !_starting);
        ReplayCommand = new RelayCommand(Replay, () => _player != null && _result is { Audio.Length: > 0 });
        Status = _runtime is null ? "The voice engine is not running." : "";
    }

    public AsyncCommand ToggleCommand { get; }
    public RelayCommand ReplayCommand { get; }

    public bool IsTesting => _test != null;

    public string ToggleText => IsTesting ? "Stop test" : "Test Coms";

    /// <summary>What the user should say. Editable; read at each release.</summary>
    public string Phrase
    {
        get => _phrase;
        set
        {
            if (!SetProperty(ref _phrase, value)) return;
            if (_test is { } test) test.Phrase = value;
        }
    }

    /// <summary>True while push-to-talk is held during a test.</summary>
    public bool IsListening
    {
        get => _listening;
        private set => SetProperty(ref _listening, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool HasResult => _result != null;

    public MicGrade? Grade => _result?.Grade;

    public string GradeText => _result?.Grade switch
    {
        MicGrade.Good => "GOOD",
        MicGrade.Fair => "FAIR",
        MicGrade.Poor => "POOR",
        _ => "",
    };

    public string HeardText => _result is null ? "" : _result.Heard.Trim().Length == 0 ? "(nothing)" : _result.Heard.Trim();

    public string ScoreText => _result is null ? "" : string.Create(CultureInfo.InvariantCulture,
        $"Match {_result.Score:0} (needs {_result.Threshold})");

    public string LevelText => _result is null ? "" : string.Create(CultureInfo.InvariantCulture,
        $"Peak {_result.PeakDb:0.0} dBFS · level {_result.RmsDb:0.0} dBFS · {_result.Seconds:0.0} s");

    public string HintsText => _result is null ? "" :
        _result.Hints.Count == 0 ? "Understood, and the microphone sounds fine." : string.Join("\n", _result.Hints);

    private async Task ToggleAsync()
    {
        if (_test != null)
        {
            Stop();
            Status = "Test stopped.";
            return;
        }
        if (_runtime is null) return;
        _starting = true;
        ToggleCommand.Refresh();
        Status = "Opening the microphone…";
        try
        {
            var test = await _runtime.StartMicTestAsync(_device(), _language());
            test.Phrase = Phrase;
            test.ListeningChanged += on => _ui.Post(() => OnListening(test, on));
            test.Transcribing += () => _ui.Post(() => Report(test, "Checking…"));
            test.Failed += message => _ui.Post(() => Report(test, message));
            test.Completed += result => _ui.Post(() => OnCompleted(test, result));
            _test = test;
            Status = $"Hold push-to-talk and say the phrase. Testing: {test.DeviceName}";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _starting = false;
            ToggleCommand.Refresh();
            OnPropertyChanged(nameof(IsTesting));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    private void OnListening(IMicTest test, bool on)
    {
        if (test != _test) return;
        IsListening = on;
        if (on)
        {
            _player?.Stop();
            Status = "Listening…";
        }
    }

    private void Report(IMicTest test, string status)
    {
        if (test == _test) Status = status;
    }

    private void OnCompleted(IMicTest test, MicCheckResult result)
    {
        if (test != _test) return;
        _result = result;
        Status = "Playing back what you said. Press push-to-talk to try again.";
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(Grade));
        OnPropertyChanged(nameof(GradeText));
        OnPropertyChanged(nameof(HeardText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(HintsText));
        ReplayCommand.Refresh();
        Replay();
    }

    private void Replay()
    {
        if (_result is { Audio.Length: > 0 } result) _player?.Play(result.Audio, WhistleModel.SampleRate);
    }

    /// <summary>Ends the test, so push-to-talk runs commands again. Also called when the dialog closes.</summary>
    public void Stop()
    {
        var test = _test;
        _test = null;
        test?.Dispose();
        _player?.Stop();
        IsListening = false;
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(ToggleText));
    }

    /// <summary>Starts over on the new microphone or language when they change mid-test.</summary>
    public async Task RestartIfTestingAsync()
    {
        if (_test is null) return;
        Stop();
        await ToggleCommand.ExecuteAsync();
    }

    public void Dispose() => Stop();
}
