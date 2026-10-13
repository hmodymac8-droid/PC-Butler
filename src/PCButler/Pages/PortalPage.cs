// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Portal: files between this PC and a phone over the local network. Start it, scan the
/// QR code with the phone, compare the code and press Allow; then the phone's browser can
/// send files here and fetch the ones offered to it.
/// </summary>
public sealed class PortalPage : Page
{
    private readonly Button startButton;
    private readonly StackPanel connect = new() { Margin = new Thickness(24, 18, 24, 20) };
    private readonly TextBlock firewallText = Ui.Muted("", 12);
    private readonly Button firewallButton;
    private readonly TextBlock folderText = Ui.Muted("", 12);
    private readonly ComboBox addresses = new() { Width = 230 };
    private readonly ListView offerList;
    private readonly ListView transferList;
    private readonly ObservableCollection<Row> transferRows = [];
    private readonly Dictionary<PortalTransfer, (Row Row, long Done, DateTime At, double Speed)> shown = [];
    /// <summary>What the PC offers the phone. Kept between sessions, so a restart doesn't empty the list.</summary>
    private readonly List<string> offered = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool starting;

    private static PortalSession? Session => Portal.Current is { State: not PortalState.Stopped } session ? session : null;

    public PortalPage(MainWindow main) : base(main, "Portal", "Files between this PC and your phone, over your own Wi-Fi.")
    {
        startButton = AddAction("Start Portal", Toggle, accent: true);
        var page = new StackPanel();

        // --- connect a phone, beside the settings ---
        var connectCard = new CardPanel("Connect a phone", "") { Margin = new Thickness(0, 0, 6, 18) };
        connectCard.SetBody(connect);

        var rows = new StackPanel();
        firewallButton = Ui.Button(Ui.T("Allow (asks for administrator rights)"), AllowFirewall);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Windows firewall"), 13, weight: FontWeights.Bold), firewallText, firewallButton, first: true));
        rows.Children.Add(Ui.SettingRow(Ui.T("Encrypted connection"),
                                        Ui.T("Hides the files from other devices on the same Wi-Fi. The phone's browser warns once that it does not know this PC's "
                                             + "certificate; choose to continue. Off is fine on your own home network. Applies the next time Portal starts."),
                                        Ui.Switch("", main.Config.Portal.Secure, on => Main.UpdateConfig(config => config.Portal.Secure = on))));
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Received files go to"), 13, weight: FontWeights.Bold), folderText,
                                        Ui.Row(6, Ui.Button(Ui.T("Open"), OpenFolder), Ui.Button(Ui.T("Change…"), ChangeFolder))));
        rows.Children.Add(Ui.SettingRow(Ui.T("Network address"), Ui.T("Which of this PC's connections the phone uses. It must be the Wi-Fi the phone is on."), addresses));
        var settingsCard = new CardPanel("Settings", "") { Margin = new Thickness(6, 0, 0, 18) };
        settingsCard.SetBody(rows);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        top.Children.Add(connectCard.At(0, 0));
        top.Children.Add(settingsCard.At(0, 1));
        page.Children.Add(top);

        // --- what the phone may fetch ---
        var offerCard = new CardPanel("Send to the phone", "") { Margin = new Thickness(0, 0, 0, 18) };
        offerCard.Controls.Children.Add(Ui.Button(Ui.T("Add files…"), AddFiles));
        offerCard.Controls.Children.Add(Ui.Button(Ui.T("Add folder…"), AddFolder).Margin(6));
        offerCard.Controls.Children.Add(Ui.Button(Ui.T("Remove from list"), RemoveOffers).Margin(6));
        offerList = Ui.List(("Name", 340, "PlainCell"), ("Kind", 110), ("Size", 110), ("Where", 460));
        offerList.MinHeight = 120;
        offerList.AllowDrop = true;
        offerList.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Offer(dropped);
        };
        offerCard.SetBody(offerList);
        page.Children.Add(offerCard);

        // --- what is moving, and what has moved ---
        var transferCard = new CardPanel("Transfers", "");
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Cancel selected"), CancelSelected));
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory).Margin(6));
        transferList = Ui.List(("Name", 340, "PlainCell"), ("Direction", 130), ("Progress", 190), ("Speed", 110), ("Status", 250));
        transferList.MinHeight = 160;
        transferList.ItemsSource = transferRows;
        transferCard.SetBody(transferList);
        page.Children.Add(transferCard);

        Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        timer.Tick += (_, _) => ShowTransfers();
        IsVisibleChanged += (_, _) => timer.IsEnabled = IsVisible;
        ShowAll();
    }

    public override void Shown() => ShowAll();

    /// <summary>Offer files from elsewhere in the program (the drop zone).</summary>
    public void Offer(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && !offered.Contains(path, StringComparer.OrdinalIgnoreCase))
                offered.Add(path);
        }
        SyncOffers();
        ShowOffers();
    }

    // --- starting and stopping ---

    private async void Toggle()
    {
        if (starting)
            return;
        if (Session is { } running)
        {
            await running.Stop();
            ShowAll();
            return;
        }
        // The test hook keeps the server on this PC only, so trying the page never opens a port to the network.
        var loopback = Environment.GetEnvironmentVariable("PCBUTLER_PORTAL_LOOPBACK") == "1";
        var address = loopback ? IPAddress.Loopback : (addresses.SelectedItem as ComboBoxItem)?.Tag as IPAddress;
        if (address is null)
        {
            Ui.Info("Portal", Ui.T("This PC is not connected to a network. Connect it to the same Wi-Fi as the phone, then start Portal."));
            return;
        }
        starting = true;
        try
        {
            var settings = Main.Config.Portal;
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            var session = await PortalSession.Start(settings, address);
            session.Changed += () => Dispatcher.BeginInvoke(ShowAll);
            Portal.Current = session;
            SyncOffers();
        }
        catch (IOException error)
        {
            Ui.Warn("Portal", Ui.T("Portal could not start on {0}.", address) + "\n\n" + error.Message);
        }
        finally
        {
            starting = false;
        }
        ShowAll();
    }

    private void SyncOffers()
    {
        if (Session is not { } session)
            return;
        offered.RemoveAll(path => session.Offer(path) is null);
        foreach (var stale in session.Offers.Where(offer => !offered.Contains(offer.Path, StringComparer.OrdinalIgnoreCase)).ToList())
            session.RemoveOffer(stale.Id);
    }

    // --- showing ---

    private void ShowAll()
    {
        var session = Session;
        startButton.Content = Ui.T(session is null ? "Start Portal" : "Stop Portal");
        ShowConnect(session);
        ShowSettings(session);
        ShowOffers();
        ShowTransfers();
    }

    private void ShowConnect(PortalSession? session)
    {
        connect.Children.Clear();
        if (session is null)
        {
            connect.Children.Add(Ui.Text(Ui.T("Portal is off"), 18, weight: FontWeights.SemiBold));
            connect.Children.Add(Ui.Muted(Ui.T("Press Start Portal, then scan the code with your phone's camera. The phone needs no app: its browser opens a "
                                               + "page served by this PC."), 13, wrap: true).Margin(0, 8, 0, 0));
            connect.Children.Add(Ui.Muted(Ui.T("Files go straight between the two devices over your network. Nothing is uploaded to the internet, and "
                                               + "nothing listens while Portal is off."), 13, wrap: true).Margin(0, 8, 0, 0));
            return;
        }
        if (session.State == PortalState.Asking)
        {
            connect.Children.Add(Ui.Text(Ui.T("{0} wants to connect", session.Device), 18, weight: FontWeights.SemiBold, wrap: true));
            connect.Children.Add(Ui.Muted(Ui.T("Allow it only if the phone shows this same code."), 13, wrap: true).Margin(0, 6, 0, 0));
            var code = new TextBlock
            {
                Text = session.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 46, FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 18),
            };
            connect.Children.Add(code);
            var allow = Ui.Button(Ui.T("Allow"), session.Approve, accent: true);
            allow.MinWidth = 130;
            var buttons = Ui.Row(8, allow, Ui.Button(Ui.T("Refuse"), session.Refuse));
            buttons.HorizontalAlignment = HorizontalAlignment.Center;
            connect.Children.Add(buttons);
            return;
        }
        if (session.State == PortalState.Connected)
        {
            connect.Children.Add(Ui.Row(10, Ui.Text("●", 12, "Primary"), Ui.Text(Ui.T("Connected: {0}", session.Device), 18, weight: FontWeights.SemiBold)));
            connect.Children.Add(Ui.Muted(Ui.T("The phone can now send files here and fetch the ones in “Send to the phone” below. Keep its page open while "
                                               + "files are moving."), 13, wrap: true).Margin(0, 8, 0, 14));
            var disconnect = Ui.Button(Ui.T("Disconnect the phone"), session.Refuse);
            disconnect.HorizontalAlignment = HorizontalAlignment.Left;
            connect.Children.Add(disconnect);
            return;
        }
        // Waiting: the QR code, on white so any camera reads it.
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.StreamSource = new MemoryStream(PortalQr.Png(session.Url));
        picture.EndInit();
        var image = new Image { Source = picture, Width = 210, Height = 210 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        connect.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Child = image,
            HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
        });
        var scan = Ui.Text(Ui.T("Scan with the phone's camera"), 15, weight: FontWeights.SemiBold);
        scan.HorizontalAlignment = HorizontalAlignment.Center;
        connect.Children.Add(scan.Margin(0, 14, 0, 0));
        var hint = Ui.Muted(Ui.T("The phone must be on the same Wi-Fi as this PC. The code is a one-time secret: don't share a picture of it."), 12, wrap: true);
        hint.TextAlignment = TextAlignment.Center;
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        connect.Children.Add(hint.Margin(0, 6, 0, 0));
        if (Portal.OnPublicNetwork())
        {
            var warning = Ui.Text(Ui.T("Windows treats this network as Public, where the firewall keeps other devices out. Portal works on networks "
                                       + "set to Private (Windows Settings, Network)."), 12, "Amber", wrap: true);
            warning.TextAlignment = TextAlignment.Center;
            connect.Children.Add(warning.Margin(0, 10, 0, 0));
        }
    }

    private void ShowSettings(PortalSession? session)
    {
        var allowed = Portal.FirewallAllows();
        firewallText.Text = Ui.T(allowed ? "Phones on private networks are allowed to reach PC Butler."
                                         : "Windows blocks other devices from reaching PC Butler until it is allowed. This is needed once.");
        firewallButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        folderText.Text = Portal.ReceiveFolder(Main.Config.Portal);
        folderText.FlowDirection = FlowDirection.LeftToRight;

        var chosen = (addresses.SelectedItem as ComboBoxItem)?.Tag as IPAddress;
        addresses.Items.Clear();
        foreach (var (address, adapter) in Portal.LocalAddresses())
            addresses.Items.Add(new ComboBoxItem { Content = $"{address}  ·  {adapter}", Tag = address });
        addresses.SelectedIndex = addresses.Items.Count == 0 ? -1
            : Math.Max(0, addresses.Items.OfType<ComboBoxItem>().ToList().FindIndex(item => Equals(item.Tag, chosen)));
        addresses.IsEnabled = session is null;
    }

    private void ShowOffers()
    {
        offered.RemoveAll(path => !File.Exists(path) && !Directory.Exists(path));
        offerList.ItemsSource = offered.Select(path =>
        {
            var folder = Directory.Exists(path);
            long size = 0;
            try
            {
                size = folder ? Shredder.Measure([path]).Size : new FileInfo(path).Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            return new Row
            {
                Name = Path.GetFileName(path.TrimEnd('\\')), Tag = path,
                Cells = [Ui.T(folder ? "Folder, as a zip" : "File"), Ui.FormatSize(size), Path.GetDirectoryName(path.TrimEnd('\\')) ?? ""],
            };
        }).ToList();
    }

    /// <summary>The session's transfers with live progress on top, then the log of earlier ones.</summary>
    private void ShowTransfers()
    {
        var live = Session?.Transfers ?? [];
        var now = DateTime.UtcNow;
        var index = 0;
        foreach (var transfer in live.Reverse())
        {
            var done = transfer.Done;
            if (!shown.TryGetValue(transfer, out var entry))
            {
                entry = (new Row { Name = transfer.Name, Tag = transfer }, done, now, 0);
                transferRows.Insert(Math.Min(index, transferRows.Count), entry.Row);
            }
            else if ((now - entry.At).TotalSeconds >= 0.9)
            {
                entry = (entry.Row, done, now, Math.Max(0, (done - entry.Done) / (now - entry.At).TotalSeconds));
            }
            shown[transfer] = entry;
            var active = transfer.State == "active";
            var percent = transfer.Size > 0 ? done * 100 / transfer.Size : 100;
            entry.Row.Cells =
            [
                Ui.T(transfer.Incoming ? "From the phone" : "To the phone"),
                active ? $"{percent}%  ·  {Ui.FormatSize(done)} / {Ui.FormatSize(transfer.Size)}" : Ui.FormatSize(transfer.Size),
                active && entry.Speed > 0 ? Ui.FormatSize((long)entry.Speed) + "/s" : "",
                Ui.T(transfer.State switch { "done" => "Done", "cancelled" => "Cancelled", "failed" => "Failed", _ => "Moving…" }),
            ];
            entry.Row.Dim = !active;
            index++;
        }
        // Rows of a session that has ended give way to the log, which holds the same transfers.
        foreach (var gone in shown.Keys.Where(transfer => !live.Contains(transfer)).ToList())
        {
            transferRows.Remove(shown[gone].Row);
            shown.Remove(gone);
        }
        var finished = live.Where(transfer => transfer.State == "done").Select(transfer => (transfer.Name, transfer.Size)).ToList();
        var log = Portal.History().Where(item => !finished.Remove((item.Name, item.Size))).ToList();
        var logRows = transferRows.Where(row => row.Tag is PortalLogEntry).ToList();
        if (logRows.Count != log.Count || (log.Count > 0 && !ReferenceEquals(logRows[0].Tag, log[0]) && !Equals(logRows[0].Tag, log[0])))
        {
            foreach (var row in logRows)
                transferRows.Remove(row);
            foreach (var item in log)
            {
                transferRows.Add(new Row
                {
                    Name = item.Name, Tag = item, Dim = true,
                    Cells = [Ui.T(item.Incoming ? "From the phone" : "To the phone"), Ui.FormatSize(item.Size), "", item.Time + "  ·  " + item.Device],
                });
            }
        }
    }

    // --- buttons ---

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to offer the phone") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Offer(dialog.FileNames);
    }

    private void AddFolder()
    {
        if (Ui.PickFolder(Ui.T("Folder to offer the phone")) is { } folder)
            Offer([folder]);
    }

    private void RemoveOffers()
    {
        foreach (var row in Ui.Selected(offerList))
            offered.Remove((string)row.Tag!);
        SyncOffers();
        ShowOffers();
    }

    private void CancelSelected()
    {
        foreach (var row in Ui.Selected(transferList))
            (row.Tag as PortalTransfer)?.Cancel();
        ShowTransfers();
    }

    private void ClearHistory()
    {
        var count = Portal.History().Count;
        if (count == 0 || !Ui.Ask("Clear history", Ui.T("Clear the list of {0} finished transfer(s)? The files themselves stay where they are.", count)))
            return;
        Portal.ClearHistory();
        ShowTransfers();
    }

    private void OpenFolder()
    {
        var folder = Portal.ReceiveFolder(Main.Config.Portal);
        Directory.CreateDirectory(folder);
        Shell.Open(folder);
    }

    private void ChangeFolder()
    {
        if (Ui.PickFolder(Ui.T("Folder for files received through Portal"), Portal.ReceiveFolder(Main.Config.Portal)) is not { } folder)
            return;
        Main.UpdateConfig(config => config.Portal.Folder = folder);
        ShowSettings(Session);
        if (Session is not null)
            Ui.Info("Portal", Ui.T("The new folder is used the next time Portal starts."));
    }

    /// <summary>The firewall rule needs administrator rights, so the optional helper adds it (Windows asks first).</summary>
    private async void AllowFirewall()
    {
        firewallButton.IsEnabled = false;
        try
        {
            if (!AdminHelper.Running)
            {
                if (!Ui.Ask("Portal", Ui.T("Windows only lets a program accept connections from other devices once an administrator allows it.\n\n"
                                           + "PC Butler starts its small helper for this, and Windows will ask for your permission. The rule covers PC Butler "
                                           + "only, on private networks only.\n\nAllow it now?"))
                    || !Sensors.StartHelper())
                    return;
                for (var tries = 0; tries < 40 && !AdminHelper.Running; tries++)
                    await Task.Delay(500);
            }
            var done = await Task.Run(() => AdminHelper.Send("allow_portal", []));
            if (done is null || !done.Contains("portal"))
                Ui.Warn("Portal", Ui.T("The firewall rule could not be added. You can also allow PC Butler yourself when Windows asks about it."));
        }
        finally
        {
            firewallButton.IsEnabled = true;
            ShowSettings(Session);
        }
    }

    /// <summary>Closing the window leaves Portal running in the tray; it stops with the program or after a while without use.</summary>
    public override bool CanClose() => true;
}
