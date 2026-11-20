// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// What this process is. PC Butler is one program file that runs as several small processes:
/// the background agent (tray icon and the background features that are switched on), the
/// floating board (plain launch), and one process per open tool (--tool &lt;id&gt;).
/// </summary>
public enum AppMode { Board, Agent, Tool }

/// <summary>The tools the board lists, grouped as the board shows them, and how each is started.</summary>
public static class Tools
{
    /// <summary>(key, icon, name, colour, its tools) of each block on the board.</summary>
    public static readonly (string Key, string Glyph, string Name, string Color, string[] Members)[] Groups =
    [
        ("files", "", "Files", "#42d8a5", ["search", "preview", "duplicates", "storage"]),
        ("cleanup", "", "Clean up", "#5aabf1", ["cleaner", "programs", "shredder"]),
        ("speed", "", "Speed", "#edb755", ["booster", "benchmark", Overlay]),
        ("network", "", "Network", "#5cc6d6", ["internet", "lan"]),
        ("share", "", "Share & protect", "#b697f1", ["portal", "clipboard", "vault"]),
        ("history", "", "History", "#d99a6c", ["history"]),
    ];

    /// <summary>The quick search box and command palette: a process of its own with no window of its own (--popup).</summary>
    public const string Popup = "popup";

    /// <summary>Open the search box (or the command palette) over whatever program is in front.</summary>
    public static void OpenPopup(bool palette) => Start(palette ? ["--popup", "palette"] : ["--popup"]);

    /// <summary>Not a window: the board's switch for the performance overlay (it runs in the background agent).</summary>
    public const string Overlay = "overlay";

    /// <summary>One line about each tool: the subtitle its page already shows.</summary>
    public static readonly Dictionary<string, string> Lines = new()
    {
        ["search"] = "Every file and folder on your drives, found as you type.",
        ["preview"] = "A little organization. A lot more peace of mind.",
        ["duplicates"] = "Find extra copies. Keep what matters.",
        ["storage"] = "See what fills your disk. Decide what stays.",
        ["cleaner"] = "Clear the clutter. Make room for what’s next.",
        ["programs"] = "Everything installed, in one list.",
        ["shredder"] = "Gone means gone.",
        ["booster"] = "A lighter startup. A smoother day.",
        ["benchmark"] = "See how your PC is really doing, while you play.",
        [Overlay] = "Frames per second and temperatures over your games.",
        ["internet"] = "How fast is your connection, and what is using it?",
        ["lan"] = "Which devices are on your network?",
        ["portal"] = "Files between your PCs, across the internet.",
        ["clipboard"] = "Everything you copy, ready to paste again.",
        ["vault"] = "Your private space. Under your control.",
        ["history"] = "Every change, all in one place.",
        ["settings"] = "Your folders. Your rules.",
        ["about"] = "A little help, whenever you need it.",
    };

    /// <summary>A window this program can open with --tool (every page of the old main window except Home).</summary>
    public static bool IsTool(string key) => key != "home" && MainWindow.PageList.Any(page => page.Key == key);

    public static string Glyph(string key) =>
        key == Overlay ? "" : MainWindow.PageList.FirstOrDefault(page => page.Key == key).Glyph ?? "";

    /// <summary>The tool's name in the user's language.</summary>
    public static string Name(string key) =>
        key == Overlay ? Ui.T("Performance overlay") : Ui.T(MainWindow.PageList.FirstOrDefault(page => page.Key == key).Name ?? key);

    /// <summary>Open a tool in a process of its own (or bring its window forward if it is open already).</summary>
    public static void Open(string key, params string[] extra)
    {
        if (key == "home")
        {
            OpenBoard();
            return;
        }
        Start(["--tool", key, .. extra]);
    }

    /// <summary>Open a tool with files handed to it (Shredder's list, say).</summary>
    public static void OpenWith(string key, IEnumerable<string> paths)
    {
        var list = Path.Combine(Path.GetTempPath(), $"PCButler-{key}-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(list, paths);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not hand files to {key}: {error.Message}");
            Open(key);
            return;
        }
        Open(key, "--paths", list);
    }

    public static void OpenBoard() => Start([]);

    /// <summary>Start the background agent, unless it is running already (it checks that itself too).</summary>
    public static void StartAgent()
    {
        if (!AgentLink.Running)
            Start(["--agent"]);
    }

    private static void Start(string[] arguments)
    {
        try
        {
            // The new window may come to the front (the tray icon's click gave this process that right).
            AllowSetForegroundWindow(-1);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not start {string.Join(' ', arguments)}: {error.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>The paths of a list file given with --paths, which is then deleted.</summary>
    public static List<string> TakeList(string file)
    {
        try
        {
            var paths = File.ReadAllLines(file).Where(line => line.Length > 0).ToList();
            File.Delete(file);
            return paths;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Which background features are switched on, and so whether the background agent has anything
/// to do. With none of them on, no PC Butler process runs at all until a tool is opened.
/// </summary>
public static class AgentNeeds
{
    /// <summary>The names of the features that need the agent, in the order the board's footer lists them.</summary>
    public static List<string> Of(Config config)
    {
        var on = new List<string>();
        if (config.AutoSortOn)
            on.Add("Automatic sorting");
        if (config.Clipboard.Enabled)
            on.Add("Clipboard history");
        if (config.Portal.ShareClipboard)
            on.Add("Clipboard sharing");
        if (Hotkeys.Effective(config.Hotkeys).Values.Any(keys => keys.Length > 0))
            on.Add("Hotkeys");
        if (config.Portal.AutoStart && (config.Portal.Used || config.Portal.Role.Length > 0))
            on.Add("Courier");
        if (config.TrackInternet)
            on.Add("Internet tracking");
        if (config.Usage.Record)
            on.Add("Usage recording");
        if (config.ResumeSession)
            on.Add("Resume yesterday");
        if (config.DropZone)
            on.Add("Drop zone");
        if (config.Overlay.Enabled)
            on.Add("Performance overlay");
        if (config.UsageAlerts)
            on.Add("Usage alerts");
        if (config.Boost.AutoApps.Count > 0)
            on.Add("Automatic Boost");
        return on;
    }

    public static bool Any(Config config) => Of(config).Count > 0;
}

/// <summary>
/// The named pipe "PCButler.Agent": how the board and the tools talk to the background agent.
/// One JSON line each way: {"cmd":"config-changed","data":"…"} and the like, answered with {"ok":true,"reply":"…"}.
/// </summary>
public static class AgentLink
{
    private static readonly string PipeName = "PCButler.Agent" + CrossProcess.Scope;
    public static readonly string MutexName = @"Local\PCButler.Agent" + CrossProcess.Scope;

    /// <summary>True while the background agent runs (it holds its mutex for as long as it does).</summary>
    public static bool Running
    {
        get
        {
            try
            {
                if (Mutex.TryOpenExisting(MutexName, out var mutex))
                {
                    mutex.Dispose();
                    return true;
                }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // one started as administrator: it is there, only out of reach
            }
        }
    }

    /// <summary>Ask the agent something. Null when no agent answered in time.</summary>
    public static string? Send(string command, int timeoutMs = 1500, string data = "")
    {
        if (!Running)
            return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["cmd"] = command, ["data"] = data }));
            var task = reader.ReadLineAsync();
            if (!task.Wait(Math.Max(timeoutMs, 6000)))
                return null;
            var answer = task.Result is { } line ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line) : null;
            return answer is not null && answer.TryGetValue("reply", out var reply) ? reply.GetString() : null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or UnauthorizedAccessException
                                          or AggregateException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The settings were saved by this process: tell the agent, or start it when something that
    /// needs it was just switched on and it is not running.
    /// </summary>
    public static void ConfigChanged()
    {
        Task.Run(() =>
        {
            if (Running)
                Send("config-changed");
            else if (AgentNeeds.Any(ConfigStore.Load()))
                Tools.StartAgent();
        });
    }

    /// <summary>The agent's side: answer each connection with <paramref name="handle"/> (run off the window's thread).</summary>
    public static void Serve(Func<string, string, string> handle, CancellationToken stop)
    {
        var thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    var line = reader.ReadLine();
                    var request = line is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(line);
                    var reply = request is not null && request.TryGetValue("cmd", out var command) ? handle(command, request.GetValueOrDefault("data", "")) : "unknown";
                    writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["reply"] = reply }));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log.Seldom("agent pipe", TimeSpan.FromHours(1), $"WARNING Agent pipe: {error.Message}");
                    Thread.Sleep(200);
                }
            }
        }) { IsBackground = true, Name = "PC Butler agent pipe" };
        thread.Start();
    }
}

/// <summary>
/// Each tool is its own entry on the taskbar: grouped apart from the others, with its own name,
/// and pinned it starts that tool again (not the board).
/// </summary>
public static class TaskbarIdentity
{
    public static string IdFor(string tool) => "PCButler.Tool." + tool;

    /// <summary>For the whole process (notifications, jump lists). Call before any window is shown.</summary>
    public static void ForProcess(string id)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(id);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    /// <summary>For one window: its id, and what a pin of it starts.</summary>
    public static void ForWindow(IntPtr handle, string id, string relaunchArguments, string displayName)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(handle, ref iid, out var store) != 0 || store is null)
                return;
            try
            {
                var exe = Environment.ProcessPath!;
                // The relaunch details first, the id last: Windows reads them when the id is set.
                Set(store, 2, $"\"{exe}\" {relaunchArguments}");
                Set(store, 3, $"{exe},0");
                Set(store, 4, displayName);
                Set(store, 5, id);
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            Log.Warning($"Taskbar identity: {error.Message}");
        }
    }

    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static void Set(IPropertyStore store, uint id, string value)
    {
        var key = new PropertyKey { FormatId = AppUserModel, PropertyId = id };
        var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string id);
}

/// <summary>What a second launch of an open tool asked for (files, a search), handed to the open one through a small file.</summary>
public static class ToolRequest
{
    private static string FileFor(string key) =>
        Path.Combine(Path.GetTempPath(), $"PCButler{CrossProcess.Scope}-request-{key}.json");

    public static void Write(string key, string[] arguments)
    {
        try
        {
            File.WriteAllText(FileFor(key), JsonSerializer.Serialize(arguments));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string[]? Take(string key)
    {
        var file = FileFor(key);
        try
        {
            if (!File.Exists(file))
                return null;
            var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file));
            File.Delete(file);
            return arguments;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
