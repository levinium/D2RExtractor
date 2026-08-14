# D2R File Extractor

A WPF desktop app with easy 1-click extraction of Diablo 2: Resurrected CASC game archives for faster load times. After a game patch, one click refreshes just the files that changed instead of re-extracting everything.

[⬇️ Download D2RExtractor v1.1.7 (Standalone ZIP)](https://github.com/levinium/D2RExtractor/releases/download/D2RExtractor_v1.1.7/D2RExtractor-Compiled-Standalone_v1.1.7.zip)

[YouTube Installation & Usage Guide](https://youtu.be/SYKQdQK1_gQ)

---

## How it works

D2R normally loads assets from compressed game archives at runtime. By pre-extracting the `data\global\`, `data\hd\`, and `data\local\` folders to plain files, the game loads them directly — dramatically reducing load times.

This app automates that process for one or more D2R installations and keeps a manifest of every extracted file, recording each file's content key so the extraction can be both fully reversed and incrementally refreshed after a patch.

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
5. Make sure that your D2R folder is on Windows Defender's exclusions list (Windows Defender can slow down running D2R using the extracted files)
6. Launch D2R with the "-direct -txt" command line options and enjoy faster load times.

### After D2R updates

1. Update D2R normally via your game launcher
2. Click **Update** (the Extract button becomes Update once an installation is extracted)

The app compares the game archives against your extracted files and writes only the ones that are new, changed, missing or damaged, then removes any the patch deleted. A typical patch writes a few hundred MB rather than re-extracting the full ~45 GB — much faster, and much easier on an SSD.

You no longer need to undo before patching. **Undo Extraction** is still there for when you want the extracted files gone entirely.

If an extraction is interrupted, the button reads **Resume** and writes only what is missing instead of starting over.

### Change detection

Each file's content key is read from the game storage during the scan the app already performs, so tracking costs nothing extra. An update rewrites a file when its key differs from the recorded one, or when the file on disk is missing or the wrong size.

Settings has an optional **"Verify extracted file contents during Update"** that checksums every extracted file instead of comparing sizes. It catches files corrupted or edited outside the app, reads the whole extraction (several extra minutes), and still writes only the files that differ.

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
    │   ├── CascExtractorService.cs  Format detection + extract / update / undo logic
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
**Manifests** are stored in: `<D2RPath>\data\.extraction_manifest.json` (header) and `<D2RPath>\data\.extraction_files.txt` (one record per extracted file: path, content key, size)

> Manifests written by 1.1.7 are not readable by 1.1.6 and earlier — those versions would see an empty file list and their Undo would remove nothing. Existing 1.1.6 manifests are upgraded automatically on the first Update.
