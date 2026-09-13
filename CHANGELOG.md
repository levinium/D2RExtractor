# Changelog

## 1.1.8

- **Each installation can now extract to somewhere other than the game folder, or to several places at once.** The folder icon on each row opens **Destinations**. By default there is one, the game folder, and everything behaves exactly as it always has — the feature costs nothing to ignore. Add another and every Extract and Update writes to all of them, one after the other, which is what makes a mods folder or a separate copy for manual patching practical.
- Each destination keeps its **own** manifest, so they can be extracted, updated and undone independently, and a destination that shares a folder with your own files loses exactly what this app put there and nothing else. An installation extracted by 1.1.7 needs no migration: its manifest is already where the default destination looks for it.
- The game folder can be turned off or removed like any other destination, but the app says plainly when nothing is writing there — D2R only loads extracted files from its own folder, so `-direct -txt` would find nothing.
- Destinations cannot be nested inside one another. Each one's update pass treats unrecognised files under its tree as leftovers from an old patch and deletes them, so overlapping destinations would quietly eat each other.
- Disk space is now checked per drive and multiplied by the number of destinations sharing it, rather than only checking the game folder.
- **Added a record of what the last run changed, per destination.** The Destinations window's **Changes** button lists every file that run added, replaced or removed, with sizes, filters and a plain-text export. That is the answer to "what did that patch actually touch?", which was previously unanswerable after the fact.
- A fresh extraction records a summary rather than a per-file list, since every one of its ~150,000 files is an addition and the list would be a second copy of the manifest for no gain. Update runs, where the answer is a handful of files, keep the full list.
- Fixed: the Update and Undo confirmations named the installation folder, which stopped being where the files are the moment destinations became configurable. They now name the folders that will actually be written to or emptied.
- Fixed: a run that had just written 41.7 GB into an empty destination reported "Up to date", because the status was read from counters only the update path fills in.
- Fixed: every grid in the app could be dragged narrower than its own columns, which clips the rightmost one rather than shrinking any of them — the destinations window was losing the right edge of **Remove**. One column per grid now absorbs the difference, and the action buttons size to their text instead of to a fixed width that clips longer labels or larger system text.
- **Added a test suite** — 122 tests, run by CI on every push and by `publish.ps1` before any release. They cover the things that fail quietly: which files a destination considers its own and will therefore delete, whether two destinations overlap, how the manifest sidecars survive a crash mid-write, and whether a published version is newer than the one running.

- **The app now checks for new releases and can install them itself.** When a newer version is published, a gold **Update to …** button appears at the top right. Pressing it shows the release notes and offers to install: the app downloads the release zip, checks it against the SHA-256 GitHub recorded for it, unpacks it, and hands the swap to a short script that waits for the app to close, replaces the files and starts it again.
- **A failed update costs nothing but the download.** The existing installation is copied aside before anything is replaced and restored if the swap fails, so a half-overwritten folder is not a way to lose the app. A download that does not match its published checksum is discarded and nothing is touched. If the app is somewhere it cannot write — unzipped into Program Files, say — it says so instead of finding out after closing itself.
- **The check never interrupts.** It runs in the background at most once a day, lights up the button and waits. Nothing is downloaded or installed without being asked for. **Check for Updates** in the gear menu asks on demand, and **Check for updates automatically** in Settings turns the daily look off entirely.
- The check sends no identifier and nothing about the machine or its game installs — it is a plain request for a public file.
- **Updating is refused while an extraction is running.** Swapping the executable out from under a run forty minutes into writing 45 GB would leave a half-extracted install and a manifest describing something else.
- Release tags here carry the product name (`D2RExtractor_v1.1.7`), which the version comparison accounts for: it reads the version from after the last underscore rather than scanning for the first digit, since the first digit in that tag is the **2** in "D2R" and would otherwise be read as version 2.0.0 forever.
- Added a **Support** button for anyone who would like to put something toward the app's upkeep. It is compiled in only for official release builds, so a build from a clean checkout has no ask in it at all.
- Added `publish.ps1`, which builds the self-contained single-file release zip the releases page serves.

## 1.1.7

- **Added incremental updates.** After a D2R patch you no longer need to undo and re-extract 45 GB. The **Extract** button becomes **Update** once an installation is extracted: it compares the game archives against the extracted files and writes only the ones that are new, changed, missing or damaged. Files the patch removed are deleted, so the extracted tree keeps matching the archives. A typical patch now writes a few hundred MB instead of tens of gigabytes.
- Change detection uses each file's content key, read straight from the storage during the scan it already performs, so it costs nothing extra. On Battle.net that key is the MD5 of the file's decoded contents; on Steam it comes from the storage's file index when that verifies, and falls back to encoding keys otherwise.
- **Interrupted extractions now resume instead of restarting.** The button reads **Resume** and writes only the files that are missing, rather than deleting everything already written and starting over.
- Enabling, disabling or switching the international language is now an ordinary update — only the affected files are rewritten. Turning international files back off correctly restores the base English files, which it previously did not.
- Added **Update All** next to Extract All, and a **"Verify extracted file contents during Update"** setting. Verification checksums every extracted file rather than comparing sizes, catching files corrupted or edited outside the app. It reads the whole extraction and takes several extra minutes, and still writes only the files that differ.
- **The manifest no longer writes gigabytes of its own.** It was rewritten in full every 500 files, so its cost grew with the square of the file count — about 1.5 GB of writes over a typical 150,000-file extraction, on top of the extraction itself. The file list now lives in an append-only sidecar (`data\.extraction_files.txt`) next to a small (~300 byte) JSON header, which brings that down to a single ~11 MB write even with the new per-file content keys: a 134× reduction.
- **Fixed: a manifest damaged by a crash or power loss could strand an entire extraction.** The manifest was truncated and rewritten in place, so an interrupted write left invalid JSON. The app then read the installation as never extracted, which disabled Undo and left ~45 GB of files with nothing referencing them. Manifest writes are now atomic, and the incomplete-extraction marker is written before the first file instead of after the first 500.
- Faster archive scanning on Battle.net: the enumeration no longer allocates a string for every one of the millions of entries it walks past, only for the ones that match.
- Corrected a stale note claiming Steam extraction needs an internet connection. It has not since 1.1.5.

> Note: manifests written by 1.1.7 are not readable by 1.1.6 and earlier — those versions would see an empty file list and their Undo would remove nothing. Existing 1.1.6 manifests are upgraded automatically on the first Update.

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
