# D2R File Extractor

A WPF desktop app with easy 1-click extraction of Diablo 2: Resurrected CASC game archives for faster load times. Also lets you undo the extraction before game updates and easily re-apply the extraction after updating.

---

## How it works

D2R normally loads assets from compressed CASC archives at runtime. By pre-extracting the `data\global\`, `data\hd\`, and `data\local\` folders to plain files, the game loads them directly — dramatically reducing load times.

This app automates that process for one or more D2R installations and keeps a manifest of every extracted file so the extraction can be fully reversed.

This app uses Ladislav Zezula's native CascLib library — the same engine that powers Ladik's CASC Viewer.

---

## Prerequisites

### Disk space

Extraction writes ~40–45 GB of data per D2R installation. Ensure sufficient free space.

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
3. Click **Extract** — extraction runs in the background (can take as long as ~30–45 min depending on your hardware)
4. Launch D2R with the "-direct -txt" command line options and enjoy faster load times.

### Before updating D2R

1. Click **Undo Extraction** — removes all extracted files using the saved manifest
2. Update D2R normally via Battle.net
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

---

## CASC_FIND_DATA struct note

The P/Invoke struct layout in `Native/CascLib.cs` targets modern CascLib builds (2.x+) with `CASC_MAX_PATH = 1024`. If you see garbled file names, the CascLib version you're using may use a different path length. Adjust the `CASC_MAX_PATH` constant and field offsets in `CascLib.cs` accordingly.
