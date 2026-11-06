using System.Windows;

namespace DcsVoiceCom.App;

/// <summary>Asks for one line of text, with optional suggestions in a drop-down.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial, IReadOnlyList<string>? suggestions)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Input.ItemsSource = suggestions ?? [];
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            if (Input.Template.FindName("PART_EditableTextBox", Input) is System.Windows.Controls.TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
