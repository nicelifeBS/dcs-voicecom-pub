using System.Windows;
using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.App;

/// <summary>Picks which panels of generated commands to add to a profile.</summary>
public partial class SeedWindow : Window
{
    public SeedWindow(SeedViewModel seed)
    {
        InitializeComponent();
        DataContext = seed;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
