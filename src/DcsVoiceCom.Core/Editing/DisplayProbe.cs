using System.Text.RegularExpressions;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>A control that might drive the display, and whether it is a likely one (ticked first).</summary>
public sealed record ProbeCandidate(string Control, string Description, bool Likely, bool IsKey);

/// <summary>A control that was left out, and why.</summary>
public sealed record ProbeDropped(string Control, string Reason);

public sealed record ProbeCandidates(IReadOnlyList<ProbeCandidate> Candidates, IReadOnlyList<ProbeDropped> Dropped);

/// <summary>What the display did when one key was pressed.</summary>
public sealed record KeyDiff(int Changed, int Position, char? Char);

/// <summary>The display and cockpit, as the probe sees them. <see cref="Read"/> returns the padded display text or null.</summary>
public sealed record ProbeContext(ControlCatalog Catalog, Func<string, string?> Read, Func<string, string, Task> Send)
{
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(150);
    public IReadOnlyList<string> Up { get; init; } = EnterValueStep.DefaultUp;
    public IReadOnlyList<string> Down { get; init; } = EnterValueStep.DefaultDown;
    public IReadOnlyList<string> Press { get; init; } = EnterValueStep.DefaultPress;
    public int MaxSteps { get; init; } = 40;
}

/// <summary>What learning found: wheel fields, or keypad keys, plus the controls that were dropped.</summary>
public sealed class ProbeResult
{
    public List<DisplayField> Wheels { get; } = [];
    public DisplayField? KeypadField { get; set; }
    public Dictionary<string, string> Keys { get; } = [];
    public string? Clear { get; set; }
    public List<ProbeDropped> Dropped { get; } = [];
    public List<string> Notes { get; } = [];
    public bool Cancelled { get; set; }
}

/// <summary>
/// "Learn from display": works out wheels or keypad keys by operating them once and watching which characters of a
/// DCS-BIOS display change. Runs only when the user asks; nothing else calls it.
/// </summary>
public static partial class DisplayProbe
{
    private static readonly HashSet<string> NeverTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWER", "MASTER", "ARM", "JETT", "FIRE", "EJECT", "CANOPY", "GEAR", "FLAP", "FLAPS", "HOOK", "EMERG", "EMERGENCY", "ZEROIZE",
    };

    /// <summary>Keys that act on the entry: only their identifier is checked, a digit key's description may say "enter".</summary>
    private static readonly HashSet<string> NeverKeyTokens = new(StringComparer.OrdinalIgnoreCase) { "ENT", "ENTER", "EXEC", "LOAD", "CMD" };

    private static readonly HashSet<string> ClearTokens = new(StringComparer.OrdinalIgnoreCase) { "CLR", "CLEAR", "DEL", "DELETE", "BKSP", "RTN", "BACK" };

    private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "POINT", "DOT", "DECIMAL", "DP", "SLASH", "DASH", "MINUS", "NEG", "SPC", "SPACE", "SP",
    };

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex Words();

    // ---- pure helpers -------------------------------------------------------------------------------------

    /// <summary>Cells that differ between any of the readings: clocks, blinking cursors, timers.</summary>
    public static HashSet<int> NoisyCells(IReadOnlyList<string> readings)
    {
        var noisy = new HashSet<int>();
        if (readings.Count < 2) return noisy;
        var width = readings.Max(r => r.Length);
        var first = readings[0].PadRight(width);
        foreach (var reading in readings.Skip(1))
        {
            var padded = reading.PadRight(width);
            for (var i = 0; i < width; i++) if (padded[i] != first[i]) noisy.Add(i);
        }
        return noisy;
    }

    /// <summary>The cells that changed between two readings, noisy ones left out.</summary>
    public static List<int> ChangedCells(string before, string after, IReadOnlySet<int>? noisy = null)
    {
        var width = Math.Max(before.Length, after.Length);
        before = before.PadRight(width);
        after = after.PadRight(width);
        var changed = new List<int>();
        for (var i = 0; i < width; i++) if (before[i] != after[i] && noisy?.Contains(i) != true) changed.Add(i);
        return changed;
    }

    /// <summary>First changed cell and how far the change reaches; null when nothing changed.</summary>
    public static (int At, int Len)? DiffSpan(string before, string after, IReadOnlySet<int>? noisy = null)
    {
        var changed = ChangedCells(before, after, noisy);
        return changed.Count == 0 ? null : (changed[0], changed[^1] - changed[0] + 1);
    }

    /// <summary>
    /// A wheel's values in click-up order as a range: a run of whole numbers is "22-39" (reversed: Flip), anything
    /// else a list ("00,25,50,75", "X,Y"). Null for fewer than two values or repeated ones.
    /// </summary>
    public static (string Range, bool Flip)? RangeFromValues(IReadOnlyList<string> values)
    {
        var items = values.Select(v => v.Trim()).ToList();
        if (items.Count < 2 || items.Any(i => i.Length == 0) || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count) return null;
        var numbers = items.Select(i => int.TryParse(i, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1).ToList();
        if (numbers.All(n => n >= 0))
        {
            if (Enumerable.Range(1, numbers.Count - 1).All(i => numbers[i] == numbers[i - 1] + 1)) return ($"{numbers[0]}-{numbers[^1]}", false);
            if (Enumerable.Range(1, numbers.Count - 1).All(i => numbers[i] == numbers[i - 1] - 1)) return ($"{numbers[^1]}-{numbers[0]}", true);
        }
        return (string.Join(",", items), false);
    }

    /// <summary>What one key press did: the single new character, or how many cells changed (0 = nothing, 2+ = a page or mode key).</summary>
    public static KeyDiff KeyFromDiff(string before, string after, IReadOnlySet<int>? noisy = null)
    {
        var changed = ChangedCells(before, after, noisy);
        if (changed.Count != 1) return new KeyDiff(changed.Count, changed.Count > 0 ? changed[0] : -1, null);
        var ch = after.PadRight(changed[0] + 1)[changed[0]];
        return new KeyDiff(1, changed[0], char.IsWhiteSpace(ch) ? null : char.ToUpperInvariant(ch));
    }

    /// <summary>
    /// A wheel that wraps is read from wherever it was left. Turns the cycle to start at its smallest value (or its
    /// biggest, when clicking up counts down), so "28,29…39,22…27" becomes "22…39".
    /// </summary>
    public static List<string> NormalizeCycle(IReadOnlyList<string> cycle)
    {
        var items = cycle.Select(v => v.Trim()).ToList();
        if (items.Count < 2) return [.. cycle];
        var numbers = items.Select(i => int.TryParse(i, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1).ToList();
        var key = numbers.All(n => n >= 0) ? numbers.Select(n => (IComparable)n).ToList() : items.Select(i => (IComparable)i.ToUpperInvariant()).ToList();
        int Cmp(int a, int b) => key[a].CompareTo(key[b]);
        var min = Enumerable.Range(0, key.Count).Aggregate((a, b) => Cmp(b, a) < 0 ? b : a);
        var max = Enumerable.Range(0, key.Count).Aggregate((a, b) => Cmp(b, a) > 0 ? b : a);
        bool Rises(int from) => Enumerable.Range(1, key.Count - 1).All(i => Cmp((from + i) % key.Count, (from + i - 1) % key.Count) > 0);
        bool Falls(int from) => Enumerable.Range(1, key.Count - 1).All(i => Cmp((from + i) % key.Count, (from + i - 1) % key.Count) < 0);
        var start = Rises(min) ? min : Falls(max) ? max : 0;
        return Enumerable.Range(0, cycle.Count).Select(i => cycle[(start + i) % cycle.Count]).ToList();
    }

    /// <summary>"never-touch" when a control must never be operated by a probe, else null.</summary>
    public static string? NeverTouchReason(ControlDefinition control)
    {
        var idWords = Words().Matches(control.Identifier).Select(m => m.Value).ToList();
        if (idWords.Any(NeverTokens.Contains) || idWords.Any(NeverKeyTokens.Contains)) return "never-touch";
        if (Words().Matches(control.Description).Any(m => NeverTokens.Contains(m.Value))) return "never-touch";
        var joined = control.Identifier.Replace("_", "").Replace("-", "") + control.Description.Replace("-", "").Replace(" ", "");
        if (joined.Contains("SELFTEST", StringComparison.OrdinalIgnoreCase)) return "never-touch";
        return null;
    }

    private static bool IsClearName(ControlDefinition control) => Words().Matches(control.Identifier).Select(m => m.Value).LastOrDefault() is { } w && ClearTokens.Contains(w);

    /// <summary>A key is a push button (action, two-position); anything that steps or has more positions is a wheel.</summary>
    public static bool IsKeyControl(ControlDefinition control)
    {
        if (control.FindInput(InputInterface.FixedStep) != null || control.FindInput(InputInterface.VariableStep) != null) return false;
        if (control.FindInput(InputInterface.SetState)?.MaxValue is >= 2) return false;
        return true;
    }

    /// <summary>
    /// Controls that could drive <paramref name="display"/>: same category (or an extra one) or the same name stem,
    /// with a step, set_state, variable_step or action input. Names only rank them; the never-touch list is dropped.
    /// </summary>
    public static ProbeCandidates Candidates(ControlCatalog catalog, string display, IEnumerable<string>? extraCategories = null)
    {
        var shown = catalog.Find(display);
        var categories = new HashSet<string>(extraCategories ?? [], StringComparer.OrdinalIgnoreCase);
        if (shown != null && shown.Category.Length > 0) categories.Add(shown.Category);
        var cut = display.LastIndexOf('_');
        var stem = (cut > 0 ? display[..cut] : display).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        var found = new List<ProbeCandidate>();
        var dropped = new List<ProbeDropped>();
        foreach (var c in catalog.Controls)
        {
            if (c.Identifier.Equals(display, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(c.FindInput(InputInterface.FixedStep) != null || c.FindInput(InputInterface.SetState) != null
                  || c.FindInput(InputInterface.VariableStep) != null || c.FindInput(InputInterface.Action) != null)) continue;
            var related = categories.Contains(c.Category) || (stem.Length > 0 && c.Identifier.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
            if (!related) continue;
            if (NeverTouchReason(c) is { } reason)
            {
                dropped.Add(new ProbeDropped(c.Identifier, reason));
                continue;
            }
            found.Add(new ProbeCandidate(c.Identifier, c.Description, IsLikely(c.Identifier), IsKeyControl(c)));
        }
        return new ProbeCandidates(
            found.OrderByDescending(f => f.Likely).ThenBy(f => f.Control, StringComparer.OrdinalIgnoreCase).ToList(),
            dropped);
    }

    private static bool IsLikely(string id)
    {
        if (Regex.IsMatch(id, @"_\d+_DIAL$", RegexOptions.IgnoreCase)) return true;
        var suffix = id[(id.LastIndexOf('_') + 1)..];
        return (suffix.Length == 1 && char.IsLetterOrDigit(suffix[0])) || NamedKeys.Contains(suffix) || ClearTokens.Contains(suffix);
    }

    /// <summary>Read-only fields for the fixed characters (a decimal point, brackets) inside the span of the given fields.</summary>
    public static List<DisplayField> WithFixedGaps(IEnumerable<DisplayField> fields, string live)
    {
        var list = fields.OrderBy(f => f.At).ToList();
        if (list.Count == 0) return list;
        var start = list.Min(f => f.At);
        var end = list.Max(f => f.At + f.Len);
        for (var i = start; i < end && i < live.Length; i++)
        {
            if (list.Any(f => i >= f.At && i < f.At + f.Len) || live[i] == ' ') continue;
            list.Add(new DisplayField { At = i, Len = 1, Kind = FieldKind.ReadOnly });
        }
        return list.OrderBy(f => f.At).ToList();
    }

    // ---- live learning ------------------------------------------------------------------------------------

    /// <summary>
    /// Operates the ticked candidates once each and learns wheel fields or keys from the display. Wheels are put
    /// back and the keypad entry is cleared at the end, also when cancelled.
    /// </summary>
    public static async Task<ProbeResult> LearnAsync(ProbeContext ctx, string display, IReadOnlyList<ProbeCandidate> candidates,
        IProgress<string>? progress, CancellationToken cancel)
    {
        var result = new ProbeResult();
        var run = new Run(ctx, display, result, progress);
        try
        {
            await run.NoiseAsync(cancel);
            var wheels = candidates.Where(c => !c.IsKey).ToList();
            var keys = candidates.Where(c => c.IsKey).ToList();
            var total = candidates.Count;
            var done = 0;
            foreach (var wheel in wheels)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Testing {wheel.Control}… {++done}/{total}");
                await run.WheelAsync(wheel, cancel);
            }
            await run.KeysAsync(keys, done, total, cancel);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        finally
        {
            await run.CleanUpAsync();
        }
        run.Finish();
        return result;
    }

    private sealed class Run(ProbeContext ctx, string display, ProbeResult result, IProgress<string>? progress)
    {
        private HashSet<int> _noisy = [];
        private readonly Dictionary<string, (WheelMove Move, int Net)> _wheels = [];
        private readonly List<(DisplayField Field, ControlDefinition Control, int Values)> _found = [];
        private bool _dirty;
        private string? _clear;
        private readonly List<(string Control, string Char, int Pos)> _keys = [];

        public readonly record struct WheelMove(string Control, bool Step);

        private string Read() => ctx.Read(display) ?? throw new InvalidOperationException("The display can't be read right now.");

        public async Task NoiseAsync(CancellationToken cancel)
        {
            var readings = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                readings.Add(Read());
                if (i < 4) await Task.Delay(ctx.Delay, cancel);
            }
            _noisy = NoisyCells(readings);
            if (_noisy.Count > 0) result.Notes.Add($"Ignored {_noisy.Count} changing characters (clock or cursor).");
        }

        private static bool IsStep(ControlDefinition c) =>
            (c.FindInput(InputInterface.FixedStep) != null || c.FindInput(InputInterface.VariableStep) != null)
            && c.FindInput(InputInterface.SetState)?.MaxValue is not <= 2;

        private async Task ClickAsync(ControlDefinition c, bool up, CancellationToken cancel)
        {
            // A click is never cut in half: a spring switch must get its release line.
            cancel.ThrowIfCancellationRequested();
            if (IsStep(c))
            {
                await ctx.Send(c.Identifier, up ? "INC" : "DEC");
            }
            else
            {
                foreach (var arg in up ? ctx.Up : ctx.Down)
                {
                    await ctx.Send(c.Identifier, arg);
                    await Task.Delay(ctx.Delay);
                }
            }
            await Task.Delay(ctx.Delay);
        }

        private bool Same(string a, string b) => ChangedCells(a, b, _noisy).Count == 0;

        public async Task WheelAsync(ProbeCandidate candidate, CancellationToken cancel)
        {
            var control = ctx.Catalog.Find(candidate.Control)!;
            var start = Read();
            var net = 0;
            _wheels[control.Identifier] = (new WheelMove(control.Identifier, IsStep(control)), 0);
            void Track(int delta) { net += delta; _wheels[control.Identifier] = (new WheelMove(control.Identifier, IsStep(control)), net); }

            await ClickAsync(control, up: true, cancel);
            Track(1);
            var readings = new List<string> { start, Read() };
            if (Same(readings[0], readings[1]))
            {
                result.Dropped.Add(new ProbeDropped(control.Identifier, "no change"));
                Track(-1);
                _wheels.Remove(control.Identifier);
                return;
            }

            var wrapped = false;
            var stuck = false;
            while (readings.Count < ctx.MaxSteps + 1)
            {
                cancel.ThrowIfCancellationRequested();
                await ClickAsync(control, up: true, cancel);
                var now = Read();
                if (Same(now, readings[^1])) { stuck = true; break; }
                Track(1);
                if (Same(now, readings[0])) { wrapped = true; Track(-readings.Count); break; }
                readings.Add(now);
            }

            var lowReadings = new List<string>();
            if (stuck)
            {
                // No wrap: go back to the start, then down to the other end.
                for (var i = 0; i < readings.Count - 1; i++) { await ClickAsync(control, up: false, cancel); Track(-1); }
                var last = readings[0];
                while (lowReadings.Count < ctx.MaxSteps)
                {
                    cancel.ThrowIfCancellationRequested();
                    await ClickAsync(control, up: false, cancel);
                    var now = Read();
                    if (Same(now, last)) break;
                    Track(-1);
                    lowReadings.Add(now);
                    last = now;
                }
            }
            else if (!wrapped)
            {
                result.Notes.Add($"{control.Identifier}: no repeat within {ctx.MaxSteps} clicks; its range may be longer.");
            }

            // Put the wheel back where it was.
            await RestoreAsync(control, cancel);

            var all = Enumerable.Reverse(lowReadings).Concat(readings).ToList();
            var span = SpanOf(all);
            if (span is null)
            {
                result.Dropped.Add(new ProbeDropped(control.Identifier, "no change"));
                return;
            }
            var (at, len) = span.Value;
            var values = all.Select(r => r.PadRight(at + len).Substring(at, len)).ToList();
            if (wrapped) values = NormalizeCycle(values);
            var range = RangeFromValues(values);
            if (range is null)
            {
                result.Dropped.Add(new ProbeDropped(control.Identifier, "values not readable"));
                return;
            }
            var field = new DisplayField
            {
                At = at, Len = len, Kind = FieldKind.Wheel, Control = control.Identifier,
                Range = range.Value.Range, Flip = range.Value.Flip,
                Mode = SetFits(control, values, range.Value) ? WheelMode.Set : IsStep(control) ? WheelMode.Step : WheelMode.Click,
            };
            _found.Add((field, control, values.Count));
        }

        private (int At, int Len)? SpanOf(List<string> readings)
        {
            var changed = new SortedSet<int>();
            foreach (var r in readings.Skip(1)) foreach (var i in ChangedCells(readings[0], r, _noisy)) changed.Add(i);
            return changed.Count == 0 ? null : (changed.Min, changed.Max - changed.Min + 1);
        }

        private static bool SetFits(ControlDefinition control, List<string> values, (string Range, bool Flip) range) =>
            !range.Flip && control.FindInput(InputInterface.SetState)?.MaxValue is { } max && max > 2
            && values.Count == max + 1 && range.Range == $"0-{max}"
            && max == (int)Math.Pow(10, values.Max(v => v.Trim().Length)) - 1; // the digits are the set_state value

        private async Task RestoreAsync(ControlDefinition control, CancellationToken cancel)
        {
            if (!_wheels.TryGetValue(control.Identifier, out var state) || state.Net == 0) { _wheels.Remove(control.Identifier); return; }
            var net = state.Net;
            while (net != 0)
            {
                await ClickAsync(control, up: net < 0, cancel);
                net += net < 0 ? 1 : -1;
                _wheels[control.Identifier] = (state.Move, net);
            }
            _wheels.Remove(control.Identifier);
        }

        // ---- keypad ----

        private async Task PressAsync(string control, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var arg in ctx.Press)
            {
                await ctx.Send(control, arg);
                await Task.Delay(ctx.Delay);
            }
            await Task.Delay(ctx.Delay);
        }

        private async Task ClearEntryAsync(CancellationToken cancel)
        {
            if (_clear is null) return;
            await PressAsync(_clear, cancel);
            await PressAsync(_clear, cancel);
        }

        public async Task KeysAsync(List<ProbeCandidate> keys, int done, int total, CancellationToken cancel)
        {
            if (keys.Count == 0) return;
            var controls = keys.Select(k => ctx.Catalog.Find(k.Control)!).ToList();
            var clearLike = controls.Where(IsClearName).ToList();
            var plain = controls.Where(c => !IsClearName(c)).ToList();

            // 1. The clear key: type one key, then see which clear-like key empties the entry.
            var baseline = Read();
            if (clearLike.Count > 0)
            {
                foreach (var typer in plain.Take(5))
                {
                    cancel.ThrowIfCancellationRequested();
                    _dirty = true;
                    await PressAsync(typer.Identifier, cancel);
                    if (KeyFromDiff(baseline, Read(), _noisy).Changed != 1) { continue; }
                    foreach (var candidate in clearLike)
                    {
                        progress?.Report($"Testing {candidate.Identifier}… {done + 1}/{total}");
                        await PressAsync(candidate.Identifier, cancel);
                        if (Same(Read(), baseline)) { _clear = candidate.Identifier; break; }
                    }
                    break;
                }
                foreach (var c in clearLike.Where(c => c.Identifier != _clear)) result.Dropped.Add(new ProbeDropped(c.Identifier, "does not clear the entry"));
                done += clearLike.Count;
            }
            if (_clear is null) result.Notes.Add("No clear key found: clear the entry by hand between steps. Cleanup is not automatic.");
            result.Clear = _clear;

            // 2. Every key once: clear, read, press, read.
            foreach (var key in plain)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Testing {key.Identifier}… {++done}/{total}");
                await ClearEntryAsync(cancel);
                var before = Read();
                _dirty = true;
                await PressAsync(key.Identifier, cancel);
                var diff = KeyFromDiff(before, Read(), _noisy);
                if (diff.Changed == 0) result.Dropped.Add(new ProbeDropped(key.Identifier, "no change"));
                else if (diff.Changed > 1) result.Dropped.Add(new ProbeDropped(key.Identifier, "changes the page"));
                else if (diff.Char is null) result.Dropped.Add(new ProbeDropped(key.Identifier, "no character"));
                else if (_keys.Any(k => k.Char == diff.Char.ToString())) result.Dropped.Add(new ProbeDropped(key.Identifier, $"same character as {_keys.First(k => k.Char == diff.Char.ToString()).Control}"));
                else _keys.Add((key.Identifier, diff.Char.ToString()!, diff.Position));
            }

            // 3. Where the text sits: type two keys, then keep typing until it stops growing.
            if (_keys.Count >= 2) await FieldAsync(cancel);
            else if (_keys.Count == 1) result.Notes.Add("Only one key worked, so the entry field could not be found.");
        }

        private async Task FieldAsync(CancellationToken cancel)
        {
            progress?.Report("Finding the entry field…");
            await ClearEntryAsync(cancel);
            var k1 = _keys[0].Control;
            var k2 = _keys[1].Control;
            var r0 = Read();
            await PressAsync(k1, cancel);
            var r1 = Read();
            var first = ChangedCells(r0, r1, _noisy);
            await PressAsync(k2, cancel);
            var r2 = Read();
            var second = ChangedCells(r1, r2, _noisy);
            if (first.Count != 1) return;
            var p1 = first[0];
            var align = second.Count == 1 && second[0] > p1 ? TextAlign.Left : second.Contains(p1) && second.Contains(p1 - 1) ? TextAlign.Right : (TextAlign?)null;
            if (align is null)
            {
                result.Notes.Add("Could not tell where the entry field is; pick it on the display.");
                return;
            }
            var count = 2;
            var last = r2;
            while (count < 40)
            {
                cancel.ThrowIfCancellationRequested();
                await PressAsync(k1, cancel);
                var now = Read();
                if (Same(now, last)) break;
                last = now;
                count++;
            }
            var at = align == TextAlign.Left ? p1 : p1 - count + 1;
            result.KeypadField = new DisplayField { At = Math.Max(0, at), Len = count, Kind = FieldKind.Keypad, Align = align };
        }

        public async Task CleanUpAsync()
        {
            // Cleanup runs also after Cancel: it must not be cancelled itself.
            foreach (var (id, state) in _wheels.ToList())
            {
                if (ctx.Catalog.Find(id) is { } control)
                {
                    try { await RestoreAsync(control, CancellationToken.None); } catch { /* best effort */ }
                }
            }
            if (_dirty)
            {
                try { await ClearEntryAsync(CancellationToken.None); } catch { /* best effort */ }
            }
        }

        public void Finish()
        {
            // Wheels that change the same characters: keep the one with more values.
            var kept = new List<(DisplayField Field, ControlDefinition Control, int Values)>();
            foreach (var item in _found.OrderByDescending(f => f.Values))
            {
                var clash = kept.FirstOrDefault(k => item.Field.At < k.Field.At + k.Field.Len && k.Field.At < item.Field.At + item.Field.Len);
                if (clash.Field != null) result.Dropped.Add(new ProbeDropped(item.Control.Identifier, $"changes the same characters as {clash.Control.Identifier}; a display that shifts its text when a wheel gets wider (9 to 10) can't be learned"));
                else kept.Add(item);
            }
            result.Wheels.AddRange(kept.Select(k => k.Field).OrderBy(f => f.At));
            foreach (var (control, ch, _) in _keys) result.Keys[ch] = control;
        }
    }
}
