using System.IO;
using System.Windows;
using System.Windows.Controls;
using D2RExtractor.Models;
using D2RExtractor.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace D2RExtractor.Views;

/// <summary>One changed file, shaped for the grid.</summary>
public sealed class ChangeRow
{
    public required ChangeKind Kind { get; init; }
    public required string RelPath { get; init; }
    public required long Size { get; init; }

    /// <summary>
    /// "Replaced" rather than "Updated", because the row sits next to an Update button and the two
    /// meanings are not the same: the run was an update, this particular file was replaced.
    /// </summary>
    public string KindLabel => Kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Updated => "Replaced",
        _ => "Removed",
    };

    public string SizeLabel => FormatBytes(Size);

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// What the last run did to one destination.
///
/// <para>
/// Only the most recent run is kept. Its whole value is answering "what did that patch actually
/// change?", which is a question about the last one — and keeping a history would mean carrying
/// file lists for runs nobody is going to ask about, inside the game folder.
/// </para>
/// </summary>
public partial class ChangesWindow : Window
{
    private readonly ExtractionTarget _target;
    private readonly List<ChangeRow> _all = [];

    public ChangesWindow(ExtractionTarget target)
    {
        InitializeComponent();

        _target = target;
        Heading.Text = $"Last run — {target.DisplayName}";

        Load();
    }

    private void Load()
    {
        ExtractionRunRecord? record = ManifestService.LoadRunRecord(_target);

        if (record == null)
        {
            Summary.Text = "Nothing has been extracted to this destination yet, or it was extracted "
                         + "by a version older than 1.1.8, which kept no record of what it changed.";
            ShowEmpty("No run has been recorded here.\n\nRun an update and this will show what it changed.");
            return;
        }

        string when = record.RanAt.ToLocalTime().ToString("d MMM yyyy, HH:mm");
        string what = record.Kind == RunKind.Extract ? "Full extraction" : "Update";

        Summary.Text = record.Kind == RunKind.Extract
            ? $"{what} on {when} — {record.FilesAdded:N0} files written "
              + $"({ChangeRow.FormatBytes(record.BytesWritten)})."
            : $"{what} on {when} — {record.FilesAdded:N0} added, {record.FilesUpdated:N0} replaced, "
              + $"{record.FilesRemoved:N0} removed, {record.FilesUnchanged:N0} unchanged "
              + $"({ChangeRow.FormatBytes(record.BytesWritten)} written).";

        if (!record.HasChangeList)
        {
            // A fresh extraction writes no per-file list on purpose: every file was an addition, so
            // it would duplicate the manifest for no gain. Say so rather than showing an empty grid
            // that reads as "nothing changed".
            ShowEmpty($"This was a full extraction, so all {record.FilesAdded:N0} files were written.\n\n"
                    + "Per-file lists are kept for updates, where the point is which handful of files "
                    + "a patch touched.");
            return;
        }

        foreach (ExtractionChange change in ManifestService.EnumerateChanges(_target))
            _all.Add(new ChangeRow { Kind = change.Kind, RelPath = change.RelPath, Size = change.Size });

        if (_all.Count == 0)
        {
            ShowEmpty("That run found nothing to change — the extraction already matched the archives.");
            return;
        }

        ApplyFilter();
    }

    private void ShowEmpty(string message)
    {
        EmptyNotice.Text = message;
        EmptyNotice.Visibility = Visibility.Visible;
        ChangesList.Visibility = Visibility.Collapsed;
        FilterRow.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = false;
        ShownCount.Text = string.Empty;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_all.Count == 0) return;

        string needle = FilterBox.Text?.Trim() ?? string.Empty;

        var shown = _all.Where(r =>
            r.Kind switch
            {
                ChangeKind.Added => ShowAdded.IsChecked == true,
                ChangeKind.Updated => ShowUpdated.IsChecked == true,
                _ => ShowRemoved.IsChecked == true,
            }
            && (needle.Length == 0
                || r.RelPath.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        ChangesList.ItemsSource = shown;

        ShownCount.Text = shown.Count == _all.Count
            ? $"{_all.Count:N0} file(s)"
            : $"{shown.Count:N0} of {_all.Count:N0} file(s)";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the last run's changes",
            FileName = $"d2r-changes-{DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Everything, not just what the filter is showing: a file saved for later is worth more
            // than a snapshot of a filter box nobody will remember setting.
            using var writer = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(false));
            writer.WriteLine(Heading.Text);
            writer.WriteLine(Summary.Text);
            writer.WriteLine(_target.FolderPath);
            writer.WriteLine();

            foreach (ChangeRow row in _all)
                writer.WriteLine($"{row.KindLabel,-9}{row.RelPath}\t{row.Size}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the file: {ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
