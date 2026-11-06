using DcsVoiceCom.Config;
using DcsVoiceCom.Input;
using DcsVoiceCom.Runtime;
using Vortice.DirectInput;
using static DcsVoiceCom.Windows.NativeMethods;

namespace DcsVoiceCom.Windows;

public sealed record GameController(Guid InstanceGuid, string Name, string Product);

/// <summary>
/// Reads a HOTAS/joystick button through DirectInput (background, non-exclusive, so DCS keeps its
/// own access) and raises Pressed/Released. Polls at 100 Hz on a background thread.
/// </summary>
public sealed class DirectInputPushToTalk : IPushToTalk
{
    private readonly PttSettings _settings;
    private readonly nint _window;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public DirectInputPushToTalk(PttSettings settings, nint window = 0, Action<string>? log = null)
    {
        _settings = settings;
        _window = window;
        _log = log ?? (_ => { });
    }

    public event Action? Pressed;
    public event Action? Released;

    public static IReadOnlyList<GameController> ListControllers()
    {
        using var input = DInput.DirectInput8Create();
        return input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .Select(d => new GameController(d.InstanceGuid, d.InstanceName.TrimEnd('\0'), d.ProductName.TrimEnd('\0')))
            .ToList();
    }

    /// <summary>
    /// Waits for a button press on any controller, for binding push-to-talk in settings. Buttons already down
    /// (a rotary or toggle switch's position) are ignored until released and pressed again.
    /// </summary>
    public static (GameController Controller, int Button)? DetectButton(TimeSpan timeout, nint window = 0, CancellationToken ct = default)
    {
        using var input = DInput.DirectInput8Create();
        var devices = new List<(GameController Controller, IDirectInputDevice8 Device)>();
        try
        {
            foreach (var instance in input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                var device = Open(input, instance.InstanceGuid, window);
                devices.Add((new GameController(instance.InstanceGuid, instance.InstanceName.TrimEnd('\0'), instance.ProductName.TrimEnd('\0')), device));
            }
            var detector = new ButtonPressDetector();
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                for (var i = 0; i < devices.Count; i++)
                {
                    var (controller, device) = devices[i];
                    // Poll first: a device's state reads all-up until its first poll.
                    if (device.Poll().Failure) continue;
                    if (detector.Update(i, device.GetCurrentJoystickState().Buttons, DateTime.UtcNow) is { } button)
                        return (controller, button);
                }
                Thread.Sleep(10);
            }
            return null;
        }
        finally
        {
            foreach (var (_, device) in devices) device.Dispose();
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PTT poll" };
        _thread.Start();
    }

    private void Run()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var input = DInput.DirectInput8Create();
                var instance = FindDevice(input);
                if (instance is null)
                {
                    _log($"PTT controller \"{_settings.DeviceName ?? _settings.DeviceGuid}\" not found; retrying");
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    continue;
                }
                using var device = Open(input, instance.InstanceGuid, _window);
                _log($"PTT: {instance.InstanceName.TrimEnd('\0')} button {_settings.Button + 1}");
                Poll(device, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Unplugged or lost: reacquire after a pause.
                _log($"PTT error: {ex.Message}; retrying");
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }

    private void Poll(IDirectInputDevice8 device, CancellationToken token)
    {
        var down = false;
        while (!token.IsCancellationRequested)
        {
            device.Poll();
            var buttons = device.GetCurrentJoystickState().Buttons;
            var now = _settings.Button < buttons.Length && buttons[_settings.Button];
            if (now != down)
            {
                down = now;
                (down ? Pressed : Released)?.Invoke();
            }
            token.WaitHandle.WaitOne(10);
        }
        if (down) Released?.Invoke();
    }

    private DeviceInstance? FindDevice(IDirectInput8 input)
    {
        var devices = input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        if (Guid.TryParse(_settings.DeviceGuid, out var guid))
        {
            var byGuid = devices.FirstOrDefault(d => d.InstanceGuid == guid);
            if (byGuid != null) return byGuid;
        }
        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            return devices.FirstOrDefault(d => d.InstanceName.Contains(_settings.DeviceName, StringComparison.OrdinalIgnoreCase)
                                            || d.ProductName.Contains(_settings.DeviceName, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static IDirectInputDevice8 Open(IDirectInput8 input, Guid instance, nint window)
    {
        var device = input.CreateDevice(instance);
        device.SetDataFormat<RawJoystickState>().CheckError();

        // Background + non-exclusive needs a top-level window handle; try the given one, then fallbacks.
        var level = CooperativeLevel.Background | CooperativeLevel.NonExclusive;
        var set = false;
        foreach (var hwnd in new[] { window, GetConsoleWindow(), GetDesktopWindow() }.Where(h => h != 0).Distinct())
        {
            if (device.SetCooperativeLevel(hwnd, level).Success)
            {
                set = true;
                break;
            }
        }
        if (!set) throw new InvalidOperationException("DirectInput refused the cooperative level");
        device.Acquire().CheckError();
        return device;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }
}
