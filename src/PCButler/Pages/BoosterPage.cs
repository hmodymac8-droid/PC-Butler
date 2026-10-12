// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>Startup apps, the programs using the most memory, and the power plan.</summary>
public sealed class BoosterPage : Page
{
    private readonly StatCard[] stats;
    private readonly ListView startupList;
    private readonly TextBlock startupSummary = Ui.Muted("").Margin(10);
    private readonly ListView programList;
    private readonly StackPanel powerList = new() { Margin = new Thickness(0, 10, 0, 10) };

    public BoosterPage(MainWindow main) : base(main, "Booster", "A lighter startup. A smoother day.")
    {
        stats = AddStats(("Memory in use", "\uE950"), ("Startup apps", "\uE7E8"), ("Power plan", "\uE945"));
        var tabs = new TabControl();

        // Startup apps: fewer of them means a faster sign-in and less running in the background.
        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Refresh"), RefreshStartup));
        bar.Children.Add(startupSummary);
        bar.Children.Add(Ui.Button(Ui.T("Turn on"), () => SetStartupSelected(true)).Dock(Dock.Right));
        bar.Children.Add(Ui.Button(Ui.T("Turn off"), () => SetStartupSelected(false), accent: true).Margin(0, 0, 6, 0).Dock(Dock.Right));
        startupList = Ui.List(("App", 240, "PlainCell"), ("Status", 70), ("Where", 190), ("Command", 420));
        startupList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                SetStartupSelected(null);
        };
        tabs.Items.Add(Tab("Startup apps", startupList, bar));

        // Programs: the biggest memory users, which can be ended to free memory.
        bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Refresh"), RefreshPrograms));
        bar.Children.Add(Ui.Muted(Ui.T("Save your work first: ending a program closes it without asking.")).Margin(10));
        bar.Children.Add(Ui.Button(Ui.T("End selected"), EndSelected, accent: true).Dock(Dock.Right));
        programList = Ui.List(("Program", 280, "PlainCell"), ("Copies", 80), ("Memory", 110), ("", 300));
        tabs.Items.Add(Tab("Running programs", programList, bar));

        // Power plan.
        var power = new StackPanel { Margin = new Thickness(14) };
        var intro = Ui.Text(Ui.T("High performance keeps the processor at full speed: faster, but it uses more power. "
                                 + "Balanced saves power while the PC is idle."), wrap: true);
        intro.MaxWidth = 640;
        intro.HorizontalAlignment = HorizontalAlignment.Left;
        power.Children.Add(intro);
        power.Children.Add(powerList);
        power.Children.Add(Ui.Row(6, Ui.Button(Ui.T("Use this plan"), ApplyPowerPlan, accent: true), Ui.Button(Ui.T("Refresh"), RefreshPower)));
        tabs.Items.Add(new TabItem { Header = Ui.T("Power plan"), Content = power });

        tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == tabs)
                RefreshMemory();
        };
        Children.Add(tabs);
        // Listing programs takes a moment, so it waits until the window is up.
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            RefreshStartup();
            RefreshPrograms();
            RefreshPower();
        };
        later.Start();
    }

    private static TabItem Tab(string title, ListView list, DockPanel bar)
    {
        var body = new DockPanel { Margin = new Thickness(10) };
        body.Children.Add(bar.Dock(Dock.Bottom));
        body.Children.Add(new Border { Child = list, Margin = new Thickness(0, 0, 0, 8) });
        return new TabItem { Header = Ui.T(title), Content = body };
    }

    private void RefreshMemory()
    {
        var (total, available) = Booster.MemoryStatus();
        var used = total - available;
        var parts = Ui.FormatSize(used).Split(' ');
        stats[0].Set(parts[0], parts[1], Ui.T("{0}% of {1} total", used * 100 / Math.Max(total, 1), Ui.FormatSize(total)));
    }

    // --- startup apps ---

    private void RefreshStartup()
    {
        var items = Booster.StartupItems();
        startupList.ItemsSource = items.Select(item => new Row
        {
            Name = item.Name, Tag = item, Dim = !item.Enabled,
            Cells = [Ui.T(item.Enabled ? "On" : "Off"), Ui.T(item.Where), item.Command],
        }).ToList();
        var on = items.Count(item => item.Enabled);
        stats[1].Set(on.ToString(), Ui.T("of {0}", items.Count), Ui.T("Start with Windows"));
        startupSummary.Text = Ui.T("{0} of {1} start with Windows. Turning one off keeps it installed.", on, items.Count);
    }

    /// <summary>enabled = null flips each selected app.</summary>
    private void SetStartupSelected(bool? enabled)
    {
        var items = Ui.Selected(startupList).Select(row => (StartupItem)row.Tag!).ToList();
        if (items.Count == 0)
        {
            Ui.Info("Startup apps", Ui.T("Select one or more apps first."));
            return;
        }
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                Booster.SetStartupEnabled(item, enabled ?? !item.Enabled);
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(Ui.T("{0}: it is set up for all users, so changing it needs administrator rights", item.Name));
            }
            catch (IOException error)
            {
                errors.Add($"{item.Name}: {error.Message}");
            }
        }
        RefreshStartup();
        if (errors.Count > 0)
            Ui.ReportResult(Ui.T("Some apps could not be changed."), errors);
    }

    // --- running programs ---

    private void RefreshPrograms()
    {
        programList.ItemsSource = Booster.RunningPrograms().Take(60).Select(program => new Row
        {
            Name = program.Name, Tag = program, Dim = program.Protected,
            Cells = [program.Pids.Count.ToString(), Ui.FormatSize(program.Memory), program.Protected ? Ui.T("Part of Windows") : ""],
        }).ToList();
        RefreshMemory();
    }

    private async void EndSelected()
    {
        var programs = Ui.Selected(programList).Select(row => (RunningProgram)row.Tag!).Where(program => !program.Protected).ToList();
        if (programs.Count == 0)
        {
            Ui.Info("Running programs", Ui.T("Select one or more programs that are not part of Windows."));
            return;
        }
        var names = string.Join("\n", programs.Select(program => $"{program.Name} ({Ui.FormatSize(program.Memory)})"));
        if (!Ui.Ask("End programs", Ui.T("End these programs now? Unsaved work in them is lost.\n\n{0}", names), warning: true))
            return;
        var before = Booster.MemoryStatus().Available;
        var errors = programs.Select(program => Booster.EndProgram(program) is { } error ? $"{program.Name}: {error}" : null).OfType<string>().ToList();
        await Task.Delay(500); // give Windows a moment to hand the memory back
        RefreshPrograms();
        var freed = Math.Max(Booster.MemoryStatus().Available - before, 0);
        Ui.ReportResult(Ui.T("Ended {0} program(s), about {1} freed.", programs.Count - errors.Count, Ui.FormatSize(freed)), errors);
    }

    // --- power plan ---

    private void RefreshPower()
    {
        powerList.Children.Clear();
        foreach (var plan in Booster.PowerPlans())
        {
            powerList.Children.Add(new RadioButton
            {
                Content = plan.Active ? Ui.T("{0}  (in use)", plan.Name) : plan.Name, Tag = plan, IsChecked = plan.Active,
                GroupName = "powerplan", Margin = new Thickness(0, 2, 0, 2),
            });
            if (plan.Active)
                stats[2].Set(plan.Name, "", Ui.T("The plan Windows is using now"));
        }
    }

    private void ApplyPowerPlan()
    {
        var plan = powerList.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true)?.Tag as PowerPlan;
        if (plan is null || plan.Active)
            return;
        if (Booster.SetPowerPlan(plan) is { } error)
            Ui.Warn("Power plan", error);
        RefreshPower();
    }
}
