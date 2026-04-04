# D2R File Extractor

A WPF desktop app with easy 1-click extraction of Diablo 2: Resurrected CASC game archives for faster load times. Also lets you undo the extraction before game updates and easily re-apply the extraction after updating.

[⬇️ Download D2RExtractor v1.1.3 (Standalone ZIP)](https://github.com/levinium/D2RExtractor/releases/download/D2RExtractor_1.1.3/D2RExtractor-Compiled-Standalone.zip)

[YouTube Installation & Usage Guide](https://youtu.be/SYKQdQK1_gQ)

---

## How it works

D2R normally loads assets from compressed CASC archives at runtime. By pre-extracting the `data\global\`, `data\hd\`, and `data\local\` folders to plain files, the game loads them directly — dramatically reducing load times.

This app automates that process for one or more D2R installations and keeps a manifest of every extracted file so the extraction can be fully reversed.

This app uses Ladislav Zezula's native CascLib library — the same engine that powers Ladik's CASC Viewer.

---

## Prerequisites

### Disk space

Extraction writes ~40–45 GB of data per D2R installation. More if also extracting international files. Ensure sufficient free space.

### Steam installations

Steam D2R installations (patch 3.1.2+) are fully supported. The app automatically detects when the standard CASC open fails and falls back to CDN-enabled mode. **An internet connection is required during extraction for Steam users** — game data is downloaded from Blizzard's CDN. Extraction time will depend on your download speed.

The bundled CascLib.dll includes patches for the Steam D2R CASC layout. See [CascLib issue #285](https://github.com/ladislav-zezula/CascLib/issues/285) for upstream context.

---

## Build

```
dotnet build D2RExtractor.sln -c Release -p:Platform=x64
```

Output: `D2RExtractor\bin\x64\Release\net8.0-windows\D2RExtractor.exe`

---

## Usage

1. Launch `D2RExtractor.exe`
2. Click **+ Add Installation** and select your D2R base folder (the one containing the `Data` subfolder) - Repeat for any additional D2R folders.
3. Click the **Gear** icon at the top right corner for settings. This allows for extraction of international files (multi-language dubbing) if needed.
4. Click **Extract** — extraction runs in the background (Battle.net: ~30–45 min; Steam: depends on download speed)
5. Launch D2R with the "-direct -txt" command line options and enjoy faster load times.

### Before updating D2R

1. Click **Undo Extraction** — removes all extracted files using the saved manifest
2. Update D2R normally via your game launcher
3. Re-extract after the update

---

## File layout

```
D2RExtractor\
├── D2RExtractor.sln
└── D2RExtractor\
    ├── App.xaml(.cs)
    ├── MainWindow.xaml(.cs)
    ├── Models\
    │   ├── D2RInstallation.cs       Observable model for each managed installation
    │   └── ExtractionManifest.cs    Per-install record of extracted files
    ├── Services\
    │   ├── CascExtractorService.cs  CASC open / extract / undo logic
    │   └── ManifestService.cs       JSON settings + manifest persistence
    ├── Native\
    │   └── CascLib.cs               P/Invoke declarations for CascLib.dll
    └── Tools\
        └── CascLib.dll              (place your copy here before building)
```

**Settings** are stored in: `%AppData%\D2RExtractor\settings.json`
**Manifests** are stored in: `<D2RPath>\data\.extraction_manifest.json`
