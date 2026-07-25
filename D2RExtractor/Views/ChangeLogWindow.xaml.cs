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

        AddEntry("1.1.6", [
            "Fixed a Steam extraction bug that caused an error on game launch. The Steam storage omits "
                + "path separators for some entries, so a few thousand files (e.g. sound files under "
                + "sfx\\monster\\baal\\) were written with a merged folder/file name and ended up at the wrong "
                + "path. Their contents were correct, but the game couldn't find them and errored on launch.",
            "The extractor now recovers the correct paths from the storage's 'index' manifest, so the Steam "
                + "output matches the Battle.net layout exactly. Steam only — Battle.net was never affected."
        ]);

        AddEntry("1.1.5", [
            "Restored Steam D2R support after the mid-2026 storage change (build 93236+). Steam's latest "
                + "update replaced the classic CASC layout with a self-contained 'Static Build Configuration' "
                + "format (data\\.build.config plus flat NN-NNNNNNNN.data archives) that CascLib cannot read, "
                + "so extraction had stopped working.",
            "Added a native, fully-local reader for the new Steam format — no CascLib.dll and, unlike the "
                + "previous Steam workaround, NO internet connection required. It reads and decodes everything "
                + "directly from the local game files.",
            "The extractor now auto-detects the storage format per install: the native reader for Steam "
                + "static-container installs, and CascLib for classic CASC installs (Battle.net). Extraction "
                + "output is identical for both.",
            "Battle.net extraction is unaffected and continues to use CascLib.dll."
        ]);

        AddEntry("1.1.4", [
            "Fixed international file extraction. Locale files were being extracted to a 'locales' "
                + "directory that D2R ignores in -direct mode. Files are now correctly mapped into the "
                + "data tree so the game loads dubbed audio and localized text.",
            "Added language selector \u2014 choose which language to extract in Settings. Only the "
                + "selected language's audio/text is extracted, replacing the base English files. Supports "
                + "deDE, enUS, esES, esMX, frFR, itIT, jaJP, koKR, plPL, ptBR, ruRU, zhCN, zhTW.",
            "Changing the selected language triggers a re-extraction of just the international files "
                + "(no need to undo/redo the full base extraction).",
            "Added CascDiagnostic console tool to the solution for CASC storage analysis and debugging."
        ]);

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
                + "not working correctly. (Re-enabled in v1.1.4.)"
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
