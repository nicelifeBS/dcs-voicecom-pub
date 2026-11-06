using System.Windows;
using DcsVoiceCom.Config;
using DcsVoiceCom.ViewModels;
using Forms = System.Windows.Forms;

namespace DcsVoiceCom.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.ImageSource;
    }

    public static IReadOnlyList<string> Languages => SettingsViewModel.Languages;

    /// <summary>The saved settings after Save; null when cancelled.</summary>
    public AppSettings? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _vm.Build();
        if (Result != null) DialogResult = true;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e) => _vm.ModelDirectory = PickFolder(_vm.ModelDirectory);
    private void BrowseJson_Click(object sender, RoutedEventArgs e) => _vm.DcsBiosJsonPath = PickFolder(_vm.DcsBiosJsonPath);
    private void BrowseProfiles_Click(object sender, RoutedEventArgs e) => _vm.ProfilesPath = PickFolder(_vm.ProfilesPath);

    /// <summary>The chosen folder, or <paramref name="current"/> when cancelled.</summary>
    private static string PickFolder(string current)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = current, UseDescriptionForTitle = true, Description = "Choose a folder" };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : current;
    }
}
