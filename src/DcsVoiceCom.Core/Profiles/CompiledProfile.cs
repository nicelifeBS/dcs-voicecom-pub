using DcsVoiceCom.Definitions;
using DcsVoiceCom.Input;
using DcsVoiceCom.Phrases;

namespace DcsVoiceCom.Profiles;

public sealed record PhraseEntry(PhraseTemplate Template, Command Command);

/// <summary>
/// A profile ready for recognition: every enabled command's phrase specs expanded into
/// normalized phrases, indexed by the exact text the matcher returns on a match.
/// </summary>
public sealed class CompiledProfile
{
    private readonly Dictionary<string, PhraseEntry> _byPhrase;

    private CompiledProfile(Profile profile, Dictionary<string, PhraseEntry> byPhrase, IReadOnlyList<string> problems)
    {
        Profile = profile;
        _byPhrase = byPhrase;
        Problems = problems;
    }

    public static CompiledProfile Empty { get; } = Compile(new Profile());

    public Profile Profile { get; }

    /// <summary>Phrase texts for the matcher.</summary>
    public IReadOnlyCollection<string> Phrases => _byPhrase.Keys;

    /// <summary>Each phrase with its command's name, so the matcher never ranks a command's phrases against each other.</summary>
    public IEnumerable<(string Phrase, string Command)> Entries => _byPhrase.Select(p => (p.Key, p.Value.Command.Name));

    public IReadOnlyList<string> Keywords => Profile.Keywords;

    /// <summary>Bad phrase syntax, duplicate phrases, unknown keys: shown in the UI and log, never fatal.</summary>
    public IReadOnlyList<string> Problems { get; }

    public PhraseEntry? Find(string phrase) => _byPhrase.GetValueOrDefault(phrase);

    public static CompiledProfile Compile(Profile profile)
    {
        var byPhrase = new Dictionary<string, PhraseEntry>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var command in profile.Commands.Where(c => c.Enabled))
        {
            foreach (var spec in command.Phrases)
            {
                IReadOnlyList<string> expanded;
                try
                {
                    expanded = PhraseExpander.Expand(spec);
                }
                catch (FormatException ex)
                {
                    problems.Add($"{command.Name}: {ex.Message}");
                    continue;
                }

                foreach (var phrase in expanded)
                {
                    PhraseTemplate template;
                    try
                    {
                        template = PhraseTemplate.Parse(phrase);
                    }
                    catch (FormatException ex)
                    {
                        problems.Add($"{command.Name}: {ex.Message}");
                        continue;
                    }
                    if (!template.Segments.Any(seg => seg is LiteralSegment))
                    {
                        problems.Add($"{command.Name}: phrase \"{template.Text}\" needs at least one word besides slots");
                        continue;
                    }
                    if (byPhrase.TryGetValue(template.Text, out var existing))
                    {
                        if (existing.Command != command)
                            problems.Add($"Phrase \"{template.Text}\" is used by both \"{existing.Command.Name}\" and \"{command.Name}\"; keeping the first");
                        continue;
                    }
                    byPhrase[template.Text] = new PhraseEntry(template, command);
                }
            }
            CheckSteps(command, command.Steps, problems);
        }
        return new CompiledProfile(profile, byPhrase, problems);
    }

    private static void CheckSteps(Command command, IEnumerable<Step> steps, List<string> problems)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case KeyStep key when !key.Keys.Contains('{'):
                    try { KeyChord.Parse(key.Keys); }
                    catch (FormatException ex) { problems.Add($"{command.Name}: {ex.Message}"); }
                    break;
                case BiosStep { IsPress: true } press:
                    if (press.PressMs < 0) problems.Add($"{command.Name}: press {press.Control} hold time must be 0 ms or more");
                    if (press.Arg.Trim().Equals(press.ReleaseArg.Trim(), StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{command.Name}: press {press.Control} needs different in and out values (both {press.Arg})");
                    break;
                case IfStep ifStep:
                    CheckSteps(command, ifStep.Then, problems);
                    CheckSteps(command, ifStep.Else, problems);
                    break;
                case ForEachCharStep each:
                    CheckSteps(command, each.Steps, problems);
                    break;
                case EnterValueStep enter:
                    CheckEnterValue(command, enter, problems);
                    break;
            }
        }
    }

    private static void CheckEnterValue(Command command, EnterValueStep step, List<string> problems)
    {
        var where = $"{command.Name}: enter value";
        if (step.Value.Trim().Length == 0) problems.Add($"{where} needs a value, e.g. {{freq}}");
        if (step.Fields is { Count: > 0 })
        {
            problems.AddRange(Engine.DisplayLayout.Validate(step).Select(p => $"{where}: {p}"));
            return;
        }
        if (step.Method == EnterMethod.Keypad)
        {
            if (step.Keys is not { Count: > 0 }) problems.Add($"{where} has no keys");
            return;
        }
        if (step.Wheels is not { Count: > 0 }) problems.Add($"{where} has no wheels");
        foreach (var wheel in step.Wheels ?? [])
        {
            if (wheel.Control.Trim().Length == 0) problems.Add($"{where}: a wheel has no control");
            if (wheel.Place <= 0) problems.Add($"{where}: {wheel.Control} place must be above 0");
            if (wheel.Width is < 1 or > 6) problems.Add($"{where}: {wheel.Control} width must be 1-6");
            if (wheel.Mode != WheelMode.Set && string.IsNullOrWhiteSpace(wheel.Read))
                problems.Add($"{where}: {wheel.Control} needs a read-back output");
        }
    }

    /// <summary>Control and output IDs the profile uses that the loaded DCS-BIOS definitions don't have.</summary>
    public static IReadOnlyList<string> FindMissingControls(Profile profile, ControlCatalog catalog)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(string? id)
        {
            if (!string.IsNullOrEmpty(id) && !id.Contains('{') && !catalog.Contains(id)) missing.Add(id);
        }
        void Walk(IEnumerable<Step> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case BiosStep b: Check(b.Control); break;
                    case BiosUntilStep u: Check(u.Control); Check(u.Output); break;
                    case WaitForStep w: Check(w.Until.Output); break;
                    case IfStep i: Check(i.Condition.Output); Walk(i.Then); Walk(i.Else); break;
                    case ForEachCharStep f: Walk(f.Steps); break;
                    case EnterValueStep e:
                        foreach (var w in e.Wheels ?? []) { Check(w.Control); Check(w.Read); }
                        foreach (var f in e.Fields ?? []) Check(f.Control);
                        Check(e.Display);
                        foreach (var k in (e.Keys?.Values.AsEnumerable() ?? []).Concat(e.Before ?? []).Concat(e.After ?? [])) Check(k);
                        Check(e.Verify?.Output);
                        break;
                }
            }
        }
        foreach (var command in profile.Commands) Walk(command.Steps);
        return missing.ToList();
    }
}
