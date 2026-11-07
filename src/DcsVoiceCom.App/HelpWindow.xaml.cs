using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

/// <summary>
/// The help window: the topics from docs/help on the left, the selected page on the right. It does not block the
/// main window. The view model outlives the window, so reopening shows the last topic.
/// </summary>
public partial class HelpWindow : Window
{
    public static readonly DependencyProperty HasNoMatchProperty =
        DependencyProperty.Register(nameof(HasNoMatch), typeof(bool), typeof(HelpWindow), new PropertyMetadata(false));

    private readonly HelpViewModel _vm;
    private readonly MarkdownRenderer _renderer;
    private bool _syncing;

    public HelpWindow(HelpViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.ImageSource;
        _renderer = new MarkdownRenderer(HelpContent.Image, OpenLink, ShowImage);
        vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnViewModelChanged;
        ShowPage();
        SyncList();
    }

    public bool HasNoMatch
    {
        get => (bool)GetValue(HasNoMatchProperty);
        set => SetValue(HasNoMatchProperty, value);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HelpViewModel.Selected))
        {
            ShowPage();
            SyncList();
        }
        else if (e.PropertyName == nameof(HelpViewModel.Items))
        {
            SyncList();
        }
    }

    private void ShowPage()
    {
        if (_vm.Selected is { } topic) Page.Document = _renderer.Render(topic.Markdown);
    }

    /// <summary>Selects the shown topic in the list (when it is listed) and updates the "no match" hint.</summary>
    private void SyncList()
    {
        HasNoMatch = _vm.Items.Count == 0;
        _syncing = true;
        TopicList.SelectedItem = _vm.Items.FirstOrDefault(i => i.Topic == _vm.Selected);
        _syncing = false;
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || TopicList.SelectedItem is not HelpNavItem { Topic: { } topic }) return;
        _vm.Selected = topic;
    }

    /// <summary>A link to another page opens it; a web link opens the browser.</summary>
    private void OpenLink(string url)
    {
        if (_vm.Navigate(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser: nothing to do.
        }
    }

    /// <summary>A screenshot at full size, in a window of its own; Esc or a click closes it.</summary>
    private void ShowImage(ImageSource image, string caption)
    {
        var viewer = new Window
        {
            Title = caption.Length > 0 ? caption : "Screenshot",
            Owner = this,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = SystemParameters.WorkArea.Width,
            MaxHeight = SystemParameters.WorkArea.Height,
            Icon = Icon,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image { Source = image, Stretch = Stretch.None, Margin = new Thickness(12), Cursor = Cursors.Hand },
            },
        };
        viewer.SetResourceReference(StyleProperty, typeof(Window));
        viewer.KeyDown += (_, e) => { if (e.Key == Key.Escape) viewer.Close(); };
        viewer.MouseLeftButtonUp += (_, _) => viewer.Close();
        viewer.Show();
    }

    private void Close_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
}
