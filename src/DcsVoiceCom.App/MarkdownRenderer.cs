using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace DcsVoiceCom.App;

/// <summary>
/// Turns a help page's Markdown into a themed FlowDocument. Supported: headings, paragraphs, bold, italic, code,
/// lists, code blocks, links, images (a paragraph holding only an image becomes a framed screenshot with its alt
/// text as caption) and quotes (shown as a tip box). Anything else shows as plain text.
/// </summary>
public sealed class MarkdownRenderer(Func<string, ImageSource?> loadImage, Action<string> openLink, Action<ImageSource, string>? openImage = null)
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas");

    public FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0, 0, 8, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            LineHeight = 20,
            TextAlignment = TextAlignment.Left,
            Foreground = Brush("Text"),
            Background = Brushes.Transparent,
        };
        foreach (var block in Markdown.Parse(markdown, s_pipeline))
            if (Block(block) is { } converted) document.Blocks.Add(converted);
        return document;
    }

    private WpfBlock? Block(MdBlock block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph when OnlyImage(paragraph) is { } image => Screenshot(image),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, new Thickness(0, 0, 0, 12)),
        ListBlock list => List(list),
        QuoteBlock quote => Tip(quote),
        MdTable table => Table(table),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        ThematicBreakBlock => new Paragraph { Margin = new Thickness(0, 4, 0, 16), BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 2) },
        LeafBlock leaf => Paragraph(leaf.Inline, new Thickness(0, 0, 0, 12)),
        _ => null,
    };

    private Paragraph Heading(HeadingBlock heading)
    {
        var paragraph = Paragraph(heading.Inline, heading.Level == 1 ? new Thickness(0, 0, 0, 8) : new Thickness(0, 10, 0, 6));
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = heading.Level switch { 1 => 20, 2 => 15, _ => 13 };
        return paragraph;
    }

    private Paragraph Paragraph(ContainerInline? inlines, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        if (inlines is not null) AddInlines(paragraph.Inlines, inlines);
        return paragraph;
    }

    private List List(ListBlock list)
    {
        var result = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(22, 0, 0, 0),
        };
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var child in item)
            {
                var block = child is ParagraphBlock p ? Paragraph(p.Inline, new Thickness(0)) : Block(child);
                if (block is not null) listItem.Blocks.Add(block);
            }
            result.ListItems.Add(listItem);
        }
        return result;
    }

    /// <summary>A quote is a tip: an amber box. "&gt; **Tip:** text" reads well on GitHub too.</summary>
    private BlockUIContainer Tip(QuoteBlock quote)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var first = true;
        foreach (var paragraph in quote.OfType<LeafBlock>())
        {
            if (!first) { text.Inlines.Add(new LineBreak()); text.Inlines.Add(new LineBreak()); }
            first = false;
            if (paragraph.Inline is not null) AddInlines(text.Inlines, paragraph.Inline);
        }
        foreach (var bold in text.Inlines.OfType<Bold>()) bold.Foreground = Brush("Accent");
        return new BlockUIContainer(new Border
        {
            Background = Brush("AccentDark"),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5a, 0x47, 0x19)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Child = text,
        }) { Margin = new Thickness(0, 2, 0, 14) };
    }

    private static Paragraph Code(LeafBlock block)
    {
        var paragraph = new Paragraph(new Run(string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()))))
        {
            FontFamily = s_mono, FontSize = 12, Background = Brush("Field"), Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = Brush("LineStrong"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12),
        };
        return paragraph;
    }

    /// <summary>
    /// A table as a Grid (FlowDocument tables can't size columns to their text): every column but the last as wide
    /// as its text, the last one takes the rest and wraps. A muted header and a line between rows.
    /// </summary>
    private BlockUIContainer Table(MdTable table)
    {
        var grid = new Grid();
        var columns = table.ColumnDefinitions.Count;
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var rowIndex = 0;
        foreach (var row in table.OfType<MdRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var column = 0;
            foreach (var cell in row.OfType<MdCell>())
            {
                var text = new TextBlock { TextWrapping = column == columns - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, LineHeight = 20 };
                if (cell.FirstOrDefault() is ParagraphBlock { Inline: { } inlines }) AddInlines(text.Inlines, inlines);
                if (row.IsHeader)
                {
                    text.FontWeight = FontWeights.SemiBold;
                    text.FontSize = 12;
                    text.Foreground = Brush("Muted");
                }
                var border = new Border
                {
                    Child = text,
                    Padding = new Thickness(0, 5, column == columns - 1 ? 0 : 20, 5),
                    BorderBrush = Brush("Line"),
                    BorderThickness = new Thickness(0, rowIndex == 0 ? 0 : 2, 0, 0),
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, Math.Min(column, columns - 1));
                grid.Children.Add(border);
                column++;
            }
            rowIndex++;
        }
        return new BlockUIContainer(grid) { Margin = new Thickness(0, 0, 0, 14) };
    }

    private static LinkInline? OnlyImage(ParagraphBlock paragraph)
    {
        if (paragraph.Inline is null) return null;
        var parts = paragraph.Inline.Where(i => i is not LineBreakInline && !(i is LiteralInline l && l.Content.IsEmptyOrWhitespace())).ToList();
        return parts is [LinkInline { IsImage: true } image] ? image : null;
    }

    /// <summary>A screenshot: framed, at most its own size, with the alt text as caption under it.</summary>
    private WpfBlock Screenshot(LinkInline image)
    {
        var source = loadImage(image.Url ?? "");
        var caption = PlainText(image);
        if (source is null)
            return new Paragraph(new Run($"[missing image: {image.Url}]")) { Foreground = Brush("Bad"), Margin = new Thickness(0, 0, 0, 12) };

        var picture = new Image { Source = source, Stretch = Stretch.Uniform, MaxWidth = source.Width, HorizontalAlignment = HorizontalAlignment.Left };
        if (openImage is not null)
        {
            picture.Cursor = System.Windows.Input.Cursors.Hand;
            picture.ToolTip = "Click to see it full size";
            picture.MouseLeftButtonUp += (_, _) => openImage(source, caption);
        }
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var panel = new StackPanel();
        panel.Children.Add(new Border
        {
            BorderBrush = Brush("Line"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
            Background = Brush("Field"), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Child = picture,
        });
        if (caption.Length > 0)
            panel.Children.Add(new TextBlock { Text = caption, Foreground = Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) });
        return new BlockUIContainer(panel) { Margin = new Thickness(0, 2, 0, 16) };
    }

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
            if (Inline(inline) is { } converted) target.Add(converted);
    }

    private WpfInline? Inline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case CodeInline code:
                return new Run(code.Content) { FontFamily = s_mono, FontSize = 12, Background = Brush("Field") };
            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");
            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                AddInlines(span.Inlines, emphasis);
                return span;
            }
            case LinkInline { IsImage: true } image:
                return loadImage(image.Url ?? "") is { } source
                    ? new InlineUIContainer(new Image { Source = source, Stretch = Stretch.None }) { BaselineAlignment = BaselineAlignment.Center }
                    : new Run(PlainText(image));
            case LinkInline link:
                return Link(link.Url ?? "", span => AddInlines(span.Inlines, link));
            case AutolinkInline auto:
                return Link(auto.Url, span => span.Inlines.Add(new Run(auto.Url)));
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return new Run(html.Tag);
            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            }
            default:
                return null;
        }
    }

    private Hyperlink Link(string url, Action<Span> fill)
    {
        var link = new Hyperlink { Foreground = Brush("Info"), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url };
        fill(link);
        link.Click += (_, _) => openLink(url);
        return link;
    }

    private static string PlainText(ContainerInline container) =>
        string.Concat(container.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
