// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// One tool's own window (a process of its own, see App): its page under a top bar with the
/// tool's name and a Board button. Another tool asked for from a page opens in its own window.
/// A page may still use another tool's page without showing it (Settings sorts a folder with
/// the Sorter's), which is then built hidden.
/// </summary>
public sealed class ToolWindow : Window
{
    private const double CompactBelow = 720; // shorter than this, the summary cards and small print go
    private readonly TopBar topBar = new();
    private readonly Grid pageHost = new();
    /// <summary>Every page, built the first time it is needed.</summary>
    private readonly Dictionary<string, (Func<Page> Make, Page? Page, string Name)> pages = [];
    private readonly Dictionary<string, string> glyphs = [];
    private readonly string only;
    private readonly Border banner;
    private readonly TextBlock bannerLabel = Ui.Text("");
    /// <summary>Turns automatic sorting on or off. It lives on the Settings page and follows the state.</summary>
    public CheckBox AutoSortSwitch { get; }
    private bool showingState; // set while the switch is moved to match the state, not by a click
    private bool? compact;

    public Config Config { get; private set; }
    public SorterPage Sorter => Get<SorterPage>("preview");
    public DuplicatesPage Duplicates => Get<DuplicatesPage>("duplicates");
    public HistoryPage History => Get<HistoryPage>("history");
    public VaultPage Vault => Get<VaultPage>("vault");
    public ShredderPage Shredder => Get<ShredderPage>("shredder");
    public PortalPage PortalPage => Get<PortalPage>("portal");
    public CleanerPage Cleaner => Get<CleanerPage>("cleaner");
    public ProgramsPage Programs => Get<ProgramsPage>("programs");
    public SettingsPage Settings => Get<SettingsPage>("settings");
    public BenchmarkPage Benchmark => Get<BenchmarkPage>("benchmark");
    public AboutPage About => Get<AboutPage>("about");
    public SearchPage SearchPage => Get<SearchPage>("search");

    /// <summary>(key, icon, name) of every tool. The board and the command palette list them too.</summary>
    public static readonly (string Key, string Glyph, string Name)[] PageList =
    [
        ("search", "", "Search"),
        ("preview", "", "Sorter"),
        ("duplicates", "", "Duplicates"),
        ("cleaner", "", "Cleaner"),
        ("storage", "", "Storage"),
        ("booster", "", "Booster"),
        ("benchmark", "", "Benchmark"),
        ("programs", "", "Programs"),
        ("internet", "", "Internet"),
        ("lan", "", "Scan LAN"),
        ("shredder", "", "Shredder"),
        ("clipboard", "", "Clipboard"),
        ("portal", "", "Courier"),
        ("vault", "", "Vault"),
        ("history", "", "History"),
        ("settings", "", "Settings"),
        ("about", "", "Help & about"),
    ];

    private Page Make(string key) => key switch
    {
        "search" => new SearchPage(this),
        "preview" => new SorterPage(this),
        "duplicates" => new DuplicatesPage(this),
        "cleaner" => new CleanerPage(this),
        "storage" => new StoragePage(this),
        "booster" => new BoosterPage(this),
        "benchmark" => new BenchmarkPage(this),
        "programs" => new ProgramsPage(this),
        "internet" => new InternetPage(this),
        "lan" => new LanPage(this),
        "shredder" => new ShredderPage(this),
        "clipboard" => new ClipboardPage(this),
        "portal" => new PortalPage(this),
        "vault" => new VaultPage(this),
        "history" => new HistoryPage(this),
        "settings" => new SettingsPage(this),
        "about" => new AboutPage(this),
        _ => throw new ArgumentException($"No page {key}", nameof(key)),
    };

    public ToolWindow(string only)
    {
        this.only = only;
        Ui.StyleWindow(this);
        Title = $"{Tools.Name(only)} · {AppInfo.Name}";
        Config = ConfigStore.Load();
        Width = 1120;
        Height = 760;
        MinWidth = 760;
        MinHeight = 540;
        FitInWorkArea();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var category in Config.Categories)
            Ui.Tone(System.IO.Path.GetFileName(category.Folder)); // fixes each folder's tile colour, in settings order

        var main = new DockPanel();
        topBar.AddBoard(Tools.OpenBoard);
        main.Children.Add(topBar.Dock(Dock.Top));

        // Shown across the top only when a newer version exists.
        var bannerRow = new DockPanel { LastChildFill = false };
        bannerRow.Children.Add(bannerLabel);
        bannerRow.Children.Add(Ui.Button(Ui.T("Later"), () => banner!.Visibility = Visibility.Collapsed).Dock(Dock.Right));
        bannerRow.Children.Add(Ui.Button(Ui.T("Update now"), () => About!.InstallUpdate(), accent: true).Margin(0, 0, 6, 0).Dock(Dock.Right));
        banner = new Border { Padding = new Thickness(22, 10, 22, 10), Child = bannerRow, Visibility = Visibility.Collapsed };
        main.Children.Add(banner.Dock(Dock.Top));
        main.Children.Add(pageHost);
        AutoSortSwitch = Ui.Switch("", false, on =>
        {
            if (!showingState)
                SetAutoSort(on);
        });

        foreach (var (key, glyph, name) in PageList)
        {
            pages[key] = (() => Make(key), null, Ui.T(name));
            glyphs[key] = glyph;
        }
        main.SizeChanged += (_, _) => FitHeight(main.ActualHeight);
        Content = main;

        Closing += (_, e) =>
        {
            if (!Built.All(page => page.CanClose()))
                e.Cancel = true;
            else
                foreach (var page in Built)
                    page.Closing(); // saves edits to opened vault files and the Duplicates settings
        };
        RefreshBackgroundStatus();
        ShowPage(only);
        RefreshPreview();
        // Settings and Help look for updates when they open (the background agent looks too).
        if (only is not ("settings" or "about"))
            return;
        var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        firstCheck.Tick += (_, _) =>
        {
            firstCheck.Stop();
            About.CheckUpdates();
        };
        firstCheck.Start();
    }

    /// <summary>Show the window maximized: within the work area of its screen, so the taskbar stays visible.</summary>
    public void Reveal()
    {
        WindowState = WindowState.Maximized;
        Show();
        Activate();
    }

    /// <summary>The pages built so far.</summary>
    private IEnumerable<Page> Built => pages.Values.Select(entry => entry.Page).OfType<Page>();

    /// <summary>A page, built now if it was not yet.</summary>
    private T Get<T>(string key) where T : Page => (T)Page(key);

    private Page Page(string key)
    {
        var (make, page, name) = pages[key];
        if (page is not null)
            return page;
        page = make();
        page.Visibility = Visibility.Collapsed;
        if (compact is { } small)
            page.SetCompact(small);
        pageHost.Children.Add(page);
        pages[key] = (make, page, name);
        return page;
    }

    /// <summary>This tool's page; another tool opens in a window of its own.</summary>
    public void ShowPage(string key)
    {
        if (key != only)
        {
            Tools.Open(key);
            return;
        }
        foreach (var page in Built)
            page.Visibility = Visibility.Collapsed;
        var shown = Page(key);
        shown.Visibility = Visibility.Visible;
        topBar.SetPage(glyphs[key], pages[key].Name);
        shown.Shown();
    }

    /// <summary>A count beside a page's name; the board has no badges, so it is not shown.</summary>
    public void SetBadge(string key, string text)
    {
    }

    /// <summary>Change settings on top of what is on disk, since the agent and the other tools write there too.</summary>
    public void UpdateConfig(Action<Config> change)
    {
        var config = ConfigStore.Load();
        change(config);
        ConfigStore.Save(config);
        Config = config;
    }

    /// <summary>Something was switched outside the window (the tray menu, a hotkey): show it on the pages built so far.</summary>
    public void ShowSwitches()
    {
        Config = ConfigStore.Load();
        RefreshBackgroundStatus();
        foreach (var page in Built)
            page.ShowSwitches();
    }

    /// <summary>Plan the Sorter list again, and History with it, after anything that moves files (the pages built here).</summary>
    public void RefreshPreview()
    {
        (pages["preview"].Page as SorterPage)?.Refresh();
        (pages["history"].Page as HistoryPage)?.Refresh();
    }

    /// <summary>Files sent to the vault from File Explorer or the drop zone: add them (after the password).</summary>
    public void TakeVaultInbox()
    {
        if (only != "vault")
            return;
        var waiting = VaultInbox.Take();
        if (waiting.Count > 0)
            Vault.AddFromOutside(waiting);
    }

    /// <summary>Files and folders shared with Courier from File Explorer: ready to send, Courier started first when it is off.</summary>
    public void TakeCourierInbox()
    {
        if (only != "portal")
            return;
        var waiting = CourierInbox.Take();
        if (waiting.Count == 0)
            return;
        if (Courier.Node is null)
            _ = Courier.Start(); // the page explains what is missing when it can't start
        SendWithPortal(waiting);
    }

    /// <summary>Send files to another PC through Courier: on this window's list, or the Courier tool's.</summary>
    public void SendWithPortal(IEnumerable<string> paths)
    {
        if (only != "portal")
        {
            CourierInbox.Add(paths.ToList());
            Tools.Open("portal");
            return;
        }
        PortalPage.Queue(paths);
    }

    /// <summary>Put items on the Shredder's list. Nothing is shredded until the user presses Shred there.</summary>
    public void ShredLater(IEnumerable<string> paths)
    {
        if (only != "shredder")
        {
            Tools.OpenWith("shredder", paths);
            return;
        }
        Shredder.AddPaths(paths);
    }

    /// <summary>Files were moved or cleaned elsewhere.</summary>
    public void DataChanged()
    {
        RefreshBackgroundStatus();
        (pages["history"].Page as HistoryPage)?.Refresh();
    }

    // --- updates ---

    public void UpdateFound(UpdateInfo? update) => About.Found(update);

    public void ShowBanner(string text)
    {
        bannerLabel.Text = text;
        banner.Visibility = Visibility.Visible;
    }

    public void HideBanner() => banner.Visibility = Visibility.Collapsed;

    // --- size and closing ---

    private void FitInWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
        MinWidth = Math.Min(MinWidth, area.Width - 40);
        MinHeight = Math.Min(MinHeight, area.Height - 40);
    }

    /// <summary>Hide the summary cards and the headers' small print while the window is too short for them and the lists below.</summary>
    private void FitHeight(double height)
    {
        var now = height < CompactBelow;
        if (now == compact)
            return;
        compact = now;
        foreach (var page in Built)
            page.SetCompact(now);
    }

    /// <summary>Close for good (Exit, or to open again in another language), unless a page asks to wait for a job.</summary>
    public void Quit() => Close();

    // --- automatic sorting ---

    public void RefreshBackgroundStatus()
    {
        var onDisk = ConfigStore.Load();
        Config.PausedUntil = onDisk.PausedUntil; // the tray menu can change these
        Config.AutoSort = onDisk.AutoSort;
        var running = Config.AutoSortOn;
        var paused = Config.IsPaused;
        var color = running && paused ? Ui.Paint("Amber") : running ? Ui.Paint("Primary") : Ui.Paint("ControlLine");
        topBar.SetStatus(Ui.T(running && paused ? "Automatic sorting paused" : running ? "Automatic sorting on" : "Automatic sorting off"), color);
        showingState = true;
        AutoSortSwitch.IsChecked = running;
        showingState = false;
    }

    /// <summary>Automatic sorting only; starting with Windows is a switch of its own.</summary>
    public void SetAutoSort(bool on)
    {
        if (on == Config.AutoSortOn)
        {
        }
        else if (!on)
        {
            UpdateConfig(config => config.AutoSort = false);
        }
        else if (Config.ScanIntervalMinutes == 0)
        {
            Ui.Info("Automatic sorting", Ui.T("The check interval is 0, which keeps automatic sorting off.\n\n"
                                              + "Set it to 1 or more in Settings, save, then turn it on."));
        }
        else if (Ui.Ask("Automatic sorting", Ui.T(
                     "The {0} file(s) on the Sorter page will be moved now, and from then on files will be checked every {1} minutes and moved without asking.\n\n"
                     + "After each run you can review, approve or undo what moved.\n\n"
                     + "It works while PC Butler is running, also from the taskbar tray.\n\nTurn it on?",
                     Sorter.Count, Config.ScanIntervalMinutes)))
        {
            UpdateConfig(config => config.AutoSort = true);
            // The background agent notices the switch within 5 seconds.
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                if (IsLoaded)
                    RefreshPreview();
            };
            later.Start();
        }
        RefreshBackgroundStatus();
    }

    public static void SetAutostart(bool enabled)
    {
        // A trial copy with its own data folder (PCBUTLER_DATA) leaves Windows' start-up list to the installed one.
        if (Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 })
            return;
        try
        {
            Autostart.Set(enabled);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Warning($"Could not change the start at sign-in: {error.Message}");
        }
    }
}
