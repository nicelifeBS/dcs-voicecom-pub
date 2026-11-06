using System.Collections.ObjectModel;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Seeding;

namespace DcsVoiceCom.ViewModels;

/// <summary>One generated command in the preview.</summary>
public sealed class SeedItemViewModel(SeedCandidate candidate)
{
    public SeedCandidate Candidate { get; } = candidate;
    public string VoiceLine => Candidate.VoiceLine;
    public string Category => Candidate.Category;
    public string Steps => StepText.Summarize(Candidate.Command.Steps);
    public bool Critical => Candidate.Command.Critical;
    public bool IsCovered => Candidate.Status == SeedStatus.Covered;

    public string StatusText => Candidate.Status switch
    {
        SeedStatus.Covered => $"you have “{Candidate.CoveredBy}”",
        SeedStatus.Update => "replaces seeded",
        _ => "",
    };
}

/// <summary>A panel (DCS-BIOS category) with a check box: ticked panels are added.</summary>
public sealed class SeedCategoryViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public SeedCategoryViewModel(string name, IReadOnlyList<SeedItemViewModel> items, Action changed)
    {
        Name = name;
        Items = items;
        _changed = changed;
    }

    public string Name { get; }
    public IReadOnlyList<SeedItemViewModel> Items { get; }

    /// <summary>Commands this panel would add: everything not covered by one of yours.</summary>
    public int AddCount => Items.Count(i => !i.IsCovered);
    public string CountText => AddCount == Items.Count ? $"{AddCount}" : $"{AddCount} of {Items.Count}";
    public bool HasCritical => Items.Any(i => i.Critical && !i.IsCovered);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            _changed();
        }
    }
}

/// <summary>
/// The seeding window: an aircraft's generated commands grouped by panel. The user ticks panels,
/// looks through the preview, and adds them; nothing is enabled until they choose to.
/// </summary>
public sealed class SeedViewModel : ObservableObject
{
    private SeedCategoryViewModel? _selectedCategory;
    private string _search = "";

    public SeedViewModel(string aircraft, SeedResult result)
    {
        Aircraft = aircraft;
        Result = result;
        foreach (var group in result.Candidates.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            Categories.Add(new SeedCategoryViewModel(group.Key, group.Select(c => new SeedItemViewModel(c)).ToList(), Changed));
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        SelectedCategory = Categories.FirstOrDefault();
    }

    public string Aircraft { get; }
    public SeedResult Result { get; }
    public ObservableCollection<SeedCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<SeedItemViewModel> Preview { get; } = [];
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public string Summary =>
        $"{Result.Candidates.Count} commands from the DCS-BIOS definitions of {Aircraft}, in {Categories.Count} panels. " +
        $"{Result.SkippedControls} controls have nothing to say a command for (lights, displays, knobs).";

    public SeedCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            UpdatePreview();
        }
    }

    /// <summary>Filters the preview across all panels by voice line, panel or control ID.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            UpdatePreview();
            OnPropertyChanged(nameof(PreviewTitle));
        }
    }

    public string PreviewTitle => Search.Trim().Length > 0 ? $"Matching “{Search.Trim()}”" : SelectedCategory?.Name ?? "";

    /// <summary>Generated commands in ticked panels that are not covered by one of yours.</summary>
    public IReadOnlyList<SeedCandidate> Picked =>
        Categories.Where(c => c.IsChecked).SelectMany(c => c.Items).Where(i => !i.IsCovered).Select(i => i.Candidate).ToList();

    public int PickedCount => Categories.Where(c => c.IsChecked).Sum(c => c.AddCount);
    public bool CanAdd => PickedCount > 0;
    public string AddText => PickedCount == 1 ? "Add 1 command" : $"Add {PickedCount} commands";

    private void SetAll(bool value)
    {
        foreach (var category in Categories) category.IsChecked = value;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddText));
    }

    private void UpdatePreview()
    {
        Preview.Clear();
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = words.Length > 0
            ? Categories.SelectMany(c => c.Items).Where(i => words.All(w =>
                i.VoiceLine.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Category.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Candidate.Control.Identifier.Contains(w, StringComparison.OrdinalIgnoreCase)))
            : SelectedCategory?.Items ?? [];
        foreach (var item in items) Preview.Add(item);
        OnPropertyChanged(nameof(PreviewTitle));
    }
}
