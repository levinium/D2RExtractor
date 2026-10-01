using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace D2RExtractor.Models;

/// <summary>
/// Represents a single D2R installation folder managed by the extractor.
/// Implements INotifyPropertyChanged so WPF bindings update automatically.
/// </summary>
public class D2RInstallation : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _folderPath = string.Empty;
    private bool _isExtracting;
    private bool _isQueued;
    private bool _isEnumerating;
    private string _enumeratingFile = string.Empty;
    private double _progress;
    private string _statusText = "Ready";
    private int _filesExtracted;
    private int _totalFiles;

    // Cached manifest completion state — set by RefreshState(...).
    private bool _isExtracted;
    private bool _isInterrupted;
    private bool _isInternationalPending;

    // -----------------------------------------------------------------------
    // Persisted properties (saved to settings.json)
    // -----------------------------------------------------------------------

    /// <summary>User-defined display name for this installation.</summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Absolute path to the D2R installation folder (e.g. "C:\Program Files (x86)\Diablo II Resurrected").
    ///
    /// <para>
    /// This is the SOURCE — where the game archives are read from. Since 1.1.8 it is no longer
    /// necessarily where files are written; see <see cref="Targets"/>.
    /// </para>
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set
        {
            _folderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExtracted));
            OnPropertyChanged(nameof(IsPartiallyExtracted));
            OnPropertyChanged(nameof(ManifestPath));
            RaiseActionState();
        }
    }

    /// <summary>
    /// Where this installation's archives get extracted to.
    ///
    /// <para>
    /// Empty or absent means the default: one target pointing at <see cref="FolderPath"/>, which is
    /// what every version before 1.1.8 did unconditionally. Settings files written by those
    /// versions have no such property, so they deserialise to empty and
    /// <see cref="EffectiveTargets"/> supplies the default — an upgrade with nothing to migrate.
    /// </para>
    /// </summary>
    public List<ExtractionTarget> Targets { get; set; } = new();

    // -----------------------------------------------------------------------
    // Computed / runtime properties (NOT persisted)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The targets to actually operate on: whatever is configured, or the implicit default when
    /// nothing is.
    ///
    /// <para>
    /// Every caller goes through this rather than <see cref="Targets"/>, so "no targets configured"
    /// never has to be handled twice. The synthesized default is created once and kept, so its
    /// runtime state survives a refresh.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ExtractionTarget> EffectiveTargets
    {
        get
        {
            if (Targets.Count > 0) return Targets;

            _implicitTarget ??= new ExtractionTarget { FolderPath = FolderPath, Label = "Game folder" };

            // The install's folder can be edited after the fact; the implicit target follows it.
            if (!string.Equals(_implicitTarget.FolderPath, FolderPath, StringComparison.OrdinalIgnoreCase))
                _implicitTarget.FolderPath = FolderPath;

            return _implicitSingleton ??= new[] { _implicitTarget };
        }
    }

    private ExtractionTarget? _implicitTarget;
    private ExtractionTarget[]? _implicitSingleton;

    /// <summary>The targets a run should actually touch.</summary>
    [JsonIgnore]
    public IReadOnlyList<ExtractionTarget> ActiveTargets =>
        EffectiveTargets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.FolderPath)).ToList();

    /// <summary>True once this installation has more than the one default destination.</summary>
    [JsonIgnore]
    public bool HasCustomTargets =>
        Targets.Count > 1
        || (Targets.Count == 1 && !Targets[0].IsGameFolder(FolderPath));

    /// <summary>
    /// True when nothing is being written into the game folder, so D2R itself gains nothing from
    /// this installation's extractions and <c>-direct</c> would find no files.
    /// </summary>
    [JsonIgnore]
    public bool SkipsGameFolder => !ActiveTargets.Any(t => t.IsGameFolder(FolderPath));

    /// <summary>Manifest path of the default (game folder) destination. Kept for the status summary.</summary>
    [JsonIgnore]
    public string ManifestPath =>
        Path.Combine(FolderPath, "data", ".extraction_manifest.json");

    /// <summary>True when the extraction manifest exists on disk and is marked complete.</summary>
    [JsonIgnore]
    public bool IsExtracted => _isExtracted;

    /// <summary>
    /// True when the extraction is not currently in the state the user asked for: either it was
    /// interrupted, or the international setting changed since it ran.
    /// </summary>
    [JsonIgnore]
    public bool IsPartiallyExtracted => _isInterrupted || _isInternationalPending;

    /// <summary>
    /// True when a previous extraction was interrupted (manifest present but marked incomplete).
    /// <para>
    /// Distinct from <see cref="IsInternationalPending"/> because the two need different handling:
    /// an interrupted extraction is resumed by writing the files that are missing, whereas a
    /// pending language change rewrites files that already exist.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsInterrupted => _isInterrupted;

    /// <summary>True when the base extraction is complete but international files are missing or in the wrong language.</summary>
    [JsonIgnore]
    public bool IsInternationalPending => _isInternationalPending;

    /// <summary>True while an extraction or undo operation is running.</summary>
    [JsonIgnore]
    public bool IsExtracting
    {
        get => _isExtracting;
        set
        {
            _isExtracting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsPartiallyExtracted));
            RaiseActionState();
        }
    }

    [JsonIgnore]
    public bool IsIdle => !_isExtracting;

    /// <summary>True while this installation is waiting in the Extract All / Undo All queue.</summary>
    [JsonIgnore]
    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            _isQueued = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsPartiallyExtracted));
            RaiseActionState();
        }
    }

    /// <summary>True while extracting, undoing, or waiting in the queue.</summary>
    [JsonIgnore]
    public bool IsBusy => _isExtracting || _isQueued;

    /// <summary>True during the CASC file-list enumeration phase (indeterminate progress).</summary>
    [JsonIgnore]
    public bool IsEnumerating
    {
        get => _isEnumerating;
        set { _isEnumerating = value; OnPropertyChanged(); }
    }

    /// <summary>The virtual path of the file currently being enumerated (updated ~every 500 ms).</summary>
    [JsonIgnore]
    public string EnumeratingFile
    {
        get => _enumeratingFile;
        set { _enumeratingFile = value; OnPropertyChanged(); }
    }

    /// <summary>Extraction progress 0–100.</summary>
    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    /// <summary>Human-readable status shown in the UI.</summary>
    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public int FilesExtracted
    {
        get => _filesExtracted;
        set { _filesExtracted = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public int TotalFiles
    {
        get => _totalFiles;
        set { _totalFiles = value; OnPropertyChanged(); }
    }

    /// <summary>True when at least one active target has anything Undo could remove.</summary>
    /// <remarks>
    /// Asked of the targets rather than of the aggregate flags, which is the difference that
    /// matters once there is more than one destination: with one extracted and one empty, neither
    /// <see cref="IsExtracted"/> nor <see cref="IsPartiallyExtracted"/> is true, and reading Undo
    /// off those would grey it out while 45 GB sat on disk with no other way to remove it.
    /// </remarks>
    [JsonIgnore]
    public bool AnyTargetExtracted => ActiveTargets.Any(t => t.HasManifest);

    /// <summary>True when a full extraction is what the primary button would start.</summary>
    [JsonIgnore]
    public bool CanExtract => !AnyTargetExtracted && !IsBusy && ActiveTargets.Count > 0;

    /// <summary>
    /// True when an extraction exists that can be brought in line with the archives instead of
    /// being redone — including an interrupted one, which resumes rather than restarting.
    /// </summary>
    [JsonIgnore]
    public bool CanUpdate => AnyTargetExtracted && !IsBusy;

    /// <summary>Primary action button enabled state.</summary>
    [JsonIgnore]
    public bool CanPrimaryAction => CanExtract || CanUpdate;

    /// <summary>
    /// Label for the primary action button. The same button extracts, resumes or updates depending
    /// on what this installation currently needs.
    /// </summary>
    [JsonIgnore]
    public string PrimaryActionLabel =>
        IsInterrupted        ? "Resume"
        : AnyTargetExtracted ? "Update"
        : "Extract";

    /// <summary>Tooltip explaining what the primary action button will do right now.</summary>
    [JsonIgnore]
    public string PrimaryActionTooltip
    {
        get
        {
            string scope = ActiveTargets.Count > 1
                ? $" Runs for all {ActiveTargets.Count} destinations."
                : string.Empty;

            return (IsInterrupted ? "Resume the interrupted extraction — files already written are kept"
                 : IsInternationalPending ? "Apply the current international file settings"
                 : AnyTargetExtracted ? "Compare the game archives against the extracted files and rewrite only what changed"
                 : "Extract the game archives to this installation's destination folder") + scope;
        }
    }

    /// <summary>Undo button enabled state. True when any target has files recorded and not busy.</summary>
    [JsonIgnore]
    public bool CanUndo => AnyTargetExtracted && !IsBusy;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Rolls every active target's state up into the one status and button the row shows.
    ///
    /// <para>
    /// Each target's own state is refreshed first, from its own manifest — see
    /// <c>MainWindow.RefreshInstallState</c>. This method only combines them, so the rule for
    /// combining lives in one place rather than being re-derived by each caller.
    /// </para>
    ///
    /// <para>
    /// "Extracted" requires ALL active targets to be extracted, not any. A row that reads
    /// "Extracted" while one of its two destinations is empty would be lying in the direction that
    /// costs someone a working mods folder they believed was populated.
    /// </para>
    /// </summary>
    public void RefreshAggregateState()
    {
        var active = ActiveTargets;

        _isExtracted = active.Count > 0 && active.All(t => t.IsExtracted);
        _isInterrupted = active.Any(t => t.IsInterrupted);

        // An interrupted target is the more urgent of the two, and resuming it is what the button
        // should offer; a pending language change is reported only once nothing is half-written.
        _isInternationalPending = !_isInterrupted && active.Any(t => t.IsInternationalPending);

        OnPropertyChanged(nameof(IsExtracted));
        OnPropertyChanged(nameof(IsPartiallyExtracted));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(IsInternationalPending));
        OnPropertyChanged(nameof(HasCustomTargets));
        OnPropertyChanged(nameof(SkipsGameFolder));
        OnPropertyChanged(nameof(TargetSummary));
        RaiseActionState();

        if (!IsExtracting)
            StatusText = BuildStatusText(active);
    }

    private string BuildStatusText(IReadOnlyList<ExtractionTarget> active)
    {
        if (active.Count == 0) return "No destinations";

        // With one destination the counts are noise, so the wording is exactly what it always was.
        if (active.Count == 1)
        {
            return _isExtracted ? "Extracted"
                 : IsPartiallyExtracted ? "Partial"
                 : "Ready";
        }

        int done = active.Count(t => t.IsExtracted);

        return _isExtracted ? $"Extracted ({done} of {active.Count})"
             : done > 0 || IsPartiallyExtracted ? $"Partial ({done} of {active.Count})"
             : "Ready";
    }

    /// <summary>One line naming how many destinations there are, for the row's tooltip.</summary>
    [JsonIgnore]
    public string TargetSummary
    {
        get
        {
            var active = ActiveTargets;
            if (active.Count <= 1) return FolderPath;

            return $"{active.Count} destinations:\n" + string.Join("\n", active.Select(t => "  " + t.FolderPath));
        }
    }

    /// <summary>
    /// Re-raises every property the action buttons bind to.
    ///
    /// <para>
    /// Called from all four places that can change them — <see cref="RefreshState"/>, the
    /// <see cref="IsExtracting"/> and <see cref="IsQueued"/> setters, and the
    /// <see cref="FolderPath"/> setter. Missing one leaves a button showing a stale label or
    /// enabled state, so they all funnel through here rather than each listing the properties.
    /// </para>
    /// </summary>
    private void RaiseActionState()
    {
        OnPropertyChanged(nameof(AnyTargetExtracted));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanPrimaryAction));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(PrimaryActionTooltip));
        OnPropertyChanged(nameof(TargetSummary));
    }

    // -----------------------------------------------------------------------
    // INotifyPropertyChanged
    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
