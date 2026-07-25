# Changelog

## 1.1.6

- **Fixed a Steam extraction bug that caused a game-launch error.** The Steam TVFS omits path separators for some entries, so ~3,481 files (e.g. `data\global\sfx\monster\baal\coldtrail.flac`) were written with a merged folder/file name (`…\monster\baalcoldtrail.flac`) — landing at the wrong path. The file contents were correct, but the game couldn't find them and errored on launch.
- The extractor now recovers the canonical paths from the storage's `index` text ROOT (verified against the build config's `root` key) and joins them onto the TVFS encoding keys. The resulting Steam path set is now byte-for-byte identical to the Battle.net layout.
- Affects Steam only; Battle.net was never impacted.

## 1.1.5

- **Restored Steam D2R support after the mid-2026 storage change (build 93236+).** Steam's latest update replaced the classic CASC layout (`.build.info` + `Data\indices` + `*.idx`) with a self-contained "Static Build Configuration" storage: a `data\.build.config` plus flat `NN-NNNNNNNN.data` archives whose physical location is encoded directly in each file's encoding key. CascLib does not support this format, so extraction stopped working.
- Added a **native, fully-local reader** for the new Steam format — no CascLib.dll and **no internet connection required** (unlike the previous 3.1.2-era CDN-download workaround). It parses the build config, resolves file locations from the key-layout bit fields, walks the TVFS file tree, and decodes BLTE/zlib blobs entirely from the local `.data` files.
- The extractor now **auto-detects the storage format** per install: the native reader for Steam static-container installs (`data\.build.config` present), and CascLib for classic CASC installs (Battle.net). Both produce identical virtual paths, so extraction output is unchanged.
- Battle.net extraction is unaffected and continues to use CascLib.dll.
- Extraction backends are now abstracted behind a common interface, so both formats share one extraction/manifest/progress loop.
- Installation validation now accepts a Steam static-container folder (`data\.build.config`) in addition to the classic `Data\indices` layout.

## 1.1.4

- **Fixed international file extraction.** Locale files were being extracted to a `locales\` directory that D2R ignores in `-direct` mode. Files are now correctly mapped into the `data\` tree (e.g. `data:locales\audio\itit\data\local\sfx\...` → `data\local\sfx\...`) so the game loads them.
- Added language selector — choose which language to extract in Settings. Only the selected language's audio/text is extracted, replacing the base English files. Supports deDE, enUS, esES, esMX, frFR, itIT, jaJP, koKR, plPL, ptBR, ruRU, zhCN, zhTW.
- Changing the selected language triggers a re-extraction of just the international files (no need to undo/redo the full base extraction).
- Added CascDiagnostic console tool to the solution for CASC storage analysis and debugging.

## 1.1.3

- **Steam D2R support (patch 3.1.2+):** Full extraction now works for Steam installations. Game data is downloaded from Blizzard's CDN during extraction, so an internet connection is required for Steam users.
- Patched and rebuilt CascLib.dll with three fixes for the Steam D2R CASC layout:
  - Fixed `CASC_FEATURE_ONLINE` flag being silently stripped during storage opening, preventing CDN downloads.
  - Added archive index (`.index`) file loading for local storages opened with CDN support, providing correct EKey-to-size mappings.
  - Added `EncodedSize` resolution from archive indices for CDN-hosted files, enabling CascLib to read file data via CDN download.
- Added `CascOpenStorageEx` fallback with `CASC_FEATURE_ONLINE | CASC_FEATURE_ALLOW_DOWNLOAD` flags — enables both metadata and file data downloads from Blizzard's CDN.
- Added diagnostic logging of CASC metadata file presence (`.build.info`, `.build.db`, `.product.db`) at each candidate path for easier troubleshooting.
- Expanded CASC storage fallback to probe alternate subdirectory paths (e.g. `Data\`) when the game root fails.
- Throttled extraction progress reporting to prevent UI freezes when many files are processed rapidly.

## 1.1.2

- Added `CascOpenStorageEx` fallback for D2R installations where the standard `CascOpenStorage` fails (e.g. Steam after patch 3.1.2). The app now automatically retries with CDN-enabled and full online-storage modes before reporting an error. Battle.net installations are unaffected.
- Added clear error messaging with a link to the upstream CascLib tracking issue when all CASC open attempts fail.
- Graceful handling when `CascOpenStorageEx` is not available in older CascLib.dll versions, with guidance to update.
- Temporarily disabled international file extraction (multi-language audio) due to the feature not working correctly. The option is grayed out in settings until a fix is available.

## 1.1.1

- Replaced CASC enumeration dry-spell heuristic with `CascGetStorageInfo` file count query. The previous approach used a "dry spell" threshold to work around a CascLib DLL bug where `CascFindNextFile` never returns false. This could silently miss international files (`data:locales\`) if they were stored far from the base data entries in the CASC index. The new approach queries the total file count up front (padded by 10%) to set a reliable iteration cap, with a 30M fallback if the query fails.
- Verified the `data:locales\` CASC virtual-path prefix against a real D2R installation. Confirmed international audio files use the path format `data:locales\audio\<langcode>\data\...`.
- Optimized file extraction with reusable read buffer and pre-sized file output to reduce allocations and filesystem overhead.

## 1.1.0

- Added settings window with option to extract international audio files (multi-language dubbing).
- Added change log window accessible from the gear menu.
- Added international file extraction support (locales folder).

## 1.0.0

- Initial release with CASC extraction and undo support for D2R installations.
