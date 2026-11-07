using System.Text.RegularExpressions;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.ViewModels.Tests;

public class HelpTests
{
    private static HelpViewModel Sample() => new(
    [
        ("02-phrases.md", "---\ntitle: Writing phrases\ngroup: Commands\n---\n# Writing phrases\n\nUse `{freq}` for a frequency. See [start](01-start.md).\n"),
        ("01-start.md", "---\ntitle: First steps\ngroup: Start\n---\nBind the push-to-talk button.\n"),
        ("03-keys.md", "---\ntitle: Shortcuts\ngroup: Reference\n---\n| Key | Does |\n|---|---|\n| F1 | Help |\n"),
        ("images/x.png", "not a page"),
    ], "1.4.0");

    [Fact]
    public void Reads_front_matter_and_strips_it_from_the_page()
    {
        var topic = HelpViewModel.Parse("05-seed.md", "---\r\ntitle: \"Seeding\"\r\ngroup: Commands\r\n---\r\n\r\n# Seeding\r\n");

        Assert.Equal("05-seed", topic.Id);
        Assert.Equal("Seeding", topic.Title);
        Assert.Equal("Commands", topic.Group);
        Assert.Equal("# Seeding\n", topic.Markdown);
    }

    [Fact]
    public void Without_front_matter_the_file_name_is_the_title()
    {
        var topic = HelpViewModel.Parse("notes.md", "Just text.");

        Assert.Equal("notes", topic.Title);
        Assert.Equal("", topic.Group);
        Assert.Equal("Just text.", topic.Markdown);
    }

    [Fact]
    public void Lists_topics_by_file_name_under_their_group_and_opens_the_first()
    {
        var help = Sample();

        Assert.Equal(["START", "First steps", "COMMANDS", "Writing phrases", "REFERENCE", "Shortcuts"], help.Items.Select(i => i.Text));
        Assert.True(help.Items[0].IsGroup);
        Assert.Equal("01-start", help.Selected?.Id);
        Assert.Equal("Version 1.4.0", help.VersionText);
    }

    [Fact]
    public void Search_matches_every_word_in_the_text_and_hides_the_groups()
    {
        var help = Sample();

        help.Search = "freq";
        Assert.Equal(["Writing phrases"], help.Items.Select(i => i.Text));

        help.Search = "push button";
        Assert.Equal(["First steps"], help.Items.Select(i => i.Text));

        help.Search = "F1 help";
        Assert.Equal(["Shortcuts"], help.Items.Select(i => i.Text));

        help.Search = "nothing like this";
        Assert.Empty(help.Items);
        Assert.Equal("01-start", help.Selected?.Id); // the page stays

        help.Search = "";
        Assert.Equal(6, help.Items.Count);
    }

    [Fact]
    public void Links_to_other_pages_open_them_and_clear_the_search()
    {
        var help = Sample();
        help.Search = "freq";

        Assert.True(help.Navigate("03-keys.md#f1"));
        Assert.Equal("03-keys", help.Selected?.Id);
        Assert.Equal("", help.Search);

        Assert.True(help.Navigate("02-phrases"));
        Assert.Equal("02-phrases", help.Selected?.Id);
        Assert.False(help.Navigate("https://example.com/03-keys.md"));
        Assert.False(help.Navigate("99-missing.md"));
        Assert.Equal("02-phrases", help.Selected?.Id);
    }

    /// <summary>The real pages in docs/help: front matter, and every image and page they link to exists.</summary>
    [Fact]
    public void Shipped_help_pages_are_complete()
    {
        var folder = Path.Combine(RepoRoot(), "docs", "help");
        var files = Directory.GetFiles(folder, "*.md").Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToList();
        var help = new HelpViewModel(files, "test");

        Assert.NotEmpty(help.Topics);
        foreach (var topic in help.Topics)
        {
            Assert.False(topic.Title == topic.Id, $"{topic.Id}.md has no title");
            Assert.False(topic.Group.Length == 0, $"{topic.Id}.md has no group");
            foreach (Match link in Regex.Matches(topic.Markdown, @"(!?)\[[^\]]*\]\(([^)\s]+)\)"))
            {
                var href = link.Groups[2].Value;
                if (href.Contains("://")) continue;
                if (link.Groups[1].Value == "!")
                    Assert.True(File.Exists(Path.Combine(folder, href)), $"{topic.Id}.md shows a missing image: {href}");
                else
                    Assert.True(help.Topics.Any(t => t.Id == HelpViewModel.TopicId(href)), $"{topic.Id}.md links to a missing page: {href}");
            }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DcsVoiceCom.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root (DcsVoiceCom.sln) not found");
    }
}
