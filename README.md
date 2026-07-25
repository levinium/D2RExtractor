# D2R File Extractor

A WPF desktop app with easy 1-click extraction of Diablo 2: Resurrected CASC game archives for faster load times. Also lets you undo the extraction before game updates and easily re-apply the extraction after updating.

[⬇️ Download D2RExtractor v1.1.5 (Standalone ZIP)](https://github.com/levinium/D2RExtractor/releases/download/D2RExtractor_v1.1.5/D2RExtractor-Compiled-Standalone_v1.1.5.zip)

[YouTube Installation & Usage Guide](https://youtu.be/SYKQdQK1_gQ)

---

## How it works

D2R normally loads assets from compressed game archives at runtime. By pre-extracting the `data\global\`, `data\hd\`, and `data\local\` folders to plain files, the game loads them directly — dramatically reducing load times.

This app automates that process for one or more D2R installations and keeps a manifest of every extracted file so the extraction can be fully reversed.

The app automatically detects which storage format each installation uses:

- **Battle.net** installs use the classic CASC layout and are read with Ladislav Zezula's native CascLib library — the same engine that powers Ladik's CASC Viewer.
- **Steam** installs (mid-2026, game build 93236+) use a newer self-contained storage format that CascLib does not support. These are read with a built-in native reader that decodes everything directly from the local game files.

---

## Prerequisites

### Disk space

Extraction writes ~40–45 GB of data per D2R installation. More if also extracting international files. Ensure sufficient free space.

### Steam installations

Steam D2R is fully supported. As of the mid-2026 update (build 93236+), Steam switched to a self-contained "static build configuration" storage format (a `data\.build.config` plus flat `NN-NNNNNNNN.data` archives) that replaced the classic CASC layout. The app reads this format natively and **no internet connection is required** — all data is decoded from the local game files. (This replaces the earlier CDN-download approach that older Steam builds needed.)

The bundled CascLib.dll is still used for Battle.net installs.

---

## Build

```
dotnet build D2RExtractor.sln -c Release -p:Platform=x64
```

Output: `D2RExtractor\bin\x64\Release\net8.0-windows\D2RExtractor.exe`

---

## Usage

1. Launch `D2RExtractor.exe`
2. Click **+ Add Installation** and select your D2R base folder (the one containing the `Data` / `data` subfolder) - Repeat for any additional D2R folders.
3. Click the **Gear** icon at the top right corner for settings. This allows for extraction of international files (multi-language dubbing) if needed.
4. Click **Extract** — extraction runs in the background (Battle.net: ~30–45 min; Steam: no download required, disk-speed bound)
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
    │   ├── CascExtractorService.cs  Format detection + extract / undo logic
    │   ├── IExtractionBackend.cs     Backend abstraction (CascLib vs Steam native)
    │   ├── ManifestService.cs       JSON settings + manifest persistence
    │   └── Steam\                   Native reader for the Steam static-container format
    │       ├── SteamBuildConfig.cs  Parses data\.build.config
    │       ├── StaticContainer.cs   EKey → data-file location + blob reads
    │       ├── Blte.cs              BLTE block decoder
    │       ├── Tvfs.cs              TVFS file-tree parser
    │       └── SteamStaticStorage.cs Enumerate + extract entry point
    ├── Native\
    │   └── CascLib.cs               P/Invoke declarations for CascLib.dll (Battle.net)
    └── Tools\
        └── CascLib.dll              (place your copy here before building)
```

**Settings** are stored in: `%AppData%\D2RExtractor\settings.json`
**Manifests** are stored in: `<D2RPath>\data\.extraction_manifest.json`
