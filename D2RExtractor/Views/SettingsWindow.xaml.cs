using System.Linq;
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
            ExtractInternationalFiles = current.ExtractInternationalFiles,
            InternationalLanguage = current.InternationalLanguage,
            VerifyFileContents = current.VerifyFileContents
        };

        // Populate language dropdown.
        foreach (var (code, name) in AppPreferences.AvailableLanguages)
            LanguageComboBox.Items.Add($"{name}  [{code}]");

        // Set initial selections.
        InternationalCheckBox.IsChecked = current.ExtractInternationalFiles;
        VerifyContentsCheckBox.IsChecked = current.VerifyFileContents;

        int langIdx = current.InternationalLanguage != null
            ? System.Array.FindIndex(AppPreferences.AvailableLanguages, l => l.Code == current.InternationalLanguage)
            : -1;
        LanguageComboBox.SelectedIndex = langIdx >= 0 ? langIdx : 0;

        UpdateLanguagePanelVisibility();
    }

    private void InternationalCheckBox_Changed(object sender, RoutedEventArgs e)
        => UpdateLanguagePanelVisibility();

    private void UpdateLanguagePanelVisibility()
    {
        bool show = InternationalCheckBox.IsChecked == true;
        LanguagePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        IntlDescriptionText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Preferences.ExtractInternationalFiles = InternationalCheckBox.IsChecked == true;
        Preferences.VerifyFileContents = VerifyContentsCheckBox.IsChecked == true;

        if (Preferences.ExtractInternationalFiles && LanguageComboBox.SelectedIndex >= 0)
            Preferences.InternationalLanguage = AppPreferences.AvailableLanguages[LanguageComboBox.SelectedIndex].Code;
        else
            Preferences.InternationalLanguage = null;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
