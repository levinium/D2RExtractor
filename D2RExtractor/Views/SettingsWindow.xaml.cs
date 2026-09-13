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

        // Clone so Cancel doesn't mutate the caller's copy. Every field has to be
        // carried, including the ones this window never shows: the caller replaces
        // its whole preferences object with this one on Save, so anything omitted
        // here is not preserved — it is erased. LastUpdateCheckUtc left behind
        // that way would reset the daily check to "never looked" every time
        // someone opened Settings.
        Preferences = new AppPreferences
        {
            ExtractInternationalFiles = current.ExtractInternationalFiles,
            InternationalLanguage = current.InternationalLanguage,
            VerifyFileContents = current.VerifyFileContents,
            CheckForUpdatesAutomatically = current.CheckForUpdatesAutomatically,
            LastUpdateCheckUtc = current.LastUpdateCheckUtc
        };

        // Populate language dropdown.
        foreach (var (code, name) in AppPreferences.AvailableLanguages)
            LanguageComboBox.Items.Add($"{name}  [{code}]");

        // Set initial selections.
        InternationalCheckBox.IsChecked = current.ExtractInternationalFiles;
        VerifyContentsCheckBox.IsChecked = current.VerifyFileContents;
        CheckForUpdatesCheckBox.IsChecked = current.CheckForUpdatesAutomatically;

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
        Preferences.CheckForUpdatesAutomatically = CheckForUpdatesCheckBox.IsChecked == true;

        if (Preferences.ExtractInternationalFiles && LanguageComboBox.SelectedIndex >= 0)
            Preferences.InternationalLanguage = AppPreferences.AvailableLanguages[LanguageComboBox.SelectedIndex].Code;
        else
            Preferences.InternationalLanguage = null;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
