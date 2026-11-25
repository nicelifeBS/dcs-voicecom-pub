using DcsVoiceCom.Engine;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.ViewModels;

/// <summary>What the Say window needs to play a preview: the voice, and the options as saved in Settings (null: no voice).</summary>
public sealed record SayContext(ITextToSpeech Tts, Func<TtsOptions?> Options)
{
    /// <summary>Opens the TTS values window to pick a value; null when cancelled. Null: no profile, no button.</summary>
    public Func<TtsValuePick?>? PickValue { get; init; }

    /// <summary>A TTS value's live text for the preview; null when it isn't a value or can't be read now.</summary>
    public Func<string, string?>? LiveValue { get; init; }

    /// <summary>No voice: Play is off and the note says so.</summary>
    public static SayContext Offline { get; } = new(new NullTextToSpeech(), () => null);
}

/// <summary>Edits the text of one Say step, with a Play button for a preview.</summary>
public sealed class SayEditorViewModel : ObservableObject
{
    public const string Hint = "Use {slot} for spoken values, e.g. Radio set to {freq}. Insert value… adds a live cockpit value, e.g. {clock}.";

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

    /// <summary>
    /// What Play says: a TTS value's live text, else the slot name ("Radio set to {freq}" → "Radio set to freq").
    /// </summary>
    public string PreviewSpoken => Placeholders.Preview(Text, _context.LiveValue ?? (_ => null)).Trim();

    /// <summary>Shows the Insert value… button: there is a profile to keep values in.</summary>
    public bool CanPickValue => _context.PickValue != null;

    /// <summary>
    /// Opens the TTS values window. Renames made there are applied to this text; returns "{name}" to insert at the
    /// cursor, or null when cancelled.
    /// </summary>
    public string? PickValue()
    {
        if (_context.PickValue?.Invoke() is not { } pick) return null;
        var text = Text;
        foreach (var (old, @new) in pick.Renames) text = Editing.TtsValueUsage.Rename(text, old, @new);
        Text = text;
        OnPropertyChanged(nameof(PreviewSpoken));
        return "{" + pick.Name + "}";
    }

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
