# What's new in PC Butler

This file is shown inside the program (Help & about, and once after each update).
Each version is a `## heading` followed by `- ` lines.

## 1.22.13

- Search remembers what you searched for: your recent searches (the last 30) show under the search box on the Search page, and stay after PC Butler restarts. Click one to search again, right-click it to take it out, or press Clear. A search goes in when you open a result, press Enter, or leave it unchanged for a moment.

## 1.22.12

- Clipboard sharing between the phone and the PC, for real now: with Courier connected, text copied on the PC lands on the phone's clipboard (and in its Clipboard page), ready to paste. On the phone, "Send what's copied to PC" (or the Send to PC button on a saved text) puts the text straight on the PC's clipboard, ready for Ctrl+V, instead of sending a text file. What was copied on the phone also goes to the PC by itself when you come back to PC Butler. A "Share the clipboard" switch on the Courier page (and "Share with my PCs" on the phone) turns it off; text that password managers mark as private is never sent.
- The PC's Courier page has a Send clipboard button for the selected PC or phone.
- On the phone, text shared to PC Butler from another app (Share > PC Butler) goes onto the PC's clipboard; before, it was dropped.
- Fixed on the phone: after opening a tool, its tab could keep showing that tool (the Courier tab showed Search); switching tabs now shows the tab's own page.
- Fixed on the phone: Duplicates kept saying "Comparing … file(s)…" under the results after the search had finished.

## 1.22.11

- Search: when broken entries are taken out of the saved index, the drives are indexed again in the background, so the files that were under them can be found again.

## 1.22.10

- Fixed: PC Butler could freeze for good on the Search page, or when going from it to Search settings. A broken entry in the saved index (a folder that led back to itself) made the list work out a path millions of folders long. Such entries are now taken out when the index loads, and a path never goes deeper than Windows allows.

## 1.22.9

- Fixed: on the phone, the Cleaner could freeze PC Butler ("not responding") after Scan files. The scan now runs in the background and shows how many folders it has looked at, and long lists show 40 files at a time with "Show more" (Search too).

## 1.22.8

- Clipboard: a locked item's password can now be as short as one character (it needed at least 4).

## 1.22.7

- Clipboard: a Lock button next to Pin on the Clipboard page. It locks the chosen item with a password (hidden and kept encrypted), unlocks a locked one, or locks an unlocked one again. This was only in the right-click menu before.

## 1.22.6

- Fixed: the Search page could freeze the window ("Not Responding") with millions of files indexed. The list waited for the index while it was being saved, sorted or updated; now it never waits (a line fills in a moment later), the index is copied quickly and written to disk without holding up searches, and a search with millions of results is not run again every second while files change.

## 1.22.5

- Fixed: clicking the tray icon while its panel was closing could stop PC Butler with "Cannot set Visibility to Visible or call Show, ShowDialog, Close... while a Window is closing". The panel now ignores a second close while it is already closing.

## 1.22.4

- PC Butler on the phone has a new look, from a design made in Lovable: darker colors with a mint accent, rounded cards and pill buttons, new icons, and a "PC Butler" bar on every page with its own header and buttons.
- Courier on the phone: the phone's ID sits in one big card with a copy button; a PC's ID is typed in one box with a paste button next to Connect; the settings (received files, how new PCs get in, stop) open in a sheet from the gear button; a PC's actions open in a sheet.
- Sorter on the phone: the count and Sort now on top, folders as chips, and a preview grouped by type that opens and closes.
- Duplicates on the phone: pick how files match with chips; in each group tap the copy to keep and the others are marked Delete; a bar at the bottom shows the space freed and deletes the extras.
- Storage on the phone: the free space shown big, a Scan button in the header, folders with bars, and a delete button on each large old file.
- Vault on the phone: new create and unlock screens with a password eye, three tiles to add files, add a folder or make one, a ⋮ sheet for each file, a bar at the bottom while selecting, and lock settings in a sheet.
- New on the phone: a Tools button on top of every page opens all of PC Butler's tools. Search finds files by name or type in your folders. Cleaner finds installers, files of 100 MB or more and old downloads to remove, and clears PC Butler's own cache. Apps lists your apps with their size, to open, see in Android's app info or uninstall. Benchmark measures the processor and storage for real. Clipboard keeps text to copy again or send to a PC through Courier. History shows what PC Butler did.

## 1.22.3

- Fixed: PC Butler could close with "A new guard page for the stack cannot be created". Search worked out a file's full path one folder level at a time in a way that ran out of room on very deep folders; it now walks the folders in a loop, and a folder is never moved inside itself in the index.

## 1.22.2

- Clipboard page: a new "Replace Windows' clipboard history" switch. On, Win+V opens PC Butler's list instead of Windows' own; off, Win+V goes back to Windows and PC Butler's list opens with Ctrl+Alt+V.
- When a shortcut is taken, the message now says by whom when it can: Win+V is named as Windows' clipboard history. For other programs Windows does not say which one, and the message says so.
- Fixed: recording a shortcut with the Win key (such as Win+F12) on the Hotkeys tab or the Clipboard page now keeps the Win key.

## 1.22.1

- Fixed: the overlay could say "helper is off" while the helper was running, and frames per second stayed empty.
- The tray panel shows only the background features now, each tile with its full name. Sort now, Pause and Report are gone from it: sorting is on the Sorter page, Pause in the window's status bar.
- Sorter: a Review moved files button opens the list of what automatic sorting moved, to approve it or undo it.
- Courier IDs are six digits now, like 205 937: the last six of the nine digits shown before, so the same PC keeps the same ID. A nine-digit ID typed or saved earlier still works.
- On the PC, the ID box takes digits only, typed or pasted (Arabic digits too), with Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A and Backspace as usual. On the phone, the ID is typed on a keypad of the app's own, with Paste; a keyboard plugged in or the PC's keyboard in the emulator types into it too (digits, Backspace, Enter, Esc, Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A).
- The Vault is a file manager now, on the PC and on the phone: it takes whole folders with what is in them, and keeps folders of its own that you open, make, rename, move and delete, with files moved between them. On the PC, files and folders can be dragged onto a folder, or dropped in from File Explorer, and the search finds files in every folder. A folder taken out comes back as a normal folder with the same shape.
- Vault: choose how long it stays open without use before it locks (1 to 60 minutes, or never), and whether it locks when PC Butler is closed (on the phone, when the app leaves the screen).
- PC Butler on the phone: Add files… in the Vault works again (the vault no longer locks while Android's file picker is open), and a file opened from the vault is no longer removed while it is being shown.
- PC Butler on the phone: in Arabic the app reads from right to left; Duplicates and Storage share one list of folders; the Recycle Bin says when the space is freed and empties itself after 30 days; Sorter's Undo also removes the folders it left empty; putting a big file back no longer freezes the screen; buttons with nothing to act on are hidden.
- Courier: when a PC wants to connect, or to send files, a pop-up asks right away, on the PC and on the phone, with the code to compare and Allow or Refuse. Closing it leaves the question on the Courier page, where it can still be answered.

## 1.22.0

- New Search page: every file and folder on your drives, found as you type. Space means and, | means or, ! means not, < > groups, quotes keep spaces, * and ? are wildcards, a \ searches the path, and path:, file:, folder:, ext:jpg;png, case: and diacritics: work as in other file search tools.
- Arabic names are found without their tashkeel, and أ إ آ ا, ى ي and ة ه count as the same letter (both can be switched off).
- Win + F opens a search box over whatever program is in front: Enter opens, Ctrl+Enter opens the folder. The shortcut can be changed on the Hotkeys tab.
- Results: sort by name, path, size or date modified; open, open the folder, copy the path or name, rename, or move files to the Recycle Bin (History can bring them back).
- Settings > Search: which drives and folders are indexed, what is left out, and how much memory the index uses. The index is saved, so search is ready at once when PC Butler starts, and changes are followed while it runs.
- Courier is one page and one way of working now: no more choosing between sharing and connecting. Every PC and phone has its own ID, always the same (it comes from the device's own key, made once and kept), and can be reached by it while Courier runs. Type another PC's ID to connect to it.
- The list of your PCs is always there, also while Courier is off: every PC you connected to, and every PC that connected to you. Connect, disconnect, rename or remove each one there. A connected PC stays connected, after a restart too, until you disconnect it; you can be connected to several at once, and files go straight between two PCs.
- Disconnect ends the connection on both PCs at once, and neither connects back by itself. Remove kicks a PC out: it is disconnected and no longer trusted, and has to ask, and be allowed, again. A PC or phone that stops Courier shows as disconnected on the others at once, not a minute later.
- Files that arrive from another PC show a Windows notification, which stays in the notification centre so nothing that came while you were away is missed; a click on it shows the files. On the phone they get a notification of their own.
- Courier: every file sent or received shows a notification in the corner of the screen with its progress bar, the time it has taken and the time left, and Pause (then Resume) and Cancel. It shows while the Courier widget is hidden or switched off, and says how the transfer ended for a few seconds.
- PC Butler on the phone: Courier's notification shows the file on its way with its progress bar, time taken and time left, and has Pause or Resume, Cancel and Disconnect buttons. The Courier tab has a Pause button and the times under each transfer too. A pause on one side holds the transfer on both.
- Each PC chooses what happens when a new PC connects to it: it asks you to allow it, or it lets in the PCs that give the password you set. The phone asks too, with Allow and Refuse.
- Sorter: a category can delete its files instead of moving them (Settings, edit the category, "Delete these files instead of moving them"). They go to the Recycle Bin once they are old enough, the list says so before anything happens, and History can bring them back.
- Clipboard: lock an item with a password of its own (right-click it). A locked item shows nothing of what it holds and is kept encrypted; pasting it asks for the password. Unlock it to paste it again and again without the password, and lock it again when you are done.
- Benchmark: PC Butler records what each app uses of the processor, the graphics card, the memory and the disk, and its internet use while the helper runs, per day for 90 days. The report shows today, yesterday, or the last 7, 30 or 90 days, and can be saved as a spreadsheet.
- Programs: AndroidDC installs, and updates, with Install: PC Butler downloads it from its own GitHub repository and sets it up, without opening its page.
- Programs, Updates: an update is matched to the program of that very name, so its menu's Run, Open file location and Uninstall no longer pick another program whose name starts the same.
- File Explorer's PC Butler menu comes in groups with a line between them: sorting and sharing, then the Vault, then Shred on its own.
- The tray icon's panel has a tile for everything PC Butler runs in the background (automatic sorting, clipboard history, the performance overlay, Courier, Boost, the drop zone, usage alerts, starting with Windows), lit when it is on. A click switches it on or off right there instead of opening the program, and the same switch in the window follows.
- Install this version on every PC and phone that use Courier together.

## 1.21.6

- PC Butler no longer writes to the disk every second. The helper's temperatures and frame counts reach the program through memory instead of the sensors.json and fps.json files, and settings are only written when they change.
- The log (pcbutler.log) stays under a megabyte: the older part moves to pcbutler.old.log. Deleting files adds one line, not one per file, and Courier no longer notes every repeated knock and renewal.

## 1.21.5

- Clipboard: Win+V now opens PC Butler's clipboard history instead of Windows' own, and it is the default shortcut (if you kept the old Ctrl+Alt+V, it moves to Win+V). Any other shortcut can still be set on the Clipboard page or in Settings > Hotkeys.
- Share with Courier from File Explorer: right-click files or folders, choose PC Butler > Share with Courier, and they wait on the Courier page, ready to send (sent at once when one PC is connected).

## 1.21.4

- Settings > Hotkeys: the clipboard history's shortcut can be changed there too, like the others.

## 1.21.3

- Fixed: switching the overlay off could, rarely, close PC Butler.

## 1.21.2

- Frames per second now work without the helper on accounts Windows allows to count frames (members of its "Performance Log Users" group): PC Butler counts them itself, with no permission prompt. Other accounts still use the helper.
- Benchmark: a switch that starts the helper by itself when a game comes up, so frames per second are counted without visiting the page first. Windows asks for your permission once each time PC Butler runs.
- The overlay says why there is no frame rate: "helper is off" when nothing is counting, instead of a dash.
- A game session is no longer cut short by a look at another window: it ends when the game is closed.

## 1.21.1

- Courier has its own encrypted connection code now, the same on Windows and in the PC Butler phone app, so a PC and a phone work together: install this version on every PC that talks to a phone.
- Files shared to PC Butler from another app on the phone are sent by themselves, to the PC that is connected (or as soon as one is).

## 1.21.0

- Right-click menus across PC Butler look better: an icon on each line, a tick on what is on, and submenus that open to the side.
- Clipboard tags work from the right-click menu: tick a tag in its submenu, or type a new one at the foot of the list (several at once, separated by commas).
- Clipboard: deleting an item that is pinned or tagged asks first.

## 1.20.1

- The tray icon opens its new panel with a right-click too; the old menu is gone. A double-click still opens the window.

## 1.20.0

- The tray icon opens a panel, like Windows' quick settings: what the sorter is doing, the processor and memory, tiles for Sort now, Pause, the report, Clipboard, the drop zone and Courier (lit when on), the update when there is one, and Open and Exit. A double-click opens the window; a right-click keeps a short menu.
- The window opens much faster: each page is built the first time it is opened, slow checks (the firewall, Windows Sandbox) run in the background, and closing the window only hides it, so opening it again from the tray is instant. The Vault still locks when the window is closed.
- The program itself starts faster: its code is compiled when it is built, not on your PC each time.

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
