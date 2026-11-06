using System.Globalization;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>
/// Turns an older <see cref="EnterValueStep"/> (wheels with place, width and read-back outputs) into the display
/// layout (<see cref="EnterValueStep.Fields"/>). The editor runs it when it opens an old step; the engine still runs
/// old steps as they are.
/// </summary>
public static class EnterValueUpgrade
{
    /// <summary>
    /// The step in the display layout, or null with the <paramref name="reason"/> it can't be converted.
    /// <paramref name="liveText"/> is the display's text right now (null without DCS); without it the layout is
    /// guessed from the step's max value and the display's length in DCS-BIOS, and <paramref name="reason"/> is a
    /// note to check the fields once DCS runs.
    /// </summary>
    public static EnterValueStep? TryUpgrade(EnterValueStep step, ControlCatalog? catalog, string? liveText, out string? reason)
    {
        reason = null;
        if (step.Fields is { Count: > 0 }) return step;
        if (step.Method == EnterMethod.Keypad)
        {
            // Where the typed text sits on the display can only be seen live; the user picks the field.
            reason = "Pick the keypad field on the live display (needs DCS).";
            return null;
        }

        var wheels = step.Wheels ?? [];
        if (wheels.Count == 0)
        {
            reason = "The step has no wheels.";
            return null;
        }
        var display = DisplayOf(step, catalog);
        if (display is null)
        {
            reason = "The step has no display to read the wheels from. Pick one.";
            return null;
        }

        var guessed = string.IsNullOrEmpty(liveText);
        var sample = guessed ? GuessSample(step, catalog, display) : liveText;
        if (sample is null)
        {
            reason = "The display's layout is unknown without DCS (the step has no max value).";
            return null;
        }

        var fields = PlaceWheels(sample, wheels, out reason);
        if (fields is null) return null;
        foreach (var (wheel, field) in wheels.Zip(fields))
        {
            var (low, high) = EnterValue.Range(step, wheel);
            field.Range = $"{low}-{high}";
        }

        // The decimal point between the wheels is text that never changes.
        var start = fields.Min(f => f.At);
        var end = fields.Max(f => f.At + f.Len);
        var point = sample.IndexOf('.', start);
        if (point >= 0 && point < end && !fields.Any(f => point >= f.At && point < f.At + f.Len))
        {
            fields.Add(new DisplayField { At = point, Len = 1, Kind = FieldKind.ReadOnly });
        }

        if (guessed) reason = "The layout was guessed without DCS. Check the fields once DCS runs.";
        return new EnterValueStep
        {
            Value = step.Value,
            Method = EnterMethod.Wheels,
            Display = display,
            Sample = sample,
            Fields = [.. fields.OrderBy(f => f.At)],
            TimeoutMs = step.Verify?.TimeoutMs,
            Up = step.Up,
            Down = step.Down,
            MaxSteps = step.MaxSteps,
            DelayMs = step.DelayMs,
        };
    }

    /// <summary>
    /// Where wheels with a place and width sit on <paramref name="sample"/>, by lining up the decimal point
    /// (or the end of the number when there is none): place 10, width 2 on "251.000" → at 0, len 2.
    /// Null with a <paramref name="reason"/> when a wheel falls off the display.
    /// </summary>
    public static List<DisplayField>? PlaceWheels(string sample, IReadOnlyList<Wheel> wheels, out string? reason)
    {
        reason = null;
        var point = sample.IndexOf('.');
        if (point < 0) point = sample.TrimEnd().Length;
        var fields = new List<DisplayField>();
        foreach (var wheel in wheels)
        {
            if (wheel.Place <= 0 || wheel.Width < 1)
            {
                reason = $"{wheel.Control} has no valid place and width.";
                return null;
            }
            var power = (int)Math.Round(Math.Log10((double)wheel.Place));
            var last = power >= 0 ? point - 1 - power : point - power;
            var at = last - wheel.Width + 1;
            if (at < 0 || last >= sample.Length)
            {
                reason = $"{wheel.Control} (place {wheel.Place.ToString(CultureInfo.InvariantCulture)}) does not fit on \"{sample}\".";
                return null;
            }
            fields.Add(new DisplayField
            {
                At = at,
                Len = wheel.Width,
                Kind = FieldKind.Wheel,
                Control = wheel.Control,
                Mode = wheel.Mode,
                Flip = wheel.Flip,
            });
        }
        return fields;
    }

    /// <summary>The display to read: the old check display, else a wheel's read-back that is a string output.</summary>
    private static string? DisplayOf(EnterValueStep step, ControlCatalog? catalog)
    {
        if (step.Verify is { Output.Length: > 0 } verify) return verify.Output;
        return (step.Wheels ?? [])
            .Select(w => w.Read)
            .FirstOrDefault(r => !string.IsNullOrEmpty(r) && StringOutput(catalog, r!) is not null);
    }

    private static OutputDefinition? StringOutput(ControlCatalog? catalog, string id) =>
        catalog?.Find(id)?.Outputs.FirstOrDefault(o => o.IsString);

    /// <summary>The max value with as many decimals as the smallest wheel, right-aligned to the display's length: "399.975".</summary>
    private static string? GuessSample(EnterValueStep step, ControlCatalog? catalog, string display)
    {
        if (step.Max is not { } max || max < 0) return null;
        var smallest = (step.Wheels ?? []).Where(w => w.Place > 0).Select(w => w.Place).DefaultIfEmpty(1).Min();
        var decimals = smallest < 1 ? (int)Math.Round(-Math.Log10((double)smallest)) : 0;
        var text = max.ToString("F" + decimals, CultureInfo.InvariantCulture);
        var length = StringOutput(catalog, display)?.MaxLength ?? text.Length;
        return text.PadLeft(Math.Max(length, text.Length));
    }
}
