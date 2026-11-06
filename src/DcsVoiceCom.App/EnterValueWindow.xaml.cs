using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

/// <summary>Edits one "enter value" step: the read-back display cell by cell, its fields, keys and a preview.</summary>
public partial class EnterValueWindow : Window
{
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public EnterValueWindow(EnterValueEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = $"Enter value — {editor.Value}";
        _live.Tick += (_, _) => editor.RefreshLive();
        Loaded += (_, _) => _live.Start();
        Closed += (_, _) => _live.Stop();
    }

    private EnterValueEditorViewModel Editor => (EnterValueEditorViewModel)DataContext;

    /// <summary>A display character: click picks it, Shift+click widens the selection.</summary>
    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index })
            Editor.PickCell(index, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.ConfirmSave()) DialogResult = true;
    }
}
