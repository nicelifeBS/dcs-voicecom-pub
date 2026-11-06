using System.Windows;
using System.Windows.Controls;

namespace DcsVoiceCom.App;

/// <summary>A themed replacement for MessageBox: a message and a row of buttons.</summary>
public partial class MessageWindow : Window
{
    private MessageWindow(string title, string message, MessageBoxButton buttons)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                Add("Cancel", MessageBoxResult.Cancel, isCancel: true);
                Add("OK", MessageBoxResult.OK, isDefault: true);
                break;
            case MessageBoxButton.YesNo:
                Add("No", MessageBoxResult.No, isCancel: true);
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Cancel", MessageBoxResult.Cancel, isCancel: true);
                Add("No", MessageBoxResult.No);
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                break;
        }
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public static MessageBoxResult Show(Window? owner, string title, string message, MessageBoxButton buttons)
    {
        var window = new MessageWindow(title, message, buttons);
        if (owner != null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        // Closing with the title bar counts as the cancel button.
        return window.Result != MessageBoxResult.None ? window.Result
            : buttons switch
            {
                MessageBoxButton.YesNo => MessageBoxResult.No,
                MessageBoxButton.OK => MessageBoxResult.OK,
                _ => MessageBoxResult.Cancel,
            };
    }

    private void Add(string text, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text, MinWidth = 80, IsDefault = isDefault, IsCancel = isCancel,
            Margin = new Thickness(Buttons.Children.Count == 0 ? 0 : 8, 0, 0, 0),
        };
        if (isDefault) button.Style = (Style)FindResource("Primary");
        button.Click += (_, _) =>
        {
            Result = result;
            Close();
        };
        Buttons.Children.Add(button);
    }
}
