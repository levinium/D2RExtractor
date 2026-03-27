using System.Windows;
using D2RExtractor.Models;

namespace D2RExtractor.Views;

public partial class SettingsWindow : Window
{
    public AppPreferences Preferences { get; }

    public SettingsWindow(AppPreferences current)
    {
        InitializeComponent();
        // Clone so Cancel can discard changes
        Preferences = new AppPreferences
        {
            ExtractInternationalFiles = current.ExtractInternationalFiles
        };
        IntlCheckBox.IsChecked = Preferences.ExtractInternationalFiles;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        Preferences.ExtractInternationalFiles = IntlCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
