using System;
using System.Collections.Generic;
using System.IO;
using D2RExtractor.Models;
using Newtonsoft.Json;

namespace D2RExtractor.Services;

/// <summary>
/// Reads and writes the per-installation extraction manifest and the global app settings.
/// </summary>
public static class ManifestService
{
    // -----------------------------------------------------------------------
    // App settings (list of managed installations)
    // -----------------------------------------------------------------------

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D2RExtractor");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>Loads the saved list of D2R installations. Returns an empty list if none saved yet.</summary>
    public static List<D2RInstallation> LoadInstallations()
    {
        if (!File.Exists(SettingsPath))
            return new List<D2RInstallation>();

        try
        {
            string json = File.ReadAllText(SettingsPath);
            return JsonConvert.DeserializeObject<List<D2RInstallation>>(json)
                   ?? new List<D2RInstallation>();
        }
        catch
        {
            return new List<D2RInstallation>();
        }
    }

    /// <summary>Persists the current list of D2R installations to disk.</summary>
    public static void SaveInstallations(IEnumerable<D2RInstallation> installations)
    {
        Directory.CreateDirectory(SettingsDir);
        string json = JsonConvert.SerializeObject(installations, Formatting.Indented);
        File.WriteAllText(SettingsPath, json);
    }

    // -----------------------------------------------------------------------
    // Per-installation extraction manifest
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads the extraction manifest for the given installation.
    /// Returns null if no manifest exists (not yet extracted).
    /// </summary>
    public static ExtractionManifest? LoadManifest(D2RInstallation installation)
    {
        if (!File.Exists(installation.ManifestPath))
            return null;

        try
        {
            string json = File.ReadAllText(installation.ManifestPath);
            return JsonConvert.DeserializeObject<ExtractionManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves the extraction manifest for the given installation.</summary>
    public static void SaveManifest(D2RInstallation installation, ExtractionManifest manifest)
    {
        string dir = Path.GetDirectoryName(installation.ManifestPath)!;
        Directory.CreateDirectory(dir);
        string json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        File.WriteAllText(installation.ManifestPath, json);
    }

    /// <summary>Deletes the extraction manifest for the given installation (called during Undo).</summary>
    public static void DeleteManifest(D2RInstallation installation)
    {
        if (File.Exists(installation.ManifestPath))
            File.Delete(installation.ManifestPath);
    }
}
