namespace DcsVoiceCom.Input;

/// <summary>A key by hardware scan code; <see cref="Extended"/> marks E0-prefixed keys (right Ctrl/Alt, arrows, ...).</summary>
public readonly record struct ScanKey(string Name, ushort ScanCode, bool Extended = false, bool IsModifier = false);

/// <summary>
/// A key combination such as "RCtrl+RShift+G". Modifiers are pressed in order, the key is tapped,
/// then modifiers are released in reverse. Left and right modifiers are distinct scan codes, so DCS
/// can tell LCtrl from RCtrl.
/// </summary>
public sealed record KeyChord(IReadOnlyList<ScanKey> Keys)
{
    public IEnumerable<ScanKey> Modifiers => Keys.Where(k => k.IsModifier);
    public IEnumerable<ScanKey> MainKeys => Keys.Where(k => !k.IsModifier);

    public static KeyChord Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var keys = new List<ScanKey>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            // "Ctrl++" style: an empty part followed by another empty part means the plus key.
            if (part.Length == 0 && i + 1 < parts.Length && parts[i + 1].Length == 0)
            {
                part = "NumPlus";
                i++;
            }
            if (!KeyTable.TryGet(part, out var key)) throw new FormatException($"Unknown key \"{part}\" in \"{text}\"");
            keys.Add(key);
        }
        if (keys.Count == 0) throw new FormatException("Empty key chord");
        return new KeyChord(keys);
    }

    public override string ToString() => string.Join('+', Keys.Select(k => k.Name));
}

/// <summary>Sends key chords to the focused window (DCS).</summary>
public interface IKeySender
{
    Task SendAsync(KeyChord chord, TimeSpan hold, CancellationToken cancellationToken = default);
}

/// <summary>US-layout set 1 scan codes. DCS binds by scan code, so these match DCS key names on any layout.</summary>
public static class KeyTable
{
    private static readonly Dictionary<string, ScanKey> Keys = Build();

    public static bool TryGet(string name, out ScanKey key) => Keys.TryGetValue(name, out key);

    public static IEnumerable<string> Names => Keys.Keys;

    private static Dictionary<string, ScanKey> Build()
    {
        var k = new Dictionary<string, ScanKey>(StringComparer.OrdinalIgnoreCase);
        void Add(ushort code, bool extended, bool modifier, params string[] names)
        {
            foreach (var name in names) k[name] = new ScanKey(names[0], code, extended, modifier);
        }

        Add(0x1D, false, true, "LCtrl", "LControl", "Ctrl", "Control");
        Add(0x1D, true, true, "RCtrl", "RControl");
        Add(0x2A, false, true, "LShift", "Shift");
        Add(0x36, false, true, "RShift");
        Add(0x38, false, true, "LAlt", "Alt");
        Add(0x38, true, true, "RAlt", "AltGr");
        Add(0x5B, true, true, "LWin", "Win");
        Add(0x5C, true, true, "RWin");

        const string row1 = "QWERTYUIOP", row2 = "ASDFGHJKL", row3 = "ZXCVBNM";
        for (var i = 0; i < row1.Length; i++) Add((ushort)(0x10 + i), false, false, row1[i].ToString());
        for (var i = 0; i < row2.Length; i++) Add((ushort)(0x1E + i), false, false, row2[i].ToString());
        for (var i = 0; i < row3.Length; i++) Add((ushort)(0x2C + i), false, false, row3[i].ToString());
        for (var d = 1; d <= 9; d++) Add((ushort)(0x01 + d), false, false, d.ToString(), "D" + d);
        Add(0x0B, false, false, "0", "D0");

        ushort[] fkeys = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (var i = 0; i < fkeys.Length; i++) Add(fkeys[i], false, false, "F" + (i + 1));

        Add(0x01, false, false, "Esc", "Escape");
        Add(0x0C, false, false, "Minus", "-");
        Add(0x0D, false, false, "Equals", "=");
        Add(0x0E, false, false, "Backspace", "Back");
        Add(0x0F, false, false, "Tab");
        Add(0x1A, false, false, "LBracket", "[");
        Add(0x1B, false, false, "RBracket", "]");
        Add(0x1C, false, false, "Enter", "Return");
        Add(0x27, false, false, "Semicolon", ";");
        Add(0x28, false, false, "Apostrophe", "'");
        Add(0x29, false, false, "Grave", "`");
        Add(0x2B, false, false, "Backslash", "\\");
        Add(0x33, false, false, "Comma", ",");
        Add(0x34, false, false, "Period", ".");
        Add(0x35, false, false, "Slash", "/");
        Add(0x39, false, false, "Space");
        Add(0x3A, false, false, "CapsLock");
        Add(0x46, false, false, "ScrollLock");

        Add(0x52, true, false, "Insert", "Ins");
        Add(0x53, true, false, "Delete", "Del");
        Add(0x47, true, false, "Home");
        Add(0x4F, true, false, "End");
        Add(0x49, true, false, "PageUp", "PgUp");
        Add(0x51, true, false, "PageDown", "PgDn");
        Add(0x48, true, false, "Up");
        Add(0x50, true, false, "Down");
        Add(0x4B, true, false, "Left");
        Add(0x4D, true, false, "Right");

        Add(0x52, false, false, "Num0");
        Add(0x4F, false, false, "Num1");
        Add(0x50, false, false, "Num2");
        Add(0x51, false, false, "Num3");
        Add(0x4B, false, false, "Num4");
        Add(0x4C, false, false, "Num5");
        Add(0x4D, false, false, "Num6");
        Add(0x47, false, false, "Num7");
        Add(0x48, false, false, "Num8");
        Add(0x49, false, false, "Num9");
        Add(0x53, false, false, "NumDecimal", "NumPeriod");
        Add(0x37, false, false, "NumMultiply", "NumStar");
        Add(0x4A, false, false, "NumMinus");
        Add(0x4E, false, false, "NumPlus");
        Add(0x35, true, false, "NumDivide", "NumSlash");
        Add(0x1C, true, false, "NumEnter");
        return k;
    }
}
