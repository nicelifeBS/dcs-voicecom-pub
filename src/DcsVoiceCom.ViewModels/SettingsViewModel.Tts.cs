using System.Collections.ObjectModel;
using System.Globalization;
using DcsVoiceCom.Config;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels;

/// <summary>A choice in the voice-language list: <see cref="Tag"/> null means "same as recognition".</summary>
public sealed record TtsLanguageChoice(string? Tag, string Text)
{
    public override string ToString() => Text;
}

/// <summary>The VOICE REPLIES card of the Settings dialog.</summary>
public sealed partial class SettingsViewModel
{
    public const string DefaultOutput = "Windows default output";
    public const string DefaultPreviewText = "Gear up. Radio set to two five one point zero.";
    public const string AddVoiceHelp = "Add one in Windows Settings › Time & language › Speech › Manage voices, then reopen Settings.";
    public const string SpeechSettingsUri = "ms-settings:speech";

    private ITextToSpeech _tts = new NullTextToSpeech();
    private Action<string>? _openUri;
    private IReadOnlyList<VoiceInfo> _allVoices = [];
    private List<string> _allOutputs = [];
    private string? _ttsLanguageTag;
    private string? _ttsVoiceId;
    private string _ttsDevice = "";
    private bool _ttsEnabled = true;
    private double _ttsRate = 1.0;
    private double _ttsVolume = 1.0;
    private string _previewText = DefaultPreviewText;
    private string _ttsOutputSearch = "";
    private TtsLanguageChoice? _selectedTtsLanguage;
    private VoiceInfo? _selectedVoice;

    public ObservableCollection<TtsLanguageChoice> TtsLanguages { get; } = [];
    public ObservableCollection<VoiceInfo> TtsVoices { get; } = [];
    public ObservableCollection<string> TtsOutputs { get; } = [];
    public RelayCommand PlayPreviewCommand { get; private set; } = null!;
    public RelayCommand OpenSpeechSettingsCommand { get; private set; } = null!;

    private void InitTts(AppSettings settings, ITextToSpeech? tts, Action<string>? openUri)
    {
        _tts = tts ?? new NullTextToSpeech();
        _openUri = openUri;
        _allVoices = _tts.Voices;
        _allOutputs = _tts.OutputDevices.ToList();
        _ttsEnabled = settings.Tts.Enabled;
        _ttsLanguageTag = NullIfEmpty(settings.Tts.Language ?? "");
        _ttsVoiceId = NullIfEmpty(settings.Tts.Voice ?? "");
        _ttsDevice = settings.Tts.Device ?? "";
        _ttsRate = Math.Clamp(settings.Tts.Rate, 0.5, 2.0);
        _ttsVolume = Math.Clamp(settings.Tts.Volume, 0.0, 1.0);
        if (_ttsDevice.Length > 0 && !_allOutputs.Contains(_ttsDevice, StringComparer.OrdinalIgnoreCase))
            _allOutputs.Add(_ttsDevice); // unplugged: keep it listed so Save doesn't lose it
        PlayPreviewCommand = new RelayCommand(PlayPreview, () => CanPlayPreview);
        OpenSpeechSettingsCommand = new RelayCommand(() => _openUri?.Invoke(SpeechSettingsUri), () => _openUri != null);
        RebuildLanguages();
        RebuildVoices();
        FilterOutputs();
    }

    // ---- enabled, speed, volume

    public bool TtsEnabled
    {
        get => _ttsEnabled;
        set => SetProperty(ref _ttsEnabled, value);
    }

    /// <summary>Speaking speed, 0.5–2.0.</summary>
    public double TtsRate
    {
        get => _ttsRate;
        set => SetProperty(ref _ttsRate, Math.Clamp(value, 0.5, 2.0));
    }

    /// <summary>Volume, 0–1.</summary>
    public double TtsVolume
    {
        get => _ttsVolume;
        set => SetProperty(ref _ttsVolume, Math.Clamp(value, 0.0, 1.0));
    }

    // ---- language and voice

    /// <summary>The language voices are chosen for: the picked one, else the recognition language.</summary>
    public string EffectiveTtsLanguage => _ttsLanguageTag ?? Language;

    public TtsLanguageChoice? SelectedTtsLanguage
    {
        get => _selectedTtsLanguage;
        set
        {
            if (value is null) return;
            if (!SetProperty(ref _selectedTtsLanguage, value)) return;
            _ttsLanguageTag = value.Tag;
            RebuildVoices();
        }
    }

    public VoiceInfo? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (value is null) return;
            _ttsVoiceId = value.Id; // an explicit pick is saved, even when it is already the shown default
            if (SetProperty(ref _selectedVoice, value)) OnPreviewStateChanged();
        }
    }

    private void OnRecognitionLanguageChanged()
    {
        RebuildLanguages();
        if (_ttsLanguageTag is null) RebuildVoices();
    }

    private void RebuildLanguages()
    {
        var same = new TtsLanguageChoice(null, $"Same as recognition ({Language})");
        TtsLanguages.Clear();
        TtsLanguages.Add(same);
        var tags = _allVoices.Select(v => v.Language).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (_ttsLanguageTag != null && !tags.Contains(_ttsLanguageTag, StringComparer.OrdinalIgnoreCase)) tags.Add(_ttsLanguageTag);
        foreach (var tag in tags) TtsLanguages.Add(new TtsLanguageChoice(tag, tag));
        _selectedTtsLanguage = TtsLanguages.FirstOrDefault(c => c.Tag != null && c.Tag.Equals(_ttsLanguageTag, StringComparison.OrdinalIgnoreCase)) ?? same;
        OnPropertyChanged(nameof(SelectedTtsLanguage));
    }

    private void RebuildVoices()
    {
        var language = EffectiveTtsLanguage;
        TtsVoices.Clear();
        foreach (var voice in _allVoices.Where(v => VoiceChooser.MatchesLanguage(v.Language, language))) TtsVoices.Add(voice);
        _selectedVoice = VoiceChooser.Pick(TtsVoices, language, _ttsVoiceId);
        OnPropertyChanged(nameof(SelectedVoice));
        OnPropertyChanged(nameof(EffectiveTtsLanguage));
        OnPropertyChanged(nameof(TtsNote));
        OnPropertyChanged(nameof(HasTtsNote));
        OnPropertyChanged(nameof(HasTtsVoices));
        OnPreviewStateChanged();
    }

    public bool HasTtsVoices => TtsVoices.Count > 0;

    /// <summary>The warning shown instead of the voice list, or "" when a voice is available.</summary>
    public string TtsNote
    {
        get
        {
            if (_allVoices.Count == 0) return "No Windows voices found. Voice replies are off.";
            if (TtsVoices.Count == 0) return $"No voice for {LanguageName(EffectiveTtsLanguage)} ({EffectiveTtsLanguage}) is installed.";
            return "";
        }
    }

    public bool HasTtsNote => TtsNote.Length > 0;

    private static string LanguageName(string tag)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(tag.Split('-')[0]).EnglishName;
            return string.IsNullOrWhiteSpace(name) ? tag : name;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    // ---- output device

    public string TtsOutputSearch
    {
        get => _ttsOutputSearch;
        set
        {
            if (SetProperty(ref _ttsOutputSearch, value)) FilterOutputs();
        }
    }

    public string? SelectedTtsOutput
    {
        get => _ttsDevice.Length == 0 ? DefaultOutput : TtsOutputs.FirstOrDefault(o => o.Equals(_ttsDevice, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return; // the filter dropped the selection; keep the setting
            _ttsDevice = value == DefaultOutput ? "" : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TtsOutputText));
        }
    }

    public string TtsOutputText => _ttsDevice.Length == 0 ? DefaultOutput : _ttsDevice;

    private void FilterOutputs()
    {
        var words = TtsOutputSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        TtsOutputs.Clear();
        if (words.Length == 0) TtsOutputs.Add(DefaultOutput);
        foreach (var name in _allOutputs.Where(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))) TtsOutputs.Add(name);
        OnPropertyChanged(nameof(SelectedTtsOutput));
    }

    // ---- preview

    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (SetProperty(ref _previewText, value)) OnPreviewStateChanged();
        }
    }

    public bool CanPlayPreview => _selectedVoice != null && !string.IsNullOrWhiteSpace(PreviewText);

    private void OnPreviewStateChanged() => PlayPreviewCommand?.Refresh();

    /// <summary>Speaks the preview with the values shown in the dialog, before they are saved.</summary>
    private void PlayPreview()
    {
        if (_selectedVoice is null) return;
        _tts.Stop();
        var options = new TtsOptions(_selectedVoice.Id, TtsRate, TtsVolume, _ttsDevice.Length == 0 ? null : _ttsDevice);
        _ = _tts.SpeakAsync(PreviewText.Trim(), options).ContinueWith(_ => { }, TaskScheduler.Default); // errors are logged by the voice
    }

    private TtsSettings BuildTts() => _original.Tts with
    {
        Enabled = TtsEnabled,
        Language = _ttsLanguageTag,
        // The remembered pick is kept across language switches, but only saved when it is the voice shown.
        Voice = _selectedVoice?.Id == _ttsVoiceId ? _ttsVoiceId : null,
        Rate = TtsRate,
        Volume = TtsVolume,
        Device = NullIfEmpty(_ttsDevice),
    };
}
