using System;
using System.ComponentModel;
using System.IO;
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

    /// <summary>Absolute path to the D2R installation folder (e.g. "C:\Program Files (x86)\Diablo II Resurrected").</summary>
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

    // -----------------------------------------------------------------------
    // Computed / runtime properties (NOT persisted)
    // -----------------------------------------------------------------------

    /// <summary>Manifest file path for this installation.</summary>
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

    /// <summary>True when a full extraction is what the primary button would start.</summary>
    [JsonIgnore]
    public bool CanExtract => !IsExtracted && !IsPartiallyExtracted && !IsBusy;

    /// <summary>
    /// True when an extraction exists that can be brought in line with the archives instead of
    /// being redone — including an interrupted one, which resumes rather than restarting.
    /// </summary>
    [JsonIgnore]
    public bool CanUpdate => (IsExtracted || IsPartiallyExtracted) && !IsBusy;

    /// <summary>Primary action button enabled state.</summary>
    [JsonIgnore]
    public bool CanPrimaryAction => CanExtract || CanUpdate;

    /// <summary>
    /// Label for the primary action button. The same button extracts, resumes or updates depending
    /// on what this installation currently needs.
    /// </summary>
    [JsonIgnore]
    public string PrimaryActionLabel =>
        IsExtracted     ? "Update"
        : IsInterrupted ? "Resume"
        : IsPartiallyExtracted ? "Update"
        : "Extract";

    /// <summary>Tooltip explaining what the primary action button will do right now.</summary>
    [JsonIgnore]
    public string PrimaryActionTooltip =>
        IsExtracted            ? "Compare the game archives against the extracted files and rewrite only what changed"
        : IsInterrupted        ? "Resume the interrupted extraction — files already written are kept"
        : IsInternationalPending ? "Apply the current international file settings"
        : "Extract the game archives to this installation folder";

    /// <summary>Undo button enabled state. True when any manifest exists (full or partial) and not busy.</summary>
    [JsonIgnore]
    public bool CanUndo => (IsExtracted || IsPartiallyExtracted) && !IsBusy;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Updates the manifest state and refreshes all dependent UI properties.
    /// </summary>
    /// <param name="manifestIsComplete">
    ///   null  → no manifest (Ready)
    ///   false → manifest exists but incomplete (Partial — interrupted extraction)
    ///   true  → manifest exists and complete
    /// </param>
    /// <param name="manifestInternationalExtracted">
    ///   null or false → international files not extracted
    ///   true          → international files extracted
    /// </param>
    /// <param name="internationalEnabled">
    ///   Whether the "Extract international files" setting is currently on.
    /// </param>
    /// <param name="manifestLanguage">The language code extracted in the manifest (null if none).</param>
    /// <param name="preferredLanguage">The language code currently selected in preferences.</param>
    public void RefreshState(bool? manifestIsComplete, bool? manifestInternationalExtracted,
        bool internationalEnabled, string? manifestLanguage = null, string? preferredLanguage = null)
    {
        // International is satisfied when extracted AND the language matches what's configured.
        bool intlSatisfied = manifestInternationalExtracted == true
                             && string.Equals(manifestLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase);

        // Fully extracted = base complete AND (int'l not required OR int'l done with correct language)
        _isExtracted = manifestIsComplete == true
                       && (!internationalEnabled || intlSatisfied);

        // An interrupted extraction and a pending international change both read as "Partial" to
        // the user, but they are different situations — see IsInterrupted.
        _isInterrupted = manifestIsComplete == false;
        _isInternationalPending = manifestIsComplete == true && internationalEnabled && !intlSatisfied;

        OnPropertyChanged(nameof(IsExtracted));
        OnPropertyChanged(nameof(IsPartiallyExtracted));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(IsInternationalPending));
        RaiseActionState();

        if (!IsExtracting)
        {
            StatusText = _isExtracted           ? "Extracted"
                       : IsPartiallyExtracted   ? "Partial"
                       : "Ready";
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
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanPrimaryAction));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(PrimaryActionTooltip));
    }

    // -----------------------------------------------------------------------
    // INotifyPropertyChanged
    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
