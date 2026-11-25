using System.Windows;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

/// <summary>Edits the text of one Say step, with a Play button for a preview.</summary>
public partial class SayWindow : Window
{
    public SayWindow(SayEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
        HintText.Text = SayEditorViewModel.Hint;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>Opens the TTS values window and puts the picked {name} at the cursor.</summary>
    private void InsertValue_Click(object sender, RoutedEventArgs e)
    {
        var editor = (SayEditorViewModel)DataContext;
        var caret = Math.Min(Input.CaretIndex, editor.Text.Length);
        var before = editor.Text;
        if (editor.PickValue() is not { } placeholder) return;
        // Renames may have changed the text; then add at the end.
        if (editor.Text != before) caret = editor.Text.Length;
        editor.Text = editor.Text.Insert(caret, placeholder);
        Input.Focus();
        Input.CaretIndex = caret + placeholder.Length;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
