using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.ImageSource;

        // Keep the newest log line in view: on every new line, and when the panel opens (a hidden list doesn't scroll).
        // Deferred: during the Add notification the list hasn't laid out the item yet.
        ((INotifyCollectionChanged)vm.Log.Entries).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) ScrollLogToEnd();
        };
        LogList.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) ScrollLogToEnd();
        };

        // Status bar and live control values.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => vm.Tick(), Dispatcher);
        _timer.Start();
    }

    /// <summary>
    /// Scrolls the log to its last line. ScrollToEnd, not ScrollIntoView: with wrapped (taller) lines ScrollIntoView
    /// can stop short of the bottom.
    /// </summary>
    private void ScrollLogToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!LogList.IsVisible) return;
            if (FindScrollViewer(LogList) is { } scroller) scroller.ScrollToEnd();
            else if (_vm.Log.Entries.Count > 0) LogList.ScrollIntoView(_vm.Log.Entries[^1]);
        });

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found) return found;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Opens the Settings dialog; set by the app.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>True once the user chose Quit (tray) or confirmed closing: the window really closes.</summary>
    public bool AllowClose { get; set; }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();

    /// <summary>Opens (or brings forward) the Help window; set by the app.</summary>
    public Action? OpenHelp { get; set; }

    private void Help_Executed(object sender, ExecutedRoutedEventArgs e) => OpenHelp?.Invoke();

    /// <summary>Ctrl+C or Copy in the log: the selected lines, or all of them when none is selected.</summary>
    private void LogCopy_Executed(object sender, ExecutedRoutedEventArgs e) =>
        CopyText(_vm.Log.Format(LogList.SelectedItems.OfType<LogEntry>()));

    private void LogCopyAll_Click(object sender, RoutedEventArgs e) => CopyText(_vm.Log.Format());

    private static void CopyText(string text)
    {
        if (text.Length == 0) return;
        // Another app can hold the clipboard for a moment; retry briefly instead of failing.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Ctrl+S saves, after committing the field being typed in (some only commit on leaving them).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CommitFocusedField();
            _ = _vm.SaveCommand.ExecuteAsync();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private static void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void TestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = _vm.TestCommand.ExecuteAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && !_vm.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        _timer.Stop();
        base.OnClosing(e);
    }
}
