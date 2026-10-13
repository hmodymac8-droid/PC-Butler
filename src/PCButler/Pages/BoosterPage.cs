// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>Boost, startup apps, the programs using the most memory, and the power plan.</summary>
public sealed class BoosterPage : Page
{
    private readonly StatCard[] stats;
    private readonly ListView startupList;
    private readonly TextBlock startupSummary = Ui.Muted("").Margin(10);
    private readonly ListView programList;
    private readonly StackPanel powerList = new() { Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock boostStatus = Ui.Muted("", 12);
    private readonly TextBlock boostDetail = Ui.Muted("", wrap: true);
    private readonly ComboBox favoured = new() { Width = 340 };
    private readonly Button boostButton;
    private readonly CheckBox servicesSwitch;
    private readonly TextBox autoApps;
    private string shownBoost = "?";

    public BoosterPage(MainWindow main) : base(main, "Booster", "A lighter startup. A smoother day.")
    {
        stats = AddStats(("Memory in use", "\uE950"), ("Startup apps", "\uE7E8"), ("Power plan", "\uE945"));
        var tabs = new TabControl();

        // Boost: one program gets the PC's attention; everything is put back when it is switched off.
        var settings = main.Config.Boost;
        var boost = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var heading = new DockPanel { Margin = new Thickness(24, 8, 24, 4) };
        heading.Children.Add(new Border
        {
            Background = Ui.Paint("Secondary"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5),
            VerticalAlignment = VerticalAlignment.Center, Child = boostStatus,
        }.Dock(Dock.Right));
        var title = Ui.Text(Ui.T("A calmer background. A faster foreground."), 15, weight: FontWeights.Bold);
        title.ToolTip = Ui.T("Boost gives one program the PC's attention: it runs at high priority while the programs behind it "
                             + "step back. Nothing is closed, Windows' own programs are never touched, and switching Boost off "
                             + "puts everything back.");
        heading.Children.Add(title);
        boost.Children.Add(heading);
        boost.Children.Add(boostDetail.Margin(24, 0, 24, 10));
        boost.Children.Add(Ui.Row(10, Ui.Muted(Ui.T("Program to speed up")), favoured, Ui.Button(Ui.T("Refresh"), RefreshFavoured))
                             .Margin(24, 0, 24, 10));

        // Each part of Boost: a title, what it does, and its switch on the far side.
        boost.Children.Add(Ui.SettingRow(Ui.T("Lower the priority of background programs"),
                                         Ui.T("Give the foreground program a little more attention."),
                                         Ui.Switch("", settings.Priorities, on => Main.UpdateConfig(config => config.Boost.Priorities = on))));
        boost.Children.Add(Ui.SettingRow(Ui.T("Use the high-performance power plan while Boost is on"),
                                         Ui.T("Your own plan comes back when Boost is turned off."),
                                         Ui.Switch("", settings.PowerPlan, on => Main.UpdateConfig(config => config.Boost.PowerPlan = on))));
        boost.Children.Add(Ui.SettingRow(Ui.T("Trim the memory of background programs"),
                                         Ui.T("Trimming frees memory at once, but those programs load it back when you return to them, so they "
                                              + "feel slower for a moment. Worth it only when memory is nearly full."),
                                         Ui.Switch("", settings.TrimMemory, on => Main.UpdateConfig(config => config.Boost.TrimMemory = on))));
        servicesSwitch = Ui.Switch("", settings.Services, SetServices);
        boost.Children.Add(Ui.SettingRow(Ui.T("Pause background services (needs administrator rights)"),
                                         Ui.T("Print Spooler (no printing while paused), Telemetry and SysMain. They are started again when Boost "
                                              + "goes off or PC Butler closes."), servicesSwitch));
        autoApps = Ui.Field(string.Join(", ", settings.AutoApps), 260, "game.exe, editor.exe");
        autoApps.LostFocus += (_, _) => SaveAutoApps();
        boost.Children.Add(Ui.SettingRow(Ui.T("Boost by itself while these programs are running"),
                                         Ui.T("PC Butler must be running (it can stay in the tray). Boost goes off when the program closes."),
                                         Ui.Row(6, autoApps, Ui.Button(Ui.T("Add the chosen program"), AddChosenToAuto))));

        boostButton = new Button { MinWidth = 140, Style = (Style)Application.Current.FindResource("AccentButton") };
        boostButton.Click += (_, _) => ToggleBoost();
        boost.Children.Add(new Border
        {
            BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 18, 24, 8),
            Child = Ui.Row(0, boostButton),
        });
        tabs.Items.Add(new TabItem
        {
            Header = Ui.T("Boost"),
            Content = new ScrollViewer { Content = boost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });

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

        // Advanced, last: lasting changes for the whole PC, in presets, with one Undo.
        tabs.Items.Add(new TabItem { Header = Ui.T("Advanced"), Content = new BoosterAdvanced() });

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
            RefreshFavoured();
        };
        later.Start();
        // Boost can also be switched on and off by the automatic list, so the page keeps looking.
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        watch.Tick += (_, _) => ShowBoost();
        watch.Start();
        ShowBoost();
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

    // --- boost ---

    private void RefreshFavoured()
    {
        var chosen = (favoured.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
        favoured.Items.Clear();
        favoured.Items.Add(new ComboBoxItem { Content = Ui.T("None: only quieten the background"), Tag = 0 });
        foreach (var program in BoostMode.WindowedPrograms())
        {
            var title = program.Title.Length > 44 ? program.Title[..44] + "…" : program.Title;
            favoured.Items.Add(new ComboBoxItem { Content = $"{program.Name}  ·  {title}", Tag = program.Pid, ToolTip = program.Name });
        }
        favoured.SelectedIndex = Math.Max(0, favoured.Items.OfType<ComboBoxItem>().ToList().FindIndex(item => (int)item.Tag == chosen));
    }

    private void ShowBoost()
    {
        var state = BoostMode.Active;
        var key = state is null ? "" : state.Started + state.Processes.Count + state.Auto;
        if (key == shownBoost)
            return;
        shownBoost = key;
        boostButton.Content = Ui.Row(8, Ui.Glyph("\uE945", 13, "PrimaryText"), Ui.Text(Ui.T(state is null ? "Start Boost" : "Stop Boost"), 13, "PrimaryText"));
        boostStatus.Foreground = Ui.Paint(state is null ? "Muted" : "Primary");
        if (state is null)
        {
            boostStatus.Text = Ui.T("Not active");
            boostDetail.Text = Ui.T("Choose a program, then press Start Boost.");
            return;
        }
        boostStatus.Text = Ui.T("Active since {0}", state.Started[^5..]);
        var lines = new List<string>();
        if (state.Processes.Any(process => process.Raised))
            lines.Add(Ui.T("{0} is running at high priority.", state.Favoured));
        if (state.Lowered > 0)
            lines.Add(Ui.T("{0} background program(s) stepped back.", state.Lowered));
        if (state.PlanName.Length > 0)
            lines.Add(Ui.T("Power plan: {0}.", state.PlanName));
        if (state.Trimmed > 0)
            lines.Add(Ui.T("{0} of memory freed by trimming {1} program(s).", Ui.FormatSize(state.Freed), state.Trimmed));
        if (state.PausedServices.Count > 0)
            lines.Add(Ui.T("{0} service(s) paused.", state.PausedServices.Count));
        if (state.Auto)
            lines.Add(Ui.T("Switched on by itself; it goes off when {0} closes.", state.Favoured));
        if (lines.Count == 0)
            lines.Add(Ui.T("Nothing needed changing."));
        boostDetail.Text = string.Join("  ", lines);
    }

    /// <summary>Pausing services is done by the administrator helper, so switching it on offers to start it.</summary>
    private void SetServices(bool on)
    {
        if (on && !AdminHelper.Running)
        {
            if (!Ui.Ask("Boost", Ui.T("Pausing services needs administrator rights. PC Butler starts a small helper for it; Windows will "
                                      + "ask for your permission. Without it, Boost still does everything else.\n\nStart the helper now?"))
                || !Sensors.StartHelper())
            {
                servicesSwitch.IsChecked = false;
                return;
            }
        }
        Main.UpdateConfig(config => config.Boost.Services = on);
    }

    private async void ToggleBoost()
    {
        boostButton.IsEnabled = false;
        try
        {
            if (BoostMode.Active is not null)
            {
                await Task.Run(BoostMode.Stop);
            }
            else
            {
                var settings = Main.Config.Boost;
                var pid = (favoured.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
                await Task.Run(() => BoostMode.Start(settings, pid));
            }
        }
        finally
        {
            boostButton.IsEnabled = true;
        }
        ShowBoost();
        RefreshPower();
        RefreshMemory();
    }

    private void SaveAutoApps()
    {
        var apps = autoApps.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(app => app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe")
            .Where(app => !Booster.ProtectedNames.Contains(app))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        autoApps.Text = string.Join(", ", apps);
        if (!apps.SequenceEqual(Main.Config.Boost.AutoApps))
            Main.UpdateConfig(config => config.Boost.AutoApps = apps);
    }

    private void AddChosenToAuto()
    {
        if ((favoured.SelectedItem as ComboBoxItem)?.ToolTip is not string name)
        {
            Ui.Info("Boost", Ui.T("Choose the program in the list above first (it must be open)."));
            return;
        }
        autoApps.Text = autoApps.Text.Trim().Length > 0 ? $"{autoApps.Text}, {name}" : name;
        SaveAutoApps();
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
