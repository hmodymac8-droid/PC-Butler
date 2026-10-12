// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>The control panel: sidebar, top bar, the eight pages and the status bar.</summary>
public sealed partial class MainWindow : Window
{
    private const double CompactBelow = 720; // shorter than this, the summary cards and small print go
    private readonly Sidebar sidebar;
    private readonly TopBar topBar = new();
    private readonly Grid pageHost = new();
    private readonly Dictionary<string, (Page Page, string Name)> pages = [];
    private readonly Border banner;
    private readonly TextBlock bannerLabel = Ui.Text("");
    private readonly TextBlock statusGlyph = Ui.Glyph("\uE895", 16);
    private readonly TextBlock backgroundLabel = Ui.Text("");
    /// <summary>Turns automatic sorting on or off. It lives on the Settings page and follows the state.</summary>
    public CheckBox AutoSortSwitch { get; }
    private bool showingState; // set while the switch is moved to match the state, not by a click
    private readonly Button pauseButton;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool? compact;

    public Config Config { get; private set; }
    public SorterPage Sorter { get; }
    public DuplicatesPage Duplicates { get; }
    public HistoryPage History { get; }
    public VaultPage Vault { get; }
    private readonly Dictionary<string, string> glyphs = [];
    public ShredderPage Shredder { get; }
    public SettingsPage Settings { get; }
    public AboutPage About { get; }

    public MainWindow()
    {
        Ui.StyleWindow(this);
        Title = AppInfo.Name;
        Config = ConfigStore.Load();
        Width = 1200;
        Height = 800;
        MinWidth = 980;
        MinHeight = 680;
        FitInWorkArea();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var category in Config.Categories)
            Ui.Tone(System.IO.Path.GetFileName(category.Folder)); // fixes each folder's tile colour, in settings order

        sidebar = new Sidebar(ShowPage);
        var main = new DockPanel();
        main.Children.Add(topBar.Dock(Dock.Top));

        // Shown across the top only when a newer version exists.
        var bannerRow = new DockPanel { LastChildFill = false };
        bannerRow.Children.Add(bannerLabel);
        bannerRow.Children.Add(Ui.Button(Ui.T("Later"), () => banner!.Visibility = Visibility.Collapsed).Dock(Dock.Right));
        bannerRow.Children.Add(Ui.Button(Ui.T("Update now"), () => About!.InstallUpdate(), accent: true).Margin(0, 0, 6, 0).Dock(Dock.Right));
        banner = new Border { Padding = new Thickness(22, 10, 22, 10), Child = bannerRow, Visibility = Visibility.Collapsed };
        main.Children.Add(banner.Dock(Dock.Top));

        // The status bar. Its buttons come first so a narrow window cuts the text, not them.
        var bar = new DockPanel { Margin = new Thickness(30, 12, 30, 12) };
        AutoSortSwitch = Ui.Switch("", false, on =>
        {
            if (!showingState)
                SetAutoSort(on);
        });
        pauseButton = Ui.Button("", TogglePause).Margin(0, 0, 6, 0).Dock(Dock.Right);
        bar.Children.Add(new Border { Background = Ui.Paint("Secondary"), Padding = new Thickness(8, 4, 8, 4), Child = statusGlyph,
                                      Margin = new Thickness(0, 0, 12, 0) }.Dock(Dock.Left));
        bar.Children.Add(pauseButton);
        backgroundLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        bar.Children.Add(backgroundLabel);
        main.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar }.Dock(Dock.Bottom));
        main.Children.Add(pageHost);

        Sorter = new SorterPage(this);
        Duplicates = new DuplicatesPage(this);
        History = new HistoryPage(this);
        Vault = new VaultPage(this);
        Shredder = new ShredderPage(this);
        Settings = new SettingsPage(this);
        About = new AboutPage(this);
        // (key, icon, sidebar name, page, in the sidebar's bottom group)
        foreach (var (key, glyph, name, page, bottom) in new (string, string, string, Page, bool)[]
                 {
                     ("home", "\uE80F", "Home", new HomePage(this), false),
                     ("preview", "\uE8FD", "Sorter", Sorter, false),
                     ("duplicates", "\uE8C8", "Duplicates", Duplicates, false),
                     ("cleaner", "\uE74D", "Cleaner", new CleanerPage(this), false),
                     ("storage", "\uEDA2", "Storage", new StoragePage(this), false),
                     ("booster", "\uE945", "Booster", new BoosterPage(this), false),
                     ("programs", "\uE71D", "Programs", new ProgramsPage(this), false),
                     ("internet", "\uE774", "Internet", new InternetPage(this), false),
                     ("shredder", "\uE75C", "Shredder", Shredder, false),
                     ("clipboard", "\uE77F", "Clipboard", new ClipboardPage(this), false),
                     ("vault", "\uE72E", "Vault", Vault, false),
                     ("history", "\uE81C", "History", History, false),
                     ("settings", "\uE713", "Settings", Settings, true),
                     ("about", "\uE946", "Help & about", About, true),
                 })
        {
            page.Visibility = Visibility.Collapsed;
            pageHost.Children.Add(page);
            pages[key] = (page, Ui.T(name));
            glyphs[key] = glyph;
            sidebar.Add(key, glyph, Ui.T(name), bottom);
        }
        sidebar.ShowStorage(ConfigStore.DetectDownloadsFolder());

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(sidebar.At(0, 0));
        root.Children.Add(main.At(0, 1));
        root.SizeChanged += (_, _) => FitHeight(root.ActualHeight);
        Content = root;

        SourceInitialized += (_, _) => RestoreGeometry();
        Closing += (_, e) =>
        {
            if (pages.Values.All(entry => entry.Page.CanClose()))
                OnClosing();
            else
                e.Cancel = true;
        };
        RefreshPreview();
        RefreshBackgroundStatus();
        statusTimer.Tick += (_, _) => RefreshBackgroundStatus();
        statusTimer.Start();
        ShowPage("home");
        var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        firstCheck.Tick += (_, _) =>
        {
            firstCheck.Stop();
            About.CheckUpdates();
        };
        firstCheck.Start();
    }

    /// <summary>Show the window (after the launch screen, when there is one).</summary>
    public void Reveal()
    {
        WindowState = WindowState.Maximized; // always opens filling the screen
        Show();
        Activate();
    }

    public void ShowPage(string key)
    {
        foreach (var (page, _) in pages.Values)
            page.Visibility = Visibility.Collapsed;
        var (shown, name) = pages[key];
        shown.Visibility = Visibility.Visible;
        sidebar.Select(key);
        topBar.SetPage(glyphs[key], name);
        shown.Shown();
    }

    public void SetBadge(string key, string text) => sidebar.SetBadge(key, text);

    /// <summary>Change settings on top of what is on disk, since the tray menu writes there too.</summary>
    public void UpdateConfig(Action<Config> change)
    {
        var config = ConfigStore.Load();
        change(config);
        ConfigStore.Save(config);
        Config = config;
    }

    /// <summary>Plan the Sorter list again, and History with it, after anything that moves files.</summary>
    public void RefreshPreview()
    {
        Sorter.Refresh();
        History.Refresh();
    }

    /// <summary>Files sent to the vault from File Explorer or the drop zone: open the Vault page and add them (after the password).</summary>
    public void TakeVaultInbox()
    {
        var waiting = VaultInbox.Take();
        if (waiting.Count == 0)
            return;
        ShowPage("vault");
        Vault.AddFromOutside(waiting);
    }

    /// <summary>Put items on the Shredder page's list. Nothing is shredded until the user presses Shred there.</summary>
    public void ShredLater(IEnumerable<string> paths)
    {
        ShowPage("shredder");
        Shredder.AddPaths(paths);
    }

    /// <summary>The background sorter moved or cleaned files.</summary>
    public void DataChanged()
    {
        RefreshBackgroundStatus();
        History.Refresh();
    }

    // --- updates ---

    public void UpdateFound(UpdateInfo? update) => About.Found(update);

    public void ShowBanner(string text)
    {
        bannerLabel.Text = text;
        banner.Visibility = Visibility.Visible;
    }

    public void HideBanner() => banner.Visibility = Visibility.Collapsed;

    // --- window size and closing ---

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
        foreach (var (page, _) in pages.Values)
            page.SetCompact(now);
    }

    [GeneratedRegex(@"^(\d+)x(\d+)\+(-?\d+)\+(-?\d+)$")]
    private static partial Regex GeometryPattern();

    /// <summary>The last size and position, saved as "WIDTHxHEIGHT+LEFT+TOP" in screen pixels.</summary>
    private void RestoreGeometry()
    {
        var match = GeometryPattern().Match(Config.WindowGeometry.Trim());
        if (!match.Success)
            return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double Number(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) / scale;
        double width = Number(1), height = Number(2), left = Number(3), top = Number(4);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Only where it can still be seen (a monitor may have gone since).
        if (width < 200 || height < 200 || !screen.Contains(new Point(left + 60, top + 20)))
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);
        Left = left;
        Top = top;
    }

    private string Geometry()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        int Px(double value) => (int)Math.Round(value * scale);
        return $"{Px(bounds.Width)}x{Px(bounds.Height)}+{Px(bounds.Left)}+{Px(bounds.Top)}";
    }

    private void OnClosing()
    {
        statusTimer.Stop();
        foreach (var (page, _) in pages.Values)
            page.Closing(); // saves edits to opened vault files and the Duplicates settings
        var geometry = Geometry();
        UpdateConfig(config => config.WindowGeometry = geometry);
    }

    // --- automatic sorting ---

    public void RefreshBackgroundStatus()
    {
        var onDisk = ConfigStore.Load();
        Config.PausedUntil = onDisk.PausedUntil; // the tray menu can change these
        Config.AutoSort = onDisk.AutoSort;
        // The tray icon is part of this process, so automatic sorting runs whenever it is on.
        var running = Config.AutoSortOn;
        var paused = Config.IsPaused;
        string text;
        if (running && paused)
            text = Ui.T("Automatic sorting is PAUSED until {0}", Config.PausedUntil);
        else if (running)
            text = Ui.T("Automatic sorting is ON (see the icon in the taskbar tray)");
        else if (Config.ScanIntervalMinutes == 0)
            text = Ui.T("Automatic sorting is OFF: the check interval is set to 0");
        else
            text = Ui.T("Automatic sorting is OFF: nothing moves until you click Move");
        backgroundLabel.Text = text;
        var color = running && paused ? Ui.Paint("Amber") : running ? Ui.Paint("Primary") : Ui.Paint("ControlLine");
        statusGlyph.Foreground = color;
        topBar.SetStatus(Ui.T(running && paused ? "Automatic sorting paused" : running ? "Automatic sorting on" : "Automatic sorting off"), color);
        showingState = true;
        AutoSortSwitch.IsChecked = running;
        showingState = false;
        pauseButton.Content = Ui.T(paused ? "Resume" : "Pause for today");
        pauseButton.IsEnabled = running;
    }

    private void TogglePause()
    {
        UpdateConfig(config => config.PausedUntil = config.IsPaused ? "" : ConfigStore.PauseUntilTomorrow());
        RefreshBackgroundStatus();
    }

    /// <summary>Automatic sorting only; starting with Windows is a switch of its own.</summary>
    private void SetAutoSort(bool on)
    {
        if (on == Config.AutoSortOn)
        {
        }
        else if (!on)
        {
            // The tray icon stays; only the scheduled sorting stops.
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
            // The background sorter notices the switch within 5 seconds.
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
