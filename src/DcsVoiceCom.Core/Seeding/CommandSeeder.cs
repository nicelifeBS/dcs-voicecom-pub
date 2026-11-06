using System.Globalization;
using System.Text.RegularExpressions;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Editing;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Seeding;

public enum SeedStatus
{
    /// <summary>Not in the profile yet.</summary>
    New,

    /// <summary>Replaces a command an earlier seeding generated (and nobody has edited since).</summary>
    Update,

    /// <summary>A command of yours (or of the common profile) already has this voice line or sends this exact input.</summary>
    Covered,
}

/// <summary>One generated command and where it came from.</summary>
public sealed record SeedCandidate(Command Command, ControlDefinition Control, string Category, SeedStatus Status, string? CoveredBy = null)
{
    public string VoiceLine => Command.Name;
}

/// <summary>What a catalog yields: the commands, and how many controls had nothing a voice command could set.</summary>
public sealed record SeedResult(IReadOnlyList<SeedCandidate> Candidates, int SkippedControls);

/// <summary>
/// Generates starter commands from an aircraft's DCS-BIOS definitions: one command per position of a
/// switch, a press for a push button, an on/off pair when the definition names no positions. Knobs,
/// dials, lights and displays are left out. Every command starts disabled and marked
/// <see cref="Command.Seeded"/>; editing or enabling one makes it the user's own, and seeding again
/// never touches it.
/// </summary>
public static partial class CommandSeeder
{
    /// <summary>Switches with more positions than this become one command with the position as a spoken parameter.</summary>
    public const int MaxNamedPositions = 12;

    /// <summary>Above this many positions a control is a dial, left to hand-made commands.</summary>
    public const int MaxSlotPositions = 40;

    public const string PushButtonVariant = "momentary_last_position";

    public static SeedResult Generate(ControlCatalog catalog, Profile profile, Profile? common = null)
    {
        var controls = catalog.Controls
            .Where(c => !c.IsDeprecated)
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var levels = controls.ToDictionary(c => c, c => IsLoneKey(c) ? NameLevel.Panel : NameLevel.Plain);
        var generated = new List<(Command Command, ControlDefinition Control)>();
        for (var round = 0; ; round++)
        {
            generated = controls.SelectMany(c => ForControl(c, ControlName(c, levels[c])).Select(cmd => (cmd, c))).ToList();
            // Two controls with one voice line: both get their crew station, then their panel, then their identifier.
            var clashing = generated
                .GroupBy(g => Key(g.Command.Name))
                .Where(g => g.Select(x => x.Control).Distinct().Count() > 1)
                .SelectMany(g => g.Select(x => x.Control))
                .Where(c => levels[c] < NameLevel.Identifier)
                .Distinct()
                .ToList();
            if (clashing.Count == 0 || round > 4) break;
            foreach (var control in clashing) levels[control]++;
        }

        var mine = profile.Commands.Where(c => !c.Seeded).Concat(common?.Commands ?? []).ToList();
        var seededNames = profile.Commands.Where(c => c.Seeded).Select(c => Key(c.Name)).ToHashSet();
        var candidates = new List<SeedCandidate>();
        var taken = new HashSet<string>();
        foreach (var (command, control) in generated)
        {
            if (!taken.Add(Key(command.Name))) continue;
            var coveredBy = mine.FirstOrDefault(m => Key(m.Name) == Key(command.Name) || SendsSameInput(m, command));
            var status = coveredBy != null ? SeedStatus.Covered
                : seededNames.Contains(Key(command.Name)) ? SeedStatus.Update
                : SeedStatus.New;
            candidates.Add(new SeedCandidate(command, control, control.Category, status, coveredBy?.Name));
        }
        var used = candidates.Select(c => c.Control).ToHashSet();
        return new SeedResult(candidates, catalog.Count - used.Count);
    }

    private enum NameLevel { Plain, Crew, Panel, Identifier }

    /// <summary>
    /// The control's spoken name at a level: "canopy jettison", "RIO canopy jettison",
    /// "cockpit mechanics RIO canopy jettison", "RIO canopy jett".
    /// </summary>
    private static string ControlName(ControlDefinition control, NameLevel level)
    {
        if (level == NameLevel.Identifier)
            return VoiceLines.ControlName(control.Identifier.Replace('_', ' ').ToLowerInvariant(), control.Identifier).Name;
        var (name, crew) = VoiceLines.ControlName(control.Description, control.Identifier, PositionCount(control));
        return level switch
        {
            NameLevel.Crew => VoiceLines.Sentence([crew ?? "", Lower(name)]),
            NameLevel.Panel => VoiceLines.Sentence([VoiceLines.CategoryName(control.Category), crew ?? "", Lower(name)]),
            _ => name,
        };
    }

    /// <summary>A key named by one short word ("3", "U", "DIR") means nothing without its panel.</summary>
    private static bool IsLoneKey(ControlDefinition control)
    {
        var key = Key(control.Description);
        return key.Length <= 4 && !key.Contains(' ');
    }

    /// <summary>
    /// Adds the candidates to the profile, replacing earlier seeded commands of the same name.
    /// Covered candidates are left out. Returns the number of commands added or replaced.
    /// </summary>
    public static int Apply(Profile profile, IEnumerable<SeedCandidate> picked)
    {
        var count = 0;
        foreach (var candidate in picked)
        {
            if (candidate.Status == SeedStatus.Covered) continue;
            var key = Key(candidate.Command.Name);
            var index = profile.Commands.FindIndex(c => c.Seeded && Key(c.Name) == key);
            if (index >= 0) profile.Commands[index] = candidate.Command;
            else profile.Commands.Add(candidate.Command);
            count++;
        }
        return count;
    }

    /// <summary>Removes every seeded command nobody has edited or enabled; returns how many.</summary>
    public static int RemoveSeeded(Profile profile) => profile.Commands.RemoveAll(c => c.Seeded);

    /// <summary>The commands one control gets; empty for knobs, dials, lights and displays.</summary>
    public static IReadOnlyList<Command> ForControl(ControlDefinition control, string? name = null)
    {
        name ??= ControlName(control, IsLoneKey(control) ? NameLevel.Panel : NameLevel.Plain);
        var critical = IsCritical(control);
        var source = $"{control.Identifier} ({control.Category}): {control.Description}";
        var setState = control.FindInput(InputInterface.SetState);
        var action = control.FindInput(InputInterface.Action);

        if (control.ApiVariant == PushButtonVariant && setState is { MaxValue: 1 })
        {
            return [Make($"Press {Lower(name)}", source, critical, ControlArguments.Press(control.Identifier))];
        }

        if (setState is { MaxValue: { } max and >= 1 })
        {
            var positions = control.Positions is { } p && p.Count == max + 1 && p.All(s => VoiceLines.Position(s).Length > 0)
                ? p.Select(VoiceLines.Position).ToList()
                : null;
            if (positions != null && positions.Distinct().Count() == positions.Count)
            {
                // Digit wheels and counters ("0".."9", "1".."8"): one command with the number spoken.
                if (positions.All(IsNumber) && positions.Select(Canonical).Distinct().Count() == positions.Count)
                {
                    var slot = positions.Select((pos, value) => Canonical(pos) == Num(value)).All(same => same)
                        ? $"{{position:0-{Num(max)}}}"
                        : "{position:" + string.Join('|', positions.Select((pos, value) => $"{Canonical(pos)}={Num(value)}")) + "}";
                    return [Make($"{name} {slot}", source, critical, SetPosition(control))];
                }
                if (max <= MaxNamedPositions)
                {
                    return positions.Select((pos, value) => Make(VoiceLines.Sentence([name, pos]), source, critical,
                        new BiosStep { Control = control.Identifier, Arg = Num(value), SkipIfAlready = true })).ToList();
                }
                if (max <= MaxSlotPositions)
                {
                    var slot = "{position:" + string.Join('|', positions.Select((pos, value) => $"{pos}={Num(value)}")) + "}";
                    return [Make($"{name} {slot}", source, critical, SetPosition(control))];
                }
                return [];
            }
            if (max == 1)
            {
                const string guess = " — DCS-BIOS names no positions: check that 1 is on";
                return
                [
                    Make(VoiceLines.Sentence([name, "on"]), source + guess, critical,
                        new BiosStep { Control = control.Identifier, Arg = "1", SkipIfAlready = true }),
                    Make(VoiceLines.Sentence([name, "off"]), source + guess, critical,
                        new BiosStep { Control = control.Identifier, Arg = "0", SkipIfAlready = true }),
                ];
            }
            if (max <= MaxSlotPositions)
                return [Make($"{name} position {{position:0-{Num(max)}}}", source + " — positions numbered from 0", critical, SetPosition(control))];
            return [];
        }

        if (action != null && control.Inputs.All(i => i.Interface == InputInterface.Action))
        {
            var arg = string.IsNullOrEmpty(action.Argument) ? "TOGGLE" : action.Argument;
            var verb = arg.Equals("TOGGLE", StringComparison.OrdinalIgnoreCase) ? "Press" : VoiceLines.Sentence([VoiceLines.Position(arg)]);
            return [Make($"{verb} {Lower(name)}", source, critical, new BiosStep { Control = control.Identifier, Arg = arg })];
        }

        return [];
    }

    /// <summary>Positions of a switch, null for push buttons and controls without set_state.</summary>
    private static int? PositionCount(ControlDefinition control) =>
        control.ApiVariant != PushButtonVariant && control.FindInput(InputInterface.SetState) is { MaxValue: { } max } ? max + 1 : null;

    private static BiosStep SetPosition(ControlDefinition control) =>
        new() { Control = control.Identifier, Arg = "{position}", SkipIfAlready = true };

    private static bool IsNumber(string position) => position.Length > 0 && position.All(char.IsDigit);

    /// <summary>"03" → "3": what the number parser makes of the spoken value.</summary>
    private static string Canonical(string number) => number.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";

    /// <summary>Jettison, ejection, fire, dumping and the like need the stricter critical threshold.</summary>
    public static bool IsCritical(ControlDefinition control) =>
        CriticalWords().IsMatch(control.Description) || CriticalWords().IsMatch(control.Identifier.Replace('_', ' '));

    private static Command Make(string voiceLine, string description, bool critical, params Step[] steps)
    {
        var command = new Command
        {
            Description = "Seeded from " + description,
            Steps = steps.ToList(),
            Critical = critical,
            Enabled = false,
            Seeded = true,
        };
        SetVoiceLine(command, voiceLine);
        return command;
    }

    /// <summary>Name and first phrase are the voice line; numbers also get a spelled-out alternative ("UFC 1" / "UFC one").</summary>
    private static void SetVoiceLine(Command command, string voiceLine)
    {
        command.Name = voiceLine;
        command.Phrases = [voiceLine];
        if (!voiceLine.Contains('{') && VoiceLines.NumbersAsWords(voiceLine) is { } words) command.Phrases.Add(words);
    }

    private static bool SendsSameInput(Command mine, Command seeded)
    {
        if (seeded.Steps.FirstOrDefault() is not BiosStep first) return false;
        return Flatten(mine.Steps).OfType<BiosStep>().Any(b =>
            b.Control.Equals(first.Control, StringComparison.OrdinalIgnoreCase) &&
            b.Arg.Equals(first.Arg, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Step> Flatten(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            var inner = step switch
            {
                IfStep i => i.Then.Concat(i.Else),
                ForEachCharStep f => f.Steps,
                _ => [],
            };
            foreach (var s in Flatten(inner)) yield return s;
        }
    }

    private static string Key(string text) => PhraseText.Normalize(text);

    /// <summary>Lower-cases the first letter unless the first word is an acronym: "Master arm" → "master arm", "UFC 1" stays.</summary>
    private static string Lower(string line)
    {
        var first = line.Split(' ')[0];
        return first.Any(char.IsLetter) && first.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? line : char.ToLowerInvariant(line[0]) + line[1..];
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b(jett\w*|eject\w*|fire|extinguish\w*|dump|self ?destruct|cut ?off|arrest\w*|emergency|emer|master arm|canopy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CriticalWords();
}
