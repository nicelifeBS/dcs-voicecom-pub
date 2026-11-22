using System.Diagnostics;
using System.Runtime.InteropServices;
using DcsVoiceCom.Input;
using static DcsVoiceCom.Windows.NativeMethods;

namespace DcsVoiceCom.Windows;

/// <summary>
/// Presses key chords with SendInput using scan codes, so DCS (which reads keys through DirectInput)
/// sees them and left/right modifiers stay distinct. Keys are only sent while DCS is the active window
/// (see <see cref="DcsWindow"/>); otherwise the step fails.
/// </summary>
public sealed class SendInputKeySender : IKeySender
{
    /// <summary>Gap between modifier and key events; some games miss modifiers sent in the same instant.</summary>
    public TimeSpan ModifierGap { get; init; } = TimeSpan.FromMilliseconds(15);

    public async Task SendAsync(KeyChord chord, TimeSpan hold, CancellationToken cancellationToken = default)
    {
        DcsWindow.EnsureActive(ForegroundProcessName(), chord);
        var modifiers = chord.Modifiers.ToList();
        var keys = chord.MainKeys.ToList();
        try
        {
            Send(modifiers, up: false);
            if (modifiers.Count > 0) await Task.Delay(ModifierGap, cancellationToken).ConfigureAwait(false);
            Send(keys, up: false);
            await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always release, even when cancelled, so no key stays stuck down in DCS.
            Send(keys, up: true);
            if (modifiers.Count > 0) Thread.Sleep(ModifierGap);
            modifiers.Reverse();
            Send(modifiers, up: true);
        }
    }

    /// <summary>The process name of the window that has focus, or null when there is none.</summary>
    private static string? ForegroundProcessName()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // the process just exited
        }
    }

    private static void Send(IReadOnlyList<ScanKey> keys, bool up)
    {
        if (keys.Count == 0) return;
        var inputs = keys.Select(k => new NativeInput
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = k.ScanCode,
                    Flags = KeyEventScanCode | (k.Extended ? KeyEventExtendedKey : 0) | (up ? KeyEventKeyUp : 0),
                },
            },
        }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastPInvokeError()})");
    }
}
