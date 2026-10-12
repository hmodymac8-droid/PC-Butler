// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>The installed programs: uninstall one and clear what it leaves behind, or update through winget.</summary>
public sealed class ProgramsPage : Page
{
    private readonly StatCard[] stats;
    private readonly ListView installedList;
    private readonly ListView updateList;
    private readonly TextBox search;
    private readonly ComboBox driveFilter = new() { Width = 130 };
    private readonly TextBlock installedStatus = Ui.Muted("").Margin(10);
    private readonly TextBlock updateStatus = Ui.Muted("").Margin(10);
    private List<InstalledProgram> programs = [];
    private bool loaded, busy;

    public ProgramsPage(MainWindow main) : base(main, "Programs", "Everything installed, in one list.")
    {
        stats = AddStats(("Programs installed", ""), ("Space they use", ""), ("Updates available", ""));
        stats[2].Set("—", "", Ui.T("Press Check for updates"));
        var tabs = new TabControl();

        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Refresh"), RefreshInstalled));
        search = Ui.Field(width: 200, hint: Ui.T("Search programs…"));
        search.TextChanged += (_, _) => ShowInstalled();
        bar.Children.Add(search.Margin(6));
        driveFilter.SelectionChanged += (_, _) => ShowInstalled();
        bar.Children.Add(driveFilter.Margin(6));
        bar.Children.Add(installedStatus);
        bar.Children.Add(Ui.Button(Ui.T("Uninstall"), UninstallSelected, accent: true).Dock(Dock.Right));
        installedList = Ui.List(("Program", 300, "PlainCell"), ("Drive", 60), ("Version", 120), ("Publisher", 200), ("Size", 90), ("Installed", 110));
        installedList.SelectionMode = SelectionMode.Single;
        installedList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && Ui.RowAt(e.OriginalSource)?.Tag is InstalledProgram program)
                Run(program);
        };
        tabs.Items.Add(Tab("Installed", installedList, bar));

        bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Check for updates"), CheckUpdates));
        bar.Children.Add(updateStatus);
        bar.Children.Add(Ui.Button(Ui.T("Update selected"), UpdateSelected, accent: true).Dock(Dock.Right));
        updateList = Ui.List(("Program", 300, "PlainCell"), ("Installed", 150), ("New version", 150), ("Package", 260));
        updateStatus.Text = Ui.T("Updates come from winget, Windows' own package manager.");
        tabs.Items.Add(Tab("Updates", updateList, bar));
        Children.Add(tabs);
    }

    private static TabItem Tab(string title, ListView list, DockPanel bar)
    {
        var body = new DockPanel { Margin = new Thickness(10) };
        body.Children.Add(bar.Dock(Dock.Bottom));
        body.Children.Add(new Border { Child = list, Margin = new Thickness(0, 0, 0, 8) });
        return new TabItem { Header = Ui.T(title), Content = body };
    }

    /// <summary>Reading the list takes a moment, so it waits until the page is first opened.</summary>
    public override void Shown()
    {
        if (!loaded)
            RefreshInstalled();
        loaded = true;
    }

    private async void RefreshInstalled()
    {
        programs = await Task.Run(Programs.Installed);
        stats[0].Set(programs.Count.ToString());
        var size = Ui.FormatSize(programs.Sum(program => program.Size)).Split(' ');
        stats[1].Set(size[0], size[1], Ui.T("As the installers reported it"));
        // The drive filter offers the drives programs are actually on, and keeps the choice across refreshes.
        var chosen = (driveFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        driveFilter.Items.Clear();
        driveFilter.Items.Add(new ComboBoxItem { Content = Ui.T("All drives"), Tag = "" });
        foreach (var drive in programs.Select(program => program.Drive).Where(drive => drive.Length > 0).Distinct().Order())
            driveFilter.Items.Add(new ComboBoxItem { Content = Ui.T("Drive {0}", drive), Tag = drive });
        driveFilter.SelectedIndex = Math.Max(0, driveFilter.Items.OfType<ComboBoxItem>().ToList().FindIndex(item => (string)item.Tag == chosen));
        ShowInstalled();
    }

    private void ShowInstalled()
    {
        var needle = search.Text.Trim();
        var drive = (driveFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        installedList.ItemsSource = programs
            .Where(program => drive.Length == 0 || program.Drive == drive)
            .Where(program => needle.Length == 0 || program.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                              || program.Publisher.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .Select(program => new Row
            {
                Name = program.Name, Tag = program,
                Cells = [program.Drive, program.Version, program.Publisher, program.Size > 0 ? Ui.FormatSize(program.Size) : "", program.Installed],
            }).ToList();
    }

    private async void Run(InstalledProgram program)
    {
        var launcher = await Task.Run(() => Programs.FindLauncher(program));
        if (launcher is null)
        {
            Ui.Info("Programs", Ui.T("{0} has nothing to open, or PC Butler could not find what starts it.", program.Name));
            return;
        }
        Shell.Open(launcher);
    }

    private async void UninstallSelected()
    {
        if (busy)
            return;
        if (Ui.Selected(installedList).FirstOrDefault()?.Tag is not InstalledProgram program)
        {
            Ui.Info("Programs", Ui.T("Select a program first."));
            return;
        }
        if (program.Name.StartsWith(AppInfo.Name, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Info("Programs", Ui.T("PC Butler can't uninstall itself while it is running. Use Windows' Settings for that."));
            return;
        }
        if (!Ui.Ask("Uninstall", Ui.T("Uninstall {0}?\n\nIts own uninstaller will open. Afterwards PC Butler looks for what it left behind "
                                      + "and lets you choose what to remove.", program.Name), warning: true))
            return;
        busy = true;
        try
        {
            using var uninstaller = Programs.StartUninstall(program);
            if (uninstaller is null)
            {
                Ui.Warn("Uninstall", Ui.T("The uninstaller of {0} could not be started.", program.Name));
                return;
            }
            installedStatus.Text = Ui.T("Waiting for the uninstaller of {0}…", program.Name);
            await uninstaller.WaitForExitAsync();
            // Many uninstallers hand over to a second program and end at once, so the list is what tells when it is done.
            for (var seconds = 0; seconds < 180 && Programs.StillInstalled(program); seconds++)
                await Task.Delay(1000);
            if (Programs.StillInstalled(program))
            {
                Ui.Info("Uninstall", Ui.T("{0} is still installed: the uninstaller was cancelled or has not finished yet.", program.Name));
                return;
            }
            installedStatus.Text = Ui.T("Looking for what {0} left behind…", program.Name);
            var leftovers = await Task.Run(() => Programs.Leftovers(program));
            RefreshInstalled();
            if (leftovers.Count == 0)
            {
                Ui.Info("Uninstall", Ui.T("{0} was uninstalled and left nothing behind.", program.Name));
                return;
            }
            var chosen = LeftoversDialog.Ask(Main, program.Name, leftovers);
            if (chosen.Count == 0)
                return;
            var errors = await Task.Run(() => Programs.RemoveLeftovers(chosen));
            Ui.ReportResult(Ui.T("Removed {0} leftover(s) of {1}.", chosen.Count - errors.Count, program.Name), errors);
        }
        finally
        {
            busy = false;
            installedStatus.Text = "";
        }
    }

    // --- updates ---

    private async void CheckUpdates()
    {
        if (busy)
            return;
        busy = true;
        updateStatus.Text = Ui.T("Asking winget…");
        try
        {
            var updates = await Task.Run(Programs.Updates);
            if (updates is null)
            {
                updateStatus.Text = Ui.T(Programs.WingetAvailable
                    ? "winget did not answer. Open a terminal and run \"winget upgrade\" once, then try again."
                    : "winget is not installed on this PC (it comes with App Installer from the Microsoft Store).");
                return;
            }
            updateList.ItemsSource = updates.Select(update => new Row
            {
                Name = update.Name, Tag = update, Cells = [update.Version, update.Available, update.Id],
            }).ToList();
            stats[2].Set(updates.Count.ToString(), "", Ui.T("Found by winget"));
            updateStatus.Text = updates.Count == 0 ? Ui.T("Everything winget knows is up to date.")
                                                   : Ui.T("{0} program(s) can be updated.", updates.Count);
        }
        finally
        {
            busy = false;
        }
    }

    private async void UpdateSelected()
    {
        if (busy)
            return;
        var updates = Ui.Selected(updateList).Select(row => (ProgramUpdate)row.Tag!).ToList();
        if (updates.Count == 0)
        {
            Ui.Info("Updates", Ui.T("Check for updates, then select one or more programs."));
            return;
        }
        busy = true;
        try
        {
            foreach (var update in updates)
            {
                updateStatus.Text = Ui.T("Updating {0}: follow the winget window…", update.Name);
                using var winget = Programs.StartUpdate(update);
                if (winget is not null)
                    await winget.WaitForExitAsync();
            }
        }
        finally
        {
            busy = false;
        }
        CheckUpdates();
        RefreshInstalled();
    }
}

/// <summary>What an uninstalled program left behind, to tick what should go.</summary>
public sealed class LeftoversDialog : Window
{
    private readonly List<Leftover> chosen = [];

    private LeftoversDialog(string program, List<Leftover> leftovers)
    {
        Title = Ui.T("Leftovers");
        Ui.StyleWindow(this);
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var boxes = new StackPanel();
        foreach (var item in leftovers)
        {
            var kind = item.Kind switch { "registry" => "Registry", "file" => "Shortcut", _ => "Folder" };
            var label = Ui.Text(item.Size > 0 ? $"{item.Path}  ({Ui.FormatSize(item.Size)})" : item.Path, wrap: true);
            label.FlowDirection = FlowDirection.LeftToRight;
            boxes.Children.Add(new CheckBox
            {
                IsChecked = true, Tag = item, Margin = new Thickness(0, 4, 0, 4),
                Content = new StackPanel { Children = { Ui.Muted(Ui.T(kind), 11), label } },
            });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Ui.Button(Ui.T("Remove ticked"), () =>
        {
            chosen.AddRange(boxes.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (Leftover)box.Tag));
            Close();
        }, accent: true));
        buttons.Children.Add(Ui.Button(Ui.T("Keep everything"), Close).Margin(6));
        var body = new DockPanel { Margin = new Thickness(18) };
        body.Children.Add(Ui.Text(Ui.T("{0} was uninstalled, but these were left behind. Untick anything you want to keep.", program), wrap: true)
            .Margin(0, 0, 0, 10).Dock(Dock.Top));
        body.Children.Add(buttons.Dock(Dock.Bottom));
        body.Children.Add(Ui.Muted(Ui.T("Folders go to the Recycle Bin. Registry keys are saved to a .reg file in PC Butler's data folder first."), 12,
                                   wrap: true).Margin(0, 10, 0, 0).Dock(Dock.Bottom));
        body.Children.Add(new ScrollViewer { Content = boxes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = body;
    }

    /// <summary>Returns the leftovers the user ticked; none if they kept everything.</summary>
    public static List<Leftover> Ask(Window owner, string program, List<Leftover> leftovers)
    {
        var dialog = new LeftoversDialog(program, leftovers) { Owner = owner };
        dialog.ShowDialog();
        return dialog.chosen;
    }
}
