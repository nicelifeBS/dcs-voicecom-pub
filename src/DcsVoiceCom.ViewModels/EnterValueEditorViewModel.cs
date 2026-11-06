using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels;

/// <summary>What the "Enter value" window needs from the app: controls, live values and a way to send one test click.</summary>
public sealed record EnterValueContext(ControlCatalog Catalog, Func<StateBuffer?> LiveBuffer, Func<string, string, Task>? Send)
{
    public static EnterValueContext Offline { get; } = new(ControlCatalog.Empty, () => null, null);
}

/// <summary>One character of the read-back display: what it shows, which field it belongs to and what it will show for the try value.</summary>
public sealed class DisplayCellViewModel
{
    public int Index { get; init; }

    /// <summary>The character shown now: live text, or for a read-only field its saved character; blank without DCS.</summary>
    public string Text { get; init; } = "";

    /// <summary>What the cell will show for the try value; blank for read-only and unused cells.</summary>
    public string Target { get; init; } = "";

    /// <summary>The target differs from what the cell shows now.</summary>
    public bool Changes { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>The field's name on its first cell ("W1", "K", "R/O"); "" elsewhere.</summary>
    public string Label { get; init; } = "";

    /// <summary>The cell's field kind, or null when no field uses it.</summary>
    public FieldKind? Kind { get; init; }

    public bool IsFieldStart { get; init; }
    public bool IsFieldEnd { get; init; }

    /// <summary>The cell belongs to the selected field.</summary>
    public bool InSelectedField { get; init; }
}

/// <summary>One field of the display: a wheel, the keypad field, or read-only text.</summary>
public sealed class DisplayFieldViewModel : ObservableObject
{
    private readonly EnterValueEditorViewModel _owner;
    private FieldKind _kind;
    private string _control, _range;
    private WheelMode _mode;
    private TextAlign _align;
    private bool _flip;
    private string _status = "";

    internal DisplayFieldViewModel(EnterValueEditorViewModel owner, DisplayField field)
    {
        _owner = owner;
        At = field.At;
        Len = field.Len;
        _kind = field.Kind;
        _control = owner.Canonical(field.Control ?? "");
        _range = field.Range ?? "";
        _mode = field.Mode ?? WheelMode.Click;
        _align = field.Align ?? TextAlign.Left;
        _flip = field.Flip;
        ClickUpCommand = new AsyncCommand(() => owner.TestClickAsync(this, up: true), owner.ReportError, () => CanTest);
        ClickDownCommand = new AsyncCommand(() => owner.TestClickAsync(this, up: false), owner.ReportError, () => CanTest);
        SelectCommand = new RelayCommand(() => owner.PickCell(At, shift: false));
    }

    /// <summary>Selects this field (its name button in the field table).</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>First character (0-based) and number of characters; set by picking cells.</summary>
    public int At { get; internal set; }
    public int Len { get; internal set; }

    /// <summary>"W1", "W2"… for wheels, "K" for the keypad field, "R/O" for read-only text.</summary>
    public string Name { get; internal set; } = "";

    public FieldKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value || !_owner.CanChangeKind(this, value)) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWheel));
            OnPropertyChanged(nameof(IsKeypad));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(KindLabel));
            if (value == FieldKind.Wheel && _range.Length == 0) _range = EnterValueEditorViewModel.DefaultRange(Len);
            _owner.Changed();
        }
    }

    public bool IsWheel => Kind == FieldKind.Wheel;
    public bool IsKeypad => Kind == FieldKind.Keypad;
    public bool IsReadOnly => Kind == FieldKind.ReadOnly;

    /// <summary>Wheel: the control to turn, in DCS-BIOS's exact spelling.</summary>
    public string Control { get => _control; set { if (SetProperty(ref _control, _owner.Canonical(value))) _owner.Changed(); } }

    /// <summary>Wheel: "0-9", "22-39", or a list in click order such as "X,Y".</summary>
    public string Range { get => _range; set { if (SetProperty(ref _range, value)) _owner.Changed(); } }

    public WheelMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            RefreshCommands();
            _owner.Changed();
        }
    }

    public IReadOnlyList<WheelMode> Modes { get; } = Enum.GetValues<WheelMode>();

    /// <summary>Keypad: where the text sits in the field.</summary>
    public TextAlign Align { get => _align; set { if (SetProperty(ref _align, value)) _owner.Changed(); } }

    /// <summary>The Left / Right toggles; clicking the one that is on leaves it on.</summary>
    public bool AlignLeft { get => Align == TextAlign.Left; set => SetAlign(value ? TextAlign.Left : Align); }
    public bool AlignRight { get => Align == TextAlign.Right; set => SetAlign(value ? TextAlign.Right : Align); }

    private void SetAlign(TextAlign align)
    {
        Align = align;
        OnPropertyChanged(nameof(AlignLeft));
        OnPropertyChanged(nameof(AlignRight));
    }

    public bool Flip { get => _flip; set => SetProperty(ref _flip, value); }

    /// <summary>Width in characters, and the place of the last digit (10, 1, 0.1…) or "—" for a letter wheel; from where the field sits.</summary>
    public int Width => Len;
    public string Place { get; internal set; } = "";

    /// <summary>What the field shows now ("25"), or blank without DCS.</summary>
    public string Shown { get; internal set; } = "";

    /// <summary>"✓ up works", "⇅ flipped" or "did not move" after a test click.</summary>
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool CanTest => _owner.CanTest && IsWheel && Mode != WheelMode.Set;
    public AsyncCommand ClickUpCommand { get; }
    public AsyncCommand ClickDownCommand { get; }

    /// <summary>"Wheel", "Keypad" or "Read-only", for the field table.</summary>
    public string KindLabel => Kind switch { FieldKind.Wheel => "Wheel", FieldKind.Keypad => "Keypad", _ => "Read-only" };

    internal void RefreshCommands()
    {
        ClickUpCommand.Refresh();
        ClickDownCommand.Refresh();
    }

    internal void Notify()
    {
        OnPropertyChanged(nameof(At));
        OnPropertyChanged(nameof(Len));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Place));
        OnPropertyChanged(nameof(Shown));
        OnPropertyChanged(nameof(Range));
        OnPropertyChanged(nameof(AlignLeft));
        OnPropertyChanged(nameof(AlignRight));
    }

    public DisplayField ToField() => new()
    {
        At = At,
        Len = Len,
        Kind = Kind,
        Control = IsWheel ? Control.Trim() : null,
        Range = IsWheel && Range.Trim().Length > 0 ? Range.Trim() : null,
        Mode = IsWheel ? Mode : null,
        Flip = IsWheel && Flip,
        Align = IsKeypad ? Align : null,
    };
}

/// <summary>One keypad key: a character and the control that types it.</summary>
public sealed class KeypadKeyViewModel(EnterValueEditorViewModel owner, string character, string control) : ObservableObject
{
    private string _control = owner.Canonical(control);
    private bool _used, _missing;

    public string Character { get; } = character;
    public string Label => Character == " " ? "space" : Character;

    public string Control
    {
        get => _control;
        set { if (SetProperty(ref _control, owner.Canonical(value))) owner.Changed(); }
    }

    /// <summary>The try value uses this key.</summary>
    public bool Used { get => _used; internal set => SetProperty(ref _used, value); }

    /// <summary>The try value uses this key, but it has no control.</summary>
    public bool Missing { get => _missing; internal set => SetProperty(ref _missing, value); }
}

/// <summary>
/// The "Enter value" window: one read-back display shown cell by cell. The user selects cells (click, Shift+click)
/// and makes them a wheel, the keypad field or read-only text; place and width come from where the field sits.
/// Picking fields needs a live display (DCS running); everything else can be edited without it.
/// </summary>
public sealed class EnterValueEditorViewModel : ObservableObject
{
    public const string NeedsDcs = "Picking fields needs DCS running: the display shows where each digit is.";

    private readonly EnterValueContext _context;
    private readonly IDialogs _dialogs;
    private readonly EnterValueStep? _legacy;
    private readonly List<string>? _before, _after;
    private string _value, _display, _sample, _decimals, _timeout, _up, _down, _delay, _maxSteps, _press, _try;
    private bool _isKeypad;
    private (int Start, int End)? _selection;
    private string? _message, _legacyNote;
    private string _liveText = "";

    public EnterValueEditorViewModel(EnterValueStep step, IEnumerable<string> phrases, EnterValueContext context, IDialogs dialogs)
    {
        _context = context;
        _dialogs = dialogs;
        Slots = SlotsOf(phrases);

        if (step.Fields is not { Count: > 0 })
        {
            // An older step: convert it when possible, else keep it as it is until fields are picked.
            var display = step.Verify?.Output;
            var live = display is null ? null : ReadDisplay(display);
            if (EnterValueUpgrade.TryUpgrade(step, context.Catalog, live, out var note) is { } upgraded)
            {
                // The layout comes from what the display shows right now (or a guess): it may be in another mode.
                _legacyNote = note ?? "Converted from the old layout using what the display shows now. Check that the fields sit on the right characters.";
                step = upgraded;
            }
            else if ((step.Wheels is { Count: > 0 }) || (step.Keys is { Count: > 0 }))
            {
                _legacy = step;
                _legacyNote = note;
            }
        }

        _value = step.Value;
        _isKeypad = step.Method == EnterMethod.Keypad;
        _display = Canonical(step.Display ?? step.Verify?.Output ?? "");
        _sample = step.Sample ?? "";
        _decimals = step.Decimals?.ToString(CultureInfo.InvariantCulture) ?? "";
        _timeout = (step.TimeoutMs ?? step.Verify?.TimeoutMs ?? DisplayLayout.DefaultTimeoutMs).ToString(CultureInfo.InvariantCulture);
        _up = string.Join(",", step.Up ?? EnterValueStep.DefaultUp);
        _down = string.Join(",", step.Down ?? EnterValueStep.DefaultDown);
        _delay = step.DelayMs.ToString(CultureInfo.InvariantCulture);
        _maxSteps = step.MaxSteps.ToString(CultureInfo.InvariantCulture);
        _press = string.Join(",", step.Press ?? EnterValueStep.DefaultPress);
        _before = step.Before;
        _after = step.After;
        foreach (var field in step.Fields ?? []) Fields.Add(new DisplayFieldViewModel(this, field));
        foreach (var (ch, control) in step.Keys ?? []) Keys.Add(new KeypadKeyViewModel(this, ch, control));
        _try = _isKeypad ? "alpha bravo one" : "";

        StringOutputs = context.Catalog.Controls.Where(c => c.PrimaryStringOutput != null).Select(c => c.Identifier).Order(StringComparer.OrdinalIgnoreCase).ToList();
        InputControls = context.Catalog.Controls.Where(c => c.Inputs.Count > 0).Select(c => c.Identifier).Order(StringComparer.OrdinalIgnoreCase).ToList();

        AddWheelCommand = new RelayCommand(() => Apply(FieldKind.Wheel));
        AddKeypadCommand = new RelayCommand(() => Apply(FieldKind.Keypad));
        AddReadOnlyCommand = new RelayCommand(() => Apply(FieldKind.ReadOnly));
        RemoveFieldCommand = new RelayCommand(RemoveSelected);
        SuggestCommand = new RelayCommand(Suggest);
        FillKeysCommand = new RelayCommand(FillKeys);
        AddKeyCommand = new RelayCommand(AddKey);
        RefreshLive();
    }

    public IReadOnlyList<string> Slots { get; }
    public IReadOnlyList<string> StringOutputs { get; }
    public IReadOnlyList<string> InputControls { get; }
    public ObservableCollection<DisplayCellViewModel> Cells { get; } = [];
    public ObservableCollection<DisplayFieldViewModel> Fields { get; } = [];
    public ObservableCollection<KeypadKeyViewModel> Keys { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    public RelayCommand AddWheelCommand { get; }
    public RelayCommand AddKeypadCommand { get; }
    public RelayCommand AddReadOnlyCommand { get; }
    public RelayCommand RemoveFieldCommand { get; }
    public RelayCommand SuggestCommand { get; }
    public RelayCommand FillKeysCommand { get; }
    public RelayCommand AddKeyCommand { get; }

    public string Value { get => _value; set { if (SetProperty(ref _value, value)) Changed(); } }

    /// <summary>The read-back display: every wheel is read from it, and it is checked after entering.</summary>
    public string Display
    {
        get => _display;
        set
        {
            if (!SetProperty(ref _display, Canonical(value))) return;
            _sample = "";
            RefreshLive();
        }
    }

    public bool IsKeypad
    {
        get => _isKeypad;
        set
        {
            if (!SetProperty(ref _isKeypad, value)) return;
            OnPropertyChanged(nameof(IsWheels));
            OnPropertyChanged(nameof(SuggestLabel));
            // Wheels become the keypad field and back; read-only text stays. A keypad step has one field, so
            // several wheels are dropped and the user picks the field to type into.
            var to = value ? FieldKind.Keypad : FieldKind.Wheel;
            var others = Fields.Where(f => !f.IsReadOnly).ToList();
            Message = null;
            if (value && others.Count > 1)
            {
                foreach (var old in others) Fields.Remove(old);
                _selection = null;
                Message = "A keypad step types into one field. Select it on the display and press Keypad.";
                others = [];
            }
            foreach (var old in others)
            {
                var replacement = new DisplayFieldViewModel(this, new DisplayField
                {
                    At = old.At, Len = old.Len, Kind = to, Range = to == FieldKind.Wheel ? DefaultRange(old.Len) : null,
                });
                Fields[Fields.IndexOf(old)] = replacement;
            }
            if (value && _try.Length == 0) _try = "alpha bravo one";
            if (!value && !EnterValue.TryParseNumber(_try, out _)) _try = "";
            OnPropertyChanged(nameof(Try));
            Changed();
        }
    }

    public bool IsWheels { get => !_isKeypad; set => IsKeypad = !value; }

    public string TimeoutMs { get => _timeout; set { if (SetProperty(ref _timeout, value)) Changed(); } }
    public string Decimals { get => _decimals; set { if (SetProperty(ref _decimals, value)) Changed(); } }
    public string Up { get => _up; set { if (SetProperty(ref _up, value)) Changed(); } }
    public string Down { get => _down; set { if (SetProperty(ref _down, value)) Changed(); } }
    public string DelayMs { get => _delay; set { if (SetProperty(ref _delay, value)) Changed(); } }
    public string MaxSteps { get => _maxSteps; set { if (SetProperty(ref _maxSteps, value)) Changed(); } }
    public string Press { get => _press; set { if (SetProperty(ref _press, value)) Changed(); } }

    /// <summary>Implied decimals only matter on a display without a decimal point.</summary>
    public bool ShowDecimals => IsWheels && _sample.Length > 0 && !_sample.Contains('.');

    /// <summary>A value to preview: a number, or spoken spelling ("alpha bravo one").</summary>
    public string Try { get => _try; set { if (SetProperty(ref _try, value)) Changed(); } }

    public bool IsLive => _context.LiveBuffer() != null;
    public bool CanTest => IsLive && _context.Send != null;

    /// <summary>"● live from DCS-BIOS", or the warning shown without DCS.</summary>
    public string LiveStatus => IsLive
        ? "● live from DCS-BIOS"
        : $"No live connection to DCS. Showing {Cells.Count} empty fields (length from DCS-BIOS). Values show when DCS runs.";

    public string SuggestLabel => IsKeypad ? "Suggest fields…" : "Fill from radio…";

    /// <summary>The last message from picking, a test click or a suggestion.</summary>
    public string? Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>A note about an older step: converted with a guessed layout, or kept as it is until fields are picked.</summary>
    public string? LegacyNote => _legacyNote;
    public bool HasLegacyNote => !string.IsNullOrEmpty(_legacyNote);

    /// <summary>Keys pressed before or after the text in an older step; still pressed, but no longer edited here.</summary>
    public bool HasHiddenKeys => _before is { Count: > 0 } || _after is { Count: > 0 };

    public (int Start, int End)? Selection => _selection;

    /// <summary>The field the selection is exactly on, or null.</summary>
    public DisplayFieldViewModel? SelectedField =>
        _selection is { } s ? Fields.FirstOrDefault(f => f.At == s.Start && f.At + f.Len - 1 == s.End) : null;

    public bool HasSelection => _selection != null;
    public bool HasNoSelection => _selection == null;

    /// <summary>"W2 — characters 3–4", or "New field — characters 6–7" for cells no field uses yet.</summary>
    public string SelectionTitle => _selection is not { } s ? ""
        : $"{SelectedField?.Name ?? "New field"} — character{(s.End > s.Start ? $"s {s.Start + 1}–{s.End + 1}" : $" {s.Start + 1}")}";

    /// <summary>Smaller cells for a long display (a CDU line).</summary>
    public bool CompactCells => Cells.Count > 10;

    /// <summary>"Wheel" or "Keypad": the kind the step's method uses.</summary>
    public string MainKindLabel => IsKeypad ? "Keypad" : "Wheel";
    public bool SelectedIsMainKind => SelectedField is { IsReadOnly: false };
    public bool SelectedIsReadOnly => SelectedField is { IsReadOnly: true };
    public RelayCommand MainKindCommand => IsKeypad ? AddKeypadCommand : AddWheelCommand;
    public bool HasSelectedField => SelectedField != null;

    /// <summary>The selection is on cells no field uses yet: it can become a new field.</summary>
    public bool CanAddField => _selection != null && SelectedField == null;

    /// <summary>"'2' → '5'", "3 clicks up", "types 'N41'", or a reason the value can't be entered, for the selected field.</summary>
    public string SelectedPreview { get; private set; } = "";

    public bool HasProblems => Problems.Count > 0;

    // ---- picking ------------------------------------------------------------------------------------------

    /// <summary>A click on a cell: select it, or with Shift widen the selection (resizing the selected field).</summary>
    public void PickCell(int index, bool shift)
    {
        Message = null;
        if (index < 0 || index >= Cells.Count) return;
        var at = FieldAt(index);
        if (shift && _selection is { } sel)
        {
            if (!IsLive)
            {
                Message = NeedsDcs;
                return;
            }
            var (a, b) = (Math.Min(sel.Start, index), Math.Max(sel.End, index));
            var selected = SelectedField;
            if (IsWheels && Text(_sample, a, b).Contains('.') && (selected is null || selected.IsWheel))
            {
                Message = "A wheel can't span the decimal point.";
                return;
            }
            if (Fields.FirstOrDefault(f => f != selected && f.At <= b && f.At + f.Len - 1 >= a) is { } other)
            {
                Message = $"That overlaps {other.Name}. Remove it first.";
                return;
            }
            if (selected != null)
            {
                selected.At = a;
                selected.Len = b - a + 1;
            }
            Select(a, b);
            return;
        }
        if (at != null) Select(at.At, at.At + at.Len - 1);
        else Select(index, index);
    }

    private void Select(int start, int end)
    {
        _selection = (start, end);
        Changed();
    }

    /// <summary>The kind buttons: change the selected field, or make the selected cells a new field.</summary>
    private void Apply(FieldKind kind)
    {
        if (SelectedField is { } field) field.Kind = kind;
        else AddField(kind);
    }

    private void AddField(FieldKind kind)
    {
        Message = null;
        if (_selection is not { } sel || SelectedField != null) return;
        if (!IsLive)
        {
            Message = NeedsDcs;
            return;
        }
        if (kind == FieldKind.Keypad && Fields.Any(f => f.IsKeypad))
        {
            Message = "A keypad step types into one field. For a second field (e.g. long after lat), add another Enter value step.";
            return;
        }
        if (kind == FieldKind.Wheel && Text(_sample, sel.Start, sel.End).Contains('.'))
        {
            Message = "A wheel can't span the decimal point.";
            return;
        }
        var field = new DisplayFieldViewModel(this, new DisplayField
        {
            At = sel.Start, Len = sel.End - sel.Start + 1, Kind = kind,
            Range = kind == FieldKind.Wheel ? DefaultRange(sel.End - sel.Start + 1) : null,
        });
        var index = Fields.TakeWhile(f => f.At < field.At).Count();
        Fields.Insert(index, field);
        Changed();
    }

    private void RemoveSelected()
    {
        if (SelectedField is not { } field) return;
        Fields.Remove(field);
        Changed();
    }

    /// <summary>A keypad step has one keypad field; switching a field's kind checks that and the method.</summary>
    internal bool CanChangeKind(DisplayFieldViewModel field, FieldKind kind)
    {
        Message = null;
        if (kind == FieldKind.Keypad && (IsWheels || Fields.Any(f => f != field && f.IsKeypad)))
        {
            Message = IsWheels ? "Switch the step to Keypad first." : "A keypad step types into one field.";
            return false;
        }
        if (kind == FieldKind.Wheel && IsKeypad)
        {
            Message = "Switch the step to Switches / dials first.";
            return false;
        }
        return true;
    }

    // ---- live --------------------------------------------------------------------------------------------

    /// <summary>Reads the display and refreshes cells, fields and previews; the window calls this a few times a second.</summary>
    public void RefreshLive()
    {
        var live = Display.Trim().Length > 0 ? ReadDisplay(Display.Trim()) : null;
        _liveText = live ?? "";
        // The sample keeps the last layout seen live: length, decimal point and read-only characters.
        if (live != null && live.Trim().Length > 0) _sample = live;
        else if (_sample.Length == 0 && DisplayLength() is { } length) _sample = new string(' ', length);
        foreach (var field in Fields) field.RefreshCommands();
        Changed();
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(CanTest));
    }

    /// <summary>Sends one click up or down and sees which way the field moved; a wrong way sets <see cref="DisplayFieldViewModel.Flip"/>.</summary>
    public async Task TestClickAsync(DisplayFieldViewModel field, bool up)
    {
        if (_context.Send is null || !IsLive || !field.IsWheel || field.Mode == WheelMode.Set) return;
        Message = null;
        var range = DisplayLayout.RangeOf(field.ToField());
        var before = ReadPosition(field, range);
        var swapped = up != field.Flip;
        var args = field.Mode == WheelMode.Step
            ? [swapped ? "INC" : "DEC"]
            : List(swapped ? Up : Down) ?? (swapped ? EnterValueStep.DefaultUp : EnterValueStep.DefaultDown).ToList();
        foreach (var arg in args)
        {
            await _context.Send(field.Control, arg);
            await Task.Delay(TestDelay);
        }
        await Task.Delay(TestDelay);
        var after = ReadPosition(field, range);
        if (before is null || after is null) field.Status = "not in range";
        else if (after == before) field.Status = "did not move";
        else if (EnterValue.TurnDirection(before.Value, after.Value, (range.Low, range.High)) > 0 == up) field.Status = field.Flip ? "⇅ flipped" : "✓ up works";
        else
        {
            field.Flip = !field.Flip;
            field.Status = "⇅ flipped";
        }
        RefreshLive();
    }

    /// <summary>Wait between a test click's lines and before reading back.</summary>
    public TimeSpan TestDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    internal void ReportError(Exception ex) => Message = ex.Message;

    private int? ReadPosition(DisplayFieldViewModel field, ValueRange range)
    {
        var text = ReadDisplay(Display.Trim());
        return text is null ? null : range.IndexOf(DisplayLayout.Read(text, field.ToField()));
    }

    /// <summary>The display's text padded to its DCS-BIOS length, or null without DCS or for an unknown display.</summary>
    private string? ReadDisplay(string id)
    {
        if (_context.LiveBuffer() is not { } buffer || _context.Catalog.Find(id)?.PrimaryStringOutput is not { } output) return null;
        var length = output.MaxLength ?? 0;
        return buffer.ReadString(output.Address, length).PadRight(length);
    }

    private int? DisplayLength() => _context.Catalog.Find(Display.Trim())?.PrimaryStringOutput?.MaxLength;

    // ---- suggestions ---------------------------------------------------------------------------------------

    private const string PresetsSuffix = " presets";

    /// <summary>
    /// Fill from radio: finds a radio's wheels (or preset selector) and, with DCS running, places them on the live
    /// display. Without DCS only the display is filled in.
    /// </summary>
    private void Suggest()
    {
        Message = null;
        if (IsKeypad)
        {
            FillKeys();
            if (!Fields.Any(f => f.IsKeypad)) Message = IsLive ? "Select the field to type into and press Keypad." : NeedsDcs;
            return;
        }
        var options = EnterValueGuess.RadioPrefixes(_context.Catalog)
            .Concat(EnterValueGuess.PresetPrefixes(_context.Catalog).Select(p => p + PresetsSuffix))
            .ToList();
        if (options.Count == 0)
        {
            Message = "No radio wheels (…_1_DIAL, …_01_DIAL) or preset selectors (…_PRESETS) found in this aircraft's DCS-BIOS controls.";
            return;
        }
        var choice = options.Count == 1
            ? options[0]
            : _dialogs.Prompt("Fill from radio", "Which radio? Its frequency wheels, or \"… presets\" for the preset channel selector", options[0], options)?.Trim();
        if (string.IsNullOrEmpty(choice)) return;

        var prefix = choice;
        List<Wheel> wheels;
        string? range = null;
        if (choice.EndsWith(PresetsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            prefix = choice[..^PresetsSuffix.Length].Trim();
            wheels = EnterValueGuess.PresetWheel(_context.Catalog, prefix) is { } preset ? [preset] : [];
            range = "1-20";
        }
        else
        {
            wheels = EnterValueGuess.Wheels(_context.Catalog, prefix);
        }
        if (Display.Trim().Length == 0 && EnterValueGuess.Display(_context.Catalog, prefix, wheels.Select(w => w.Control)) is { } display) Display = display;

        var live = ReadDisplay(Display.Trim());
        if (live is null || live.Trim().Length == 0)
        {
            Message = $"Found {wheels.Count} controls. {NeedsDcs}";
            return;
        }
        if (EnterValueUpgrade.PlaceWheels(live, wheels, out var problem) is not { } placed)
        {
            Message = problem;
            return;
        }
        Fields.Clear();
        foreach (var field in placed)
        {
            field.Range = range ?? DefaultRange(field.Len);
            Fields.Add(new DisplayFieldViewModel(this, field));
        }
        var point = live.IndexOf('.');
        if (point >= 0 && placed.Count > 0 && point > placed.Min(f => f.At) && point < placed.Max(f => f.At + f.Len) && FieldAt(point) is null)
        {
            Fields.Insert(Fields.TakeWhile(f => f.At < point).Count(), new DisplayFieldViewModel(this, new DisplayField { At = point, Len = 1, Kind = FieldKind.ReadOnly }));
        }
        _selection = null;
        Message = $"Suggested {placed.Count} fields from the live display. Check them before saving.";
        Changed();
    }

    private void FillKeys()
    {
        var prefixes = EnterValueGuess.KeypadPrefixes(_context.Catalog);
        if (prefixes.Count == 0)
        {
            Message = "No keypad (CDU_A, CDU_1…) found in this aircraft's DCS-BIOS controls.";
            return;
        }
        var prefix = prefixes.Count == 1 ? prefixes[0] : _dialogs.Prompt("Fill from keypad", "Which keypad? (prefix of its key controls)", prefixes[0], prefixes)?.Trim();
        if (string.IsNullOrEmpty(prefix)) return;
        var (keys, _) = EnterValueGuess.Keys(_context.Catalog, prefix);
        Keys.Clear();
        foreach (var (ch, control) in keys) Keys.Add(new KeypadKeyViewModel(this, ch, control));
        if (Display.Trim().Length == 0 && EnterValueGuess.Display(_context.Catalog, prefix, keys.Values.Where(v => v.Length > 0)) is { } display) Display = display;
        Changed();
    }

    private void AddKey()
    {
        var ch = _dialogs.Prompt("Add key", "Character (one letter, digit or sign)", "");
        if (string.IsNullOrEmpty(ch)) return;
        var trimmed = ch.Trim();
        if (trimmed.Length == 0 && ch != " ") return; // only a single " " means the space key
        var key = trimmed.Length == 0 ? " " : trimmed.ToUpperInvariant()[..1];
        if (Keys.Any(k => k.Character == key)) return;
        Keys.Add(new KeypadKeyViewModel(this, key, ""));
        Changed();
    }

    // ---- saving ------------------------------------------------------------------------------------------

    /// <summary>OK: true to save. With problems the user is asked first.</summary>
    public bool ConfirmSave() =>
        !HasProblems || _dialogs.Confirm("Enter value", "This step has problems:\n\n• " + string.Join("\n• ", Problems) + "\n\nSave anyway?");

    /// <summary>The edited step. An older step whose fields were never picked is saved as it was (with the new value).</summary>
    public EnterValueStep ToStep()
    {
        if (_legacy != null && Fields.Count == 0)
        {
            var kept = Copy(_legacy);
            kept.Value = Value.Trim();
            return kept;
        }
        var step = new EnterValueStep
        {
            Value = Value.Trim(),
            Method = IsKeypad ? EnterMethod.Keypad : EnterMethod.Wheels,
            Display = Display.Trim().Length > 0 ? Display.Trim() : null,
            Sample = _sample.Length > 0 ? _sample : null,
            Fields = Fields.OrderBy(f => f.At).Select(f => f.ToField()).ToList(),
            TimeoutMs = Int(TimeoutMs, DisplayLayout.DefaultTimeoutMs) is var timeout && timeout != DisplayLayout.DefaultTimeoutMs ? timeout : null,
            DelayMs = Int(DelayMs, 80),
            MaxSteps = Int(MaxSteps, 40),
        };
        if (IsKeypad)
        {
            step.Keys = Keys.GroupBy(k => k.Character).ToDictionary(g => g.Key, g => g.Last().Control.Trim());
            step.Press = OrNull(List(Press), EnterValueStep.DefaultPress);
            step.Before = _before;
            step.After = _after;
        }
        else
        {
            step.Up = OrNull(List(Up), EnterValueStep.DefaultUp);
            step.Down = OrNull(List(Down), EnterValueStep.DefaultDown);
            step.Decimals = int.TryParse(Decimals, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        }
        return step;
    }

    // ---- recompute ---------------------------------------------------------------------------------------

    internal void Changed()
    {
        var step = ToStep();
        NameFields();
        Problems.Clear();
        if (step.Value.Length == 0) Problems.Add("Pick the spoken value, e.g. {freq}.");
        else if (Slots.Count > 0 && !Slots.Contains(step.Value)) Problems.Add($"{step.Value} is not a slot of the voice line ({string.Join(", ", Slots)}).");
        if (_legacy != null && Fields.Count == 0)
        {
            Problems.Add("This step still uses the old layout. Pick its fields on the live display to convert it.");
        }
        else
        {
            foreach (var p in DisplayLayout.Validate(step)) Problems.Add(Sentence(p));
            CheckKnown(Display, "Display");
            foreach (var field in Fields.Where(f => f.IsWheel)) CheckKnown(field.Control, $"{field.Name} control");
            if (IsKeypad) foreach (var key in Keys) CheckKnown(key.Control, $"Key {key.Label}");
        }

        // The try value: what each cell will show, and the selected field's preview.
        IReadOnlyList<FieldTarget> targets = [];
        var text = TryText();
        if (text.Length > 0 && Fields.Count > 0)
        {
            targets = DisplayLayout.Targets(step, text, out var problem);
            if (problem != null) Problems.Add(Sentence(problem));
            if (IsKeypad && EnterValue.MissingKeys(step, text) is { Count: > 0 } missing)
                Problems.Add("No key for " + string.Join(", ", missing.Select(c => c == ' ' ? "space" : $"\"{c}\"")) + ".");
        }
        var used = IsKeypad ? text.ToUpperInvariant().ToHashSet() : [];
        foreach (var key in Keys)
        {
            var isUsed = key.Character.Length == 1 && used.Contains(key.Character[0]);
            key.Used = isUsed && key.Control.Trim().Length > 0;
            key.Missing = isUsed && key.Control.Trim().Length == 0;
        }

        BuildCells(step, targets);
        SelectedPreview = Preview(step, targets);

        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(SelectedField));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(CompactCells));
        OnPropertyChanged(nameof(MainKindLabel));
        OnPropertyChanged(nameof(MainKindCommand));
        OnPropertyChanged(nameof(SelectedIsMainKind));
        OnPropertyChanged(nameof(SelectedIsReadOnly));
        OnPropertyChanged(nameof(HasSelectedField));
        OnPropertyChanged(nameof(CanAddField));
        OnPropertyChanged(nameof(SelectedPreview));
        OnPropertyChanged(nameof(ShowDecimals));
        OnPropertyChanged(nameof(LiveStatus));
        OnPropertyChanged(nameof(HasProblems));
    }

    private string TryText()
    {
        var t = Try.Trim();
        if (t.Length == 0) return "";
        if (EnterValue.TryParseNumber(t, out _)) return t;
        return (SlotParser.SpellText(t) ?? t).ToUpperInvariant();
    }

    private void NameFields()
    {
        var wheel = 0;
        foreach (var field in Fields.OrderBy(f => f.At))
        {
            field.Name = field.Kind switch { FieldKind.Wheel => $"W{++wheel}", FieldKind.Keypad => "K", _ => "R/O" };
        }
    }

    private void BuildCells(EnterValueStep step, IReadOnlyList<FieldTarget> targets)
    {
        var length = Math.Max(_sample.Length, _liveText.Length);
        var target = new string(' ', length).ToCharArray();
        foreach (var t in targets)
        {
            for (var i = 0; i < t.Field.Len && t.Field.At + i < length; i++) target[t.Field.At + i] = t.Text[i];
        }
        var selected = SelectedField;
        var live = IsLive;
        Cells.Clear();
        for (var i = 0; i < length; i++)
        {
            var field = FieldAt(i);
            var shown = live ? Char(_liveText, i) : field?.IsReadOnly == true ? Char(_sample, i) : "";
            var aimed = field is { IsReadOnly: false } && targets.Count > 0 ? target[i].ToString() : "";
            Cells.Add(new DisplayCellViewModel
            {
                Index = i,
                Text = shown,
                Target = aimed,
                Changes = aimed.Trim().Length > 0 && (!live || !string.Equals(aimed, shown, StringComparison.OrdinalIgnoreCase)),
                IsSelected = _selection is { } s && i >= s.Start && i <= s.End,
                Label = field != null && field.At == i ? field.Name : "",
                Kind = field?.Kind,
                IsFieldStart = field != null && field.At == i,
                IsFieldEnd = field != null && field.At + field.Len - 1 == i,
                InSelectedField = field != null && field == selected,
            });
        }
        foreach (var field in Fields)
        {
            field.Place = field.IsWheel && DisplayLayout.PlaceOf(step, step.Fields!.First(f => f.At == field.At)) is { } place
                ? place.ToString("0.############", CultureInfo.InvariantCulture)
                : "—";
            field.Shown = live ? DisplayLayout.Read(_liveText, field.ToField()) : "";
            field.Notify();
        }
    }

    private string Preview(EnterValueStep step, IReadOnlyList<FieldTarget> targets)
    {
        if (SelectedField is not { } field || field.IsReadOnly) return "";
        var target = targets.FirstOrDefault(t => t.Field.At == field.At)?.Text;
        if (target is null) return "";
        if (field.IsKeypad) return $"types ‘{target.Trim()}’";
        var current = IsLive ? DisplayLayout.Read(_liveText, field.ToField()) : null;
        var range = DisplayLayout.RangeOf(field.ToField());
        var head = $"‘{current?.Trim() ?? "?"}’ → ‘{target.Trim()}’";
        if (field.Mode == WheelMode.Set) return $"{head} · send {range.IndexOf(target)}";
        if (current is null || range.IndexOf(current) is not { } from || range.IndexOf(target) is not { } to) return head;
        var d = to - from;
        return d == 0 ? $"{head} · no change" : $"{head} · {Math.Abs(d)} click{(Math.Abs(d) == 1 ? "" : "s")} {(d > 0 ? "up" : "down")}";
    }

    private DisplayFieldViewModel? FieldAt(int index) => Fields.FirstOrDefault(f => index >= f.At && index < f.At + f.Len);

    /// <summary>"X is not a DCS-BIOS control of this aircraft", when the aircraft's controls are loaded and X isn't one of them.</summary>
    private void CheckKnown(string id, string what)
    {
        var name = id.Trim();
        if (name.Length > 0 && _context.Catalog.Count > 0 && !_context.Catalog.Contains(name))
            Problems.Add($"{what} \"{name}\" is not a DCS-BIOS control of this aircraft; pick one from the list.");
    }

    /// <summary>The catalog's exact spelling of a control; DCS-BIOS ignores a name in the wrong case.</summary>
    internal string Canonical(string? id)
    {
        var text = id ?? "";
        return text.Trim().Length > 0 && _context.Catalog.Find(text.Trim()) is { } control ? control.Identifier : text;
    }

    internal static string DefaultRange(int len) => $"0-{(int)Math.Pow(10, Math.Clamp(len, 1, 6)) - 1}";

    private static string Text(string text, int start, int end) =>
        start >= text.Length ? "" : text.Substring(start, Math.Min(end, text.Length - 1) - start + 1);

    private static string Char(string text, int i) => i < text.Length ? text[i].ToString() : "";

    private static string Sentence(string problem) => problem.Length == 0 ? problem : char.ToUpperInvariant(problem[0]) + problem[1..] + (problem.EndsWith('.') ? "" : ".");

    /// <summary>Slot names of the voice line as "{freq}", in order.</summary>
    private static List<string> SlotsOf(IEnumerable<string> phrases)
    {
        var slots = new List<string>();
        foreach (var spec in phrases)
        {
            try
            {
                foreach (var phrase in PhraseExpander.Expand(spec))
                foreach (var slot in PhraseTemplate.Parse(phrase).Slots)
                {
                    var name = $"{{{slot.Name}}}";
                    if (!slots.Contains(name)) slots.Add(name);
                }
            }
            catch (FormatException)
            {
                // shown by the command editor
            }
        }
        return slots;
    }

    /// <summary>A deep copy, so Cancel leaves the profile's step untouched.</summary>
    public static EnterValueStep Copy(EnterValueStep step) =>
        (EnterValueStep)JsonSerializer.Deserialize<Step>(JsonSerializer.Serialize<Step>(step, ProfileSerializer.Options), ProfileSerializer.Options)!;

    private static List<string>? List(string text)
    {
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return items.Count > 0 ? items : null;
    }

    private static List<string>? OrNull(List<string>? items, IReadOnlyList<string> defaults) =>
        items is null || items.SequenceEqual(defaults) ? null : items;

    private static int Int(string text, int fallback) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
