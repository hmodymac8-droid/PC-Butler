// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The PC at a glance: gauges for the processor, the graphics card, the memory and the time
/// since the last restart, the free space, and under them the drives, the temperatures and
/// the neatness score with what is holding it back.
/// </summary>
public sealed class HomePage : Page
{
    private readonly MetricCard processor = new("Processor", "", "CPU");
    private readonly MetricCard graphics = new("Graphics card", "", "GPU");
    private readonly MetricCard memory = new("Memory", "", "RAM");
    private readonly MetricCard restart = new("Uptime", "", "");
    private readonly AmountCard space = new("Free space", "");
    private readonly StackPanel drives = new();
    private readonly TextBlock gpuTemperature = Ui.Text("—", 13);
    private readonly TextBlock cpuTemperature = Ui.Text("—", 13);
    private readonly TextBlock sensorsText = Ui.Muted("", 12.5, wrap: true);
    private readonly Button sensorsButton;
    private readonly TextBlock score = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Ui.Paint("Primary"), FontFamily = Ui.DisplayFont, Text = "—" };
    private readonly MeterBar scoreBar = new();
    private readonly TextBlock scoreNote = Ui.Muted("", 12.5, wrap: true);
    private readonly StackPanel notes = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(UsageMonitor.IntervalSeconds) };
    private bool scoring;

    public HomePage(MainWindow main) : base(main, "Welcome back.", "Your PC at a glance.")
    {
        AddAction("Refresh", Recalculate);
        ShowVersionInsteadOfHeading();

        // Three gauges, then the restart gauge beside the free space, which takes two columns.
        var cards = new Grid();
        for (var column = 0; column < 3; column++)
            cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(cards, processor, 0, 0);
        Place(cards, graphics, 0, 1);
        Place(cards, memory, 0, 2);
        Place(cards, restart, 1, 0);
        Place(cards, space, 1, 1, 2);

        var under = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 8) };
        var underTitle = Ui.Text(Ui.T("Under the hood"), 17, weight: FontWeights.Medium);
        underTitle.FontFamily = Ui.DisplayFont;
        under.Children.Add(underTitle);
        under.Children.Add(Ui.Row(6, Ui.Glyph("", 13), Ui.Muted(Ui.T("System overview"), 13)).Dock(Dock.Right));

        // Storage: every drive, the graphics card's temperature, and what else can be read.
        var storage = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
        storage.Children.Add(PanelTitle("", "Storage"));
        storage.Children.Add(drives);
        var processorTemperature = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        processorTemperature.Children.Add(Ui.Row(8, Ui.Glyph("\uE950", 14), Ui.Text(Ui.T("Processor temperature"), 13)));
        processorTemperature.Children.Add(cpuTemperature.Dock(Dock.Right));
        storage.Children.Add(Line(processorTemperature, 10));
        var temperature = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        temperature.Children.Add(Ui.Row(8, Ui.Glyph("", 14), Ui.Text(Ui.T("Graphics card temperature"), 13)));
        temperature.Children.Add(gpuTemperature.Dock(Dock.Right));
        storage.Children.Add(Line(temperature, 10));
        var sensors = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        sensors.Children.Add(sensorsText);
        sensorsButton = Ui.Button(Ui.T("Start the helper (asks for administrator rights)"), TurnOnSensors);
        sensorsButton.HorizontalAlignment = HorizontalAlignment.Left;
        sensors.Children.Add(sensorsButton.Margin(0, 8));
        storage.Children.Add(Line(sensors, 10));

        // The neatness score, and under it what is holding it back.
        var neat = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
        var neatTitle = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        neatTitle.Children.Add(Ui.Row(9, Ui.Glyph("", 15), Ui.Text(Ui.T("Neatness score"), 14, weight: FontWeights.Medium)));
        var outOf = Ui.Muted(Ui.T("out of 100"), 13).Margin(6, 0, 0, 3);
        outOf.VerticalAlignment = VerticalAlignment.Bottom;
        neatTitle.Children.Add(Ui.Row(0, score, outOf).Dock(Dock.Right));
        neat.Children.Add(neatTitle);
        neat.Children.Add(scoreBar);
        var ends = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 7, 0, 0) };
        ends.Children.Add(Ui.Muted(Ui.T("Needs work"), 12.5));
        ends.Children.Add(Ui.Muted(Ui.T("Pristine"), 12.5).Dock(Dock.Right));
        neat.Children.Add(ends);
        neat.Children.Add(scoreNote.Margin(0, 9, 0, 4));
        neat.Children.Add(notes);
        var alerts = Ui.Switch(Ui.T("Warn me when the processor or memory stays nearly full"), main.Config.UsageAlerts,
                               on => Main.UpdateConfig(config => config.UsageAlerts = on));
        alerts.HorizontalAlignment = HorizontalAlignment.Left;
        neat.Children.Add(Line(alerts, 9));

        var panels = new Grid();
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Child = storage, Margin = new Thickness(0, 0, 6, 0) };
        var right = new Border { Child = neat, Margin = new Thickness(6, 0, 0, 0) };
        Widgets.StyleCard(left);
        Widgets.StyleCard(right);
        panels.Children.Add(left.At(0, 0));
        panels.Children.Add(right.At(0, 1));

        var all = new StackPanel { Children = { cards, under, panels } };
        Children.Add(new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        processor.Set("—", "", 0, "Primary", "", "", "");
        graphics.Set("—", "", 0, "Primary", "", "", "");
        memory.Set("—", "", 0, "Blue", "", "", "");
        timer.Tick += (_, _) => ShowLive();
        IsVisibleChanged += (_, _) =>
        {
            // The graphics card is only read while this page is on screen.
            App.Current.Usage.WantGpu = IsVisible;
            timer.IsEnabled = IsVisible;
        };
    }

    private static void Place(Grid grid, FrameworkElement card, int row, int column, int span = 1)
    {
        // Gaps of 12 between cards, none at the outer edges.
        var last = column + span == 3;
        card.Margin = new Thickness(column == 0 ? 0 : 6, row == 0 ? 0 : 12, last ? 0 : 6, 0);
        grid.Children.Add(card.At(row, column, span));
    }

    private static UIElement PanelTitle(string glyph, string text) =>
        Ui.Row(9, Ui.Glyph(glyph, 15), Ui.Text(Ui.T(text), 14, weight: FontWeights.Medium)).Margin(0, 0, 0, 12);

    /// <summary>The element under a hairline, as the design separates the parts of a panel.</summary>
    private static Border Line(UIElement child, double space) =>
        new() { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, space, 0, 0), Child = child };

    public override void Shown()
    {
        ShowLive();
        ShowFacts();
        Recalculate();
    }

    private void ShowLive()
    {
        if (App.Current.Usage.Latest is not { } sample)
            return;
        var busy = sample.Cpu >= 85;
        // The processor's own temperature: from the helper (with the PawnIO driver), or from a hardware-monitor program.
        var cpuDegrees = Sensors.Latest()?.CpuTemperature ?? Sensors.MonitorApp()?.Cpu;
        var cpuHot = cpuDegrees >= 90;
        processor.Set(sample.Cpu.ToString(), Ui.T("% usage"), sample.Cpu, busy ? "Amber" : "Primary",
                      Ui.T(busy ? "Working hard" : sample.Cpu >= 50 ? "Busy" : "Running smoothly"),
                      cpuDegrees is { } shown ? $"{Math.Round(shown)}°C  ·  " + Ui.T("{0} processor threads", Environment.ProcessorCount)
                                              : Ui.T("{0} processor threads", Environment.ProcessorCount),
                      Ui.T(cpuHot ? "Running hot" : busy ? "High activity" : "Normal activity"));
        cpuTemperature.Text = cpuDegrees is { } degreesNow ? $"{Math.Round(degreesNow)}°C  ·  " + Ui.T(cpuHot ? "Hot" : "Normal") : "—";
        cpuTemperature.Foreground = Ui.Paint(cpuHot ? "Amber" : "Text");
        var full = sample.MemoryPercent >= 85;
        var used = Ui.FormatSize(sample.MemoryUsed).Split(' ');
        memory.Set(used[0], Ui.T("{0} in use", used[1]), sample.MemoryPercent, full ? "Amber" : "Blue",
                   Ui.T("{0}% of total memory", sample.MemoryPercent), Ui.T("{0} installed", Ui.FormatSize(sample.MemoryTotal)),
                   Ui.T(full ? "High usage" : "Normal usage"));
        if (sample.Gpu is { } gpu)
        {
            if (gpu.Temperature is { } degrees)
            {
                var hot = degrees >= 85;
                graphics.Set(degrees.ToString(), "°C", degrees * 100.0 / 110, hot ? "Amber" : "Primary", gpu.Name,
                             gpu.Usage is { } use ? Ui.T("{0}% in use", use) : "", Ui.T(hot ? "Running hot" : "Normal temperature"));
                gpuTemperature.Text = $"{degrees}°C  ·  " + Ui.T(hot ? "Hot" : "Normal");
                gpuTemperature.Foreground = Ui.Paint(hot ? "Amber" : "Text");
            }
            else
            {
                graphics.Set(gpu.Usage?.ToString() ?? "—", gpu.Usage is null ? "" : Ui.T("% usage"), gpu.Usage ?? 0, "Primary", gpu.Name,
                             Ui.T("It does not report a temperature"), Ui.T("Normal activity"));
                gpuTemperature.Text = "—";
            }
        }
        ShowSensors();
    }

    private void ShowFacts()
    {
        var since = DateTime.Now - UsageMonitor.LastRestart;
        var (number, unit) = since.TotalDays >= 2 ? (((int)since.TotalDays).ToString(), Ui.T("days ago"))
                           : since.TotalHours >= 1 ? (((int)since.TotalHours).ToString(), Ui.T("hours ago"))
                           : (Math.Max(1, (int)since.TotalMinutes).ToString(), Ui.T("minutes ago"));
        var length = since.TotalDays >= 1 ? Ui.T("{0} d {1} h", (int)since.TotalDays, since.Hours) : Ui.T("{0} h {1} min", (int)since.TotalHours, since.Minutes);
        var stale = since.TotalDays >= 7;
        // A full ring is one day; after that it stays full.
        restart.Set(number, unit, since.TotalHours * 100 / 24, stale ? "Amber" : "Blue", length, UsageMonitor.LastRestart.ToString("yyyy-MM-dd HH:mm"),
                    Ui.T(stale ? "A restart would help" : "Up and running"));

        var all = UsageMonitor.Drives();
        long total = all.Sum(drive => drive.Total), free = all.Sum(drive => drive.Free);
        var freeShare = total > 0 ? free * 100.0 / total : 0;
        var parts = Ui.FormatSize(free).Split(' ');
        space.Set(parts[0], Ui.T("{0} available", parts[1]), 100 - freeShare, freeShare < 10 ? "Red" : freeShare < 20 ? "Amber" : "Primary",
                  Ui.T("Used · {0}", Ui.FormatSize(total - free)), Ui.T("Free · {0}", Ui.FormatSize(free)),
                  Ui.T(freeShare < 20 ? "Consider a cleanup" : "Plenty of room"), Ui.T("{0} drive(s)", all.Count));

        drives.Children.Clear();
        var index = 0;
        foreach (var drive in all)
        {
            var tone = Ui.DriveTone(index++);
            var percent = drive.Total > 0 ? drive.Used * 100.0 / drive.Total : 0;
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Ui.Text(Ui.T("Local disk ({0})", drive.Name), 13));
            row.Children.Add(Ui.Muted(Ui.T("{0} free of {1}", Ui.FormatSize(drive.Free), Ui.FormatSize(drive.Total)), 12.5).Dock(Dock.Right));
            drives.Children.Add(row);
            var bar = new MeterBar(5);
            // Each drive its own colour, as on the Storage page; red once it is nine-tenths full.
            bar.Set(percent, percent >= 90 ? Ui.Paint("Red") : tone);
            drives.Children.Add(bar.Margin(0, 0, 0, 12));
        }
    }

    private void ShowSensors()
    {
        var lines = new List<string>();
        var gpu = App.Current.Usage.Latest?.Gpu;
        if (gpu?.Fan is { } gpuFan)
            lines.Add(Ui.T("Graphics card fan: {0} RPM", gpuFan));
        var monitor = Sensors.MonitorApp();
        var reading = Sensors.Latest();
        var fans = monitor?.Fans.Count > 0 ? monitor.Value.Fans : reading?.Fans ?? [];
        if (reading?.CpuTemperature is null && monitor?.Cpu is not null)
            lines.Add(Ui.T("The processor's temperature comes from {0}.", monitor.Value.App));
        if (reading?.Temperature is { } zone && monitor?.Cpu is null)
            lines.Add(Ui.T("Motherboard sensor: {0} °C", zone));
        if (fans.Count > 0)
            lines.Add(Ui.T("Fans: {0} RPM", string.Join(", ", fans)));
        if (monitor?.Cpu is null && reading?.CpuTemperature is null)
            lines.Add(Ui.T(CpuSensor.DriverInstalled
                ? "The processor's temperature appears here while the helper is running (the button below)."
                : "The processor's own temperature, Intel or AMD, needs the free PawnIO driver. The Benchmark page has the link; "
                  + "once it is installed, start the helper and the temperature appears here."));
        sensorsText.Text = string.Join("\n", lines);
        // The helper only adds the motherboard's sensor and its fans, so it is offered only while it isn't running.
        sensorsButton.Visibility = reading is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TurnOnSensors()
    {
        if (!Sensors.StartHelper())
            return;
        sensorsButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>The score looks at files and folders, so it is worked out off the window's thread.</summary>
    private void Recalculate()
    {
        ShowFacts();
        if (scoring)
            return;
        scoring = true;
        var config = Main.Config;
        Task.Run(() =>
        {
            try
            {
                return Neatness.Compute(config);
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            {
                return (-1, new List<NeatnessNote>());
            }
        }).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            scoring = false;
            var (points, list) = task.Result;
            if (points < 0)
                return;
            score.Text = points.ToString();
            var tone = points >= 90 ? "Primary" : points >= 70 ? "Amber" : "Red";
            score.Foreground = Ui.Paint(tone);
            scoreBar.Set(points, tone);
            scoreNote.Text = list.Count == 0
                ? Ui.T("Nothing is holding the score back. Everything is in order.")
                : Ui.T("The score weighs files waiting to be sorted, temporary files, loose desktop files, free drive space and startup apps. "
                       + "Clear what is listed below and it reaches 100.");
            notes.Children.Clear();
            foreach (var note in list)
            {
                var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(Ui.Glyph("", 14, "Amber").Margin(0, 0, 10));
                row.Children.Add(Ui.Muted(Ui.T(note.Text, note.Arguments), 12.5));
                row.Children.Add(Ui.Text(Ui.T("−{0} pts", note.Lost), 12.5, "Amber").Dock(Dock.Right));
                notes.Children.Add(row);
            }
        }));
    }
}
