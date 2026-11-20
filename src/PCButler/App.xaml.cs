// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PCButler.Core;
using Forms = System.Windows.Forms;

namespace PCButler;

//// <summary>
/// One program file, several small processes (see AppMode):
///
///   PCButler.exe                 the floating board: pick a tool, and the board is gone
///   PCButler.exe --tool &lt;id&gt;     one tool in a window of its own (one process per tool)
///   PCButler.exe --agent         the tray icon and the background features that are switched on;
///                                with none on it does not stay at all (--background is the old name)
///   PCButler.exe --classic       the old all-in-one window, kept for now
///
/// Other arguments: --auto-on turns automatic sorting on first (the installer's option).
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private Forms.NotifyIcon? tray;
    private MainWindow? window;
    private BoardWindow? board;
    private ReportWindow? report;
    private bool exiting;

    public static new App Current => (App)Application.Current;
    /// <summary>What this process is: the board, the background agent, or one tool.</summary>
    public AppMode Mode { get; private set; } = AppMode.Board;
    /// <summary>True only in the background agent: the one process that records the clipboard, holds the shortcuts and so on.</summary>
    public bool RunsServices => Mode == AppMode.Agent;
    /// <summary>The tool this process shows (Mode is Tool); "home" for the classic window.</summary>
    public string ToolKey { get; private set; } = "";
    public BackgroundService Background { get; } = new();
    /// <summary>Processor and memory readings for the Home page and the tray icon.</summary>
    public UsageMonitor Usage { get; } = new();
    private ClipboardManager? clips;
    /// <summary>The clipboard manager: records copies while it is on (in the agent), and opens its list.</summary>
    public ClipboardManager Clips => clips ??= new ClipboardManager();
    private ClipboardShare? clipShare;
    /// <summary>Copied text shared with the PCs and phones connected through Courier.</summary>
    public ClipboardShare ClipShare => clipShare ??= new ClipboardShare();
    /// <summary>The performance overlay of the Benchmark page.</summary>
    public OverlayController Overlay { get; } = new();
    private Hotkeys? keys;
    /// <summary>The shortcuts of the Settings page's Hotkeys tab (registered in the agent only).</summary>
    public Hotkeys Keys => keys ??= new Hotkeys(RunHotkey, active: RunsServices);
    private PCButler.Core.Search.SearchEngine? search;
    /// <summary>
    /// File search. Never kept in the background agent: a tool that searches loads the saved
    /// index when it first asks, keeps it up to date while it is open, and saves it when it closes.
    /// </summary>
    public PCButler.Core.Search.SearchEngine Search
    {
        get
        {
            if (search is null)
            {
                search = new PCButler.Core.Search.SearchEngine();
                if (!RunsServices)
                    search.Start(ConfigStore.Load().Search);
            }
            return search;
        }
    }

    /// <summary>
    /// One search without keeping the index (the agent, when a phone asks Courier for a file by
    /// name): the saved index is read, searched and let go.
    /// </summary>
    public PCButler.Core.Search.SearchResult SearchOnce(string query)
    {
        if (search is not null || !RunsServices)
            return Search.Search(query, PCButler.Core.Search.SearchSort.Name, false, CancellationToken.None);
        var settings = ConfigStore.Load().Search;
        var loaded = PCButler.Core.Search.IndexStore.Load(PCButler.Core.Search.IndexStore.DefaultFile);
        var index = loaded?.Index ?? new PCButler.Core.Search.SearchIndex();
        return PCButler.Core.Search.Searcher.Run(index, query, PCButler.Core.Search.SearchSort.Name, false,
                                                 new PCButler.Core.Search.SearchOptions(settings.MatchCase, settings.MatchDiacritics), CancellationToken.None);
    }

    private SearchPopup? searchPopup;
    private string trayState = "";
    private bool firstStart;
    private DropZoneWindow? dropZone;
    /// <summary>The tool's window while it is open (null in the agent and the board).</summary>
    public MainWindow? Workspace => window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = e.Args;
        // Nothing saved yet means a first start: there is no update to announce (see ShowWhatsNew).
        firstStart = !File.Exists(AppInfo.ConfigFile);
        if (arguments.Length >= 3 && arguments[0] == "--sensors")
        {
            // The helper that reads temperatures as administrator (see Sensors): no window, no tray icon.
            Sensors.RunHelper(arguments[1], int.TryParse(arguments[2], out var parent) ? parent : 0);
            Shutdown();
            return;
        }
        if (arguments.Length >= 3 && arguments[0] == "--act")
        {
            // An item of File Explorer's "PC Butler" submenu. With several items selected, the
            // first copy gathers the others' paths and acts once; the rest just hand theirs over.
            Tr.Language = ConfigStore.Load().EffectiveLanguage();
            if (MenuBatch.Gather(arguments[1], arguments[2]) is { } batch)
                MenuActions.Run(arguments[1], batch);
            Shutdown();
            return;
        }
        // The single entry of versions before 1.9.1 (until the program starts and rewrites the menu).
        if (arguments.Length >= 2 && arguments[0] == "--process")
        {
            // File Explorer's right-click entry: one small window in a process of its own, whether or not PC Butler is running.
            Tr.Language = ConfigStore.Load().EffectiveLanguage();
            new ProcessWindow(arguments[1]).ShowDialog();
            Shutdown();
            return;
        }
        if (arguments.Contains("--auto-on"))
        {
            // The setup wizard's "sort automatically" option was ticked.
            var config = ConfigStore.Load();
            config.AutoSort = true;
            ConfigStore.Save(config);
        }

        var toolAt = Array.IndexOf(arguments, "--tool");
        if (arguments.Contains("--agent") || arguments.Contains("--background"))
            Mode = AppMode.Agent;
        else if (arguments.Contains("--classic"))
            (Mode, ToolKey) = (AppMode.Tool, "home");
        else if (arguments.Contains("--popup"))
            (Mode, ToolKey) = (AppMode.Tool, Tools.Popup); // the quick search box and command palette
        else if (toolAt >= 0 && toolAt + 1 < arguments.Length && Tools.IsTool(arguments[toolAt + 1]))
            (Mode, ToolKey) = (AppMode.Tool, arguments[toolAt + 1]);

        var scope = CrossProcess.Scope;
        var (instanceName, signalName) = Mode switch
        {
            AppMode.Agent => (AgentLink.MutexName, @"Local\PCButler.Agent.Show" + scope),
            AppMode.Tool => (ToolMutex(ToolKey), ToolMutex(ToolKey) + ".Show"),
            _ => (@"Local\PCButler.Board" + scope, @"Local\PCButler.Board.Show" + scope),
        };
        bool first;
        try
        {
            instance = new Mutex(true, instanceName, out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        }
        catch (UnauthorizedAccessException)
        {
            // A copy is already running with other rights (one started "as administrator"): Windows
            // won't let this one even look at it, so it can't be asked to show its window. Never a second copy.
            Shutdown();
            return;
        }
        if (!first)
        {
            // Already open: a second launch brings the board or the tool forward (with what it was asked to do).
            // A second agent has nothing to do.
            if (Mode == AppMode.Tool)
                ToolRequest.Write(ToolKey, arguments);
            if (Mode != AppMode.Agent)
            {
                AllowSetForegroundWindow(-1);
                showSignal.Set();
            }
            Shutdown();
            return;
        }

        AppInfo.ImportLegacyData();
        Tr.Language = ConfigStore.Load().EffectiveLanguage();
        DispatcherUnhandledException += OnUnhandledException;
        // Errors on other threads can't be shown, but they are logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // The same failure over and over (a dropped Courier link, say) is written once an hour, not each time.
            Log.Seldom("unobserved " + e.Exception.GetBaseException().Message, TimeSpan.FromHours(1), $"ERROR Unobserved: {e.Exception}");
            e.SetObserved();
        };
        var waiter = new Thread(WaitForShowSignal) { IsBackground = true, Name = "PC Butler show signal" };
        waiter.Start();

        switch (Mode)
        {
            case AppMode.Agent:
                StartAgentMode();
                break;
            case AppMode.Tool:
                StartToolMode(arguments);
                break;
            default:
                StartBoardMode();
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string ToolMutex(string key) => @"Local\PCButler.Tool." + key + CrossProcess.Scope;

    /// <summary>True while the window of that tool is open (in its own process).</summary>
    public static bool ToolRunning(string key)
    {
        try
        {
            if (!Mutex.TryOpenExisting(ToolMutex(key), out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // --- the board ---

    private void StartBoardMode()
    {
        // The background features that are switched on run in the agent: start it if it is not running
        // (after a sign-in with "start with Windows" off, say).
        if (!AgentLink.Running && AgentNeeds.Any(ConfigStore.Load()))
            Tools.StartAgent();
        board = new BoardWindow(firstStart);
        board.Closed += (_, _) =>
        {
            board = null;
            if (!exiting)
            {
                exiting = true;
                Shutdown();
            }
        };
        board.Show();
        board.Activate();
    }

    // --- one tool ---

    private bool reopening;

    private void StartToolMode(string[] arguments)
    {
        if (ToolKey == Tools.Popup)
        {
            StartPopupMode(arguments);
            return;
        }
        if (ToolKey == "benchmark")
            MirrorOverlay();
        var id = TaskbarIdentity.IdFor(ToolKey);
        TaskbarIdentity.ForProcess(id);
        // Anything this tool switches on or off reaches the background agent (or starts it).
        ConfigStore.Saved += () => Dispatcher.BeginInvoke(QueueConfigChanged);
        if (ToolKey is "benchmark" or "booster" or "home")
            Usage.Start(); // their live figures
        window = new MainWindow(ToolKey == "home" ? null : ToolKey);
        var opened = window;
        opened.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(opened).Handle;
            TaskbarIdentity.ForWindow(handle, id, ToolKey == "home" ? "--classic" : $"--tool {ToolKey}", opened.Title);
        };
        opened.Closed += (_, _) =>
        {
            window = null;
            ExitTool();
        };
        opened.Reveal();
        ToolArguments(arguments);
        if (ToolKey == "portal")
            BorrowCourier();
        Dispatcher.BeginInvoke(() =>
        {
            if (window is { } shown && IsOpen(shown))
            {
                if (ToolKey is "vault" or "home")
                    shown.TakeVaultInbox();
                if (ToolKey == "portal" && courierReady)
                    shown.TakeCourierInbox();
            }
        }, DispatcherPriority.ApplicationIdle);
        if (ToolKey == "home")
            ShowWhatsNew(opened);
    }

    /// <summary>What a launch asked the tool to do: files for its list, a search, the What's new list.</summary>
    private void ToolArguments(string[] arguments)
    {
        if (window is not { } shown)
            return;
        string? After(string name)
        {
            var at = Array.IndexOf(arguments, name);
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
        if (After("--paths") is { } list && Tools.TakeList(list) is { Count: > 0 } paths)
        {
            if (ToolKey == "shredder")
                shown.ShredLater(paths);
            else if (ToolKey == "portal")
                shown.SendWithPortal(paths);
        }
        if (After("--query") is { } query && ToolKey == "search")
            shown.SearchPage.SearchFor(query);
        if (arguments.Contains("--whats-new"))
            ShowWhatsNew(shown, always: true);
        if (arguments.Contains("--auto-sort"))
            shown.SetAutoSort(true);
        if (After("--tab") is { } tab && ToolKey == "settings")
            shown.Settings.ShowTab(tab);
        if (arguments.Contains("--scan") && ToolKey == "cleaner")
            shown.Cleaner.ScanNow();
        if (arguments.Contains("--check-updates") && ToolKey == "about")
            shown.About.CheckUpdates();
    }

    // --- the quick search box and the command palette (Win+F, Alt+Space) ---

    private DispatcherTimer? popupIdle;
    private long popupUsed;

    /// <summary>
    /// A small process for the search box over whatever program is in front. It loads the saved
    /// index when it starts, stays ready while it is used, and ends after ten idle minutes, so the
    /// index is not held all day.
    /// </summary>
    private void StartPopupMode(string[] arguments)
    {
        _ = Search; // loads the saved index now, while the box comes up
        OpenPopup(arguments);
        popupIdle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        popupIdle.Tick += (_, _) =>
        {
            if (searchPopup is { IsVisible: true })
                popupUsed = Environment.TickCount64;
            else if (Environment.TickCount64 - popupUsed > TimeSpan.FromMinutes(10).TotalMilliseconds)
                ExitTool();
        };
        popupIdle.Start();
    }

    private void OpenPopup(string[] arguments)
    {
        popupUsed = Environment.TickCount64;
        ShowSearchPopup(forCommands: arguments.Contains("palette"));
    }

    // --- the Benchmark tool shows the overlay the agent runs ---

    /// <summary>Every second while the Benchmark tool is open: the agent's overlay readings, copied here.</summary>
    private void MirrorOverlay()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var asking = false;
        timer.Tick += (_, _) =>
        {
            if (asking || exiting)
                return;
            asking = true;
            Task.Run(() => AgentLink.Send("overlay-state", 800)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                asking = false;
                Overlay.Mirror(task.Result);
            }));
        };
        timer.Start();
    }

    private DispatcherTimer? configTimer;

    /// <summary>Several saves in a row (a page writes a few settings at once) make one message to the agent.</summary>
    private void QueueConfigChanged()
    {
        if (configTimer is null)
        {
            configTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            configTimer.Tick += (_, _) =>
            {
                configTimer.Stop();
                AgentLink.ConfigChanged();
            };
        }
        configTimer.Stop();
        configTimer.Start();
    }

    /// <summary>The tool's window closed: so does its process.</summary>
    private void ExitTool()
    {
        if (exiting)
            return;
        exiting = true;
        if (configTimer is { IsEnabled: true })
        {
            configTimer.Stop();
            AgentLink.ConfigChanged();
        }
        if (BoostMode.Active is not null)
            BoostMode.Stop(); // a Boost started from the Booster lasts while it is open
        Usage.Stop();
        search?.Stop(); // saves the index, so the next search opens at once
        if (ToolKey == "portal" || Portal.Current is not null)
        {
            Portal.Current?.Stop().Wait(TimeSpan.FromSeconds(4));
            Portal.Current = null;
            // Courier goes back to the background agent, or the agent starts and takes it.
            if (AgentLink.Running)
                AgentLink.Send("courier-resume", 3000);
            else if (AgentNeeds.Any(ConfigStore.Load()))
                Tools.StartAgent();
        }
        if (reopening)
        {
            // Opened again (in another language): this process lets go of the tool before the new one starts.
            try
            {
                instance?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            instance?.Dispose();
            instance = null;
            if (ToolKey == "home")
                System.Diagnostics.Process.Start(Environment.ProcessPath!, "--classic")?.Dispose();
            else
                Tools.Open(ToolKey);
        }
        showSignal?.Set();
        Shutdown();
    }

    private bool courierReady;

    /// <summary>
    /// The Courier tool runs Courier itself while it is open, so its page shows the connections
    /// live: the agent stops its own first and starts it again when the tool closes.
    /// </summary>
    private void BorrowCourier()
    {
        Task.Run(() =>
        {
            var reply = AgentLink.Send("courier-release", 8000);
            var settings = ConfigStore.Load().Portal;
            var start = reply == "released" || (reply is null && settings.AutoStart && (settings.Used || settings.Role.Length > 0));
            Dispatcher.BeginInvoke(async () =>
            {
                if (start && !exiting)
                    await Courier.Start();
                courierReady = true;
                if (!exiting && window is { } shown)
                {
                    shown.ShowSwitches();
                    shown.TakeCourierInbox();
                }
            });
        });
    }

    // --- the background agent ---

    private void StartAgentMode()
    {
        var config = ConfigStore.Load();
        if (!AgentNeeds.Any(config))
        {
            // Nothing that needs to run in the background is switched on: no process at all.
            Log.Info("Agent: no background feature is on, so nothing stays running");
            exiting = true;
            Shutdown();
            return;
        }
        // If the program was closed the hard way while Boost was on (and no Booster has it now).
        if (!ToolRunning("booster"))
            BoostMode.Recover();
        Log.Info($"Agent started: {string.Join(", ", AgentNeeds.Of(config))}");
        // Windows is about to shut down or restart: note once more what is open, for Resume yesterday.
        // (Windows' own notice, which reaches a program that only has a tray icon open too.)
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
        {
            if (ConfigStore.Load().ResumeSession)
                SessionRestore.Freeze();
        };
        // Older versions started with Windows as "--background": the entry is written again as "--agent".
        if (Autostart.IsEnabled() && AppInfo.IsInstalled)
            PCButler.MainWindow.SetAutostart(true);
        Dispatcher.BeginInvoke(FinishStartup, DispatcherPriority.Background);
    }

    /// <summary>The agent's features, once the dispatcher is running.</summary>
    private void FinishStartup()
    {
        if (exiting)
            return;
        var config = ConfigStore.Load();
        SyncExplorerMenu(config.ExplorerMenu);
        if (config.DropZone)
            ShowDropZone(true);

        StartTray();
        if (Clips.Apply(config.Clipboard) is { } clipboardError)
            Log.Warning($"Clipboard manager: {clipboardError}");
        ClipShare.Apply(config.Portal.ShareClipboard);
        foreach (var (action, problem) in Keys.Apply(config.Hotkeys))
            Log.Warning($"Hotkey for {action}: {problem}");
        Overlay.Apply(config);
        StartBackground();
        StartCourier();
        RecordUsage(config.Usage.Record);
        StartResume();
        RestoreIndexing();
        TrackInternet(config.TrackInternet);
        Remember(config);
        WatchConfig();
        AgentLink.Serve(AgentCommand, agentStop.Token);
    }

    private readonly CancellationTokenSource agentStop = new();
    private FileSystemWatcher? configWatcher;
    private DispatcherTimer? reapplyTimer;
    private readonly Dictionary<string, string> applied = [];
    private bool courierLent;

    /// <summary>A tool (or anything else) wrote config.json: apply what changed, a moment later.</summary>
    private void WatchConfig()
    {
        reapplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        reapplyTimer.Tick += (_, _) =>
        {
            reapplyTimer.Stop();
            Reapply();
        };
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            configWatcher = new FileSystemWatcher(AppInfo.DataDir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            configWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(QueueReapply);
            configWatcher.Created += (_, _) => Dispatcher.BeginInvoke(QueueReapply);
            configWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(QueueReapply);
            configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warning($"Agent: cannot watch the settings file: {error.Message}");
        }
    }

    private void QueueReapply()
    {
        if (exiting || reapplyTimer is null)
            return;
        reapplyTimer.Stop();
        reapplyTimer.Start();
    }

    /// <summary>One request from the board or a tool, over the agent's pipe (on the pipe's thread).</summary>
    private string AgentCommand(string command, string data)
    {
        switch (command)
        {
            case "ping":
                return "agent";
            case "hotkey":
                Dispatcher.BeginInvoke(() => RunHotkey(data));
                return "ok";
            case "apply-hotkeys":
                // The Settings tool asks for its shortcuts now, to show at once which could not be used.
                return Dispatcher.Invoke(() =>
                {
                    var wanted = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? [];
                    var problems = Keys.Apply(wanted);
                    applied["hotkeys"] = Snapshot(wanted);
                    return System.Text.Json.JsonSerializer.Serialize(problems);
                });
            case "apply-clipboard":
                return Dispatcher.Invoke(() =>
                {
                    var wanted = System.Text.Json.JsonSerializer.Deserialize<ClipboardSettings>(data) ?? new ClipboardSettings();
                    var problem = Clips.Apply(wanted);
                    applied["clipboard"] = Snapshot(wanted);
                    return problem ?? "";
                });
            case "overlay-state":
                return Dispatcher.Invoke(Overlay.State);
            case "update":
                return Background.Update?.Version ?? "";
            case "config-changed":
                Dispatcher.BeginInvoke(QueueReapply);
                return "ok";
            case "courier-release":
                var stopping = Dispatcher.Invoke(() =>
                {
                    courierLent = true;
                    return Courier.Node is null ? Task.CompletedTask : Courier.Stop();
                });
                stopping.Wait(TimeSpan.FromSeconds(6));
                return "released";
            case "courier-resume":
                Dispatcher.BeginInvoke(ResumeCourier);
                return "ok";
            case "exit":
                Dispatcher.BeginInvoke(ExitProgram);
                return "ok";
            default:
                return "unknown";
        }
    }

    /// <summary>The Courier tool closed: Courier runs here again, if it starts by itself.</summary>
    private void ResumeCourier()
    {
        if (!courierLent || exiting)
            return;
        courierLent = false;
        var settings = ConfigStore.Load().Portal;
        if (Courier.Node is null && settings.AutoStart && (settings.Used || settings.Role.Length > 0))
            _ = Courier.Start();
    }

    private static string Snapshot(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string CourierSnapshot(Config config) => $"{config.Portal.AutoStart}{config.Portal.Used}{config.Portal.Role}";

    private void Remember(Config config)
    {
        applied["clipboard"] = Snapshot(config.Clipboard);
        applied["hotkeys"] = Snapshot(config.Hotkeys);
        applied["overlay"] = Snapshot(config.Overlay) + Snapshot(config.Boost.AutoApps);
        applied["resume"] = config.ResumeSession.ToString();
        applied["courier"] = CourierSnapshot(config);
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
            ExitProgram();
            return;
        }
        if (Changed("language", config.EffectiveLanguage()))
            Tr.Language = config.EffectiveLanguage();
        if (Changed("clipboard", Snapshot(config.Clipboard)) && Clips.Apply(config.Clipboard) is { } clipboardError)
            Log.Warning($"Clipboard manager: {clipboardError}");
        ClipShare.Apply(config.Portal.ShareClipboard);
        if (Changed("hotkeys", Snapshot(config.Hotkeys)))
            foreach (var (action, problem) in Keys.Apply(config.Hotkeys))
                Log.Warning($"Hotkey for {action}: {problem}");
        if (Changed("overlay", Snapshot(config.Overlay) + Snapshot(config.Boost.AutoApps)))
            Overlay.Apply(config);
        TrackInternet(config.TrackInternet);
        RecordUsage(config.Usage.Record);
        if (Changed("resume", config.ResumeSession.ToString()))
        {
            if (config.ResumeSession)
                SessionRestore.StartOfRun();
            RememberSession(config.ResumeSession);
        }
        ShowDropZone(config.DropZone);
        Courier.WidgetOn = config.Portal.Widget;
        if (Changed("courier", CourierSnapshot(config)) && !courierLent
            && Courier.Node is null && config.Portal.AutoStart && (config.Portal.Used || config.Portal.Role.Length > 0))
            _ = Courier.Start();
        Remember(config);
    }

    private void WaitForShowSignal()
    {
        while (showSignal!.WaitOne())
        {
            if (exiting)
                return;
            // A second launch: bring the board or the tool forward.
            Dispatcher.BeginInvoke(() =>
            {
                if (Mode == AppMode.Board)
                    board?.Bring();
                else if (Mode == AppMode.Tool && ToolKey == Tools.Popup)
                    OpenPopup(ToolRequest.Take(ToolKey) ?? []);
                else if (Mode == AppMode.Tool)
                {
                    ShowMainWindow();
                    if (ToolRequest.Take(ToolKey) is { } asked)
                        ToolArguments(asked);
                }
            });
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception.ToString());
        Ui.Warn(AppInfo.Name, Ui.T("Something went wrong.") + "\n\n" + e.Exception.Message);
        e.Handled = true;
    }

    // --- the window ---

    /// <summary>
    /// "Open PC Butler": the board, from the agent and the board; in a tool, its own window comes forward.
    /// </summary>
    public void ShowMainWindow(bool splash = false)
    {
        if (exiting)
            return;
        if (Mode == AppMode.Agent)
        {
            Tools.OpenBoard();
            return;
        }
        if (Mode == AppMode.Board)
        {
            board?.Bring();
            return;
        }
        if (window is null)
            return;
        if (window.WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(window); // back to normal or maximized, as it was
        window.Show();
        window.Activate();
        if (ToolKey is "vault" or "home")
            window.TakeVaultInbox();
        if ((ToolKey == "portal" && courierReady) || ToolKey == "home")
            window.TakeCourierInbox();
    }

    /// <summary>
    /// Add or remove File Explorer's right-click entry. Only an installed copy adds it, so a
    /// copy run from a build folder never leaves an entry pointing at a file that may move.
    /// </summary>
    public static string? SyncExplorerMenu(bool wanted) =>
        !AppInfo.IsInstalled ? null
            : ExplorerMenu.Sync(wanted, Environment.ProcessPath!, english => Ui.T(english));

    private void ShowDropZone(bool show)
    {
        if (!RunsServices)
            return; // the drop zone lives in the background agent
        if (show && dropZone is null && !exiting)
        {
            dropZone = new DropZoneWindow();
            dropZone.Closed += (_, _) => dropZone = null;
            dropZone.Show();
        }
        else if (!show)
        {
            dropZone?.Close();
        }
    }

    /// <summary>Send files to another PC: on the Courier tool's list (opened for it when it is not the one open).</summary>
    public void SendWithPortal(IEnumerable<string> paths)
    {
        if (window is not null)
        {
            window.SendWithPortal(paths);
            return;
        }
        CourierInbox.Add(paths.ToList());
        Tools.Open("portal");
    }

    /// <summary>Put items on the Shredder's list, in its own window.</summary>
    public void ShredLater(IEnumerable<string> paths)
    {
        if (window is not null)
        {
            window.ShredLater(paths);
            return;
        }
        Tools.OpenWith("shredder", paths);
    }

    /// <summary>A shortcut from the Hotkeys tab was pressed, in whatever program.</summary>
    public void RunHotkey(string action)
    {
        if (exiting)
            return;
        if (!RunsServices && action is "sort" or "boost" or "overlay" or "dropzone")
        {
            // These act on what the background agent runs (from the command palette): it does them.
            Task.Run(() =>
            {
                if (AgentLink.Send("hotkey", 2000, action) is null && action != "sort")
                    Dispatcher.BeginInvoke(() => Ui.Info(AppInfo.Name, Ui.T("PC Butler is not running in the background. Switch a background feature on in Settings first.")));
            });
            return;
        }
        switch (action)
        {
            case "open":
                Tools.OpenBoard();
                break;
            case "palette":
            case "search":
                // In a small process of its own that loads the saved index (it is not kept in the agent).
                Tools.OpenPopup(palette: action == "palette");
                break;
            case "searchpage":
                Tools.Open("search");
                break;
            case "sort":
                Background.RequestSort();
                break;
            case "dropzone":
                SetDropZone(!ConfigStore.Load().DropZone);
                break;
            case "courier":
                Tools.Open("portal");
                break;
            case "overlay":
                var on = !ConfigStore.Load().Overlay.Enabled;
                SetFeature("overlay", on);
                Notify(on ? "The performance overlay is on." : "The performance overlay is off.", []);
                break;
            case "boost":
                ToggleBoost();
                break;
        }
    }

    /// <summary>
    /// The command palette (forCommands) or the file search box, over whatever program is in front.
    /// Built the first time and then only hidden, so it comes up at once; built again only when
    /// the language has changed.
    /// </summary>
    public void ShowSearchPopup(bool forCommands = false)
    {
        if (exiting)
            return;
        if (searchPopup is not null && searchPopup.MadeForLanguage != Tr.Language)
        {
            searchPopup.Close();
            searchPopup = null;
        }
        if (searchPopup is null)
        {
            var made = new SearchPopup();
            made.Closed += (_, _) =>
            {
                if (searchPopup == made)
                    searchPopup = null; // Alt+F4 closes it for good: the next press builds another
            };
            searchPopup = made;
        }
        searchPopup.Open(forCommands);
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
                Dispatcher.BeginInvoke(() => Notify("Boost is off: everything is back as it was.", []));
                return;
            }
            var state = BoostMode.Start(settings, front is null || front.Pid == Environment.ProcessId ? 0 : front.Pid);
            Dispatcher.BeginInvoke(() =>
            {
                if (state.Favoured.Length > 0)
                    Notify("Boost is on for {0}.", [state.Favoured]);
                else
                    Notify("Boost is on.", []);
            });
        });
    }

    // --- the background features, as the tray panel lists them ---

    /// <summary>Something PC Butler does in the background, its name on the tray tile, and whether it is on now.</summary>
    public sealed record Feature(string Key, string Name, string Glyph, bool On);

    /// <summary>Every background feature, each read from where it really lives (the settings, Windows, or the running part).</summary>
    public IReadOnlyList<Feature> Features()
    {
        var config = ConfigStore.Load();
        return
        [
            new("sort", "Automatic sorting", "\uE8FD", config.AutoSortOn),
            new("clipboard", "Clipboard history", "\uE77F", config.Clipboard.Enabled),
            new("overlay", "Performance overlay", "\uE7F4", config.Overlay.Enabled),
            new("courier", "Courier", "\uE8AB", Courier.Node is not null),
            new("boost", "Boost", "\uE945", BoostMode.Active is not null),
            new("dropzone", "Drop zone", "\uE8B7", config.DropZone),
            new("alerts", "Usage alerts", "\uE7BA", config.UsageAlerts),
            new("autostart", "Start with Windows", "\uE7E8", Autostart.IsEnabled()),
            new("resume", "Resume yesterday", "\uE81C", config.ResumeSession),
        ];
    }

    /// <summary>Switch a background feature on or off, the same way its own switch in the window does, and show it there.</summary>
    public async void SetFeature(string key, bool on)
    {
        if (exiting)
            return;
        switch (key)
        {
            case "sort" when on:
                // Turning it on moves files, so the Sorter asks first, as its own switch does.
                if (window is null)
                {
                    Tools.Open("preview", "--auto-sort");
                    break;
                }
                ShowMainWindow();
                window.ShowSwitches();
                window.SetAutoSort(true);
                break;
            case "sort":
                ChangeConfig(config => config.AutoSort = false);
                break;
            case "clipboard":
                ChangeConfig(config => config.Clipboard.Enabled = on);
                if (Clips.Apply(ConfigStore.Load().Clipboard) is { } clipboardError)
                    Ui.Warn("Clipboard", clipboardError);
                break;
            case "overlay":
                ChangeConfig(config => config.Overlay.Enabled = on);
                Overlay.Apply(ConfigStore.Load());
                break;
            case "courier" when on:
                // Not set up yet (no role, no host): its page is where that is done.
                if (await Courier.Start() is not null)
                    ShowPage("portal");
                break;
            case "courier":
                await Courier.Stop();
                break;
            case "boost":
                if (on != (BoostMode.Active is not null))
                    ToggleBoost();
                break;
            case "dropzone":
                SetDropZone(on);
                break;
            case "alerts":
                ChangeConfig(config => config.UsageAlerts = on);
                break;
            case "autostart":
                PCButler.MainWindow.SetAutostart(on);
                break;
            case "resume":
                SetResumeSession(on);
                break;
        }
        window?.ShowSwitches();
    }

    /// <summary>Change the saved settings, and the window's copy of them when it is open.</summary>
    private void ChangeConfig(Action<Config> change)
    {
        if (window is not null)
        {
            window.UpdateConfig(change);
            return;
        }
        var config = ConfigStore.Load();
        change(config);
        ConfigStore.Save(config);
    }

    /// <summary>Show or hide the drop zone and remember the choice.</summary>
    public void SetDropZone(bool show)
    {
        var config = ConfigStore.Load();
        config.DropZone = show;
        ConfigStore.Save(config);
        window?.UpdateConfig(saved => saved.DropZone = show);
        ShowDropZone(show);
        window?.ShowSwitches();
    }

    /// <summary>Once after each update, when the window first opens: the list of what changed.</summary>
    private void ShowWhatsNew(MainWindow owner, bool always = false)
    {
        var seen = ConfigStore.Load().LastSeenVersion;
        if (seen == AppInfo.Version && !always)
            return;
        owner.UpdateConfig(config => config.LastSeenVersion = AppInfo.Version);
        if (!always && !Changelog.ShouldShow(seen, firstStart))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsOpen(owner))
                new WhatsNewWindow { Owner = owner }.Show();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Close the window and open it again, e.g. in a new language.</summary>
    public void ReopenMainWindow()
    {
        if (window is not { } old)
            return;
        // The tool's process ends with its window, and a new one starts in its place.
        reopening = true;
        old.Quit();
        if (IsOpen(old))
            reopening = false; // the window is waiting for a job to finish
    }

    /// <summary>The report window after an automatic run. Only one is open at a time; it refreshes itself.</summary>
    public void ShowReport()
    {
        if (exiting)
            return;
        if (report is not null)
        {
            if (report.WindowState == WindowState.Minimized)
                SystemCommands.RestoreWindow(report);
            report.Activate();
            return;
        }
        report = new ReportWindow();
        report.Closed += (_, _) => report = null;
        report.Show();
    }

    /// <summary>False once a window has really closed (a refused close leaves it open).</summary>
    private static bool IsOpen(Window shown) => new System.Windows.Interop.WindowInteropHelper(shown).Handle != IntPtr.Zero;

    /// <summary>True while any window of the program is on screen.</summary>
    public bool WindowIsOpen() => Windows.OfType<Window>().Any(open => open.IsVisible);

    // --- the tray icon ---

    private void StartTray()
    {
        tray = new Forms.NotifyIcon { Text = AppInfo.Name, Visible = true };
        try
        {
            // The logo alone on a transparent background, like the program and window icons.
            using var stream = GetResourceStream(new Uri("pack://application:,,,/PCButler;component/logo.ico"))!.Stream;
            tray.Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception error) when (error is IOException or ArgumentException or NullReferenceException)
        {
            tray.Icon = System.Drawing.SystemIcons.Application;
        }
        // A click, left or right, opens the panel (a second click closes it); a double-click opens the
        // window itself. From the tray the program is already running, so there is no launch screen.
        // A click on a notification about files that arrived opens them where they are.
        tray.BalloonTipClicked += (_, _) =>
        {
            if (receivedPath is not { } path)
                return;
            if (File.Exists(path))
                Shell.Reveal(path);
            else if (Directory.Exists(path))
                Shell.Open(path);
        };
        tray.BalloonTipClosed += (_, _) => receivedPath = null;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                flyout?.Dismiss();
                Tools.OpenBoard();
            }
            else if (e.Button == Forms.MouseButtons.Right)
                ToggleFlyout();
        };
    }

    private TrayFlyout? flyout;

    /// <summary>Open the tray panel, or close it if it is open (or was closed by this very click).</summary>
    private void ToggleFlyout()
    {
        if (exiting)
            return;
        if (flyout is not null)
        {
            flyout.Dismiss();
            return;
        }
        // The click on the icon took the focus away, which already closed the panel: that click meant "close".
        if (Environment.TickCount64 - TrayFlyout.ClosedAt < 300)
            return;
        flyout = new TrayFlyout();
        flyout.Closed += (_, _) => flyout = null;
        flyout.Show();
    }

    /// <summary>Open the window at one of its pages.</summary>
    public void ShowPage(string key)
    {
        if (window is null)
        {
            // The agent and the board open the tool in a window of its own.
            Tools.Open(key);
            board?.Close();
            return;
        }
        ShowMainWindow();
        window.ShowPage(key);
    }

    /// <summary>The tray panel's update button.</summary>
    public void InstallAvailableUpdate()
    {
        if (Background.Update is { } update)
            Task.Run(() => InstallUpdate(update));
    }

    private void ShowTrayText()
    {
        if (tray is null)
            return;
        var text = $"{AppInfo.Name}: {trayState}";
        if (Usage.Latest is { } sample)
            text += "\n" + Ui.T("Processor {0}%  ·  Memory {1}%", sample.Cpu, sample.MemoryPercent);
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Another PC asks to pair or to send files: say so, and bring up the Portal page where the answer is given.</summary>
    public void PortalNeedsAttention(string message) => Dispatcher.BeginInvoke(() =>
    {
        if (exiting)
            return;
        Notify("{0}", [message]);
        // The widget asks right there; without it, the Courier page does. In the agent a pop-up asks (see WatchCourier).
        if (RunsServices)
        {
            widgetDismissed = false;
            WatchCourier();
            return;
        }
        ShowMainWindow();
        window?.ShowPage("portal");
    });

    // --- Search indexing switched off by an older Extreme tune-up ---

    /// <summary>
    /// Before 1.22.15, Extreme switched off Windows Search. With it off, Windows can fail to
    /// start Store apps (Terminal, Photos, Copilot, PowerToys' menu items), showing "Windows
    /// cannot find ...\WindowsApps\wt.exe" and the like. Indexing is now in no preset, so when a
    /// preset still has it switched off, it is switched back on by itself, once, without asking.
    /// </summary>
    private void RestoreIndexing()
    {
        if (ConfigStore.Load().IndexingBackRestored || TuneUp.ServiceStart("WSearch").Start != 4)
            return;
        // The service itself decides, not tuneup.json alone: a note from an older version may not read back.
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
                // One small command with administrator rights (Windows may show its permission prompt),
                // put back to Windows' own default: automatic, delayed, running. The helper is not
                // used: on some PCs it did not answer in time.
                var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                                                                    $"/c {sc} config WSearch start= delayed-auto & {sc} start WSearch")
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                try
                {
                    using var process = System.Diagnostics.Process.Start(start);
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
                _ = Dispatcher.BeginInvoke(() => Notify("Search indexing is back on, so Store apps such as Terminal and Photos start again.", []));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warning($"Search indexing stays off: {error.Message}");
            }
            finally
            {
                // Tried once, whatever came of it: the permission prompt is not shown at every start.
                _ = Dispatcher.BeginInvoke(() => ChangeConfig(config => config.IndexingBackRestored = true));
            }
        });
    }

    // --- Resume yesterday: the apps and folders that were open before Windows restarted ---

    private Timer? sessionTimer;

    /// <summary>
    /// At the start: put aside what was open before the restart, open it all again by itself (a
    /// little later, once Windows has settled after signing in), and start noting what is open now.
    /// </summary>
    private void StartResume()
    {
        if (!ConfigStore.Load().ResumeSession)
            return;
        SessionRestore.StartOfRun();
        RememberSession(true);
        if (SessionRestore.Previous() is null)
            return;
        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            if (!exiting && ConfigStore.Load().ResumeSession)
                ResumeYesterday();
        };
        later.Start();
    }

    /// <summary>Note what is open every minute, or stop.</summary>
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

    /// <summary>The switch in Settings. Switched off, what was noted is forgotten.</summary>
    public void SetResumeSession(bool on)
    {
        ChangeConfig(config => config.ResumeSession = on);
        if (on && RunsServices)
            SessionRestore.StartOfRun();
        if (RunsServices)
            RememberSession(on); // in a tool, the agent sees the switch in the saved settings
        if (!on)
            SessionRestore.Forget();
    }

    /// <summary>What there is to resume, while the feature is on.</summary>
    public SessionSnapshot? ResumeAvailable => ConfigStore.Load().ResumeSession ? SessionRestore.Previous() : null;

    /// <summary>"5 apps, 2 folders, 3 files", in the user's language.</summary>
    public static string ResumeSummary(SessionSnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Apps.Count > 0)
            parts.Add(Ui.T("{0} app(s)", snapshot.Apps.Count));
        if (snapshot.Folders.Count > 0)
            parts.Add(Ui.T("{0} folder(s)", snapshot.Folders.Count));
        if (snapshot.FileCount > 0)
            parts.Add(Ui.T("{0} file(s)", snapshot.FileCount));
        return string.Join(Tr.IsArabic ? "، " : ", ", parts);
    }

    /// <summary>Open again what was open before the restart (by itself at the start, or the tray panel's button). Done once.</summary>
    public void ResumeYesterday()
    {
        if (SessionRestore.Previous() is not { } previous)
            return;
        SessionRestore.Forget(previousOnly: true);
        Task.Run(() =>
        {
            var (opened, errors) = SessionRestore.Resume(previous);
            Dispatcher.BeginInvoke(() =>
            {
                if (errors.Count > 0)
                    Ui.ReportResult(Ui.T("Opened {0} again.", opened), errors);
                else
                    Notify("Opened {0} again.", [opened]);
            });
        });
    }

    // --- the Internet page's tracker ---

    /// <summary>Counts what goes over the network each day, while the tracker is on.</summary>
    public NetUsageRecorder? NetTracker { get; private set; }

    public void TrackInternet(bool on)
    {
        if (!RunsServices)
            return; // counted by the background agent, which reads the switch from the saved settings
        if (on && NetTracker is null)
            NetTracker = new NetUsageRecorder();
        else if (!on && NetTracker is not null)
        {
            NetTracker.Dispose();
            NetTracker = null;
        }
    }

    // --- the record of what each app uses (the Benchmark page's report) ---

    private AppUsageRecorder? usageRecorder;
    private readonly DispatcherTimer usageWatch = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool netAsked;
    private bool? countAppTraffic;

    /// <summary>Whether the helper is to count each app's traffic (the Internet page's Start and Stop).</summary>
    public bool CountingAppTraffic => countAppTraffic ??= ConfigStore.Load().CountAppTraffic;

    /// <summary>Start or stop counting each app's traffic; starting needs the helper running already.</summary>
    public Task CountAppTraffic(bool on)
    {
        countAppTraffic = on;
        netAsked = on && AdminHelper.Running;
        return AdminHelper.Running ? Task.Run(() => AdminHelper.Send("net", [on ? "on" : "off"], 10)) : Task.CompletedTask;
    }

    /// <summary>Record what each app uses, or stop. While the administrator helper runs, it is asked to count internet use per app too.</summary>
    public void RecordUsage(bool on)
    {
        if (!RunsServices)
            return; // recorded by the background agent, which reads the switch from the saved settings
        if (on && usageRecorder is null)
        {
            usageRecorder = new AppUsageRecorder();
            usageWatch.Tick -= WatchUsage;
            usageWatch.Tick += WatchUsage;
            usageWatch.Start();
        }
        else if (!on && usageRecorder is not null)
        {
            usageWatch.Stop();
            usageRecorder.Dispose();
            usageRecorder = null;
            if (netAsked && AdminHelper.Running)
                Task.Run(() => AdminHelper.Send("net", ["off"], 10));
            netAsked = false;
        }
    }

    private void WatchUsage(object? sender, EventArgs e)
    {
        var running = AdminHelper.Running;
        if (running && !netAsked && CountingAppTraffic)
        {
            netAsked = true;
            Task.Run(() => AdminHelper.Send("net", ["on"], 10));
        }
        else if (!running)
        {
            netAsked = false; // a helper started later is asked again
        }
    }

    /// <summary>Where the files of the last "received" notification are, for a click on it.</summary>
    private string? receivedPath;

    /// <summary>
    /// Files from another PC arrived: a Windows notification says so (it stays in the
    /// notification centre, so files that came while nobody watched are not missed), and a
    /// click on it shows them.
    /// </summary>
    /// <summary>Text from a PC or phone is on the clipboard now: a notification says so, with its start.</summary>
    public void ClipboardArrived(string peer, string text)
    {
        if (exiting)
            return;
        var start = text.ReplaceLineEndings(" ").Trim();
        if (start.Length > 80)
            start = start[..80] + "…";
        Notify("Text from {0} is on the clipboard: {1}", [peer, start]);
    }

    public void PortalReceived(PortalTransfer transfer) => Dispatcher.BeginInvoke(() =>
    {
        if (exiting)
            return;
        Notify(transfer.Files == 1 ? "Received {0} from {1}." : "Received {0} ({2} files) from {1}.", [transfer.Name, transfer.Peer, transfer.Files]);
        receivedPath = transfer.Path;
    });

    // --- Courier: started with the program, and its widget while PCs are connected ---

    private CourierWidget? courierWidget;
    private CourierNotices? courierNotices;
    private bool widgetDismissed, widgetWasBusy;
    private readonly DispatcherTimer courierWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private int lentChecks;

    private void StartCourier()
    {
        courierWatch.Tick += (_, _) => WatchCourier();
        courierWatch.Start();
        var settings = ConfigStore.Load().Portal;
        Courier.WidgetOn = settings.Widget;
        // Once Courier has been switched on here (before 1.22: once a role was chosen), it starts by itself: reachable, and connected to its PCs.
        if (settings.AutoStart && (settings.Used || settings.Role.Length > 0))
            _ = Courier.Start();
    }

    /// <summary>Show the widget while something is going on (a PC connected, a transfer, a question), hide it otherwise.</summary>
    private void WatchCourier()
    {
        if (exiting)
            return;
        // Lent to the Courier tool, which then closed the hard way: Courier comes back here.
        if (courierLent && ++lentChecks % 5 == 0 && !ToolRunning("portal"))
            ResumeCourier();
        var node = Courier.Node;
        var busy = node is not null && Courier.Busy(node);
        // Closing it lasts until things calm down and start again (the next connection or question).
        if (busy && !widgetWasBusy)
            widgetDismissed = false;
        widgetWasBusy = busy;
        var wanted = busy && !widgetDismissed && Courier.WidgetOn;
        if (wanted)
        {
            if (courierWidget is null)
            {
                courierWidget = new CourierWidget();
                courierWidget.Dismissed += () => widgetDismissed = true;
                courierWidget.Closed += (_, _) => courierWidget = null;
            }
            if (!courierWidget.IsVisible)
                courierWidget.Show();
            courierWidget.Refresh();
        }
        else
        {
            courierWidget?.Hide();
        }
        // A notification for each transfer while the widget, which shows the same, is not on screen.
        courierNotices ??= new CourierNotices();
        courierNotices.Update(node, allowed: courierWidget is not { IsVisible: true });
        // A PC that wants to connect, or to send files, is asked about in a pop-up.
        CourierAsk.Watch(node);
    }

    /// <summary>The widget was switched on or off on the Courier page.</summary>
    public void CourierWidgetChanged()
    {
        widgetDismissed = false;
        WatchCourier();
    }

    private void Notify(string message, object[] arguments)
    {
        try
        {
            tray?.ShowBalloonTip(5000, AppInfo.Name, Ui.T(message, arguments), Forms.ToolTipIcon.None);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            Log.Warning($"Notification failed: {error.Message}"); // a failed toast must not stop the sorter
        }
    }

    // --- the background sorter ---

    private void StartBackground()
    {
        var background = Background;
        background.Notify += (message, arguments) => Dispatcher.BeginInvoke(() => Notify(message, arguments));
        background.ReportWanted += () => Dispatcher.BeginInvoke(ShowReport);
        background.StateChanged += (state, arguments) => Dispatcher.BeginInvoke(() =>
        {
            if (tray is null)
                return;
            trayState = Ui.T(state, arguments);
            ShowTrayText();
        });
        // The tray icon's tooltip doubles as a small monitor, and warns when the PC is under strain.
        Usage.Updated += _ => Dispatcher.BeginInvoke(ShowTrayText);
        Usage.Alert += (message, arguments) => Dispatcher.BeginInvoke(() =>
        {
            if (ConfigStore.Load().UsageAlerts)
                Notify(message, arguments);
        });
        // The same two-second tick watches for the programs that switch Boost on by themselves.
        var ticks = 0;
        Usage.Updated += _ =>
        {
            if (++ticks % 3 != 0 || exiting)
                return;
            var change = BoostMode.AutoCheck(ConfigStore.Load().Boost);
            if (change is null)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (change.Length > 0)
                    Notify("Boost is on for {0}.", [change]);
                else
                    Notify("Boost is off: everything is back as it was.", []);
            });
        };
        Usage.Start();
        background.DataChanged += () => Dispatcher.BeginInvoke(() => window?.DataChanged());
        background.UpdateFound += () => Dispatcher.BeginInvoke(() => window?.UpdateFound(background.Update));
        background.WindowIsOpen = () => Dispatcher.Invoke(WindowIsOpen);
        background.InstallUpdate = InstallUpdate;
        background.Start();
    }

    /// <summary>
    /// Download the new version and hand over to its installer (the tray's Install item, and
    /// automatic updates). Runs off the window's thread.
    /// </summary>
    private void InstallUpdate(UpdateInfo update)
    {
        if (!AppInfo.IsInstalled)
        {
            // Not an installed copy: nothing to replace, so show the release page.
            Shell.Open(update.Page);
            return;
        }
        Dispatcher.BeginInvoke(() => Notify("Downloading version {0}…", [update.Version]));
        try
        {
            var installer = Updater.Download(update);
            Log.Info($"Installing update {update.Version}");
            Updater.Apply(installer);
        }
        catch (Exception error) // a blocked or failed download must not take the program down
        {
            Log.Warning($"Update failed: {error.Message}");
            Dispatcher.BeginInvoke(() => Notify("The update could not be downloaded. It will be tried again later.", []));
            return;
        }
        Dispatcher.BeginInvoke(ExitProgram); // the installer needs the program closed
    }

    /// <summary>The tray's Exit: close every window, stop the sorter and remove the icon.</summary>
    public void ExitProgram()
    {
        if (exiting)
            return;
        if (Mode == AppMode.Tool && ToolKey == Tools.Popup)
        {
            // "Quit PC Butler" in the command palette: the background agent too.
            AgentLink.Send("exit", 1500);
            ExitTool();
            return;
        }
        if (Mode == AppMode.Tool)
        {
            window?.Quit(); // its Closed ends the process (ExitTool)
            if (window is null)
                ExitTool();
            return;
        }
        if (Mode == AppMode.Board)
        {
            board?.Close();
            return;
        }
        exiting = true;
        agentStop.Cancel();
        configWatcher?.Dispose();
        report?.Close();
        foreach (var open in Windows.OfType<Window>().ToList())
            open.Close();
        Background.Stop();
        Usage.Stop();
        BoostMode.Stop(); // Boost only lasts while PC Butler is running
        Overlay.Stop();
        RecordUsage(false);
        sessionTimer?.Dispose();
        TrackInternet(false);
        search?.Stop();
        courierWatch.Stop();
        courierWidget?.Close();
        Portal.Current?.Stop().Wait(TimeSpan.FromSeconds(4)); // and so does Portal: nothing listens once the program is gone
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        showSignal?.Set(); // lets the waiting thread finish
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        clips?.Dispose();
        keys?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
