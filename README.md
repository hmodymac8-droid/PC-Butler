# PC Butler

**English** | [العربية](#العربية)

A small Windows program that keeps your PC tidy. It moves files that have been sitting in your Downloads folder for a while into category folders (Documents, Pictures, Videos…), either automatically in the background or only when you say so, and every change is logged and can be undone. It also finds duplicate files, cleans junk files, trims startup apps and keeps private files in an encrypted vault.

PC Butler is the C# rewrite of [Downloads Sorter](https://github.com/hmodymac8-droid/downloads-sorter), with the same features and look. On first start it brings along Downloads Sorter's settings, history and vault (it copies them; the old program keeps working).

![PC Butler](docs/screenshot.png)

## Features

- **Sorts by age and type.** Files older than a set number of days (7 by default) move into category folders by file extension. Each category can have its own age.
- **Preview first.** See exactly which files will move and where before anything happens, and exclude the ones you want to keep.
- **Automatic mode.** Runs in the background with a tray icon, checks on a schedule, and starts when you sign in. After each run a report window lets you approve or undo what moved.
- **Full history with undo.** Every move and deletion is logged. Put back any files you choose; deleted files are restored from the Recycle Bin.
- **Duplicate finder.** Looks in the sorted folders or any folder you pick and finds copies by identical content, exact name, name and size, size, or a *similar name* (messy song and video titles are matched as the same file). Tick files one by one or with quick picks, then choose whether they go to the Recycle Bin or are deleted for good.
- **Cleaner.** Finds temporary files, browser caches, crash reports and other junk. Nothing is deleted until you click Clean, and everything cleaned is listed in History.
- **Booster.** Turns startup apps off and on (they stay installed), ends programs that use a lot of memory, and switches the power plan.
- **Vault.** Password-protected storage: files you add are encrypted (AES-256) and removed from where they were.
- **Auto clean-up.** Optionally recycle old files in a category (for example installers after 90 days). Only files the program itself moved are touched.
- **Never-move list.** Protect files by name or pattern (`*.torrent`). Files you put back with Undo are not moved again automatically.
- **Several folders.** Sort Desktop or any other folder with the same rules.
- **English and Arabic.** The interface follows Windows' language, or you pick one in Settings.
- **Updates.** Checks this repository's releases, notifies you when a new version is out, and updates with one click (or automatically, if you turn that on).
- **Safe by design.** Unfinished downloads and shortcuts are skipped, nothing is ever overwritten, and deletions go to the Recycle Bin unless you choose to delete for good.

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
| `src/PCButler.Core` | The engine: settings, rules, moving, history, undo, Recycle Bin, duplicates, cleaner, booster, vault, updates and the background loop |
| `src/PCButler` | The windows (WPF): control panel, tray icon, report window, launch screen |
| `tests/PCButler.Tests` | Tests of the engine |
| `installer/installer.iss` | The setup wizard (Inno Setup script) |
| `build.ps1` | Publishes the program and compiles the setup wizard |

Settings, the history log and the activity log are stored in `%APPDATA%\PCButler`.

## License

Copyright (c) 2026 hmodymac8-droid.

Released under the [GNU General Public License, version 3](LICENSE). You are free to use, change and share it, including commercially, but any version you distribute must keep this copyright notice, say what you changed, and come with its full source code under the same license.

The bundled fonts in `src/PCButler/Fonts`, [DM Sans](https://github.com/googlefonts/dm-fonts) and [Manrope](https://github.com/googlefonts/manrope), are under the SIL Open Font License; their licence texts are next to them.

---

<div dir="rtl">

## العربية

[English](#pc-butler) | **العربية**

برنامج صغير لنظام ويندوز يحافظ على ترتيب جهازك. ينقل الملفات التي بقيت في مجلد التنزيلات لفترة إلى مجلدات مصنّفة (مستندات، صور، فيديو…)، إما تلقائيًا في الخلفية أو فقط عندما تطلب ذلك، وكل تغيير يُسجَّل ويمكن التراجع عنه. كما يعثر على الملفات المكررة، وينظّف الملفات غير الضرورية، ويخفّف برامج بدء التشغيل، ويحفظ الملفات الخاصة في خزنة مشفّرة.

PC Butler هو إعادة كتابة لبرنامج [Downloads Sorter](https://github.com/hmodymac8-droid/downloads-sorter) بلغة C#، بالمميزات والشكل نفسيهما. عند أول تشغيل ينسخ إعدادات Downloads Sorter وسجله وخزنته (نسخًا، فيبقى البرنامج القديم يعمل).

### المميزات

- **الفرز حسب العمر والنوع.** الملفات الأقدم من عدد محدد من الأيام (7 افتراضيًا) تُنقل إلى مجلدات التصنيف حسب امتداد الملف، ويمكن تحديد عمر مختلف لكل تصنيف.
- **المعاينة أولًا.** شاهد الملفات التي ستُنقل ووجهتها قبل أي تنفيذ، واستثنِ ما تريد إبقاءه.
- **الوضع التلقائي.** يعمل في الخلفية مع أيقونة بجانب الساعة، ويفحص المجلد وفق جدول زمني، ويبدأ مع تسجيل الدخول. بعد كل عملية تظهر نافذة تقرير للموافقة على ما نُقل أو التراجع عنه.
- **سجل كامل مع إمكانية التراجع.** كل عملية نقل أو حذف تُسجَّل. يمكنك إعادة أي ملفات تختارها، والملفات المحذوفة تُستعاد من سلة المحذوفات.
- **كاشف الملفات المكررة.** يبحث في المجلدات المرتّبة أو في أي مجلد تختاره، ويعثر على النسخ المكررة حسب المحتوى المتطابق، أو الاسم نفسه، أو الاسم والحجم، أو الحجم، أو *الاسم المتشابه* (عناوين الأغاني والفيديو الفوضوية تُعتبر الملف نفسه). حدّد الملفات واحدًا واحدًا أو باختيارات سريعة، ثم اختر: إلى سلة المحذوفات أو حذف نهائي.
- **المنظّف.** يعثر على الملفات المؤقتة وذاكرة المتصفحات المؤقتة وتقارير الأعطال وغيرها. لا يُحذف شيء حتى تضغط «تنظيف»، وكل ما يُنظَّف يظهر في السجل.
- **المسرّع.** يوقف برامج بدء التشغيل أو يشغّلها (تبقى مثبّتة)، وينهي البرامج التي تستهلك الكثير من الذاكرة، ويبدّل خطة الطاقة.
- **الخزنة.** مساحة محمية بكلمة مرور: الملفات التي تضيفها تُشفَّر (AES-256) وتُزال من مكانها الأصلي.
- **التنظيف التلقائي.** خيار لإرسال الملفات القديمة في تصنيف معيّن إلى سلة المحذوفات (مثل ملفات التثبيت بعد 90 يومًا). لا يمسّ إلا الملفات التي نقلها البرنامج نفسه.
- **قائمة «لا تنقل أبدًا».** احمِ ملفات معيّنة بالاسم أو بالنمط (مثل `*.torrent`). الملفات التي أعدتها بالتراجع لا تُنقل تلقائيًا مرة أخرى.
- **عدة مجلدات.** افرز سطح المكتب أو أي مجلد آخر بالقواعد نفسها.
- **العربية والإنجليزية.** الواجهة تتبع لغة ويندوز، أو تختار اللغة من الإعدادات.
- **التحديثات.** يتحقق من إصدارات هذا المستودع، وينبّهك عند صدور نسخة جديدة، ويحدّث نفسه بضغطة واحدة (أو تلقائيًا إذا فعّلت ذلك).
- **آمن في تصميمه.** يتجاوز التنزيلات غير المكتملة والاختصارات، ولا يستبدل أي ملف موجود، والحذف يكون إلى سلة المحذوفات إلا إذا اخترت الحذف النهائي.

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

### الترخيص

حقوق النشر (c) 2026 hmodymac8-droid.

البرنامج منشور بموجب [رخصة جنو العمومية، الإصدار 3](LICENSE). يحق لك استخدامه وتعديله ومشاركته، حتى تجاريًا، بشرط أن تُبقي أي نسخة توزّعها على إشعار حقوق النشر هذا، وأن توضّح ما غيّرته، وأن ترفق شيفرتها المصدرية كاملة بالرخصة نفسها.

</div>
