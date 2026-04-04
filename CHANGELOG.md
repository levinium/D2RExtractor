# Changelog

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
