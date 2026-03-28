using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace D2RExtractor.Views;

public partial class ChangeLogWindow : Window
{
    private static readonly SolidColorBrush AccentBrush = new(System.Windows.Media.Color.FromRgb(0xC8, 0xA9, 0x51));
    private static readonly SolidColorBrush TextBrush   = new(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly SolidColorBrush MutedBrush  = new(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

    public ChangeLogWindow()
    {
        InitializeComponent();
        BuildEntries();
    }

    private void BuildEntries()
    {
        // Entries are listed newest-first. Each call to AddEntry appends to the panel.

        AddEntry("1.1.1", [
            "Replaced CASC enumeration dry-spell heuristic with CascGetStorageInfo file count query. "
                + "This fixes a potential issue where international (locales) files could be silently "
                + "skipped if they were stored far from the base data entries in the CASC index.",
            "Verified the data:locales\\ CASC virtual-path prefix against a real D2R installation.",
            "Optimized file extraction with reusable read buffer and pre-sized file output."
        ]);

        AddEntry("1.1.0", [
            "Added settings window with option to extract international audio files (multi-language dubbing).",
            "Added change log window accessible from the gear menu.",
            "Added international file extraction support (locales folder)."
        ]);

        AddEntry("1.0.0", [
            "Initial release with CASC extraction and undo support for D2R installations."
        ]);
    }

    private void AddEntry(string version, string[] items)
    {
        // Version header
        var header = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentBrush,
            Margin = new Thickness(0, EntriesPanel.Children.Count > 0 ? 16 : 0, 0, 6)
        };
        header.Inlines.Add(new Run($"v{version}"));
        EntriesPanel.Children.Add(header);

        // Bullet items
        foreach (string item in items)
        {
            var bullet = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextBrush,
                FontSize = 12,
                Margin = new Thickness(8, 2, 0, 2)
            };
            bullet.Inlines.Add(new Run("\u2022  ") { Foreground = MutedBrush });
            bullet.Inlines.Add(new Run(item));
            EntriesPanel.Children.Add(bullet);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
