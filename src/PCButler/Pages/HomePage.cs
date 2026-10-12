// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The PC at a glance: how busy the processor, memory and graphics card are, how full the
/// drives are, when Windows last restarted, and a score for how tidy things are.
/// </summary>
public sealed class HomePage : Page
{
    private readonly StatCard[] live;
    private readonly StatCard[] facts;
    private readonly StackPanel drives = new();
    private readonly StackPanel notes = new();
    private readonly TextBlock sensorsText = Ui.Muted("", wrap: true);
    private readonly Button sensorsButton;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(UsageMonitor.IntervalSeconds) };
    private bool scoring;

    public HomePage(MainWindow main) : base(main, "Welcome back.", "Your PC at a glance.")
    {
        AddAction("Refresh", Recalculate);
        live = AddStats(("Processor", ""), ("Memory", ""), ("Graphics card", ""));
        facts = AddStats(("Neatness score", ""), ("Last restart", ""), ("Free space", ""));
        foreach (var card in live.Concat(facts))
            card.Set("—");

        var panel = new CardPanel("Details", "");
        var body = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        body.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel();
        left.Children.Add(Heading("Storage"));
        left.Children.Add(drives);
        left.Children.Add(Heading("Temperature and fans").Margin(0, 18));
        left.Children.Add(sensorsText);
        sensorsButton = Ui.Button(Ui.T("Turn on sensors (asks for administrator rights)"), TurnOnSensors);
        sensorsButton.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(sensorsButton.Margin(0, 8));
        body.Children.Add(left.At(0, 0));

        var right = new StackPanel();
        right.Children.Add(Heading("What lowers your score"));
        right.Children.Add(notes);
        var alerts = Ui.Switch(Ui.T("Warn me when the processor or memory stays nearly full"), main.Config.UsageAlerts,
                               on => Main.UpdateConfig(config => config.UsageAlerts = on));
        alerts.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(alerts.Margin(0, 18));
        body.Children.Add(right.At(0, 2));

        panel.SetBody(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Children.Add(panel);

        timer.Tick += (_, _) => ShowLive();
        IsVisibleChanged += (_, _) =>
        {
            // The graphics card is only read while this page is on screen.
            App.Current.Usage.WantGpu = IsVisible;
            timer.IsEnabled = IsVisible;
        };
    }

    private static TextBlock Heading(string text)
    {
        var heading = Ui.Text(Ui.T(text), 14, weight: FontWeights.Bold);
        heading.Margin = new Thickness(0, 0, 0, 8);
        return heading;
    }

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
        live[0].Set(sample.Cpu.ToString(), "%", Ui.T("{0} processor threads", Environment.ProcessorCount));
        var used = Ui.FormatSize(sample.MemoryUsed).Split(' ');
        live[1].Set(used[0], used[1], Ui.T("{0}% of {1} total", sample.MemoryPercent, Ui.FormatSize(sample.MemoryTotal)));
        if (sample.Gpu is { } gpu)
        {
            var temperature = gpu.Temperature is { } degrees ? Ui.T("{0} °C", degrees) + "  ·  " : "";
            live[2].Set(gpu.Usage?.ToString() ?? "—", gpu.Usage is null ? "" : "%", temperature + gpu.Name);
        }
        ShowSensors();
    }

    private void ShowFacts()
    {
        var since = DateTime.Now - UsageMonitor.LastRestart;
        var ago = since.TotalDays >= 1 ? Ui.T("{0} d {1} h", (int)since.TotalDays, since.Hours)
                : since.TotalHours >= 1 ? Ui.T("{0} h {1} min", (int)since.TotalHours, since.Minutes)
                : Ui.T("{0} min", Math.Max(1, (int)since.TotalMinutes));
        facts[1].Set(ago, Ui.T("ago"), UsageMonitor.LastRestart.ToString("yyyy-MM-dd HH:mm"));

        var all = UsageMonitor.Drives();
        var free = Ui.FormatSize(all.Sum(drive => drive.Free)).Split(' ');
        facts[2].Set(free[0], free[1], Ui.T("of {0} on {1} drive(s)", Ui.FormatSize(all.Sum(drive => drive.Total)), all.Count));
        drives.Children.Clear();
        foreach (var drive in all)
        {
            var percent = drive.Total > 0 ? (double)drive.Used / drive.Total : 0;
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(Ui.Text(drive.Name, 13));
            row.Children.Add(Ui.Muted(Ui.T("{0} free of {1}", Ui.FormatSize(drive.Free), Ui.FormatSize(drive.Total)), 12).Dock(Dock.Right));
            drives.Children.Add(row);
            // Red once a drive is nine-tenths full.
            var fill = new Border { Background = Ui.Paint(percent >= 0.9 ? "Red" : "Primary"), HorizontalAlignment = HorizontalAlignment.Left, Height = 6 };
            var bar = new Border { Background = Ui.Paint("Raised"), Height = 6, Margin = new Thickness(0, 0, 0, 12), Child = fill };
            bar.SizeChanged += (_, _) => fill.Width = Math.Max(0, bar.ActualWidth * percent);
            drives.Children.Add(bar);
        }
    }

    private void ShowSensors()
    {
        if (Sensors.Latest() is not { } reading)
        {
            sensorsText.Text = Ui.T("Windows only gives processor temperature and fan speeds to a program running as administrator. "
                                    + "PC Butler doesn't need that for anything else, so it asks only if you turn this on.");
            sensorsButton.Visibility = Visibility.Visible;
            return;
        }
        sensorsButton.Visibility = Visibility.Collapsed;
        var temperature = reading.Temperature is { } degrees
            ? Ui.T("Temperature: {0} °C", degrees) : Ui.T("This PC does not report a temperature to Windows.");
        var fans = reading.Fans.Count > 0
            ? Ui.T("Fans: {0} RPM", string.Join(", ", reading.Fans)) : Ui.T("This PC does not report fan speeds to Windows.");
        sensorsText.Text = temperature + "\n" + fans;
    }

    private void TurnOnSensors()
    {
        if (!Sensors.StartHelper())
            return;
        sensorsButton.Visibility = Visibility.Collapsed;
        sensorsText.Text = Ui.T("Reading the sensors…");
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
            var (score, list) = task.Result;
            if (score < 0)
                return;
            facts[0].Set(score.ToString(), Ui.T("of 100"), score >= 90 ? Ui.T("Tidy. Well done.") : score >= 70 ? Ui.T("Good, with a little to clear") : Ui.T("Worth a clean-up"));
            notes.Children.Clear();
            if (list.Count == 0)
                notes.Children.Add(Ui.Muted(Ui.T("Nothing. Everything is in order.")));
            foreach (var note in list)
            {
                var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(Ui.Text(Ui.T(note.Text, note.Arguments), 13));
                row.Children.Add(Ui.Text($"−{note.Lost}", 13, "Amber").Dock(Dock.Right));
                notes.Children.Add(row);
            }
        }));
    }
}
