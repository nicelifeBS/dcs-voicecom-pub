using System.Collections.ObjectModel;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Runtime;
using DcsVoiceCom.Seeding;

namespace DcsVoiceCom.ViewModels;

public sealed class ProfileItemViewModel : ObservableObject
{
    private int _commandCount;
    private bool _isActive;

    public ProfileItemViewModel(string name, int commandCount)
    {
        Name = name;
        _commandCount = commandCount;
    }

    public string Name { get; }
    public bool IsCommon => Name.Equals(ProfileStore.CommonProfileName, StringComparison.OrdinalIgnoreCase);
    public string Display => IsCommon ? "common (all aircraft)" : Name;

    public int CommandCount
    {
        get => _commandCount;
        set => SetProperty(ref _commandCount, value);
    }

    /// <summary>True for the profile the runtime is using right now.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

public sealed class CommandItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isVisible = true;

    public CommandItemViewModel(Command command, Action changed)
    {
        Command = command;
        _changed = changed;
    }

    public Command Command { get; }
    public string Name => Command.Name.Length > 0 ? Command.Name : "(unnamed)";
    public string Phrase => Command.Phrases.Count > 0 ? Command.Phrases[0] : "(no phrase)";
    public string Summary => StepText.Summarize(Command.Steps);
    public bool Critical => Command.Critical;
    public bool SkipIfAlready => Command.Steps.OfType<BiosStep>().Any(s => s.SkipIfAlready);
    public bool Seeded => Command.Seeded;

    public bool Enabled
    {
        get => Command.Enabled;
        set
        {
            if (Command.Enabled == value) return;
            Command.Enabled = value;
            Command.Seeded = false; // enabling or disabling makes a seeded command yours
            OnPropertyChanged();
            OnPropertyChanged(nameof(Seeded));
            _changed();
        }
    }

    /// <summary>False while the command list's filter hides it.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Phrase));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Critical));
        OnPropertyChanged(nameof(SkipIfAlready));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Seeded));
    }
}

/// <summary>
/// The main window: profiles, the selected profile's commands, the command editor with the DCS-BIOS
/// control browser, the test panel, the log and the status bar. Works without a runtime (editing only).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    public const string AutoProfile = "Auto";

    private ProfileStore _store;
    private DefinitionLoader _definitions;
    private readonly IDialogs _dialogs;
    private readonly IUiThread _ui;
    private IVoiceComRuntime? _runtime;
    private ProfileItemViewModel? _selectedProfile;
    private CommandItemViewModel? _selectedCommand;
    private CommandEditorViewModel? _editor;
    private Profile? _profile;
    private bool _isDirty;
    private bool _showLog = true;
    private string _testText = "";
    private bool _dryRun = true;
    private string _selectedOverride = AutoProfile;
    private bool _isListening;
    private bool _dryRunInFlight;
    private string _dcsStatus = "DCS-BIOS · not started";
    private string _speechStatus = "Speech · not started";
    private string _activeProfileText = "";
    private string _pttText;
    private string _commandFilter = "";
    private bool _hideSeeded;

    public MainViewModel(ProfileStore store, DefinitionLoader definitions, IDialogs dialogs, IUiThread ui, string? pttText = null)
    {
        _store = store;
        _definitions = definitions;
        _dialogs = dialogs;
        _ui = ui;
        _pttText = pttText ?? "not bound";

        Browser.ArgumentPicked += OnArgumentPicked;

        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(p => DeleteProfile(p as ProfileItemViewModel ?? SelectedProfile), p => CanDeleteProfile(p as ProfileItemViewModel ?? SelectedProfile));
        NewCommandCommand = new RelayCommand(NewCommand, () => _profile != null);
        DuplicateCommandCommand = new RelayCommand(p => Duplicate(p as CommandItemViewModel ?? SelectedCommand), p => (p ?? SelectedCommand) is CommandItemViewModel);
        DeleteCommandCommand = new RelayCommand(p => Delete(p as CommandItemViewModel ?? SelectedCommand), p => (p ?? SelectedCommand) is CommandItemViewModel);
        SaveCommand = new AsyncCommand(SaveAsync, ReportError, () => IsDirty);
        RevertCommand = new RelayCommand(Revert, () => IsDirty);
        TestCommand = new AsyncCommand(() => TestAsync(TestText), ReportError, () => TestText.Trim().Length > 0);
        TestSelectedCommand = new AsyncCommand(TestSelectedAsync, ReportError, () => SelectedCommand != null);
        ReloadCommand = new AsyncCommand(ReloadAsync, ReportError);
        ClearLogCommand = new RelayCommand(Log.Clear);
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);
        SeedCommand = new RelayCommand(Seed, () => CanSeed);
        RemoveSeededCommand = new RelayCommand(RemoveSeeded, () => SeededCount > 0);

        ReloadProfileList();
    }

    public LogViewModel Log { get; } = new();
    public ControlBrowserViewModel Browser { get; } = new();

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<CommandItemViewModel> Commands { get; } = [];

    /// <summary>"Auto" plus every aircraft profile: what the profile override can pin.</summary>
    public ObservableCollection<string> OverrideOptions { get; } = [];

    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand NewCommandCommand { get; }
    public RelayCommand DuplicateCommandCommand { get; }
    public RelayCommand DeleteCommandCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public AsyncCommand TestCommand { get; }
    public AsyncCommand TestSelectedCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ToggleLogCommand { get; }
    public RelayCommand SeedCommand { get; }
    public RelayCommand RemoveSeededCommand { get; }

    /// <summary>Shows only commands whose voice line, alternatives or steps contain every word.</summary>
    public string CommandFilter
    {
        get => _commandFilter;
        set
        {
            if (SetProperty(ref _commandFilter, value)) ApplyFilter();
        }
    }

    /// <summary>Hides generated commands nobody has adopted yet.</summary>
    public bool HideSeeded
    {
        get => _hideSeeded;
        set
        {
            if (SetProperty(ref _hideSeeded, value)) ApplyFilter();
        }
    }

    public int SeededCount => _profile?.Commands.Count(c => c.Seeded) ?? 0;

    public string CommandCountText
    {
        get
        {
            if (_profile is null) return "";
            var shown = Commands.Count(c => c.IsVisible);
            var text = shown == Commands.Count ? $"{Commands.Count} commands" : $"{shown} of {Commands.Count} commands";
            var seeded = SeededCount;
            return seeded > 0 ? $"{text} · {seeded} seeded" : text;
        }
    }

    /// <summary>An aircraft profile whose DCS-BIOS definitions are loaded.</summary>
    public bool CanSeed => _profile != null && SelectedProfile is { IsCommon: false } && Browser.Catalog.Count > 0;

    /// <summary>Raised when the user picks a profile override (null = Auto), so the app can remember it.</summary>
    public event Action<string?>? ProfileOverrideChanged;

    public IVoiceComRuntime? Runtime => _runtime;

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(value, _selectedProfile)) return;
            if (!ConfirmDiscardOrSave())
            {
                // Keep the current selection: tell the list to read it back.
                _ui.Post(() => OnPropertyChanged());
                return;
            }
            _selectedProfile = value;
            OnPropertyChanged();
            DeleteProfileCommand.Refresh();
            LoadSelectedProfile();
        }
    }

    public CommandItemViewModel? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (!SetProperty(ref _selectedCommand, value)) return;
            Editor = value is null ? null : new CommandEditorViewModel(value.Command, _dialogs, () => CommandChanged(value), EnterValueContext);
            DuplicateCommandCommand.Refresh();
            DeleteCommandCommand.Refresh();
            TestSelectedCommand.Refresh();
        }
    }

    public CommandEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value)) OnPropertyChanged(nameof(HasEditor));
        }
    }

    public bool HasEditor => Editor != null;
    public bool HasProfile => _profile != null;

    public string ProfileTitle => _profile is null ? "No profile selected" : $"{SelectedProfile?.Display} commands";

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            SaveCommand.Refresh();
            RevertCommand.Refresh();
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string WindowTitle => "DCS VoiceCom" + (_profile is null ? "" : $" — {SelectedProfile?.Display}{(IsDirty ? " •" : "")}");

    public int Threshold
    {
        get => _profile?.Threshold ?? Profile.DefaultThreshold;
        set
        {
            if (_profile is null || _profile.Threshold == value) return;
            _profile.Threshold = Math.Clamp(value, 50, 100);
            OnPropertyChanged();
            IsDirty = true;
        }
    }

    public int CriticalThreshold
    {
        get => _profile?.CriticalThreshold ?? Profile.DefaultCriticalThreshold;
        set
        {
            if (_profile is null || _profile.CriticalThreshold == value) return;
            _profile.CriticalThreshold = Math.Clamp(value, 50, 100);
            OnPropertyChanged();
            IsDirty = true;
        }
    }

    /// <summary>Recognizer keywords, comma separated: only words Whistle gets wrong.</summary>
    public string KeywordsText
    {
        get => _profile is null ? "" : string.Join(", ", _profile.Keywords);
        set
        {
            if (_profile is null) return;
            var keywords = value.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            if (keywords.SequenceEqual(_profile.Keywords)) return;
            _profile.Keywords = keywords;
            OnPropertyChanged();
            IsDirty = true;
        }
    }

    public bool ShowLog
    {
        get => _showLog;
        set => SetProperty(ref _showLog, value);
    }

    public string TestText
    {
        get => _testText;
        set
        {
            if (SetProperty(ref _testText, value)) TestCommand.Refresh();
        }
    }

    /// <summary>Test without sending anything to DCS: the steps are only listed.</summary>
    public bool DryRun
    {
        get => _dryRun;
        set => SetProperty(ref _dryRun, value);
    }

    public string SelectedOverride
    {
        get => _selectedOverride;
        set
        {
            value ??= AutoProfile;
            if (!SetProperty(ref _selectedOverride, value)) return;
            var pinned = value == AutoProfile ? null : value;
            ProfileOverrideChanged?.Invoke(pinned);
            if (_runtime != null) _ = RunAsync(() => _runtime.SetProfileOverrideAsync(pinned));
        }
    }

    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (SetProperty(ref _isListening, value)) OnPropertyChanged(nameof(ListeningText));
        }
    }

    public string ListeningText => IsListening ? "Listening" : "Idle";

    public string PttText
    {
        get => _pttText;
        set => SetProperty(ref _pttText, value);
    }

    public string DcsStatus
    {
        get => _dcsStatus;
        private set => SetProperty(ref _dcsStatus, value);
    }

    public bool DcsLive => _runtime?.IsLive == true;

    public string SpeechStatus
    {
        get => _speechStatus;
        private set => SetProperty(ref _speechStatus, value);
    }

    public string ActiveProfileText
    {
        get => _activeProfileText;
        private set => SetProperty(ref _activeProfileText, value);
    }

    /// <summary>Connects a (new) runtime: its events feed the log and status. Pass null to detach.</summary>
    public void AttachRuntime(IVoiceComRuntime? runtime)
    {
        if (_runtime != null)
        {
            _runtime.CommandExecuted -= OnCommandExecuted;
            _runtime.ProfileLoaded -= OnProfileLoaded;
            _runtime.ListeningChanged -= OnListeningChanged;
            _runtime.AircraftChanged -= OnAircraftChanged;
        }
        _runtime = runtime;
        if (runtime != null)
        {
            runtime.CommandExecuted += OnCommandExecuted;
            runtime.ProfileLoaded += OnProfileLoaded;
            runtime.ListeningChanged += OnListeningChanged;
            runtime.AircraftChanged += OnAircraftChanged;
            _selectedOverride = runtime.ProfileOverride ?? AutoProfile;
            OnPropertyChanged(nameof(SelectedOverride));
        }
        MarkActive();
        RefreshStatus();
    }

    /// <summary>Points the editor at other folders after the settings changed; unsaved edits are asked about first.</summary>
    public void Reconfigure(ProfileStore store, DefinitionLoader definitions)
    {
        ConfirmDiscardOrSave();
        _store = store;
        _definitions = definitions;
        ReloadProfileList();
        LoadSelectedProfile();
    }

    /// <summary>A runtime log line; safe to call from any thread.</summary>
    public void PostMessage(string message) => _ui.Post(() => Log.AddMessage(message));

    /// <summary>Called a few times a second by the window: status bar and live control values.</summary>
    public void Tick()
    {
        RefreshStatus();
        var runtime = _runtime;
        var live = runtime != null && runtime.IsLive && _selectedProfile != null &&
                   string.Equals(runtime.DcsAircraft, _selectedProfile.Name, StringComparison.OrdinalIgnoreCase);
        Browser.RefreshLive(runtime?.State, live);
    }

    /// <summary>
    /// Controls, live values and test clicks for the "Enter value" window, while DCS flies this profile's aircraft.
    /// Built when the window opens, and it looks up the runtime on every call, so a later connect/disconnect is seen.
    /// </summary>
    private EnterValueContext EnterValueContext()
    {
        return new EnterValueContext(Browser.Catalog,
            () => _runtime is { } runtime && IsLiveForSelected() ? runtime.State : null,
            (control, arg) => _runtime is { } runtime
                ? runtime.SendBiosAsync(control, arg)
                : throw new InvalidOperationException("Not connected to DCS-BIOS."));
    }

    private bool IsLiveForSelected()
    {
        var runtime = _runtime;
        return runtime != null && runtime.IsLive && _selectedProfile != null &&
               string.Equals(runtime.DcsAircraft, _selectedProfile.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Before closing the window: false when the user cancels.</summary>
    public bool ConfirmClose() => ConfirmDiscardOrSave();

    /// <summary>Re-reads the profile folder, keeping the selection by name.</summary>
    public void ReloadProfileList()
    {
        var keep = _selectedProfile?.Name;
        var names = _store.List().ToList();
        Profiles.Clear();
        foreach (var name in names) Profiles.Add(new ProfileItemViewModel(name, CountCommands(name)));
        Profiles.Add(new ProfileItemViewModel(ProfileStore.CommonProfileName, CountCommands(ProfileStore.CommonProfileName)));

        OverrideOptions.Clear();
        OverrideOptions.Add(AutoProfile);
        foreach (var name in names) OverrideOptions.Add(name);
        if (!OverrideOptions.Contains(_selectedOverride)) OverrideOptions.Add(_selectedOverride);

        MarkActive();
        if (keep != null)
        {
            _selectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(keep, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedProfile));
        }
    }

    private int CountCommands(string name)
    {
        try
        {
            return _store.TryLoad(name)?.Commands.Count ?? 0;
        }
        catch (Exception)
        {
            return 0; // a broken file still shows; opening it reports the error
        }
    }

    private void LoadSelectedProfile()
    {
        SelectedCommand = null;
        Commands.Clear();
        _profile = null;
        var item = _selectedProfile;
        if (item != null)
        {
            try
            {
                _profile = _store.TryLoad(item.Name) ?? new Profile { Aircraft = item.Name };
            }
            catch (Exception ex)
            {
                Log.Add("ERROR", $"Could not read profile {item.Name}: {ex.Message}");
            }
        }
        Browser.Catalog = LoadCatalog(item);
        RebuildCommands();
        IsDirty = false;
        NewCommandCommand.Refresh();
        SeedCommand.Refresh();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(CriticalThreshold));
        OnPropertyChanged(nameof(KeywordsText));
        SelectedCommand = Commands.FirstOrDefault();
        if (_profile != null) ReportMissingControls(_profile, Browser.Catalog);
    }

    /// <summary>Fills the command list from the profile, keeping the filter.</summary>
    private void RebuildCommands()
    {
        Commands.Clear();
        if (_profile != null)
        {
            foreach (var command in _profile.Commands) Commands.Add(new CommandItemViewModel(command, ItemToggled));
        }
        ApplyFilter();
    }

    /// <summary>A command's Enabled box in the list was clicked.</summary>
    private void ItemToggled()
    {
        IsDirty = true;
        RemoveSeededCommand.Refresh();
        OnPropertyChanged(nameof(SeededCount));
        OnPropertyChanged(nameof(CommandCountText));
    }

    private void ApplyFilter()
    {
        var words = CommandFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in Commands)
        {
            var command = item.Command;
            item.IsVisible = !(HideSeeded && command.Seeded) && words.All(w =>
                command.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                command.Phrases.Any(p => p.Contains(w, StringComparison.OrdinalIgnoreCase)) ||
                item.Summary.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        RemoveSeededCommand.Refresh();
        OnPropertyChanged(nameof(SeededCount));
        OnPropertyChanged(nameof(CommandCountText));
    }

    /// <summary>Generates commands from the DCS-BIOS definitions; the user picks panels in the seed window.</summary>
    private void Seed()
    {
        if (_profile is null || !CanSeed) return;
        Profile? common = null;
        try
        {
            common = _store.TryLoad(ProfileStore.CommonProfileName);
        }
        catch (Exception ex)
        {
            Log.Add("WARN", $"Could not read the common profile: {ex.Message}");
        }
        var result = CommandSeeder.Generate(Browser.Catalog, _profile, common);
        if (result.Candidates.Count == 0)
        {
            Log.Add("INFO", $"DCS-BIOS defines no switches or buttons for {SelectedProfile?.Display}");
            return;
        }
        var seed = new SeedViewModel(SelectedProfile!.Display, result);
        if (!_dialogs.ShowSeed(seed)) return;

        var added = CommandSeeder.Apply(_profile, seed.Picked);
        if (added == 0) return;
        RebuildCommands();
        IsDirty = true;
        UpdateCount();
        Log.Add("INFO", $"Added {added} seeded commands to {SelectedProfile.Display}, all disabled: tick the ones you want, then Save");
    }

    private void RemoveSeeded()
    {
        if (_profile is null) return;
        var count = SeededCount;
        if (count == 0 || !_dialogs.Confirm("Remove seeded commands",
                $"Remove the {count} seeded commands from {SelectedProfile?.Display}? Commands you edited or enabled stay.")) return;
        var selected = SelectedCommand?.Command;
        CommandSeeder.RemoveSeeded(_profile);
        RebuildCommands();
        IsDirty = true;
        UpdateCount();
        SelectedCommand = Commands.FirstOrDefault(c => ReferenceEquals(c.Command, selected)) ?? Commands.FirstOrDefault();
        Log.Add("INFO", $"Removed {count} seeded commands from {SelectedProfile?.Display}");
    }

    private ControlCatalog LoadCatalog(ProfileItemViewModel? item)
    {
        if (item is null || item.IsCommon || !_definitions.Exists) return ControlCatalog.Empty;
        try
        {
            return _definitions.LoadAircraft(item.Name);
        }
        catch (Exception ex)
        {
            Log.Add("ERROR", $"Could not read DCS-BIOS definitions for {item.Name}: {ex.Message}");
            return ControlCatalog.Empty;
        }
    }

    private void ReportMissingControls(Profile profile, ControlCatalog catalog)
    {
        if (catalog.Count == 0) return;
        foreach (var id in CompiledProfile.FindMissingControls(profile, catalog))
            Log.Add("WARN", $"{profile.Aircraft} uses {id}, which DCS-BIOS does not define for this aircraft");
    }

    private void NewProfile()
    {
        var existing = Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = _definitions.Exists
            ? SafeAvailableAircraft().Where(a => !existing.Contains(a)).ToList()
            : [];
        var flying = _runtime?.DcsAircraft;
        var initial = !string.IsNullOrEmpty(flying) && !flying.Equals("NONE", StringComparison.OrdinalIgnoreCase) && !existing.Contains(flying) ? flying : "";
        var name = _dialogs.Prompt("New profile", "Aircraft name as DCS-BIOS reports it (_ACFT_NAME)", initial, suggestions)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (existing.Contains(name))
        {
            SelectedProfile = Profiles.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (!ConfirmDiscardOrSave()) return;
        _store.Save(new Profile { Aircraft = name });
        Log.Add("INFO", $"Created profile {name}");
        IsDirty = false;
        _selectedProfile = null;
        ReloadProfileList();
        SelectedProfile = Profiles.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanDeleteProfile(ProfileItemViewModel? item) =>
        item != null && !item.Name.Equals(ProfileStore.CommonProfileName, StringComparison.OrdinalIgnoreCase);

    private void DeleteProfile(ProfileItemViewModel? item)
    {
        if (!CanDeleteProfile(item)) return;
        var count = item!.CommandCount == 1 ? "1 command" : $"{item.CommandCount} commands";
        if (!_dialogs.Confirm("Delete profile", $"Delete the {item.Display} profile and its {count}? This cannot be undone.")) return;
        var wasSelected = ReferenceEquals(item, _selectedProfile);
        var index = Profiles.IndexOf(item);
        _store.Delete(item.Name);
        Log.Add("INFO", $"Deleted profile {item.Name}");
        if (wasSelected)
        {
            IsDirty = false; // its unsaved edits went with it
            _selectedProfile = null;
        }
        ReloadProfileList();
        if (wasSelected) SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    private IReadOnlyList<string> SafeAvailableAircraft()
    {
        try
        {
            return _definitions.AvailableAircraft();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void NewCommand() => AddCommand(new Command { Name = "New command" });

    private void AddCommand(Command command)
    {
        if (_profile is null) return;
        _profile.Commands.Add(command);
        var item = new CommandItemViewModel(command, ItemToggled);
        Commands.Add(item);
        IsDirty = true;
        UpdateCount();
        OnPropertyChanged(nameof(CommandCountText));
        SelectedCommand = item;
    }

    private void Duplicate(CommandItemViewModel? item)
    {
        if (item is null || _profile is null) return;
        var copy = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(new Profile { Commands = [item.Command] })).Commands[0];
        copy.Name += " copy";
        AddCommand(copy);
    }

    private void Delete(CommandItemViewModel? item)
    {
        if (item is null || _profile is null) return;
        if (!_dialogs.Confirm("Delete command", $"Delete “{item.Name}” from {SelectedProfile?.Display}?")) return;
        var index = Commands.IndexOf(item);
        _profile.Commands.Remove(item.Command);
        Commands.Remove(item);
        IsDirty = true;
        UpdateCount();
        ApplyFilter();
        if (ReferenceEquals(SelectedCommand, item) || SelectedCommand is null)
            SelectedCommand = Commands.Count == 0 ? null : Commands[Math.Min(index, Commands.Count - 1)];
    }

    private void UpdateCount()
    {
        if (_selectedProfile != null && _profile != null) _selectedProfile.CommandCount = _profile.Commands.Count;
    }

    /// <summary>A control's argument button: adds a step to the selected command, or a new command when none is selected.</summary>
    private void OnArgumentPicked(ControlDefinition control, ArgumentChoice choice)
    {
        if (_profile is null) return;
        if (Editor is null)
        {
            if (choice.Press)
            {
                AddCommand(new Command
                {
                    Name = $"Press {control.Description}".Trim(),
                    Phrases = [PhraseText.Normalize($"press {control.Description}")],
                });
            }
            else
            {
                var label = ControlArguments.Suggest(control)
                    .FirstOrDefault(c => c.Arg == choice.Arg && !c.Press)?.Label ?? choice.Arg;
                AddCommand(new Command
                {
                    Name = $"{control.Description} {label}".Trim(),
                    Phrases = [PhraseText.Normalize($"{control.Description} {SpokenLabel(label)}")],
                });
            }
        }
        if (choice.Press) Editor!.AddPress(control.Identifier, choice.Arg);
        else Editor!.AddBios(control.Identifier, choice.Arg);
    }

    /// <summary>"1 DN" → "dn", "TOGGLE" → "toggle": a starting phrase the user then edits.</summary>
    private static string SpokenLabel(string label)
    {
        var parts = label.Split(' ', 2);
        return (parts.Length == 2 && int.TryParse(parts[0], out _) ? parts[1] : label).ToLowerInvariant();
    }

    private void CommandChanged(CommandItemViewModel item)
    {
        if (item.Command.Seeded)
        {
            // Edited: the command is yours now, and seeding again leaves it alone.
            item.Command.Seeded = false;
            OnPropertyChanged(nameof(SeededCount));
            OnPropertyChanged(nameof(CommandCountText));
            RemoveSeededCommand.Refresh();
        }
        item.Refresh();
        IsDirty = true;
    }

    private async Task SaveAsync()
    {
        if (_profile is null || _selectedProfile is null) return;
        _store.Save(_profile);
        IsDirty = false;
        UpdateCount();
        var problems = CompiledProfile.Compile(_profile).Problems;
        Log.Add("INFO", $"Saved {_selectedProfile.Display}" + (problems.Count > 0 ? $" with {problems.Count} problem(s)" : ""));
        foreach (var problem in problems) Log.Add("WARN", problem);
        if (_runtime != null && AffectsActiveProfile(_selectedProfile.Name)) await _runtime.ReloadProfileAsync();
    }

    private bool AffectsActiveProfile(string name) =>
        name.Equals(ProfileStore.CommonProfileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_runtime?.ActiveAircraft, name, StringComparison.OrdinalIgnoreCase);

    private void Revert()
    {
        IsDirty = false;
        LoadSelectedProfile();
    }

    /// <summary>True when it is fine to leave the current profile: nothing unsaved, saved, or discarded.</summary>
    private bool ConfirmDiscardOrSave()
    {
        if (!IsDirty || _profile is null) return true;
        var answer = _dialogs.ConfirmOrCancel("Unsaved changes", $"Save changes to {SelectedProfile?.Display}?");
        if (answer is null) return false;
        if (answer == true)
        {
            _store.Save(_profile);
            Log.Add("INFO", $"Saved {SelectedProfile?.Display}");
            if (_runtime != null && _selectedProfile != null && AffectsActiveProfile(_selectedProfile.Name))
                _ = RunAsync(() => _runtime.ReloadProfileAsync());
        }
        IsDirty = false;
        return true;
    }

    private async Task TestAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (_runtime is null)
        {
            Log.Add("ERROR", "Not running: the voice engine is stopped.");
            return;
        }
        if (_runtime.ActiveAircraft is { } active && _selectedProfile != null && !_selectedProfile.IsCommon &&
            !active.Equals(_selectedProfile.Name, StringComparison.OrdinalIgnoreCase))
            Log.Add("INFO", $"Testing against the active profile {active}, not {_selectedProfile.Name} (pin it with the profile override).");
        if (IsDirty && _selectedProfile != null && AffectsActiveProfile(_selectedProfile.Name)) await SaveAsync();
        // The outcome arrives through the runtime's CommandExecuted event, which doesn't say it was a dry run.
        _dryRunInFlight = DryRun;
        try
        {
            await _runtime.TestAsync(text, DryRun);
        }
        finally
        {
            _dryRunInFlight = false;
        }
    }

    /// <summary>"Test selected": the selected command's first phrase without spoken values, else fills the test box.</summary>
    private async Task TestSelectedAsync()
    {
        var command = SelectedCommand?.Command;
        if (command is null) return;
        var phrases = command.Phrases.SelectMany(SafeExpand).ToList();
        var plain = phrases.FirstOrDefault(p => !p.Contains('{'));
        if (plain is null)
        {
            TestText = phrases.FirstOrDefault() ?? "";
            Log.Add("INFO", "This command takes spoken values: fill them in the test box and press Test.");
            return;
        }
        TestText = plain;
        await TestAsync(plain);
    }

    private static IEnumerable<string> SafeExpand(string spec)
    {
        try
        {
            return PhraseExpander.Expand(spec);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private async Task ReloadAsync()
    {
        if (!ConfirmDiscardOrSave()) return;
        ReloadProfileList();
        LoadSelectedProfile();
        if (_runtime != null) await _runtime.ReloadProfileAsync();
    }

    private void OnCommandExecuted(CommandOutcome outcome)
    {
        var dryRun = _dryRunInFlight;
        _ui.Post(() => Log.AddOutcome(outcome, dryRun));
    }

    private void OnProfileLoaded(CompiledProfile profile) => _ui.Post(() =>
    {
        MarkActive();
        RefreshStatus();
    });

    private void OnListeningChanged(bool listening) => _ui.Post(() =>
    {
        IsListening = listening;
        if (listening) Log.Add("PTT", "listening");
    });

    private void OnAircraftChanged(string aircraft) => _ui.Post(RefreshStatus);

    private void MarkActive()
    {
        var active = _runtime?.ActiveAircraft;
        foreach (var p in Profiles) p.IsActive = active != null && p.Name.Equals(active, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshStatus()
    {
        var runtime = _runtime;
        if (runtime is null)
        {
            DcsStatus = "DCS-BIOS · not running";
            SpeechStatus = "Speech · not running";
            ActiveProfileText = "";
        }
        else
        {
            var aircraft = runtime.DcsAircraft;
            DcsStatus = runtime.IsLive
                ? $"DCS-BIOS · {(aircraft.Length == 0 || aircraft.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? "no aircraft" : aircraft)}"
                : runtime.FrameCount == 0 ? "DCS-BIOS · waiting for DCS" : "DCS-BIOS · no data";
            SpeechStatus = runtime.SpeechReady ? "Speech · ready" : "Speech · offline";
            ActiveProfileText = runtime.ActiveAircraft is { } active
                ? $"Profile · {active}{(runtime.ProfileOverride != null ? " (pinned)" : "")}"
                : "Profile · none";
            IsListening = runtime.IsListening;
        }
        OnPropertyChanged(nameof(DcsLive));
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ReportError(Exception ex) => _ui.Post(() => Log.Add("ERROR", ex.Message));
}
