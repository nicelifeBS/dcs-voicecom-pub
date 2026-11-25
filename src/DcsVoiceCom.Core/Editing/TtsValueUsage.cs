using System.Text.RegularExpressions;
using DcsVoiceCom.Phrases;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Editing;

/// <summary>Where a profile's Say steps use a TTS value, and renaming it there.</summary>
public static class TtsValueUsage
{
    /// <summary>Commands whose Say text uses {name} for the live value (not for a spoken slot of that name).</summary>
    public static int CountCommands(Profile profile, string name) =>
        profile.Commands.Count(c => !HasSlot(c, name) && SayTexts(c.Steps).Any(say => Uses(say.Text, name)));

    /// <summary>Commands with a spoken slot of this name: there the spoken value wins.</summary>
    public static int CountSlotClashes(Profile profile, string name) => profile.Commands.Count(c => HasSlot(c, name));

    /// <summary>Changes {old} to {new} in every Say step that means the live value. Returns how many steps changed.</summary>
    public static int Rename(Profile profile, string oldName, string newName)
    {
        var changed = 0;
        foreach (var command in profile.Commands.Where(c => !HasSlot(c, oldName)))
        {
            foreach (var say in SayTexts(command.Steps))
            {
                var text = Rename(say.Text, oldName, newName);
                if (text == say.Text) continue;
                say.Text = text;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>{old}, {old+1} … in the text become {new}, {new+1} ….</summary>
    public static string Rename(string text, string oldName, string newName) =>
        Pattern(oldName).Replace(text, "{" + newName);

    public static bool Uses(string text, string name) => Pattern(name).IsMatch(text);

    private static Regex Pattern(string name) =>
        new(@"\{" + Regex.Escape(name) + @"(?=[}/%*+\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<SayStep> SayTexts(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case SayStep say: yield return say; break;
                case IfStep i:
                    foreach (var s in SayTexts(i.Then.Concat(i.Else))) yield return s;
                    break;
                case ForEachCharStep f:
                    foreach (var s in SayTexts(f.Steps)) yield return s;
                    break;
            }
        }
    }

    private static bool HasSlot(Command command, string name)
    {
        foreach (var spec in command.Phrases)
        {
            try
            {
                if (PhraseExpander.Expand(spec).Any(p => PhraseTemplate.Parse(p).Slots.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    return true;
            }
            catch (FormatException)
            {
                // bad phrases are reported elsewhere
            }
        }
        return false;
    }
}
