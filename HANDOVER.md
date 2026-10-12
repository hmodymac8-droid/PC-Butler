# Handover: rewriting Downloads Sorter in C# as PC Butler

This file is for whoever continues the work. It says what the job is, what has been
decided, what exists, what is missing, and how to build and release.

## The job

Rebuild the Python/Tkinter program **Downloads Sorter** in **C#** as **PC Butler**:

- Same features, same behaviour, same look. Nothing added, nothing dropped.
- New name: **PC Butler** (the owner first said "PC Keeper"; that is an existing commercial
  product, PCKeeper, so "PC Butler" was chosen instead and matches this repository's name).
- New repository: `hmodymac8-droid/PC-Butler` (this one).
- License: **GPL v3** (`LICENSE` is the official text). Copyright line: `hmodymac8-droid`.
  The owner has not yet said whether to use a real name instead; ask before a release.
- Releases start again at **1.0.0** (tag `v1.0.0`).

## The reference: the Python program

Repository: <https://github.com/hmodymac8-droid/downloads-sorter> (branch `main`, version 1.8).
It is still being changed by its owner, so pull the latest before porting a page.

| Python file | What it is | C# counterpart | Status |
|---|---|---|---|
| `sort_downloads.py` | Engine: settings, planning, moving, history, undo, Recycle Bin, auto clean-up, updates, background loop | `src/PCButler.Core/` `Config.cs`, `Sorter.cs`, `RecycleBin.cs`, `Updater.cs` | **Done, tested** |
| `duplicates.py` | Duplicate finder, five match modes | `Duplicates.cs` | **Done, tested** |
| `cleaner.py` | Junk-file scanner | `Cleaner.cs` | **Done, tested** |
| `booster.py` | Startup apps, running programs, power plan | `Booster.cs` | **Done, read-only parts tested** |
| `vault.py` | Password-protected encrypted storage | `Vault.cs` (includes `Scrypt`) | **Done, tested, opens Python-made vaults** |
| `i18n.py`, `translations_ar.py` | Arabic interface | `Tr.cs`, `Translations/ar.json` | **Done** (425 strings converted) |
| `theme.py` | Colours, fonts, widgets | `src/PCButler/Theme.xaml`, `Ui.cs` | **Written, never compiled or run** |
| `sort_gui.pyw` | All windows and pages | `src/PCButler/` | **Not started** (see below) |
| `installer.iss`, `build.py` | Inno Setup installer and build | `installer/`, a build script | **Not started** |

## What exists and works

`src/PCButler.Core` builds with 0 warnings, and `tests/PCButler.Tests` passes:

```
dotnet run --project tests/PCButler.Tests
```

The tests use a temporary folder (`PCBUTLER_DATA` overrides the data folder) and cover:
settings and the migration of old settings, planning and moving, never overwriting, history,
undoing one file or a whole run, delete to the Recycle Bin and restore from it, delete for
good, the 90-day auto clean-up, all duplicate modes, the cleaner scan, booster listings,
scrypt against the RFC 7914 test vector, the vault round trip and tamper detection,
translations, and version comparison. The suite was last run, and passed, at this handover.

Passing two arguments, the data folder of a vault created by the Python program and its
password, also proves the C# vault opens it. That was run and passed.

### Decisions already built into the engine

- **File formats are unchanged.** `config.json` and `history.json` use the same keys as the
  Python program, and the vault format is byte-compatible (`DSV1`, AES-256-GCM in 1 MB
  chunks, scrypt n=2^17 r=8 p=1).
- **Data folder** is `%APPDATA%\PCButler`. On first run `AppInfo.ImportLegacyData()` copies
  (never moves) settings, history and the vault from `%APPDATA%\DownloadsSorter`. Nothing
  calls it yet; the app's startup should.
- **One process, not two.** The Python program runs a separate background process for the
  tray icon. The C# design is a single process: tray icon plus a window that can be closed and
  reopened. `BackgroundService` (in `Updater.cs`) is the 5-second loop; it raises events
  (`Notify`, `ReportWanted`, `StateChanged`, `UpdateFound`, `DataChanged`) for the UI.
  A second launch should signal the first to show its window.
- **Translations** are looked up by the English text: `Tr.T("Moved {0} file(s).", count)`.
  `tools/convert_translations.py` regenerates `ar.json` from the Python `translations_ar.py`
  and renames "Downloads Sorter" to "PC Butler" in it.
- **Deleting** always goes to the Recycle Bin unless the user picks "Delete for good", and
  every deletion is logged so History can restore it.
- **Autostart** is the per-user Run value `PCButler` = `"<exe>" --background`.
- **Updates** read `https://api.github.com/repos/hmodymac8-droid/PC-Butler/releases/latest`
  and expect an asset whose name ends in `setup.exe`. Updating runs that installer with
  `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (plus `/reopen=1` when started from the window).
  `AppInfo.IsInstalled` is true when `unins000.exe` sits beside the program.

## What is missing: the whole interface

`src/PCButler` has only the project file, the manifest, `Theme.xaml` and `Ui.cs`. **It does
not build yet**: there is no `App.xaml`, no `MainWindow`, and `Ui.cs` and `Theme.xaml` have
never been compiled, so expect errors in them.

Still to write, all in WPF (`net10.0-windows`, .NET SDK 10 is installed):

1. `App.xaml` / `App.xaml.cs`: merge `Theme.xaml`, single instance, tray icon
   (`System.Windows.Forms.NotifyIcon`), `BackgroundService`, arguments `--background`
   (start hidden), `--no-splash`, `--auto-on` (set `auto_sort` true, used by the installer).
2. `MainWindow`: sidebar, breadcrumb bar, page header, status bar, update banner.
3. Eight pages: Sorter, Duplicates, Cleaner, Booster, Vault, History, Settings, Help & about.
4. Windows: splash, the report window after an automatic run, category dialog, vault
   change-password dialog. (`DeleteHowDialog` is already in `Ui.cs`.)
5. `installer/installer.iss` (adapt the Python project's) and a build script
   (`dotnet publish` self-contained, then Inno Setup's `ISCC.exe`).
6. `README.md` in English and Arabic, as the Python project has.

`Ui.cs` already provides: colours, `Text`/`Muted`/`Glyph`/`Button`/`Switch`/`Field`, the
`Row` class used by every list, `Ui.List(...)`, `Ui.AttachFileMenu(...)` (right-click: show in
Explorer, open, delete; double-click shows in Explorer), `StatCard`, `CardPanel`,
`DeleteHowDialog`, message boxes and `Ui.StyleWindow` (dark title bar).

## The design to match

Screenshots of the Python program are in `docs/reference/` (Sorter, Duplicates, Cleaner,
Vault, About, splash). To see the other pages (Booster, History, Settings, the report window)
run the Python program.

- **Colours:** page `#0d0e11`, cards `#141517`, sidebar `#090a0c`, quiet fill `#1c1e21`,
  selected row `#24272a`, lines `#2a2b2e`, text `#e9ebee`, muted `#a7abb1`,
  accent `#48d7a1` with `#041b12` text on it, amber `#e6b55d`, red `#f45a56`.
- **Tile tones** (per destination folder, in order): `#48d7a1 #69acf3 #e6b55d #e581bc #a993f0
  #5cc6d6 #d99a6c #9ccf6a`. A tile is the tone mixed 14% into the card colour, with the tone
  as its text; "Other" and tone-less tiles are grey.
- **Fonts:** DM Sans for text, Manrope for headings, both bundled in `src/PCButler/Fonts`
  (SIL Open Font License, licence files included). Icons are glyphs from
  "Segoe Fluent Icons" / "Segoe MDL2 Assets".
- **Layout:** left sidebar (brand, "WORKSPACE" group, storage bar, Settings and Help at the
  bottom, version and a Donate link); a breadcrumb bar "Workspace > page" with the
  automatic-sorting state on the right; each page has the eyebrow "YOUR DIGITAL SPACE,
  SORTED", a large title, a one-line description and its main button on the right; most pages
  then show three summary cards and one bordered panel holding a list; a status bar at the
  bottom with "Pause for today" and "Turn on automatic sorting".
- **Sidebar glyphs:** Sorter `E8FD`, Duplicates `E8C8`, Cleaner `E74D`, Booster `E945`,
  Vault `E72E`, History `E81C`, Settings `E713`, Help & about `E946`. Others used: file
  `E8A5`, drive `EDA2`, folder `E8B7`/`E838`, filter `E71C`, shield `E83D`, chip `E950`,
  power `E7E8`, undo `E7A7`, clock `E823`, refresh `E895`, chevron `E76C`.
- **Page titles and descriptions** are in `sort_gui.pyw`, in the tuple inside `App.__init__`.
- The Donate link points to `https://paypal.me/mfsafadi` (`AppInfo.DonateUrl`).

## Behaviour that is easy to get wrong

- The Sorter list shows only files old enough to move, unless "Show all files" is on.
- A file the user put back with Undo is not moved again automatically; "Sort all now" still
  takes it.
- Automatic runs mark their history entries `review: true`; the report window lists those and
  offers Approve and Undo all. Only one report window is open at a time.
- Turning automatic sorting off keeps the tray icon; only the schedule and the autostart entry
  go. A check interval of 0 also means off.
- History is paged (50/100/250/500 rows) and searchable; Undo works on Moved and Deleted rows.
- The vault locks itself after 10 minutes without use, and on locking saves edits made to
  opened copies.
- Opening the window from the tray icon skips the splash screen.
- Arabic: WPF lays out right-to-left text correctly by itself, so the word-reordering
  workaround in the Python `i18n.py` must **not** be ported.

## Building and releasing

- Build: `dotnet build src/PCButler.Core` (works today). For the app, once it exists:
  `dotnet publish src/PCButler -c Release -r win-x64 --self-contained`.
- Installer: Inno Setup 6 is installed at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
  The Python project's `installer.iss` is a working template: per-user install, English and
  Arabic, closes the running program, keeps automatic sorting as it was on silent updates,
  writes `%APPDATA%\...\updated.txt` so the program can announce the update. Change the
  names, the AppId, the data folder name (`PCButler`) and the executable (`PCButler.exe`).
- Release: tag `v1.0.0`, create a GitHub release, attach `PCButlerSetup.exe` (the updater
  matches any asset ending in `setup.exe`). `AppInfo.Version` must equal the tag.
- Git identity for this repository is set locally to `hmodymac8-droid` with GitHub's private
  address `hmodymac8-droid@users.noreply.github.com`, so no real email is published.
- The owner asked that commits and published files not mention AI tools.

## Loose ends from the Python project

- Its owner's installed copy was set up from inside a sandboxed desktop app, which wrote
  settings and registry entries to a private location. `finish_install.cmd` in the Python
  project folder (not in git) fixes that. It is unrelated to this repository, but explains why
  that program's history may look empty on the owner's PC.
- The Python project switched from MIT to GPL v3 at version 1.8; versions 1.5 to 1.7 remain
  MIT. This C# project is GPL v3 from the start.
