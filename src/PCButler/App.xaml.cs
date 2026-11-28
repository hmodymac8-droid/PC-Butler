// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The WPF side of PC Butler, several small processes (see AppMode):
///
///   PCButler.exe                 the floating board: pick a tool, and the board is gone
///   PCButler.exe --tool &lt;id&gt;     one tool in a window of its own (also PCButler.&lt;Tool&gt;.exe)
///   PCButler.exe --popup         the quick search box and command palette (PCButler.QuickSearch.exe)
///   PCButler.exe --ui            the companion: the windows of the background agent (drop zone,
///                                Courier's widget and questions, the overlay, the clipboard list,
///                                the report after a sort) and Courier itself; it runs while one of
///                                those is on, and ends by itself when none is
///
/// The background agent is PCButler.Agent.exe (no WPF). --agent and --background, as older
/// versions and their start-up entry used them, start it.
/// Other arguments: --auto-on turns automatic sorting on first (the installer's option).
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private ToolWindow? window;
    private BoardWindow? board;
    private ReportWindow? report;
    private bool exiting;

    public static new App Current => (App)Application.Current;
    /// <summary>What this process is: the board, the companion, or one tool.</summary>
    public AppMode Mode { get; private set; } = AppMode.Board;
    /// <summary>True only in the companion: the one WPF process that runs Courier, the overlay, the drop zone and clipboard sharing.</summary>
    public bool RunsServices => Mode == AppMode.Companion;
    /// <summary>The tool this process shows (Mode is Tool).</summary>
    public string ToolKey { get; private set; } = "";
    /// <summary>Not started here (the agent runs it): only its update check, for Help &amp; about.</summary>
    public BackgroundService Background { get; } = new();
    /// <summary>Processor and memory readings for the pages that show them live.</summary>
    public UsageMonitor Usage { get; } = new();
    private ClipboardManager? clips;
    /// <summary>The clipboard list (the agent records the copies; this shows them and puts them back).</summary>
    public ClipboardManager Clips => clips ??= new ClipboardManager();
    private ClipboardShare? clipShare;
    /// <summary>Copied text shared with the PCs and phones connected through Courier (in the companion).</summary>
    public ClipboardShare ClipShare => clipShare ??= new ClipboardShare();
    /// <summary>The performance overlay (it runs in the companion; the Benchmark tool shows it).</summary>
    public OverlayController Overlay { get; } = new();
    private Hotkeys? keys;
    /// <summary>The shortcuts of the Settings page's Hotkeys tab (the agent registers them).</summary>
    public Hotkeys Keys => keys ??= new Hotkeys(RunHotkey, active: false);
    private PCButler.Core.Search.SearchEngine? search;
    /// <summary>
    /// File search: a process that searches loads the saved index when it first asks, keeps it up
    /// to date while it is open, and saves it when it closes.
    /// </summary>
    public PCButler.Core.Search.SearchEngine Search
    {
        get
        {
            if (search is null)
            {
                search = new PCButler.Core.Search.SearchEngine();
                if (Mode != AppMode.Companion)
                    search.Start(ConfigStore.Load().Search);
            }
            return search;
        }
    }

    /// <summary>
    /// One search without keeping the index (the companion, when a phone asks Courier for a file
    /// by name): the saved index is read, searched and let go.
    /// </summary>
    public PCButler.Core.Search.SearchResult SearchOnce(string query)
    {
        if (search is not null || Mode != AppMode.Companion)
            return Search.Search(query, PCButler.Core.Search.SearchSort.Name, false, CancellationToken.None);
        var settings = ConfigStore.Load().Search;
        var loaded = PCButler.Core.Search.IndexStore.Load(PCButler.Core.Search.IndexStore.DefaultFile);
        var index = loaded?.Index ?? new PCButler.Core.Search.SearchIndex();
        return PCButler.Core.Search.Searcher.Run(index, query, PCButler.Core.Search.SearchSort.Name, false,
                                                 new PCButler.Core.Search.SearchOptions(settings.MatchCase, settings.MatchDiacritics), CancellationToken.None);
    }

    private SearchPopup? searchPopup;
    private bool firstStart;
    private DropZoneWindow? dropZone;
    /// <summary>The tool's window while it is open (null in the board and the companion).</summary>
    public ToolWindow? Workspace => window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = e.Args;
        // Nothing saved yet means a first start: there is no update to announce (see ShowWhatsNew).
        firstStart = !File.Exists(AppInfo.ConfigFile);
        if (arguments.Length >= 3 && arguments[0] == "--sensors")
        {
            // The helper that reads temperatures as administrator (see Sensors): no window, no tray icon.
            Sensors.RunHelper(arguments[1], int.TryParse(arguments[2], out var parent) ? parent : 0,
                             stopped => AdminHelper.Poll(arguments[1], stopped), AdminHelper.ResumeAll);
            Shutdown();
            return;
        }
        if (arguments.Length >= 1 && arguments[0] == "--remove-search-service")
        {
            // The uninstaller, with administrator rights: the search service goes with PC Butler.
            SearchServiceSetup.Remove();
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
        if (arguments.Contains("--agent") || arguments.Contains("--background"))
        {
            // The background agent is a program of its own now.
            Launcher.StartAgent();
            Shutdown();
            return;
        }

        if (arguments.Length == 0 && Tools.KeyOfExe(Environment.ProcessPath) is { } own)
            arguments = own == Tools.Popup ? ["--popup"] : ["--tool", own];
        var toolAt = Array.IndexOf(arguments, "--tool");
        if (arguments.Contains("--ui"))
            Mode = AppMode.Companion;
        else if (arguments.Contains("--popup"))
            (Mode, ToolKey) = (AppMode.Tool, Tools.Popup); // the quick search box and command palette
        else if (toolAt >= 0 && toolAt + 1 < arguments.Length && Tools.IsTool(arguments[toolAt + 1]))
            (Mode, ToolKey) = (AppMode.Tool, arguments[toolAt + 1]);

        var scope = CrossProcess.Scope;
        var (instanceName, signalName) = Mode switch
        {
            AppMode.Companion => (Link.Ui.MutexName, Link.Ui.MutexName + ".Show"),
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
            // Already open: a second launch brings the board or the tool forward (with what it was
            // asked to do). The companion is asked over its pipe instead.
            if (Mode == AppMode.Tool)
                ToolRequest.Write(ToolKey, arguments);
            else if (Mode == AppMode.Companion)
                ForwardToCompanion(arguments);
            if (Mode != AppMode.Companion)
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
            case AppMode.Companion:
                StartCompanionMode(arguments);
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

    private static string? After(string[] arguments, string name)
    {
        var at = Array.IndexOf(arguments, name);
        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
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
        if (ToolKey is "benchmark" or "booster")
            Usage.Start(); // their live figures
        window = new ToolWindow(ToolKey);
        var opened = window;
        opened.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(opened).Handle;
            TaskbarIdentity.ForWindow(handle, id, $"--tool {ToolKey}", opened.Title);
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
                if (ToolKey == "vault")
                    shown.TakeVaultInbox();
                if (ToolKey == "portal" && courierReady)
                    shown.TakeCourierInbox();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>What a launch asked the tool to do: files for its list, a search, the What's new list.</summary>
    private void ToolArguments(string[] arguments)
    {
        if (window is not { } shown)
            return;
        if (After(arguments, "--paths") is { } list && Tools.TakeList(list) is { Count: > 0 } paths)
        {
            if (ToolKey == "shredder")
                shown.ShredLater(paths);
            else if (ToolKey == "portal")
                shown.SendWithPortal(paths);
        }
        if (After(arguments, "--query") is { } query && ToolKey == "search")
            shown.SearchPage.SearchFor(query);
        if (arguments.Contains("--whats-new"))
            ShowWhatsNew(shown, always: true);
        if (arguments.Contains("--auto-sort"))
            shown.SetAutoSort(true);
        if (After(arguments, "--tab") is { } tab && ToolKey == "settings")
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

    // --- the Benchmark tool shows the overlay the companion runs ---

    /// <summary>Every second while the Benchmark tool is open: the companion's overlay readings, copied here.</summary>
    private void MirrorOverlay()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var asking = false;
        timer.Tick += (_, _) =>
        {
            if (asking || exiting)
                return;
            asking = true;
            Task.Run(() => Link.Ui.Send("overlay-state", 800)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
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
            // Courier goes back to the companion, or the agent starts it again.
            if (Link.Ui.Running)
                Link.Ui.Send("courier-resume", 3000);
            else
                AgentLink.ConfigChanged();
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
            Tools.Open(ToolKey);
        }
        showSignal?.Set();
        Shutdown();
    }

    private bool courierReady;

    /// <summary>
    /// The Courier tool runs Courier itself while it is open, so its page shows the connections
    /// live: the companion stops its own first and starts it again when the tool closes.
    /// </summary>
    private void BorrowCourier()
    {
        Task.Run(() =>
        {
            var reply = Link.Ui.Send("courier-release", 8000);
            var start = reply == "released" || (reply is null && AgentNeeds.CourierOn(ConfigStore.Load()));
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

    // --- the companion: the agent's windows, and Courier ---

    private readonly CancellationTokenSource companionStop = new();
    private FileSystemWatcher? configWatcher;
    private DispatcherTimer? reapplyTimer;
    private readonly Dictionary<string, string> applied = [];
    private bool courierLent;
    private long lastBusy = Environment.TickCount64;

    private void StartCompanionMode(string[] arguments)
    {
        var config = ConfigStore.Load();
        Log.Info("Companion started");
        Clips.Use(config.Clipboard);
        if (config.DropZone)
            ShowDropZone(true);
        ClipShare.Apply(config.Portal.ShareClipboard);
        OverlayUsage(config.Overlay.Enabled);
        Overlay.Apply(config);
        StartCourier();
        Remember(config);
        WatchConfig();
        Link.Ui.Serve(CompanionCommand, companionStop.Token);
        CompanionArguments(arguments);
        // Ends by itself when nothing of its own is on and nothing it showed is open.
        var lifetime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        lifetime.Tick += (_, _) =>
        {
            if (exiting)
                return;
            if (AgentNeeds.Companion(ConfigStore.Load()) || Courier.Node is not null || courierLent
                || Windows.OfType<Window>().Any(open => open.IsVisible))
                lastBusy = Environment.TickCount64;
            else if (Environment.TickCount64 - lastBusy > TimeSpan.FromMinutes(2).TotalMilliseconds)
            {
                Log.Info("Companion: nothing to show or run; closing");
                ExitProgram();
                return;
            }
            if (!Windows.OfType<Window>().Any(open => open.IsActive))
                Memory.Trim();
        };
        lifetime.Start();
    }

    private bool usageOn;

    /// <summary>The overlay shows the processor and graphics card's use: read while it is on.</summary>
    private void OverlayUsage(bool on)
    {
        if (on == usageOn)
            return;
        usageOn = on;
        if (on)
            Usage.Start();
        else
            Usage.Stop();
    }

    /// <summary>What the companion was started (or asked) to show: --picker &lt;window&gt;, --report.</summary>
    private void CompanionArguments(string[] arguments)
    {
        if (After(arguments, "--picker") is { } target)
            ShowPicker(target);
        if (arguments.Contains("--report"))
            ShowReport();
    }

    /// <summary>A second start of the companion (the agent asking for the clipboard list, say): passed to the running one.</summary>
    private static void ForwardToCompanion(string[] arguments)
    {
        if (After(arguments, "--picker") is { } target)
            Link.Ui.Send("show-picker", 3000, target);
        if (arguments.Contains("--report"))
            Link.Ui.Send("show-report", 3000);
    }

    private void ShowPicker(string target)
    {
        lastBusy = Environment.TickCount64;
        Clips.Use(ConfigStore.Load().Clipboard);
        Clips.ShowPicker(long.TryParse(target, out var handle) ? new IntPtr(handle) : IntPtr.Zero);
    }

    /// <summary>One request over the companion's pipe (on the pipe's thread).</summary>
    private string CompanionCommand(string command, string data)
    {
        switch (command)
        {
            case "ping":
                return "companion";
            case "show-picker":
                Dispatcher.BeginInvoke(() => ShowPicker(data));
                return "ok";
            case "show-report":
                Dispatcher.BeginInvoke(ShowReport);
                return "ok";
            case "overlay-state":
                return Dispatcher.Invoke(Overlay.State);
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
            Log.Warning($"Companion: cannot watch the settings file: {error.Message}");
        }
    }

    private void QueueReapply()
    {
        if (exiting || reapplyTimer is null)
            return;
        reapplyTimer.Stop();
        reapplyTimer.Start();
    }

    /// <summary>The Courier tool closed: Courier runs here again, if it starts by itself.</summary>
    private void ResumeCourier()
    {
        if (!courierLent || exiting)
            return;
        courierLent = false;
        if (Courier.Node is null && AgentNeeds.CourierOn(ConfigStore.Load()))
            _ = Courier.Start();
    }

    private static string Snapshot(object value) => JsonSerializer.Serialize(value);

    private static string CourierSnapshot(Config config) => $"{config.Portal.AutoStart}{config.Portal.Used}{config.Portal.Role}";

    private void Remember(Config config)
    {
        applied["overlay"] = Snapshot(config.Overlay) + Snapshot(config.Boost.AutoApps);
        applied["courier"] = CourierSnapshot(config);
        applied["language"] = config.EffectiveLanguage();
    }

    private bool Changed(string key, string now) => !applied.TryGetValue(key, out var before) || before != now;

    /// <summary>The settings changed on disk: switch the companion's features to match.</summary>
    private void Reapply()
    {
        if (exiting)
            return;
        var config = ConfigStore.Load();
        if (Changed("language", config.EffectiveLanguage()))
            Tr.Language = config.EffectiveLanguage();
        Clips.Use(config.Clipboard);
        ClipShare.Apply(config.Portal.ShareClipboard);
        if (Changed("overlay", Snapshot(config.Overlay) + Snapshot(config.Boost.AutoApps)))
        {
            OverlayUsage(config.Overlay.Enabled);
            Overlay.Apply(config);
        }
        ShowDropZone(config.DropZone);
        Courier.WidgetOn = config.Portal.Widget;
        if (Changed("courier", CourierSnapshot(config)) && !courierLent)
        {
            if (Courier.Node is null && AgentNeeds.CourierOn(config))
                _ = Courier.Start();
            else if (Courier.Node is not null && !config.Portal.AutoStart)
                _ = Courier.Stop(); // switched off in the tray menu
        }
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

    /// <summary>"Open PC Butler": the board; in a tool, its own window comes forward.</summary>
    public void ShowMainWindow()
    {
        if (exiting)
            return;
        if (Mode == AppMode.Companion)
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
        // Brought forward by a second launch: always maximized, as it opened.
        window.Show();
        window.WindowState = WindowState.Maximized;
        window.Activate();
        if (ToolKey == "vault")
            window.TakeVaultInbox();
        if (ToolKey == "portal" && courierReady)
            window.TakeCourierInbox();
    }

    /// <summary>
    /// Add or remove File Explorer's right-click entry. Only an installed copy adds it, so a
    /// copy run from a build folder never leaves an entry pointing at a file that may move.
    /// </summary>
    public static string? SyncExplorerMenu(bool wanted) =>
        !AppInfo.IsInstalled ? null
            : ExplorerMenu.Sync(wanted, AppInfo.MainExe, english => Ui.T(english));

    private void ShowDropZone(bool show)
    {
        if (!RunsServices)
            return; // the drop zone lives in the companion
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

    /// <summary>A shortcut's action, from the command palette or the drop zone's menu.</summary>
    public void RunHotkey(string action)
    {
        if (exiting)
            return;
        switch (action)
        {
            case "open":
                Tools.OpenBoard();
                break;
            case "palette":
            case "search":
                Tools.OpenPopup(palette: action == "palette");
                break;
            case "searchpage":
                Tools.Open("search");
                break;
            case "courier":
                Tools.Open("portal");
                break;
            default:
                // Sorting, Boost, the overlay and the drop zone belong to the background agent: it does them.
                Task.Run(() =>
                {
                    if (AgentLink.Send("hotkey", 2000, action) is null)
                        Dispatcher.BeginInvoke(() => Ui.Info(AppInfo.Name,
                            Ui.T("PC Butler is not running in the background. Switch a background feature on in Settings first.")));
                });
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
        ChangeConfig(config => config.DropZone = show);
        ShowDropZone(show);
        window?.ShowSwitches();
    }

    /// <summary>Once after each update, when the window first opens: the list of what changed.</summary>
    private void ShowWhatsNew(ToolWindow owner, bool always = false)
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
        }, DispatcherPriority.ApplicationIdle);
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
        lastBusy = Environment.TickCount64;
        if (report is not null)
        {
            report.WindowState = WindowState.Maximized;
            report.Activate();
            return;
        }
        report = new ReportWindow();
        report.Closed += (_, _) => report = null;
        report.Show();
    }

    /// <summary>False once a window has really closed (a refused close leaves it open).</summary>
    private static bool IsOpen(Window shown) => new System.Windows.Interop.WindowInteropHelper(shown).Handle != IntPtr.Zero;

    /// <summary>Open the window at one of its pages.</summary>
    public void ShowPage(string key)
    {
        if (window is null)
        {
            // The board and the companion open the tool in a window of its own.
            Tools.Open(key);
            board?.Close();
            return;
        }
        ShowMainWindow();
        window.ShowPage(key);
    }

    /// <summary>Another PC asks to pair or to send files: say so; the widget or a pop-up asks right away.</summary>
    public void PortalNeedsAttention(string message) => Dispatcher.BeginInvoke(() =>
    {
        if (exiting)
            return;
        Notify("{0}", [message]);
        if (RunsServices)
        {
            widgetDismissed = false;
            WatchCourier();
            return;
        }
        ShowMainWindow();
        window?.ShowPage("portal");
    });

    // --- Resume yesterday (the agent does it; the classic Home page and the palette offer it) ---

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

    /// <summary>Open again what was open before the restart. Done once.</summary>
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

    /// <summary>The switch in Settings. Switched off, what was noted is forgotten (the agent notes it).</summary>
    public void SetResumeSession(bool on)
    {
        ChangeConfig(config => config.ResumeSession = on);
        if (!on)
            SessionRestore.Forget();
    }

    // --- switches whose work the agent does (it reads them from the saved settings) ---

    public void TrackInternet(bool on)
    {
    }

    public void RecordUsage(bool on)
    {
    }

    private bool? countAppTraffic;

    /// <summary>Whether the helper is to count each app's traffic (the Internet page's Start and Stop).</summary>
    public bool CountingAppTraffic => countAppTraffic ??= ConfigStore.Load().CountAppTraffic;

    /// <summary>Start or stop counting each app's traffic; starting needs the helper running already.</summary>
    public Task CountAppTraffic(bool on)
    {
        countAppTraffic = on;
        return AdminHelper.Running ? Task.Run(() => AdminHelper.Send("net", [on ? "on" : "off"], 10)) : Task.CompletedTask;
    }

    // --- notifications: shown by the agent's tray icon ---

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

    /// <summary>Files from another PC arrived: a notification says so, and a click on it shows them.</summary>
    public void PortalReceived(PortalTransfer transfer) => Dispatcher.BeginInvoke(() =>
    {
        if (exiting)
            return;
        Notify(transfer.Files == 1 ? "Received {0} from {1}." : "Received {0} ({2} files) from {1}.", [transfer.Name, transfer.Peer, transfer.Files],
               transfer.Path);
    });

    private static void Notify(string message, object[] arguments, string path = "")
    {
        var notice = JsonSerializer.Serialize(new TrayNotice(Ui.T(message, arguments), path));
        Task.Run(() => AgentLink.Send("notify", 2000, notice));
    }

    // --- Courier: in the companion, and its widget while PCs are connected ---

    private CourierWidget? courierWidget;
    private CourierNotices? courierNotices;
    private bool widgetDismissed, widgetWasBusy;
    private readonly DispatcherTimer courierWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private int lentChecks;

    private void StartCourier()
    {
        courierWatch.Tick += (_, _) => WatchCourier();
        courierWatch.Start();
        var config = ConfigStore.Load();
        Courier.WidgetOn = config.Portal.Widget;
        // Once Courier has been switched on (before 1.22: once a role was chosen), it starts by itself: reachable, and connected to its PCs.
        if (AgentNeeds.CourierOn(config))
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
        if (RunsServices)
            WatchCourier();
    }

    /// <summary>Close this process: a tool's window, the board, or the companion with everything it runs.</summary>
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
        companionStop.Cancel();
        configWatcher?.Dispose();
        report?.Close();
        foreach (var open in Windows.OfType<Window>().ToList())
            open.Close();
        Overlay.Stop();
        courierWatch.Stop();
        courierWidget?.Close();
        Portal.Current?.Stop().Wait(TimeSpan.FromSeconds(4)); // nothing listens once the companion is gone
        showSignal?.Set(); // lets the waiting thread finish
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        clips?.Dispose();
        keys?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
