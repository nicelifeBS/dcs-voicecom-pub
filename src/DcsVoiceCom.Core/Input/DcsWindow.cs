using DcsVoiceCom.Engine;

namespace DcsVoiceCom.Input;

/// <summary>
/// Keys are only pressed while DCS is the active window. Key presses go to whatever window has focus, so without
/// this a profile's key steps could type into a browser, a chat or the Windows Run box.
/// </summary>
public static class DcsWindow
{
    /// <summary>True for the DCS game process (DCS.exe), by process name without ".exe".</summary>
    public static bool IsDcsProcess(string? processName) =>
        string.Equals(processName?.Trim(), "DCS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws a <see cref="StepException"/> (the command fails) unless <paramref name="processName"/> is DCS.</summary>
    public static void EnsureActive(string? processName, KeyChord chord)
    {
        if (IsDcsProcess(processName)) return;
        var active = string.IsNullOrWhiteSpace(processName) ? "no window" : processName.Trim();
        throw new StepException($"Key {chord} not sent: DCS is not the active window ({active} is)");
    }
}
