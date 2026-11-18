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

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
