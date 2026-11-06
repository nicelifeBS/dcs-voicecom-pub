using System.Collections.ObjectModel;
using DcsVoiceCom.Engine;

namespace DcsVoiceCom.ViewModels;

public sealed record LogEntry(DateTime Time, string Tag, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>The line as copied to the clipboard.</summary>
    public string Line => $"{TimeText}  {Tag,-7}  {Message}";
}

/// <summary>The log panel: app messages and one line per recognized push-to-talk or test.</summary>
public sealed class LogViewModel
{
    public const int MaxEntries = 500;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Add(string tag, string message)
    {
        Entries.Add(new LogEntry(DateTime.Now, tag, message));
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    /// <summary>A free-text runtime message; speech messages and errors get their own tags.</summary>
    public void AddMessage(string message)
    {
        if (message.StartsWith("[speech] ", StringComparison.Ordinal))
        {
            var text = message["[speech] ".Length..];
            Add(text.StartsWith("speech unavailable", StringComparison.OrdinalIgnoreCase) ? "ERROR" : "SPEECH", text);
        }
        else if (message.StartsWith("Error", StringComparison.Ordinal)) Add("ERROR", message);
        else Add("INFO", message);
    }

    /// <summary>HEAR for a command that ran, MISS for no match, REFUSED / CANCEL / FAIL otherwise; dry runs add their steps.</summary>
    public void AddOutcome(CommandOutcome outcome, bool dryRun = false)
    {
        var r = outcome.Recognition;
        var heard = string.IsNullOrEmpty(r.Raw) ? "(nothing heard)" : $"“{r.Raw}”";
        var match = r.Phrase is null ? "no match" : $"{r.Phrase} ({r.Score:0})";
        var values = outcome.Values.Count > 0 ? " " + string.Join(", ", outcome.Values.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        var tag = outcome.Status switch
        {
            OutcomeStatus.Completed => dryRun ? "DRY" : "HEAR",
            OutcomeStatus.NoMatch => "MISS",
            OutcomeStatus.Refused => "REFUSED",
            OutcomeStatus.Cancelled => "CANCEL",
            _ => "FAIL",
        };
        var command = outcome.Command is null ? "" : $" → {outcome.Command}";
        var why = outcome.Message is null ? "" : $" — {outcome.Message}";
        Add(tag, $"{heard} → {match}{command}{values}{why}");
        foreach (var step in outcome.Trace) Add(dryRun ? "STEP" : "SEND", step);
    }

    public void Clear() => Entries.Clear();

    /// <summary>Text of the given lines in log order (all lines when none are given), for the clipboard.</summary>
    public string Format(IEnumerable<LogEntry>? lines = null)
    {
        var chosen = lines?.ToHashSet();
        var picked = chosen is { Count: > 0 } ? Entries.Where(chosen.Contains) : Entries;
        return string.Join(Environment.NewLine, picked.Select(e => e.Line));
    }
}
