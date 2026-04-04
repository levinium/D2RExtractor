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

        AddEntry("1.1.3", [
            "Steam D2R support (patch 3.1.2+): Full extraction now works for Steam installations. "
                + "Game data is downloaded from Blizzard's CDN during extraction, so an internet connection "
                + "is required for Steam users.",
            "Patched and rebuilt CascLib.dll with fixes for the Steam D2R CASC layout — "
                + "fixed ONLINE flag propagation, added archive index loading for CDN-assisted storages, "
                + "and enabled encoded size resolution from archive indices.",
            "Added CascOpenStorageEx fallback with ONLINE + ALLOW_DOWNLOAD flags for both metadata "
                + "and file data CDN downloads.",
            "Added diagnostic logging of CASC metadata file presence for easier troubleshooting.",
            "Throttled extraction progress reporting to prevent UI freezes during rapid processing."
        ]);

        AddEntry("1.1.2", [
            "Added CascOpenStorageEx fallback for D2R installations where the standard CascOpenStorage "
                + "fails (e.g. Steam after patch 3.1.2). The app now automatically retries with CDN-enabled "
                + "and full online-storage modes before reporting an error.",
            "Added clear error messaging with a link to the upstream CascLib tracking issue when all "
                + "CASC open attempts fail.",
            "Graceful handling when CascOpenStorageEx is not available in older CascLib.dll versions, "
                + "with guidance to update.",
            "Temporarily disabled international file extraction (multi-language audio) due to the feature "
                + "not working correctly. The option is grayed out in settings until a fix is available."
        ]);

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
