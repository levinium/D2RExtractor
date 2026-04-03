# Changelog

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
