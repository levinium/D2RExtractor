using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace D2RExtractor.Models;

/// <summary>
/// One place an installation's archives get extracted to.
///
/// <para>
/// Until 1.1.8 there was no such thing: an installation's folder was both where the archives were
/// read from and where the files were written, and the two were the same string in the same
/// property. Splitting them is what lets someone extract to a mods folder, or to a staging copy for
/// manual patching, or to both — while the default target, pointing at the game folder itself,
/// behaves exactly as every previous version did.
/// </para>
///
/// <para>
/// Each target carries its own manifest, at <c>&lt;FolderPath&gt;\data\.extraction_manifest.json</c>.
/// That falls out of where the manifest already lived, and it is what makes the upgrade free: an
/// installation extracted by 1.1.7 has its manifest exactly where the default target looks for it,
/// so nothing has to be migrated, moved or re-read.
/// </para>
/// </summary>
public class ExtractionTarget : INotifyPropertyChanged
{
    private string _folderPath = string.Empty;
    private string? _label;
    private bool _enabled = true;

    // Cached manifest state, set by RefreshState.
    private bool _isExtracted;
    private bool _isInterrupted;
    private bool _isInternationalPending;
    private string _statusText = "Ready";

    // -----------------------------------------------------------------------
    // Persisted
    // -----------------------------------------------------------------------

    /// <summary>Absolute path of the folder to extract into. The <c>data\</c> tree is created beneath it.</summary>
    public string FolderPath
    {
        get => _folderPath;
        set
        {
            _folderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ManifestPath));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>
    /// What to call this target in the UI. Optional — an unlabelled target shows its path.
    /// </summary>
    public string? Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    /// <summary>
    /// Whether this target takes part in Extract, Update and Undo.
    ///
    /// <para>
    /// A disabled target is left exactly as it is rather than cleaned up: turning a destination off
    /// is not the same as asking for its 45 GB to be deleted, and the one that does ask for that is
    /// Undo.
    /// </para>
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); }
    }

    // -----------------------------------------------------------------------
    // Computed
    // -----------------------------------------------------------------------

    /// <summary>This target's manifest, which lives inside the folder it writes to.</summary>
    [JsonIgnore]
    public string ManifestPath => Path.Combine(FolderPath, "data", ".extraction_manifest.json");

    /// <summary>Label if there is one, otherwise the path.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? FolderPath : Label!;

    /// <summary>True when this target's manifest exists and is marked complete.</summary>
    [JsonIgnore]
    public bool IsExtracted => _isExtracted;

    /// <summary>True when a previous run into this target was interrupted.</summary>
    [JsonIgnore]
    public bool IsInterrupted => _isInterrupted;

    /// <summary>True when the base extraction is complete but international files need applying.</summary>
    [JsonIgnore]
    public bool IsInternationalPending => _isInternationalPending;

    /// <summary>True when this target is not in the state the settings ask for.</summary>
    [JsonIgnore]
    public bool IsPartiallyExtracted => _isInterrupted || _isInternationalPending;

    /// <summary>True when anything has been written here that Undo could remove.</summary>
    [JsonIgnore]
    public bool HasManifest => _isExtracted || IsPartiallyExtracted;

    /// <summary>Per-target status, shown in the destinations window.</summary>
    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether this target writes into the game folder itself — the only kind D2R's
    /// <c>-direct</c> mode can actually read.
    /// </summary>
    public bool IsGameFolder(string installFolderPath) =>
        !string.IsNullOrWhiteSpace(FolderPath)
        && !string.IsNullOrWhiteSpace(installFolderPath)
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(FolderPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolderPath)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Updates cached manifest state and refreshes the dependent UI properties.</summary>
    /// <param name="manifestIsComplete">null = no manifest, false = interrupted, true = complete.</param>
    public void RefreshState(
        bool? manifestIsComplete,
        bool? manifestInternationalExtracted,
        bool internationalEnabled,
        string? manifestLanguage,
        string? preferredLanguage)
    {
        bool intlSatisfied = manifestInternationalExtracted == true
                             && string.Equals(manifestLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase);

        _isExtracted = manifestIsComplete == true && (!internationalEnabled || intlSatisfied);
        _isInterrupted = manifestIsComplete == false;
        _isInternationalPending = manifestIsComplete == true && internationalEnabled && !intlSatisfied;

        StatusText = _isExtracted ? "Extracted"
                   : _isInterrupted ? "Interrupted"
                   : _isInternationalPending ? "Language pending"
                   : "Not extracted";

        OnPropertyChanged(nameof(IsExtracted));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(IsInternationalPending));
        OnPropertyChanged(nameof(IsPartiallyExtracted));
        OnPropertyChanged(nameof(HasManifest));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
