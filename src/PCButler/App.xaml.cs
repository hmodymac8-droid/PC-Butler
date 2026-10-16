// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PCButler.Core;
using Forms = System.Windows.Forms;

namespace PCButler;

/// <summary>
/// One process for everything: the tray icon and the background sorter run for as long as
/// the program does, and the window can be closed and opened again.
///
/// Arguments: --background starts without a window (sign-in, installer), --auto-on turns
/// automatic sorting on first (the installer's option), --no-splash skips the launch screen.
/// </summary>
public partial class App : Application
{
    // A copy pointed at its own data folder (PCBUTLER_DATA, used for testing) counts as a
    // separate program, so trying it out never disturbs the installed one.
    private static readonly string Scope =
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 } custom
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                  System.Text.Encoding.UTF8.GetBytes(custom.ToLowerInvariant())))[..8]
            : "";
    private static readonly string InstanceName = @"Local\PCButler.Instance" + Scope;
    private static readonly string ShowSignalName = @"Local\PCButler.Show" + Scope;

    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private Forms.NotifyIcon? tray;
    private MainWindow? window;
    private ReportWindow? report;
    private bool exiting;

    public static new App Current => (App)Application.Current;
    public BackgroundService Background { get; } = new();
    /// <summary>Processor and memory readings for the Home page and the tray icon.</summary>
    public UsageMonitor Usage { get; } = new();
    /// <summary>The clipboard manager: records copies while it is on, and opens its list with the shortcut.</summary>
    public ClipboardManager Clips { get; private set; } = null!;
    /// <summary>The performance overlay of the Benchmark page.</summary>
    public OverlayController Overlay { get; } = new();
    /// <summary>The shortcuts of the Settings page's Hotkeys tab.</summary>
    public Hotkeys Keys { get; private set; } = null!;
    private string trayState = "";
    private bool firstStart;
    private DropZoneWindow? dropZone;
    /// <summary>The main window while it is open.</summary>
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

        bool first;
        try
        {
            instance = new Mutex(true, InstanceName, out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
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
            // Already running: a plain launch brings its window up, a background start has nothing to do.
            if (!arguments.Contains("--background"))
                showSignal.Set();
            Shutdown();
            return;
        }

        AppInfo.ImportLegacyData();
        BoostMode.Recover(); // if the program was closed the hard way while Boost was on
        Tr.Language = ConfigStore.Load().EffectiveLanguage();
        DispatcherUnhandledException += OnUnhandledException;
        // Errors on other threads can't be shown, but they are logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Unobserved: {e.Exception}");
            e.SetObserved();
        };
        Log.Info("Started (tray icon, updates, and automatic sorting when it is on)");

        // The launch screen comes first; everything else waits until it is on screen.
        var launching = !arguments.Contains("--background");
        var withSplash = launching && !arguments.Contains("--no-splash");
        if (withSplash)
        {
            splashOpen = true;
            new SplashWindow(onClose: SplashClosed).Show();
        }
        Dispatcher.BeginInvoke(() => FinishStartup(launching, withSplash), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The rest of the start, once the launch screen (if any) has been drawn.</summary>
    private void FinishStartup(bool launching, bool withSplash)
    {
        if (exiting)
            return;
        SyncExplorerMenu(ConfigStore.Load().ExplorerMenu);
        if (ConfigStore.Load().DropZone)
            ShowDropZone(true);

        StartTray();
        Clips = new ClipboardManager();
        if (Clips.Apply(ConfigStore.Load().Clipboard) is { } clipboardError)
            Log.Warning($"Clipboard manager: {clipboardError}");
        Keys = new Hotkeys(RunHotkey);
        foreach (var (action, problem) in Keys.Apply(ConfigStore.Load().Hotkeys))
            Log.Warning($"Hotkey for {action}: {problem}");
        Overlay.Apply(ConfigStore.Load());
        StartBackground();
        StartCourier();
        var waiter = new Thread(WaitForShowSignal) { IsBackground = true, Name = "PC Butler show signal" };
        waiter.Start();

        if (launching)
            ShowMainWindow(splash: withSplash);
    }

    private bool splashOpen;
    private bool buildingWindow;
    private Action? afterSplash; // shows the window once the launch screen closes

    private void SplashClosed()
    {
        splashOpen = false;
        var next = afterSplash;
        afterSplash = null;
        next?.Invoke();
    }

    private void WaitForShowSignal()
    {
        while (showSignal!.WaitOne())
        {
            if (exiting)
                return;
            // A second launch: the program is already running, so no launch screen.
            Dispatcher.BeginInvoke(() => ShowMainWindow(splash: false));
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception.ToString());
        Ui.Warn(AppInfo.Name, Ui.T("Something went wrong.") + "\n\n" + e.Exception.Message);
        e.Handled = true;
    }

    // --- the window ---

    /// <summary>Bring up the main window, opening it if it is closed.</summary>
    public void ShowMainWindow(bool splash = false)
    {
        if (exiting)
            return;
        if (window is not null)
        {
            if (window.WindowState == WindowState.Minimized)
                SystemCommands.RestoreWindow(window); // back to normal or maximized, as it was
            window.Show();
            window.Activate();
            window.TakeVaultInbox();
            return;
        }
        // Building the window takes a moment, and some of it lets other clicks through (a tray icon
        // double-click is two clicks): while one is being built, a second click must not build another.
        if (buildingWindow)
            return;
        buildingWindow = true;
        try
        {
            window = new MainWindow();
        }
        finally
        {
            buildingWindow = false;
        }
        window.Closed += (_, _) => window = null;
        var opened = window;
        if (splash)
        {
            // The launch screen is already up (see OnStartup): the window waits for it to close.
            void Show()
            {
                // Not shown yet, so it has no window handle: what matters is that it is still the program's window.
                if (window == opened)
                {
                    opened.Reveal();
                    ShowWhatsNew(opened);
                }
            }
            if (splashOpen)
                afterSplash = Show;
            else
                Show(); // it was clicked away before the window was ready
            // While the launch screen is up: look for junk and for program updates, so the results are waiting.
            opened.Cleaner.ScanNow();
            opened.Programs.CheckAtLaunch();
        }
        else
        {
            opened.Reveal();
            ShowWhatsNew(opened);
        }
        // After the window is up (and after the launch screen, if there is one).
        Dispatcher.BeginInvoke(() =>
        {
            if (IsOpen(opened))
                opened.TakeVaultInbox();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

    /// <summary>A shortcut from the Hotkeys tab was pressed, in whatever program.</summary>
    private void RunHotkey(string action)
    {
        if (exiting)
            return;
        switch (action)
        {
            case "open":
                ShowMainWindow();
                break;
            case "sort":
                Background.RequestSort();
                break;
            case "dropzone":
                SetDropZone(!ConfigStore.Load().DropZone);
                break;
            case "courier":
                ShowMainWindow();
                window?.ShowPage("portal");
                break;
            case "overlay":
                var config = ConfigStore.Load();
                config.Overlay.Enabled = !config.Overlay.Enabled;
                ConfigStore.Save(config);
                window?.UpdateConfig(saved => saved.Overlay.Enabled = config.Overlay.Enabled);
                Overlay.Apply(config);
                window?.Benchmark.ShowEnabled();
                Notify(config.Overlay.Enabled ? "The performance overlay is on." : "The performance overlay is off.", []);
                break;
            case "boost":
                // Boost the program that is in front; pressed again, everything goes back.
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
                break;
        }
    }

    /// <summary>Show or hide the drop zone and remember the choice.</summary>
    public void SetDropZone(bool show)
    {
        var config = ConfigStore.Load();
        config.DropZone = show;
        ConfigStore.Save(config);
        window?.UpdateConfig(saved => saved.DropZone = show);
        ShowDropZone(show);
    }

    /// <summary>Once after each update, when the window first opens: the list of what changed.</summary>
    private void ShowWhatsNew(MainWindow owner)
    {
        var seen = ConfigStore.Load().LastSeenVersion;
        if (seen == AppInfo.Version)
            return;
        owner.UpdateConfig(config => config.LastSeenVersion = AppInfo.Version);
        if (!Changelog.ShouldShow(seen, firstStart))
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
        var old = window;
        old?.Quit();
        if (old is not null && IsOpen(old))
            return; // the window is waiting for a job to finish
        window = null;
        ShowMainWindow();
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
        tray.MouseClick += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                ToggleFlyout();
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left)
                return;
            flyout?.Close();
            ShowMainWindow();
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
            flyout.Close();
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
        ShowMainWindow();
        window?.ShowPage(key);
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
        // The widget asks right there; without it, the Courier page does.
        if (Courier.WidgetOn)
        {
            widgetDismissed = false;
            WatchCourier();
            return;
        }
        ShowMainWindow();
        window?.ShowPage("portal");
    });

    // --- Courier: started with the program, and its widget while PCs are connected ---

    private CourierWidget? courierWidget;
    private CourierNotices? courierNotices;
    private bool widgetDismissed, widgetWasBusy;
    private readonly DispatcherTimer courierWatch = new() { Interval = TimeSpan.FromSeconds(1) };

    private void StartCourier()
    {
        courierWatch.Tick += (_, _) => WatchCourier();
        courierWatch.Start();
        var settings = ConfigStore.Load().Portal;
        Courier.WidgetOn = settings.Widget;
        // A PC that has chosen its role starts Courier by itself: a client gets connected, a host gets reachable.
        if (settings.AutoStart && settings.Role is PortalSettings.Host or PortalSettings.Client)
            _ = Courier.Start();
    }

    /// <summary>Show the widget while something is going on (a PC connected, a transfer, a question), hide it otherwise.</summary>
    private void WatchCourier()
    {
        if (exiting)
            return;
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
        window?.Quit();
        if (window is not null && IsOpen(window))
            return; // the window is waiting for a job to finish
        exiting = true;
        report?.Close();
        foreach (var open in Windows.OfType<Window>().ToList())
            open.Close();
        Background.Stop();
        Usage.Stop();
        BoostMode.Stop(); // Boost only lasts while PC Butler is running
        Overlay.Stop();
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
        Clips?.Dispose();
        Keys?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
