using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DcsVoiceCom.App;

/// <summary>The app icon, for windows (WPF) and the tray (WinForms).</summary>
internal static class AppIcon
{
    public static ImageSource ImageSource { get; } =
        BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));

    public static System.Drawing.Icon TrayIcon()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico")
            ?? throw new InvalidOperationException("app.ico resource missing");
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
