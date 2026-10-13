// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Portal: files between this PC and other PCs running PC Butler, over the local network.
/// Start it on both, pair them once by comparing a six-digit code, then send files and
/// folders to a PC from the list.
/// </summary>
public sealed class PortalPage : Page
{
    private readonly Button startButton;
    private readonly StackPanel status = new() { Margin = new Thickness(24, 18, 24, 18) };
    private readonly StackPanel asks = new();
    private readonly TextBlock firewallText = Ui.Muted("", 12);
    private readonly Button firewallButton;
    private readonly TextBlock folderText = Ui.Muted("", 12);
    private readonly ComboBox addresses = new() { Width = 230 };
    private readonly ListView deviceList;
    private readonly TextBox addressBox;
    private readonly TextBlock queuedText = Ui.Text("", 13, "Primary");
    private readonly ListView transferList;
    private readonly ObservableCollection<Row> transferRows = [];
    private readonly Dictionary<PortalTransfer, (Row Row, long Done, DateTime At, double Speed)> shown = [];
    /// <summary>Pairings this PC started; they stay on screen until answered.</summary>
    private readonly List<PortalPairing> started = [];
    /// <summary>Files handed over from the drop zone, waiting for a PC to be chosen.</summary>
    private readonly List<string> queued = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool starting;

    private static PortalNode? Node => Portal.Current is { Stopped: false } node ? node : null;

    public PortalPage(MainWindow main) : base(main, "Portal", "Files between your PCs, straight over your own network.")
    {
        startButton = AddAction("Start Portal", Toggle, accent: true);
        var page = new StackPanel();

        // --- requests from other PCs come first: they wait for an answer ---
        page.Children.Add(asks);

        // --- this PC, beside the settings ---
        var statusCard = new CardPanel("This PC", "") { Margin = new Thickness(0, 0, 6, 18) };
        statusCard.SetBody(status);

        var rows = new StackPanel();
        firewallButton = Ui.Button(Ui.T("Allow (asks for administrator rights)"), AllowFirewall);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Windows firewall"), 13, weight: FontWeights.Bold), firewallText, firewallButton, first: true));
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Received files go to"), 13, weight: FontWeights.Bold), folderText,
                                        Ui.Row(6, Ui.Button(Ui.T("Open"), OpenFolder), Ui.Button(Ui.T("Change…"), ChangeFolder))));
        rows.Children.Add(Ui.SettingRow(Ui.T("Network address"), Ui.T("Which of this PC's connections Portal uses. It must be on the same network as the other PC."), addresses));
        var settingsCard = new CardPanel("Settings", "") { Margin = new Thickness(6, 0, 0, 18) };
        settingsCard.SetBody(rows);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        top.Children.Add(statusCard.At(0, 0));
        top.Children.Add(settingsCard.At(0, 1));
        page.Children.Add(top);

        // --- the other PCs ---
        var deviceCard = new CardPanel("Other PCs", "") { Margin = new Thickness(0, 0, 0, 18) };
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send files…"), SendFiles, accent: true));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send folder…"), SendFolder).Margin(6));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Pair…"), PairSelected).Margin(6));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Accept automatically: on/off"), ToggleAuto).Margin(6));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Forget"), ForgetSelected).Margin(6));
        var footer = deviceCard.AddFooter();
        addressBox = Ui.Field(width: 210, hint: "192.168.1.20:50000");
        addressBox.FlowDirection = FlowDirection.LeftToRight;
        footer.Children.Add(Ui.Row(8, Ui.Muted(Ui.T("Not in the list? Type the address the other PC shows:"), 12), addressBox, Ui.Button(Ui.T("Find"), FindByAddress)));
        footer.Children.Add(queuedText.Dock(Dock.Right));
        deviceList = Ui.List(("PC", 300, "PlainCell"), ("Status", 170), ("Trust", 250), ("Address", 220));
        deviceList.MinHeight = 130;
        deviceList.SelectionMode = SelectionMode.Single;
        deviceList.AllowDrop = true;
        deviceList.Drop += (_, e) =>
        {
            // Dropping files on a PC's row sends them to that PC.
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                SendTo(Ui.RowAt(e.OriginalSource)?.Tag as PortalPeer ?? Chosen(), dropped);
        };
        deviceCard.SetBody(deviceList);
        page.Children.Add(deviceCard);

        // --- what is moving, and what has moved ---
        var transferCard = new CardPanel("Transfers", "");
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Cancel selected"), CancelSelected));
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory).Margin(6));
        transferList = Ui.List(("Name", 320, "PlainCell"), ("Direction", 190), ("Progress", 200), ("Speed", 110), ("Status", 300));
        transferList.MinHeight = 160;
        transferList.ItemsSource = transferRows;
        transferCard.SetBody(transferList);
        page.Children.Add(transferCard);

        Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        timer.Tick += (_, _) => ShowTransfers();
        IsVisibleChanged += (_, _) => timer.IsEnabled = IsVisible;
        // A node started earlier (the window was closed to the tray meanwhile) is picked up again.
        if (Node is { } running)
            running.Changed += OnChanged;
        ShowAll();
    }

    public override void Shown() => ShowAll();

    private void OnChanged() => Dispatcher.BeginInvoke(ShowAll);

    /// <summary>Files from elsewhere in the program (the drop zone): sent at once if there is one PC to send to, otherwise kept until one is chosen.</summary>
    public void Queue(IEnumerable<string> paths)
    {
        queued.AddRange(paths.Where(path => File.Exists(path) || Directory.Exists(path)));
        var ready = Node?.Peers.Where(peer => peer is { Trusted: true, Online: true }).ToList() ?? [];
        if (ready.Count == 1)
            SendTo(ready[0], []);
        ShowAll();
    }

    // --- starting and stopping ---

    private async void Toggle()
    {
        if (starting)
            return;
        if (Node is { } running)
        {
            running.Changed -= OnChanged;
            await running.Stop();
            ShowAll();
            return;
        }
        // The test hook keeps Portal on this PC only, so trying the page never opens a port to the network.
        var loopback = Environment.GetEnvironmentVariable("PCBUTLER_PORTAL_LOOPBACK") == "1";
        var address = loopback ? IPAddress.Loopback : (addresses.SelectedItem as ComboBoxItem)?.Tag as IPAddress;
        if (address is null)
        {
            Ui.Info("Portal", Ui.T("This PC is not connected to a network. Connect it to the same network as the other PC, then start Portal."));
            return;
        }
        starting = true;
        try
        {
            var settings = Main.Config.Portal;
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            var node = await PortalNode.Start(settings, address, () => Dispatcher.BeginInvoke(() =>
                Main.UpdateConfig(config => config.Portal.Devices = settings.Devices.ToList())), discovery: !loopback);
            node.Changed += OnChanged;
            node.Attention += App.Current.PortalNeedsAttention;
            Portal.Current = node;
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

    // --- showing ---

    private void ShowAll()
    {
        var node = Node;
        startButton.Content = Ui.T(node is null ? "Start Portal" : "Stop Portal");
        ShowStatus(node);
        ShowAsks(node);
        ShowSettings(node);
        ShowDevices(node);
        ShowTransfers();
    }

    private void ShowStatus(PortalNode? node)
    {
        status.Children.Clear();
        if (node is null)
        {
            status.Children.Add(Ui.Text(Ui.T("Portal is off"), 18, weight: FontWeights.SemiBold));
            status.Children.Add(Ui.Muted(Ui.T("Start Portal here and on the other PC. The two find each other on the network; pair them once, then send files "
                                              + "and folders either way."), 13, wrap: true).Margin(0, 8, 0, 0));
            status.Children.Add(Ui.Muted(Ui.T("Files go straight between the PCs, encrypted, over your own network. Nothing is uploaded to the internet, and "
                                              + "nothing listens while Portal is off."), 13, wrap: true).Margin(0, 8, 0, 0));
            return;
        }
        status.Children.Add(Ui.Row(10, Ui.Text("●", 12, "Primary"), Ui.Text(Ui.T("Portal is on"), 18, weight: FontWeights.SemiBold)));
        status.Children.Add(Ui.Muted(Ui.T("Other PCs see this one as:"), 12).Margin(0, 12, 0, 2));
        status.Children.Add(Ui.Text(node.Name, 16, weight: FontWeights.SemiBold));
        status.Children.Add(Ui.Muted(Ui.T("Its address, to type on the other PC if it does not show up there:"), 12, wrap: true).Margin(0, 12, 0, 2));
        var address = new TextBox
        {
            Text = $"{node.Address}:{node.Port}", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 16, Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        status.Children.Add(address);
        if (Portal.OnPublicNetwork())
        {
            status.Children.Add(Ui.Text(Ui.T("Windows treats this network as Public, where the firewall keeps other PCs out. Portal works on networks set to "
                                             + "Private (Windows Settings, Network)."), 12, "Amber", wrap: true).Margin(0, 12, 0, 0));
        }
    }

    /// <summary>What waits for an answer: pairings (the code to compare) and files another PC wants to send.</summary>
    private void ShowAsks(PortalNode? node)
    {
        asks.Children.Clear();
        started.RemoveAll(pairing => pairing.State != "waiting");
        if (node is null)
            return;
        foreach (var pairing in node.Pairings.Where(pairing => pairing.Code.Length > 0))
        {
            var code = new TextBlock
            {
                Text = pairing.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 34, FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0),
            };
            var match = Ui.Button(Ui.T("They match"), () => node.Confirm(pairing), accent: true);
            asks.Children.Add(Ask(Ui.T(pairing.Incoming ? "{0} wants to pair with this PC" : "Pairing with {0}", pairing.PeerName),
                                  Ui.T("Look at the other PC's screen. Press “They match” on both PCs only if it shows this same code. If the codes differ, "
                                       + "refuse: someone else on the network may be in between."),
                                  code, match, Ui.Button(Ui.T("Refuse"), () => node.Refuse(pairing))));
        }
        foreach (var request in node.Requests)
        {
            var accept = Ui.Button(Ui.T("Accept"), () => node.Accept(request.Id), accent: true);
            asks.Children.Add(Ask(Ui.T("{0} wants to send {1} file(s)", request.PeerName, request.Files),
                                  $"{request.Name}  ·  {Ui.FormatSize(request.Size)}", null, accept, Ui.Button(Ui.T("Refuse"), () => node.Refuse(request.Id))));
        }
    }

    private static Border Ask(string title, string detail, UIElement? lead, params Button[] buttons)
    {
        var row = new DockPanel { Margin = new Thickness(24, 16, 24, 16) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        foreach (var button in buttons)
        {
            button.MinWidth = 110;
            actions.Children.Add(button.Margin(actions.Children.Count == 0 ? 0 : 8));
        }
        row.Children.Add(actions.Dock(Dock.Right));
        if (lead is not null)
            row.Children.Add(lead.Dock(Dock.Right));
        row.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Text(title, 16, weight: FontWeights.SemiBold, wrap: true), Ui.Muted(detail, 12.5, wrap: true).Margin(0, 4, 0, 0) },
        });
        var card = new Border { Child = row, Margin = new Thickness(0, 0, 0, 12) };
        Widgets.StyleCard(card);
        card.BorderBrush = Ui.Paint("Primary");
        return card;
    }

    private void ShowSettings(PortalNode? node)
    {
        var allowed = Portal.FirewallAllows();
        firewallText.Text = Ui.T(allowed ? "Other PCs on private networks are allowed to reach PC Butler."
                                         : "Windows blocks other PCs from reaching PC Butler until it is allowed. This is needed once, on each PC.");
        firewallButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        folderText.Text = Portal.ReceiveFolder(Main.Config.Portal);
        folderText.FlowDirection = FlowDirection.LeftToRight;

        var chosen = (addresses.SelectedItem as ComboBoxItem)?.Tag as IPAddress;
        addresses.Items.Clear();
        foreach (var (address, adapter) in Portal.LocalAddresses())
            addresses.Items.Add(new ComboBoxItem { Content = $"{address}  ·  {adapter}", Tag = address });
        addresses.SelectedIndex = addresses.Items.Count == 0 ? -1
            : Math.Max(0, addresses.Items.OfType<ComboBoxItem>().ToList().FindIndex(item => Equals(item.Tag, chosen)));
        addresses.IsEnabled = node is null;
    }

    private void ShowDevices(PortalNode? node)
    {
        var chosen = Chosen()?.Id;
        var peers = node?.Peers ?? [];
        // While Portal is off, the PCs paired before are still listed, so they can be forgotten.
        var rows = node is null
            ? Main.Config.Portal.Devices.Select(device => new Row
            {
                Name = device.Name, Tag = device, Dim = true, Cells = [Ui.T("Portal is off"), Ui.T("Paired"), $"{device.Address}:{device.Port}"],
            }).ToList()
            : peers.Select(peer => new Row
            {
                Name = peer.Name.Length > 0 ? peer.Name : "PC", Tag = peer, Dim = !peer.Online,
                Cells =
                [
                    Ui.T(peer.Online ? "On this network" : "Not seen"),
                    Ui.T(!peer.Trusted ? "Not paired yet" : peer.AutoAccept ? "Paired, accepts automatically" : "Paired, asks before taking files"),
                    peer.Address.Length > 0 ? $"{peer.Address}:{peer.Port}" : "",
                ],
            }).ToList();
        deviceList.ItemsSource = rows;
        deviceList.SelectedItem = rows.FirstOrDefault(row => (row.Tag as PortalPeer)?.Id == chosen) ?? (rows.Count == 1 ? rows[0] : null);
        queuedText.Text = queued.Count > 0 ? Ui.T("{0} item(s) ready: choose a PC and press Send files", queued.Count) : "";
    }

    private PortalPeer? Chosen() => (deviceList.SelectedItem as Row)?.Tag as PortalPeer;

    /// <summary>The node's transfers with live progress on top, then the log of earlier ones.</summary>
    private void ShowTransfers()
    {
        var live = Node?.Transfers ?? [];
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
            var state = Ui.T(transfer.State switch
            {
                "waiting" => transfer.Incoming ? "Waiting for your answer" : "Waiting for the other PC to accept",
                "done" => "Done", "cancelled" => "Cancelled", "refused" => "Refused", "failed" => "Failed", _ => "Moving…",
            });
            entry.Row.Cells =
            [
                Ui.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer),
                active ? $"{percent}%  ·  {Ui.FormatSize(done)} / {Ui.FormatSize(transfer.Size)}" : Ui.FormatSize(transfer.Size),
                active && entry.Speed > 0 ? Ui.FormatSize((long)entry.Speed) + "/s" : "",
                transfer.Note.Length > 0 ? $"{state}: {transfer.Note}" : state,
            ];
            entry.Row.Dim = !transfer.Running;
            index++;
        }
        // Rows of a node that has stopped give way to the log, which holds the same transfers.
        foreach (var gone in shown.Keys.Where(transfer => !live.Contains(transfer)).ToList())
        {
            transferRows.Remove(shown[gone].Row);
            shown.Remove(gone);
        }
        var finished = live.Where(transfer => transfer.State == "done").Select(transfer => (transfer.Name, transfer.Size)).ToList();
        var log = Portal.History().Where(item => !finished.Remove((item.Name, item.Size))).ToList();
        var logRows = transferRows.Where(row => row.Tag is PortalLogEntry).ToList();
        if (logRows.Count != log.Count || (log.Count > 0 && !Equals(logRows[0].Tag, log[0])))
        {
            foreach (var row in logRows)
                transferRows.Remove(row);
            foreach (var item in log)
            {
                transferRows.Add(new Row
                {
                    Name = item.Name, Tag = item, Dim = true,
                    Cells = [Ui.T(item.Incoming ? "From {0}" : "To {0}", item.Device), Ui.FormatSize(item.Size), "", Ui.T("Done") + "  ·  " + item.Time],
                });
            }
        }
    }

    // --- buttons ---

    /// <summary>The PC to send to, or null with a word to the user about what is missing.</summary>
    private PortalPeer? Target()
    {
        if (Node is null)
        {
            Ui.Info("Portal", Ui.T("Start Portal first, here and on the other PC."));
            return null;
        }
        var peer = Chosen();
        if (peer is null)
            Ui.Info("Portal", Ui.T("Select a PC in the list first."));
        else if (!peer.Trusted)
            Ui.Info("Portal", Ui.T("{0} is not paired with this PC yet. Select it and press Pair.", peer.Name));
        else if (!peer.Online)
            Ui.Info("Portal", Ui.T("{0} is not on the network right now. Start Portal on it.", peer.Name));
        else
            return peer;
        return null;
    }

    private void SendTo(PortalPeer? peer, IEnumerable<string> paths)
    {
        if (peer is null || Node is not { } node || peer is not { Trusted: true, Online: true })
        {
            // Nowhere to send yet: keep them, and say what is missing when asked.
            queued.AddRange(paths);
            if (Target() is not { } target)
            {
                ShowAll();
                return;
            }
            peer = target;
            node = Node!;
            paths = [];
        }
        var all = queued.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        queued.Clear();
        if (all.Count > 0 && node.Send(peer, all) is null)
            Ui.Info("Portal", Ui.T("None of those files could be read."));
        ShowAll();
    }

    private void SendFiles()
    {
        if (Target() is not { } peer)
            return;
        if (queued.Count > 0)
        {
            SendTo(peer, []);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to send to {0}", peer.Name) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SendTo(peer, dialog.FileNames);
    }

    private void SendFolder()
    {
        if (Target() is { } peer && Ui.PickFolder(Ui.T("Folder to send to {0}", peer.Name)) is { } folder)
            SendTo(peer, [folder]);
    }

    private async void PairSelected()
    {
        if (Node is not { } node)
        {
            Ui.Info("Portal", Ui.T("Start Portal first, here and on the other PC."));
            return;
        }
        if (Chosen() is not { } peer)
        {
            Ui.Info("Portal", Ui.T("Select a PC in the list first."));
            return;
        }
        if (await node.Pair(peer) is { } pairing)
            started.Add(pairing);
        else
            Ui.Warn("Portal", Ui.T("{0} did not answer. Check that Portal is running on it and that its firewall allows PC Butler.", peer.Name));
        ShowAll();
    }

    private void ToggleAuto()
    {
        if (Node is { } node && Chosen() is { Trusted: true } peer)
            node.SetAutoAccept(peer.Id, !peer.AutoAccept);
        else
            Ui.Info("Portal", Ui.T("Select a paired PC first (with Portal on)."));
    }

    private void ForgetSelected()
    {
        var row = deviceList.SelectedItem as Row;
        var (id, name) = row?.Tag switch
        {
            PortalPeer { Trusted: true } peer => (peer.Id, peer.Name),
            PortalDevice device => (device.Id, device.Name),
            _ => ("", ""),
        };
        if (id.Length == 0)
        {
            Ui.Info("Portal", Ui.T("Select a paired PC first."));
            return;
        }
        if (!Ui.Ask("Portal", Ui.T("Forget {0}? It will have to be paired again before files can go either way.", name)))
            return;
        if (Node is { } node)
            node.Forget(id);
        else
            Main.UpdateConfig(config => config.Portal.Devices.RemoveAll(device => device.Id == id));
        ShowAll();
    }

    private async void FindByAddress()
    {
        if (Node is not { } node)
        {
            Ui.Info("Portal", Ui.T("Start Portal first, here and on the other PC."));
            return;
        }
        var parts = addressBox.Text.Trim().Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out _) || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
        {
            Ui.Info("Portal", Ui.T("Type the address exactly as the other PC shows it under “This PC”, like 192.168.1.20:50000."));
            return;
        }
        if (await node.AddByAddress(parts[0], port) is null)
            Ui.Warn("Portal", Ui.T("No PC Butler Portal answered at {0}. Check that Portal is running there and that its firewall allows PC Butler.", addressBox.Text.Trim()));
        else
            addressBox.Text = "";
        ShowAll();
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
        ShowSettings(Node);
        if (Node is not null)
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
                if (!Ui.Ask("Portal", Ui.T("Windows only lets a program accept connections from other PCs once an administrator allows it.\n\n"
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
            ShowSettings(Node);
        }
    }

    public override void Closing()
    {
        if (Node is { } node)
            node.Changed -= OnChanged;
    }
}
