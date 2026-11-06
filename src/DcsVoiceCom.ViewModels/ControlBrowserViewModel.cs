using System.Collections.ObjectModel;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels;

/// <summary>One argument button under a control: clicking it adds that DCS-BIOS step (a press for BUTTON).</summary>
public sealed record ArgumentButton(string Label, string Arg, bool Press, RelayCommand Pick)
{
    public string ToolTip => Press ? $"Press: {Arg}, hold {BiosStep.DefaultPressMs} ms, release {BiosStep.DefaultRelease}" : Arg;
}

public sealed class ControlItemViewModel : ObservableObject
{
    private string _liveValue = "";

    public ControlItemViewModel(ControlDefinition control, Action<ControlDefinition, ArgumentChoice> pick)
    {
        Control = control;
        Arguments = ControlArguments.Suggest(control)
            .Select(c => new ArgumentButton(c.Label, c.Arg, c.Press, new RelayCommand(() => pick(control, c))))
            .ToList();
    }

    public ControlDefinition Control { get; }
    public string Identifier => Control.Identifier;
    public string Category => Control.Category;
    public string Description => Control.Description;
    public IReadOnlyList<ArgumentButton> Arguments { get; }
    public bool IsOutputOnly => Arguments.Count == 0;

    public string LiveValue
    {
        get => _liveValue;
        set => SetProperty(ref _liveValue, value);
    }
}

/// <summary>
/// The DCS-BIOS control browser beside the command editor: search by words, filter by category,
/// live cockpit values when DCS is flying the aircraft being edited, and argument buttons that add a step.
/// </summary>
public sealed class ControlBrowserViewModel : ObservableObject
{
    public const string AllCategories = "All categories";

    private ControlCatalog _catalog = ControlCatalog.Empty;
    private string _query = "";
    private string _category = AllCategories;
    private string _customArgument = "";
    private ControlItemViewModel? _selected;
    private string _status = "";

    public ControlBrowserViewModel()
    {
        AddCustomCommand = new RelayCommand(
            () => ArgumentPicked?.Invoke(Selected!.Control, new ArgumentChoice(CustomArgument.Trim(), CustomArgument.Trim())),
            () => Selected != null && CustomArgument.Trim().Length > 0);
    }

    /// <summary>Raised with the control and argument when the user picks one.</summary>
    public event Action<ControlDefinition, ArgumentChoice>? ArgumentPicked;

    public ObservableCollection<ControlItemViewModel> Results { get; } = [];
    public ObservableCollection<string> Categories { get; } = [AllCategories];

    public RelayCommand AddCustomCommand { get; }

    public ControlCatalog Catalog
    {
        get => _catalog;
        set
        {
            _catalog = value;
            // Keep "All categories" in place: clearing the list would make the selected item vanish under the combo box.
            while (Categories.Count > 1) Categories.RemoveAt(1);
            foreach (var c in ControlSearch.Categories(value)) Categories.Add(c);
            _category = AllCategories;
            OnPropertyChanged(nameof(Category));
            Status = value.Count == 0 ? "No DCS-BIOS definitions for this profile." : $"{value.Count} controls";
            Refilter();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value)) Refilter();
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value ?? AllCategories)) Refilter();
        }
    }

    public ControlItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) AddCustomCommand.Refresh();
        }
    }

    /// <summary>An argument typed by hand for the selected control, e.g. "{deg}" or "+1000".</summary>
    public string CustomArgument
    {
        get => _customArgument;
        set
        {
            if (SetProperty(ref _customArgument, value)) AddCustomCommand.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Updates the shown values from the export buffer; clears them when DCS isn't flying this catalog's aircraft.</summary>
    public void RefreshLive(StateBuffer? buffer, bool live)
    {
        foreach (var item in Results) item.LiveValue = live && buffer != null ? LiveValue.Read(buffer, item.Control) : "";
    }

    private void Pick(ControlDefinition control, ArgumentChoice choice) => ArgumentPicked?.Invoke(control, choice);

    private void Refilter()
    {
        var category = _category == AllCategories ? null : _category;
        Results.Clear();
        foreach (var control in ControlSearch.Search(_catalog, _query, category)) Results.Add(new ControlItemViewModel(control, Pick));
        Selected = Results.FirstOrDefault();
    }
}
