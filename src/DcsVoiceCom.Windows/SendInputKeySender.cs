using System.Runtime.InteropServices;
using DcsVoiceCom.Input;
using static DcsVoiceCom.Windows.NativeMethods;

namespace DcsVoiceCom.Windows;

/// <summary>
/// Presses key chords with SendInput using scan codes, so DCS (which reads keys through DirectInput)
/// sees them and left/right modifiers stay distinct. DCS must have focus.
/// </summary>
public sealed class SendInputKeySender : IKeySender
{
    /// <summary>Gap between modifier and key events; some games miss modifiers sent in the same instant.</summary>
    public TimeSpan ModifierGap { get; init; } = TimeSpan.FromMilliseconds(15);

    public async Task SendAsync(KeyChord chord, TimeSpan hold, CancellationToken cancellationToken = default)
    {
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
