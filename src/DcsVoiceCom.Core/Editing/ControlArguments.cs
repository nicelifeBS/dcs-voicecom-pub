using System.Globalization;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>
/// A ready-made DCS-BIOS argument for a control, with a label for its button. <see cref="Press"/>: a button press
/// (push in with <see cref="Arg"/>, then release) rather than a plain line.
/// </summary>
public sealed record ArgumentChoice(string Arg, string Label, bool Press = false);

public static class ControlArguments
{
    /// <summary>Above this many positions a set_state control gets only its ends, not one button per value.</summary>
    public const int MaxPositionButtons = 12;

    /// <summary>Label of the choice that presses and releases a two-state control.</summary>
    public const string ButtonLabel = "BUTTON";

    /// <summary>
    /// The arguments its inputs accept: each position of a set_state switch (labelled from the
    /// definition when it names them), DEC/INC for fixed steps, -/+ the suggested step for knobs,
    /// an action's own argument (usually TOGGLE), and BUTTON (press 1, release 0) for two-state controls.
    /// </summary>
    public static IReadOnlyList<ArgumentChoice> Suggest(ControlDefinition control)
    {
        var choices = new List<ArgumentChoice>();
        foreach (var input in control.Inputs)
        {
            switch (input.Interface)
            {
                case InputInterface.SetState when input.MaxValue is { } max and >= 0:
                    var values = max <= MaxPositionButtons ? Enumerable.Range(0, max + 1) : [0, max];
                    choices.AddRange(values.Select(v => new ArgumentChoice(Num(v), PositionLabel(control, v) is { } label ? $"{v} {label}" : Num(v))));
                    break;
                case InputInterface.FixedStep:
                    choices.Add(new ArgumentChoice("DEC", "DEC"));
                    choices.Add(new ArgumentChoice("INC", "INC"));
                    break;
                case InputInterface.VariableStep:
                    var step = Num(input.SuggestedStep ?? 3200);
                    choices.Add(new ArgumentChoice("-" + step, "-" + step));
                    choices.Add(new ArgumentChoice("+" + step, "+" + step));
                    break;
                case InputInterface.Action:
                    var arg = string.IsNullOrEmpty(input.Argument) ? "TOGGLE" : input.Argument;
                    choices.Add(new ArgumentChoice(arg, arg));
                    break;
            }
        }
        choices = choices.DistinctBy(c => c.Arg).ToList();
        if (control.FindInput(InputInterface.SetState) is { MaxValue: 1 })
            choices.Add(new ArgumentChoice("1", ButtonLabel, Press: true));
        return choices;
    }

    /// <summary>One step that pushes a button in with <paramref name="arg"/> and lets it out with 0 after the default hold.</summary>
    public static BiosStep Press(string control, string arg = "1") =>
        new() { Control = control, Arg = arg, PressMs = BiosStep.DefaultPressMs, Release = BiosStep.DefaultRelease };

    /// <summary>The definition's name for a position ("UP", "DN"), when it names every position.</summary>
    public static string? PositionLabel(ControlDefinition control, int value) =>
        control.Positions is { } positions && value >= 0 && value < positions.Count && positions[value].Length > 0
            ? positions[value]
            : null;

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}
