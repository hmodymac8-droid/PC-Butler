// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Text.Json;
using PCButler.Core;
using static PCButler.Agent.Native;

namespace PCButler.Agent;

/// <summary>
/// PC Butler's background agent: the tray icon, the shortcuts, clipboard history's recording,
/// automatic sorting and update checks, the usage monitor and its alerts, automatic Boost, the
/// usage and internet records, and Resume yesterday. No WPF: what has to appear on screen (the
/// drop zone, Courier, the overlay, the clipboard list, the report after a sort) is shown by the
/// companion, PCButler.exe --ui, which it starts when one of those is needed.
/// </summary>
internal sealed class Agent
{
    private readonly MessageLoop loop = new();
    private readonly CancellationTokenSource stop = new();
    private readonly BackgroundService background = new();
    private readonly UsageMonitor usage = new();
    private readonly Dictionary<string, string> applied = [];
    private Tray tray = null!;
    private Shortcuts shortcuts = null!;
    private ClipRecorder clipboard = null!;
    private FileSystemWatcher? watcher;
    private nuint? reapplyTimer;
    private string trayState = "";
    private string? receivedPath;
    private bool exiting;

    public void Run()
    {
        SynchronizationContext.SetSynchronizationContext(loop);
        var config = ConfigStore.Load();
        Tr.Language = config.EffectiveLanguage();
        Log.Info($"Agent started: {string.Join(", ", AgentNeeds.Of(config))}");
        // If the program was closed the hard way while Boost was on (and no Booster has it now).
        if (!ToolRunning("booster"))
            BoostMode.Recover();
        // Start-up entries of older versions ("PCButler.exe --background") are written again for this program.
        if (Autostart.IsEnabled() && AppInfo.IsInstalled)
        {
            try
            {
                Autostart.Set(true);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Warning($"Could not change the start at sign-in: {error.Message}");
            }
        }
        if (AppInfo.IsInstalled)
            ExplorerMenu.Sync(config.ExplorerMenu, AppInfo.MainExe, english => Tr.T(english));

        tray = new Tray(loop) { Clicked = Launcher.OpenBoard, Menu = Menu, NoticeClicked = OpenReceived, NoticeGone = () => receivedPath = null };
        clipboard = new ClipRecorder(loop);
        clipboard.Apply(config.Clipboard);
        shortcuts = new Shortcuts(loop, RunHotkey, ShowPicker);
        foreach (var (action, problem) in shortcuts.Apply(config.Hotkeys))
            Log.Warning($"Hotkey for {action}: {problem}");
        if (shortcuts.ApplyClipboard(config.Clipboard) is { } clipboardError)
            Log.Warning($"Clipboard manager: {clipboardError}");
        // Windows is about to shut down or restart: note once more what is open, for Resume yesterday.
        loop.Listen((message, _, _) =>
        {
            if (message == WM_QUERYENDSESSION && ConfigStore.Load().ResumeSession)
                SessionRestore.Freeze();
            return false;
        });
        StartBackground();
        RecordUsage(config.Usage.Record);
        TrackInternet(config.TrackInternet);
        StartResume();
        RestoreIndexing();
        Remember(config);
        WatchConfig();
        Link.Agent.Serve(Command, stop.Token);
        Companion(config);
        // The companion is checked on now and then (started again if it ended the hard way), and the
        // memory the agent no longer uses goes back to Windows.
        loop.After(TimeSpan.FromSeconds(30), Memory.Trim);
        loop.Every(TimeSpan.FromMinutes(1), () =>
        {
            if (!exiting)
                Companion(ConfigStore.Load());
        });
        loop.Every(TimeSpan.FromMinutes(5), Memory.Trim);
        loop.Run();
    }

    private static bool ToolRunning(string key)
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Local\PCButler.Tool." + key + CrossProcess.Scope, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Start the companion when something it runs is on (Courier, the drop zone, the overlay).</summary>
    private static void Companion(Config config)
    {
        if (AgentNeeds.Companion(config) && !Link.Ui.Running)
            Launcher.StartCompanion();
    }

    // --- notifications and messages ---

    private void Notify(string message, params object[] arguments) => tray.Notify(Tr.T(message, arguments));

    private void OpenReceived()
    {
        if (receivedPath is not { } path)
            return;
        receivedPath = null;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private static void Report(string message, List<string> errors)
    {
        var text = errors.Count == 0 ? message : message + "\n\n" + string.Join("\n", errors.Take(15));
        var flags = 0x30u /* MB_ICONWARNING */ | 0x40000u /* MB_TOPMOST */ | (Tr.IsArabic ? 0x100000u | 0x80000u : 0); // RTL reading, right-aligned
        Task.Run(() => MessageBox(IntPtr.Zero, text, AppInfo.Name, flags));
    }

    // --- the tray menu ---

    private List<MenuEntry> Menu()
    {
        var config = ConfigStore.Load();
        var entries = new List<MenuEntry> { new(Tr.T($"Open {AppInfo.Name}"), Launcher.OpenBoard, Bold: true), MenuEntry.Separator };
        if (trayState.Length > 0)
            entries.Add(new MenuEntry(trayState, null, Enabled: false));
        if (usage.Latest is { } sample)
            entries.Add(new MenuEntry(Tr.T("Processor {0}%  ·  Memory {1}%", sample.Cpu, sample.MemoryPercent), null, Enabled: false));
        if (entries.Count > 2)
            entries.Add(MenuEntry.Separator);
        entries.Add(new(Tr.T("Automatic sorting"), () => SetFeature("sort", !config.AutoSortOn), config.AutoSortOn));
        entries.Add(new(Tr.T("Clipboard history"), () => SetFeature("clipboard", !config.Clipboard.Enabled), config.Clipboard.Enabled));
        entries.Add(new(Tr.T("Performance overlay"), () => SetFeature("overlay", !config.Overlay.Enabled), config.Overlay.Enabled));
        entries.Add(new(Tr.T("Courier"), () => SetFeature("courier", !AgentNeeds.CourierOn(config)), AgentNeeds.CourierOn(config)));
        entries.Add(new(Tr.T("Boost"), () => SetFeature("boost", BoostMode.Active is null), BoostMode.Active is not null));
        entries.Add(new(Tr.T("Drop zone"), () => SetFeature("dropzone", !config.DropZone), config.DropZone));
        entries.Add(new(Tr.T("Usage alerts"), () => SetFeature("alerts", !config.UsageAlerts), config.UsageAlerts));
        entries.Add(new(Tr.T("Start with Windows"), () => SetFeature("autostart", !Autostart.IsEnabled()), Autostart.IsEnabled()));
        entries.Add(new(Tr.T("Resume yesterday"), () => SetFeature("resume", !config.ResumeSession), config.ResumeSession));
        entries.Add(MenuEntry.Separator);
        entries.Add(new(Tr.T("Sort now"), background.RequestSort));
        if (config.ResumeSession && SessionRestore.Previous() is not null)
            entries.Add(new(Tr.T("Open again what was open before the restart"), ResumeYesterday));
        if (background.Update is { } update)
            entries.Add(new(Tr.T("Install version {0}", update.Version), () => Task.Run(() => InstallUpdate(update))));
        entries.Add(MenuEntry.Separator);
        entries.Add(new(Tr.T("Exit"), Exit));
        return entries;
    }

    private static void ChangeConfig(Action<Config> change)
    {
        var config = ConfigStore.Load();
        change(config);
        ConfigStore.Save(config);
    }

    /// <summary>Switch a background feature from the tray menu (the same setting its own switch changes).</summary>
    private void SetFeature(string key, bool on)
    {
        switch (key)
        {
            case "sort" when on:
                // Turning it on moves files, so the Sorter asks first, as its own switch does.
                Launcher.Open("preview", "--auto-sort");
                break;
            case "sort":
                ChangeConfig(config => config.AutoSort = false);
                break;
            case "clipboard":
                ChangeConfig(config => config.Clipboard.Enabled = on);
                break;
            case "overlay":
                ChangeConfig(config => config.Overlay.Enabled = on);
                break;
            case "courier":
                ChangeConfig(config =>
                {
                    config.Portal.AutoStart = on;
                    if (on)
                        config.Portal.Used = true;
                });
                break;
            case "boost":
                if (on != (BoostMode.Active is not null))
                    ToggleBoost();
                break;
            case "dropzone":
                ChangeConfig(config => config.DropZone = on);
                break;
            case "alerts":
                ChangeConfig(config => config.UsageAlerts = on);
                break;
            case "autostart":
                try
                {
                    Autostart.Set(on);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    Log.Warning($"Could not change the start at sign-in: {error.Message}");
                }
                break;
            case "resume":
                ChangeConfig(config => config.ResumeSession = on);
                if (!on)
                    SessionRestore.Forget();
                break;
        }
        Reapply();
    }

    // --- the shortcuts ---

    private void RunHotkey(string action)
    {
        if (exiting)
            return;
        switch (action)
        {
            case "open":
                Launcher.OpenBoard();
                break;
            case "palette":
            case "search":
                // In a small process of its own that loads the saved index (it is not kept here).
                Launcher.OpenPopup(palette: action == "palette");
                break;
            case "searchpage":
                Launcher.Open("search");
                break;
            case "sort":
                background.RequestSort();
                break;
            case "dropzone":
                SetFeature("dropzone", !ConfigStore.Load().DropZone);
                break;
            case "courier":
                Launcher.Open("portal");
                break;
            case "overlay":
                var on = !ConfigStore.Load().Overlay.Enabled;
                SetFeature("overlay", on);
                Notify(on ? "The performance overlay is on." : "The performance overlay is off.");
                break;
            case "boost":
                ToggleBoost();
                break;
        }
    }

    /// <summary>The clipboard list, for the program in front (the companion shows it and pastes into that program).</summary>
    private static void ShowPicker()
    {
        var target = GetForegroundWindow().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        AllowSetForegroundWindow(-1);
        if (Link.Ui.Running && Link.Ui.Send("show-picker", 2000, target) is not null)
            return;
        Launcher.StartCompanion("--picker", target);
    }

    /// <summary>Boost the program that is in front; done again, everything goes back.</summary>
    private void ToggleBoost()
    {
        var front = GameDetector.Foreground();
        var settings = ConfigStore.Load().Boost;
        Task.Run(() =>
        {
            if (BoostMode.Active is not null)
            {
                BoostMode.Stop();
                loop.Post(() => Notify("Boost is off: everything is back as it was."));
                return;
            }
            var state = BoostMode.Start(settings, front is null || front.Pid == Environment.ProcessId ? 0 : front.Pid);
            loop.Post(() =>
            {
                if (state.Favoured.Length > 0)
                    Notify("Boost is on for {0}.", state.Favoured);
                else
                    Notify("Boost is on.");
            });
        });
    }

    // --- the pipe: the board, the tools and the companion ---

    private string Command(string command, string data)
    {
        switch (command)
        {
            case "ping":
                return "agent";
            case "hotkey":
                loop.Post(() => RunHotkey(data));
                return "ok";
            case "apply-hotkeys":
                // The Settings tool asks for its shortcuts now, to show at once which could not be used.
                return loop.Invoke(() =>
                {
                    var wanted = JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? [];
                    var problems = shortcuts.Apply(wanted);
                    applied["hotkeys"] = JsonSerializer.Serialize(wanted);
                    return JsonSerializer.Serialize(problems);
                });
            case "apply-clipboard":
                return loop.Invoke(() =>
                {
                    var wanted = JsonSerializer.Deserialize<ClipboardSettings>(data) ?? new ClipboardSettings();
                    clipboard.Apply(wanted);
                    var problem = shortcuts.ApplyClipboard(wanted);
                    applied["clipboard"] = JsonSerializer.Serialize(wanted);
                    return problem ?? "";
                });
            case "update":
                return background.Update?.Version ?? "";
            case "notify":
                loop.Post(() =>
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<TrayNotice>(data) is { } notice)
                        {
                            tray.Notify(notice.Text);
                            if (notice.Path.Length > 0)
                                receivedPath = notice.Path;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                });
                return "ok";
            case "config-changed":
                loop.Post(QueueReapply);
                return "ok";
            case "exit":
                loop.Post(Exit);
                return "ok";
            default:
                return "unknown";
        }
    }

    // --- settings changed by a tool ---

    private void WatchConfig()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            watcher = new FileSystemWatcher(AppInfo.DataDir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => loop.Post(QueueReapply);
            watcher.Created += (_, _) => loop.Post(QueueReapply);
            watcher.Renamed += (_, _) => loop.Post(QueueReapply);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warning($"Agent: cannot watch the settings file: {error.Message}");
        }
    }

    private void QueueReapply()
    {
        if (exiting)
            return;
        if (reapplyTimer is { } running)
            loop.Stop(running);
        reapplyTimer = loop.Every(TimeSpan.FromMilliseconds(600), () =>
        {
            if (reapplyTimer is { } timer)
                loop.Stop(timer);
            reapplyTimer = null;
            Reapply();
        });
    }

    private void Remember(Config config)
    {
        applied["clipboard"] = JsonSerializer.Serialize(config.Clipboard);
        applied["hotkeys"] = JsonSerializer.Serialize(config.Hotkeys);
        applied["resume"] = config.ResumeSession.ToString();
        applied["language"] = config.EffectiveLanguage();
    }

    private bool Changed(string key, string now) => !applied.TryGetValue(key, out var before) || before != now;

    /// <summary>The settings changed on disk: switch the background features to match, or close when none is left.</summary>
    private void Reapply()
    {
        if (exiting)
            return;
        var config = ConfigStore.Load();
        if (!AgentNeeds.Any(config))
        {
            Log.Info("Agent: every background feature is off now; closing");
            Exit();
            return;
        }
        if (Changed("language", config.EffectiveLanguage()))
            Tr.Language = config.EffectiveLanguage();
        if (Changed("clipboard", JsonSerializer.Serialize(config.Clipboard)))
        {
            clipboard.Apply(config.Clipboard);
            if (shortcuts.ApplyClipboard(config.Clipboard) is { } clipboardError)
                Log.Warning($"Clipboard manager: {clipboardError}");
        }
        if (Changed("hotkeys", JsonSerializer.Serialize(config.Hotkeys)))
            foreach (var (action, problem) in shortcuts.Apply(config.Hotkeys))
                Log.Warning($"Hotkey for {action}: {problem}");
        TrackInternet(config.TrackInternet);
        RecordUsage(config.Usage.Record);
        if (Changed("resume", config.ResumeSession.ToString()))
        {
            if (config.ResumeSession)
                SessionRestore.StartOfRun();
            RememberSession(config.ResumeSession);
        }
        Remember(config);
        Companion(config);
        if (Link.Ui.Running)
            Task.Run(() => Link.Ui.Send("config-changed"));
    }

    // --- automatic sorting, updates, the usage monitor ---

    private void StartBackground()
    {
        background.Notify += (message, arguments) => loop.Post(() => Notify(message, arguments));
        background.ReportWanted += () => loop.Post(() =>
        {
            if (!Link.Ui.Running || Link.Ui.Send("show-report", 2000) is null)
                Launcher.StartCompanion("--report");
        });
        background.StateChanged += (state, arguments) => loop.Post(() =>
        {
            trayState = Tr.T(state, arguments);
            ShowTip();
        });
        // The tray icon's tooltip doubles as a small monitor, and warns when the PC is under strain.
        usage.Updated += _ => loop.Post(ShowTip);
        usage.Alert += (message, arguments) => loop.Post(() =>
        {
            if (ConfigStore.Load().UsageAlerts)
                Notify(message, arguments);
        });
        // The same two-second tick watches for the programs that switch Boost on by themselves.
        var ticks = 0;
        usage.Updated += _ =>
        {
            if (++ticks % 3 != 0 || exiting)
                return;
            var change = BoostMode.AutoCheck(ConfigStore.Load().Boost);
            if (change is null)
                return;
            loop.Post(() =>
            {
                if (change.Length > 0)
                    Notify("Boost is on for {0}.", change);
                else
                    Notify("Boost is off: everything is back as it was.");
            });
        };
        usage.Start();
        background.WindowIsOpen = () => false;
        background.InstallUpdate = InstallUpdate;
        background.Start();
    }

    private void ShowTip()
    {
        var text = trayState.Length > 0 ? $"{AppInfo.Name}: {trayState}" : AppInfo.Name;
        if (usage.Latest is { } sample)
            text += "\n" + Tr.T("Processor {0}%  ·  Memory {1}%", sample.Cpu, sample.MemoryPercent);
        tray.SetTip(text);
    }

    /// <summary>Download the new version and hand over to its installer. Runs off the loop's thread.</summary>
    private void InstallUpdate(UpdateInfo update)
    {
        if (!AppInfo.IsInstalled)
        {
            // Not an installed copy: nothing to replace, so show the release page.
            Process.Start(new ProcessStartInfo(update.Page) { UseShellExecute = true })?.Dispose();
            return;
        }
        loop.Post(() => Notify("Downloading version {0}…", update.Version));
        try
        {
            var installer = Updater.Download(update);
            Log.Info($"Installing update {update.Version}");
            Updater.Apply(installer);
        }
        catch (Exception error) // a blocked or failed download must not take the agent down
        {
            Log.Warning($"Update failed: {error.Message}");
            loop.Post(() => Notify("The update could not be downloaded. It will be tried again later."));
            return;
        }
        loop.Post(Exit); // the installer needs the program closed
    }

    // --- the records of what each app uses and of internet use ---

    private AppUsageRecorder? usageRecorder;
    private nuint? usageWatch;
    private bool netAsked;
    private NetUsageRecorder? netTracker;

    private void RecordUsage(bool on)
    {
        if (on && usageRecorder is null)
        {
            usageRecorder = new AppUsageRecorder();
            usageWatch = loop.Every(TimeSpan.FromSeconds(10), WatchUsage);
        }
        else if (!on && usageRecorder is not null)
        {
            if (usageWatch is { } watch)
                loop.Stop(watch);
            usageWatch = null;
            usageRecorder.Dispose();
            usageRecorder = null;
            if (netAsked && AdminHelper.Running)
                Task.Run(() => AdminHelper.Send("net", ["off"], 10));
            netAsked = false;
        }
    }

    /// <summary>While the administrator helper runs, it is asked to count internet use per app too.</summary>
    private void WatchUsage()
    {
        var running = AdminHelper.Running;
        if (running && !netAsked && ConfigStore.Load().CountAppTraffic)
        {
            netAsked = true;
            Task.Run(() => AdminHelper.Send("net", ["on"], 10));
        }
        else if (!running)
            netAsked = false; // a helper started later is asked again
    }

    private void TrackInternet(bool on)
    {
        if (on && netTracker is null)
            netTracker = new NetUsageRecorder();
        else if (!on && netTracker is not null)
        {
            netTracker.Dispose();
            netTracker = null;
        }
    }

    // --- Resume yesterday ---

    private Timer? sessionTimer;

    private void StartResume()
    {
        if (!ConfigStore.Load().ResumeSession)
            return;
        SessionRestore.StartOfRun();
        RememberSession(true);
        if (SessionRestore.Previous() is null)
            return;
        // A little later, once Windows has settled after signing in.
        loop.After(TimeSpan.FromSeconds(15), () =>
        {
            if (!exiting && ConfigStore.Load().ResumeSession)
                ResumeYesterday();
        });
    }

    private void RememberSession(bool on)
    {
        sessionTimer?.Dispose();
        sessionTimer = null;
        if (on)
            sessionTimer = new Timer(_ =>
            {
                if (!exiting)
                    SessionRestore.Remember();
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
    }

    private void ResumeYesterday()
    {
        if (SessionRestore.Previous() is not { } previous)
            return;
        SessionRestore.Forget(previousOnly: true);
        Task.Run(() =>
        {
            var (opened, errors) = SessionRestore.Resume(previous);
            loop.Post(() =>
            {
                if (errors.Count > 0)
                    Report(Tr.T("Opened {0} again.", opened), errors);
                else
                    Notify("Opened {0} again.", opened);
            });
        });
    }

    // --- Search indexing switched off by an older Extreme tune-up ---

    /// <summary>
    /// Before 1.22.15, Extreme switched off Windows Search, which can stop Store apps from
    /// starting. When a preset still has it off, it is switched back on, once, without asking.
    /// </summary>
    private void RestoreIndexing()
    {
        if (ConfigStore.Load().IndexingBackRestored || TuneUp.ServiceStart("WSearch").Start != 4)
            return;
        string note;
        try
        {
            note = File.Exists(TuneUp.StatePath) ? File.ReadAllText(TuneUp.StatePath) : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            note = "";
        }
        if (!note.Contains("\"indexing\"", StringComparison.Ordinal))
        {
            Log.Info("Search indexing is off, but not by PC Butler: left as it is");
            return;
        }
        Log.Info("Search indexing was switched off by an older Extreme tune-up; switching it back on");
        Task.Run(async () =>
        {
            try
            {
                var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                                                 $"/c {sc} config WSearch start= delayed-auto & {sc} start WSearch")
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                };
                try
                {
                    using var process = Process.Start(start);
                    if (process is not null)
                        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Log.Warning("Search indexing stays off: the permission prompt was declined");
                    return;
                }
                if (TuneUp.ServiceStart("WSearch").Start == 4)
                {
                    Log.Warning("Search indexing stays off: Windows did not switch it on");
                    return;
                }
                TuneUp.Forget("indexing");
                Log.Info("Search indexing switched back on");
                loop.Post(() => Notify("Search indexing is back on, so Store apps such as Terminal and Photos start again."));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warning($"Search indexing stays off: {error.Message}");
            }
            finally
            {
                // Tried once, whatever came of it: the permission prompt is not shown at every start.
                loop.Post(() => ChangeConfig(config => config.IndexingBackRestored = true));
            }
        });
    }

    // --- leaving ---

    /// <summary>The tray's Exit (and the companion with it): stop everything and remove the icon.</summary>
    public void Exit()
    {
        if (exiting)
            return;
        exiting = true;
        stop.Cancel();
        watcher?.Dispose();
        background.Stop();
        usage.Stop();
        BoostMode.Stop(); // Boost only lasts while PC Butler is running
        RecordUsage(false);
        sessionTimer?.Dispose();
        TrackInternet(false);
        clipboard.Dispose();
        shortcuts.Dispose();
        if (Link.Ui.Running)
            Link.Ui.Send("exit", 2000);
        tray.Dispose();
        loop.Quit();
    }
}
