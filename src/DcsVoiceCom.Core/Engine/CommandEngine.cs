using System.Globalization;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Input;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Engine;

public sealed record EngineOptions
{
    public const int DefaultThreshold = 85;
    public const int DefaultCriticalThreshold = 95;

    /// <summary>Score (0-100) a command needs, for every profile.</summary>
    public int Threshold { get; init; } = DefaultThreshold;

    /// <summary>Score a critical command needs.</summary>
    public int CriticalThreshold { get; init; } = DefaultCriticalThreshold;

    public TimeSpan KeyHold { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// Turns a recognition result into cockpit actions: checks the threshold (normal or critical),
/// parses spoken parameters, and runs the command's steps. One command runs at a time;
/// <see cref="Cancel"/> (called on every push-to-talk press) stops a running sequence.
/// </summary>
public sealed class CommandEngine
{
    private readonly IBiosSender _bios;
    private readonly IKeySender _keys;
    private readonly IStateReader _state;
    private readonly ICuePlayer _cues;
    private readonly EngineOptions _options;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _ctsLock = new();
    private CancellationTokenSource? _running;
    private CompiledProfile _profile = CompiledProfile.Empty;

    public CommandEngine(IBiosSender bios, IKeySender keys, IStateReader state, ICuePlayer cues, EngineOptions? options = null)
    {
        _bios = bios;
        _keys = keys;
        _state = state;
        _cues = cues;
        _options = options ?? new EngineOptions();
    }

    public CompiledProfile Profile
    {
        get => _profile;
        set => _profile = value ?? CompiledProfile.Empty;
    }

    /// <summary>Raised for every handled recognition, after the sequence ends.</summary>
    public event Action<CommandOutcome>? Executed;

    /// <summary>Cancels the running sequence, if any.</summary>
    public void Cancel()
    {
        lock (_ctsLock)
        {
            _running?.Cancel();
        }
    }

    /// <summary>
    /// Resolves the recognition against the active profile. With <paramref name="dryRun"/> the steps are
    /// traced but no DCS-BIOS line, key or cue is sent, and waits are skipped.
    /// </summary>
    public async Task<CommandOutcome> HandleAsync(RecognitionResult recognition, bool dryRun = false)
    {
        var profile = _profile;
        var empty = new Dictionary<string, string>();
        var cues = dryRun ? new NullCuePlayer() : _cues;

        CommandOutcome Finish(OutcomeStatus status, string? command, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> trace, string? message)
        {
            var outcome = new CommandOutcome(status, recognition, command, values, trace, message);
            Executed?.Invoke(outcome);
            return outcome;
        }

        if (recognition.Reject is not null)
        {
            cues.Play(Cue.NoMatch);
            var rejected = recognition.Phrase is null ? null : profile.Find(recognition.Phrase)?.Command.Name;
            return Finish(OutcomeStatus.NoMatch, rejected, empty, [], recognition.Reject);
        }

        // The best match first, then runner-ups: the first one above threshold whose spoken values parse wins.
        var candidates = new List<RecognitionCandidate>();
        if (recognition.Phrase is not null)
            candidates.Add(new RecognitionCandidate { Phrase = recognition.Phrase, Score = recognition.Score, Slots = recognition.Slots });
        candidates.AddRange(recognition.Candidates.Where(c => c.Phrase != recognition.Phrase));

        PhraseEntry? entry = null;
        RecognitionCandidate? chosen = null;
        SlotParseResult? slots = null;
        string? firstSlotError = null;
        string? firstCommand = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Score < _options.Threshold) break;
            var found = profile.Find(candidate.Phrase);
            if (found is null) continue;
            firstCommand ??= found.Command.Name;
            var parsed = SlotParser.Parse(found.Template, candidate.Slots);
            if (!parsed.Success)
            {
                firstSlotError ??= parsed.Error;
                continue;
            }
            (entry, chosen, slots) = (found, candidate, parsed);
            break;
        }

        if (entry is null || chosen is null || slots is null)
        {
            cues.Play(Cue.NoMatch);
            var why = recognition.Error ?? firstSlotError ?? (recognition.Phrase is null || profile.Find(recognition.Phrase) is null
                ? "no phrase matched"
                : $"score {recognition.Score:0} below {_options.Threshold}");
            return Finish(OutcomeStatus.NoMatch, firstCommand, empty, [], why);
        }

        var command = entry.Command;
        if (command.Critical && chosen.Score < _options.CriticalThreshold)
        {
            cues.Play(Cue.Refused);
            return Finish(OutcomeStatus.Refused, command.Name, empty, [], $"critical command, score {chosen.Score:0} below {_options.CriticalThreshold}");
        }

        // A new command replaces the running one.
        Cancel();
        await _runLock.WaitAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        lock (_ctsLock) _running = cts;
        var trace = new List<string>();
        try
        {
            cues.Play(Cue.Accepted);
            var runner = new StepRunner(
                dryRun ? NullSenders.Instance : _bios,
                dryRun ? NullSenders.Instance : _keys,
                _state, cues, _options, trace, dryRun);
            var ok = await runner.RunAsync(command.Steps, slots.Values, cts.Token).ConfigureAwait(false);
            if (!ok)
            {
                cues.Play(Cue.Refused);
                return Finish(OutcomeStatus.Refused, command.Name, slots.Values, trace, trace.LastOrDefault());
            }
            return Finish(OutcomeStatus.Completed, command.Name, slots.Values, trace, null);
        }
        catch (OperationCanceledException)
        {
            return Finish(OutcomeStatus.Cancelled, command.Name, slots.Values, trace, "cancelled");
        }
        catch (Exception ex) when (ex is StepException or FormatException)
        {
            cues.Play(Cue.Refused);
            return Finish(OutcomeStatus.Failed, command.Name, slots.Values, trace, ex.Message);
        }
        finally
        {
            lock (_ctsLock)
            {
                if (_running == cts) _running = null;
            }
            cts.Dispose();
            _runLock.Release();
        }
    }
}

/// <summary>Executes one command's steps. Returns false when a wait or step limit fails (the caller plays "refused").</summary>
internal sealed class StepRunner(
    IBiosSender bios,
    IKeySender keys,
    IStateReader state,
    ICuePlayer cues,
    EngineOptions options,
    List<string> trace,
    bool dryRun)
{
    public async Task<bool> RunAsync(IEnumerable<Step> steps, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!await RunStepAsync(step, values, ct).ConfigureAwait(false)) return false;
        }
        return true;
    }

    private async Task<bool> RunStepAsync(Step step, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        switch (step)
        {
            case BiosStep b:
            {
                var control = Placeholders.Apply(b.Control, values);
                var arg = Placeholders.Apply(b.Arg, values);
                if (b.PressMs is { } holdMs)
                {
                    var release = Placeholders.Apply(b.ReleaseArg, values);
                    trace.Add($"press {control} {arg} ({holdMs} ms), release {release}");
                    try
                    {
                        // Inside the try: a cancel that lands while the push-in line goes out still sends the release.
                        await bios.SendAsync(control, arg, ct).ConfigureAwait(false);
                        if (!dryRun) await Task.Delay(Math.Max(0, holdMs), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Never leave a button held in, even when push-to-talk cancels the sequence mid-press.
                        await bios.SendAsync(control, release, CancellationToken.None).ConfigureAwait(false);
                    }
                    return true;
                }
                if (b.SkipIfAlready && int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var wanted)
                    && state.IsLive && state.TryReadInteger(control, out var current) && current == wanted)
                {
                    trace.Add($"skip {control} {arg} (already {current})");
                    return true;
                }
                trace.Add($"bios {control} {arg}");
                await bios.SendAsync(control, arg, ct).ConfigureAwait(false);
                return true;
            }

            case BiosUntilStep u:
                return await RunUntilAsync(u, values, ct).ConfigureAwait(false);

            case EnterValueStep e:
                return await RunEnterValueAsync(e, values, ct).ConfigureAwait(false);

            case KeyStep k:
            {
                var chord = KeyChord.Parse(Placeholders.Apply(k.Keys, values));
                var hold = k.HoldMs is { } ms ? TimeSpan.FromMilliseconds(ms) : options.KeyHold;
                trace.Add($"key {chord}");
                await keys.SendAsync(chord, hold, ct).ConfigureAwait(false);
                return true;
            }

            case WaitStep w:
                trace.Add($"wait {w.Ms} ms");
                if (!dryRun) await Task.Delay(w.Ms, ct).ConfigureAwait(false);
                return true;

            case WaitForStep wf:
            {
                var description = Describe(wf.Until, values);
                if (dryRun)
                {
                    trace.Add($"wait for {description} (timeout {wf.TimeoutMs} ms)");
                    return true;
                }
                var deadline = DateTime.UtcNow.AddMilliseconds(wf.TimeoutMs);
                while (!Evaluate(wf.Until, values))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        trace.Add($"timed out waiting for {description}");
                        return false;
                    }
                    await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
                }
                trace.Add($"waited for {description}");
                return true;
            }

            case IfStep i:
            {
                var result = Evaluate(i.Condition, values);
                trace.Add($"if {Describe(i.Condition, values)} → {(result ? "then" : "else")}");
                return await RunAsync(result ? i.Then : i.Else, values, ct).ConfigureAwait(false);
            }

            case CueStep c:
                trace.Add($"cue {c.Cue}");
                cues.Play(c.Cue);
                return true;

            case ForEachCharStep f:
            {
                var text = Placeholders.Apply(f.Value, values);
                foreach (var ch in text)
                {
                    var key = ch.ToString();
                    var scoped = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                    {
                        ["char"] = f.Map.GetValueOrDefault(key) ?? key,
                    };
                    if (!await RunAsync(f.Steps, scoped, ct).ConfigureAwait(false)) return false;
                }
                return true;
            }

            default:
                throw new StepException($"Unsupported step {step.Type}");
        }
    }

    private async Task<bool> RunUntilAsync(BiosUntilStep u, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var control = Placeholders.Apply(u.Control, values);
        var output = Placeholders.Apply(u.Output ?? u.Control, values);
        var targetText = Placeholders.Apply(u.Target, values);
        if (!double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var target))
            throw new StepException($"bios_until target \"{targetText}\" is not a number");
        var raw = (int)Math.Round(target * u.Scale);

        if (dryRun)
        {
            trace.Add($"turn {control} until {output} = {raw} (±{u.Tolerance}, max {u.MaxSteps} steps)");
            return true;
        }
        if (!state.IsLive || !state.TryReadInteger(output, out _))
        {
            trace.Add($"cannot read {output}");
            return false;
        }

        int? lastDirection = null;
        for (var step = 0; step <= u.MaxSteps; step++)
        {
            state.TryReadInteger(output, out var current);
            var diff = raw - current;
            if (Math.Abs(diff) <= u.Tolerance)
            {
                trace.Add($"{output} = {current} after {step} steps");
                return true;
            }
            var direction = Math.Sign(diff);
            if (lastDirection is { } last && last != direction)
            {
                // Passed the target: the knob's step is coarser than the tolerance; stop at the closest.
                trace.Add($"{output} = {current} (overshot {raw}) after {step} steps");
                return true;
            }
            if (step == u.MaxSteps) break;
            lastDirection = direction;
            await bios.SendAsync(control, direction > 0 ? u.Inc : u.Dec, ct).ConfigureAwait(false);
            await Task.Delay(u.DelayMs, ct).ConfigureAwait(false);
        }
        trace.Add($"{output} did not reach {raw} within {u.MaxSteps} steps");
        return false;
    }

    private async Task<bool> RunEnterValueAsync(EnterValueStep e, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var text = Placeholders.Apply(e.Value, values).Trim();
        if (e.Fields is { Count: > 0 }) return await RunDisplayFieldsAsync(e, text, values, ct).ConfigureAwait(false);

        var verify = e.Verify is { Output.Length: > 0 } v ? Placeholders.Apply(v.Output, values) : null;

        if (e.Method == EnterMethod.Keypad)
        {
            text = text.ToUpperInvariant();
            ThrowOnMissingKeys(e, text);
        }
        else if (EnterValue.CheckNumber(e, text, out _) is { } problem)
        {
            throw new StepException(problem);
        }

        if (verify != null && !dryRun && state.IsLive && state.TryReadString(verify, out var before) && EnterValue.DisplayMatches(before, text))
        {
            trace.Add($"skip: {verify} already shows {text}");
            return true;
        }

        var ok = e.Method == EnterMethod.Keypad
            ? await TypeKeysAsync(e, text, ct).ConfigureAwait(false)
            : await TurnWheelsAsync(e, text, ct).ConfigureAwait(false);
        if (!ok) return false;
        if (verify is null) return true;
        if (dryRun)
        {
            trace.Add($"check {verify} shows {text}");
            return true;
        }

        var timeout = e.Verify!.TimeoutMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        var shown = "";
        while (true)
        {
            if (state.TryReadString(verify, out shown) && EnterValue.DisplayMatches(shown, text))
            {
                trace.Add($"{verify} = \"{shown.Trim()}\"");
                return true;
            }
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }
        trace.Add($"{verify} shows \"{shown.Trim()}\", wanted {text}");
        return false;
    }

    private static void ThrowOnMissingKeys(EnterValueStep e, string text)
    {
        var missing = EnterValue.MissingKeys(e, text);
        if (missing.Count > 0) throw new StepException($"No key for {string.Join(", ", missing.Select(c => c == ' ' ? "space" : $"\"{c}\""))} in \"{text}\"");
    }

    /// <summary>
    /// The display layout: every field's target comes from the value's position on <see cref="EnterValueStep.Display"/>,
    /// wheels are read back from their own characters of it, and the same display is checked afterwards.
    /// </summary>
    private async Task<bool> RunDisplayFieldsAsync(EnterValueStep e, string text, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(e.Display)) throw new StepException("enter_value has no display");
        var display = Placeholders.Apply(e.Display, values);
        text = text.ToUpperInvariant();
        var targets = DisplayLayout.Targets(e, text, out var problem);
        if (problem != null) throw new StepException(problem);
        if (e.Method == EnterMethod.Keypad) ThrowOnMissingKeys(e, text);

        if (!dryRun && state.IsLive && state.TryReadString(display, out var before) && DisplayLayout.ShowsAll(before, targets))
        {
            trace.Add($"skip: {display} already shows {text}");
            return true;
        }

        bool ok;
        if (e.Method == EnterMethod.Keypad)
        {
            ok = await TypeKeysAsync(e, text, ct).ConfigureAwait(false);
        }
        else if (dryRun)
        {
            trace.Add($"enter {text}: " + string.Join(", ", targets.Select(t => $"{t.Field.Control} → {t.Text.Trim()}")));
            ok = true;
        }
        else
        {
            ok = true;
            foreach (var target in targets)
            {
                if (!await TurnFieldAsync(e, display, target, ct).ConfigureAwait(false))
                {
                    ok = false;
                    break;
                }
            }
        }
        if (!ok) return false;
        if (dryRun)
        {
            trace.Add($"check {display} shows {text}");
            return true;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(e.TimeoutMs ?? DisplayLayout.DefaultTimeoutMs);
        var shown = "";
        while (true)
        {
            if (state.TryReadString(display, out shown) && DisplayLayout.ShowsAll(shown, targets))
            {
                trace.Add($"{display} = \"{shown.Trim()}\"");
                return true;
            }
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }
        trace.Add($"{display} shows \"{shown.Trim()}\", wanted {text}");
        return false;
    }

    private Task<bool> TurnFieldAsync(EnterValueStep e, string display, FieldTarget target, CancellationToken ct)
    {
        var field = target.Field;
        var range = DisplayLayout.RangeOf(field);
        var wheel = new WheelMove(field.Control ?? "", field.Mode ?? WheelMode.Click, field.Flip, (range.Low, range.High), range.Show, display);
        bool Read(out int position)
        {
            position = 0;
            if (!state.TryReadString(display, out var shown) || range.IndexOf(DisplayLayout.Read(shown, field)) is not { } index) return false;
            position = index;
            return true;
        }
        return TurnAsync(e, wheel, range.IndexOf(target.Text)!.Value, Read, ct);
    }

    private async Task<bool> TypeKeysAsync(EnterValueStep e, string text, CancellationToken ct)
    {
        var press = e.Press is { Count: > 0 } p ? (IReadOnlyList<string>)p : EnterValueStep.DefaultPress;
        var presses = EnterValue.KeyPresses(e, text);
        if (dryRun)
        {
            trace.Add($"type \"{text}\": {string.Join(" ", presses)}");
            return true;
        }
        foreach (var key in presses) await SendAllAsync(key, press, e.DelayMs, ct).ConfigureAwait(false);
        trace.Add($"typed \"{text}\" ({presses.Count} keys)");
        return true;
    }

    private async Task<bool> TurnWheelsAsync(EnterValueStep e, string text, CancellationToken ct)
    {
        EnterValue.TryParseNumber(text, out var value);
        var wheels = e.Wheels ?? [];
        if (wheels.Count == 0) throw new StepException("enter_value has no wheels");

        if (dryRun)
        {
            trace.Add($"enter {text}: " + string.Join(", ", wheels.Select(w => $"{w.Control} → {EnterValue.Format(EnterValue.Digits(value, w.Place, w.Width), w.Width)}")));
            return true;
        }
        foreach (var wheel in wheels)
        {
            if (!await TurnWheelAsync(e, wheel, EnterValue.Digits(value, wheel.Place, wheel.Width), ct).ConfigureAwait(false)) return false;
        }
        return true;
    }

    private Task<bool> TurnWheelAsync(EnterValueStep e, Wheel wheel, int target, CancellationToken ct)
    {
        if (wheel.Mode != WheelMode.Set && string.IsNullOrEmpty(wheel.Read)) throw new StepException($"{wheel.Control} needs a read-back output");
        var move = new WheelMove(wheel.Control, wheel.Mode, wheel.Flip, EnterValue.Range(e, wheel), d => EnterValue.Format(d, wheel.Width), wheel.Read ?? "");
        bool Read(out int digits)
        {
            digits = 0;
            return !string.IsNullOrEmpty(wheel.Read) && TryReadWheel(wheel, out digits);
        }
        return TurnAsync(e, move, target, Read, ct);
    }

    /// <summary>A wheel to move: its control, how it moves, the positions it can show, how a position looks, and what reads it back.</summary>
    private sealed record WheelMove(string Control, WheelMode Mode, bool Flip, (int Low, int High) Range, Func<int, string> Show, string ReadFrom);

/// <summary>Sends of one step-mode click before a wheel counts as not moving.</summary>
    private const int StepTries = 3;

        private delegate bool PositionReader(out int position);

    /// <summary>
    /// Moves one wheel until it reads back <paramref name="target"/>. A click that moves the wrong way swaps
    /// up and down for the rest of the run; no movement or passing the target stops with "refused".
    /// </summary>
    private async Task<bool> TurnAsync(EnterValueStep e, WheelMove wheel, int target, PositionReader read, CancellationToken ct)
    {
        var shownTarget = wheel.Show(target);
        if (wheel.Mode == WheelMode.Set)
        {
            // set_state takes the digits (or the list position) themselves; a read-back, when there is one, checks the result.
            var before = 0;
            var canCheck = state.IsLive && read(out before);
            await SendAllAsync(wheel.Control, [target.ToString(CultureInfo.InvariantCulture)], e.DelayMs, ct).ConfigureAwait(false);
            if (!canCheck || before == target)
            {
                trace.Add($"{wheel.Control} {shownTarget}");
                return true;
            }
            var set = await WaitForChangeAsync(read, before, ct).ConfigureAwait(false);
            if (set == target)
            {
                trace.Add($"{wheel.Control} = {shownTarget}");
                return true;
            }
            trace.Add($"{wheel.Control} shows {wheel.Show(set)} after set {shownTarget} (its set_state may be a position, not digits; try click or step mode)");
            return false;
        }
        if (!state.IsLive || !read(out var current))
        {
            trace.Add($"cannot read {wheel.ReadFrom}");
            return false;
        }

        var flip = wheel.Flip;
        for (var clicks = 0; ; clicks++)
        {
            if (current == target)
            {
                trace.Add($"{wheel.Control} = {shownTarget} after {clicks} clicks");
                return true;
            }
            if (clicks >= e.MaxSteps)
            {
                trace.Add($"{wheel.Control} stuck at {wheel.Show(current)}, wanted {shownTarget} ({e.MaxSteps} clicks)");
                return false;
            }

            var direction = Math.Sign(target - current);
            var up = direction > 0 != flip;
            var args = wheel.Mode == WheelMode.Step
                ? [up ? "INC" : "DEC"]
                : (IReadOnlyList<string>)(up ? e.Up is { Count: > 0 } u ? u : EnterValueStep.DefaultUp : e.Down is { Count: > 0 } d ? d : EnterValueStep.DefaultDown);
            // A knob's small step (+3200) may need a few sends before it reaches the next detent.
            var after = current;
            for (var tries = 0; tries < (wheel.Mode == WheelMode.Step ? StepTries : 1) && after == current; tries++)
            {
                await SendAllAsync(wheel.Control, args, e.DelayMs, ct).ConfigureAwait(false);
                after = await WaitForChangeAsync(read, current, ct).ConfigureAwait(false);
            }
            if (after == current)
            {
                trace.Add($"{wheel.Control} did not move from {wheel.Show(current)} (check {wheel.ReadFrom})");
                return false;
            }
            if (EnterValue.TurnDirection(current, after, wheel.Range) != direction)
            {
                flip = !flip;
                trace.Add($"{wheel.Control} turned the other way; swapping up and down");
            }
            else if (Math.Sign(target - after) == -direction)
            {
                trace.Add($"{wheel.Control} passed {shownTarget} ({wheel.Show(current)} → {wheel.Show(after)}); the wheel cannot show it");
                return false;
            }
            current = after;
        }
    }

    private bool TryReadWheel(Wheel wheel, out int digits)
    {
        digits = 0;
        decimal raw;
        if (state.TryReadInteger(wheel.Read!, out var integer)) raw = integer;
        else if (!state.TryReadString(wheel.Read!, out var text) || !EnterValue.TryParseDisplay(text, out raw)) return false; // a display: "12", "251.075"
        digits = EnterValue.Digits(raw * wheel.ReadScale, wheel.Place, wheel.Width);
        return true;
    }

    /// <summary>The wheel's position once it changes, or <paramref name="before"/> after half a second without change.</summary>
    private async Task<int> WaitForChangeAsync(PositionReader read, int before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (true)
        {
            if (read(out var now) && now != before) return now;
            if (DateTime.UtcNow >= deadline) return before;
            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAllAsync(string control, IReadOnlyList<string> args, int delayMs, CancellationToken ct)
    {
        foreach (var arg in args)
        {
            await bios.SendAsync(control, arg, ct).ConfigureAwait(false);
            if (!dryRun && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    private bool Evaluate(Condition condition, IReadOnlyDictionary<string, string> values)
    {
        var output = Placeholders.Apply(condition.Output, values);
        var expected = Placeholders.Apply(condition.Value, values);

        if (state.TryReadInteger(output, out var actual))
        {
            if (!int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new StepException($"Condition value \"{expected}\" for {output} is not an integer");
            return condition.Op switch
            {
                "=" or "==" => actual == value,
                "!=" or "<>" => actual != value,
                "<" => actual < value,
                "<=" => actual <= value,
                ">" => actual > value,
                ">=" => actual >= value,
                _ => throw new StepException($"Unknown operator \"{condition.Op}\""),
            };
        }
        if (state.TryReadString(output, out var text))
        {
            return condition.Op switch
            {
                "=" or "==" => string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase),
                "!=" or "<>" => !string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase),
                _ => throw new StepException($"Operator \"{condition.Op}\" needs an integer output, {output} is a string"),
            };
        }
        throw new StepException($"Unknown output {output}");
    }

    private static string Describe(Condition condition, IReadOnlyDictionary<string, string> values) =>
        $"{Placeholders.Apply(condition.Output, values)} {condition.Op} {Placeholders.Apply(condition.Value, values)}";
}

/// <summary>Swallows output during a dry run; the runner's trace shows what would have been sent.</summary>
internal sealed class NullSenders : IBiosSender, IKeySender
{
    public static NullSenders Instance { get; } = new();

    public Task SendAsync(string controlId, string argument, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAsync(KeyChord chord, TimeSpan hold, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
