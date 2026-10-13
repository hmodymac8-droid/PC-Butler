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

        instance = new Mutex(true, InstanceName, out var first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
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
        StartBackground();
        var waiter = new Thread(WaitForShowSignal) { IsBackground = true, Name = "PC Butler show signal" };
        waiter.Start();

        if (launching)
            ShowMainWindow(splash: withSplash);
    }

    private bool splashOpen;
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
        window = new MainWindow();
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
        old?.Close();
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
        tray = new Forms.NotifyIcon { Text = AppInfo.Name, Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        TrayMenu.Style(tray.ContextMenuStrip);
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
        // A click opens the window (the menu's default item). From the tray the program is
        // already running, so the window opens without the launch screen.
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
        // Built each time it opens, so the texts follow the language and the pause state.
        BuildTrayMenu(tray.ContextMenuStrip);
        tray.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildTrayMenu(tray.ContextMenuStrip);
            e.Cancel = false;
        };
    }

    private void BuildTrayMenu(Forms.ContextMenuStrip menu)
    {
        var config = ConfigStore.Load();
        var update = Background.Update;
        TrayMenu.Fill(menu, config,
        [
            new("\uE8A7", Ui.T($"Open {AppInfo.Name}"), () => ShowMainWindow(), Main: true),
            new("\uE8FD", Ui.T("Sort now"), Background.RequestSort),
            // Pausing only means something while automatic sorting is on, as in the window's status bar.
            config.IsPaused ? new("\uE768", Ui.T("Resume sorting"), Background.TogglePause, config.AutoSortOn)
                            : new("\uE769", Ui.T("Pause for today"), Background.TogglePause, config.AutoSortOn),
            new("\uE81C", Ui.T("Show report"), ShowReport),
            config.Clipboard.Enabled ? new("\uE77F", Ui.T("Clipboard history"), () => Clips.ShowPicker()) : null,
            config.DropZone ? new("\uE8B7", Ui.T("Hide the drop zone"), () => SetDropZone(false))
                            : new("\uE8B7", Ui.T("Show the drop zone"), () => SetDropZone(true)),
            // Only shown once a newer version is found.
            update is null ? null : new("\uE896", Ui.T("Install version {0}", update.Version), () => Task.Run(() => InstallUpdate(update))),
            null,
            new("\uE7E8", Ui.T("Exit"), ExitProgram),
        ]);
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
        ShowMainWindow();
        window?.ShowPage("portal");
    });

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
        window?.Close();
        if (window is not null && IsOpen(window))
            return; // the window is waiting for a job to finish
        exiting = true;
        report?.Close();
        foreach (var open in Windows.OfType<Window>().ToList())
            open.Close();
        Background.Stop();
        Usage.Stop();
        BoostMode.Stop(); // Boost only lasts while PC Butler is running
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
        instance?.Dispose();
        base.OnExit(e);
    }
}
