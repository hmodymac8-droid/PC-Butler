# Plan: Everything-style instant file search in PC Butler

Status: plan only, no code written. Prepared 2026-10-16 against PC Butler 1.21.4 (`main`, commit 9733472).

## 1. The short version

- Everything is fast for one reason: on NTFS it reads the file table (the MFT) directly instead of walking folders, keeps the whole index in memory as compact arrays, and stays current by reading the NTFS change journal (USN journal). Everything else (syntax, filters, bookmarks, servers) sits on top of that index.
- PC Butler can do the same in C#. The hard part is not the code but **administrator rights**: reading the MFT and the journal needs them, and PC Butler's rule is that it never runs as administrator. The plan keeps that rule: a small, optional, read-only **Search service** (installed once through the existing helper, one permission prompt) reads the volumes, and the normal program builds the index and answers searches. Without the service, search still works using a slower folder scan plus folder watchers.
- The work splits into seven phases. Phase 1 (search without admin, basic syntax) already ships something useful; phase 2 brings the instant speed; phases 3 to 6 add the rest of Everything's features; the last phase is the network and phone side.
- The index also makes existing pages faster: Storage, Duplicates, Cleaner and Sorter can read sizes and names from it instead of walking the disk.

**Decisions for Mohammad** (each has a recommended default, marked ★):

1. How the fast index gets admin rights: **decided 2026-10-16: the optional Windows service** (section 4, option A).
2. Whether "all features" includes Everything's FTP/ETP server: ★ no, replace it with search over Courier's paired link and an optional web page / yes, build ETP for compatibility with Everything clients.
3. Where search lives in the window: **decided 2026-10-16: a Search page plus a pop-up search box, opened anywhere with Win + F by default, changeable on the Hotkeys tab** (see 3.3).

## 2. Everything's feature inventory, and what PC Butler does with each

Everything's current line is 1.5 (the developer's own forum calls the installer "1.5b" as of May 2026); 1.4.1 is the long-standing stable. The list below covers both. "Phase" refers to section 5.

### 2.1 Indexing

| Everything feature | PC Butler | Phase |
|---|---|---|
| Read NTFS volumes from the MFT (seconds for a million files) | Search service reads the MFT; program builds the index | 2 |
| Live updates from the USN change journal | Service reads the journal and streams changes; program applies them | 2 |
| Catch up at start from the saved journal position, full rescan if the journal wrapped | Same; saved per volume in the index file | 2 |
| ReFS volumes (1.5) | Same code path, 128-bit file IDs (USN_RECORD_V3) | 2 |
| FAT/exFAT, network shares, folders without a journal ("folder indexing") | Folder scan with `FindFirstFileEx` (large fetch) plus `FileSystemWatcher`, with a rescan interval per folder | 1 |
| Index sizes, dates (modified/created/accessed), attributes; folder sizes | Same, each optional because they cost memory (see 6.2) | 1, 2 |
| Include only / exclude folders, exclude hidden or system files, exclude by pattern | Settings tab "Search"; defaults exclude `Windows\WinSxS` and other users' profiles | 1 |
| Removable drives indexed when plugged in | `WM_DEVICECHANGE` in the program, drive added or dropped | 2 |
| Database saved to disk, fast start | Binary index file in `%LOCALAPPDATA%\PCButler\search\` (large, so not in roaming `%APPDATA%`) | 1 |
| Everything service (lets a normal user read the MFT) | PC Butler Search service, see section 4 | 2 |
| Index journal / "recent changes" window (1.5) | "Recent changes" view fed by the journal, plus `rc:` | 3 |
| File lists (.efu), including indexing a list as if it were a drive | Import and export EFU and CSV; `filelist:` | 4 |
| Property indexing (1.5: any Windows property, media tags) | Read on demand through the Windows property system, cached; chosen properties can be indexed | 5 |
| Content indexing (1.5) | Optional content index for text-like files in folders the user picks | 5 |

### 2.2 Searching

| Everything feature | PC Butler | Phase |
|---|---|---|
| Search as you type, results while typing | Debounced, previous search cancelled | 1 |
| Space = AND, `\|` = OR, `!` = NOT, `< >` grouping, `"..."` exact text | Same syntax | 1 |
| Wildcards `*` `?` (whole name when present) | Same | 1 |
| Path matching (`\` in the term, `path:`), `nopath:` | Same | 1 |
| Modifiers: `case:`, `diacritics:`, `wholeword:`/`ww:`, `wfn:` (whole file name), `regex:`, `file:`, `folder:`, `startwith:`, `endwith:`, `ascii:`, `nocase:` etc. | Same, plus the matching Search menu toggles (match case, whole word, path, diacritics, regex, prefix, suffix, ignore punctuation, ignore white-space) | 1 (basic), 3 (rest) |
| Functions with comparisons and ranges: `size:>1gb`, `dm:2025-07-01..2026-06-30`, `ext:cpp;h` | Same grammar: `:`, `<`, `>`, `<=`, `>=`, `=`, `..` | 3 |
| Function list: `size:`, `dm:`/`dc:`/`da:`/`dr:` (date run), `rc:` (recently changed), `attrib:`, `ext:`, `type:`, `len:`, `depth:`, `parent:`, `parents:`, `infolder:`, `root:`, `child:`, `childcount:`, `childfile:`, `childfolder:`, `empty:`, `dupe:`, `sizedupe:`, `namepartdupe:`, `runcount:`, `content:`, `filelist:` | All of these | 3 (`content:` 5, `filelist:` 4) |
| Media functions: `width:`, `height:`, `dimensions:`, `orientation:`, `bitdepth:`, `length:` (duration), `artist:`, `album:`, `title:`, `genre:`, `track:`, `year:`, `comment:` | From the Windows property system | 5 |
| Constants: sizes (`empty`, `tiny`, `small`, `medium`, `large`, `huge`, `gigantic`, `kb`/`mb`/`gb`), dates (`today`, `yesterday`, `thisweek`, `lastmonth`, `thisyear`, `7days`, ISO dates, bare years), durations (`min`, `hour`) | Same | 3 |
| Macros: `audio:`, `zip:`, `doc:`, `exe:`, `pic:`/`image:`, `video:`; custom macros from bookmarks or `define:` | Same; the extension lists come from Sorter's categories in `config.json`, so a user's own category becomes a macro | 3 |
| Diacritics matching | Important for Arabic: off by default means `محمد` also finds `مُحَمَّد`. Add an Arabic letter-variant option (أ إ آ ا, ى ي, ة ه) | 1 |

### 2.3 Results window

| Everything feature | PC Butler | Phase |
|---|---|---|
| Columns: name, path, size, extension, type, date modified/created/accessed, attributes, date recently changed, run count, date run; more in 1.5 | Same set; columns chosen and saved | 1 (basic), 3 |
| Instant sort on any column ("fast sort") | Pre-sorted orders kept for name, path, size, date modified | 2 |
| Highlighting of matched text | Same | 3 |
| Open, open folder, open path, copy name or full path, rename in place, delete (to Recycle Bin, logged in History) | Same; delete goes through `RecycleBin` and History as everywhere else in PC Butler | 1 |
| Windows right-click menu on results, drag results out, drop onto other apps | Shell context menu (`IContextMenu` through COM interop), `DataObject` file drop | 3 |
| Status bar: object count, selection size | Same | 1 |
| Filters (Everything, Audio, Compressed, Document, Executable, Folder, Picture, Video, custom, with hotkeys); 1.5 filter sidebar | Filter chips above the list; custom filters saved in `config.json` | 3 |
| Bookmarks (search + filter + sort + columns), organizer, hotkeys | Same | 3 |
| Search history and run history, sort by run count | Same, off-switch for privacy, cleared by "Clear history" | 3 |
| Preview pane and thumbnail views (1.5) | Preview handlers (`IPreviewHandler`) and shell thumbnails, virtualized | 5 |
| Export results (CSV, EFU, TXT) | Same | 4 |
| New window hotkey, show/hide hotkey, start in tray, multiple windows | Pop-up search box on a hotkey (built like `ClipboardPicker`), through the existing `Hotkeys` in `Overlay.cs` | 3 |
| Dark mode, translations (Everything ships Arabic) | Already PC Butler's look; every string through `Tr.T` and `ar.json` | 1 |
| Duplicate finding (1.5 "Find duplicates") | `dupe:` functions plus a "Find duplicates of the results" button that hands the list to the Duplicates page | 4 |

### 2.4 Outside the window

| Everything feature | PC Butler | Phase |
|---|---|---|
| Command line: `-search`, `-path`, `-parent`, `-filter`, `-bookmark`, `-sort`, `-create-filelist`, etc. | `PCButler.exe --search "<query>"` and friends (handled like `--process <path>` in `App.xaml.cs`) | 3 |
| `es.exe` console search | Small console program `pcbsearch.exe` that asks the running program over the named pipe | 6 |
| IPC / SDK (WM_COPYDATA, Everything SDK DLL, Everything3 named-pipe SDK in 1.5) | A documented JSON-over-named-pipe protocol, per-user | 6 |
| HTTP server (web page with search and download) | Optional, off by default, on Kestrel (already used by Courier), password, local network only | 6 |
| ETP/FTP server and connecting to a remote Everything | Recommended replacement: search a paired PC through Courier (same trust and pinned TLS); ETP only if Mohammad wants compatibility | 6 |
| Explorer integration | "Search in this folder with PC Butler" on the existing right-click entry (`ExplorerMenu.cs`) | 4 |
| Portable mode, multiple instances (`-instance`) | Not planned: PC Butler is installed per user and single-instance by design | — |

## 3. How it fits the existing code

### 3.1 New code

All engine code goes in `src/PCButler.Core/Search/` (a new folder; the Core project has none yet, but a feature this large should not be one more 1,000-line file next to `Portal.cs`).

| File | What it holds |
|---|---|
| `SearchIndex.cs` | The in-memory index: struct-of-arrays, one row per file or folder (6.2). Add, remove, rename, move by file reference number. Folder full paths built lazily from the parent chain and cached. |
| `IndexStore.cs` | Saving and loading the index file (versioned binary, per-volume journal ID and next USN). |
| `VolumeSource.cs` | One volume or folder feeding the index. `FolderScanSource` (no admin: `FindFirstFileEx` + `FileSystemWatcher`) and `JournalSource` (records from the service). |
| `MftReader.cs`, `UsnJournal.cs` | The NTFS side: `FSCTL_ENUM_USN_DATA` for names and parents, raw `$MFT` record parsing for sizes and dates, `FSCTL_QUERY_USN_JOURNAL` / `FSCTL_READ_USN_JOURNAL` for changes. Used only inside the service. Parsers take bytes, so tests can feed them captured records without admin. |
| `QueryParser.cs` | Text to a syntax tree: terms, operators, modifiers, functions, macros, constants. |
| `QueryCompiler.cs` | Syntax tree to a matcher over index rows; cheap tests first (extension, attributes, size), name matching next, path and property tests last. |
| `Searcher.cs` | Runs a compiled query in parallel over the index, cancellable, returns an `int[]` of row numbers in the chosen order. |
| `SearchSettings.cs` | Section of `config.json`: drives and folders, exclusions, what to index, filters, bookmarks, history switches. Same pattern as `ScheduleSettings` in `Config.cs`. |
| `SearchPipe.cs` | Named-pipe server and client: the program talks to the service, and later `pcbsearch.exe` and the web page talk to the program. |
| `SearchService.cs` | The service's whole life: open volumes, enumerate, follow journals, stream records to the one user it serves. |

Window side (`src/PCButler/`):

| File | What it holds |
|---|---|
| `Pages/SearchPage.cs` | The page: search box, filter chips, results list, status line, preview pane later. Registered in the page list in `MainWindow.cs` (around line 93). |
| `SearchPopup.cs` | The hotkey pop-up, modelled on `ClipboardPicker.cs`. |
| `SearchResults.cs` | A virtualized list over the `int[]` result: rows are drawn from the index on demand, never one object per file. `SortableList` in `Ui.cs` is fine for hundreds of rows but not for millions, so this is a separate control with the same look. |
| `ShellMenu.cs` | The Windows right-click menu for selected results. |
| `SettingsPage.cs` | New "Search" tab: drives, folders, exclusions, the service switch. |

The Android app shares Core files by linking them one by one (`PCButler.Android.csproj`), so `QueryParser.cs` and `QueryCompiler.cs` must not depend on Windows APIs; then the phone can reuse them (phase 6).

### 3.2 Reused code

- **Helper and its rules** (`AdminHelper.cs`, `Sensors.RunHelper`): a new action `install_search_service` / `remove_search_service`, added to the list `AdminHelper.Poll` accepts, nothing else.
- **Kestrel and Courier** (`PortalServer.cs`, `Portal.cs`): the web page and searching a paired PC reuse the HTTPS server, pairing and pinned certificates.
- **Hotkeys** (`Overlay.cs`): new actions `search` (pop-up, default Win + F, see 3.3) and `searchpage` (no default).
- **Recycle Bin and History**: deleting from results goes through them, so it can be undone.
- **Sorter's categories**: become the `audio:` / `doc:` / ... macros.
- **Storage, Duplicates, Cleaner, Sorter**: when the index is ready, they ask it for sizes and file lists instead of walking folders (phase 4). They keep their own scan as a fallback.
- **Translations**: every new string through `Tr.T`, and `ar.json` grows with them.

### 3.3 The Win + F shortcut

Mohammad chose Win + F as the default shortcut for the pop-up (2026-10-16), changeable on the Hotkeys tab in Settings like the other shortcuts.

Windows itself holds Win + F (it opens Feedback Hub), so `RegisterHotKey`, which the existing `Hotkeys` class uses, will be refused for it. The plan:

- **A low-level keyboard hook** (`SetWindowsHookEx(WH_KEYBOARD_LL)`), installed only while a Win-key shortcut is set and PC Butler is running. It catches Win + F, swallows it so Feedback Hub does not open, and opens the pop-up. It needs no administrator rights and works at once, without restarting File Explorer. When PC Butler is closed, Win + F does what Windows normally does.
- The hook passes every other key straight on and does nothing else, since a slow hook makes typing lag everywhere: it only compares the key and posts a message to the window thread.
- **Limit:** while a program running as administrator is in front, Windows does not pass its keys to the hook, so Win + F opens Feedback Hub there instead. The Hotkeys tab says so in one line.
- **Fallback:** if the hook cannot be installed, the tab offers to take Win + F from File Explorer through the per-user `DisabledHotkeys` value (`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`, letter `F` added to any letters already there). That takes effect after File Explorer restarts, and is undone when the shortcut is changed or PC Butler is uninstalled.
- Shortcuts without the Win key keep using `RegisterHotKey` as today. If the user picks a combination another program already holds, the tab shows the same "could not be used" message it shows now.

## 4. Admin rights: the Search service

Reading the MFT and the change journal means opening the volume itself (`\\.\C:`), which Windows allows only to administrators. Everything solves this with a Windows service. The options for PC Butler:

| Option | Prompts | Problem |
|---|---|---|
| ★ **A. Optional Windows service** installed once through the helper | One, at install (and at each update of the service) | Must be built so it cannot become a way to get administrator rights (below). |
| B. The helper, started at every login with the "runas" prompt | One per login | Annoying; a scheduled task "run with highest privileges" avoids the prompt but has the same weakness as A without A's fixes. |
| C. No fast index | None | First indexing of a big drive takes minutes, not seconds; changes on folders without watchers are caught only by rescans. |

How option A keeps PC Butler's rules:

- **PC Butler itself still never runs as administrator.** The service is a separate, small program (`PCButlerSearch.exe`) that only reads volumes. It never writes files, never starts programs, never deletes anything. It sends records (file reference number, parent, name, size, dates, attributes) and nothing else.
- **The service's program lives where only administrators can write.** PC Butler is installed per user under the user's own profile, which any program running as that user can change. A service running as SYSTEM from there would let any such program replace it and gain full control. So the helper copies `PCButlerSearch.exe` into `%ProgramFiles%\PC Butler Search\`, checks its hash against the one built into PC Butler, and installs the service from there. Updating the service is a new prompt; the program keeps working with an older service as long as the pipe protocol version matches.
- **Only the user who installed it can talk to it.** The named pipe's access list allows that user's SID and nothing else, and the service checks the client's process path.
- **Privacy.** A SYSTEM service sees every file name on the volume, including other users' files. Default: the service leaves out other users' profile folders and anything the installing user cannot open (checked once per folder, cached). This is a real difference from Everything, which shows everything.
- **Off by default.** The Search page works without it (option C behaviour) and offers "Make search instant" with one line on what it installs.
- Phase 0 checked whether a lower-privilege account holding only `SeManageVolumePrivilege` could read the volume: it cannot (access denied on `GENERIC_READ`; the journal calls need read access). So the service runs as LocalSystem, and the Program Files copy, pipe ACL and read-only design above are required, not optional.

## 5. Phases

Each phase ends in its own release with both changelogs, engine tests in `tests/PCButler.Tests`, and a hand test on the owner's PC (tests cannot run in the cloud container; build and tests need Windows and .NET 10).

**Phase 0: measure first (no release). Done 2026-10-16, see 5a.** A throwaway console prototype on the owner's PC: enumerate C: and G: with `FSCTL_ENUM_USN_DATA` and with a raw `$MFT` read, time both, count entries, and measure memory per entry with the planned layout. Check the lower-privilege service account idea. Output: the numbers that decide 6.2's defaults.

**Phase 1: search without administrator rights.**
Index built by folder scanning and kept current by watchers; saved to disk; Search page; basic syntax (words, AND/OR/NOT, quotes, wildcards, path, `file:`, `folder:`, `ext:`, `case:`, diacritics with the Arabic options); name, path, size, date modified columns; open, open folder, copy path, rename, delete to the Recycle Bin; Search settings tab. Tests: parser, matcher, index add/remove/rename, saving and loading, folder scan on a temp folder.

**Phase 2: instant on NTFS.**
The Search service, its install through the helper, MFT enumeration, journal following, catch-up at start, full rescan when the journal wrapped or was recreated, removable drives, ReFS, hard links (one file, several names), pre-sorted orders. Tests: MFT and journal record parsing from captured bytes; index updates from synthetic journal records (create, rename, move, delete, a folder moved with its contents).

**Phase 3: the full search language and the comfortable window.**
All functions in 2.2 except content and media, constants, macros, filters, bookmarks, search and run history, highlighting, the Windows right-click menu, drag out, more columns, recent changes view, the pop-up and hotkeys, `--search` command-line arguments.

**Phase 4: the index helps the rest of PC Butler.**
Storage's treemap from the index (instant), Duplicates and `dupe:` sharing one engine, Cleaner and Sorter reading from it, "Search in this folder" in File Explorer, EFU/CSV export and import, `filelist:`.

**Phase 5: inside files.**
`content:` (read on demand, with a size limit, skipping cloud-only OneDrive files so nothing is downloaded by a search), media functions and property columns, preview pane, thumbnails, optional content index for chosen folders.

**Phase 6: from outside.**
Named-pipe protocol and `pcbsearch.exe`; optional web page; searching a paired PC through Courier and pulling the file with Courier; the Android app searching its own picked folders with the shared parser, and searching the PC through Courier.

## 5a. Phase 0 results (measured 2026-10-16 on the owner's PC)

PC: i5-9400F (6 cores), 32 GB RAM, Windows 11 Pro build 26300. All volumes NTFS, 4 KB clusters, 1 KB MFT records. Single runs; the folder walk ran first, so some MFT pages may have been cached for the admin runs. Prototype source and raw JSON: `G:\pcb-search-spike` on that PC (not in the repo).

| Drive | Entries | Folder walk, no admin | USN enumeration, admin | Raw `$MFT` read, admin |
|---|---|---|---|---|
| C: NVMe SSD | 1.20M | 48 s | 14.2 s | **10.9 s** |
| D: SATA SSD | 3.22M | 168 s | 42.5 s | **10.2 s** |
| E: 5400 rpm HDD | 640k | 357 s | 53 s | **10.0 s** |
| G: 5400 rpm HDD | 798k | 618 s | 310 s | **23.2 s** |

What this decides:

- **The service reads the raw `$MFT`, not `FSCTL_ENUM_USN_DATA`.** It is 1.3 to 13 times faster (sequential 16 MB reads at 330 to 374 MB/s on SSD, 67 to 78 MB/s on HDD) and gives sizes and dates at no extra cost. USN enumeration is kept only as a fallback if a `$MFT` cannot be parsed. Counts from both agreed within about 20 entries.
- **The journal is cheap to follow.** Every volume has a 32 MB journal (8 MB allocation delta), about 160k to 380k records, read in full in 0.2 to 1.7 s; the last 5,000 records in 0.2 to 2 ms. Catch-up at start is fast. A 32 MB journal can wrap after a few hundred thousand changes while PC Butler is closed, so the rescan path matters; the service may raise the journal size (setting, off by default).
- **No-admin mode is usable only as a fallback.** First index of all four drives takes about 20 minutes by walking (single thread); after that the saved index plus watchers keep it current. A parallel walk per drive should cut that on SSDs.
- **Memory matches section 6.2.** Measured 22 bytes per entry plus 2 bytes per name character: 63 to 106 bytes per entry names-only (average name 21 to 42 characters), plus 16 bytes with size and date modified. All four drives (5.86M entries): about 420 MB names-only, about 515 MB with size and date. Default: size and date modified on, created and accessed off, and the Search settings tab shows the memory per drive.
- **Plain per-name search is not instant at this scale.** Case-insensitive substring search per name in parallel: 22 to 55 ms typical on C: (1.2M), 60 to 100 ms on D: (3.2M), up to 210 ms. Phase 1 therefore keeps a case-folded copy of the name pool (or folds on load) and searches it as one block with vectorized search, then maps hits back to entries by offset. Target under 30 ms for 6M names. The folded copy costs about 2 bytes per character more, so this is weighed against memory in phase 1.

## 6. Risks

### 6.1 Administrator rights and security
Covered in section 4. The biggest single risk in the plan is a SYSTEM service run from a folder the user can write; the plan avoids that by copying it to Program Files. Antivirus programs may look hard at a program that opens raw volumes; the service should be small, do one thing, and be named plainly. The program is not code-signed today, which makes a SmartScreen or Defender warning on the service more likely.

### 6.2 Memory
Everything uses roughly 75 to 100 bytes per file. The plan aims for the same:

- No object per file. One row is a set of array entries: parent row (4 bytes), name offset and length into a shared character pool (6), name characters (UTF-16, average 20 to 30 characters, 40 to 60 bytes), attributes (4), and optionally size (8) and each date (8). File reference numbers (8) only for journal-fed volumes.
- That is about 70 bytes per file with names only, about 110 with size and date modified. One million files is about 70 to 110 MB; five million about 350 to 550 MB. Created and accessed dates are off by default, as in Everything.
- The pool grows in large chunks, so the garbage collector does not walk millions of strings. Folder paths are cached, file paths never are.
- The program already sits in the tray all day, so this memory is held all day. The Search settings tab should show the index's size and let the user drop drives.

### 6.3 Speed
- Matching: a parallel scan over the name pool with `MemoryExtensions.IndexOf(..., OrdinalIgnoreCase)` (vectorized in .NET) is what keeps searches under 50 ms for a few million names. Path tests run only on rows that already passed the cheaper tests.
- Sorting a million results per keystroke is too slow, so name, path, size and date modified keep pre-sorted orders that are updated on change (Everything's "fast sort").
- The window must never draw one WPF element per result; the results control is virtualized over row numbers.
- Journal bursts (unzipping 200,000 files) are applied in batches on a background thread, and the visible results refresh at most a few times a second.
- Startup: loading the saved index must be quick (tens of MB read in one go), then the journal catches up.

### 6.4 Correctness
- The journal can wrap (fixed size) or be deleted; the saved journal ID and position detect that and force a rescan.
- Hard links, 8.3 short names (ignore them, as the Recycle Bin restore bug in HANDOVER showed they bite), names with characters invalid in other places, very long paths (over 260 characters).
- The `$MFT` sizes for compressed, sparse and cloud-only files differ from what Explorer shows; decide per column which size is shown.
- Folder-scan mode misses changes on network shares where watchers do not work; rescan intervals cover them.

### 6.5 Scope and naming
- "All features of Everything" is about two decades of work by its developer. Phases 1 to 3 cover what most people use; phases 5 and 6 are where cost grows fastest and value drops. Each phase is a separate decision.
- Everything is closed-source; PC Butler copies feature ideas and the search syntax (which users already know), not code, and does not use the name "Everything" in the program.

## 7. What is needed from Mohammad

Decision 1 is made (the service). Decisions 2 and 3 go with their defaults unless Mohammad says otherwise. Phase 0 is done (5a); the next step is phase 1, search without administrator rights.

Sources consulted: voidtools' searching help page (<https://ftp.voidtools.com/support/everything/searching>), the voidtools forum (<https://www.voidtools.com/forum/viewtopic.php?p=82042>), and the PC Butler repository (`HANDOVER.md`, `AdminHelper.cs`, `Sensors.cs`, `Storage.cs`, `MainWindow.cs`, `Overlay.cs`, `Ui.cs`, the project files). Everything's full function and modifier lists come from its documentation as known before this plan; check the exact spelling of each against voidtools' "Search functions" page when implementing phase 3.
