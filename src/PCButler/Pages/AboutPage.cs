// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>What the program is, its links, and a card of updates and preferences.</summary>
public sealed class AboutPage : Page
{
    private readonly TextBlock updateTitle = Ui.Text("", 13, weight: FontWeights.Bold);
    private readonly TextBlock updateStatus = Ui.Muted("", 12);
    private readonly Button checkButton;
    private readonly Button updateButton;
    private UpdateInfo? update; // the newer release, once a check has found one

    public AboutPage(MainWindow main) : base(main, "A little help, whenever you need it.", "Meet the butler behind your tidier digital space.")
    {
        var tab = new StackPanel();

        // The brand: the icon, the name with its accent full stop, and the version line.
        var mark = new Image { Source = Ui.AppIcon(), Width = 56, Height = 56, Margin = new Thickness(0, 0, 18, 0) };
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        var name = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ui.Paint("Text") };
        name.Inlines.Add(AppInfo.Name);
        name.Inlines.Add(new System.Windows.Documents.Run(".") { Foreground = Ui.Paint("Primary") });
        var version = Ui.Muted(Ui.T("Version {0}  ·  © 2026 hmodymac8-droid  ·  All rights reserved", AppInfo.Version), 12).Margin(0, 2, 0, 0);
        tab.Children.Add(Ui.Row(0, mark, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, version } }).Margin(0, 4, 0, 22));

        var description = Ui.Text(Ui.T("A tidy space for everything you download. Keep your folders in order, make room for what matters, "
                                       + "and give your PC a little care."), wrap: true);
        description.MaxWidth = 560;
        description.LineHeight = 26;
        tab.Children.Add(description);
        tab.Children.Add(Ui.Row(6, Ui.Button(Ui.T("What's new"), () => new WhatsNewWindow { Owner = Main }.Show()),
                                Ui.Button(Ui.T("GitHub page"), () => Shell.Open($"https://github.com/{AppInfo.Repo}")),
                                Ui.Button(Ui.T("Open settings and log folder"), OpenDataFolder),
                                // Shown again on request, it stays until clicked (or for half a minute).
                                Ui.Button(Ui.T("View splash screen"), () => new SplashWindow(seconds: 30).Show())).Margin(0, 16, 0, 22));

        // One card: each row a title, a line of explanation, and its button or switch on the far side.
        var card = new CardPanel("Updates & preferences", "\uE9CE");
        var rows = new StackPanel();
        checkButton = new Button { Content = Ui.Row(8, Ui.Glyph("\uE895", 13, "Text"), Ui.Text(Ui.T("Check for updates"), 13)) };
        checkButton.Click += (_, _) => CheckUpdates();
        updateButton = Ui.Button(Ui.T("Update now"), InstallUpdate, accent: true);
        updateButton.Visibility = Visibility.Collapsed;
        ShowUpdateState("Not checked yet.", "Version {0}", AppInfo.Version);
        rows.Children.Add(Ui.SettingRow(updateTitle, updateStatus, Ui.Row(6, updateButton, checkButton), first: true));

        var auto = Ui.SettingRow(Ui.T("Install new versions automatically"), Ui.T("Keep PC Butler ready for whatever comes next."),
                                 Ui.Switch("", main.Config.AutoUpdate, on => Main.UpdateConfig(config => config.AutoUpdate = on)));
        auto.ToolTip = Ui.T("Off: you get a notification and choose when to update. On: while automatic sorting is running, "
                            + "new versions install by themselves when no PC Butler window is open.");
        rows.Children.Add(auto);
        card.SetBody(rows);
        tab.Children.Add(card);
        Children.Add(new ScrollViewer { Content = tab, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    /// <summary>The update row: a short title and the line under it, in the chosen language.</summary>
    private void ShowUpdateState(string title, string detail, params object[] arguments)
    {
        updateTitle.Text = Ui.T(title);
        updateStatus.Text = Ui.T(detail, arguments);
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        Shell.Open(AppInfo.DataDir);
    }

    public async void CheckUpdates()
    {
        checkButton.IsEnabled = false;
        ShowUpdateState("Checking for updates…", "Version {0}", AppInfo.Version);
        // The network call runs off the window's thread so the window never freezes.
        var found = await Task.Run(() => Updater.CheckForUpdate());
        checkButton.IsEnabled = true;
        if (found is not null)
            Found(found);
        else
        {
            update = null;
            updateButton.Visibility = Visibility.Collapsed;
            ShowUpdateState("You’re up to date", "You have the latest version, {0}.", AppInfo.Version);
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
        ShowUpdateState("A new version is ready", "Version {0} is available (you have {1}).", newer.Version, AppInfo.Version);
        Main.ShowBanner(Ui.T("Version {0} is available (you have {1}).", newer.Version, AppInfo.Version));
        updateButton.Visibility = Visibility.Visible;
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
        ShowUpdateState("Updating", "Downloading version {0}…", chosen.Version);
        string installer;
        try
        {
            installer = await Task.Run(() => Updater.Download(chosen));
        }
        catch (IOException error)
        {
            updateButton.IsEnabled = true;
            ShowUpdateState("The update didn’t finish", "The download failed: {0}", error.Message);
            return;
        }
        Updater.Apply(installer, reopen: true);
        App.Current.ExitProgram(); // the installer needs every window closed
    }
}
