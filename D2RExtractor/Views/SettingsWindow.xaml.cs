using System.Windows;
using D2RExtractor.Models;

namespace D2RExtractor.Views;

public partial class SettingsWindow : Window
{
    /// <summary>The preferences snapshot — read this after ShowDialog() returns true.</summary>
    public AppPreferences Preferences { get; private set; }

    public SettingsWindow(AppPreferences current)
    {
        InitializeComponent();
        // Clone so Cancel doesn't mutate the caller's copy.
        Preferences = new AppPreferences
        {
            ExtractInternationalFiles = current.ExtractInternationalFiles
        };
        InternationalCheckBox.IsChecked = Preferences.ExtractInternationalFiles;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Preferences.ExtractInternationalFiles = InternationalCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
