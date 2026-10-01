using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using D2RExtractor.Services.Updates;

namespace D2RExtractor.Services;

/// <summary>Why an install could not be attempted, or did not finish.</summary>
public sealed record InstallFailure(string Message);

/// <summary>
/// Downloads a release and replaces this installation with it.
/// <para>
/// The app cannot overwrite its own executable while it is running, so the
/// swap is done by a short script that waits for this process to exit, moves
/// the new files in, and starts the app again. Everything that CAN be done
/// before that point — downloading, verifying, unpacking — is done first, so
/// the irreversible part is as short as possible and runs only against files
/// already checked.
/// </para>
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>The executable the swap script relaunches, and the proof a staging folder is sound.</summary>
    private const string ExeName = "D2RExtractor.exe";

    private readonly HttpClient _http;

    public UpdateInstaller(HttpClient? http = null)
    {
        _http = http ?? UpdateService.CreateClient();
    }

    /// <summary>The folder this build is running from.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// Whether the app could replace its own files.
    /// <para>
    /// Unzipped into Program Files, or onto a read-only share, it cannot — and
    /// finding that out AFTER downloading 63 MB and closing the app would be the
    /// worst possible moment. Asked first, a person is simply sent to the
    /// releases page instead.
    /// </para>
    /// </summary>
    public static bool CanWriteToInstallDirectory()
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches the release, checks it, unpacks it, and hands the swap to a
    /// script. Returns a failure to report, or null when the app is about to be
    /// replaced and the caller should shut down.
    /// </summary>
    /// <param name="asset">The zip named by the release feed.</param>
    /// <param name="progress">Fraction downloaded, 0 to 1.</param>
    public async Task<InstallFailure?> InstallAsync(
        ReleaseAsset asset,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!CanWriteToInstallDirectory())
            return new InstallFailure(
                "This copy is in a folder the app is not allowed to write to. Download the new version manually, or move the app somewhere like your Documents folder.");

        var work = Path.Combine(Path.GetTempPath(), $"D2RExtractor-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(work);

            var zip = Path.Combine(work, asset.Name);

            var download = await DownloadAsync(asset, zip, progress, ct).ConfigureAwait(false);
            if (download is not null) return download;

            var verify = Verify(zip, asset);
            if (verify is not null) return verify;

            var staging = Path.Combine(work, "staging");
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);

            // Some releases zip the folder itself rather than its contents, so
            // the exe can be one level down. Find it rather than assume.
            var root = FindPayloadRoot(staging);
            if (root is null)
                return new InstallFailure(
                    $"The downloaded file did not contain {ExeName}. Nothing has been changed.");

            File.Delete(zip);

            Swap(root, work);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDelete(work);
            return new InstallFailure("Update cancelled. Nothing has been changed.");
        }
        catch (Exception ex)
        {
            TryDelete(work);
            return new InstallFailure($"The update could not be prepared: {ex.Message}. Nothing has been changed.");
        }
    }

    private async Task<InstallFailure?> DownloadAsync(
        ReleaseAsset asset,
        string destination,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new InstallFailure($"The download failed ({(int)response.StatusCode}). Nothing has been changed.");

        // Content-Length is what the server actually promises; the feed's size is
        // the fallback when a proxy strips it.
        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            if (total > 0) progress?.Report(Math.Min(1d, (double)done / total));
        }

        return null;
    }

    /// <summary>
    /// Checks the download is the file the feed described.
    /// <para>
    /// This is the one check that matters. The app is about to replace its own
    /// executable and run it, so what arrived has to be what was published — not
    /// a truncated download, and not a captive portal's login page saved under a
    /// .zip name. GitHub records a SHA-256 for every asset it stores; when it is
    /// present it is compared, and a mismatch stops everything.
    /// </para>
    /// <para>
    /// Assets uploaded before GitHub added the field carry no digest. Those fall
    /// back to a length check, which catches the truncation and the login page
    /// but proves nothing about the contents.
    /// </para>
    /// </summary>
    private static InstallFailure? Verify(string path, ReleaseAsset asset)
    {
        var length = new FileInfo(path).Length;

        if (asset.Size > 0 && length != asset.Size)
            return new InstallFailure(
                $"The download was {length:N0} bytes but should have been {asset.Size:N0}. Nothing has been changed.");

        if (string.IsNullOrWhiteSpace(asset.Digest)) return null;

        var expected = asset.Digest.Split(':', 2) switch
        {
            ["sha256", var hex] => hex,
            _ => null,
        };

        // An algorithm this build does not know is not a failure to report. The
        // length already matched, and refusing every future digest format would
        // be a time bomb in an app people cannot easily update by then.
        if (expected is null) return null;

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : new InstallFailure("The download did not match its published checksum, so it has been discarded. Nothing has been changed.");
    }

    /// <summary>
    /// The folder inside the extracted zip that actually holds the app: the
    /// staging root, or a single subfolder within it.
    /// </summary>
    private static string? FindPayloadRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, ExeName))) return staging;

        foreach (var child in Directory.GetDirectories(staging))
        {
            if (File.Exists(Path.Combine(child, ExeName))) return child;
        }

        return null;
    }

    /// <summary>
    /// Hands the replacement to a script and leaves.
    /// <para>
    /// The old installation is copied aside first and restored if the swap
    /// fails, because the alternative — a half-overwritten folder and no way
    /// back — turns a failed update into a lost app. Robocopy does the copying:
    /// it retries a locked file instead of giving up on it, which matters in the
    /// second after a process exits and a virus scanner opens everything it left
    /// behind.
    /// </para>
    /// </summary>
    private static void Swap(string staging, string work)
    {
        var install = InstallDirectory;
        var backup = Path.Combine(Path.GetTempPath(), $"D2RExtractor-backup-{Guid.NewGuid():N}");
        var script = Path.Combine(Path.GetTempPath(), $"D2RExtractor-swap-{Guid.NewGuid():N}.ps1");
        var exe = Path.Combine(install, ExeName);

        File.WriteAllText(script, SwapScript, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                // The script is one this app just wrote to a path it chose. Bypass
                // is here so a machine-wide policy of Restricted - the default on
                // Windows client SKUs - does not make the updater a no-op.
                "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(),
                "-Staging", staging,
                "-Install", install,
                "-Backup", backup,
                "-Exe", exe,
                "-Work", work,
            },
        });
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A leftover folder in TEMP is not worth telling anyone about.
        }
    }

    /// <summary>
    /// The swap, as it runs after this process is gone.
    /// <para>
    /// Embedded rather than shipped as a file beside the exe: this build is
    /// published as a single file, and a script sitting next to it would be one
    /// more thing a zip could arrive without.
    /// </para>
    /// </summary>
    private const string SwapScript = """
        param(
            [int]    $ProcessId,
            [string] $Staging,
            [string] $Install,
            [string] $Backup,
            [string] $Exe,
            [string] $Work
        )

        $ErrorActionPreference = 'Stop'

        # Wait for the app to let go of its own files. Capped, because hanging
        # here forever would leave the update permanently half-done and silent.
        for ($i = 0; $i -lt 60; $i++) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }

        # Robocopy's exit codes are a bit field, and anything under 8 means it
        # did the job (1 = copied, 2 = extra files present, 3 = both). Only 8 and
        # above are real failures, so $LASTEXITCODE cannot be tested for 0.
        function Copy-Tree($from, $to) {
            robocopy $from $to /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed with $LASTEXITCODE" }
        }

        try {
            Copy-Tree $Install $Backup
            Copy-Tree $Staging $Install
        }
        catch {
            # Put back what was there. A failed update should cost nothing but
            # the download.
            if (Test-Path $Backup) {
                robocopy $Backup $Install /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            }
        }

        if (Test-Path $Exe) { Start-Process -FilePath $Exe }

        # Tidy up, including this script. Best effort: a file left in TEMP is not
        # worth failing over, and there is nobody here to tell.
        foreach ($path in @($Work, $Backup)) {
            try { if (Test-Path $path) { Remove-Item $path -Recurse -Force } } catch { }
        }

        try { Remove-Item $PSCommandPath -Force } catch { }
        """;
}
