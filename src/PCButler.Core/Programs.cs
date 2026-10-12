// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>A program Windows lists as installed.</summary>
public sealed class InstalledProgram
{
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    /// <summary>Bytes, as the installer reported it; 0 when it didn't.</summary>
    public long Size { get; init; }
    /// <summary>"yyyy-MM-dd", or "" when unknown.</summary>
    public string Installed { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string Location { get; init; } = "";
    /// <summary>The icon Windows shows for it, usually the program's own .exe.</summary>
    public string Icon { get; init; } = "";
    /// <summary>The drive it is installed on ("C:"), or "" when its entry doesn't say where it is.</summary>
    public string Drive
    {
        get
        {
            foreach (var text in new[] { Location, Icon, UninstallCommand })
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<![A-Za-z])([A-Za-z]):\\");
                if (match.Success)
                    return match.Groups[1].Value.ToUpperInvariant() + ":";
            }
            return "";
        }
    }
    /// <summary>Where its entry lives: "HKCU" or "HKLM", the registry view and the key's name.</summary>
    public required string Hive { get; init; }
    public RegistryView View { get; init; }
    public required string Key { get; init; }
    public bool Msi { get; init; }
}

/// <summary>Something an uninstalled program left behind: Kind is "folder", "file" or "registry".</summary>
public sealed record Leftover(string Kind, string Path, long Size);

/// <summary>A newer version that winget can install.</summary>
public sealed record ProgramUpdate(string Name, string Id, string Version, string Available);

/// <summary>
/// The installed programs: listing them, uninstalling one with its own uninstaller and
/// then finding what it left behind, and updating through winget (Windows' own package
/// manager). Leftovers are only ever suggested: the user ticks what goes, folders go to
/// the Recycle Bin and registry keys are saved to a .reg file first.
/// </summary>
public static partial class Programs
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static RegistryKey Base(string hive, RegistryView view) =>
        RegistryKey.OpenBaseKey(hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);

    public static List<InstalledProgram> Installed()
    {
        var programs = new List<InstalledProgram>();
        foreach (var (hive, view) in new[]
                 {
                     ("HKLM", RegistryView.Registry64), ("HKLM", RegistryView.Registry32), ("HKCU", RegistryView.Default),
                 })
        {
            try
            {
                using var root = Base(hive, view);
                using var list = root.OpenSubKey(UninstallKey);
                foreach (var name in list?.GetSubKeyNames() ?? [])
                {
                    using var key = list!.OpenSubKey(name);
                    if (key is null)
                        continue;
                    string Text(string value) => key.GetValue(value)?.ToString()?.Trim() ?? "";
                    var display = Text("DisplayName");
                    // Entries Windows itself hides: parts of other programs, updates, and ones with nothing to run.
                    if (display.Length == 0 || Text("SystemComponent") == "1" || Text("ParentKeyName").Length > 0
                        || Text("ReleaseType") is "Update" or "Hotfix" or "Security Update")
                        continue;
                    var msi = Text("WindowsInstaller") == "1";
                    var uninstall = Text("UninstallString");
                    if (uninstall.Length == 0 && !msi)
                        continue;
                    if (programs.Any(other => other.Name == display && other.Version == Text("DisplayVersion")))
                        continue; // listed in both the 64- and 32-bit views
                    var date = Text("InstallDate");
                    programs.Add(new InstalledProgram
                    {
                        Name = display, Version = Text("DisplayVersion"), Publisher = Text("Publisher"),
                        Size = key.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : 0,
                        Installed = date.Length == 8 && date.All(char.IsDigit) ? $"{date[..4]}-{date[4..6]}-{date[6..]}" : "",
                        UninstallCommand = uninstall, Location = Text("InstallLocation").Trim('"').TrimEnd('\\'),
                        Icon = Text("DisplayIcon"), Hive = hive, View = view, Key = name, Msi = msi,
                    });
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return programs.OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool StillInstalled(InstalledProgram program)
    {
        try
        {
            using var root = Base(program.Hive, program.View);
            using var key = root.OpenSubKey($@"{UninstallKey}\{program.Key}");
            return key is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Start the program's own uninstaller, the same one Windows' Settings would start.
    /// It shows its own windows and asks for administrator rights itself if it needs them.
    /// </summary>
    public static Process? StartUninstall(InstalledProgram program)
    {
        var start = program.Msi && program.Key.StartsWith('{')
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {program.Key}")
            // The command is written for the command prompt, so that is what runs it (hidden).
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/s /c \"{program.UninstallCommand}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        start.UseShellExecute = true;
        try
        {
            return Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Log.Warning($"Could not start the uninstaller of {program.Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>Words in a file name that mark it as not the program itself.</summary>
    private static readonly string[] NotTheProgram = ["unins", "setup", "install", "update", "crash", "helper", "msiexec", "repair", "remove"];

    private static bool LooksLikeProgram(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
        && !NotTheProgram.Any(word => Path.GetFileName(path).Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What to run to open the program: its Start menu shortcut, the .exe its entry shows
    /// as its icon, or the one .exe in its folder that carries its name. Null when there is
    /// no telling (many entries are drivers, runtimes and the like, with nothing to open).
    /// </summary>
    public static string? FindLauncher(InstalledProgram program)
    {
        var name = CleanName(program.Name);
        if (name.Length > 0)
        {
            foreach (var menu in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                try
                {
                    var folder = Environment.GetFolderPath(menu);
                    var shortcut = folder.Length == 0 ? null : Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories)
                        .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file).Equals(name, StringComparison.OrdinalIgnoreCase)
                                                || Path.GetFileNameWithoutExtension(file).Equals(program.Name, StringComparison.OrdinalIgnoreCase));
                    if (shortcut is not null)
                        return shortcut;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        // "C:\path\app.exe,0" or a quoted path.
        var icon = program.Icon.Trim('"');
        var comma = icon.LastIndexOf(',');
        if (comma > 2 && int.TryParse(icon[(comma + 1)..].Trim(), out _))
            icon = icon[..comma].Trim('"');
        if (LooksLikeProgram(icon))
            return icon;
        try
        {
            if (program.Location.Length > 0 && Directory.Exists(program.Location))
            {
                var programs = Directory.EnumerateFiles(program.Location, "*.exe").Where(LooksLikeProgram).ToList();
                var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return programs.FirstOrDefault(file => words.Any(word => word.Length > 2 && Path.GetFileNameWithoutExtension(file)
                                                                             .Contains(word, StringComparison.OrdinalIgnoreCase)))
                       ?? (programs.Count == 1 ? programs[0] : null);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    // --- leftovers ---

    [GeneratedRegex(@"\s*(\([^)]*\)|\[[^\]]*\]|v?\d+(\.\d+)+\S*|\b(x64|x86|64-bit|32-bit|amd64)\b|\bversion\b|\b\d+\b)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    /// <summary>Names too general to be one program's own folder.</summary>
    private static readonly HashSet<string> TooGeneral = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Google", "Adobe", "Intel", "NVIDIA", "AMD", "Programs", "Temp", "Packages", "Mozilla",
        "Apple", "Update", "Setup", "Installer", "Driver", "Drivers", "Tools", "Games", "System", "Data", "Cache", "Local", "Roaming",
        "Desktop", "Documents", "Downloads", "Classes", "Policies", "Wow6432Node", "Office", "Java", "Python", "Steam", "User",
    };

    /// <summary>"Example App 2.1 (64-bit)" -> "Example App". Empty when what remains is too short or too general to match on.</summary>
    public static string CleanName(string name)
    {
        var clean = name.Trim();
        for (var before = ""; before != clean;)
        {
            before = clean;
            clean = TrailingVersion().Replace(clean, "").Trim(' ', '-', ',', '_');
        }
        return clean.Length < 4 || TooGeneral.Contains(clean) || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "" : clean;
    }

    private static long FolderSize(string folder)
    {
        long size = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                size += file.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return size;
    }

    /// <summary>
    /// What an uninstalled program left behind: its install folder, folders carrying its
    /// exact name in the usual places, its Start menu and desktop shortcuts, and its own
    /// key under HKEY_CURRENT_USER\Software. Matching is by exact name only, and a folder
    /// that another installed program lives in is never offered.
    /// </summary>
    public static List<Leftover> Leftovers(InstalledProgram program, IReadOnlyList<InstalledProgram>? stillInstalled = null)
    {
        stillInstalled ??= Installed();
        var found = new List<Leftover>();
        var name = CleanName(program.Name);
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var roots = new[]
        {
            Folder(Environment.SpecialFolder.ApplicationData), Folder(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Folder(Environment.SpecialFolder.CommonApplicationData), Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(root => root.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var menus = new[]
        {
            Folder(Environment.SpecialFolder.Programs), Folder(Environment.SpecialFolder.CommonPrograms),
            Folder(Environment.SpecialFolder.DesktopDirectory), Folder(Environment.SpecialFolder.CommonDesktopDirectory),
        }.Where(root => root.Length > 0).ToList();
        string[] never =
        [
            .. roots, .. menus, Folder(Environment.SpecialFolder.Windows), Folder(Environment.SpecialFolder.UserProfile),
            Folder(Environment.SpecialFolder.System), AppInfo.DataDir,
        ];

        bool Safe(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            // Not a drive or a folder right under it, not one of the shared places, not home to another program.
            if (full.Count(character => character == '\\') < 2
                || never.Any(folder => folder.Length > 0 && (full.Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                                              || folder.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))))
                return false;
            return !stillInstalled.Any(other => other.Key != program.Key && other.Location.Length > 0
                                                && (other.Location + "\\").StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase));
        }

        void AddFolder(string path)
        {
            if (Directory.Exists(path) && Safe(path) && !found.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                found.Add(new Leftover("folder", path, FolderSize(path)));
        }

        if (program.Location.Length > 0 && Path.IsPathFullyQualified(program.Location))
            AddFolder(program.Location);
        if (name.Length > 0)
        {
            var publisher = program.Publisher.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? program.Publisher.Trim().TrimEnd('.') : "";
            foreach (var root in roots)
            {
                AddFolder(Path.Combine(root, name));
                if (publisher.Length > 0)
                    AddFolder(Path.Combine(root, publisher, name));
            }
            foreach (var menu in menus)
            {
                AddFolder(Path.Combine(menu, name));
                var shortcut = Path.Combine(menu, name + ".lnk");
                if (File.Exists(shortcut))
                    found.Add(new Leftover("file", shortcut, new FileInfo(shortcut).Length));
            }
            foreach (var key in publisher.Length > 0 ? new[] { name, $@"{publisher}\{name}" } : [name])
            {
                try
                {
                    using var software = Registry.CurrentUser.OpenSubKey($@"Software\{key}");
                    if (software is not null)
                        found.Add(new Leftover("registry", $@"HKCU\Software\{key}", 0));
                }
                catch (Exception error) when (error is System.Security.SecurityException or IOException)
                {
                }
            }
        }
        return found;
    }

    public static string RegistryBackupFolder => Path.Combine(AppInfo.DataDir, "registry-backups");

    /// <summary>
    /// Remove the chosen leftovers: folders and files go to the Recycle Bin, registry keys
    /// are saved to a .reg file in the data folder and then deleted. Returns what failed.
    /// </summary>
    public static List<string> RemoveLeftovers(IEnumerable<Leftover> leftovers)
    {
        var errors = new List<string>();
        foreach (var item in leftovers)
        {
            try
            {
                if (item.Kind != "registry")
                {
                    RecycleBin.Send(item.Path);
                    if (Directory.Exists(item.Path))
                        throw new IOException("it needs administrator rights, or a file in it is in use");
                    continue;
                }
                const string prefix = @"HKCU\Software\";
                if (!item.Path.StartsWith(prefix, StringComparison.Ordinal) || item.Path.Length == prefix.Length)
                    continue;
                Directory.CreateDirectory(RegistryBackupFolder);
                var safe = string.Concat(item.Path[prefix.Length..].Select(character => char.IsLetterOrDigit(character) ? character : '-'));
                var backup = Path.Combine(RegistryBackupFolder, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
                var export = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var argument in new[] { "export", item.Path, backup, "/y" })
                    export.ArgumentList.Add(argument);
                using (var process = Process.Start(export)!)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0 || !File.Exists(backup))
                        throw new IOException("it could not be backed up, so it was left alone");
                }
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + item.Path[prefix.Length..], throwOnMissingSubKey: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                              or System.ComponentModel.Win32Exception or ArgumentException)
            {
                errors.Add($"{item.Path}: {error.Message}");
            }
        }
        return errors;
    }

    // --- updates, through winget ---

    private static string Winget =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    public static bool WingetAvailable => File.Exists(Winget);

    [GeneratedRegex(@"^[\w.+-]+$")] private static partial Regex PackageId();
    [GeneratedRegex(@"^-{10,}\s*$")] private static partial Regex Rule();

    /// <summary>Read winget's table of upgrades. Each row ends with: id, version, available and sometimes the source.</summary>
    public static List<ProgramUpdate> ParseUpdates(string output)
    {
        var updates = new List<ProgramUpdate>();
        // A progress spinner rewrites its line with carriage returns; what counts is the text after the last one.
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).Select(line => line[(line.LastIndexOf('\r') + 1)..]).ToList();
        var inTable = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (Rule().IsMatch(lines[index]))
            {
                inTable = true;
                continue;
            }
            if (!inTable || (index + 1 < lines.Count && Rule().IsMatch(lines[index + 1])))
                continue; // not in a table yet, or the heading of a second table
            var parts = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                inTable = lines[index].Trim().Length == 0 && inTable;
                continue;
            }
            // The source column is left out when only one source was asked, so the row then ends with the new version.
            var end = char.IsDigit(parts[^1][0]) ? 0 : 1;
            if (parts.Length < 4 + end)
                continue;
            var (id, version, available) = (parts[^(3 + end)], parts[^(2 + end)], parts[^(1 + end)]);
            // A name or id cut short to fit the screen ends in "…" and can't be used to update.
            if (!PackageId().IsMatch(id) || !char.IsDigit(available[0]) || updates.Any(update => update.Id == id))
                continue;
            updates.Add(new ProgramUpdate(string.Join(' ', parts[..^(3 + end)]), id, version, available));
        }
        return updates;
    }

    /// <summary>Ask winget which programs have a newer version. Returns null if winget is missing or fails.</summary>
    public static List<ProgramUpdate>? Updates()
    {
        if (!WingetAvailable)
            return null;
        try
        {
            var start = new ProcessStartInfo(Winget, "upgrade --source winget --disable-interactivity")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var updates = ParseUpdates(output);
            if (process.ExitCode != 0 && updates.Count == 0)
            {
                Log.Warning($"winget upgrade ended with code {process.ExitCode}");
                return null;
            }
            return updates;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"winget could not be run: {error.Message}");
            return null;
        }
    }

    /// <summary>Update one program in a winget window of its own, so its questions and progress are visible.</summary>
    public static Process? StartUpdate(ProgramUpdate update)
    {
        if (!WingetAvailable || !PackageId().IsMatch(update.Id))
            return null;
        try
        {
            return Process.Start(new ProcessStartInfo(Winget, $"upgrade --id {update.Id} --exact --source winget") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Log.Warning($"winget could not be started: {error.Message}");
            return null;
        }
    }

    // --- apps to install, through winget ---

    /// <summary>Apps most PCs need: a browser, archives, chat, games, video, the runtimes other programs rely on and a few tools.</summary>
    public static readonly CatalogApp[] EssentialApps =
    [
        new("Google Chrome", "Google.Chrome", "Google Chrome", "Web browser"),
        new("WinRAR", "RARLab.WinRAR", "WinRAR", "Opens and makes rar and zip archives"),
        new("Visual C++ Redistributable", "Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            "Needed by many programs and games"),
        new(".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft Windows Desktop Runtime - 8", "Needed by many Windows programs"),
        new("Discord", "Discord.Discord", "Discord", "Voice, video and text chat"),
        new("Steam", "Valve.Steam", "Steam", "Games store and launcher"),
        new("Git (with Git Bash)", "Git.Git", "Git", "Version control, with the Git Bash terminal"),
        new("Media Player Classic (MPC-HC)", "clsid2.mpc-hc", "MPC-HC", "A light video player that plays almost anything"),
        new("Visual Studio Code", "Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "Code and text editor"),
        new("TeraCopy", "CodeSector.TeraCopy", "TeraCopy", "Faster, safer file copying"),
        new("WizTree", "AntibodySoftware.WizTree", "WizTree", "Shows what fills your drives, in seconds"),
    ];

    /// <summary>Apps worth having: well-known, free and useful on many PCs.</summary>
    public static readonly CatalogApp[] RecommendedApps =
    [
        new("7-Zip", "7zip.7zip", "7-Zip", "Opens and makes zip, rar and 7z archives"),
        new("Mozilla Firefox", "Mozilla.Firefox", "Mozilla Firefox", "Web browser"),
        new("VLC media player", "VideoLAN.VLC", "VLC media player", "Plays almost any video or audio file"),
        new("Adobe Acrobat Reader", "Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat", "Opens and fills in PDF files"),
        new("GitHub Desktop", "GitHub.GitHubDesktop", "GitHub Desktop", "Work with GitHub repositories without the command line"),
        new("MSI Afterburner", "Guru3D.Afterburner", "MSI Afterburner", "Watches and tunes your graphics card"),
        new("Everything", "voidtools.Everything", "Everything", "Finds any file on your PC the moment you type"),
        new("Microsoft PowerToys", "Microsoft.PowerToys", "PowerToys", "Extra tools for Windows from Microsoft"),
        new("ShareX", "ShareX.ShareX", "ShareX", "Screenshots and screen recordings"),
        new("Bitwarden", "Bitwarden.Bitwarden", "Bitwarden", "Keeps your passwords safe"),
        new("LibreOffice", "TheDocumentFoundation.LibreOffice", "LibreOffice", "Free office suite: documents, sheets and slides"),
        new("OBS Studio", "OBSProject.OBSStudio", "OBS Studio", "Records and streams your screen"),
        new("EchoSub", "mPhpMaster/echo-sub", "EchoSub", "Live, translated captions for anything your PC plays", AppSource.GitHub, Exact: true),
        new("EchoSub Lens", "mPhpMaster/echo-sub-lens", "EchoSub Lens", "Translates the text on your screen, in place", AppSource.GitHub),
        new("ATS CV Studio", "mPhpMaster/ats-cv-studio", "ATS CV Studio", "Builds ATS-friendly CVs and checks any CV, in English and Arabic",
            AppSource.GitHub),
        new("AndroidDC", "mPhpMaster/androiddc", "AndroidDC", "Controls an Android phone from Windows over adb: mirroring, files, apps and more",
            AppSource.Page),
        new("Prayer Times Reminder", "9PFTWDWLNL7C", "Prayer Times Reminder", "Reminds you of prayer times and takes a break for them",
            AppSource.Store),
    ];

    /// <summary>Whether an app from the lists above is installed, judged by the names in the installed list.</summary>
    public static bool IsInstalled(CatalogApp app, IEnumerable<InstalledProgram> installed) =>
        app.Source != AppSource.Page
        && installed.Any(program => program.Name.StartsWith(app.Match, StringComparison.OrdinalIgnoreCase)
                                    // a whole name or word: "Git" is not "GitHub Desktop"
                                    && (program.Name.Length == app.Match.Length
                                        || !app.Exact && !char.IsLetterOrDigit(program.Name[app.Match.Length])));

    /// <summary>
    /// Start installing one app, with its own window so its questions and progress are visible:
    /// winget for winget and Microsoft Store apps, the release's setup file for GitHub apps.
    /// An app with only a page gets that page opened, and null back. Throws IOException when
    /// a GitHub download fails.
    /// </summary>
    public static Process? StartInstall(CatalogApp app)
    {
        switch (app.Source)
        {
            case AppSource.Page:
                Shell.Open(app.Page);
                return null;
            case AppSource.GitHub:
                return Process.Start(new ProcessStartInfo(DownloadRelease(app)) { UseShellExecute = true });
        }
        if (!WingetAvailable || !PackageId().IsMatch(app.Id))
            return null;
        var source = app.Source == AppSource.Store ? "msstore" : "winget";
        try
        {
            return Process.Start(new ProcessStartInfo(Winget, $"install --id {app.Id} --exact --source {source}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Log.Warning($"winget could not be started: {error.Message}");
            return null;
        }
    }

    /// <summary>The setup file of the app's latest GitHub release, downloaded and checked against its .sha256 when it has one.</summary>
    public static string DownloadRelease(CatalogApp app)
    {
        try
        {
            using var client = Updater.Client(600);
            using var release = System.Text.Json.JsonDocument.Parse(
                client.GetStringAsync($"https://api.github.com/repos/{app.Id}/releases/latest").GetAwaiter().GetResult());
            var assets = release.RootElement.GetProperty("assets").EnumerateArray()
                .Select(asset => (Name: asset.GetProperty("name").GetString() ?? "", Url: asset.GetProperty("browser_download_url").GetString() ?? ""))
                .ToList();
            var setup = assets.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                       && asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));
            if (setup.Url.Length == 0 || Path.GetFileName(setup.Name) != setup.Name)
                throw new IOException($"the latest release of {app.Name} has no setup file");
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCButlerApps")).FullName;
            var target = Path.Combine(folder, setup.Name);
            using (var download = client.GetStreamAsync(setup.Url).GetAwaiter().GetResult())
            using (var file = File.Create(target))
                download.CopyTo(file);
            var check = assets.FirstOrDefault(asset => asset.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (check.Url.Length > 0)
            {
                var expected = client.GetStringAsync(check.Url).GetAwaiter().GetResult().Trim().Split(' ', '\t')[0];
                using var stream = File.OpenRead(target);
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Dispose();
                    File.Delete(target);
                    throw new IOException($"the download of {app.Name} did not match its checksum");
                }
            }
            return target;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException(error.Message, error);
        }
    }
}

public enum AppSource { Winget, Store, GitHub, Page }

/// <summary>An app that can be installed from the Programs page: its name, winget package, the start of its installed name, and what it does.</summary>
/// Id is a winget or Microsoft Store package, or "owner/repo" on GitHub. Exact: only that very name counts as installed.
public sealed record CatalogApp(string Name, string Id, string Match, string Description, AppSource Source = AppSource.Winget, bool Exact = false)
{
    public string Page => Source switch
    {
        AppSource.GitHub or AppSource.Page => $"https://github.com/{Id}",
        AppSource.Store => $"https://apps.microsoft.com/detail/{Id.ToLowerInvariant()}",
        _ => $"https://winstall.app/apps/{Id}",
    };
}
