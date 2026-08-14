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
using OperationKind = D2RExtractor.Services.CascExtractorService.OperationKind;
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

    // FIFO queue for sequential extraction/update — shared by the per-row button and the bulk
    // buttons. The operation travels with the installation because Extract and Update are queued
    // through the same path but must not be confused: Extract can delete first, Update never does.
    private readonly Queue<(D2RInstallation Install, OperationKind Kind)> _extractQueue = new();
    private bool _extractQueueRunning;
    private AppPreferences _preferences = new();

    // -----------------------------------------------------------------------
    // INotifyPropertyChanged — toolbar button state
    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool CanExtractAll => _installations.Any(i => i.CanExtract);
    public bool CanUpdateAll  => _installations.Any(i => i.CanUpdate);
    public bool CanUndoAll    => _installations.Any(i => i.CanUndo);
    public bool CanCancelAll  => _installations.Any(i =>  i.IsExtracting || i.IsQueued);

    /// <summary>True while any installation is extracting, undoing, or queued.</summary>
    public bool IsAnyBusy => _installations.Any(i => i.IsBusy);

    /// <summary>
    /// Gear button context menu is always available; Settings item is disabled while any operation is running.
    /// This property drives the Settings MenuItem IsEnabled binding.
    /// </summary>
    public bool IsGearEnabled => !IsAnyBusy;

    private void RefreshToolbarState()
    {
        OnPropertyChanged(nameof(CanExtractAll));
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(CanUndoAll));
        OnPropertyChanged(nameof(CanCancelAll));
        OnPropertyChanged(nameof(IsAnyBusy));
        OnPropertyChanged(nameof(IsGearEnabled));
    }

    /// <summary>
    /// Loads the current manifest for <paramref name="install"/> and calls RefreshState
    /// with the current preferences. Use this everywhere instead of calling RefreshState directly.
    /// </summary>
    private void RefreshInstallState(D2RInstallation install)
    {
        var manifest = ManifestService.LoadManifest(install);
        install.RefreshState(manifest?.IsComplete, manifest?.InternationalExtracted,
            _preferences.ExtractInternationalFiles,
            manifest?.InternationalLanguage, _preferences.InternationalLanguage);
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
        VersionLabel.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        InstallationsList.ItemsSource = _installations;
        _installations.CollectionChanged += OnInstallationsChanged;
        LoadInstallations();
    }

    // -----------------------------------------------------------------------
    // Load / Save
    // -----------------------------------------------------------------------

    private void LoadInstallations()
    {
        _preferences = ManifestService.LoadPreferences();
        // LoadPreferences() always returns a non-null AppPreferences (defaults on error).
        // No null check needed; errors are silently swallowed inside LoadPreferences().

        var saved = ManifestService.LoadInstallations();
        foreach (var inst in saved)
        {
            RefreshInstallState(inst);
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
        RefreshInstallState(install);
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

    private void EnqueueOperation(D2RInstallation install, OperationKind kind)
    {
        if (install.IsQueued || install.IsExtracting) return; // guard against double-enqueue
        install.IsQueued = true;
        install.StatusText = "Queued";
        _extractQueue.Enqueue((install, kind));
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
                var (install, kind) = _extractQueue.Dequeue();
                if (!install.IsQueued) continue; // was cancelled while waiting
                install.IsQueued = false;

                if (kind == OperationKind.Update)
                    await RunUpdateAsync(install);
                else
                    await RunExtractAsync(install);
            }
        }
        finally
        {
            _extractQueueRunning = false;
        }
    }

    /// <summary>True when <paramref name="install"/> is waiting in the extract/update queue.</summary>
    private bool IsInExtractQueue(D2RInstallation install) =>
        _extractQueue.Any(item => item.Install == install);

    private void DequeueExtraction(D2RInstallation install)
    {
        // Queue<T> has no Remove(item) — rebuild without the target
        var remaining = _extractQueue.Where(item => item.Install != install).ToList();
        _extractQueue.Clear();
        foreach (var item in remaining) _extractQueue.Enqueue(item);
        install.IsQueued = false;
        RefreshInstallState(install);
        Log($"[{install.Name}] Dequeued.");
    }

    // -----------------------------------------------------------------------
    // Extract
    // -----------------------------------------------------------------------

    /// <summary>
    /// The single contextual action button: extracts, resumes or updates depending on what this
    /// installation currently needs. The two paths are deliberately kept apart — an update must
    /// never fall through to the extraction path, which deletes the existing files first.
    /// </summary>
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

        var manifest = ManifestService.LoadManifest(install);
        var kind = CascExtractorService.PlanOperation(
            manifest, _preferences.ExtractInternationalFiles, _preferences.InternationalLanguage);

        if (kind == OperationKind.Update)
        {
            if (!ConfirmUpdate(install)) return;
            EnqueueOperation(install, OperationKind.Update);
            return;
        }

        long diskRequired = _preferences.ExtractInternationalFiles
            ? 50L * 1024 * 1024 * 1024   // ~50 GB (base ~45 GB + one language ~1-2 GB)
            : 48L * 1024 * 1024 * 1024;  // ~48 GB base only
        string? spaceWarning = CascExtractorService.CheckDiskSpace(install.FolderPath, diskRequired);
        if (spaceWarning != null)
        {
            var proceed = MessageBox.Show(spaceWarning + "\n\nContinue anyway?",
                "Disk Space Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        string intlNote = _preferences.ExtractInternationalFiles && !string.IsNullOrEmpty(_preferences.InternationalLanguage)
            ? $"International files for '{_preferences.InternationalLanguage}' will also be extracted, replacing base English audio/text.\n\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Extract D2R game files for:\n{install.FolderPath}\n\n" +
            "This will extract approximately 45–70 GB of data (depending on whether international files are enabled) " +
            "and may take 30–90 minutes.\n\n" +
            intlNote +
            "After a D2R update, use 'Update' to refresh only the files that changed.\n\nStart extraction?",
            "Confirm Extraction", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        EnqueueOperation(install, OperationKind.Extract);
    }

    /// <summary>
    /// Confirmation for an update or resume. Deliberately lighter than the extraction dialog: no
    /// size or duration warning, and no disk-space gate, because the whole point is that it writes
    /// only the difference. What it does need to say is that the comparison itself takes a while.
    /// </summary>
    private bool ConfirmUpdate(D2RInstallation install)
    {
        string what = install.IsInterrupted
            ? "Resume the interrupted extraction for:"
            : "Check for changed game files in:";

        string verifyNote = _preferences.VerifyFileContents
            ? "\n'Verify extracted file contents' is on, so every extracted file will also be " +
              "checksummed. That reads the whole extraction and takes noticeably longer, but still " +
              "only writes files that differ.\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"{what}\n{install.FolderPath}\n\n" +
            "The game archives will be compared against the extracted files. Only files that are " +
            "new, changed, missing or damaged get written, and files the game no longer ships are " +
            "removed.\n\n" +
            "Comparing takes a few minutes before anything is written.\n" +
            verifyNote +
            "\nProceed?",
            install.IsInterrupted ? "Confirm Resume" : "Confirm Update",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        return confirm == MessageBoxResult.Yes;
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
            "Note: you no longer need to undo before updating D2R. Use 'Update' afterwards to " +
            "refresh only the files the patch changed.\n\nProceed?",
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
        if (install.IsQueued && IsInExtractQueue(install))
        {
            DequeueExtraction(install);
            return;
        }

        // Dequeue from undo queue (_pendingQueue) if waiting there.
        if (install.IsQueued && _pendingQueue.Remove(install))
        {
            install.IsQueued = false;
            RefreshInstallState(install);
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
        foreach (var (install, _) in _extractQueue.ToList())
        {
            install.IsQueued = false;
            RefreshInstallState(install);
        }
        _extractQueue.Clear();

        // Clear undo queue.
        foreach (var install in _pendingQueue.ToList())
        {
            install.IsQueued = false;
            RefreshInstallState(install);
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

        var confirm = MessageBox.Show(
            $"Queue {targets.Count} installation(s) for extraction?\n\n" +
            string.Join("\n", targets.Select(i => $"  • {i.Name}")) + "\n" +
            "\nEach extraction writes ~45–70 GB (depending on settings) and takes 30–90 minutes.\n\n" +
            "After a D2R update, use Update All to refresh only the files that changed.\n\nProceed?",
            "Confirm Extract All", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        // Disk space check upfront before queueing (preserves per-item skip dialog),
        // then enqueue — the shared queue processor runs them one at a time.
        foreach (var install in targets)
        {
            long diskRequired = _preferences.ExtractInternationalFiles
                ? 50L * 1024 * 1024 * 1024
                : 48L * 1024 * 1024 * 1024;
            string? spaceWarning = CascExtractorService.CheckDiskSpace(install.FolderPath, diskRequired);
            if (spaceWarning != null)
            {
                var skip = MessageBox.Show(
                    $"[{install.Name}] {spaceWarning}\n\nSkip this installation?",
                    "Disk Space Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (skip == MessageBoxResult.Yes) continue;
            }

            EnqueueOperation(install, OperationKind.Extract);
        }
    }

    /// <summary>
    /// Queues every extracted installation for an update. No disk-space gate: an update writes only
    /// the difference, so demanding tens of gigabytes free would nag for no reason.
    /// </summary>
    private void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = _installations.Where(i => i.CanUpdate).ToList();
        if (targets.Count == 0) return;

        int resumeCount = targets.Count(i => i.IsInterrupted);
        string resumeNote = resumeCount > 0
            ? $"\nNote: {resumeCount} interrupted extraction(s) will be resumed — files already written are kept.\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Check {targets.Count} installation(s) against the game archives?\n\n" +
            string.Join("\n", targets.Select(i => $"  • {i.Name}{(i.IsInterrupted ? " (interrupted)" : "")}")) + "\n" +
            resumeNote +
            "\nOnly files that are new, changed, missing or damaged are written; files the game no " +
            "longer ships are removed.\n\n" +
            "Each installation takes a few minutes to compare before anything is written.\n\nProceed?",
            "Confirm Update All", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        foreach (var install in targets)
            EnqueueOperation(install, OperationKind.Update);
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
    // Shared async extract / update / undo helpers (used by both single and bulk)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the progress handler shared by extract and update.
    ///
    /// <para>
    /// An update has phases that write nothing and can run for a minute or more — listing the
    /// archives, diffing them against the disk, checksumming — so those report an indeterminate bar
    /// with a phase name rather than a percentage stuck at zero.
    /// </para>
    /// </summary>
    private Progress<ExtractionProgress> CreateProgressReporter(D2RInstallation install) =>
        new(p =>
        {
            bool indeterminate = p.Phase is ExtractionPhase.Enumerating;
            install.IsEnumerating = indeterminate;

            if (p.Phase == ExtractionPhase.Enumerating)
            {
                install.StatusText = p.FilesProcessed > 0
                    ? $"Enumerating… ({p.FilesProcessed:N0} found)"
                    : "Enumerating files…";
                install.EnumeratingFile = p.CurrentFile;
                return;
            }

            install.EnumeratingFile = string.Empty;
            double pct = p.TotalFiles > 0 ? (p.FilesProcessed * 100.0 / p.TotalFiles) : 0;
            install.Progress = pct;
            install.FilesExtracted = p.FilesProcessed;
            install.TotalFiles = p.TotalFiles;

            install.StatusText = p.Phase switch
            {
                ExtractionPhase.Comparing => $"Comparing {p.FilesProcessed:N0} / {p.TotalFiles:N0}",
                ExtractionPhase.Verifying => $"Verifying {p.FilesProcessed:N0} / {p.TotalFiles:N0}",
                ExtractionPhase.Removing  => $"Removing {p.FilesProcessed:N0} / {p.TotalFiles:N0}",
                _                         => $"{p.FilesProcessed:N0} / {p.TotalFiles:N0}",
            };

            if (p.Phase == ExtractionPhase.Writing && p.FilesProcessed > 0 && p.FilesProcessed % 1000 == 0)
                Log($"[{install.Name}] {p.FilesProcessed:N0}/{p.TotalFiles:N0} — {pct:F1}%");
        });

    /// <summary>
    /// Brings an existing extraction in line with the archives.
    ///
    /// <para>
    /// Kept separate from <see cref="RunExtractAsync"/> rather than sharing a parameterised path,
    /// because that method deletes an incomplete extraction before starting over. Routing an
    /// update through it would silently destroy tens of gigabytes of perfectly good files — the
    /// exact opposite of what this operation is for.
    /// </para>
    /// </summary>
    private async Task RunUpdateAsync(D2RInstallation install)
    {
        if (!CascLib.IsDllPresent())
        {
            MessageBox.Show("CascLib.dll is not found next to the executable.",
                "Missing CascLib.dll", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var manifest = ManifestService.LoadManifest(install);
        if (manifest == null)
        {
            Log($"[{install.Name}] No manifest found — nothing to update. Run an extraction first.");
            RefreshInstallState(install);
            return;
        }

        var cts = new CancellationTokenSource();
        _activeCts[install] = cts;
        install.IsExtracting = true;
        install.Progress = 0;
        install.StatusText = "Starting…";
        Log($"[{install.Name}] {(install.IsInterrupted ? "Resume" : "Update")} started.");

        try
        {
            var progress = CreateProgressReporter(install);

            UpdateSummary summary = await Task.Run(() => _extractor.UpdateExtraction(
                install, manifest,
                _preferences.ExtractInternationalFiles, _preferences.InternationalLanguage,
                _preferences.VerifyFileContents, progress,
                msg => AppendLog($"[{install.Name}] {msg}"), cts.Token));

            RefreshInstallState(install);
            install.Progress = 100;
            install.StatusText = summary.FilesWritten == 0 && summary.FilesRemoved == 0
                ? "Up to date"
                : "Updated";

            Log(summary.FilesWritten == 0 && summary.FilesRemoved == 0
                ? $"[{install.Name}] Already up to date — nothing written."
                : $"[{install.Name}] Update complete: {summary.FilesWritten:N0} written, " +
                  $"{summary.FilesRemoved:N0} removed, {summary.FilesUnchanged:N0} unchanged.");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Log($"[{install.Name}] Update cancelled.");
            install.StatusText = "Cancelled";
            RefreshInstallState(install);
        }
        catch (Exception ex)
        {
            Log($"[{install.Name}] ERROR: {ex.Message}");
            install.StatusText = "Error";
            RefreshInstallState(install);
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
            var progress = CreateProgressReporter(install);

            await Task.Run(() => _extractor.Extract(install, _preferences.ExtractInternationalFiles,
                _preferences.InternationalLanguage, progress,
                msg => AppendLog($"[{install.Name}] {msg}"), cts.Token));

            RefreshInstallState(install);
            install.Progress = 100;
            install.StatusText = "Extracted";
            Log($"[{install.Name}] Extraction complete.");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Log($"[{install.Name}] Extraction cancelled.");
            install.StatusText = "Cancelled";
            RefreshInstallState(install);
        }
        catch (Exception ex)
        {
            Log($"[{install.Name}] ERROR: {ex.Message}");
            install.StatusText = "Error";
            RefreshInstallState(install);
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

            RefreshInstallState(install);
            install.Progress = 0;
            install.StatusText = "Ready";
            Log($"[{install.Name}] Undo complete.");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Log($"[{install.Name}] Undo cancelled.");
            install.StatusText = "Cancelled";
            RefreshInstallState(install);
        }
        catch (Exception ex)
        {
            Log($"[{install.Name}] ERROR during undo: {ex.Message}");
            install.StatusText = "Error";
            RefreshInstallState(install);
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

    // -----------------------------------------------------------------------
    // Gear icon / Settings / Change Log
    // -----------------------------------------------------------------------

    private void GearMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var win = new Views.SettingsWindow(_preferences) { Owner = this };
        if (win.ShowDialog() == true)
        {
            bool intlChanged = win.Preferences.ExtractInternationalFiles != _preferences.ExtractInternationalFiles;
            bool langChanged = !string.Equals(win.Preferences.InternationalLanguage, _preferences.InternationalLanguage, StringComparison.OrdinalIgnoreCase);
            _preferences = win.Preferences;
            ManifestService.SavePreferences(_preferences);
            if (intlChanged || langChanged)
            {
                foreach (var install in _installations)
                    RefreshInstallState(install);
                RefreshToolbarState();
                string langDisplay = _preferences.InternationalLanguage ?? "none";
                Log($"Settings saved. International extraction: {(_preferences.ExtractInternationalFiles ? $"enabled ({langDisplay})" : "disabled")}.");
            }
        }
    }

    private void ChangeLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new Views.ChangeLogWindow { Owner = this }.ShowDialog();
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
