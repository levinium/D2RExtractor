using System.Diagnostics;
using System.Windows;
using D2RExtractor.Services;
using D2RExtractor.Services.Updates;

// UseWindowsForms is on for the folder browser, so these are ambiguous unqualified.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace D2RExtractor.Views;

/// <summary>
/// What the app found, and the one button that acts on it.
/// <para>
/// The release notes are shown rather than summarized, because this dialog asks
/// someone to let the app replace its own executable and restart itself. That
/// is not a thing to agree to on the strength of a version number alone.
/// </para>
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateVerdict _verdict;
    private readonly Func<bool> _isBusy;
    private CancellationTokenSource? _cancel;
    private bool _installing;

    /// <param name="verdict">What the check concluded.</param>
    /// <param name="isBusy">
    /// Whether an extraction is running. Asked at the moment Update is pressed
    /// rather than when the window opened: this dialog is not modal to the work,
    /// and an extraction can start, or finish, while it sits on screen.
    /// </param>
    public UpdateWindow(UpdateVerdict verdict, Func<bool> isBusy)
    {
        InitializeComponent();

        _verdict = verdict;
        _isBusy = isBusy;

        Render();
    }

    private void Render()
    {
        switch (_verdict.Outcome)
        {
            case UpdateOutcome.Available:
                Heading.Text = $"Version {_verdict.Version} is available";
                Subheading.Text = $"You are running {UpdateService.CurrentVersion}.";
                var notes = ReleaseNotes.ToPlainText(_verdict.Notes);
                NotesText.Text = notes.Length == 0
                    ? "No release notes were published for this version."
                    : notes;

                // A release with no zip attached can still be announced; it just
                // cannot be installed from here.
                PrimaryButton.Content = "Update now";
                PrimaryButton.Visibility = _verdict.CanInstall ? Visibility.Visible : Visibility.Collapsed;
                break;

            case UpdateOutcome.UpToDate:
                Heading.Text = "You are up to date";
                Subheading.Text = $"Version {UpdateService.CurrentVersion} is the latest release.";
                NotesPanel.Visibility = Visibility.Collapsed;
                PrimaryButton.Visibility = Visibility.Collapsed;
                break;

            default:
                Heading.Text = "Could not check for updates";
                Subheading.Text =
                    "The releases page could not be reached, or did not answer in a way this version understands. "
                    + "You are still running " + UpdateService.CurrentVersion + ".";
                NotesPanel.Visibility = Visibility.Collapsed;
                PrimaryButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || _verdict.Asset is not { } asset) return;

        // Swapping the executable out from under a run that is forty minutes
        // into writing 45 GB would leave a half-extracted install and a manifest
        // describing something else.
        if (_isBusy())
        {
            MessageBox.Show(
                this,
                "An extraction is still running. Let it finish, or cancel it, before updating.",
                "Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _installing = true;
        _cancel = new CancellationTokenSource();

        PrimaryButton.IsEnabled = false;
        PageButton.IsEnabled = false;
        CloseButton.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Starting download...";

        var size = asset.Size > 0 ? $" of {asset.Size / 1024d / 1024d:N0} MB" : string.Empty;

        var progress = new Progress<double>(fraction =>
        {
            DownloadProgress.Value = fraction * 100;
            ProgressText.Text = $"Downloading... {fraction:P0}{size}";
        });

        var failure = await new UpdateInstaller()
            .InstallAsync(asset, progress, _cancel.Token)
            .ConfigureAwait(true);

        if (failure is null)
        {
            // The swap script is waiting for this process to exit. Everything
            // downloaded is verified and unpacked by now, so the only thing left
            // is to get out of its way.
            ProgressText.Text = "Restarting to finish the update...";
            Application.Current.Shutdown();
            return;
        }

        _installing = false;
        _cancel.Dispose();
        _cancel = null;

        ProgressPanel.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = true;
        PageButton.IsEnabled = true;
        CloseButton.Content = "Close";

        MessageBox.Show(this, failure.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void PageButton_Click(object sender, RoutedEventArgs e)
    {
        var url = _verdict.Url ?? UpdateService.PageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // A machine with no default browser is one where nothing useful can
            // happen here, and throwing out of this would be an absurd way to
            // lose the app.
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Mid-download the same button is Cancel. The install has written
        // nothing outside TEMP at this point, so stopping costs only the bytes.
        if (_installing)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }
}
