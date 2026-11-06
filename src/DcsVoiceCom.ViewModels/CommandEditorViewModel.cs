using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Input;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels;

/// <summary>One step in the editor's list.</summary>
public sealed class StepItemViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>The argument a step had before it became a press (e.g. TOGGLE), put back when the press is unticked.</summary>
    private string? _argBeforePress;

    public StepItemViewModel(Step step, int index, Action changed)
    {
        Step = step;
        Index = index;
        _changed = changed;
    }

    public Step Step { get; }
    public int Index { get; }
    public int Number => Index + 1;
    public string Text => StepText.Describe(Step);
    public string Type => Step.Type;

    public bool IsBios => Step is BiosStep;

    /// <summary>Skipping only makes sense for a bios step that sets a position, not for a press.</summary>
    public bool CanSkipIfAlready => Step is BiosStep { IsPress: false };

    /// <summary>For a bios step: do nothing when the switch is already in that position.</summary>
    public bool SkipIfAlready
    {
        get => Step is BiosStep { SkipIfAlready: true };
        set
        {
            if (Step is not BiosStep bios || bios.SkipIfAlready == value) return;
            bios.SkipIfAlready = value;
            Notify(nameof(SkipIfAlready));
        }
    }

    /// <summary>For a bios step: press the button (push in, hold, release) instead of leaving it in one state.</summary>
    public bool IsPress
    {
        get => Step is BiosStep { IsPress: true };
        set
        {
            if (Step is not BiosStep bios || bios.IsPress == value) return;
            if (value)
            {
                bios.PressMs = BiosStep.DefaultPressMs;
                bios.Release = BiosStep.DefaultRelease;
                bios.SkipIfAlready = false;
                // A press needs a number that differs from the release (0); keep the old argument to put back.
                if (!int.TryParse(bios.Arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                    || SameValue(bios.Arg, bios.ReleaseArg))
                {
                    _argBeforePress = bios.Arg;
                    bios.Arg = "1";
                }
            }
            else
            {
                bios.PressMs = null;
                bios.Release = null;
                if (_argBeforePress != null) bios.Arg = _argBeforePress;
                _argBeforePress = null;
            }
            Notify(nameof(IsPress), nameof(CanSkipIfAlready), nameof(SkipIfAlready), nameof(PressValue), nameof(ReleaseValue), nameof(HoldMs));
        }
    }

    /// <summary>A press's push-in value (the step's argument), default 1.</summary>
    public string PressValue
    {
        get => (Step as BiosStep)?.Arg ?? "";
        set
        {
            var text = value.Trim();
            if (Step is not BiosStep bios || text.Length == 0 || bios.Arg == text) return;
            // In and out must differ, or the press does nothing; the box goes back to the old value.
            if (bios.IsPress && SameValue(text, bios.ReleaseArg)) { OnPropertyChanged(nameof(PressValue)); return; }
            bios.Arg = text;
            _argBeforePress = null;
            Notify(nameof(PressValue));
        }
    }

    /// <summary>A press's release value, default 0.</summary>
    public string ReleaseValue
    {
        get => (Step as BiosStep)?.ReleaseArg ?? "";
        set
        {
            var text = value.Trim();
            if (Step is not BiosStep bios || text.Length == 0 || bios.ReleaseArg == text) return;
            if (SameValue(text, bios.Arg)) { OnPropertyChanged(nameof(ReleaseValue)); return; }
            bios.Release = text;
            Notify(nameof(ReleaseValue));
        }
    }

    /// <summary>How long a press holds the button in, in ms.</summary>
    public string HoldMs
    {
        get => (Step as BiosStep)?.PressMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            if (Step is not BiosStep { IsPress: true } bios
                || !int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                || ms == bios.PressMs) return;
            bios.PressMs = ms;
            Notify(nameof(HoldMs));
        }
    }

    private static bool SameValue(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Notify(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Text));
        _changed();
    }
}

/// <summary>Edits one command in place: name, phrases, flags and steps. Every change marks the profile dirty.</summary>
public sealed class CommandEditorViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly Action _changed;
    private readonly Func<EnterValueContext> _enterValue;
    private string _voiceLine;
    private string _alternativesText;
    private string _stepsJson = "";
    private bool _showJson;
    private string? _jsonError;

    public CommandEditorViewModel(Command command, IDialogs dialogs, Action changed, Func<EnterValueContext>? enterValue = null)
    {
        Command = command;
        _dialogs = dialogs;
        _changed = changed;
        _enterValue = enterValue ?? (() => EnterValueContext.Offline);
        _voiceLine = command.Phrases.Count > 0 ? command.Phrases[0] : "";
        _alternativesText = string.Join(Environment.NewLine, command.Phrases.Skip(1));

        MoveUpCommand = new RelayCommand(p => Move(p as StepItemViewModel, -1), p => p is StepItemViewModel s && s.Index > 0);
        MoveDownCommand = new RelayCommand(p => Move(p as StepItemViewModel, +1), p => p is StepItemViewModel s && s.Index < Steps.Count - 1);
        RemoveStepCommand = new RelayCommand(p => Remove(p as StepItemViewModel), p => p is StepItemViewModel);
        EditStepCommand = new RelayCommand(p => Edit(p as StepItemViewModel), p => p is StepItemViewModel);
        AddKeyCommand = new RelayCommand(AddKey);
        AddWaitCommand = new RelayCommand(AddWait);
        AddEnterValueCommand = new RelayCommand(AddEnterValue);
        ToggleJsonCommand = new RelayCommand(ToggleJson);
        ApplyJsonCommand = new RelayCommand(() => ApplyJson());
        RebuildSteps();
        Validate();
    }

    public Command Command { get; }

    public ObservableCollection<StepItemViewModel> Steps { get; } = [];
    public ObservableCollection<string> PhrasePreview { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand RemoveStepCommand { get; }
    public RelayCommand EditStepCommand { get; }
    public RelayCommand AddKeyCommand { get; }
    public RelayCommand AddWaitCommand { get; }
    public RelayCommand AddEnterValueCommand { get; }
    public RelayCommand ToggleJsonCommand { get; }
    public RelayCommand ApplyJsonCommand { get; }

    public string Name
    {
        get => Command.Name;
        set
        {
            if (Command.Name == value) return;
            Command.Name = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public string Description
    {
        get => Command.Description ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (Command.Description == v) return;
            Command.Description = v;
            OnPropertyChanged();
            Changed();
        }
    }

    /// <summary>
    /// What to say, e.g. "[gear;landing gear] up" or "tacan {channel:1-126} {band:X|Y}". It is also the command's name.
    /// </summary>
    public string VoiceLine
    {
        get => _voiceLine;
        set
        {
            if (!SetProperty(ref _voiceLine, value)) return;
            var name = value.Trim();
            if (name.Length > 0 && Command.Name != name)
            {
                Command.Name = name;
                OnPropertyChanged(nameof(Name));
            }
            UpdatePhrases();
        }
    }

    /// <summary>Other ways to say it, one phrase spec per line.</summary>
    public string AlternativesText
    {
        get => _alternativesText;
        set
        {
            if (!SetProperty(ref _alternativesText, value)) return;
            UpdatePhrases();
        }
    }

    private void UpdatePhrases()
    {
        Command.Phrases = new[] { _voiceLine }.Concat(_alternativesText.Split('\n'))
            .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        Changed();
    }

    public bool Critical
    {
        get => Command.Critical;
        set
        {
            if (Command.Critical == value) return;
            Command.Critical = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public bool Enabled
    {
        get => Command.Enabled;
        set
        {
            if (Command.Enabled == value) return;
            Command.Enabled = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public bool HasProblems => Problems.Count > 0;
    public bool HasNoSteps => Steps.Count == 0;

    public bool ShowJson
    {
        get => _showJson;
        set => SetProperty(ref _showJson, value);
    }

    /// <summary>All steps as JSON, for the step types the list can't edit (if/else, wait_for, bios_until...).</summary>
    public string StepsJson
    {
        get => _stepsJson;
        set => SetProperty(ref _stepsJson, value);
    }

    public string? JsonError
    {
        get => _jsonError;
        private set => SetProperty(ref _jsonError, value);
    }

    public void AddBios(string control, string arg)
    {
        Command.Steps.Add(new BiosStep { Control = control, Arg = arg });
        StepsChanged();
    }

    /// <summary>Adds one button press: <paramref name="arg"/> in, held <see cref="BiosStep.DefaultPressMs"/>, then 0 out.</summary>
    public void AddPress(string control, string arg = "1")
    {
        Command.Steps.Add(ControlArguments.Press(control, arg));
        StepsChanged();
    }

    /// <summary>Replaces the steps from <see cref="StepsJson"/>; false (and <see cref="JsonError"/>) when it doesn't parse.</summary>
    public bool ApplyJson()
    {
        try
        {
            var steps = JsonSerializer.Deserialize<List<Step>>(StepsJson, ProfileSerializer.Options) ?? [];
            Command.Steps = steps;
            JsonError = null;
            StepsChanged(refreshJson: false);
            return true;
        }
        catch (JsonException ex)
        {
            JsonError = ex.Message;
            return false;
        }
    }

    private void ToggleJson()
    {
        if (!ShowJson) StepsJson = JsonSerializer.Serialize(Command.Steps, ProfileSerializer.Options);
        ShowJson = !ShowJson;
    }

    private void AddKey()
    {
        var keys = PromptKeys("");
        if (keys is null) return;
        Command.Steps.Add(new KeyStep { Keys = keys });
        StepsChanged();
    }

    /// <summary>What "+ Wait" offers before anything was entered.</summary>
    public const int DefaultWaitMs = 100;

    /// <summary>
    /// The last wait time entered, offered by the next "+ Wait". Shared by every editor while the app runs;
    /// starts again at <see cref="DefaultWaitMs"/> on the next launch.
    /// </summary>
    public static int LastWaitMs { get; set; } = DefaultWaitMs;

    private void AddWait()
    {
        var ms = PromptMs(LastWaitMs);
        if (ms is null) return;
        LastWaitMs = ms.Value;
        Command.Steps.Add(new WaitStep { Ms = ms.Value });
        StepsChanged();
    }

    /// <summary>Adds one "enter value" step for the voice line's first slot; Edit opens its window.</summary>
    private void AddEnterValue()
    {
        var slot = FirstSlot();
        Command.Steps.Add(new EnterValueStep
        {
            Value = slot is null ? "" : $"{{{slot.Name}}}",
            Method = slot?.Kind == SlotKind.Text ? EnterMethod.Keypad : EnterMethod.Wheels,
        });
        StepsChanged();
    }

    private SlotSpec? FirstSlot()
    {
        foreach (var spec in Command.Phrases)
        {
            try
            {
                foreach (var phrase in PhraseExpander.Expand(spec))
                {
                    var slots = PhraseTemplate.Parse(phrase).Slots.ToList();
                    if (slots.FirstOrDefault(s => s.Kind is SlotKind.Number or SlotKind.Integer or SlotKind.Text) is { } best) return best;
                    if (slots.Count > 0) return slots[0];
                }
            }
            catch (FormatException)
            {
                // reported in Problems
            }
        }
        return null;
    }

    private void Edit(StepItemViewModel? item)
    {
        if (item is null) return;
        switch (item.Step)
        {
            case BiosStep bios:
                var arg = _dialogs.Prompt("Edit DCS-BIOS step", $"Argument for {bios.Control} (a value, INC, DEC, TOGGLE, +3200 or a {{placeholder}})", bios.Arg);
                if (string.IsNullOrWhiteSpace(arg)) return;
                bios.Arg = arg.Trim();
                break;
            case KeyStep key:
                var keys = PromptKeys(key.Keys);
                if (keys is null) return;
                key.Keys = keys;
                break;
            case WaitStep wait:
                var ms = PromptMs(wait.Ms);
                if (ms is null) return;
                wait.Ms = ms.Value;
                LastWaitMs = ms.Value;
                break;
            case EnterValueStep enter:
                var editor = new EnterValueEditorViewModel(EnterValueEditorViewModel.Copy(enter), Command.Phrases, _enterValue(), _dialogs);
                if (!_dialogs.ShowEnterValue(editor)) return;
                Command.Steps[item.Index] = editor.ToStep();
                break;
            default:
                if (!ShowJson) ToggleJson();
                return;
        }
        StepsChanged();
    }

    private string? PromptKeys(string initial)
    {
        while (true)
        {
            var text = _dialogs.Prompt("Key step", "Keys, e.g. RCtrl+RShift+G, LAlt+F1, Space (left/right modifiers are distinct)", initial);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return KeyChord.Parse(text.Trim()).ToString();
            }
            catch (FormatException ex)
            {
                if (!_dialogs.Confirm("Unknown key", ex.Message + "\n\nTry again?")) return null;
                initial = text;
            }
        }
    }

    private int? PromptMs(int initial)
    {
        var text = _dialogs.Prompt("Wait step", "Milliseconds", initial.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0 ? ms : null;
    }

    private void Move(StepItemViewModel? item, int delta)
    {
        if (item is null) return;
        var to = item.Index + delta;
        if (to < 0 || to >= Command.Steps.Count) return;
        (Command.Steps[item.Index], Command.Steps[to]) = (Command.Steps[to], Command.Steps[item.Index]);
        StepsChanged();
    }

    private void Remove(StepItemViewModel? item)
    {
        if (item is null) return;
        Command.Steps.RemoveAt(item.Index);
        StepsChanged();
    }

    private void StepsChanged(bool refreshJson = true)
    {
        RebuildSteps();
        if (refreshJson && ShowJson) StepsJson = JsonSerializer.Serialize(Command.Steps, ProfileSerializer.Options);
        Changed();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        for (var i = 0; i < Command.Steps.Count; i++) Steps.Add(new StepItemViewModel(Command.Steps[i], i, Changed));
        OnPropertyChanged(nameof(HasNoSteps));
        MoveUpCommand?.Refresh();
        MoveDownCommand?.Refresh();
    }

    private void Changed()
    {
        Validate();
        _changed();
    }

    /// <summary>Phrase syntax and key problems for this command alone, plus what the phrases expand to.</summary>
    private void Validate()
    {
        Problems.Clear();
        PhrasePreview.Clear();
        if (Command.Phrases.Count == 0) Problems.Add("Enter the voice line.");
        var probe = new Command { Name = Command.Name, Phrases = Command.Phrases, Steps = Command.Steps, Enabled = true };
        foreach (var problem in CompiledProfile.Compile(new Profile { Commands = [probe] }).Problems) Problems.Add(problem);
        foreach (var spec in Command.Phrases)
        {
            try
            {
                foreach (var phrase in PhraseExpander.Expand(spec))
                {
                    if (PhrasePreview.Count >= 8) break;
                    PhrasePreview.Add(phrase);
                }
            }
            catch (FormatException)
            {
                // reported in Problems
            }
        }
        OnPropertyChanged(nameof(HasProblems));
    }
}
