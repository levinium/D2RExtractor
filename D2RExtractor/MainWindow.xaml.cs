using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using D2RExtractor.Models;
using D2RExtractor.Native;
using D2RExtractor.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace D2RExtractor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<D2RInstallation> _installations = new();
    private readonly CascExtractorService _extractor = new();

    // Maps an installation to its active CancellationTokenSource (one operation at a time per install)
    private readonly Dictionary<D2RInstallation, CancellationTokenSource> _activeCts = new();

    // Installations waiting in the Extract All / Undo All queue
    private readonly HashSet<D2RInstallation> _pendingQueue = new();

    // FIFO queue for sequential extraction — shared by single Extract and Extract All
    private readonly Queue<D2RInstallation> _extractQueue = new();
    private bool _extractQueueRunning;

    // -----------------------------------------------------------------------
    // INotifyPropertyChanged — toolbar button state
    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool CanExtractAll => _installations.Any(i => i.CanExtract);
    public bool CanUndoAll    => _installations.Any(i => i.CanUndo);
    public bool CanCancelAll  => _installations.Any(i =>  i.IsExtracting || i.IsQueued);

    private void RefreshToolbarState()
    {
        OnPropertyChanged(nameof(CanExtractAll));
        OnPropertyChanged(nameof(CanUndoAll));
        OnPropertyChanged(nameof(CanCancelAll));
    }

    private void OnInstallationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (D2RInstallation i in e.NewItems)
                i.PropertyChanged += OnInstallPropertyChanged;
        if (e.OldItems != null)
            foreach (D2RInstallation i in e.OldItems)
                i.PropertyChanged -= OnInstallPropertyChanged;
        RefreshToolbarState();
    }

    private void OnInstallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(D2RInstallation.IsExtracting)
                           or nameof(D2RInstallation.IsQueued)
                           or nameof(D2RInstallation.IsExtracted)
                           or nameof(D2RInstallation.IsPartiallyExtracted))
            RefreshToolbarState();
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
        VersionLabel.Text = $"v{ver.Major}.{ver.Minor}";
        InstallationsList.ItemsSource = _installations;
        _installations.CollectionChanged += OnInstallationsChanged;
        LoadInstallations();
    }

    // -----------------------------------------------------------------------
    // Load / Save
    // -----------------------------------------------------------------------

    private void LoadInstallations()
    {
        var saved = ManifestService.LoadInstallations();
        foreach (var inst in saved)
        {
            inst.RefreshState(ManifestService.LoadManifest(inst)?.IsComplete);
            _installations.Add(inst);
        }
        Log("D2R Extractor ready. Loaded " + _installations.Count + " installation(s).");
    }

    private void Save() => ManifestService.SaveInstallations(_installations);

    // -----------------------------------------------------------------------
    // Add / Remove
    // -----------------------------------------------------------------------

    private void AddInstallation_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select your D2R installation folder (the one containing the 'Data' subfolder)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        string folder = dlg.SelectedPath;

        // Validate it looks like D2R
        string? validationError = CascExtractorService.ValidateInstallationFolder(folder);
        if (validationError != null)
        {
            System.Windows.MessageBox.Show(validationError, "Invalid Folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Avoid duplicates
        if (_installations.Any(i => i.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show("This folder is already in the list.",
                "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Derive a friendly name from the folder
        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var install = new D2RInstallation { Name = name, FolderPath = folder };
        install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        _installations.Add(install);
        Save();

        Log($"Added installation: {name} → {folder}");
    }

    private void RemoveInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not D2RInstallation selected)
        {
            System.Windows.MessageBox.Show("Select an installation from the list first.",
                "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.IsExtracting)
        {
            System.Windows.MessageBox.Show(
                "Cannot remove an installation while extraction is in progress.",
                "Operation in Progress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Remove '{selected.Name}' from the list?\n\n" +
            "(This does NOT delete any files — it only removes the entry from D2R Extractor.)",
            "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _installations.Remove(selected);
        Save();
        Log($"Removed installation: {selected.Name}");
    }

    // -----------------------------------------------------------------------
    // Extract queue
    // -----------------------------------------------------------------------

    private void EnqueueExtraction(D2RInstallation install)
    {
        if (install.IsQueued || install.IsExtracting) return; // guard against double-enqueue
        install.IsQueued = true;
        install.StatusText = "Queued";
        _extractQueue.Enqueue(install);
        ProcessExtractQueue();
    }

    private async void ProcessExtractQueue()
    {
        if (_extractQueueRunning) return;
        _extractQueueRunning = true;
        try
        {
            while (_extractQueue.Count > 0)
            {
                var install = _extractQueue.Dequeue();
                if (!install.IsQueued) continue; // was cancelled while waiting
                install.IsQueued = false;
                await RunExtractAsync(install);
            }
        }
        finally
        {
            _extractQueueRunning = false;
        }
    }

    private void DequeueExtraction(D2RInstallation install)
    {
        // Queue<T> has no Remove(item) — rebuild without the target
        var remaining = _extractQueue.Where(i => i != install).ToList();
        _extractQueue.Clear();
        foreach (var i in remaining) _extractQueue.Enqueue(i);
        install.IsQueued = false;
        install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        Log($"[{install.Name}] Dequeued.");
    }

    // -----------------------------------------------------------------------
    // Extract
    // -----------------------------------------------------------------------

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not D2RInstallation install)
            return;

        if (!CascLib.IsDllPresent())
        {
            MessageBox.Show(
                "CascLib.dll is not found next to the executable.\n\n" +
                "Please copy CascLib.dll (x64) from Ladik's CASC Viewer next to D2RExtractor.exe and try again.",
                "Missing CascLib.dll", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string? spaceWarning = CascExtractorService.CheckDiskSpace(install.FolderPath);
        if (spaceWarning != null)
        {
            var proceed = MessageBox.Show(spaceWarning + "\n\nContinue anyway?",
                "Disk Space Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        string partialNote = install.IsPartiallyExtracted
            ? "This installation has a partial extraction. The partial files will be cleaned up automatically before starting fresh.\n\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            partialNote +
            $"Extract D2R game files for:\n{install.FolderPath}\n\n" +
            "This will extract approximately 40–45 GB of data and may take 30–45 minutes.\n\n" +
            "IMPORTANT: Before updating D2R, use 'Undo Extraction' first.\n\nStart extraction?",
            "Confirm Extraction", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        EnqueueExtraction(install);
    }

    // -----------------------------------------------------------------------
    // Undo
    // -----------------------------------------------------------------------

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not D2RInstallation install)
            return;

        string filesDesc = install.IsPartiallyExtracted
            ? "All partially extracted files will be permanently deleted from the 'data' folder."
            : "All extracted files will be permanently deleted from the 'data' folder.";

        var confirm = MessageBox.Show(
            $"Undo extraction for:\n{install.FolderPath}\n\n" +
            filesDesc + "\n" +
            "The original CASC archives are NOT affected — you can re-extract at any time.\n\n" +
            "IMPORTANT: Do this before updating D2R.\n\nProceed?",
            "Confirm Undo Extraction", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        await RunUndoAsync(install);
        if (install.StatusText == "Ready")
            MessageBox.Show($"Undo complete for '{install.Name}'.\n\nYou can now safely update D2R.",
                "Done", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // -----------------------------------------------------------------------
    // Cancel
    // -----------------------------------------------------------------------

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not D2RInstallation install)
            return;

        // Dequeue from extraction queue if waiting there.
        if (install.IsQueued && _extractQueue.Contains(install))
        {
            DequeueExtraction(install);
            return;
        }

        // Dequeue from undo queue (_pendingQueue) if waiting there.
        if (install.IsQueued && _pendingQueue.Remove(install))
        {
            install.IsQueued = false;
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
            Log($"[{install.Name}] Dequeued.");
            return;
        }

        // Cancel if actively running.
        if (_activeCts.TryGetValue(install, out var cts))
        {
            cts.Cancel();
            Log($"[{install.Name}] Cancellation requested…");
        }
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cts in _activeCts.Values.ToList())
            cts.Cancel();

        // Clear extraction queue.
        foreach (var install in _extractQueue.ToList())
        {
            install.IsQueued = false;
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        _extractQueue.Clear();

        // Clear undo queue.
        foreach (var install in _pendingQueue.ToList())
        {
            install.IsQueued = false;
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        _pendingQueue.Clear();

        Log("Cancel All requested.");
    }

    // -----------------------------------------------------------------------
    // Extract All / Undo All
    // -----------------------------------------------------------------------

    private void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = _installations.Where(i => i.CanExtract).ToList();
        if (targets.Count == 0) return;

        int partialCount = targets.Count(i => i.IsPartiallyExtracted);
        string partialNote = partialCount > 0
            ? $"\nNote: {partialCount} installation(s) with partial extractions will be cleaned up first.\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Queue {targets.Count} installation(s) for extraction?\n\n" +
            string.Join("\n", targets.Select(i => $"  • {i.Name}{(i.IsPartiallyExtracted ? " (partial)" : "")}")) + "\n" +
            partialNote +
            "\nEach extraction writes ~40–45 GB and takes 30–45 minutes.\n\n" +
            "IMPORTANT: Before updating D2R, use Undo All first.\n\nProceed?",
            "Confirm Extract All", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        // Disk space check upfront before queueing (preserves per-item skip dialog),
        // then enqueue — the shared queue processor runs them one at a time.
        foreach (var install in targets)
        {
            string? spaceWarning = CascExtractorService.CheckDiskSpace(install.FolderPath);
            if (spaceWarning != null)
            {
                var skip = MessageBox.Show(
                    $"[{install.Name}] {spaceWarning}\n\nSkip this installation?",
                    "Disk Space Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (skip == MessageBoxResult.Yes) continue;
            }

            EnqueueExtraction(install);
        }
    }

    private async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = _installations.Where(i => i.CanUndo).ToList();
        if (targets.Count == 0) return;

        int partialCount = targets.Count(i => i.IsPartiallyExtracted);
        string filesDesc = partialCount == targets.Count
            ? "All partially extracted files will be permanently deleted from each 'data' folder."
            : partialCount > 0
                ? "All extracted and partially extracted files will be permanently deleted from each 'data' folder."
                : "All extracted files will be permanently deleted from each 'data' folder.";

        var confirm = MessageBox.Show(
            $"Queue {targets.Count} installation(s) for undo?\n\n" +
            string.Join("\n", targets.Select(i => $"  • {i.Name}{(i.IsPartiallyExtracted ? " (partial)" : "")}")) + "\n\n" +
            filesDesc + "\nProceed?",
            "Confirm Undo All", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        // Mark all as queued immediately.
        foreach (var install in targets)
        {
            install.IsQueued = true;
            install.StatusText = "Queued";
            _pendingQueue.Add(install);
        }

        foreach (var install in targets)
        {
            if (!_pendingQueue.Contains(install)) continue; // dequeued via Cancel

            _pendingQueue.Remove(install);
            install.IsQueued = false;
            await RunUndoAsync(install);
        }
    }

    // -----------------------------------------------------------------------
    // Shared async extract / undo helpers (used by both single and bulk)
    // -----------------------------------------------------------------------

    private async Task RunExtractAsync(D2RInstallation install)
    {
        if (!CascLib.IsDllPresent())
        {
            MessageBox.Show("CascLib.dll is not found next to the executable.",
                "Missing CascLib.dll", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        _activeCts[install] = cts;
        install.IsExtracting = true;
        install.Progress = 0;
        install.StatusText = "Starting…";
        Log($"[{install.Name}] Extraction started.");

        try
        {
            // If a partial extraction exists, clean it up before starting fresh.
            if (install.IsPartiallyExtracted)
            {
                Log($"[{install.Name}] Partial extraction found — cleaning up before fresh extraction…");
                install.StatusText = "Cleaning up…";
                await Task.Run(() => _extractor.UndoExtraction(install, null,
                    msg => AppendLog($"[{install.Name}] {msg}"), cts.Token));
                install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
                install.StatusText = "Starting…";
            }

            var progress = new Progress<ExtractionProgress>(p =>
            {
                install.IsEnumerating = p.IsEnumerating;
                if (p.IsEnumerating)
                {
                    install.StatusText = p.FilesProcessed > 0
                        ? $"Enumerating… ({p.FilesProcessed:N0} found)"
                        : "Enumerating files…";
                    install.EnumeratingFile = p.CurrentFile;
                }
                else
                {
                    install.EnumeratingFile = string.Empty;
                    double pct = p.TotalFiles > 0 ? (p.FilesProcessed * 100.0 / p.TotalFiles) : 0;
                    install.Progress = pct;
                    install.FilesExtracted = p.FilesProcessed;
                    install.TotalFiles = p.TotalFiles;
                    install.StatusText = $"{p.FilesProcessed:N0} / {p.TotalFiles:N0}";
                    if (p.FilesProcessed > 0 && p.FilesProcessed % 1000 == 0)
                        Log($"[{install.Name}] {p.FilesProcessed:N0}/{p.TotalFiles:N0} — {pct:F1}%");
                }
            });

            await Task.Run(() => _extractor.Extract(install, progress,
                msg => AppendLog($"[{install.Name}] {msg}"), cts.Token));

            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
            install.Progress = 100;
            install.StatusText = "Extracted";
            Log($"[{install.Name}] Extraction complete.");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Log($"[{install.Name}] Extraction cancelled.");
            install.StatusText = "Cancelled";
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        catch (Exception ex)
        {
            Log($"[{install.Name}] ERROR: {ex.Message}");
            install.StatusText = "Error";
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        finally
        {
            install.IsExtracting = false;
            install.IsEnumerating = false;
            install.EnumeratingFile = string.Empty;
            install.Progress = 0;
            _activeCts.Remove(install);
            cts.Dispose();
        }
    }

    private async Task RunUndoAsync(D2RInstallation install)
    {
        var cts = new CancellationTokenSource();
        _activeCts[install] = cts;
        install.IsExtracting = true;
        install.Progress = 0;
        install.StatusText = "Undoing…";
        Log($"[{install.Name}] Undo extraction started.");

        try
        {
            var progress = new Progress<ExtractionProgress>(p =>
            {
                double pct = p.TotalFiles > 0 ? (p.FilesProcessed * 100.0 / p.TotalFiles) : 0;
                install.Progress = pct;
                install.StatusText = $"Removing {p.FilesProcessed:N0}/{p.TotalFiles:N0}";
            });

            await Task.Run(() => _extractor.UndoExtraction(install, progress,
                msg => AppendLog($"[{install.Name}] {msg}"), cts.Token));

            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
            install.Progress = 0;
            install.StatusText = "Ready";
            Log($"[{install.Name}] Undo complete.");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Log($"[{install.Name}] Undo cancelled.");
            install.StatusText = "Cancelled";
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        catch (Exception ex)
        {
            Log($"[{install.Name}] ERROR during undo: {ex.Message}");
            install.StatusText = "Error";
            install.RefreshState(ManifestService.LoadManifest(install)?.IsComplete);
        }
        finally
        {
            install.IsExtracting = false;
            install.IsEnumerating = false;
            install.Progress = 0;
            _activeCts.Remove(install);
            cts.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Log helpers
    // -----------------------------------------------------------------------

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = string.Empty;
    }

    private void Log(string message) => AppendLog(message);

    private void AppendLog(string message)
    {
        // AppendLog may be called from background threads via Progress<T> callbacks.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
        LoggingService.Write(message);
    }

    // -----------------------------------------------------------------------
    // Window closing
    // -----------------------------------------------------------------------

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        bool anyActive = _activeCts.Count > 0 || _extractQueue.Count > 0 || _pendingQueue.Count > 0;
        if (anyActive)
        {
            var result = System.Windows.MessageBox.Show(
                "An extraction is in progress. Cancel it and exit?",
                "Exit Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            foreach (var cts in _activeCts.Values)
                cts.Cancel();
        }

        Save();
        base.OnClosing(e);
    }
}
