using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using D2RExtractor.Models;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace D2RExtractor.Views;

/// <summary>What the caller should do once this window closes.</summary>
public enum DestinationRequest
{
    None,

    /// <summary>Extract or update one destination.</summary>
    Apply,

    /// <summary>Undo one destination.</summary>
    Undo,
}

/// <summary>
/// Manages where one installation's archives get extracted to.
///
/// <para>
/// Running an extraction is NOT done here. The window hands the request back and closes, and the
/// main window runs it through the same queue as everything else — so a destination extracted from
/// here reports its progress on the row, cancels with the same button, and cannot start a second
/// run alongside one already going.
/// </para>
/// </summary>
public partial class DestinationsWindow : Window
{
    private readonly D2RInstallation _install;
    private readonly ObservableCollection<ExtractionTarget> _targets = new();

    /// <summary>What the caller should run, if anything.</summary>
    public DestinationRequest Request { get; private set; } = DestinationRequest.None;

    /// <summary>The destination <see cref="Request"/> applies to.</summary>
    public ExtractionTarget? RequestTarget { get; private set; }

    public DestinationsWindow(D2RInstallation install)
    {
        InitializeComponent();

        _install = install;
        Heading.Text = $"Destinations — {install.Name}";

        // Materialize the implicit default into a real, editable entry the first time this window
        // is opened. Until now it existed only as a runtime stand-in; someone who has come here to
        // manage destinations needs the game folder to be one of the things they can manage.
        foreach (ExtractionTarget target in install.EffectiveTargets)
            _targets.Add(target);

        TargetsList.ItemsSource = _targets;

        foreach (ExtractionTarget target in _targets)
            target.PropertyChanged += OnTargetChanged;

        RefreshWarning();
    }

    private void OnTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtractionTarget.Enabled)) RefreshWarning();
    }

    private void RefreshWarning()
    {
        bool anyGameFolder = _targets.Any(t => t.Enabled && t.IsGameFolder(_install.FolderPath));
        GameFolderWarning.Visibility = anyGameFolder ? Visibility.Collapsed : Visibility.Visible;
    }

    // -----------------------------------------------------------------------
    // Editing the list
    // -----------------------------------------------------------------------

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder to extract into. A data\\ folder is created inside it.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string folder = dlg.SelectedPath;

        string? problem = Validate(folder);
        if (problem != null)
        {
            MessageBox.Show(this, problem, "Cannot use that folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = new ExtractionTarget
        {
            FolderPath = folder,
            Label = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
        };

        target.PropertyChanged += OnTargetChanged;
        _targets.Add(target);
        Commit();
        RefreshWarning();
    }

    /// <summary>
    /// Whether a folder can be added as a destination.
    /// </summary>
    /// <remarks>
    /// The nesting check is the one that matters. A destination inside another one would have the
    /// outer one's scan walk the inner one's files, see paths the archives do not contain, and
    /// delete them as orphans on the next update — each destination quietly eating the other.
    /// </remarks>
    private string? Validate(string folder)
    {
        string? full = DestinationPaths.Normalize(folder);
        if (full is null) return "That path is not valid.";

        if (!Directory.Exists(full))
            return "That folder does not exist.";

        foreach (ExtractionTarget existing in _targets)
        {
            if (DestinationPaths.AreSame(full, existing.FolderPath))
                return "That folder is already a destination for this installation.";

            if (DestinationPaths.Overlap(full, existing.FolderPath))
                return "Destinations cannot be nested inside one another.\n\n" +
                       $"This folder and '{existing.DisplayName}' overlap, which would make each one " +
                       "treat the other's files as leftovers and delete them on the next update.";
        }

        return null;
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ExtractionTarget target) return;

        if (_targets.Count == 1)
        {
            MessageBox.Show(this,
                "An installation needs at least one destination. Add another one first, or remove " +
                "the installation itself from the main window.",
                "Cannot remove", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string extra = target.HasManifest
            ? "\n\nThis destination still holds extracted files. Removing it here does NOT delete them — " +
              "they will be left on disk with nothing tracking them. Use Undo first if you want them gone."
            : string.Empty;

        var confirm = MessageBox.Show(this,
            $"Stop extracting to:\n{target.FolderPath}{extra}\n\nRemove it?",
            "Remove destination", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        target.PropertyChanged -= OnTargetChanged;
        _targets.Remove(target);
        Commit();
        RefreshWarning();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not ExtractionTarget target)
        {
            MessageBox.Show(this, "Select a destination first.", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TextPromptWindow("Rename destination", "Name:", target.Label ?? string.Empty)
        {
            Owner = this,
        };

        if (dlg.ShowDialog() != true) return;

        target.Label = string.IsNullOrWhiteSpace(dlg.Value) ? null : dlg.Value.Trim();
        Commit();
    }

    /// <summary>
    /// Writes the edited list back to the installation and asks the owner to persist.
    ///
    /// <para>
    /// Raised on every change rather than on Close, so a crash or a force-quit cannot lose a
    /// destination someone has already extracted 45 GB into.
    /// </para>
    ///
    /// <para>
    /// This window must NOT write settings.json itself. The file holds every managed installation
    /// and is written whole, so saving from here — with only the one installation this window knows
    /// about — would silently delete all the others. The owner has the full list; it does the
    /// writing.
    /// </para>
    /// </summary>
    private void Commit()
    {
        _install.Targets = _targets.ToList();
        Saved?.Invoke();
    }

    /// <summary>Raised when the target list changes, so the caller can persist and re-read state.</summary>
    public event Action? Saved;

    // -----------------------------------------------------------------------
    // Per-destination operations, handed back to the main window
    // -----------------------------------------------------------------------

    private void ExtractOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ExtractionTarget target) return;

        Request = DestinationRequest.Apply;
        RequestTarget = target;
        DialogResult = true;
    }

    private void UndoOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ExtractionTarget target) return;

        var confirm = MessageBox.Show(this,
            $"Remove every file this app extracted to:\n{target.FolderPath}\n\n" +
            "Only files recorded for this destination are deleted; anything else in that folder is " +
            "left alone.\n\nProceed?",
            "Undo destination", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        Request = DestinationRequest.Undo;
        RequestTarget = target;
        DialogResult = true;
    }

    private void Changes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ExtractionTarget target) return;

        new ChangesWindow(target) { Owner = this }.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
