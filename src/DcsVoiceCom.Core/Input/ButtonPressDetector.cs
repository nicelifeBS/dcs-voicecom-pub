namespace DcsVoiceCom.Input;

/// <summary>
/// Picks the button the user presses while binding, out of controllers that may already hold buttons down:
/// a rotary or toggle switch keeps its position's button on, and must not win. During a settling period every
/// button seen down is treated as held; after it, only a button going from up to down counts. A held button
/// counts once it was released and pressed again.
/// </summary>
public sealed class ButtonPressDetector
{
    private readonly TimeSpan _settle;
    private readonly Dictionary<int, bool[]> _last = new();
    private DateTime? _started;

    public ButtonPressDetector(TimeSpan? settle = null) => _settle = settle ?? TimeSpan.FromMilliseconds(300);

    /// <summary>Feeds one poll of a controller; returns the newly pressed button, or null.</summary>
    public int? Update(int controller, IReadOnlyList<bool> buttons, DateTime now)
    {
        _started ??= now;
        var settling = now - _started.Value < _settle;
        if (!_last.TryGetValue(controller, out var last) || last.Length != buttons.Count)
        {
            // First poll of this controller: whatever is down now is a switch position, not a press.
            _last[controller] = buttons.ToArray();
            return null;
        }
        int? pressed = null;
        for (var b = 0; b < buttons.Count; b++)
        {
            if (!settling && pressed is null && buttons[b] && !last[b]) pressed = b;
            last[b] = buttons[b];
        }
        return pressed;
    }
}
