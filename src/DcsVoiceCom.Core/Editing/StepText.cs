using System.Globalization;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>One-line descriptions of steps for the command list and editor.</summary>
public static class StepText
{
    public static string Describe(Step step) => step switch
    {
        BiosStep { IsPress: true } b => $"press {b.Control} {b.Arg}/{b.ReleaseArg} ({b.PressMs!.Value.ToString(CultureInfo.InvariantCulture)} ms)",
        BiosStep b => $"{b.Control} {b.Arg}",
        BiosUntilStep u => $"turn {u.Control} until {u.Output ?? u.Control} = {u.Target}",
        KeyStep k => $"key {k.Keys}" + (k.HoldMs is { } hold ? $" ({hold} ms)" : ""),
        WaitStep w => $"wait {w.Ms.ToString(CultureInfo.InvariantCulture)} ms",
        WaitForStep f => $"wait for {f.Until} ({(f.TimeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s)",
        IfStep i => $"if {i.Condition}: {Count(i.Then)}" + (i.Else.Count > 0 ? $", else {Count(i.Else)}" : ""),
        CueStep c => $"cue {c.Cue.ToString().ToLowerInvariant()}",
        EnterValueStep v => Engine.EnterValue.Describe(v),
        ForEachCharStep e => $"for each character of {e.Value}: {Count(e.Steps)}",
        _ => step.Type,
    };

    /// <summary>"GEAR_LEVER 0" for one step, "PLT_MASTER_ARM_COVER 1, PLT_MASTER_ARM_SW 0" for two, "… +3 more" beyond.</summary>
    public static string Summarize(IReadOnlyList<Step> steps, int shown = 2)
    {
        if (steps.Count == 0) return "no steps";
        var text = string.Join(", ", steps.Take(shown).Select(Describe));
        return steps.Count > shown ? $"{text} +{steps.Count - shown} more" : text;
    }

    private static string Count(List<Step> steps) => steps.Count == 1 ? "1 step" : $"{steps.Count} steps";
}
