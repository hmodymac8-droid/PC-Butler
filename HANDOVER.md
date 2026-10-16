# Handover: rewriting Downloads Sorter in C# as PC Butler

This file is for whoever continues the work. It says what the job is, what has been
decided, what exists, what is missing, and how to build and release.

## The job

Rebuild the Python/Tkinter program **Downloads Sorter** in **C#** as **PC Butler**:

- Same features, same behaviour, same look. That was version 1.0; the owner has asked for new
  features since (see "Added after 1.0" below).
- New name: **PC Butler** (the owner first said "PC Keeper"; that is an existing commercial
  product, PCKeeper, so "PC Butler" was chosen instead and matches this repository's name).
- New repository: `hmodymac8-droid/PC-Butler` (this one).
- License: **proprietary, all rights reserved**, from version 1.12.2 (`LICENSE` holds the terms; it is the page the setup wizard
  shows). Versions 1.0.0 to 1.12.1 were GPL v3 and stay so as released. Copyright line: `hmodymac8-droid`
  (the owner confirmed no real name). Only the owner changes the program; contributions are not accepted.
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
| `theme.py` | Colours, fonts, widgets | `src/PCButler/Theme.xaml`, `Ui.cs`, `Chrome.cs` | **Done** |
| `sort_gui.pyw` | All windows and pages | `src/PCButler/` (`App.xaml.cs`, `MainWindow.cs`, `Pages/`, `Dialogs.cs`, `ReportWindow.cs`) | **Done, builds with 0 warnings** (see "Testing") |
| `installer.iss`, `build.py` | Inno Setup installer and build | `installer/installer.iss`, `build.ps1` | **Done, built and test-installed** |

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
  (never moves) settings, history and the vault from `%APPDATA%\DownloadsSorter`. The app's
  startup calls it.
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

## The interface

All of it is written in WPF (`net10.0-windows`), built from code; the only XAML is
`App.xaml` and `Theme.xaml`.

- `App.xaml.cs`: one process. Single instance (a second launch brings up the running
  program's window; a second `--background` launch just exits), the tray icon (`NotifyIcon`,
  its menu rebuilt each time it opens), `BackgroundService` wired to tray notifications, the
  report window and the main window, the legacy data import, and the arguments `--background`,
  `--no-splash` and `--auto-on`. The window can be closed and reopened; Exit in the tray menu
  ends the program.
- `MainWindow.cs`: sidebar, breadcrumb bar, update banner, status bar, saved size and
  position (`window_geometry`, in screen pixels), and a compact mode below 720 px of height
  (the summary cards and the headers' small print hide).
- `Chrome.cs`: the `Page` base class (header, page buttons, summary cards), `Sidebar`,
  `NavItem`, `TopBar`, `SplashWindow`.
- `Pages/`: the eight pages. `Dialogs.cs`: the category and vault change-password dialogs.
  `ReportWindow.cs`: the window after an automatic run. `Ui.cs`: shared building blocks.
- Arabic: text goes through `Ui.T`. The layout is not mirrored. An implicit `TextBlock` style
  in `Theme.xaml` makes any text containing Arabic read right to left, so full stops and
  quotes land on the correct side; WPF ignores the RLE/PDF control characters, so don't use
  them. Message boxes use right-to-left reading in Arabic.
- Resource URIs name the assembly (`pack://application:,,,/PCButler;component/...`), so they
  also resolve when another program hosts the window (a test harness).

### Testing

- Every page, the launch screen and the report window were rendered in English and Arabic
  and compared with `docs/reference/`.
- Driven live through UI Automation against a scratch data folder (`PCBUTLER_DATA`, with
  `TEMP` pointed at a fake temp folder for the Cleaner), all of which worked:
  - Sorter: Organize, Sort all now, Undo last move.
  - Duplicates: scan, the pre-ticked copy, delete to the Recycle Bin; History then brought the
    file back out of the Recycle Bin.
  - Cleaner: scan, clean only the ticked category (empty folders removed).
  - Booster: a startup entry switched off and on (StartupApproved 3, then 2), a program ended
    and its memory reported freed. Power plans were listed but not switched.
  - Vault: create, add, move out, lock, a wrong password refused, unlock, change password,
    delete, unlock with the new password.
  - Settings: save (age and never-move list written, Sorter updated), switch to Arabic and back
    (the window reopens in the new language).
  - An automatic run: files moved by the background sorter, the report window listed them,
    Undo all put them back, and the next automatic pass left them alone.
- Bugs this found and fixed: Recycle Bin restore failed for paths written with short
  (8.3) folder names; the language switch did not reopen the window; Booster's tabs and the
  sidebar were invisible to screen readers and UI Automation.
- Not exercised: the tray menu (UI Automation can't reach the notification area) and an update
  from a real release.
- `build.ps1 -Test` built a TestBuild installer. It installed silently into a scratch folder,
  the installed program started without the .NET SDK, and it uninstalled cleanly.

### Added after 1.0

Each was asked for by the owner, has engine tests, and was published as its own release.

| Version | What | Engine | Window |
|---|---|---|---|
| 1.0.1 | Arabic flips the whole layout right-to-left | | `Ui.StyleWindow`, `Theme.xaml` |
| 1.0.3 | Every list sorts by clicking a column heading | | `SortableList` in `Ui.cs` |
| 1.1 | Storage: treemap of a folder, large and old files | `Storage.cs` | `Pages/StoragePage.cs` |
| 1.2 | Daily/weekly schedules for sorting and cleaning; privacy categories in the Cleaner | `ScheduleSettings` in `Config.cs`, `Updater.cs`, `Cleaner.cs` | `Pages/SettingsPage.cs` |
| 1.3 | Home page, neatness score, tray usage monitor, optional sensors helper | `SystemMonitor.cs`, `Sensors.cs` | `Pages/HomePage.cs`, `App.xaml.cs` |
| 1.4 | Boost: priorities, power plan, optional memory trim, automatic for listed programs | `BoostMode.cs` | Boost tab in `Pages/BoosterPage.cs` |
| 1.5 | Boost can pause three services through the administrator helper | `AdminHelper.cs`, `Sensors.RunHelper` | Boost tab |
| 1.6 | Programs (uninstall with leftovers, winget updates), internet speed test, Clear history | `Programs.cs`, `SpeedTest.cs`, `History.Clear` | `Pages/ProgramsPage.cs`, `Pages/InternetPage.cs`, `Pages/HistoryPage.cs` |

| 1.7 | Graphics-card temperature from Windows (no rights needed), readings from LibreHardwareMonitor when it runs, window always opens maximized, speed-test history with Clear, Programs: drive column and filter, double-click runs | `GpuSensors.cs`, `Sensors.MonitorApp`, `SpeedTest.History`, `Programs.FindLauncher` | `Pages/HomePage.cs`, `Pages/InternetPage.cs`, `Pages/ProgramsPage.cs`, `MainWindow.Reveal` |
| 1.8 | What's new window after an update (from `CHANGELOG.md` / `CHANGELOG.ar.md`, built into the program), File Explorer's right-click entry "Process with PC Butler" | `Changelog.cs`, `ExplorerMenu.cs` | `WhatsNewWindow.cs`, `ProcessWindow.cs`, `App.xaml.cs` (`--process <path>`), `Pages/AboutPage.cs` |
| 1.9 | Shredder page (deep delete), drop zone, Vault and Shred in the right-click window, Windows 11 classic-menu switch; the File Explorer, drop-zone and automatic-sorting switches moved to Settings with Save settings at the bottom | `Shredder.cs` (with `VaultInbox`), `Sorter.PlanDropped`, `ExplorerMenu.SetClassicMenu` | `Pages/ShredderPage.cs`, `DropZoneWindow.cs`, `ProcessWindow.cs`, `Pages/SettingsPage.cs`, `Pages/VaultPage.cs` (`AddFromOutside`) |
| 1.10 | New look from the owner's own design (a web mock-up they supplied): new logo, palette, Manrope text, sidebar and top bar, and the Home page rebuilt with ring gauges | | `Theme.xaml`, `Widgets.cs` (`Gauge`, `MeterBar`, `MetricCard`, `AmountCard`), `Pages/HomePage.cs`, `Chrome.cs`, `icon.png`, `icon.ico` |

The look now follows that mock-up, not `docs/reference`. Its colours are in `Theme.xaml`; its heading typeface is Sora, which is named first in `DisplayFont` but not bundled yet, so headings fall back to Manrope until `Fonts/Sora[wght].ttf` (OFL) is added. `icon.png` is the bare logo for use inside the program; `icon.ico` is the logo on a dark tile for the taskbar and the installer.
| 1.13 | Portal: files and folders between two PCs over the local network | `Portal.cs` (`PortalNode`, `Portal`), `AdminHelper` action `allow_portal` | `Pages/PortalPage.cs`, `App.PortalNeedsAttention`, drop zone entry |
| 1.14 | The feature is renamed Courier for the user (the code still says Portal). Courier over the internet by 12-digit ID, with no server of ours | `PortalInternet.cs` (ID, reachable, `ConnectById`), `PortalDht.cs` (bencode, a minimal BitTorrent-DHT client), `PortalTunnel.cs` (`PortalLink`: one UDP socket, hole punching; `ReliableStream`: reliable ordered bytes over UDP) | `Pages/PortalPage.cs` |

Version 1.17 added, all in `TuneUp.cs` on the engine side:

- **Tune-up** (Booster > Advanced, `Pages/BoosterAdvanced.cs`). `TuneUp.Catalogue` is the fixed list of tweaks; each is a list of steps (`RegistryStep`, `ServiceStep`, `PowerPlanStep`, `AnimationStep`) with the lowest preset that includes it. `TuneUp.Apply(keys, level)` first undoes what the tune-up had applied, then applies the chosen tweaks; `tuneup.json` holds, for every step, what was there before (`-` = no value, `d:` a number, `s:` text, `start:delayed:running` for a service, `""` = the step changed nothing). `TuneUp.Undo()` puts it all back and leaves alone a value the user changed since. Steps under HKCU run in the program; tweaks marked `Admin` go to the helper as `tune_apply` (tweak keys) and `tune_restore` (`key#step=before`), and the helper only restores values of the right kind for steps in the catalogue. Without the helper those tweaks are reported in `TuneResult.NeedsHelper` and stay as they are.
- **Stop Windows updates** (Settings) is the catalogue tweak `windows_update`, group `update`, outside the presets: it sets the pause dates under `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` ten years ahead and the `NoAutoUpdate` policy. Not tried on a real PC with the helper yet.
- **Rename the PC** (Settings): `PcName`, helper action `rename_pc` (`SetComputerNameEx`, takes effect after a restart).
- **IP addresses** (Internet page): `NetworkAddresses.Local` and `.Public` (Cloudflare's `/cdn-cgi/trace`).
- **Courier pause, resume and times**: `PortalTransfer.Pause/Resume`, `Paused`, `PausedThere`, `Elapsed`, `Speed`, `Remaining`. A paused sender cancels the piece in flight and waits; a paused receiver answers 423 and the sender polls until it is let go; `POST send/{id}/hold?on=1` tells the receiver that the sender paused. IDs are nine digits (`Portal.NumberOf`), and the directory keys changed with them (`pcbutler-courier-v3`), so 1.16 and 1.17 do not find each other.

Version 1.18 added:

- **Benchmark page and overlay** (`Benchmark.cs`, `Overlay.cs`, `Pages/BenchmarkPage.cs`). `OverlayController` ticks once a second: `GameDetector.Foreground()` reads the window in front, `LooksLikeGame` decides (on the user's list, or borderless full screen and not in `NeverGames`), `OverlayWindow` is a click-through topmost WPF window placed with `SetWindowPos`. It cannot show over exclusive full screen. `BenchmarkRecorder` turns the readings of one game into a `BenchmarkSession` in `benchmark.json`.
- **Frames per second**: `FpsMeter`. The helper (action `fps`, `on`/`off`) opens an ETW session named `PCButlerFps` with the TraceEvent library and counts DXGI event 42 and D3D9 event 1 per process, falling back to DxgKrnl event 184 (keyword 0x8000000) for programs that present another way; it writes `fps.json` once a second. An account in Windows' "Performance Log Users" group may open that session without administrator rights, so `OverlayController` first tries `FpsMeter.Start` in the program itself and only falls back to the helper when Windows refuses or nothing is reported for 15 seconds. Counting was checked for real that way on the owner's PC (1.21.2); the helper's path runs the same code but has not been run yet.
- **Processor temperature**: `CpuSensor`, in the helper, through LibreHardwareMonitorLib 0.9.6, which needs the PawnIO driver installed by the user (`CpuSensor.DriverInstalled` looks for its uninstall key). `SensorReading.CpuTemperature` carries it to the Home page and the overlay. Never bundle or install a driver; older versions of that library used WinRing0, which Windows Defender flags, so do not downgrade it.
- **Hotkeys** (`Hotkeys` in `Overlay.cs`, the Hotkeys tab in `SettingsPage`): `config.hotkeys` maps an action to keys; `App.RunHotkey` carries them out. The clipboard manager keeps its own shortcut.

Version 1.21.6 changed and added:

- **Courier has no host and clients any more.** Every node announces itself under its own ID (`KeepAnnounced`, `KeepMapped`), takes paths from anyone who knocks (`AllowPath = true`; what comes over them is still checked by certificate), and keeps connected the PCs on its list marked `Stay` (`PortalSaved.Stay`; `KeepConnected` runs one `Reach` per number, state per number in `Attempt`, read through `StateOf(number)` and `PasswordAsks`). `Connect(number)` sets Stay; `Disconnect(number, forget)` posts `disconnect?forget=0|1` to the other PC, which clears its own Stay for this one (and with forget its trust), and both close the path. `PortalLink.Close` and `Dispose` send a goodbye packet (type 10; 5 to 8 are the streams' own), so the other side drops the path at once instead of after 45 seconds of silence. `GET ping` tells a connecting PC whether it has been let in (403 until then). The relay through a host is gone. Old settings are brought over in `PortalSavedList.Start` (`role`/`host_number` read once, `favorite` becomes `stay`). `PortalNode.Received` fires when a whole transfer has arrived: Windows shows a tray notification, the phone a notification of its own.
- **Sorter categories can delete** (`Category.Delete`): `PlannedMove.Delete`, and `MoveFiles` sends those to the Recycle Bin, logged as `[source, "", "deleted"]`. A restored file (`"restored"`) is skipped like an undone one. `MoveFiles` returns `SortResult` (Count, Errors, Deleted), still deconstructible as `(count, errors)`.
- **Clipboard items with a password** (`ClipEntry.Sealed`, `ClipboardHistory.Lock/Unlock/Relock/Peek/RemoveLock`): AES-256-GCM, PBKDF2-SHA256 210,000 rounds; a locked picture's PNG goes inside the sealed text and its file is deleted. `Save` writes a locked item's sealed form only, so what an unlocked one holds stays in memory.
- **App usage** (`AppUsage.cs`): `AppUsageRecorder` (started by `App.RecordUsage`) looks every 5 s at every process (`GetProcessTimes`, `GetProcessIoCounters` with PROCESS_QUERY_LIMITED_INFORMATION, the "GPU Engine" counters per pid) and adds the differences per program name per day to `app-usage.json` (`AppUsageLog`, 90 days). Internet use per program comes from the helper (action `net`, `NetMeter`, ETW Microsoft-Windows-Kernel-Network, the first two payload fields are PID and size), written to `net.json` like `fps.json`. Not tried on a real PC yet.
- **AndroidDC** is `AppSource.Archive`: the repository's main.zip is unpacked into `%LOCALAPPDATA%\Programs\AndroidDC` (`Programs.Unpack`, over what is there) and its `Steps` run in order; `.pcbutler-installed` marks it installed.
- The right-click submenu is grouped with `CommandFlags` = ECF_SEPARATORBEFORE (0x20) on the first item of a group; the keys are now `1sort`, `2courier`, `3vault`, `4shred`.

How the internet part works: a reachable PC announces itself in the public BitTorrent DHT under a key made from its ID, and polls a second "knock" key. A PC that wants to connect looks the first key up and announces itself under the knock key, so each learns the other's public address; both then send UDP packets to each other until one gets through both routers. Over that path runs a small reliable-stream protocol of our own, and through it the unchanged HTTPS protocol: the sender's `HttpClient` gets the stream from a `ConnectCallback`, and on the receiver each arriving stream is pumped into the node's own listener (`PumpIn`), with `pumps` mapping the local port back to the far address. So pairing, trust and transfers are the same code as on a local network, and TLS with pinned certificates is what protects them; the directory and the path are untrusted. The directory returns junk at times (strangers' addresses, this PC's own): harmless, because nothing answers there. Proven here: announce, lookup and knock against the live directory, and pairing and transfers over a path on the loopback address. NOT proven: two PCs on two different internet connections; one router could not be used for that (it does not route to its own public address).

How Portal works, for whoever changes it: each PC runs an HTTPS server (ASP.NET's Kestrel, a framework reference of `PCButler.Core`) and announces itself by UDP broadcast on port 53531. Both ends of every connection show a self-made certificate; a PC's id is that certificate's SHA-256 fingerprint, and "paired" means the fingerprint is in `portal.devices` in config.json. Pairing compares a six-digit code built from both fingerprints and a random part from each side; the starting side sends only the hash of its part first, so nobody in between can steer the code. Files go in 8 MB pieces that must each start where the last ended, which is what makes resuming work. Names and paths from the other PC go through `Portal.SafeName` / `SafePath` before anything is created. The tests run three nodes on the loopback address. A phone page served to a browser was built first and dropped at the owner's request; it is in the `portal` branch's history.

Only the main window writes to the vault: the right-click window and the drop zone leave paths in `vault-inbox.txt` and open the window, which adds them after the password. Two writers would overwrite each other's index.

Every release needs a new `## x.y.z` section at the top of both changelog files; a test fails if the
current version is not on top or the two files differ in shape.

Rules the owner set for these, which still hold:

- PC Butler itself never runs as administrator. What needs those rights goes through the helper
  (`PCButler.exe --sensors <file> <parent pid>`, started with the "runas" verb), is optional, and is
  switched on by the user. The main program leaves requests in `helper-command.json`; the helper
  only accepts what `AdminHelper` lists, because any program can write that file.
- Boost writes everything it changes to `boost.json` before changing it; `BoostMode.Recover` puts
  it back at the next start, and the helper resumes services when the main program is gone.
- Nothing is deleted without the user choosing it. Leftovers are matched by exact name only, a
  folder another program lives in is never offered, and registry keys are exported before removal.
- No invented numbers: Boost shows what it measured (memory freed, programs changed), not an
  estimated speed-up.

Asked for and not built yet: a manager for the right-click menu's entries (disable, not delete); clipboard history (encrypted, off by default); global hotkeys; unpacking
archives and cleaning names; a grouped Sorter preview; a per-file history; and a per-program
network-usage monitor (view only, through the administrator helper).

### Still to do

1. Try by hand what tests cannot reach: the Windows permission prompt for the helper (sensors and
   pausing services), a real uninstall with leftovers, and a winget update.
2. The copyright line stays `hmodymac8-droid` (the owner's decision).

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

- Build: `dotnet build src/PCButler`. Installer: `./build.ps1` publishes the program
  self-contained into `artifacts/dist` and writes `PCButlerSetup.exe` next to the script.
  `-Test` builds the TestBuild variant; `-Dotnet <path>` picks the SDK when the first `dotnet`
  on PATH is older than 10. The script refuses to build when `AppInfo.Version` and the
  `<Version>` in `PCButler.csproj` differ.
- Inno Setup 6 is installed at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
  `installer/installer.iss` is adapted from the Python project's: per-user install, English
  and Arabic, closes the running program, keeps automatic sorting as it was on silent updates,
  and writes `%APPDATA%\PCButler\updated.txt` so the program can announce the update. Its
  AppId (`C4BFA050-D93B-4180-B2D4-ACCDBE95D00A`) is new; never change it after a release.
- On the machine this was built on, the .NET 10 SDK is in `D:\dotnet` (the one on PATH is
  8.0), with NuGet's cache in `D:\nuget\packages`.
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
  MIT. This C# project was GPL v3 from 1.0.0 to 1.12.1 and is proprietary from 1.12.2. Everything in it was written for the
  owner, who holds the copyright, so nobody else's permission was needed; what it bundles from others (the fonts under the
  OFL, the .NET runtime under MIT) allows use in closed software.
