using DcsVoiceCom.Engine;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels;

/// <summary>What the Say window needs to play a preview: the voice, and the options as saved in Settings (null: no voice).</summary>
public sealed record SayContext(ITextToSpeech Tts, Func<TtsOptions?> Options)
{
    /// <summary>No voice: Play is off and the note says so.</summary>
    public static SayContext Offline { get; } = new(new NullTextToSpeech(), () => null);
}

/// <summary>Edits the text of one Say step, with a Play button for a preview.</summary>
public sealed class SayEditorViewModel : ObservableObject
{
    public const string Hint = "Use {slot} for spoken values, e.g. Radio set to {freq}";

    private readonly SayContext _context;
    private string _text;

    public SayEditorViewModel(string text, SayContext? context = null)
    {
        _text = text;
        _context = context ?? SayContext.Offline;
        PlayCommand = new RelayCommand(Play, () => CanPlay);
    }

    public RelayCommand PlayCommand { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(PreviewSpoken));
            PlayCommand.Refresh();
        }
    }

    /// <summary>OK needs some text; it does not need a voice, so a step can be saved without one.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Text);

    /// <summary>What Play says: each placeholder becomes its slot name ("Radio set to {freq}" → "Radio set to freq").</summary>
    public string PreviewSpoken => Placeholders.ToSlotNames(Text).Trim();

    public bool CanPlay => CanSave && _context.Options() != null;

    /// <summary>Why Play is off, or "" when it works.</summary>
    public string Note
    {
        get
        {
            if (_context.Tts.Voices.Count == 0) return $"No Windows voices found. {SettingsViewModel.AddVoiceHelp}";
            return _context.Options() is null ? "Voice replies are off, or no voice fits the language. See Settings › VOICE REPLIES." : "";
        }
    }

    public bool HasNote => Note.Length > 0;

    private void Play()
    {
        if (_context.Options() is not { } options) return;
        _context.Tts.Stop();
        _ = _context.Tts.SpeakAsync(PreviewSpoken, options).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Called when the window closes, so a long preview doesn't keep playing.</summary>
    public void StopPreview() => _context.Tts.Stop();
}
