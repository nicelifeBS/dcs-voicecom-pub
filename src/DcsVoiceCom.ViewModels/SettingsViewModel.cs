using System.Collections.ObjectModel;
using System.Globalization;
using DcsVoiceCom.Config;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.DcsHook;

namespace DcsVoiceCom.ViewModels;

/// <summary>The Settings dialog: a working copy of <see cref="AppSettings"/>; <see cref="Build"/> returns the edited settings.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Languages = ["en", "de", "fr", "es", "it", "nl", "pl"];

    private readonly AppSettings _original;
    private readonly IPttBinder? _binder;
    private PttSettings _ptt;
    private bool _binding;
    private string _bindStatus = "";
    private string? _error;
    private readonly DcsHookInstaller? _hook;
    private string _hookStatus = "";

    public SettingsViewModel(AppSettings settings, IPttBinder? binder = null, IReadOnlyList<string>? microphones = null,
        DcsHookInstaller? hook = null)
    {
        _hook = hook;
        _original = settings;
        _binder = binder;
        _ptt = settings.Ptt;
        Language = settings.Language;
        DcsBiosJsonPath = settings.DcsBiosJsonPath;
        ProfilesPath = settings.ProfilesPath;
        InputHost = settings.DcsBios.InputHost;
        InputPort = settings.DcsBios.InputPort.ToString(CultureInfo.InvariantCulture);
        ExportGroup = settings.DcsBios.ExportMulticastGroup;
        ExportPort = settings.DcsBios.ExportPort.ToString(CultureInfo.InvariantCulture);
        ModelDirectory = settings.Speech.ModelDirectory ?? "";
        Microphone = settings.Speech.Device ?? "";
        KeyHoldMs = settings.KeyHoldMs.ToString(CultureInfo.InvariantCulture);
        CuesEnabled = settings.Cues.Enabled;
        _allMicrophones = (microphones ?? []).ToList();
        if (Microphone.Length > 0 && !_allMicrophones.Contains(Microphone, StringComparer.OrdinalIgnoreCase))
            _allMicrophones.Add(Microphone); // typed by hand, or the microphone is unplugged
        FilterMicrophones();
        BindCommand = new AsyncCommand(BindAsync, ex => BindStatus = ex.Message, () => _binder != null && !_binding);
        ClearBindingCommand = new RelayCommand(() =>
        {
            _ptt = new PttSettings();
            OnPropertyChanged(nameof(PttText));
        });
        InstallHookCommand = new RelayCommand(() => RunHook(h => h.Install()), () => CanInstallHook);
        RemoveHookCommand = new RelayCommand(() => RunHook(h => h.Remove()), () => CanRemoveHook);
        RefreshHook();
    }

    public AsyncCommand BindCommand { get; }
    public RelayCommand ClearBindingCommand { get; }
    public RelayCommand InstallHookCommand { get; }
    public RelayCommand RemoveHookCommand { get; }

    /// <summary>Whether the DCS hook (for server profiles) is in Saved Games\DCS…\Scripts\Hooks.</summary>
    public string HookStatus
    {
        get => _hookStatus;
        private set => SetProperty(ref _hookStatus, value);
    }

    private HookStatus? _hookState;

    public bool CanInstallHook => _hookState is DcsHook.HookStatus.NotInstalled or DcsHook.HookStatus.OutOfDate;
    public bool CanRemoveHook => _hookState is DcsHook.HookStatus.Installed or DcsHook.HookStatus.OutOfDate;

    private void RefreshHook()
    {
        try
        {
            _hookState = _hook?.Status();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _hookState = null;
            HookStatus = $"Can't read the DCS folder: {ex.Message}";
            return;
        }
        finally
        {
            InstallHookCommand?.Refresh();
            RemoveHookCommand?.Refresh();
        }
        var text = _hookState switch
        {
            DcsHook.HookStatus.NoDcs => "No DCS folder found in Saved Games. Start DCS once, then come back.",
            DcsHook.HookStatus.NotInstalled => "Not installed. Server profiles need it.",
            DcsHook.HookStatus.OutOfDate => "Installed, but out of date. Install again.",
            DcsHook.HookStatus.Installed => "Installed. Restart DCS if it's running.",
            _ => "",
        };
        HookStatus = text;
    }

    private void RunHook(Action<DcsHookInstaller> action)
    {
        if (_hook is null) return;
        try
        {
            action(_hook);
            RefreshHook();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HookStatus = $"Failed: {ex.Message}";
        }
    }

    /// <summary>The list entry for the Windows default input (an empty setting).</summary>
    public const string DefaultMicrophone = "Windows default input";

    private readonly List<string> _allMicrophones;

    /// <summary>The microphones Windows reported that match <see cref="MicrophoneSearch"/>, after the default entry.</summary>
    public ObservableCollection<string> Microphones { get; } = [];

    private string _microphoneSearch = "";

    /// <summary>Words that must all appear in a microphone's name, in any order.</summary>
    public string MicrophoneSearch
    {
        get => _microphoneSearch;
        set
        {
            if (SetProperty(ref _microphoneSearch, value)) FilterMicrophones();
        }
    }

    /// <summary>The chosen list entry; <see cref="DefaultMicrophone"/> for an empty setting.</summary>
    public string? SelectedMicrophone
    {
        get => Microphone.Length == 0 ? DefaultMicrophone : Microphones.FirstOrDefault(m => m.Equals(Microphone, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return; // the filter dropped the selection; keep the setting
            Microphone = value == DefaultMicrophone ? "" : value;
            OnPropertyChanged();
        }
    }

    public string MicrophoneText => Microphone.Length == 0 ? DefaultMicrophone : Microphone;

    private void FilterMicrophones()
    {
        var words = MicrophoneSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Microphones.Clear();
        if (words.Length == 0) Microphones.Add(DefaultMicrophone);
        foreach (var name in _allMicrophones.Where(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))))
            Microphones.Add(name);
        OnPropertyChanged(nameof(SelectedMicrophone));
    }

    private string _language = "";

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }
    private string _dcsBiosJsonPath = "";

    public string DcsBiosJsonPath
    {
        get => _dcsBiosJsonPath;
        set => SetProperty(ref _dcsBiosJsonPath, value);
    }
    private string _profilesPath = "";

    public string ProfilesPath
    {
        get => _profilesPath;
        set => SetProperty(ref _profilesPath, value);
    }
    private string _inputHost = "";

    public string InputHost
    {
        get => _inputHost;
        set => SetProperty(ref _inputHost, value);
    }
    private string _inputPort = "";

    public string InputPort
    {
        get => _inputPort;
        set => SetProperty(ref _inputPort, value);
    }
    private string _exportGroup = "";

    public string ExportGroup
    {
        get => _exportGroup;
        set => SetProperty(ref _exportGroup, value);
    }
    private string _exportPort = "";

    public string ExportPort
    {
        get => _exportPort;
        set => SetProperty(ref _exportPort, value);
    }
    private string _modelDirectory = "";

    public string ModelDirectory
    {
        get => _modelDirectory;
        set => SetProperty(ref _modelDirectory, value);
    }
    private string _microphone = "";

    /// <summary>The setting: a microphone name, part of one, or empty for the Windows default.</summary>
    public string Microphone
    {
        get => _microphone;
        set
        {
            if (SetProperty(ref _microphone, value)) OnPropertyChanged(nameof(MicrophoneText));
        }
    }
    private string _keyHoldMs = "";

    public string KeyHoldMs
    {
        get => _keyHoldMs;
        set => SetProperty(ref _keyHoldMs, value);
    }
    private bool _cuesEnabled = false;

    public bool CuesEnabled
    {
        get => _cuesEnabled;
        set => SetProperty(ref _cuesEnabled, value);
    }

    public string PttText => Describe(_ptt);

    public string BindStatus
    {
        get => _bindStatus;
        private set => SetProperty(ref _bindStatus, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public static string Describe(PttSettings ptt) =>
        ptt.DeviceGuid is null && ptt.DeviceName is null ? "not bound" : $"{ptt.DeviceName ?? ptt.DeviceGuid} · button {ptt.Button + 1}";

    /// <summary>The edited settings, or null (with <see cref="Error"/>) when a number doesn't parse.</summary>
    public AppSettings? Build()
    {
        if (!TryPort(InputPort, "Command port", out var inputPort) || !TryPort(ExportPort, "Export port", out var exportPort)) return null;
        if (!int.TryParse(KeyHoldMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hold) || hold is < 10 or > 2000)
        {
            Error = "Key hold must be 10–2000 ms.";
            return null;
        }
        Error = null;
        return _original with
        {
            Language = Language.Trim().ToLowerInvariant(),
            DcsBiosJsonPath = DcsBiosJsonPath.Trim(),
            ProfilesPath = ProfilesPath.Trim(),
            DcsBios = _original.DcsBios with
            {
                InputHost = InputHost.Trim(),
                InputPort = inputPort,
                ExportMulticastGroup = ExportGroup.Trim(),
                ExportPort = exportPort,
            },
            Speech = _original.Speech with
            {
                ModelDirectory = NullIfEmpty(ModelDirectory),
                Device = NullIfEmpty(Microphone),
            },
            Ptt = _ptt,
            KeyHoldMs = hold,
            Cues = _original.Cues with { Enabled = CuesEnabled },
        };
    }

    private async Task BindAsync()
    {
        if (_binder is null) return;
        _binding = true;
        BindCommand.Refresh();
        BindStatus = "Press the push-to-talk button (15 s)…";
        try
        {
            var detected = await _binder.DetectAsync(TimeSpan.FromSeconds(15));
            if (detected is null)
            {
                BindStatus = "No button pressed.";
                return;
            }
            _ptt = detected;
            BindStatus = "Bound.";
            OnPropertyChanged(nameof(PttText));
        }
        finally
        {
            _binding = false;
            BindCommand.Refresh();
        }
    }

    private bool TryPort(string text, string name, out int port)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536) return true;
        Error = $"{name} must be a number from 1 to 65535.";
        return false;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
