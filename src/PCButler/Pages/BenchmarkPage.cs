// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>The performance overlay's switches, the live numbers, and the list of game sessions it measured.</summary>
public sealed class BenchmarkPage : Page
{
    private static readonly string[] Corners = ["Top left", "Top right", "Bottom left", "Bottom right"];

    private readonly StatCard[] stats;
    private readonly CheckBox enabled;
    private readonly TextBlock fpsState = Ui.Muted("", 12);
    private readonly Button fpsButton;
    private readonly TextBlock cpuState = Ui.Muted("", 12);
    private readonly Button cpuButton;
    private readonly TextBox games;
    private readonly ListView sessions;
    private int sessionsShown;

    public BenchmarkPage(MainWindow main) : base(main, "Benchmark", "See how your PC is really doing, while you play.")
    {
        stats = AddStats(("Frames per second", ""), ("Processor", ""), ("Graphics card", ""));
        var settings = main.Config.Overlay;

        var card = new CardPanel("Performance overlay", "");
        var rows = new StackPanel();
        enabled = Ui.Switch("", settings.Enabled, on => Change(overlay => overlay.Enabled = on));
        rows.Children.Add(Ui.SettingRow(Ui.T("Show the overlay in games"),
                                        Ui.T("A small panel in a corner of the screen. PC Butler notices a game when it fills the screen, shows the "
                                             + "panel, and hides it again when you leave. Clicks pass through it."), enabled, first: true));
        rows.Children.Add(Ui.SettingRow(Ui.T("Show it all the time"), Ui.T("Not only in games: over whatever program is in front."),
                                        Ui.Switch("", settings.Always, on => Change(overlay => overlay.Always = on))));
        var corner = new ComboBox { Width = 170 };
        foreach (var name in Corners)
            corner.Items.Add(Ui.T(name));
        corner.SelectedIndex = Math.Clamp(settings.Corner, 0, 3);
        corner.SelectionChanged += (_, _) => Change(overlay => overlay.Corner = Math.Max(0, corner.SelectedIndex));
        rows.Children.Add(Ui.SettingRow(Ui.T("Corner"), "", corner));
        var show = new WrapPanel { MaxWidth = 620 };
        foreach (var box in new[]
                 {
                     Check("Frames per second", settings.Fps, (overlay, on) => overlay.Fps = on),
                     Check("Frame time", settings.FrameTime, (overlay, on) => overlay.FrameTime = on),
                     Check("Processor", settings.Cpu, (overlay, on) => overlay.Cpu = on),
                     Check("Graphics card", settings.Gpu, (overlay, on) => overlay.Gpu = on),
                     Check("Temperatures", settings.Temperatures, (overlay, on) => overlay.Temperatures = on),
                     Check("Memory (RAM)", settings.Memory, (overlay, on) => overlay.Memory = on),
                     Check("Video memory (VRAM)", settings.VideoMemory, (overlay, on) => overlay.VideoMemory = on),
                     Check("Graphics card fan", settings.Fan, (overlay, on) => overlay.Fan = on),
                     Check("Network speed", settings.Network, (overlay, on) => overlay.Network = on),
                     Check("Clock", settings.Clock, (overlay, on) => overlay.Clock = on),
                 })
        {
            box.Margin = new Thickness(0, 4, 20, 4);
            show.Children.Add(box);
        }
        rows.Children.Add(Ui.SettingRow(Ui.T("What it shows"), "", show));
        fpsButton = Ui.Button(Ui.T("Start the helper"), StartHelper);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Frames per second need administrator rights"), 13, weight: FontWeights.Bold), fpsState, fpsButton));
        cpuButton = Ui.Button(Ui.T("Get PawnIO"), () => Shell.Open(CpuSensor.DriverPage));
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Processor temperature"), 13, weight: FontWeights.Bold), cpuState, cpuButton));
        games = Ui.Field(string.Join(", ", settings.Games), 300, "game.exe, emulator.exe");
        games.LostFocus += (_, _) => SaveGames();
        rows.Children.Add(Ui.SettingRow(Ui.T("Always treat these programs as games"),
                                        Ui.T("For a game that runs in a window. The programs on Boost's automatic list count as games too."), games));
        card.SetBody(rows);

        var log = new CardPanel("Game sessions", "");
        log.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearSessions));
        sessions = Ui.List(("Game", 220, "PlainCell"), ("When", 150), ("Length", 90), ("Average FPS", 110), ("Slowest 1%", 110), ("Highest FPS", 110),
                           ("Processor", 100), ("Graphics card", 110), ("Hottest", 170));
        sessions.MinHeight = 180;
        log.SetBody(sessions);
        log.Margin = new Thickness(0, 16, 0, 0);

        var all = new StackPanel();
        all.Children.Add(card);
        all.Children.Add(log);
        Children.Add(new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        ShowSessions();
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (_, _) => ShowLive();
        watch.Start();
        ShowLive();
    }

    private CheckBox Check(string label, bool on, Action<OverlaySettings, bool> set)
    {
        var box = new CheckBox { Content = Ui.T(label), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        box.Click += (_, _) => Change(overlay => set(overlay, box.IsChecked == true));
        return box;
    }

    private void Change(Action<OverlaySettings> change)
    {
        Main.UpdateConfig(config => change(config.Overlay));
        App.Current.Overlay.Apply(Main.Config);
        ShowLive();
    }

    /// <summary>The overlay was switched by its hotkey: show that here too.</summary>
    public void ShowEnabled() => enabled.IsChecked = Main.Config.Overlay.Enabled;

    private void SaveGames()
    {
        var names = games.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        games.Text = string.Join(", ", names);
        if (!names.SequenceEqual(Main.Config.Overlay.Games))
            Change(overlay => overlay.Games = names);
    }

    private async void StartHelper()
    {
        fpsButton.IsEnabled = false;
        if (await Admin.Ensure("Benchmark", Ui.T("Windows only tells an administrator when a program shows a frame, so counting frames per second "
                                                 + "needs administrator rights. Nothing is added to the game itself.")))
            await Task.Run(() => AdminHelper.Send("fps", ["on"], 10));
        fpsButton.IsEnabled = true;
        ShowLive();
    }

    private void ShowLive()
    {
        if (!IsVisible)
            return;
        var overlay = App.Current.Overlay;
        if (overlay.SessionsSaved != sessionsShown)
            ShowSessions();
        var helper = AdminHelper.Running;
        fpsState.Text = Ui.T(overlay.CountingFrames ? "The helper is counting frames. It stops when PC Butler closes."
                             : helper ? "The helper is running; frames are counted while the overlay is on."
                             : "Without the helper the overlay shows everything except frames per second. Start it once each time PC Butler starts.");
        fpsButton.Visibility = helper ? Visibility.Collapsed : Visibility.Visible;
        var driver = CpuSensor.DriverInstalled;
        cpuState.Text = Ui.T(!driver ? "Reading an Intel or AMD processor's own temperature needs a driver. PC Butler uses PawnIO, a free, signed one, "
                                       + "and does not install it for you: get it from its own site, then start the helper."
                             : Sensors.Latest()?.CpuTemperature is not null ? "PawnIO is installed and the helper is reading the processor's temperature."
                             : helper ? "PawnIO is installed, but this processor gave no temperature."
                             : "PawnIO is installed. Start the helper and the temperature appears.");
        cpuButton.Visibility = driver ? Visibility.Collapsed : Visibility.Visible;
        var reading = overlay.Latest;
        if (reading is null)
        {
            var usage = App.Current.Usage.Latest;
            stats[0].Set("—", "", Ui.T("The overlay is off"));
            stats[1].Set(usage is null ? "—" : usage.Cpu.ToString(), usage is null ? "" : "%", "");
            stats[2].Set(usage?.Gpu?.Usage is { } idle ? idle.ToString() : "—", usage?.Gpu?.Usage is null ? "" : "%", "");
            return;
        }
        stats[0].Set(reading.Fps?.ToString() ?? "—", reading.Fps is null ? "" : "FPS",
                     overlay.Game.Length > 0 ? Ui.T("Measuring {0}", overlay.Game) : Ui.T("Waiting for a game"));
        stats[1].Set(reading.Cpu.ToString(), "%", reading.CpuTemperature is { } cpu ? $"{cpu}°C" : "");
        stats[2].Set(reading.Gpu?.ToString() ?? "—", reading.Gpu is null ? "" : "%", reading.GpuTemperature is { } gpu ? $"{gpu}°C" : "");
    }

    private void ShowSessions()
    {
        sessionsShown = App.Current.Overlay.SessionsSaved;
        static string Number(int? value, string unit = "") => value is { } number ? number + unit : "—";
        sessions.ItemsSource = BenchmarkLog.All().Select(session => new Row
        {
            Name = session.Game,
            Cells =
            [
                session.Started, Courier.Time(TimeSpan.FromSeconds(session.Seconds)), Number(session.FpsAverage), Number(session.FpsLow), Number(session.FpsHigh),
                session.CpuAverage + "%", Number(session.GpuAverage, "%"),
                string.Join("  ·  ", new[] { session.CpuHottest is { } cpu ? $"CPU {cpu}°C" : null, session.GpuHottest is { } gpu ? $"GPU {gpu}°C" : null }.OfType<string>()),
            ],
        }).ToList();
    }

    private void ClearSessions()
    {
        var count = BenchmarkLog.All().Count;
        if (count == 0 || !Ui.Ask("Clear history", Ui.T("Clear all {0} game sessions?", count)))
            return;
        BenchmarkLog.Clear();
        ShowSessions();
    }
}
