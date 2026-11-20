// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCButler.Core;

/// <summary>
/// Starting PC Butler's processes: the board (PCButler.exe), a tool (its own PCButler.&lt;Tool&gt;.exe,
/// or PCButler.exe --tool), the background agent (PCButler.Agent.exe, no WPF) and the companion
/// (PCButler.exe --ui: the windows the agent shows, and Courier).
/// </summary>
public static class Launcher
{
    /// <summary>The quick search box and command palette (PCButler.QuickSearch.exe, or PCButler.exe --popup).</summary>
    public const string Popup = "popup";

    /// <summary>The program file of each tool: a copy of PCButler.exe under the tool's name, so Task Manager tells them apart.</summary>
    public static readonly Dictionary<string, string> ExeNames = new()
    {
        ["search"] = "Search", ["preview"] = "Sorter", ["duplicates"] = "Duplicates", ["storage"] = "Storage",
        ["cleaner"] = "Cleaner", ["programs"] = "Programs", ["shredder"] = "Shredder", ["booster"] = "Booster",
        ["benchmark"] = "Benchmark", ["internet"] = "Internet", ["lan"] = "ScanLAN", ["clipboard"] = "Clipboard",
        ["vault"] = "Vault", ["history"] = "History", ["settings"] = "Settings", ["about"] = "Help", [Popup] = "QuickSearch",
    };

    /// <summary>PCButler.Agent.exe beside this program, when it is there.</summary>
    public static string AgentExe => Path.Combine(AppContext.BaseDirectory, "PCButler.Agent.exe");

    /// <summary>The tool's own program file when it is there, else PCButler.exe (which opens it with --tool).</summary>
    public static string ExeOf(string key)
    {
        if (ExeNames.TryGetValue(key, out var name))
        {
            var own = Path.Combine(AppContext.BaseDirectory, $"PCButler.{name}.exe");
            if (File.Exists(own))
                return own;
        }
        return AppInfo.MainExe;
    }

    /// <summary>The tool a program file belongs to ("PCButler.Search.exe" is "search"), or null.</summary>
    public static string? KeyOfExe(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        if (!name.StartsWith("PCButler.", StringComparison.OrdinalIgnoreCase))
            return null;
        var suffix = name["PCButler.".Length..];
        return ExeNames.FirstOrDefault(pair => pair.Value.Equals(suffix, StringComparison.OrdinalIgnoreCase)).Key;
    }

    /// <summary>Open a tool in a process of its own (or bring its window forward if it is open already).</summary>
    public static void Open(string key, params string[] extra)
    {
        if (key == "home")
        {
            OpenBoard();
            return;
        }
        Start(["--tool", key, .. extra], ExeOf(key));
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

    /// <summary>Open the search box (or the command palette) over whatever program is in front.</summary>
    public static void OpenPopup(bool palette) => Start(palette ? ["--popup", "palette"] : ["--popup"], ExeOf(Popup));

    /// <summary>Start the background agent, unless it is running already (it checks that itself too).</summary>
    public static void StartAgent()
    {
        if (Link.Agent.Running)
            return;
        if (File.Exists(AgentExe))
            Start([], AgentExe);
        else
            Log.Warning("PCButler.Agent.exe is missing: the background features cannot run");
    }

    /// <summary>Start the companion (the agent's windows and Courier), with what it is to show first.</summary>
    public static void StartCompanion(params string[] extra) => Start(["--ui", .. extra]);

    public static void Start(string[] arguments, string? program = null)
    {
        try
        {
            // The new window may come to the front (a tray click or a shortcut gave this process that right).
            AllowSetForegroundWindow(-1);
            var start = new ProcessStartInfo(program ?? AppInfo.MainExe) { UseShellExecute = false };
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

/// <summary>The shortcuts' defaults: what an action has until the user changes it ("" once cleared).</summary>
public static class HotkeyDefaults
{
    public static readonly Dictionary<string, string> Defaults = new() { ["search"] = "Win+F", ["palette"] = "Alt+Space" };

    /// <summary>The saved shortcuts with the defaults filled in.</summary>
    public static Dictionary<string, string> Effective(Dictionary<string, string> saved)
    {
        var all = new Dictionary<string, string>(saved);
        foreach (var (action, keys) in Defaults)
            all.TryAdd(action, keys);
        return all;
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
        if (config.Portal.ShareClipboard && CourierOn(config))
            on.Add("Clipboard sharing");
        if (HotkeyDefaults.Effective(config.Hotkeys).Values.Any(keys => keys.Length > 0))
            on.Add("Hotkeys");
        if (CourierOn(config))
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

    /// <summary>Courier starts by itself (switched on once, and not set to wait for a click).</summary>
    public static bool CourierOn(Config config) => config.Portal.AutoStart && (config.Portal.Used || config.Portal.Role.Length > 0);

    /// <summary>
    /// Whether the companion (PCButler.exe --ui) has to run all the time: Courier, the drop zone
    /// and the overlay live there, since they show windows. Otherwise it starts only to show something.
    /// </summary>
    public static bool Companion(Config config) => CourierOn(config) || config.DropZone || config.Overlay.Enabled;
}

/// <summary>
/// A named pipe to one of PC Butler's long-running processes: "PCButler.Agent" (the background
/// agent) or "PCButler.Ui" (the companion). One JSON line each way:
/// {"cmd":"config-changed","data":"…"} answered with {"ok":true,"reply":"…"}.
/// </summary>
public sealed class Link(string name)
{
    public static readonly Link Agent = new("Agent");
    public static readonly Link Ui = new("Ui");

    private readonly string pipeName = "PCButler." + name + CrossProcess.Scope;
    /// <summary>Held by the process for as long as it runs.</summary>
    public readonly string MutexName = @"Local\PCButler." + name + CrossProcess.Scope;

    /// <summary>True while that process runs.</summary>
    public bool Running
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

    /// <summary>Ask it something. Null when it did not answer in time.</summary>
    public string? Send(string command, int timeoutMs = 1500, string data = "")
    {
        if (!Running)
            return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
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

    /// <summary>The process's own side: answer each connection with handle(command, data), on a thread of the pipe's.</summary>
    public void Serve(Func<string, string, string> handle, CancellationToken stop)
    {
        var thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    var line = reader.ReadLine();
                    var request = line is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(line);
                    var reply = request is not null && request.TryGetValue("cmd", out var command)
                        ? handle(command, request.GetValueOrDefault("data", "")) : "unknown";
                    writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["reply"] = reply }));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log.Seldom(pipeName, TimeSpan.FromHours(1), $"WARNING {pipeName} pipe: {error.Message}");
                    Thread.Sleep(200);
                }
            }
        }) { IsBackground = true, Name = pipeName + " pipe" };
        thread.Start();
    }
}

/// <summary>The board's and the tools' way to the background agent.</summary>
public static class AgentLink
{
    public static bool Running => Link.Agent.Running;

    public static string? Send(string command, int timeoutMs = 1500, string data = "") => Link.Agent.Send(command, timeoutMs, data);

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
                Launcher.StartAgent();
        });
    }
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

/// <summary>Giving memory back to Windows while a process waits in the background.</summary>
public static class Memory
{
    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}

/// <summary>A notification for the tray icon, sent to the agent by a process that has none (the companion).</summary>
public sealed record TrayNotice(string Text, string Path = "");
