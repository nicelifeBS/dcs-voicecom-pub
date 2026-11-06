using DcsVoiceCom.Definitions;

namespace DcsVoiceCom.Editing;

/// <summary>Finds controls by words from their identifier, description or category ("gear lev", "master arm").</summary>
public static class ControlSearch
{
    /// <summary>
    /// Every query word must appear in the control's identifier, description or category. Ranked by
    /// where the words hit (identifier start first, then identifier, description, category), then by identifier.
    /// An empty query lists everything in category order.
    /// </summary>
    public static IReadOnlyList<ControlDefinition> Search(ControlCatalog catalog, string? query, string? category = null, int limit = 200)
    {
        var words = Words(query);
        var hits = new List<(ControlDefinition Control, int Score)>();
        foreach (var control in catalog.Controls)
        {
            if (!string.IsNullOrEmpty(category) && !control.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
            var score = Score(control, words);
            if (score > 0) hits.Add((control, score));
        }
        var ordered = words.Length == 0
            ? hits.OrderBy(h => h.Control.Category, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Control.Identifier, StringComparer.OrdinalIgnoreCase)
            : hits.OrderByDescending(h => h.Score).ThenBy(h => h.Control.Identifier, StringComparer.OrdinalIgnoreCase);
        return ordered.Take(limit).Select(h => h.Control).ToList();
    }

    public static IReadOnlyList<string> Categories(ControlCatalog catalog) =>
        catalog.Controls.Select(c => c.Category).Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static int Score(ControlDefinition control, string[] words)
    {
        if (words.Length == 0) return 1;
        var id = control.Identifier.Replace('_', ' ').ToLowerInvariant();
        var description = control.Description.ToLowerInvariant();
        var category = control.Category.ToLowerInvariant();
        var score = 0;
        foreach (var word in words)
        {
            if (id.StartsWith(word, StringComparison.Ordinal)) score += 8;
            else if (id.Contains(" " + word, StringComparison.Ordinal)) score += 6;
            else if (id.Contains(word, StringComparison.Ordinal)) score += 4;
            else if (description.Contains(word, StringComparison.Ordinal)) score += 3;
            else if (category.Contains(word, StringComparison.Ordinal)) score += 1;
            else return 0;
        }
        return score;
    }

    private static string[] Words(string? query) =>
        (query ?? "").ToLowerInvariant().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
