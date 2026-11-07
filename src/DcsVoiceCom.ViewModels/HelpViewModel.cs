using System.Collections.ObjectModel;
using Markdig;

namespace DcsVoiceCom.ViewModels;

/// <summary>
/// One help page: a Markdown file from docs/help. Its front matter gives the title and the group; the file name
/// (without .md) is its id and sets the order.
/// </summary>
public sealed record HelpTopic(string Id, string Title, string Group, string Markdown, string PlainText);

/// <summary>A row in the help topic list: a group heading, or a topic.</summary>
public sealed record HelpNavItem(string Text, HelpTopic? Topic)
{
    public bool IsGroup => Topic is null;
}

/// <summary>The help window: topics in groups, full-text search, and links between topics.</summary>
public sealed class HelpViewModel : ObservableObject
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    private string _search = "";
    private HelpTopic? _selected;

    public HelpViewModel(IEnumerable<(string FileName, string Text)> files, string version)
    {
        Topics = files
            .Where(f => f.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => Parse(f.FileName, f.Text))
            .ToList();
        Version = version;
        Refresh();
        _selected = Topics.FirstOrDefault();
    }

    public IReadOnlyList<HelpTopic> Topics { get; }

    /// <summary>The list on the left: group headings and topics, or only the matching topics while searching.</summary>
    public ObservableCollection<HelpNavItem> Items { get; } = [];

    public string VersionText => $"Version {Version}";

    public string Version { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? "")) Refresh();
        }
    }

    public HelpTopic? Selected
    {
        get => _selected;
        set
        {
            // The list clears its selection while it is filtered; keep showing the page.
            if (value is not null) SetProperty(ref _selected, value);
        }
    }

    /// <summary>
    /// Opens the topic a link points at ("03-phrases.md", "03-phrases.md#slots" or "03-phrases"). Returns false for
    /// anything else, such as a web link.
    /// </summary>
    public bool Navigate(string href)
    {
        var id = TopicId(href);
        var topic = id is null ? null : Topics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (topic is null) return false;
        Search = "";
        Selected = topic;
        return true;
    }

    /// <summary>The topic id a link names, or null for a web or mail link.</summary>
    public static string? TopicId(string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Contains("://", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = href.Split('#')[0];
        if (path.Length == 0) return null;
        var name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    /// <summary>Topics whose title or text contain every word of <paramref name="query"/> (case ignored).</summary>
    public IEnumerable<HelpTopic> Find(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Topics.Where(t => words.All(w =>
            t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || t.PlainText.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void Refresh()
    {
        Items.Clear();
        if (_search.Trim().Length > 0)
        {
            foreach (var topic in Find(_search)) Items.Add(new HelpNavItem(topic.Title, topic));
            return;
        }
        string? group = null;
        foreach (var topic in Topics)
        {
            if (topic.Group.Length > 0 && topic.Group != group) Items.Add(new HelpNavItem(topic.Group.ToUpperInvariant(), null));
            group = topic.Group;
            Items.Add(new HelpNavItem(topic.Title, topic));
        }
    }

    /// <summary>Reads the front matter (<c>title:</c>, <c>group:</c> between two <c>---</c> lines) and the body.</summary>
    public static HelpTopic Parse(string fileName, string text)
    {
        var id = Path.GetFileNameWithoutExtension(fileName);
        var title = id;
        var group = "";
        var body = text.Replace("\r\n", "\n");
        if (body.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in body[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim().Trim('"');
                    if (key == "title") title = value;
                    else if (key == "group") group = value;
                }
                var next = body.IndexOf('\n', end + 4);
                body = next < 0 ? "" : body[(next + 1)..];
            }
        }
        body = body.TrimStart('\n');
        return new HelpTopic(id, title, group, body, Markdig.Markdown.ToPlainText(body, s_pipeline));
    }
}
