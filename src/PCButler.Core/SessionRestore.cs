// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>One program that had a window open: how to start it again.</summary>
public sealed class SessionApp
{
    /// <summary>The program file, or "" for a Store app started by <see cref="AppId"/>.</summary>
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
    /// <summary>What it was started with after the program's name (often the file it has open).</summary>
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    /// <summary>A Store app's AppUserModelID, started through shell:AppsFolder.</summary>
    [JsonPropertyName("app_id")] public string AppId { get; set; } = "";
    /// <summary>The title of its window, to show which one it is.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>Files named in its arguments that exist, so they can be counted and listed.</summary>
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];

    [JsonIgnore] public string Name => Exe.Length > 0 ? Path.GetFileNameWithoutExtension(Exe) : AppId.Split('!')[^1];
}

/// <summary>What was open at one moment: programs with windows, and File Explorer's folders.</summary>
public sealed class SessionSnapshot
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    /// <summary>When Windows had started, so a snapshot from before a restart can be told from one of this run.</summary>
    [JsonPropertyName("boot")] public DateTime Boot { get; set; }
    [JsonPropertyName("apps")] public List<SessionApp> Apps { get; set; } = [];
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];

    [JsonIgnore] public int FileCount => Apps.Sum(app => app.Files.Count);
    [JsonIgnore] public bool IsEmpty => Apps.Count == 0 && Folders.Count == 0;
}

/// <summary>
/// "Resume yesterday": while it is on, PC Butler notes every minute which programs have a
/// window open (with what they were started with, which is usually the file they have open)
/// and which folders File Explorer shows. The note from before Windows last restarted is kept
/// apart, so one click can open all of it again. Programs already running are left alone.
/// Only names and paths are noted, in session.json in the data folder, never what is in the files.
/// </summary>
public static class SessionRestore
{
    /// <summary>Programs never noted: Windows' own shell, and PC Butler itself.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "SearchApp", "LockApp", "Taskmgr", "PCButler", "ShellHost", "WidgetBoard", "Widgets", "LogonUI", "dwm", "CredentialUIBroker",
        "SecurityHealthSystray", "smartscreen", "consent", "rundll32", "dllhost", "conhost", "OpenWith", "PickerHost",
    };

    /// <summary>Programs in the Windows folder that people open themselves, and so are noted after all.</summary>
    private static readonly HashSet<string> WindowsTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "mspaint", "write", "cmd", "powershell", "regedit", "mmc", "charmap", "magnify", "SnippingTool", "wordpad",
    };

    public const int MostApps = 40;

    private static readonly object Gate = new();
    private static bool frozen;

    private static string CurrentFile => Path.Combine(AppInfo.DataDir, "session.json");
    private static string PreviousFile => Path.Combine(AppInfo.DataDir, "session-previous.json");

    /// <summary>When Windows started, to the second or so.</summary>
    public static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// At PC Butler's start: a note made before Windows restarted becomes the one to resume
    /// from. A note from this same run of Windows (PC Butler was only restarted) stays as it is.
    /// </summary>
    public static void StartOfRun()
    {
        lock (Gate)
        {
            frozen = false;
            if (Read(CurrentFile) is not { } last)
                return;
            if (Math.Abs((last.Boot - BootTime).TotalMinutes) < 2)
                return;
            try
            {
                if (!last.IsEmpty)
                    File.Move(CurrentFile, PreviousFile, overwrite: true);
                else
                    File.Delete(CurrentFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Resume yesterday: the last session could not be put aside: {error.Message}");
            }
        }
    }

    /// <summary>Note what is open now. After <see cref="Freeze"/> (Windows is shutting down) nothing more is noted.</summary>
    public static void Remember(bool last = false)
    {
        SessionSnapshot now;
        try
        {
            now = Capture();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning($"Resume yesterday: what is open could not be read: {error.Message}");
            return;
        }
        lock (Gate)
        {
            if (frozen)
                return;
            if (last && Read(CurrentFile) is { } before && (now.Time - before.Time).TotalMinutes < 3)
            {
                // Windows is closing programs as this runs: what was open a moment ago counts too.
                foreach (var app in before.Apps.Where(app => !now.Apps.Any(seen => Same(seen, app))))
                    now.Apps.Add(app);
                foreach (var folder in before.Folders.Where(folder => !now.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                    now.Folders.Add(folder);
            }
            Write(CurrentFile, now);
            frozen = last;
        }
    }

    /// <summary>Windows is shutting down: note once more, then keep that note for the next start.</summary>
    public static void Freeze() => Remember(last: true);

    /// <summary>What was open before Windows last restarted, or null when there is nothing to resume.</summary>
    public static SessionSnapshot? Previous()
    {
        lock (Gate)
            return Read(PreviousFile) is { IsEmpty: false } previous ? previous : null;
    }

    /// <summary>Forget both notes (when the feature is switched off, or after resuming).</summary>
    public static void Forget(bool previousOnly = false)
    {
        lock (Gate)
        {
            foreach (var file in previousOnly ? new[] { PreviousFile } : new[] { PreviousFile, CurrentFile })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Open again what the snapshot holds: each program not running now, with what it was
    /// started with, and each folder not already open. Returns how many were opened and what
    /// could not be.
    /// </summary>
    public static (int Opened, List<string> Errors) Resume(SessionSnapshot snapshot)
    {
        var errors = new List<string>();
        var opened = 0;
        var running = RunningPrograms();
        var openFolders = new HashSet<string>(ExplorerFolders(), StringComparer.OrdinalIgnoreCase);
        foreach (var app in snapshot.Apps)
        {
            try
            {
                if (app.AppId.Length > 0)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppId}") { UseShellExecute = true })?.Dispose();
                }
                else
                {
                    if (running.Contains(app.Exe) && app.Files.Count == 0)
                        continue; // already open, and nothing in particular to open in it
                    if (!File.Exists(app.Exe))
                    {
                        errors.Add($"{app.Name}: {Tr.T("the program is not there any more.")}");
                        continue;
                    }
                    Process.Start(new ProcessStartInfo(app.Exe, app.Arguments)
                    {
                        UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.Exe) ?? "",
                    })?.Dispose();
                }
                opened++;
                Thread.Sleep(250); // one after another, so the PC is not swamped
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                errors.Add($"{app.Name}: {error.Message}");
            }
        }
        foreach (var folder in snapshot.Folders)
        {
            if (openFolders.Contains(folder))
                continue;
            try
            {
                if (folder.StartsWith("::", StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:" + folder) { UseShellExecute = true })?.Dispose();
                else if (Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
                else
                {
                    errors.Add($"{folder}: {Tr.T("the folder is not there any more.")}");
                    continue;
                }
                opened++;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                errors.Add($"{folder}: {error.Message}");
            }
        }
        return (opened, errors);
    }

    /// <summary>What is open now: every program with a window on the taskbar, and File Explorer's folders.</summary>
    public static SessionSnapshot Capture()
    {
        var snapshot = new SessionSnapshot { Time = DateTime.Now, Boot = BootTime };
        var windows = new Dictionary<int, string>();
        EnumWindows((handle, _) =>
        {
            if (IsTaskbarWindow(handle) && GetWindowThreadProcessId(handle, out var pid) != 0 && pid != Environment.ProcessId && !windows.ContainsKey((int)pid))
                windows[(int)pid] = TitleOf(handle);
            return true;
        }, IntPtr.Zero);

        var commandLines = CommandLines(windows.Keys);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (pid, title) in windows)
        {
            if (snapshot.Apps.Count >= MostApps)
                break;
            var (exe, appId) = Identify(pid);
            if (exe.Length == 0)
                continue;
            var name = Path.GetFileNameWithoutExtension(exe);
            if (Skipped.Contains(name))
                continue;
            if (exe.StartsWith(system, StringComparison.OrdinalIgnoreCase) && !WindowsTools.Contains(name))
                continue;
            var app = new SessionApp { Title = title };
            if (appId.Length > 0 && exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                // A Store app's own file cannot be started directly; Windows starts it by its ID.
                app.AppId = appId;
            }
            else
            {
                app.Exe = exe;
                app.Arguments = commandLines.TryGetValue(pid, out var line) ? ArgumentsOf(line) : "";
                app.Files = FilesIn(app.Arguments);
            }
            if (!snapshot.Apps.Any(seen => Same(seen, app)))
                snapshot.Apps.Add(app);
        }
        foreach (var folder in ExplorerFolders())
        {
            if (!snapshot.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                snapshot.Folders.Add(folder);
        }
        return snapshot;
    }

    private static bool Same(SessionApp a, SessionApp b) =>
        string.Equals(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase) && a.Arguments == b.Arguments && a.AppId == b.AppId;

    /// <summary>The part of a command line after the program's own name.</summary>
    public static string ArgumentsOf(string commandLine)
    {
        var line = commandLine.TrimStart();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end < 0 ? "" : line[(end + 1)..].Trim();
        }
        var space = line.IndexOf(' ');
        return space < 0 ? "" : line[(space + 1)..].Trim();
    }

    /// <summary>The existing files named in arguments: quoted paths, or the whole text when it is one path.</summary>
    public static List<string> FilesIn(string arguments)
    {
        var found = new List<string>();
        if (arguments.Length == 0)
            return found;
        var parts = new List<string>();
        var quoted = arguments.Split('"');
        for (var index = 1; index < quoted.Length; index += 2)
            parts.Add(quoted[index]);
        parts.Add(arguments.Trim('"'));
        parts.AddRange(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in parts)
        {
            if (part.Length < 4 || !(part.Length > 2 && part[1] == ':' || part.StartsWith(@"\\", StringComparison.Ordinal)))
                continue;
            try
            {
                if (File.Exists(part) && !found.Contains(part, StringComparer.OrdinalIgnoreCase))
                    found.Add(part);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
            }
        }
        return found;
    }

    /// <summary>The program files of everything running now.</summary>
    private static HashSet<string> RunningPrograms()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (Identify(process.Id).Exe is { Length: > 0 } exe)
                    paths.Add(exe);
            }
        }
        return paths;
    }

    /// <summary>The folders File Explorer has open (each tab), as paths, or "::{…}" for places like This PC.</summary>
    public static List<string> ExplorerFolders()
    {
        var folders = new List<string>();
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type)
                return folders;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                var windows = shell.Windows();
                int count = windows.Count;
                for (var index = 0; index < count; index++)
                {
                    try
                    {
                        var window = windows.Item(index);
                        if (window is null)
                            continue;
                        string program = window.FullName;
                        if (!program.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string path = window.Document.Folder.Self.Path;
                        if (path.Length > 0 && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                            folders.Add(path);
                    }
                    catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                    {
                        // a window that is closing, or one that shows no folder
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            Log.Warning($"Resume yesterday: File Explorer's folders could not be read: {error.Message}");
        }
        return folders;
    }

    /// <summary>The command line each process was started with, read in one go from Windows' process list.</summary>
    private static Dictionary<int, string> CommandLines(IEnumerable<int> pids)
    {
        var lines = new Dictionary<int, string>();
        var wanted = pids.ToList();
        if (wanted.Count == 0)
            return lines;
        try
        {
            var filter = string.Join(" OR ", wanted.Select(pid => $"ProcessId = {pid}"));
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE {filter}");
            foreach (var found in searcher.Get())
            {
                using (found)
                {
                    if (found["CommandLine"] is string line && found["ProcessId"] is uint pid)
                        lines[(int)pid] = line;
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
            Log.Warning($"Resume yesterday: command lines could not be read: {error.Message}");
        }
        return lines;
    }

    /// <summary>A process's program file and, for a packaged (Store) app, its AppUserModelID.</summary>
    private static (string Exe, string AppId) Identify(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return ("", "");
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            var exe = QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
            var idLength = 512;
            var id = new StringBuilder(idLength);
            var appId = GetApplicationUserModelId(handle, ref idLength, id) == 0 ? id.ToString() : "";
            return (exe, appId);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>A window that shows on the taskbar: visible, not owned, not a tool window, not hidden away by Windows (cloaked).</summary>
    private static bool IsTaskbarWindow(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero || GetWindowTextLength(handle) == 0)
            return false;
        long style = GetWindowLong(handle, GwlExStyle);
        if ((style & WsExToolWindow) != 0 && (style & WsExAppWindow) == 0)
            return false;
        return DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string TitleOf(IntPtr handle)
    {
        var text = new StringBuilder(GetWindowTextLength(handle) + 1);
        GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private static SessionSnapshot? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(file)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write(string file, SessionSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Resume yesterday: what is open could not be saved: {error.Message}");
        }
    }

    private const int QueryLimitedInformation = 0x1000;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);
}
