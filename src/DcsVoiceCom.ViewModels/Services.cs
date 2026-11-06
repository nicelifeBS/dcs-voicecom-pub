namespace DcsVoiceCom.ViewModels;

/// <summary>Dialogs the view models ask the window layer for.</summary>
public interface IDialogs
{
    /// <summary>A yes/no question; true for yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Yes / no / cancel: true, false, or null for cancel.</summary>
    bool? ConfirmOrCancel(string title, string message);

    /// <summary>Asks for one line of text; null when cancelled. <paramref name="suggestions"/> fill a drop-down.</summary>
    string? Prompt(string title, string label, string initial = "", IReadOnlyList<string>? suggestions = null);

    /// <summary>Shows the seed window; true when the user chose to add the ticked panels.</summary>
    bool ShowSeed(SeedViewModel seed);

    /// <summary>Shows the "Enter value" window; true on OK.</summary>
    bool ShowEnterValue(EnterValueEditorViewModel editor);
}

/// <summary>Runs work on the UI thread; runtime events arrive on background threads.</summary>
public interface IUiThread
{
    void Post(Action action);
}

/// <summary>Runs actions inline: for tests, and before a window exists.</summary>
public sealed class InlineUiThread : IUiThread
{
    public static InlineUiThread Instance { get; } = new();

    public void Post(Action action) => action();
}

/// <summary>Waits for a joystick button press to bind push-to-talk.</summary>
public interface IPttBinder
{
    /// <summary>The pressed button's binding, or null when nothing was pressed in time.</summary>
    Task<Config.PttSettings?> DetectAsync(TimeSpan timeout, CancellationToken ct = default);
}
