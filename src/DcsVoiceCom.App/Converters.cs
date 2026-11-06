using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DcsVoiceCom.App;

/// <summary>true → Visible; with ConverterParameter "invert", true → Collapsed.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is true;
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible;
}

/// <summary>Non-empty string or non-zero count → Visible; "invert" as parameter flips it.</summary>
public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            string s => s.Length > 0,
            int n => n > 0,
            null => false,
            _ => true,
        } != (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Log tag → colour, as in the mockup: PTT blue, HEAR plain, SEND green, MISS orange.</summary>
public sealed class TagToBrush : IValueConverter
{
    private static readonly Dictionary<string, Brush> Brushes = new()
    {
        ["PTT"] = Freeze("#8ab4f8"),
        ["HEAR"] = Freeze("#e4e7ea"),
        ["DRY"] = Freeze("#e4e7ea"),
        ["SEND"] = Freeze("#5fd38a"),
        ["STEP"] = Freeze("#5fd38a"),
        ["MISS"] = Freeze("#ff8a65"),
        ["REFUSED"] = Freeze("#ff8a65"),
        ["FAIL"] = Freeze("#ff8a65"),
        ["ERROR"] = Freeze("#ff8a65"),
        ["WARN"] = Freeze("#f2a93b"),
        ["CANCEL"] = Freeze("#f2a93b"),
    };

    private static readonly Brush Default = Freeze("#9aa4ad");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string tag && Brushes.TryGetValue(tag, out var brush) ? brush : Default;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

/// <summary>true → the "Good" green, false → muted grey: status dots.</summary>
public sealed class BoolToStatusBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (Brush)Application.Current.FindResource(value is true ? "Good" : "Muted");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
