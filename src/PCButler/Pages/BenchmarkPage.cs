// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The performance overlay's switches, the live numbers, the list of game sessions it
/// measured, and the record of what each app used (processor, graphics card, memory, disk and
/// internet), as a report for a period that can be saved as a spreadsheet.
/// </summary>
public sealed class BenchmarkPage : Page
{
    private readonly StatCard[] stats;
    private readonly CheckBox enabled;
    private readonly TextBlock fpsState = Ui.Muted("", 12);
    private readonly Button fpsButton;
    private readonly TextBlock cpuState = Ui.Muted("", 12);
    private readonly Button cpuButton;
    private readonly TextBox games;
    private readonly ListView sessions;
    private int sessionsShown;
    private readonly ListView usage;
    private readonly ComboBox period = new() { Width = 170, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock usageNote = Ui.Muted("", 12);
    private readonly Button netButton;
    private List<AppUseRow> usageRows = [];
    private DateTime usageShown;

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
        rows.Children.Add(Ui.SettingRow(Ui.T("Temperatures"), Ui.T("Beside the processor's and the graphics card's use."),
                                        Ui.Switch("", settings.Temperatures, on => Change(overlay => overlay.Temperatures = on))));
        fpsButton = Ui.Button(Ui.T("Start the helper"), StartHelper);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Counting frames per second"), 13, weight: FontWeights.Bold), fpsState, fpsButton));
        rows.Children.Add(Ui.SettingRow(Ui.T("Start the helper by itself when a game starts"),
                                        Ui.T("So frames are counted without coming here first. Windows asks for your permission once each time PC Butler "
                                             + "runs, when the first game comes up."),
                                        Ui.Switch("", settings.AutoHelper, on => Change(overlay => overlay.AutoHelper = on))));
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

        // --- what each app used, for a report ---
        var apps = new CardPanel("App usage", "") { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var choice in new[] { "Today", "Yesterday", "Last 7 days", "Last 30 days", "Last 90 days" })
            period.Items.Add(new ComboBoxItem { Content = Ui.T(choice), Tag = choice });
        period.SelectedIndex = 0;
        period.SelectionChanged += (_, _) => ShowUsage();
        apps.Controls.Children.Add(period);
        apps.Controls.Children.Add(Ui.Button(Ui.T("Save as a spreadsheet…"), ExportUsage).Margin(6));
        apps.Controls.Children.Add(Ui.Button(Ui.T("Clear"), ClearUsage).Margin(6));
        var usageBody = new StackPanel();
        usageBody.Children.Add(Ui.SettingRow(Ui.T("Record what each app uses"),
                                             Ui.T("Every few seconds, how much of the processor, the graphics card, the memory and the disk each running app takes. "
                                                  + "Kept on this PC for 90 days, for the report below."),
                                             Ui.Switch("", main.Config.Usage.Record, on =>
                                             {
                                                 Main.UpdateConfig(config => config.Usage.Record = on);
                                                 App.Current.RecordUsage(on);
                                                 ShowUsage();
                                             }), first: true));
        netButton = Ui.Button(Ui.T("Start the helper"), StartNetHelper);
        usageBody.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Internet use per app"), 13, weight: FontWeights.Bold), usageNote, netButton));
        usage = Ui.List(("App", 200, "PlainCell"), ("Running", 100), ("Processor", 150), ("Graphics card", 150), ("Memory (average, peak)", 170),
                        ("Disk read", 100), ("Disk written", 110), ("Internet sent", 110), ("Internet received", 130));
        usage.MinHeight = 220;
        usageBody.Children.Add(usage);
        apps.SetBody(usageBody);

        var all = new StackPanel();
        all.Children.Add(card);
        // Arranging the overlay yourself: what it shows and in what order, and how it looks.
        var shows = BenchmarkLook.Rows(() => Main.Config.Overlay, Change);
        shows.Margin = new Thickness(0, 16, 0, 0);
        all.Children.Add(shows);
        var look = BenchmarkLook.Look(() => Main.Config.Overlay, Change);
        look.Margin = new Thickness(0, 16, 0, 0);
        all.Children.Add(look);
        all.Children.Add(log);
        all.Children.Add(apps);
        Children.Add(new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        ShowSessions();
        ShowUsage();
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
        // The record grows every few seconds; the report follows it now and then.
        if (DateTime.Now - usageShown > TimeSpan.FromSeconds(30))
            ShowUsage();
        var overlay = App.Current.Overlay;
        if (overlay.SessionsSaved != sessionsShown)
            ShowSessions();
        var helper = AdminHelper.Running;
        fpsState.Text = Ui.T(overlay.CountsItself ? "Frames are counted with no helper: Windows allows this account to. Nothing to start."
                             : overlay.CountingFrames ? "The helper is counting frames. It stops when PC Butler closes."
                             : helper ? "The helper is running; frames are counted while the overlay is on."
                             : "Without the helper the overlay shows everything except frames per second. Start it once each time PC Butler starts.");
        fpsButton.Visibility = helper || overlay.CountsItself ? Visibility.Collapsed : Visibility.Visible;
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

    // --- what each app used ---

    /// <summary>The first and last day of the chosen period.</summary>
    private (DateTime First, DateTime Last) Period() => ((period.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "Yesterday" => (DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1)),
        "Last 7 days" => (DateTime.Today.AddDays(-6), DateTime.Today),
        "Last 30 days" => (DateTime.Today.AddDays(-29), DateTime.Today),
        "Last 90 days" => (DateTime.Today.AddDays(-89), DateTime.Today),
        _ => (DateTime.Today, DateTime.Today),
    };

    private void ShowUsage()
    {
        usageShown = DateTime.Now;
        var cores = Environment.ProcessorCount;
        var (first, last) = Period();
        usageRows = AppUsageLog.Report(first, last);
        // Internet use is only known for the time the helper counted it; the rest shows as unknown, not as nothing.
        string Traffic(AppUse use, long bytes) => use.NetSeconds > 0 ? Ui.FormatSize(bytes) : "—";
        usage.ItemsSource = usageRows.Select(row => new Row
        {
            Name = row.App,
            Cells =
            [
                Courier.Time(TimeSpan.FromSeconds(row.Use.Seconds)),
                $"{row.Use.CpuPercent(cores):0.#}%  ·  {Courier.Time(TimeSpan.FromSeconds(row.Use.CpuSeconds))}",
                row.Use.GpuSeconds > 0 ? $"{row.Use.GpuPercent:0.#}%  ·  {Courier.Time(TimeSpan.FromSeconds(row.Use.GpuSeconds))}" : "—",
                $"{Ui.FormatSize(row.Use.AverageMemory)}, {Ui.FormatSize(row.Use.MemoryPeak)}",
                Ui.FormatSize(row.Use.Read), Ui.FormatSize(row.Use.Written), Traffic(row.Use, row.Use.Sent), Traffic(row.Use, row.Use.Received),
            ],
        }).ToList();
        var helper = AdminHelper.Running;
        usageNote.Text = Ui.T(!Main.Config.Usage.Record ? "Recording is off."
                              : helper ? "The helper is counting each app's internet traffic. It stops when PC Butler closes."
                              : "Windows only tells an administrator which app sends what, so internet use is counted while the helper runs. Without it, those columns show —.");
        netButton.Visibility = helper || !Main.Config.Usage.Record ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void StartNetHelper()
    {
        netButton.IsEnabled = false;
        if (await Admin.Ensure("Benchmark", Ui.T("Windows only tells an administrator how much each app sends and receives over the internet, so counting it "
                                                 + "needs administrator rights. Only the amounts are counted, never what is sent.")))
            await Task.Run(() => AdminHelper.Send("net", ["on"], 10));
        netButton.IsEnabled = true;
        ShowUsage();
    }

    private void ExportUsage()
    {
        if (usageRows.Count == 0)
        {
            Ui.Info("Benchmark", Ui.T("Nothing has been recorded for this period yet."));
            return;
        }
        var (first, last) = Period();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Ui.T("Save the report"), Filter = Ui.T("Spreadsheet (CSV)") + "|*.csv",
            FileName = first == last ? $"App usage {first:yyyy-MM-dd}.csv" : $"App usage {first:yyyy-MM-dd} to {last:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            // With a byte-order mark, so Excel reads app names in any language correctly.
            System.IO.File.WriteAllText(dialog.FileName, AppUsageLog.Csv(usageRows, Environment.ProcessorCount), new System.Text.UTF8Encoding(true));
            Shell.Reveal(dialog.FileName);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Ui.Warn("Benchmark", Ui.T("The report could not be saved: {0}", error.Message));
        }
    }

    private void ClearUsage()
    {
        if (AppUsageLog.FirstDay is null || !Ui.Ask("Clear", Ui.T("Forget everything recorded about what each app used?")))
            return;
        AppUsageLog.Clear();
        ShowUsage();
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
