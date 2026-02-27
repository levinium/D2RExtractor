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

    // Cached manifest completion state — set by RefreshState(bool? manifestIsComplete).
    // null  → no manifest on disk  → Ready
    // false → manifest exists, incomplete → Partial
    // true  → manifest exists, complete   → Extracted
    private bool _isExtracted;
    private bool _isPartiallyExtracted;

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
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanUndo));
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

    /// <summary>True when the extraction manifest exists on disk but is not complete (interrupted extraction).</summary>
    [JsonIgnore]
    public bool IsPartiallyExtracted => _isPartiallyExtracted;

    /// <summary>True while an extraction or undo operation is running.</summary>
    [JsonIgnore]
    public bool IsExtracting
    {
        get => _isExtracting;
        set
        {
            _isExtracting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsPartiallyExtracted));
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
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsPartiallyExtracted));
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

    /// <summary>Extract button enabled state. True when not fully extracted and not busy (includes Partial state).</summary>
    [JsonIgnore]
    public bool CanExtract => !IsExtracted && !IsBusy;

    /// <summary>Undo button enabled state. True when any manifest exists (full or partial) and not busy.</summary>
    [JsonIgnore]
    public bool CanUndo => (IsExtracted || IsPartiallyExtracted) && !IsBusy;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Updates the manifest completion state and refreshes all dependent UI properties.
    /// Pass the result of <c>ManifestService.LoadManifest(this)?.IsComplete</c>:
    ///   null  → no manifest on disk (Ready)
    ///   false → manifest exists but incomplete (Partial — interrupted extraction)
    ///   true  → manifest exists and complete (Extracted)
    /// </summary>
    public void RefreshState(bool? manifestIsComplete)
    {
        _isExtracted = manifestIsComplete == true;
        _isPartiallyExtracted = manifestIsComplete == false; // false but not null

        OnPropertyChanged(nameof(IsExtracted));
        OnPropertyChanged(nameof(IsPartiallyExtracted));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanUndo));

        if (!IsExtracting)
        {
            StatusText = _isExtracted        ? "Extracted"
                       : _isPartiallyExtracted ? "Partial"
                       : "Ready";
        }
    }

    // -----------------------------------------------------------------------
    // INotifyPropertyChanged
    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
