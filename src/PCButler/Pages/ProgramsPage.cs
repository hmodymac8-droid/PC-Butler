// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The installed programs, their updates, and ready lists of apps to install. Every list has
/// tick boxes, and a right-click menu that offers what fits the row: run, open its folder,
/// uninstall or update an installed program; install one that is not there yet.
/// </summary>
public sealed class ProgramsPage : Page
{
    private readonly StatCard[] stats;
    private readonly ListView installedList;
    private readonly ListView updateList;
    private readonly TextBox search;
    private readonly ComboBox driveFilter = new() { Width = 130 };
    private readonly TextBlock installedStatus = Ui.Muted("").Margin(10);
    private readonly TextBlock updateStatus = Ui.Muted("").Margin(10);
    private readonly List<(ListView List, CatalogApp[] Apps, TextBlock Status)> catalogs = [];
    private List<InstalledProgram> programs = [];
    private bool loaded, busy;

    public ProgramsPage(MainWindow main) : base(main, "Programs", "Everything installed, in one list.")
    {
        stats = AddStats(("Programs installed", ""), ("Space they use", ""), ("Updates available", ""));
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
        bar.Children.Add(Ui.Button(Ui.T("Uninstall"), () => Uninstall(Chosen(installedList).Select(row => (InstalledProgram)row.Tag!).ToList()),
                                   accent: true).Dock(Dock.Right));
        installedList = Ui.List(("Program", 300, "CheckCell"), ("Drive", 60), ("Version", 120), ("Publisher", 200), ("Size", 90), ("Installed", 110));
        installedList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && Ui.RowAt(e.OriginalSource)?.Tag is InstalledProgram program)
                Run(program);
        };
        AttachMenu(installedList, row => (InstalledProgram)row.Tag!, null, null);
        tabs.Items.Add(Tab("Installed", installedList, bar));

        bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Check for updates"), CheckUpdates));
        bar.Children.Add(updateStatus);
        bar.Children.Add(Ui.Button(Ui.T("Update selected"), () => Update(Chosen(updateList).Select(row => (ProgramUpdate)row.Tag!).ToList()),
                                   accent: true).Dock(Dock.Right));
        updateList = Ui.List(("Program", 300, "CheckCell"), ("Installed", 150), ("New version", 150), ("Package", 260));
        updateStatus.Text = Ui.T("Updates come from winget, Windows' own package manager.");
        AttachMenu(updateList, row => Programs.FindInstalled((ProgramUpdate)row.Tag!, programs), null, row => Update([(ProgramUpdate)row.Tag!]));
        tabs.Items.Add(Tab("Updates", updateList, bar));

        // Ready lists of well-known apps, installed through winget, the Microsoft Store or their GitHub release.
        tabs.Items.Add(CatalogTab("Essential apps", Programs.EssentialApps));
        tabs.Items.Add(CatalogTab("Recommended apps", Programs.RecommendedApps));
        Children.Add(tabs);
    }

    private static TabItem Tab(string title, ListView list, DockPanel bar)
    {
        var body = new DockPanel { Margin = new Thickness(10) };
        body.Children.Add(bar.Dock(Dock.Bottom));
        body.Children.Add(new Border { Child = list, Margin = new Thickness(0, 0, 0, 8) });
        return new TabItem { Header = Ui.T(title), Content = body };
    }

    /// <summary>The ticked rows; when none is ticked, the highlighted ones.</summary>
    private static List<Row> Chosen(ListView list)
    {
        var ticked = list.Items.OfType<Row>().Where(row => row.Checked).ToList();
        return ticked.Count > 0 ? ticked : Ui.Selected(list);
    }

    /// <summary>
    /// The right-click menu, made for the row under the mouse: an installed program can be run,
    /// shown in its folder and uninstalled (and updated, in the Updates list); an app that is not
    /// installed can be installed or have its page opened.
    /// </summary>
    private void AttachMenu(ListView list, Func<Row, InstalledProgram?> installedOf, Func<Row, CatalogApp?>? appOf, Action<Row>? update)
    {
        var menu = new ContextMenu();
        list.ContextMenu = menu;
        list.ContextMenuOpening += (_, e) =>
        {
            menu.Items.Clear();
            if (list.SelectedItem is not Row row)
            {
                e.Handled = true;
                return;
            }
            var program = installedOf(row);
            var app = appOf?.Invoke(row);
            if (program is not null)
            {
                menu.Items.Add(Ui.Item("Run", () => Run(program)));
                menu.Items.Add(Ui.Item("Open file location", () => OpenLocation(program)));
                if (update is not null)
                    menu.Items.Add(Ui.Item("Update", () => update(row)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Ui.Item("Uninstall…", () => Uninstall([program])));
            }
            else if (app is not null && app.Source != AppSource.Page)
                menu.Items.Add(Ui.Item("Install", () => Install([app], catalogs.First(catalog => catalog.List == list).Status)));
            if (app is not null)
                menu.Items.Add(Ui.Item(app.Source == AppSource.Page ? "Open its page to get it" : "Open its web page", () => Shell.Open(app.Page)));
            if (program is null && app is null)
                menu.Items.Add(new MenuItem { Header = Ui.T("Not found among the installed programs."), IsEnabled = false });
        };
    }

    private TabItem CatalogTab(string title, CatalogApp[] apps)
    {
        var list = Ui.List(("App", 260, "CheckCell"), ("Status", 120), ("What it does", 380), ("Package", 240));
        var status = Ui.Muted(Ui.T("Tick apps, then Install selected. Each installs in a window of its own. Right-click an app for more.")).Margin(10);
        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(Ui.Button(Ui.T("Refresh"), RefreshInstalled));
        bar.Children.Add(status);
        bar.Children.Add(Ui.Button(Ui.T("Install selected"), () => Install(Chosen(list).Select(row => (CatalogApp)row.Tag!).ToList(), status),
                                   accent: true).Dock(Dock.Right));
        AttachMenu(list, row => Programs.FindInstalled((CatalogApp)row.Tag!, programs), row => (CatalogApp)row.Tag!, null);
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && Ui.RowAt(e.OriginalSource)?.Tag is CatalogApp app
                && Programs.FindInstalled(app, programs) is { } program)
                Run(program);
        };
        catalogs.Add((list, apps, status));
        ShowCatalog(list, apps);
        return Tab(title, list, bar);
    }

    /// <summary>Installed apps first (then the list's own order); a click on a heading sorts differently.</summary>
    private void ShowCatalog(ListView list, CatalogApp[] apps)
    {
        var ticked = list.Items.OfType<Row>().Where(row => row.Checked).Select(row => ((CatalogApp)row.Tag!).Id).ToHashSet();
        list.ItemsSource = apps
            .Select(app => (App: app, Installed: Programs.IsInstalled(app, programs)))
            .OrderByDescending(item => item.Installed)
            .Select(item => new Row
            {
                Name = item.App.Name, Tag = item.App, Dim = item.Installed, Checked = ticked.Contains(item.App.Id),
                Cells = [Ui.T(item.App.Source == AppSource.Page ? "On its web page"
                              : programs.Count > 0 ? item.Installed ? "Is installed" : "Not installed" : ""),
                         Ui.T(item.App.Description), item.App.Source == AppSource.Winget ? item.App.Id : item.App.Page],
            }).ToList();
    }

    private async void Install(List<CatalogApp> apps, TextBlock status)
    {
        if (busy)
            return;
        if (apps.Count == 0)
        {
            Ui.Info("Programs", Ui.T("Tick or select one or more apps first."));
            return;
        }
        if (!Programs.WingetAvailable && apps.Any(app => app.Source is AppSource.Winget or AppSource.Store))
        {
            Ui.Warn("Programs", Ui.T("winget is not installed on this PC (it comes with App Installer from the Microsoft Store)."));
            return;
        }
        var already = apps.Where(app => Programs.IsInstalled(app, programs)).Select(app => app.Name).ToList();
        if (already.Count > 0 && !Ui.Ask("Programs", Ui.T("Already installed: {0}.\n\nInstall them again anyway?", string.Join(", ", already))))
            apps.RemoveAll(app => already.Contains(app.Name));
        busy = true;
        try
        {
            foreach (var app in apps)
            {
                status.Text = app.Source == AppSource.GitHub ? Ui.T("Downloading {0} from GitHub…", app.Name)
                                                             : Ui.T("Installing {0}: follow the winget window…", app.Name);
                try
                {
                    // Downloading a GitHub app's setup takes a while, so it happens off the window's thread.
                    using var installer = await Task.Run(() => Programs.StartInstall(app));
                    if (installer is not null)
                        await installer.WaitForExitAsync();
                }
                catch (Exception error) when (error is System.IO.IOException or System.ComponentModel.Win32Exception)
                {
                    Ui.Warn("Programs", Ui.T("{0} could not be installed: {1}", app.Name, error.Message));
                }
            }
        }
        finally
        {
            busy = false;
        }
        status.Text = Ui.T("Done. The list shows what is installed now.");
        foreach (var (list, _, _) in catalogs)
            foreach (var row in list.Items.OfType<Row>())
                row.SetChecked(false);
        RefreshInstalled();
    }

    /// <summary>At launch: read the installed programs and ask winget for updates, so both tabs are ready when opened.</summary>
    public void CheckAtLaunch()
    {
        Shown();
        CheckUpdates();
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
        foreach (var (list, apps, _) in catalogs)
            ShowCatalog(list, apps);
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

    private async void OpenLocation(InstalledProgram program)
    {
        var folder = await Task.Run(() => Programs.Folder(program));
        if (folder is null)
        {
            Ui.Info("Programs", Ui.T("PC Butler could not find the folder {0} is installed in.", program.Name));
            return;
        }
        Shell.Open(folder);
    }

    /// <summary>Uninstall the programs one after another, each with its own uninstaller and a look for what it left behind.</summary>
    private async void Uninstall(List<InstalledProgram> chosen)
    {
        if (busy)
            return;
        if (chosen.Count == 0)
        {
            Ui.Info("Programs", Ui.T("Tick or select a program first."));
            return;
        }
        if (chosen.FirstOrDefault(program => program.Name.StartsWith(AppInfo.Name, StringComparison.OrdinalIgnoreCase)) is { } self)
        {
            Ui.Info("Programs", Ui.T("PC Butler can't uninstall itself while it is running. Use Windows' Settings for that."));
            chosen.Remove(self);
            if (chosen.Count == 0)
                return;
        }
        var question = chosen.Count == 1
            ? Ui.T("Uninstall {0}?\n\nIts own uninstaller will open. Afterwards PC Butler looks for what it left behind "
                   + "and lets you choose what to remove.", chosen[0].Name)
            : Ui.T("Uninstall these {0} programs, one after another?\n\n{1}\n\nEach one's own uninstaller will open.",
                   chosen.Count, string.Join("\n", chosen.Select(program => program.Name)));
        if (!Ui.Ask("Uninstall", question, warning: true))
            return;
        busy = true;
        try
        {
            foreach (var program in chosen)
                await UninstallOne(program);
        }
        finally
        {
            busy = false;
            installedStatus.Text = "";
            RefreshInstalled();
        }
    }

    private async Task UninstallOne(InstalledProgram program)
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

    private async void Update(List<ProgramUpdate> updates)
    {
        if (busy)
            return;
        if (updates.Count == 0)
        {
            Ui.Info("Updates", Ui.T("Check for updates, then tick or select one or more programs."));
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
