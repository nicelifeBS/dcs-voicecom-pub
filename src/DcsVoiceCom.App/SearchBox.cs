using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DcsVoiceCom.App;

/// <summary>
/// A text box with a list of suggestions under it. Typing "arc 100k" lists only the items that contain every word
/// (underscores count as spaces, case is ignored). Only the text box changes <see cref="Text"/> while typing; the
/// list changes it only when an item is picked (click, or ↑/↓ then Enter), so filtering can't fight the typing.
/// </summary>
public sealed class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(SearchBox), new PropertyMetadata(null));

    private const int MaxShown = 1000;

    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private bool _syncing;

    public SearchBox()
    {
        _box = new TextBox { Margin = new Thickness(0) };
        _box.SetResourceReference(StyleProperty, "MonoBox");
        _box.Loaded += (_, _) => _box.Padding = new Thickness(_box.Padding.Left, _box.Padding.Top, 24, _box.Padding.Bottom); // room for the arrow
        _box.TextChanged += (_, _) => OnTyped();
        _box.PreviewKeyDown += OnKey;
        _box.LostKeyboardFocus += (_, _) => _popup!.IsOpen = false;

        var chevron = new Path
        {
            Data = Geometry.Parse("M 0 0 L 4 4 L 8 0"), StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(Shape.StrokeProperty, "Muted");
        var arrow = new Button
        {
            Width = 26, Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Arrow,
            ToolTip = "Show the list", Content = chevron,
        };
        // A transparent border, so the whole strip is clickable and not just the chevron's line.
        var hit = new FrameworkElementFactory(typeof(Border));
        hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hit.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        arrow.Template = new ControlTemplate(typeof(Button)) { VisualTree = hit };
        arrow.Click += (_, _) =>
        {
            if (_popup!.IsOpen) _popup.IsOpen = false;
            else
            {
                Filter(showAll: true);
                _box.Focus();
            }
        };

        _list = new ListBox { MaxHeight = 320, Focusable = false, ItemContainerStyle = RowStyle() };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        // Pick on mouse-down, before the click can move focus out of the text box (which closes the list).
        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is not ListBoxItem item) return;
            Pick(item.Content);
            e.Handled = true;
        };

        var frame = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Child = _list };
        frame.SetResourceReference(Border.BackgroundProperty, "Raised");
        frame.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        _popup = new Popup
        {
            PlacementTarget = _box, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true,
            Focusable = false, Child = frame,
        };
        frame.SetBinding(MinWidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = _box });

        var grid = new Grid();
        grid.Children.Add(_box);
        grid.Children.Add(arrow);
        grid.Children.Add(_popup);
        Content = grid;

        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window) window.Deactivated += (_, _) => _popup.IsOpen = false;
        };
    }

    /// <summary>Plain rows: the theme's list rows are cards, too big for a dropdown. Hover and ↑/↓ highlight.</summary>
    private static Style RowStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        bd.AppendChild(text);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };
        foreach (var property in new[] { IsMouseOverProperty, ListBoxItem.IsSelectedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Selected"), "Bd"));
            template.Triggers.Add(trigger);
        }
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Setters.Add(new Setter(FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas")));
        style.Setters.Add(new Setter(FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Text")));
        return style;
    }

    /// <summary>The typed or picked text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The suggestions (control identifiers).</summary>
    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>True when every word of the search appears in the item ("arc 100k" matches ARC210_100KHZ_SEL).</summary>
    public static bool Matches(string item, string[] words)
    {
        var text = item.Replace('_', ' ');
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SearchBox)d;
        var text = (string?)e.NewValue ?? "";
        if (box._box.Text == text) return;
        box._syncing = true;
        box._box.Text = text;
        box._box.CaretIndex = text.Length;
        box._syncing = false;
    }

    private void OnTyped()
    {
        if (_syncing) return;
        SetCurrentValue(TextProperty, _box.Text);
        if (_box.IsKeyboardFocusWithin) Filter(showAll: false);
    }

    /// <summary>Fills the list with the matching items and opens it, or closes it when nothing matches.</summary>
    private void Filter(bool showAll)
    {
        var words = showAll ? [] : _box.Text.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = (Items ?? Array.Empty<object>()).Cast<object>()
            .Where(i => Matches(i?.ToString() ?? "", words))
            .Take(MaxShown)
            .ToList();
        // A list holding only what is already typed has nothing to offer.
        var nothingNew = matches.Count == 1 && string.Equals(matches[0]?.ToString(), _box.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        _list.ItemsSource = matches;
        _list.SelectedIndex = -1;
        _popup.IsOpen = matches.Count > 0 && !nothingNew;
        if (!_popup.IsOpen) return;
        // Opened from the arrow: show where the current value is.
        var current = showAll ? matches.FindIndex(m => string.Equals(m?.ToString(), _box.Text.Trim(), StringComparison.OrdinalIgnoreCase)) : -1;
        _list.SelectedIndex = current;
        var target = matches[Math.Max(current, 0)];
        _list.ScrollIntoView(target);
        // A list that just opened isn't laid out yet; scroll again once it is.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _list.ScrollIntoView(target));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                if (!_popup.IsOpen) Filter(showAll: _box.Text.Trim().Length == 0);
                if (_list.Items.Count == 0) return;
                var step = e.Key == Key.Down ? 1 : -1;
                _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when _popup.IsOpen && _list.SelectedItem != null:
                Pick(_list.SelectedItem);
                e.Handled = e.Key == Key.Enter;
                break;
            case Key.Escape when _popup.IsOpen:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Pick(object? item)
    {
        var text = item?.ToString() ?? "";
        _popup.IsOpen = false;
        _syncing = true;
        _box.Text = text;
        _box.CaretIndex = text.Length;
        _syncing = false;
        SetCurrentValue(TextProperty, text);
        // Push the pick to the bound property now, even when the binding updates on lost focus.
        GetBindingExpression(TextProperty)?.UpdateSource();
    }
}
