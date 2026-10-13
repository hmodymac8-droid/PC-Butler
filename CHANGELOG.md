# What's new in PC Butler

This file is shown inside the program (Help & about, and once after each update).
Each version is a `## heading` followed by `- ` lines.

## 1.14.0

- Portal is now called Courier.
- Courier over the internet: every PC has a 12-digit ID. Switch on "Reachable over the internet" on one PC, type its ID on the other, and the two connect directly, with no server in between.
- IDs are looked up in the public directory that torrent programs use; files never pass through it. Not every pair of networks can link directly (mobile data often can't).

## 1.13.0

- Portal: send files and whole folders between two PCs running PC Butler, straight over your own network. Nothing goes through the internet.
- Pair two PCs once by comparing a six-digit code on both screens. Everything sent is encrypted.
- Progress, speed and Cancel on both PCs; a transfer that loses its connection carries on where it stopped.
- The drop zone can send what you drop on it to another PC.

## 1.12.2

- Licence: PC Butler is now proprietary software (all rights reserved). It stays free to download and use. Versions up to 1.12.1 remain under the GPL v3 they were released with.

## 1.12.1

- Launch: the launch screen comes up first, and everything else starts behind it. While it shows, the Cleaner scans and the Programs page reads the installed programs and checks for updates, so the results are waiting.
- Clipboard: picking an item now pastes it into the program you were in (before, it was only copied).
- Programs: every list has tick boxes, and a right-click menu for each program: Run, Open file location, Update and Uninstall when it is installed; Install or open its page when it is not. Essential and Recommended apps list the installed ones first.

## 1.12.0

- Clipboard: a new page and a clipboard manager. Turn it on to keep what you copy (text, files and pictures, up to 500 items by default, or any limit you set), search it, pin items, copy them back or delete them. A shortcut (Ctrl+Alt+V by default, changeable) opens the list in any program: pick an item and it is pasted there. Items that password managers mark as private are skipped.
- Home: the "Last restart" card is now called Uptime.
- Settings: a Windows Sandbox switch turns Windows' throwaway, safe copy of Windows on or off (administrator rights and a restart), with a button to open it. It needs Windows Pro, Enterprise or Education.
- Programs: the Essential apps and Recommended apps lists are rearranged. Essential: Chrome, WinRAR, Visual C++ Redistributable, .NET Desktop Runtime 8, Discord, Steam, Git, MPC-HC, Visual Studio Code, TeraCopy and WizTree. Recommended adds GitHub Desktop and MSI Afterburner.
- Recommended apps: added EchoSub, EchoSub Lens and ATS CV Studio (installed from their latest GitHub release, checked against its checksum), AndroidDC (opens its page) and Prayer Times Reminder (from the Microsoft Store).

## 1.11.1

- Recommended apps: added WinRAR, Google Chrome, Git (with Git Bash), Media Player Classic (MPC-HC) and WizTree. The list now starts with WinRAR, Visual Studio Code, Discord, Chrome, Steam, Git, MPC-HC, TeraCopy and WizTree.

## 1.11.0

- Programs: two new tabs, Essential apps and Recommended apps. Each lists well-known free apps, shows which are already installed, and installs the ones you select through winget.

## 1.10.2

- Settings: the language choice is now at the top of the page.

## 1.10.1

- Settings: "Start PC Butler when Windows starts" and "Sort automatically" are now two separate switches. Before, one button did both. The installer offers them separately too.
- The program, window and Task Manager icons also show the logo without the dark square.
- The tray icon and the "PC Butler" entry in File Explorer's right-click menu show the logo on a transparent background, without the dark square.

## 1.10.0

- New look: the new PC Butler logo, a refreshed colour palette and the Manrope typeface.
- Home page: ring gauges for the processor, graphics card, memory and time since the last restart, a free-space bar, and two panels for storage and the neatness score.
- Sidebar and top bar: the logo beside the name, the open page marked with a dot, and a simpler top bar.

## 1.9.1

- Right-click in File Explorer: a "PC Butler" submenu with Sort into its category folder, Lock in the Vault and Shred, for one or many files and folders at once.
- Storage: an All drives tab shows how full each drive is; click one to measure it.
- Booster and Help & about: tidier pages, each setting a row with its switch on the side.

## 1.9.0

- Shredder: deep delete. Files are overwritten with random data and then deleted, so recovery tools find nothing. It has its own page and cannot be undone.
- Drop zone: a small tile that stays on top. Drag files or folders onto it to sort them, lock them in the Vault, shred them or recycle them. Hide it from its right-click menu, the tray menu, or Help & about.
- Right-click in File Explorer: "Process with PC Butler" now also offers Lock it in the Vault and Shred it.
- Windows 11: a switch in Settings chooses between the classic full right-click menu and the short one.
- Settings: the File Explorer, drop zone and automatic-sorting switches are now on the Settings page, and Save settings is at the bottom.

## 1.8.0

- Right-click a file or folder in File Explorer and choose "Process with PC Butler": sort it, sort a folder's files, or recycle a file. On Windows 11 it is under "Show more options". It can be switched off in Help & about.
- What's new: after an update, a window lists what changed. It is also under Help & about.

## 1.7.0

- Graphics-card temperature for every card (AMD, Intel, NVIDIA), with no administrator rights.
- Processor temperature and fan speeds are shown when LibreHardwareMonitor is running.
- The window always opens maximized.
- Speed-test results are kept, with a Clear history button.
- Programs: a Drive column, a filter by drive, and double-click opens the program.

## 1.6.0

- Programs page: every installed program in one list.
- Uninstall, then choose which leftovers to remove (folders, shortcuts, its registry key).
- Updates through winget.
- Internet page: a speed test for download, upload and response time.
- History: a Clear history button.

## 1.5.0

- Boost can pause three Windows services (Print Spooler, Telemetry, SysMain) through the optional administrator helper, and resumes them afterwards.

## 1.4.0

- Boost: one program runs at high priority while background programs step back, on the high-performance power plan.
- Everything is put back when Boost stops, even after a crash.
- Boost can switch on by itself while programs you list are running.
- Optional memory trimming for background programs.

## 1.3.0

- Home page: processor, memory and graphics-card use, free space, last restart.
- Neatness score out of 100, with the reasons it lost points.
- The tray icon shows processor and memory use and warns when they stay nearly full.
- Optional helper with administrator rights for the motherboard's temperature and fan sensors.

## 1.2.0

- Sorting and cleaning can run daily or weekly at a time you set.
- Cleaner: privacy categories (recent-files list, program logs).

## 1.1.0

- Storage page: a map of what fills a folder, and a list of large files not used for a long time.

## 1.0.3

- Every list can be sorted by clicking a column heading.

## 1.0.2

- Vault: a recovery key file lets you set a new password if you forget it (Vault page, Recovery key…).
- The tray icon's menu has the program's dark look and shows whether automatic sorting is on.

## 1.0.1

- Arabic flips the whole window to right-to-left.

## 1.0.0

- First release: Downloads Sorter rebuilt in C# as PC Butler.
