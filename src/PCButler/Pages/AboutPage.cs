// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>What the program is, links, and updates.</summary>
public sealed class AboutPage : Page
{
    private readonly TextBlock updateStatus = Ui.Text("");
    private readonly Button checkButton;
    private readonly Button updateButton;
    private UpdateInfo? update; // the newer release, once a check has found one

    public AboutPage(MainWindow main) : base(main, "About", "A tidy space for everything you download.")
    {
        var tab = new StackPanel();
        var mark = new Image { Source = Ui.AppIcon(), Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0) };
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        var name = Ui.Text(AppInfo.Name, 27, weight: FontWeights.Bold);
        name.FontFamily = Ui.DisplayFont;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center,
                                    Children = { name, Ui.Muted(Ui.T("Version {0}  ·  GPL v3  ·  © 2026 hmodymac8-droid", AppInfo.Version)) } };
        tab.Children.Add(Ui.Row(0, mark, text).Margin(0, 6, 0, 14));
        var description = Ui.Text(Ui.T("Keeps your Downloads folder tidy by moving old files into category folders, "
                                       + "automatically or only when you say so. Every change is logged and can be undone."), wrap: true);
        description.MaxWidth = 620;
        description.HorizontalAlignment = HorizontalAlignment.Left;
        tab.Children.Add(description);
        tab.Children.Add(Ui.Row(6, Ui.Button(Ui.T("What's new"), () => new WhatsNewWindow { Owner = Main }.Show()),
                                Ui.Button(Ui.T("GitHub page"), () => Shell.Open($"https://github.com/{AppInfo.Repo}")),
                                Ui.Button(Ui.T("Open settings and log folder"), OpenDataFolder),
                                // Shown again on request, it stays until clicked (or for half a minute).
                                Ui.Button(Ui.T("View splash screen"), () => new SplashWindow(seconds: 30).Show())).Margin(0, 14));
        tab.Children.Add(new Border { Height = 1, Background = Ui.Paint("Line"), Margin = new Thickness(0, 8, 0, 8) });

        var section = Ui.Text(Ui.T("Updates"), 16, weight: FontWeights.Bold).Margin(0, 6, 0, 6);
        section.FontFamily = Ui.DisplayFont;
        tab.Children.Add(section);
        updateStatus.Text = Ui.T("Not checked yet.");
        tab.Children.Add(updateStatus);
        checkButton = Ui.Button(Ui.T("Check for updates"), CheckUpdates);
        updateButton = Ui.Button(Ui.T("Update now"), InstallUpdate, accent: true);
        updateButton.IsEnabled = false;
        tab.Children.Add(Ui.Row(6, checkButton, updateButton).Margin(0, 10));
        tab.Children.Add(Ui.Switch(Ui.T("Install new versions automatically"), main.Config.AutoUpdate,
                                   on => Main.UpdateConfig(config => config.AutoUpdate = on)));
        var note = Ui.Muted(Ui.T("Off: you get a notification and choose when to update. On: while automatic sorting is running, "
                                 + "new versions install by themselves when no PC Butler window is open."), wrap: true).Margin(0, 4);
        note.MaxWidth = 620;
        note.HorizontalAlignment = HorizontalAlignment.Left;
        tab.Children.Add(note);

        section = Ui.Text(Ui.T("File Explorer"), 16, weight: FontWeights.Bold).Margin(0, 18, 0, 6);
        section.FontFamily = Ui.DisplayFont;
        tab.Children.Add(section);
        tab.Children.Add(Ui.Switch(Ui.T("Show “Process with PC Butler” when I right-click a file or folder"), main.Config.ExplorerMenu, on =>
        {
            Main.UpdateConfig(config => config.ExplorerMenu = on);
            if (App.SyncExplorerMenu(on) is { } error)
                Ui.Warn("File Explorer", error);
        }));
        note = Ui.Muted(Ui.T("Sort the file into its category folder, sort a folder's loose files, or send a file to the Recycle Bin, "
                             + "without opening PC Butler. On Windows 11 the entry is under “Show more options”."), wrap: true).Margin(0, 4);
        note.MaxWidth = 620;
        note.HorizontalAlignment = HorizontalAlignment.Left;
        tab.Children.Add(note);
        Children.Add(new ScrollViewer { Content = tab, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        Shell.Open(AppInfo.DataDir);
    }

    public async void CheckUpdates()
    {
        checkButton.IsEnabled = false;
        updateStatus.Text = Ui.T("Checking for updates…");
        // The network call runs off the window's thread so the window never freezes.
        var found = await Task.Run(() => Updater.CheckForUpdate());
        checkButton.IsEnabled = true;
        if (found is not null)
            Found(found);
        else
        {
            update = null;
            updateStatus.Text = Ui.T("No newer version found. You have version {0}.", AppInfo.Version);
        }
    }

    /// <summary>A newer version exists: say so here and in the banner across the top.</summary>
    public void Found(UpdateInfo? newer)
    {
        if (newer is null)
            return;
        if (update?.Version != newer.Version && App.Current.Background.Update?.Version != newer.Version)
            App.Current.Background.CheckUpdatesNow(); // so the tray menu offers it as well
        update = newer;
        var message = Ui.T("Version {0} is available (you have {1}).", newer.Version, AppInfo.Version);
        updateStatus.Text = message;
        Main.ShowBanner(message);
        updateButton.IsEnabled = true;
    }

    public async void InstallUpdate()
    {
        if (update is not { } chosen)
            return;
        if (!AppInfo.IsInstalled)
        {
            // Not an installed copy: nothing to replace, so show the release page.
            Shell.Open(chosen.Page);
            return;
        }
        if (!Ui.Ask("Update", Ui.T("Download and install version {0} now?\n\n{1} will close and reopen by itself.", chosen.Version, AppInfo.Name)))
            return;
        updateButton.IsEnabled = false;
        Main.HideBanner();
        updateStatus.Text = Ui.T("Downloading version {0}…", chosen.Version);
        string installer;
        try
        {
            installer = await Task.Run(() => Updater.Download(chosen));
        }
        catch (IOException error)
        {
            updateButton.IsEnabled = true;
            updateStatus.Text = Ui.T("The download failed: {0}", error.Message);
            return;
        }
        Updater.Apply(installer, reopen: true);
        App.Current.ExitProgram(); // the installer needs every window closed
    }
}
