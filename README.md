# PC Butler

**English** | [العربية](#العربية)

A small Windows program that keeps your PC tidy. It moves files that have been sitting in your Downloads folder for a while into category folders (Documents, Pictures, Videos…), either automatically in the background or only when you say so, and every change is logged and can be undone. It also finds duplicate files, cleans junk files, shows what fills your drives, trims startup apps, uninstalls and updates programs, measures your internet speed and keeps private files in an encrypted vault.

PC Butler is the C# rewrite of [Downloads Sorter](https://github.com/hmodymac8-droid/downloads-sorter), with the same look and everything it could do, plus the pages added since. On first start it brings along Downloads Sorter's settings, history and vault (it copies them; the old program keeps working).

![PC Butler: the Home page](docs/screenshot.png)

![PC Butler: the Sorter page](docs/screenshot-sorter.png)

## Features

- **Home.** The PC at a glance, on ring gauges: processor, memory and graphics-card use, the graphics card's temperature, free space on every drive, and a neatness score out of 100 with the reasons it lost points. The tray icon shows processor and memory use and warns when they stay nearly full.
- **Sorts by age and type.** Files older than a set number of days (7 by default) move into category folders by file extension. Each category can have its own age.
- **Preview first.** See exactly which files will move and where before anything happens, and exclude the ones you want to keep.
- **Automatic mode.** Runs in the background with a tray icon, checks on a schedule (every so many minutes, daily or weekly at a time you set), and can start with Windows (a separate switch). Cleaning can be scheduled the same way. After each run a report window lets you approve or undo what moved.
- **Full history with undo.** Every move and deletion is logged. Put back any files you choose; deleted files are restored from the Recycle Bin. The log can be cleared with one button; no file is touched.
- **Duplicate finder.** Looks in the sorted folders or any folder you pick and finds copies by identical content, exact name, name and size, size, or a *similar name* (messy song and video titles are matched as the same file). Tick files one by one or with quick picks, then choose whether they go to the Recycle Bin or are deleted for good.
- **Cleaner.** Finds temporary files, browser caches, crash reports and other junk, and can clear traces such as the recent-files list. Nothing is deleted until you click Clean, and everything cleaned is listed in History.
- **Storage.** A map of what fills a folder or a drive, and a list of large files that have not been touched for a long time.
- **Booster.** Turns startup apps off and on (they stay installed), ends programs that use a lot of memory, and switches the power plan. Boost gives one program the PC's attention (high priority for it, lower for the background, high-performance power plan) and puts everything back when switched off; it can switch on by itself while chosen programs run. Optionally it also trims the memory of background programs and pauses three Windows services (see *Administrator rights* below). The Advanced tab makes lasting changes for the whole PC, in four presets from Low to Extreme or a choice of your own: Game Mode, no background game recording, no tips or promoted apps, the fastest power plan, no transparency or animations and, with administrator rights, telemetry, SysMain, search indexing and a few rarely used services switched off. What each setting was is written down first, and *Undo all changes* puts everything back.
- **Benchmark.** A performance overlay for games: frames per second, processor, graphics card, memory and temperatures in a corner of the screen. PC Butler notices a game when it fills the screen and shows the overlay by itself, or it can stay on all the time; it is off until you switch it on, and clicks pass through it. It is an ordinary window on top, so it shows over windowed and borderless games, not over exclusive full screen. Frames per second are counted by the administrator helper from Windows' own events, with nothing added to the game. Each game session is kept with its average and highest frame rate, the slowest 1% and the hottest temperatures.
- **Programs.** Every installed program in one list, with the drive it is on and a filter by drive; double-click one to open it. Uninstall runs the program's own uninstaller and then shows what it left behind (folders, shortcuts, its own registry key) for you to tick: folders go to the Recycle Bin, registry keys are saved to a `.reg` file first. Updates come from winget, Windows' package manager.
- **Internet.** A speed test: download, upload and response time, measured against Cloudflare's speed-test servers, with a history of earlier results. The page also shows the PC's local and public IP address, each with a Copy button.
- **Vault.** Password-protected storage: files you add are encrypted (AES-256) and removed from where they were.
- **Auto clean-up.** Optionally recycle old files in a category (for example installers after 90 days). Only files the program itself moved are touched.
- **Never-move list.** Protect files by name or pattern (`*.torrent`). Files you put back with Undo are not moved again automatically.
- **Several folders.** Sort Desktop or any other folder with the same rules.
- **Courier.** Send files and whole folders between PCs running PC Butler on different networks, across the internet, with no server in between. One PC shares (the host) and gets an ID of nine digits, like 418 205 937; every other PC connects to it by that ID, starts Courier with PC Butler and stays connected. Files go between the host and each client, and from one client to another through the host, which keeps no copy. The host asks its router to forward a port (UPnP or NAT-PMP) and is reachable over IPv6 too; without either, clients open a path through both routers, which works on most home connections but not always from mobile data, and Courier says so. Each client is paired with the host once by comparing a six-digit code on both screens; after that everything sent is encrypted and each PC recognises the other by its certificate. Nothing is opened to the local network. While PCs are connected, a small widget stays on top: drop files on it to send them, and watch each one go with a progress bar. Transfers show progress, speed, the time elapsed and the time left, can be paused, resumed and cancelled on either PC, and carry on where they stopped if the connection drops.
- **Shredder.** Deep delete: files are overwritten with random data and then deleted, so recovery tools find nothing. It cannot be undone. (On an SSD the drive may keep old copies in cells it has not reused yet, out of reach of ordinary recovery tools.)
- **Drop zone.** A small tile that stays on top of other windows: drag files or folders onto it to sort them, lock them in the Vault, shred them or recycle them. It can be moved, and hidden from its menu, the tray menu or Settings.
- **File Explorer menu.** Right-click one or more files or folders and open the *PC Butler* submenu: sort them into their category folders (a folder has its loose files sorted), lock files in the Vault, or shred them. On Windows 11 the entry is under *Show more options*; a switch in Settings brings back the classic full right-click menu, where it is one click away.
- **What's new.** After each update a window lists what changed; the full list is in [CHANGELOG.md](CHANGELOG.md).
- **Hotkeys.** Shortcuts that work in any program, set on a tab of the Settings page: open PC Butler, sort now, start or stop Boost for the program in front, show or hide the overlay or the drop zone, open Courier.
- **English and Arabic.** The interface follows Windows' language, or you pick one in Settings.
- **Clipboard manager.** Keeps what you copy (text, files, pictures; 500 items by default), with search and pinning. A shortcut opens the list in any program and pastes the item you pick. Off until you turn it on.
- **Updates.** Checks this repository's releases, notifies you when a new version is out, and updates with one click (or automatically, if you turn that on).
- **Safe by design.** Unfinished downloads and shortcuts are skipped, nothing is ever overwritten, and deletions go to the Recycle Bin unless you choose to delete for good.

## Administrator rights

PC Butler never runs as administrator. The graphics card's temperature needs no special rights. A processor's own temperature, Intel or AMD, can only be read through a kernel driver. PC Butler does not install one: if you install the free, signed [PawnIO](https://pawnio.eu) driver yourself, the helper reads the processor's temperature through it; and if LibreHardwareMonitor is running, PC Butler shows its readings. A few things need administrator rights, and all are optional: reading the motherboard's temperature and fan sensors (Home page), pausing services while Boost is on, the Booster's Advanced changes to services and machine-wide settings, stopping Windows updates and renaming the PC (both in Settings), counting frames per second and reading the processor's temperature (Benchmark), and the one-time firewall rule that lets clients reach a Courier host (for PC Butler's own file, UDP only). For those, PC Butler starts a small helper, a second copy of itself, only when you ask and only after Windows' permission prompt. The helper can read the sensors, pause or resume three named services (Print Spooler, Connected User Experiences and Telemetry, SysMain), apply and put back the changes on the Advanced tab's fixed list, hold Windows updates back, rename the PC, add that firewall rule, count the frames programs show and read the processor's temperature, nothing else. It resumes paused services and ends when PC Butler closes.

Stopping Windows updates also stops security fixes, so the PC is less protected the longer the switch stays on.

## Install

Download `PCButlerSetup.exe` from the [Releases](../../releases) page and run it. It installs for your user account only and needs no administrator rights. The installer is not code-signed, so Windows SmartScreen may warn you: click **More info**, then **Run anyway**.

## Build

Requires Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build src/PCButler
dotnet run --project tests/PCButler.Tests
```

The first builds the program (`src/PCButler/bin/Debug/net10.0-windows/PCButler.exe`), the second runs the engine's tests.

## Build the installer

Also requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```
./build.ps1
```

This publishes the program with the .NET runtime included (nothing else is needed on the user's PC) and produces `PCButlerSetup.exe`, a standard Windows setup wizard in English and Arabic, next to the script.

## How it works

| Folder or file | Purpose |
|---|---|
| `src/PCButler.Core` | The engine: settings, rules, moving, history, undo, Recycle Bin, duplicates, cleaner, storage, booster and Boost, the administrator helper, installed programs, speed test, vault, updates and the background loop |
| `src/PCButler` | The windows (WPF): control panel, tray icon, report window, launch screen |
| `tests/PCButler.Tests` | Tests of the engine |
| `installer/installer.iss` | The setup wizard (Inno Setup script) |
| `build.ps1` | Publishes the program and compiles the setup wizard |

Settings, the history log, the activity log and registry backups are stored in `%APPDATA%\PCButler`.

The speed test and the public-IP line talk to `speed.cloudflare.com` and update checks talk to GitHub and winget's source. Courier talks only to the other PCs, and only while it is switched on. It also uses the public directory of the BitTorrent network (its DHT) to look IDs up: the computers in that directory see this PC's internet address, as they do for anyone using a torrent program, and no file or file name is ever sent to them. A host also asks its own router to forward one port to it. Nothing else leaves the PC.

## Support the project

PC Butler is free. If it is useful to you and you would like to support its development, you can donate through PayPal: [paypal.me/mfsafadi](https://paypal.me/mfsafadi). The same link is behind **Donate** in the program's sidebar.

## License

Copyright (c) 2026 hmodymac8-droid.

PC Butler is proprietary software, not open source. It is free to download and use from the [Releases](../../releases) page, for personal or commercial purposes. You may not copy, redistribute, modify or reuse the program or its source code without the owner's written permission; the source in this repository is shown for reference only, and contributions are not accepted. The full terms are in [LICENSE](LICENSE).

Versions 1.0.0 to 1.12.1 were released under the GNU General Public License, version 3, which still applies to those versions as released. Version 1.12.2 and later are under the licence above.

PC Butler includes two libraries under their own licences: [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (Mozilla Public License 2.0), used unchanged to read the processor's temperature, and [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (MIT), used to count frames.

The bundled fonts in `src/PCButler/Fonts`, [DM Sans](https://github.com/googlefonts/dm-fonts) and [Manrope](https://github.com/googlefonts/manrope), are under the SIL Open Font License; their licence texts are next to them.

---

<div dir="rtl">

## العربية

[English](#pc-butler) | **العربية**

برنامج صغير لنظام ويندوز يحافظ على ترتيب جهازك. ينقل الملفات التي بقيت في مجلد التنزيلات لفترة إلى مجلدات مصنّفة (مستندات، صور، فيديو…)، إما تلقائيًا في الخلفية أو فقط عندما تطلب ذلك، وكل تغيير يُسجَّل ويمكن التراجع عنه. كما يعثر على الملفات المكررة، وينظّف الملفات غير الضرورية، ويعرض ما يملأ أقراصك، ويخفّف برامج بدء التشغيل، ويلغي تثبيت البرامج ويحدّثها، ويقيس سرعة الإنترنت، ويحفظ الملفات الخاصة في خزنة مشفّرة.

PC Butler هو إعادة كتابة لبرنامج [Downloads Sorter](https://github.com/hmodymac8-droid/downloads-sorter) بلغة C#، بالشكل نفسه وبكل ما كان يفعله، مع الصفحات التي أُضيفت بعده. عند أول تشغيل ينسخ إعدادات Downloads Sorter وسجله وخزنته (نسخًا، فيبقى البرنامج القديم يعمل).

### المميزات

- **الرئيسية.** جهازك في لمحة، على عدّادات دائرية: استهلاك المعالج والذاكرة وكرت الشاشة، وحرارة كرت الشاشة، والمساحة الحرة في كل قرص، ونقاط ترتيب من 100 مع أسباب الخصم. أيقونة البرنامج بجانب الساعة تعرض استهلاك المعالج والذاكرة وتنبّه عند امتلائهما المستمر.
- **الفرز حسب العمر والنوع.** الملفات الأقدم من عدد محدد من الأيام (7 افتراضيًا) تُنقل إلى مجلدات التصنيف حسب امتداد الملف، ويمكن تحديد عمر مختلف لكل تصنيف.
- **المعاينة أولًا.** شاهد الملفات التي ستُنقل ووجهتها قبل أي تنفيذ، واستثنِ ما تريد إبقاءه.
- **الوضع التلقائي.** يعمل في الخلفية مع أيقونة بجانب الساعة، ويفحص المجلد وفق جدول زمني (كل عدد من الدقائق، أو يوميًا أو أسبوعيًا في وقت تحدده)، ويبدأ مع تسجيل الدخول. ويمكن جدولة التنظيف بالطريقة نفسها. بعد كل عملية تظهر نافذة تقرير للموافقة على ما نُقل أو التراجع عنه.
- **سجل كامل مع إمكانية التراجع.** كل عملية نقل أو حذف تُسجَّل. يمكنك إعادة أي ملفات تختارها، والملفات المحذوفة تُستعاد من سلة المحذوفات. ويمكن مسح السجل بزر واحد دون المساس بأي ملف.
- **كاشف الملفات المكررة.** يبحث في المجلدات المرتّبة أو في أي مجلد تختاره، ويعثر على النسخ المكررة حسب المحتوى المتطابق، أو الاسم نفسه، أو الاسم والحجم، أو الحجم، أو *الاسم المتشابه* (عناوين الأغاني والفيديو الفوضوية تُعتبر الملف نفسه). حدّد الملفات واحدًا واحدًا أو باختيارات سريعة، ثم اختر: إلى سلة المحذوفات أو حذف نهائي.
- **المنظّف.** يعثر على الملفات المؤقتة وذاكرة المتصفحات المؤقتة وتقارير الأعطال وغيرها، ويمسح آثارًا مثل قائمة الملفات الأخيرة. لا يُحذف شيء حتى تضغط «تنظيف»، وكل ما يُنظَّف يظهر في السجل.
- **التخزين.** خريطة لما يملأ مجلدًا أو قرصًا، وقائمة بالملفات الكبيرة التي لم تُستخدم منذ مدة طويلة.
- **المسرّع.** يوقف برامج بدء التشغيل أو يشغّلها (تبقى مثبّتة)، وينهي البرامج التي تستهلك الكثير من الذاكرة، ويبدّل خطة الطاقة. «التعزيز» يعطي برنامجًا واحدًا تركيز الجهاز (أولوية عالية له، وأقل لبرامج الخلفية، وخطة طاقة الأداء العالي) ويعيد كل شيء عند إيقافه، ويمكنه أن يشتغل تلقائيًا ما دامت برامج تختارها تعمل. واختياريًا يفرّغ ذاكرة برامج الخلفية ويوقف ثلاث خدمات من ويندوز مؤقتًا (انظر «صلاحيات المسؤول» أدناه). وتبويب «متقدّم» يجري تغييرات دائمة للجهاز كله، في أربعة مستويات من المنخفض إلى الأقصى أو باختيارك: وضع الألعاب، وإيقاف تسجيل الألعاب في الخلفية، وإيقاف النصائح والتطبيقات المروَّجة، وأسرع خطة طاقة، وإيقاف الشفافية والحركات، وبصلاحيات المسؤول إيقاف خدمات بيانات الاستخدام و SysMain وفهرسة البحث وبعض الخدمات نادرة الاستخدام. تُسجَّل قيمة كل إعداد أولًا، وزر «التراجع عن كل التغييرات» يعيد كل شيء.
- **قياس الأداء.** لوحة أداء للألعاب: الإطارات في الثانية والمعالج وكرت الشاشة والذاكرة ودرجات الحرارة في زاوية الشاشة. يلاحظ PC Butler اللعبة عندما تملأ الشاشة فيُظهر اللوحة من تلقاء نفسه، أو تبقى ظاهرة دائمًا؛ وهي متوقفة حتى تفعّلها، والنقرات تمرّ من خلالها. هي نافذة عادية فوق النوافذ، فتظهر فوق الألعاب التي تعمل في نافذة أو بلا إطار، لا فوق وضع ملء الشاشة الحصري. الإطارات يعدّها مساعد المسؤول من أحداث ويندوز نفسه دون إضافة شيء إلى اللعبة. وتُحفظ كل جلسة لعب بمتوسط الإطارات وأعلاها وأبطأ 1% وأعلى درجات الحرارة.
- **البرامج.** كل البرامج المثبّتة في قائمة واحدة، مع القرص الذي عليه كل برنامج وفلتر حسب القرص، والنقر المزدوج يفتح البرنامج. إلغاء التثبيت يشغّل برنامج الإزالة الخاص بالبرنامج ثم يعرض ما تركه خلفه (مجلدات، اختصارات، مفتاحه في السجل) لتختار منه: المجلدات تذهب إلى سلة المحذوفات، ومفاتيح السجل تُحفظ أولًا في ملف `.reg`. التحديثات تأتي من winget، مدير الحزم الخاص بويندوز.
- **الإنترنت.** قياس السرعة: التنزيل والرفع وزمن الاستجابة، مقابل خوادم Cloudflare لقياس السرعة، مع سجل للنتائج السابقة. وتعرض الصفحة عنوان IP المحلي والعام للحاسوب، ولكل منهما زر «نسخ».
- **الخزنة.** مساحة محمية بكلمة مرور: الملفات التي تضيفها تُشفَّر (AES-256) وتُزال من مكانها الأصلي.
- **التنظيف التلقائي.** خيار لإرسال الملفات القديمة في تصنيف معيّن إلى سلة المحذوفات (مثل ملفات التثبيت بعد 90 يومًا). لا يمسّ إلا الملفات التي نقلها البرنامج نفسه.
- **قائمة «لا تنقل أبدًا».** احمِ ملفات معيّنة بالاسم أو بالنمط (مثل `*.torrent`). الملفات التي أعدتها بالتراجع لا تُنقل تلقائيًا مرة أخرى.
- **عدة مجلدات.** افرز سطح المكتب أو أي مجلد آخر بالقواعد نفسها.
- **الساعي (Courier).** إرسال الملفات والمجلدات كاملة بين حواسيب تعمل بـ PC Butler على شبكات مختلفة، عبر الإنترنت، دون خادم في الوسط. حاسوب واحد يشارك (المضيف) وله معرّف من تسعة أرقام مثل 418 205 937؛ وكل حاسوب آخر يتصل به بهذا المعرّف، ويعمل الساعي مع PC Butler ويبقى متصلًا. تنتقل الملفات بين المضيف وكل عميل، ومن عميل إلى عميل عبر المضيف الذي لا يحتفظ بنسخة. يطلب المضيف من الراوتر توجيه منفذ إليه (UPnP أو NAT-PMP)، ويمكن الوصول إليه عبر IPv6 أيضًا؛ ودون ذلك يفتح العملاء مسارًا عبر الراوترين، وهذا ينجح في أغلب اتصالات المنازل لكن ليس دائمًا من بيانات الجوال، ويخبرك الساعي بذلك. يُقرن كل عميل بالمضيف مرة واحدة بمقارنة رمز من ستة أرقام على الشاشتين؛ بعدها كل ما يُرسل مشفّر، ويعرف كل حاسوب الآخر بشهادته. لا يُفتح شيء للشبكة المحلية. وأثناء اتصال الحواسيب تبقى نافذة صغيرة فوق النوافذ: أفلت الملفات عليها لإرسالها، وتابع كل ملف بشريط تقدّم. عمليات النقل تعرض التقدّم والسرعة والوقت المنقضي والمتبقي، ويمكن إيقافها مؤقتًا ومتابعتها وإلغاؤها من أي حاسوب، وتكمل من حيث توقفت إذا انقطع الاتصال.
- **الحذف العميق (Shredder).** تُكتب فوق الملفات بيانات عشوائية ثم تُحذف، فلا تجد أدوات الاستعادة شيئًا، ولا يمكن التراجع عنه. (في أقراص SSD قد يحتفظ القرص بنسخ قديمة في خلايا لم يُعد استخدامها بعد، وهي بعيدة عن متناول أدوات الاستعادة العادية.)
- **منطقة الإفلات.** مربع صغير يبقى فوق النوافذ الأخرى: اسحب إليه ملفات أو مجلدات لفرزها أو قفلها في الخزنة أو حذفها العميق أو إرسالها لسلة المحذوفات. يمكن تحريكه، وإخفاؤه من قائمته أو من قائمة الأيقونة بجانب الساعة أو من الإعدادات.
- **قائمة File Explorer.** انقر بالزر الأيمن على ملف أو مجلد واختر «معالجة بـ PC Butler» لفرزه، أو فرز الملفات السائبة في المجلد، أو قفل الملف في الخزنة، أو حذفه العميق، أو إرساله إلى سلة المحذوفات. في ويندوز 11 تجد الخيار تحت *Show more options*، ومفتاح في الإعدادات يعيد قائمة الزر الأيمن الكلاسيكية الكاملة حيث يظهر الخيار مباشرة.
- **ما الجديد.** بعد كل تحديث تظهر نافذة بما تغيّر، والقائمة الكاملة في [CHANGELOG.ar.md](CHANGELOG.ar.md).
- **الاختصارات.** اختصارات تعمل في أي برنامج، تُضبط في تبويب من صفحة الإعدادات: فتح PC Butler، والفرز الآن، وتشغيل التعزيز أو إيقافه للبرنامج الذي في الواجهة، وإظهار اللوحة أو منطقة الإفلات أو إخفاؤهما، وفتح الساعي.
- **العربية والإنجليزية.** الواجهة تتبع لغة ويندوز، أو تختار اللغة من الإعدادات.
- **التحديثات.** يتحقق من إصدارات هذا المستودع، وينبّهك عند صدور نسخة جديدة، ويحدّث نفسه بضغطة واحدة (أو تلقائيًا إذا فعّلت ذلك).
- **آمن في تصميمه.** يتجاوز التنزيلات غير المكتملة والاختصارات، ولا يستبدل أي ملف موجود، والحذف يكون إلى سلة المحذوفات إلا إذا اخترت الحذف النهائي.

### صلاحيات المسؤول

PC Butler لا يعمل أبدًا بصلاحيات المسؤول. حرارة كرت الشاشة لا تحتاج أي صلاحيات. أما حرارة المعالج نفسه، Intel أو AMD، فلا تُقرأ إلا عبر تعريف (driver) و PC Butler لا يثبّت تعريفات: إذا ثبّتَّ بنفسك تعريف [PawnIO](https://pawnio.eu) المجاني الموقَّع قرأ المساعد حرارة المعالج من خلاله؛ وإذا كان LibreHardwareMonitor يعمل يعرض PC Butler قراءاته. أشياء قليلة تحتاج صلاحيات المسؤول وكلها اختيارية: قراءة حساسات اللوحة الأم للحرارة والمراوح (الصفحة الرئيسية)، وإيقاف الخدمات مؤقتًا أثناء التعزيز، وتغييرات تبويب «متقدّم» في الخدمات وإعدادات الجهاز، وإيقاف تحديثات ويندوز وتغيير اسم الحاسوب (كلاهما في الإعدادات)، وعدّ الإطارات في الثانية وقراءة حرارة المعالج (قياس الأداء)، وقاعدة جدار الحماية للساعي. لها يشغّل PC Butler مساعدًا صغيرًا، نسخة ثانية منه، فقط عندما تطلب وبعد موافقتك على نافذة ويندوز. المساعد يقرأ الحساسات، ويوقف أو يشغّل ثلاث خدمات محددة (Print Spooler و Connected User Experiences and Telemetry و SysMain)، ويطبّق تغييرات القائمة الثابتة في تبويب «متقدّم» ويعيدها، ويوقف تحديثات ويندوز، ويغيّر اسم الحاسوب، ويضيف قاعدة جدار الحماية، ولا شيء غير ذلك. يعيد تشغيل الخدمات الموقوفة مؤقتًا وينتهي عند إغلاق PC Butler.

إيقاف تحديثات ويندوز يوقف الإصلاحات الأمنية أيضًا، فتقلّ حماية الجهاز كلما طال الإيقاف.

### التثبيت

نزّل الملف `PCButlerSetup.exe` من صفحة [الإصدارات](../../releases) وشغّله. يُثبَّت البرنامج لحساب المستخدم الحالي فقط ولا يحتاج إلى صلاحيات المسؤول. ملف التثبيت غير موقّع رقميًا، لذلك قد يعرض ويندوز تحذير SmartScreen: اضغط **More info** ثم **Run anyway**.

### البناء من الشيفرة المصدرية

يتطلب ويندوز 10 أو 11 و[.NET 10 SDK](https://dotnet.microsoft.com/download).

</div>

```
dotnet build src/PCButler
dotnet run --project tests/PCButler.Tests
```

<div dir="rtl">

### بناء ملف التثبيت

يتطلب أيضًا [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

</div>

```
./build.ps1
```

<div dir="rtl">

ينتج عن ذلك الملف `PCButlerSetup.exe`، وهو معالج تثبيت ويندوز قياسي بالعربية والإنجليزية، ويتضمن بيئة ‎.NET فلا يحتاج جهاز المستخدم إلى أي شيء آخر.

### أين تُحفظ البيانات

الإعدادات وسجل التغييرات وسجل النشاط تُحفظ في المجلد `%APPDATA%\PCButler`.

### ادعم المشروع

PC Butler مجاني. إذا أفادك وأحببت أن تدعم تطويره، يمكنك التبرّع عبر PayPal: [paypal.me/mfsafadi](https://paypal.me/mfsafadi). الرابط نفسه موجود خلف زر «تبرّع» في الشريط الجانبي للبرنامج.

### الترخيص

حقوق النشر (c) 2026 hmodymac8-droid.

PC Butler برنامج مملوك وليس مفتوح المصدر. تنزيله واستخدامه مجاني من صفحة [الإصدارات](../../releases)، للاستخدام الشخصي أو التجاري. لا يحق نسخ البرنامج أو إعادة توزيعه أو تعديله أو إعادة استخدام شيفرته المصدرية دون إذن كتابي من المالك؛ الشيفرة في هذا المستودع معروضة للاطلاع فقط، ولا تُقبل المساهمات. الشروط الكاملة في ملف [LICENSE](LICENSE).

الإصدارات من 1.0.0 إلى 1.12.1 نُشرت بموجب رخصة جنو العمومية، الإصدار 3، وهي ما زالت تسري على تلك الإصدارات كما نُشرت. الإصدار 1.12.2 وما بعده تحت الرخصة أعلاه.

</div>
