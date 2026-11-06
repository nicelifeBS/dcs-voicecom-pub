using System.Windows;
using System.Windows.Threading;
using DcsVoiceCom.Config;
using DcsVoiceCom.ViewModels;
using DcsVoiceCom.Windows;

namespace DcsVoiceCom.App;

public sealed class WpfDialogs(Func<Window?> owner) : IDialogs
{
    public bool Confirm(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNo) == MessageBoxResult.Yes;

    public bool? ConfirmOrCancel(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNoCancel) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? Prompt(string title, string label, string initial = "", IReadOnlyList<string>? suggestions = null)
    {
        var window = new PromptWindow(title, label, initial, suggestions) { Owner = OwnerWindow() };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public bool ShowSeed(SeedViewModel seed) => new SeedWindow(seed) { Owner = OwnerWindow() }.ShowDialog() == true;

    public bool ShowEnterValue(EnterValueEditorViewModel editor) => new EnterValueWindow(editor) { Owner = OwnerWindow() }.ShowDialog() == true;

    private MessageBoxResult Show(string title, string message, MessageBoxButton buttons) =>
        MessageWindow.Show(OwnerWindow(), title, message, buttons);

    private Window? OwnerWindow()
    {
        var o = owner();
        return o is { IsVisible: true } ? o : null;
    }
}

public sealed class WpfUiThread(Dispatcher dispatcher) : IUiThread
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>Binds push-to-talk by waiting for any joystick button through DirectInput.</summary>
public sealed class DirectInputBinder(Func<nint> window) : IPttBinder
{
    public async Task<PttSettings?> DetectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var hwnd = window();
        var hit = await Task.Run(() => DirectInputPushToTalk.DetectButton(timeout, hwnd, ct), ct);
        return hit is { } h
            ? new PttSettings { DeviceGuid = h.Controller.InstanceGuid.ToString(), DeviceName = h.Controller.Name, Button = h.Button }
            : null;
    }
}
