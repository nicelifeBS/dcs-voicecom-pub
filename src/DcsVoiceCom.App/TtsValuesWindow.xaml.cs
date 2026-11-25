using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

/// <summary>The profile's TTS values: live cockpit values Say steps speak as {name}, sliced or remapped.</summary>
public partial class TtsValuesWindow : Window
{
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public TtsValuesWindow(TtsValuesEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
        _live.Tick += (_, _) => editor.RefreshLive();
        Loaded += (_, _) => _live.Start();
        Closed += (_, _) =>
        {
            _live.Stop();
            editor.StopPreview();
        };
    }

    private TtsValuesEditorViewModel Editor => (TtsValuesEditorViewModel)DataContext;

    /// <summary>A sample character: click picks it, Shift+click widens the selection.</summary>
    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index })
            Editor.SelectCell(index, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CanSave) DialogResult = true;
    }
}
