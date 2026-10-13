# What's new in PC Butler

This file is shown inside the program (Help & about, and once after each update).
Each version is a `## heading` followed by `- ` lines.

## 1.19.1

- Clipboard: right-click an item, in the list the shortcut opens or on the Clipboard page, for paste, copy, search with Google (a copied picture is searched with Google Lens), open its link or its file's folder, pin and delete.
- Clipboard tags: give items tags such as "work" or "address". Tags show under each item; searching finds them, and "#work" shows only what is tagged so.
- The Clipboard page has two tabs, History and Settings, instead of one long page.
- A clipboard history file that can't be read is set aside as history.broken.json instead of being written over.

## 1.19.0

- Arrange the performance overlay yourself, on the Benchmark page: put its readings in any order, show them in a column or on one line, change its size, slide it away from the corner, and choose the highlight, text and background colours and how solid the background is. Reset the look puts it back as it came.
- The overlay's own look is gentler: the program's font and colours, soft light text and a mostly see-through background.

## 1.18.1

- The performance overlay can show more: video memory (VRAM) in use, memory (RAM) in gigabytes as well as percent, frame time, the graphics card's fan, network speed and a clock. Each has its own tick box on the Benchmark page.
- Home: the processor's temperature has its own line, like the graphics card's, and shows on the Processor card. It needs the PawnIO driver and the helper, or LibreHardwareMonitor running.

## 1.18.0

- Benchmark, a new page: a performance overlay that shows frames per second, processor, graphics card, memory and temperatures in a corner of the screen. PC Butler notices a game when it fills the screen and shows the overlay by itself; it can also stay on all the time. It is off until you switch it on.
- Frames per second are counted by the administrator helper, by listening to Windows' own "frame shown" events. Nothing is added to the game, so anti-cheat systems are not touched.
- Game sessions: for each game the overlay measured, the average and highest frame rate, the slowest 1%, and the hottest temperatures.
- Processor temperature for Intel and AMD: with the free PawnIO driver installed (the Benchmark page has the link), the helper reads the processor's own temperature for the overlay and the Home page.
- Settings has a Hotkeys tab: shortcuts that work in any program, to open PC Butler, sort now, start or stop Boost for the program in front, show or hide the overlay or the drop zone, and open Courier.
- Booster: the Advanced tab is now the last one.

## 1.17.0

- Booster has an Advanced tab: lasting changes that make the whole PC lighter, in four presets (Low, Medium, High, Extreme) or a choice of your own. They cover Game Mode, background game recording, tips and promoted apps, the power plan, transparency and animations, and (with administrator rights) telemetry, SysMain, search indexing and a few rarely used services.
- Undo all changes: what each setting was is written down before it is changed, and one button puts everything back.
- Settings: a switch that stops Windows updates until you switch it off again (administrator rights). Security fixes stop too, so use it with care.
- Settings: rename the PC (administrator rights and a restart).
- Internet: the PC's local and public IP address, each with a Copy button.
- Courier: every transfer shows the time elapsed and the time left, and can be paused and resumed from either PC.
- Courier IDs are digits only now: nine digits, like 418 205 937. Every PC gets a new ID, and both PCs need this version to find each other.

## 1.16.0

- Courier is quicker and simpler. The first time, choose "Share from this PC" or type the other PC's ID and press Connect; after that Courier starts with PC Butler and stays connected, with no clicks.
- Shorter IDs: six characters, like K7M-2QX, with a Copy button. They can be typed in any case, and look-alike letters (O, I, L) are read as digits.
- Connecting is much faster: about 10 seconds the first time instead of up to a minute, and a second or two to a PC connected before.
- The Courier widget: a small window that appears while PCs are connected. Drop files on it to send them (it asks which PC when there are several), answer a pairing right there, and watch each file go with a progress bar and its speed, sending or receiving.
- Files from a paired PC are taken without asking; this can be switched back to asking, per PC.
- IDs from earlier versions change to the new short ones: a client that was paired finds its host again by itself.

## 1.15.1

- One copy only: a double-click on the tray icon, or starting the program again, never opens a second window, and a copy started as administrator never runs beside another.

## 1.15.0

- Courier is now a host and its clients. One PC is the host; every other PC, on any network, connects to it by its ID and stays connected (it finds the host again by itself). Files go between the host and each client, and from one client to another through the host, which keeps no copy.
- Courier works across the internet only: nothing is opened to the local network any more.
- The host asks its router to forward a port (UPnP or NAT-PMP), so clients get straight through, even from mobile data.
- IPv6: the host is also reachable over IPv6, which on many connections needs no port at all.

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
