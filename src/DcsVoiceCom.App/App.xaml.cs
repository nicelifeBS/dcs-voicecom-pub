using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DcsVoiceCom.Config;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Runtime;
using DcsVoiceCom.ViewModels;
using DcsVoiceCom.Windows;

namespace DcsVoiceCom.App;

/// <summary>
/// Starts the window, the tray icon and the voice runtime (DCS-BIOS link, speech, push-to-talk).
/// Saving settings restarts the runtime; closing the window or Quit in the tray menu exits.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "DcsVoiceCom.SingleInstance";

    private Mutex? _instance;
    private AppSettings _settings = new();
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private HelpWindow? _help;
    private HelpViewModel? _helpModel;
    private VoiceComRuntime? _runtime;
    private bool _quitting;

    public App()
    {
        // A windowed app has no console: without this, a startup failure just ends the process silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
    }

    /// <summary>Where a crash is written: %LOCALAPPDATA%\DcsVoiceCom\crash.log.</summary>
    public static string CrashLogPath => Path.Combine(AppSettings.DefaultDirectory, "crash.log");

    private static void ReportCrash(Exception? ex)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} DCS VoiceCom crashed:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(AppSettings.DefaultDirectory);
            File.AppendAllText(CrashLogPath, text);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        MessageBox.Show($"DCS VoiceCom stopped because of an error:\n\n{ex?.Message}\n\nDetails: {CrashLogPath}",
            "DCS VoiceCom", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            await StartAsync(e);
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            Shutdown(1);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            MessageWindow.Show(null, "DCS VoiceCom", "DCS VoiceCom is already running (see the tray icon).", MessageBoxButton.OK);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();
        if (!File.Exists(AppSettings.DefaultPath)) _settings.Save();

        var dialogs = new WpfDialogs(() => _window);
        _vm = new MainViewModel(new ProfileStore(_settings.ProfilesPath), new DefinitionLoader(_settings.DcsBiosJsonPath),
            dialogs, new WpfUiThread(Dispatcher), SettingsViewModel.Describe(_settings.Ptt));
        _vm.ProfileOverrideChanged += pinned => SaveSettings(_settings with { ProfileOverride = pinned });
        _vm.Log.Add("INFO", $"Settings: {AppSettings.DefaultPath}");

        _window = new MainWindow(_vm) { OpenSettings = () => _ = OpenSettingsAsync(), OpenHelp = OpenHelp };
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized) _window.Hide(); // to the tray
        };
        _window.Closed += (_, _) => _ = QuitAsync(windowClosed: true);
        _tray = new TrayIcon(_vm, ShowWindow, () => _ = QuitAsync(windowClosed: false));
        _window.Show();

        await StartRuntimeAsync();
    }

    private nint WindowHandle => _window is null ? 0 : new WindowInteropHelper(_window).Handle;

    private async Task StartRuntimeAsync()
    {
        if (_vm is null) return;
        var hasPtt = _settings.Ptt.DeviceGuid != null || _settings.Ptt.DeviceName != null;
        if (!hasPtt) _vm.Log.Add("WARN", "No push-to-talk button bound: Settings → Rebind. Typed tests still work.");
        var ptt = hasPtt ? new DirectInputPushToTalk(_settings.Ptt, WindowHandle, _vm.PostMessage) : null;
        _runtime = new VoiceComRuntime(_settings, new SendInputKeySender(), new WavCuePlayer(_settings.Cues), ptt, _vm.PostMessage,
            new WasapiMicrophoneProvider());
        _vm.AttachRuntime(_runtime);
        try
        {
            await _runtime.StartAsync();
        }
        catch (Exception ex)
        {
            _vm.Log.Add("ERROR", $"Start failed: {ex.Message}");
        }
    }

    private async Task StopRuntimeAsync()
    {
        var runtime = _runtime;
        _runtime = null;
        _vm?.AttachRuntime(null);
        if (runtime is null) return;
        try
        {
            await runtime.DisposeAsync();
        }
        catch (Exception ex)
        {
            _vm?.Log.Add("ERROR", $"Stopping: {ex.Message}");
        }
    }

    /// <summary>Shows the Help window, or brings it forward. The topic shown last is kept until the app quits.</summary>
    private void OpenHelp()
    {
        if (_window is null) return;
        if (_help is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _helpModel ??= new HelpViewModel(HelpContent.Pages(), HelpContent.Version);
        _help = new HelpWindow(_helpModel) { Owner = _window };
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    private async Task OpenSettingsAsync()
    {
        if (_vm is null || _window is null) return;
        IReadOnlyList<string> microphones = [];
        if (_runtime is { } runtime)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                microphones = await runtime.ListInputDevicesAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                _vm.Log.Add("WARN", $"Could not list microphones: {ex.Message}");
            }
        }

        var dialog = new SettingsWindow(new SettingsViewModel(_settings, new DirectInputBinder(() => WindowHandle), microphones)) { Owner = _window };
        if (dialog.ShowDialog() != true || dialog.Result is not { } updated) return;

        var pathsChanged = updated.ProfilesPath != _settings.ProfilesPath || updated.DcsBiosJsonPath != _settings.DcsBiosJsonPath;
        SaveSettings(updated);
        _vm.PttText = SettingsViewModel.Describe(updated.Ptt);
        if (pathsChanged) _vm.Reconfigure(new ProfileStore(updated.ProfilesPath), new DefinitionLoader(updated.DcsBiosJsonPath));
        _vm.Log.Add("INFO", "Settings saved; restarting the voice engine");
        await StopRuntimeAsync();
        await StartRuntimeAsync();
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            _vm?.Log.Add("ERROR", $"Could not save settings: {ex.Message}");
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <param name="windowClosed">True when the window has already closed (it asked about unsaved changes itself).</param>
    private async Task QuitAsync(bool windowClosed)
    {
        if (_quitting) return;
        if (!windowClosed && _window != null)
        {
            // Quit from the tray: ask about unsaved changes with the window in view, then close it.
            if (_vm?.IsDirty == true) ShowWindow();
            if (_vm?.ConfirmClose() == false) return;
            _quitting = true;
            _window.AllowClose = true;
            _window.Close();
        }
        _quitting = true;
        _tray?.Dispose();
        await StopRuntimeAsync();
        _instance?.ReleaseMutex();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _vm?.Log.Add("ERROR", e.Exception.Message);
        e.Handled = true;
    }
}
