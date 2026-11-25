using System.Collections.ObjectModel;
using System.Globalization;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels;

/// <summary>What the Values editor returns to the Say editor: the picked name, and the renames to apply to its text.</summary>
public sealed record TtsValuePick(string Name, IReadOnlyList<(string Old, string New)> Renames);

/// <summary>One value in the list. Holds a copy; the original name tracks renames.</summary>
public sealed class TtsValueItemViewModel : ObservableObject
{
    public TtsValueItemViewModel(TtsValue value, string? originalName)
    {
        Value = value;
        OriginalName = originalName;
    }

    public TtsValue Value { get; }

    /// <summary>The name in the profile before this edit; null for a new value.</summary>
    public string? OriginalName { get; }

    /// <summary>Typed by the user while DCS isn't running.</summary>
    public string Sample { get; set; } = "";

    public string Label => "{" + Value.Name + "}";
    public string Detail => (Value.Output.Length == 0 ? "no output" : Value.Output) + " · " + (Value.Mode == TtsValueMode.Remap ? "remap" : "slice");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>One character of the sample, clickable to build the slice template.</summary>
public sealed record SampleCellViewModel(int Index, string Text, bool IsSelected);

/// <summary>
/// The TTS values window: the profile's live values, each read from one DCS-BIOS output and made speakable
/// by slicing or remapping. Works on copies; <see cref="ToValues"/> and <see cref="Renames"/> give the result on OK.
/// </summary>
public sealed class TtsValuesEditorViewModel : ObservableObject
{
    private readonly Profile _profile;
    private readonly EnterValueContext _live;
    private readonly SayContext _say;
    private readonly IDialogs _dialogs;
    private TtsValueItemViewModel? _selected;
    private int? _anchor;
    private int? _end;

    public TtsValuesEditorViewModel(Profile profile, EnterValueContext live, SayContext say, IDialogs dialogs, bool pickMode = false)
    {
        _profile = profile;
        _live = live;
        _say = say;
        _dialogs = dialogs;
        PickMode = pickMode;
        foreach (var value in profile.Values ?? []) Items.Add(new TtsValueItemViewModel(value.Clone(), value.Name));
        Outputs = live.Catalog.Controls
            .Where(c => c.PrimaryIntegerOutput != null || c.PrimaryStringOutput != null)
            .Select(c => c.Identifier).Order(StringComparer.OrdinalIgnoreCase).ToList();

        NewCommand = new RelayCommand(New);
        RenameCommand = new RelayCommand(Rename, () => Selected != null);
        DeleteCommand = new RelayCommand(Delete, () => Selected != null);
        AddPieceCommand = new RelayCommand(AddPiece, () => _anchor != null);
        ClearTemplateCommand = new RelayCommand(() => Template = "", () => Selected != null);
        PlayCommand = new RelayCommand(Play, () => CanPlay);
        Selected = Items.FirstOrDefault();
    }

    public bool PickMode { get; }
    public string Title => PickMode ? "TTS values — pick one for the Say step" : "TTS values";
    public string PrimaryLabel => PickMode ? (Selected is null ? "Insert" : $"Insert {Selected.Label}") : "OK";

    public ObservableCollection<TtsValueItemViewModel> Items { get; } = [];
    public ObservableCollection<SampleCellViewModel> Cells { get; } = [];
    public IReadOnlyList<string> Outputs { get; }

    public RelayCommand NewCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddPieceCommand { get; }
    public RelayCommand ClearTemplateCommand { get; }
    public RelayCommand PlayCommand { get; }

    public TtsValueItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            _anchor = _end = null;
            OnPropertyChanged(string.Empty);
            RefreshAll();
        }
    }

    public bool HasSelected => Selected != null;
    private TtsValue? V => Selected?.Value;

    public string Name
    {
        get => V?.Name ?? "";
        set
        {
            if (V is null || V.Name == value.Trim()) return;
            V.Name = value.Trim();
            Changed();
        }
    }

    /// <summary>The DCS-BIOS control to read. Picking one for a remap fills the cockpit range from its max value.</summary>
    public string Output
    {
        get => V?.Output ?? "";
        set
        {
            if (V is null || V.Output == value.Trim()) return;
            V.Output = value.Trim();
            if (V.Mode == TtsValueMode.Remap && MaxValue() is { } max) V.InMax = max;
            _anchor = _end = null;
            Changed();
            OnPropertyChanged(nameof(InMax));
        }
    }

    public bool IsSlice
    {
        get => V?.Mode == TtsValueMode.Slice;
        set { if (value) SetMode(TtsValueMode.Slice); }
    }

    public bool IsRemap
    {
        get => V?.Mode == TtsValueMode.Remap;
        set { if (value) SetMode(TtsValueMode.Remap); }
    }

    /// <summary>Remap needs a number: off for an output that only has text.</summary>
    public bool CanRemap => !IsTextOutput;

    private void SetMode(TtsValueMode mode)
    {
        if (V is null || V.Mode == mode) return;
        V.Mode = mode;
        if (mode == TtsValueMode.Remap)
        {
            V.InMin ??= 0;
            V.InMax ??= MaxValue() ?? TtsValue.DefaultInMax;
            V.OutMin ??= 0;
            V.OutMax ??= TtsValue.DefaultOutMax;
        }
        else
        {
            V.Template ??= "";
        }
        OnPropertyChanged(string.Empty);
        Changed();
    }

    public string Pad
    {
        get => V is null || V.Pad == 0 ? "" : V.Pad.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (V is null) return;
            V.Pad = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 10) : 0;
            _anchor = _end = null;
            Changed();
        }
    }

    public string Template
    {
        get => V?.Template ?? "";
        set
        {
            if (V is null || V.Template == value) return;
            V.Template = value;
            Changed();
        }
    }

    public string InMin { get => Num(V?.InMin); set => SetNum(value, (v, n) => v.InMin = n); }
    public string InMax { get => Num(V?.InMax); set => SetNum(value, (v, n) => v.InMax = n); }
    public string OutMin { get => Num(V?.OutMin); set => SetNum(value, (v, n) => v.OutMin = n); }
    public string OutMax { get => Num(V?.OutMax); set => SetNum(value, (v, n) => v.OutMax = n); }

    public int Decimals
    {
        get => V?.Decimals ?? 0;
        set
        {
            if (V is null || V.Decimals == value) return;
            V.Decimals = Math.Clamp(value, 0, 2);
            Changed();
        }
    }

    public IReadOnlyList<int> DecimalChoices { get; } = [0, 1, 2];

    private static string Num(double? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";

    private void SetNum(string text, Action<TtsValue, double?> set)
    {
        if (V is null) return;
        set(V, double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null);
        Changed();
    }

    // ---- live sample ----

    /// <summary>True while DCS sends data for this aircraft and the output is known: the sample is the live value.</summary>
    public bool IsLive => ReadLive() != null;

    public bool IsOffline => !IsLive;

    /// <summary>The raw value: live, or typed by the user without DCS.</summary>
    public string Sample
    {
        get => ReadLive()?.Raw ?? Selected?.Sample ?? "";
        set
        {
            if (Selected is null || IsLive || Selected.Sample == value) return;
            Selected.Sample = value;
            _anchor = _end = null;
            Changed();
        }
    }

    /// <summary>The output only has text (a display), so it is never padded or remapped.</summary>
    public bool IsTextOutput
    {
        get
        {
            if (V is null) return false;
            if (_live.Catalog.Find(V.Output) is { } control) return control.PrimaryIntegerOutput is null && control.PrimaryStringOutput != null;
            return Sample.Length > 0 && !long.TryParse(Sample.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }
    }

    public bool ShowPad => IsSlice && !IsTextOutput;

    private (string Raw, bool IsInteger)? ReadLive()
    {
        if (V is null || _live.LiveBuffer() is not { } buffer || _live.Catalog.Find(V.Output) is not { } control) return null;
        if (control.PrimaryIntegerOutput is { } i)
            return (buffer.ReadInteger(i.Address, i.Mask ?? 0xFFFF, i.ShiftBy ?? 0).ToString(CultureInfo.InvariantCulture), true);
        if (control.PrimaryStringOutput is { } s) return (buffer.ReadString(s.Address, s.MaxLength ?? 0), false);
        return null;
    }

    private double? MaxValue() =>
        V is not null && _live.Catalog.Find(V.Output)?.PrimaryIntegerOutput?.MaxValue is { } max ? max : null;

    public string MaxValueText => MaxValue() is { } max ? $"DCS-BIOS says this output goes up to {max}." : "";

    /// <summary>Called by the window's timer while it is open, so live values move.</summary>
    public void RefreshLive()
    {
        if (!IsLive) return;
        RefreshCells();
        RefreshPreview();
    }

    // ---- slice picking ----

    /// <summary>Click on a character: starts a selection, or with <paramref name="extend"/> widens it.</summary>
    public void SelectCell(int index, bool extend)
    {
        if (extend && _anchor != null) _end = index;
        else (_anchor, _end) = (index, null);
        RefreshCells();
        AddPieceCommand.Refresh();
        OnPropertyChanged(nameof(PieceText));
    }

    /// <summary>The selection as a template piece: {3} or {0-1}; "" without one.</summary>
    public string PieceText
    {
        get
        {
            if (_anchor is not { } a) return "";
            var (from, to) = (Math.Min(a, _end ?? a), Math.Max(a, _end ?? a));
            return from == to ? $"{{{from}}}" : $"{{{from}-{to}}}";
        }
    }

    public string AddPieceLabel => "Add " + PieceText;

    private void AddPiece()
    {
        if (PieceText.Length == 0) return;
        Template += PieceText;
        _anchor = _end = null;
        RefreshCells();
        AddPieceCommand.Refresh();
        OnPropertyChanged(nameof(PieceText));
        OnPropertyChanged(nameof(AddPieceLabel));
    }

    private void RefreshCells()
    {
        Cells.Clear();
        if (V is null || !IsSlice) return;
        var text = TtsValueFormat.Padded(V, Sample, !IsTextOutput);
        int from = -1, to = -1;
        if (_anchor is { } a) (from, to) = (Math.Min(a, _end ?? a), Math.Max(a, _end ?? a));
        for (var i = 0; i < text.Length; i++)
            Cells.Add(new SampleCellViewModel(i, text[i] == ' ' ? "·" : text[i].ToString(), i >= from && i <= to));
        OnPropertyChanged(nameof(AddPieceLabel));
    }

    // ---- preview, problems, usage ----

    /// <summary>What the value says now, or "" with <see cref="Error"/> set.</summary>
    public string Result { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;

    private void RefreshPreview()
    {
        Result = Error = "";
        if (V is not null)
        {
            var problems = TtsValueFormat.Validate(V);
            if (problems.Count > 0) Error = Capitalize(problems[0]) + ".";
            else if (Sample.Length == 0) Error = IsLive ? "" : "Type a sample value to see the result.";
            else
            {
                try { Result = TtsValueFormat.Format(V, Sample, !IsTextOutput); }
                catch (StepException ex) { Error = Capitalize(ex.Message) + "."; }
            }
        }
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        PlayCommand.Refresh();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"Used in 2 commands", counted under the original name (the rename is applied on OK).</summary>
    public string UsageText
    {
        get
        {
            if (Selected is null) return "";
            var n = Selected.OriginalName is { } name ? TtsValueUsage.CountCommands(_profile, name) : 0;
            return n switch { 0 => "Not used yet", 1 => "Used in 1 command", _ => $"Used in {n} commands" };
        }
    }

    /// <summary>Warns when a command has a spoken slot of the same name: there the spoken value wins.</summary>
    public string ClashText
    {
        get
        {
            if (V is null || V.Name.Length == 0) return "";
            var n = TtsValueUsage.CountSlotClashes(_profile, V.Name);
            if (n == 0) return "";
            return $"{{{V.Name}}} is also a spoken slot in {n} command{(n == 1 ? "" : "s")}. The spoken value wins there.";
        }
    }

    public bool HasClash => ClashText.Length > 0;

    /// <summary>Why OK is off: a bad value or a name used twice. "" when everything can be saved.</summary>
    public string SaveProblem
    {
        get
        {
            var dupe = Items.GroupBy(i => i.Value.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dupe != null) return $"The name {{{dupe.Key}}} is used twice.";
            foreach (var item in Items)
            {
                if (TtsValueFormat.Validate(item.Value) is [var first, ..]) return $"{item.Label}: {first}.";
            }
            return "";
        }
    }

    public bool CanSave => SaveProblem.Length == 0 && (!PickMode || Selected != null);

    // ---- list ----

    private void New()
    {
        var name = _dialogs.Prompt("New TTS value", "Name, used as {name} in Say text (letters, digits, _)", UniqueName("value"));
        if (name is null) return;
        name = new string(name.Trim().Trim('{', '}').Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (name.Length == 0) return;
        if (char.IsAsciiDigit(name[0])) name = "_" + name;
        name = UniqueName(name);
        var item = new TtsValueItemViewModel(new TtsValue { Name = name, Template = "" }, null);
        Items.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveProblem));
    }

    private string UniqueName(string stem)
    {
        var name = stem;
        for (var n = 2; Items.Any(i => i.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = stem + n;
        return name;
    }

    private void Rename()
    {
        if (Selected is null) return;
        var name = _dialogs.Prompt("Rename TTS value", "New name. Say steps that use it are updated on OK.", Selected.Value.Name);
        if (name is null) return;
        Name = name.Trim('{', '}');
    }

    private void Delete()
    {
        if (Selected is null) return;
        var used = Selected.OriginalName is { } original ? TtsValueUsage.CountCommands(_profile, original) : 0;
        var message = used == 0
            ? $"Delete {Selected.Label}?"
            : $"{Selected.Label} is used in {used} command{(used == 1 ? "" : "s")}. Those Say steps will be skipped until you fix them. Delete it?";
        if (!_dialogs.Confirm("Delete TTS value", message)) return;
        var index = Items.IndexOf(Selected);
        Items.Remove(Selected);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveProblem));
    }

    // ---- play and result ----

    public bool CanPlay => Result.Length > 0 && _say.Options() != null;

    private void Play()
    {
        if (_say.Options() is not { } options) return;
        _say.Tts.Stop();
        _ = _say.Tts.SpeakAsync(Result, options).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void StopPreview() => _say.Tts.Stop();

    /// <summary>The values to store in the profile; null when there are none.</summary>
    public List<TtsValue>? ToValues() => Items.Count == 0 ? null : Items.Select(i => i.Value.Clone()).ToList();

    /// <summary>Values whose name changed, to update the Say steps.</summary>
    public IReadOnlyList<(string Old, string New)> Renames =>
        Items.Where(i => i.OriginalName != null && !i.OriginalName.Equals(i.Value.Name, StringComparison.Ordinal))
            .Select(i => (i.OriginalName!, i.Value.Name)).ToList();

    /// <summary>Stores the values in the profile and renames them in its Say steps.</summary>
    public void ApplyTo(Profile profile)
    {
        foreach (var (old, @new) in Renames) TtsValueUsage.Rename(profile, old, @new);
        profile.Values = ToValues();
    }

    private void Changed()
    {
        Selected?.Refresh();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Output));
        OnPropertyChanged(nameof(Template));
        OnPropertyChanged(nameof(Pad));
        OnPropertyChanged(nameof(Sample));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsTextOutput));
        OnPropertyChanged(nameof(ShowPad));
        OnPropertyChanged(nameof(CanRemap));
        OnPropertyChanged(nameof(MaxValueText));
        OnPropertyChanged(nameof(Decimals));
        OnPropertyChanged(nameof(ClashText));
        OnPropertyChanged(nameof(HasClash));
        OnPropertyChanged(nameof(SaveProblem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(PrimaryLabel));
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshCells();
        RefreshPreview();
        AddPieceCommand.Refresh();
        RenameCommand.Refresh();
        DeleteCommand.Refresh();
        ClearTemplateCommand.Refresh();
    }
}
