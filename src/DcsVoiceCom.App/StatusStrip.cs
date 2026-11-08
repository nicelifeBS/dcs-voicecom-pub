using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DcsVoiceCom.App;

/// <summary>
/// Lays its children out left to right; the last child sits at the right edge. When there is not enough room,
/// children with a <see cref="ShrinkOrderProperty"/> give up width, lowest order first, and are hidden once too
/// small to read. Children without an order keep their size; anything still too wide is clipped.
/// </summary>
public sealed class StatusStrip : Panel
{
    private const double MinVisible = 48;

    public static readonly DependencyProperty ShrinkOrderProperty = DependencyProperty.RegisterAttached(
        "ShrinkOrder", typeof(int), typeof(StatusStrip),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetShrinkOrder(UIElement element) => (int)element.GetValue(ShrinkOrderProperty);
    public static void SetShrinkOrder(UIElement element, int value) => element.SetValue(ShrinkOrderProperty, value);

    private double[] _widths = [];

    public StatusStrip() => ClipToBounds = true;

    protected override Size MeasureOverride(Size available)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        _widths = new double[children.Count];
        var height = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, available.Height));
            _widths[i] = children[i].DesiredSize.Width;
            height = Math.Max(height, children[i].DesiredSize.Height);
        }

        var excess = _widths.Sum() - available.Width;
        if (excess > 0)
        {
            var order = Enumerable.Range(0, children.Count)
                .Where(i => GetShrinkOrder(children[i]) > 0)
                .OrderBy(i => GetShrinkOrder(children[i]));
            foreach (var i in order)
            {
                if (excess <= 0) break;
                var width = Math.Max(0, _widths[i] - excess);
                if (width < MinVisible) width = 0;
                excess -= _widths[i] - width;
                _widths[i] = width;
                children[i].Measure(new Size(width, available.Height));
            }
        }
        var total = _widths.Sum();
        return new Size(double.IsInfinity(available.Width) ? total : Math.Min(total, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var x = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var width = i < _widths.Length ? _widths[i] : 0;
            if (i == children.Count - 1 && i > 0) x = Math.Max(x, final.Width - width);
            children[i].Arrange(new Rect(x, 0, width, final.Height));
            x += width;
        }
        return final;
    }
}

/// <summary>One line of text that drops characters from the middle ("S-TECS…button 58") when it does not fit.</summary>
public sealed class MiddleTrimText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MiddleTrimText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private FormattedText? _shown;

    private FormattedText Format(string text) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection,
        new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this)),
        TextElement.GetFontSize(this), TextElement.GetForeground(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size available)
    {
        var text = Text ?? "";
        var full = Format(text);
        _shown = full;
        if (full.WidthIncludingTrailingWhitespace > available.Width && text.Length > 2)
        {
            // Keep as many characters as fit, split between start and end.
            for (var keep = text.Length - 1; keep >= 2; keep--)
            {
                var head = (keep + 1) / 2;
                var candidate = Format(text[..head].TrimEnd() + "…" + text[^(keep - head)..].TrimStart());
                _shown = candidate;
                if (candidate.WidthIncludingTrailingWhitespace <= available.Width) break;
            }
        }
        ToolTip = ReferenceEquals(_shown, full) ? null : text;
        return new Size(Math.Min(_shown.WidthIncludingTrailingWhitespace, available.Width), _shown.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_shown != null) dc.DrawText(_shown, new Point(0, 0));
    }
}
