// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Courier: files between PCs on different networks, across the internet. One PC is the
/// host; the others are clients that connect to it by its ID. A client is paired with the
/// host once by comparing a six-digit code; then files go between the host and each client,
/// and from one client to another through the host.
/// </summary>
public sealed class PortalPage : Page
{
    private readonly Button startButton;
    private readonly StackPanel status = new() { Margin = new Thickness(24, 18, 24, 18) };
    private readonly StackPanel asks = new();
    private readonly StackPanel roleRows = new();
    private readonly RadioButton hostChoice;
    private readonly RadioButton clientChoice;
    private readonly TextBox hostNumberBox;
    private readonly TextBlock firewallText = Ui.Muted("", 12);
    private readonly Button firewallButton;
    private readonly Border firewallRow;
    private readonly TextBlock folderText = Ui.Muted("", 12);
    private readonly CardPanel deviceCard;
    private readonly ListView deviceList;
    private readonly Button autoButton;
    private readonly Button forgetButton;
    private readonly Button pairButton;
    private readonly TextBlock queuedText = Ui.Text("", 13, "Primary");
    private readonly ListView transferList;
    private readonly ObservableCollection<Row> transferRows = [];
    private readonly Dictionary<PortalTransfer, (Row Row, long Done, DateTime At, double Speed)> shown = [];
    /// <summary>Files handed over from the drop zone, waiting for a PC to be chosen.</summary>
    private readonly List<string> queued = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool starting, showingRole;

    private static PortalNode? Node => Portal.Current is { Stopped: false } node ? node : null;
    private PortalSettings Settings => Main.Config.Portal;
    private bool IsHost => Settings.Role == PortalSettings.Host;

    public PortalPage(MainWindow main) : base(main, "Courier", "Files between your PCs, across the internet.")
    {
        startButton = AddAction("Start Courier", Toggle, accent: true);
        var page = new StackPanel();

        // --- requests come first: they wait for an answer ---
        page.Children.Add(asks);

        // --- this PC, beside its settings ---
        var statusCard = new CardPanel("This PC", "\uE7F4") { Margin = new Thickness(0, 0, 6, 18) };
        statusCard.SetBody(status);

        hostChoice = new RadioButton { GroupName = "courier-role", Content = Ui.T("Host: the PC the others connect to") };
        clientChoice = new RadioButton { GroupName = "courier-role", Content = Ui.T("Client: connects to a host by its ID") };
        hostChoice.Checked += (_, _) => ChooseRole(PortalSettings.Host);
        clientChoice.Checked += (_, _) => ChooseRole(PortalSettings.Client);
        hostNumberBox = Ui.Field(width: 190, hint: "000 000 000 000");
        hostNumberBox.FlowDirection = FlowDirection.LeftToRight;
        hostNumberBox.LostFocus += (_, _) => SaveHostNumber();
        hostNumberBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                SaveHostNumber();
        };
        roleRows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("This PC is"), 13, weight: FontWeights.Bold),
                                            Ui.Muted(Ui.T("Choose one host. Every other PC is a client of it, on any network."), 12),
                                            new StackPanel { Children = { hostChoice, clientChoice.Margin(0, 6, 0, 0) } }, first: true));
        roleRows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("The host's ID"), 13, weight: FontWeights.Bold),
                                            Ui.Muted(Ui.T("The twelve digits the host shows under “This PC”."), 12), hostNumberBox));
        firewallButton = Ui.Button(Ui.T("Allow (asks for administrator rights)"), AllowFirewall);
        firewallRow = Ui.SettingRow(Ui.Text(Ui.T("Windows firewall"), 13, weight: FontWeights.Bold), firewallText, firewallButton);
        roleRows.Children.Add(firewallRow);
        roleRows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Received files go to"), 13, weight: FontWeights.Bold), folderText,
                                            Ui.Row(6, Ui.Button(Ui.T("Open"), OpenFolder), Ui.Button(Ui.T("Change…"), ChangeFolder))));
        var settingsCard = new CardPanel("Settings", "\uE713") { Margin = new Thickness(6, 0, 0, 18) };
        settingsCard.SetBody(roleRows);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        top.Children.Add(statusCard.At(0, 0));
        top.Children.Add(settingsCard.At(0, 1));
        page.Children.Add(top);

        // --- the other PCs: the clients (on the host), or the host and its other clients (on a client) ---
        deviceCard = new CardPanel("Clients", "\uE8AB") { Margin = new Thickness(0, 0, 0, 18) };
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send files…"), SendFiles, accent: true));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send folder…"), SendFolder).Margin(6));
        pairButton = Ui.Button(Ui.T("Pair again"), PairWithHost).Margin(6);
        deviceCard.Controls.Children.Add(pairButton);
        autoButton = Ui.Button(Ui.T("Accept automatically: on/off"), ToggleAuto).Margin(6);
        deviceCard.Controls.Children.Add(autoButton);
        forgetButton = Ui.Button(Ui.T("Forget"), ForgetSelected).Margin(6);
        deviceCard.Controls.Children.Add(forgetButton);
        var footer = deviceCard.AddFooter();
        footer.Children.Add(queuedText.Dock(Dock.Right));
        deviceList = Ui.List(("PC", 260, "PlainCell"), ("Status", 200), ("Trust", 240), ("ID", 150), ("Address", 220));
        deviceList.MinHeight = 130;
        deviceList.SelectionMode = SelectionMode.Single;
        deviceList.AllowDrop = true;
        deviceList.Drop += (_, e) =>
        {
            // Dropping files on a PC's row sends them to that PC.
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                SendTo(Ui.RowAt(e.OriginalSource)?.Tag ?? (deviceList.SelectedItem as Row)?.Tag, dropped);
        };
        deviceList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                SendFiles();
        };
        deviceCard.SetBody(deviceList);
        page.Children.Add(deviceCard);

        // --- what is moving, and what has moved ---
        var transferCard = new CardPanel("Transfers", "\uE896");
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Cancel selected"), CancelSelected));
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory).Margin(6));
        transferList = Ui.List(("Name", 320, "PlainCell"), ("Direction", 230), ("Progress", 200), ("Speed", 110), ("Status", 300));
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
        var targets = Targets().Where(target => target.Ready).ToList();
        if (targets.Count == 1)
            SendTo(targets[0].Tag, []);
        ShowAll();
    }

    // --- the role, and starting and stopping ---

    private void ChooseRole(string role)
    {
        if (showingRole || Settings.Role == role)
            return;
        if (Settings.Devices.Count > 0 && !Ui.Ask("Courier", Ui.T("Changing this PC's role forgets the PCs it was paired with; they will have to be paired again. Change it?")))
        {
            ShowSettings();
            return;
        }
        Main.UpdateConfig(config => { config.Portal.Role = role; config.Portal.Devices = []; });
        ShowAll();
    }

    private void SaveHostNumber()
    {
        var number = new string(hostNumberBox.Text.Where(char.IsDigit).ToArray());
        if (number == Settings.HostNumber)
            return;
        Main.UpdateConfig(config => config.Portal.HostNumber = number);
        Node?.SetHost(number);
        hostNumberBox.Text = Portal.ShowNumber(number);
        ShowAll();
    }

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
        if (Settings.Role is not (PortalSettings.Host or PortalSettings.Client))
        {
            Ui.Info("Courier", Ui.T("Choose first whether this PC is the host or a client (under Settings, on the right)."));
            return;
        }
        SaveHostNumber();
        if (!IsHost && Settings.HostNumber.Length != 12)
        {
            Ui.Info("Courier", Ui.T("Type the host's ID first: the twelve digits it shows under “This PC” when Courier runs on it."));
            hostNumberBox.Focus();
            return;
        }
        starting = true;
        try
        {
            var settings = Settings;
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            Portal.ClearRelay();
            // The test hook keeps Courier on this PC only, so trying the page never opens a port to the internet.
            var local = Environment.GetEnvironmentVariable("PCBUTLER_PORTAL_LOOPBACK") == "1";
            var node = await PortalNode.Start(settings, () => Dispatcher.BeginInvoke(() =>
                Main.UpdateConfig(config => { config.Portal.Devices = settings.Devices.ToList(); config.Portal.HostNumber = settings.HostNumber; })), local: local);
            node.Changed += OnChanged;
            node.Attention += App.Current.PortalNeedsAttention;
            Portal.Current = node;
        }
        catch (IOException error)
        {
            Ui.Warn("Courier", Ui.T("Courier could not start.") + "\n\n" + error.Message);
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
        startButton.Content = Ui.T(node is null ? "Start Courier" : "Stop Courier");
        ShowStatus(node);
        ShowAsks(node);
        ShowSettings();
        ShowDevices(node);
        ShowTransfers();
    }

    private static TextBox Copyable(string text, double size) => new()
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = size, Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private void Line(string text, string brush = "Muted") =>
        status.Children.Add(Ui.Text(text, 12, brush, wrap: true).Margin(0, 8, 0, 0));

    private void ShowStatus(PortalNode? node)
    {
        status.Children.Clear();
        if (node is null)
        {
            status.Children.Add(Ui.Text(Ui.T("Courier is off"), 18, weight: FontWeights.SemiBold));
            Line(Ui.T("One PC is the host; every other PC, on any network, is a client of it. Start Courier on the host, then on each client "
                      + "with the host's ID. Pair each client with the host once, then send files and folders either way, and from client to client."));
            Line(Ui.T("Files go straight between the PCs, encrypted, across the internet. There is no server in between, nothing is opened to "
                      + "the local network, and nothing listens while Courier is off."));
            return;
        }
        status.Children.Add(Ui.Row(10, Ui.Text("●", 12, "Primary"),
                                   Ui.Text(Ui.T(node.IsHost ? "Courier is on: host" : "Courier is on: client"), 18, weight: FontWeights.SemiBold)));
        status.Children.Add(Ui.Muted(Ui.T("This PC ({0}) has the ID:", node.Name), 12).Margin(0, 12, 0, 0));
        status.Children.Add(Copyable(Portal.ShowNumber(node.Number), 26));
        if (node.IsHost)
            ShowHostStatus(node);
        else
            ShowClientStatus(node);
    }

    /// <summary>The host: its ID to give out, and how well clients can get through to it.</summary>
    private void ShowHostStatus(PortalNode node)
    {
        Line(Ui.T("Give this ID to each client. They find this PC by it, from any network."));
        if (!node.MappingTried)
            Line(Ui.T("Asking the router to open a port for Courier…"));
        else if (node.Mapping is { } given)
            Line(Ui.T("The router forwards a port to this PC ({0}), so clients on any connection, mobile data included, can reach it.", given.Method), "Primary");
        else
            Line(Ui.T("The router did not open a port (UPnP and NAT-PMP are off on it, or missing). Clients still connect by opening a path through both "
                      + "routers; that works on most home connections, but not always from mobile data. Turning on UPnP in the router helps."), "Amber");
        if (node.PublicAddress6 is not null)
            Line(Ui.T("IPv6 works on this connection: clients that have IPv6 too get through without any port being opened."), "Primary");
        if (node.AddressKeepsChanging && node.Mapping is null)
            Line(Ui.T("This connection gives every link a new address (as mobile data and some providers do), so clients may not get through. "
                      + "A host works best on a home internet connection."), "Amber");
        if (node.PublicAddress is { } outside)
            status.Children.Add(Ui.Muted(Ui.T("Seen from the internet as {0}", outside), 11.5).Margin(0, 10, 0, 0));
    }

    /// <summary>A client: whether it has reached its host, and what is in the way if not.</summary>
    private void ShowClientStatus(PortalNode node)
    {
        var host = node.HostPeer;
        var number = Portal.ShowNumber(Settings.HostNumber);
        if (host is { Online: true, Trusted: true })
            Line(Ui.T("Connected to the host {0} ({1}).", host.Name, number), "Primary");
        else if (host is { Online: true })
            Line(Ui.T("Connected to {0}. Compare the code above on both PCs to pair them.", host.Name), "Amber");
        else if (node.Connecting)
            Line(Ui.T("Looking for the host {0}… this can take up to a minute.", number));
        else
        {
            Line(Ui.T(node.HostProblem switch
            {
                PortalConnectProblem.NoDirectory => "This PC could not reach the public directory that IDs are looked up in. This network may block it.",
                PortalConnectProblem.NotFound => "No host is announced under {0}. Check the ID, and that Courier is running on the host as a host.",
                PortalConnectProblem.NoPath => "The host {0} was found, but the two networks would not let a link through. Turning on UPnP in the host's router "
                                               + "fixes this in most cases; a host on mobile data can't be reached.",
                PortalConnectProblem.BadNumber => "{0} is not the ID of another PC.",
                _ => "Connecting to the host {0}…",
            }, number), node.HostProblem == PortalConnectProblem.None ? "Muted" : "Amber");
            if (node.HostProblem != PortalConnectProblem.None)
                Line(Ui.T("It tries again by itself every 20 seconds."));
        }
    }

    /// <summary>What waits for an answer: pairings (the code to compare) and files another PC wants to send.</summary>
    private void ShowAsks(PortalNode? node)
    {
        asks.Children.Clear();
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
            asks.Children.Add(Ask(Ui.T(pairing.Incoming ? "{0} wants to join this host" : "Pairing with the host {0}", pairing.PeerName),
                                  Ui.T("Look at the other PC's screen. Press “They match” on both PCs only if it shows this same code. If the codes differ, "
                                       + "refuse: someone else may be in between."),
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

    private void ShowSettings()
    {
        var off = Node is null;
        showingRole = true;
        hostChoice.IsChecked = Settings.Role == PortalSettings.Host;
        clientChoice.IsChecked = Settings.Role == PortalSettings.Client;
        showingRole = false;
        hostChoice.IsEnabled = clientChoice.IsEnabled = off;
        // The host's ID belongs to a client; it can be changed while running (the client then looks for the new host).
        ((FrameworkElement)roleRows.Children[1]).Visibility = Settings.Role == PortalSettings.Client ? Visibility.Visible : Visibility.Collapsed;
        if (!hostNumberBox.IsKeyboardFocused)
            hostNumberBox.Text = Portal.ShowNumber(Settings.HostNumber);
        // Only the host takes connections it did not start, so only the host needs the firewall to let them in.
        var allowed = Portal.FirewallAllows();
        firewallRow.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        firewallText.Text = Ui.T(allowed ? "Clients across the internet are allowed to reach PC Butler."
                                         : "Windows blocks clients from reaching the host until it is allowed. This is needed once, on the host.");
        firewallButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        folderText.Text = Portal.ReceiveFolder(Settings);
        folderText.FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>One PC files can go to: the host, a client of the host, or (through the host) another client.</summary>
    private sealed record Target(string Name, object Tag, bool Ready);

    private List<Target> Targets()
    {
        if (Node is not { } node)
            return [];
        if (node.IsHost)
            return node.Peers.Where(peer => peer.Trusted).Select(peer => new Target(peer.Name, peer, peer.Online)).ToList();
        var host = node.HostPeer;
        var list = new List<Target>();
        if (host is not null)
            list.Add(new Target(host.Name, host, host is { Online: true, Trusted: true }));
        list.AddRange(node.OtherClients.Select(client => new Target(client.Name, client, client.Online && host is { Online: true, Trusted: true })));
        return list;
    }

    private void ShowDevices(PortalNode? node)
    {
        var chosen = (deviceList.SelectedItem as Row)?.Tag switch { PortalPeer peer => peer.Id, PortalClient client => client.Id, PortalDevice device => device.Id, _ => "" };
        deviceCard.Title = Ui.T(IsHost ? "Clients" : "Host and other clients");
        autoButton.Visibility = forgetButton.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        pairButton.Visibility = !IsHost && node?.HostPeer is { Online: true, Trusted: false } ? Visibility.Visible : Visibility.Collapsed;
        List<Row> rows;
        if (node is null)
        {
            // While Courier is off, the PCs paired before are still listed, so they can be forgotten.
            rows = Settings.Devices.Select(device => new Row
            {
                Name = device.Name, Tag = device, Dim = true,
                Cells = [Ui.T("Courier is off"), Ui.T(IsHost ? "Paired client" : "Paired host"), Portal.ShowNumber(Portal.NumberOf(device.Id)), ""],
            }).ToList();
        }
        else if (node.IsHost)
        {
            rows = node.Peers.Where(peer => peer.Trusted).Select(peer => new Row
            {
                Name = peer.Name.Length > 0 ? peer.Name : "PC", Tag = peer, Dim = !peer.Online,
                Cells =
                [
                    Ui.T(peer.Online ? "Connected" : "Not connected"),
                    Ui.T(peer.AutoAccept ? "Paired, accepts automatically" : "Paired, asks before taking files"),
                    Portal.ShowNumber(peer.Number), peer.Tunnel?.ToString() ?? "",
                ],
            }).ToList();
        }
        else
        {
            rows = [];
            if (node.HostPeer is { } host)
            {
                rows.Add(new Row
                {
                    Name = host.Name.Length > 0 ? host.Name : "PC", Tag = host, Dim = !host.Online,
                    Cells =
                    [
                        Ui.T(host.Online ? "Host, connected" : "Host, not connected"), Ui.T(host.Trusted ? "Paired" : "Not paired yet"),
                        Portal.ShowNumber(host.Number), host.Tunnel?.ToString() ?? "",
                    ],
                });
            }
            rows.AddRange(node.OtherClients.Select(client => new Row
            {
                Name = client.Name, Tag = client, Dim = !client.Online,
                Cells = [Ui.T(client.Online ? "Client, connected" : "Client, not connected"), Ui.T("Through the host"), Portal.ShowNumber(Portal.NumberOf(client.Id)), ""],
            }));
        }
        deviceList.ItemsSource = rows;
        deviceList.SelectedItem = rows.FirstOrDefault(row => row.Tag switch { PortalPeer peer => peer.Id, PortalClient client => client.Id, PortalDevice device => device.Id, _ => "" } == chosen)
                                  ?? (rows.Count == 1 ? rows[0] : null);
        queuedText.Text = queued.Count > 0 ? Ui.T("{0} item(s) ready: choose a PC and press Send files", queued.Count) : "";
    }

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

    // --- sending ---

    /// <summary>Where the chosen row sends to, or null with a word to the user about what is missing.</summary>
    private object? Chosen(bool say = true)
    {
        void Say(string text)
        {
            if (say)
                Ui.Info("Courier", text);
        }

        if (Node is not { } node)
        {
            Say(Ui.T(IsHost ? "Start Courier first." : "Start Courier first, then wait until it is connected to the host."));
            return null;
        }
        var targets = Targets();
        var tag = (deviceList.SelectedItem as Row)?.Tag ?? (targets.Count == 1 ? targets[0].Tag : null);
        var target = targets.FirstOrDefault(item => ReferenceEquals(item.Tag, tag));
        if (target is null)
            Say(Ui.T(targets.Count == 0 ? node.IsHost ? "No client is paired with this host yet." : "This PC is not connected to the host yet."
                                        : "Select a PC in the list first."));
        else if (!target.Ready)
            Say(Ui.T("{0} is not connected right now. It can get files once Courier runs on it and it is connected to the host.", target.Name));
        else
            return target.Tag;
        return null;
    }

    private void SendTo(object? target, IEnumerable<string> paths)
    {
        queued.AddRange(paths);
        target = target is not null && Targets().Any(item => ReferenceEquals(item.Tag, target) && item.Ready) ? target : Chosen();
        if (target is null || Node is not { } node)
        {
            // Nowhere to send yet: they are kept until a PC is chosen.
            ShowAll();
            return;
        }
        var all = queued.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        queued.Clear();
        var sent = target switch
        {
            PortalPeer peer => node.Send(peer, all),
            PortalClient client when node.HostPeer is { } host => node.Send(host, all, client),
            _ => null,
        };
        if (all.Count > 0 && sent is null)
            Ui.Info("Courier", Ui.T("None of those files could be read."));
        ShowAll();
    }

    private void SendFiles()
    {
        if (Chosen() is not { } target)
            return;
        if (queued.Count > 0)
        {
            SendTo(target, []);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to send to {0}", NameOf(target)) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SendTo(target, dialog.FileNames);
    }

    private void SendFolder()
    {
        if (Chosen() is { } target && Ui.PickFolder(Ui.T("Folder to send to {0}", NameOf(target))) is { } folder)
            SendTo(target, [folder]);
    }

    private static string NameOf(object target) => target switch { PortalPeer peer => peer.Name, PortalClient client => client.Name, _ => "" };

    private async void PairWithHost()
    {
        if (Node is { HostPeer: { Online: true, Trusted: false } host } node && await node.Pair(host) is null)
            Ui.Warn("Courier", Ui.T("{0} did not answer. Check that Courier is running on it as the host.", host.Name));
        ShowAll();
    }

    private void ToggleAuto()
    {
        if (Node is { } node && (deviceList.SelectedItem as Row)?.Tag is PortalPeer { Trusted: true } peer)
            node.SetAutoAccept(peer.Id, !peer.AutoAccept);
        else
            Ui.Info("Courier", Ui.T("Select a paired client first (with Courier on)."));
    }

    private void ForgetSelected()
    {
        var (id, name) = (deviceList.SelectedItem as Row)?.Tag switch
        {
            PortalPeer { Trusted: true } peer => (peer.Id, peer.Name),
            PortalDevice device => (device.Id, device.Name),
            _ => ("", ""),
        };
        if (id.Length == 0)
        {
            Ui.Info("Courier", Ui.T("Select a paired PC first."));
            return;
        }
        if (!Ui.Ask("Courier", Ui.T("Forget {0}? It will have to be paired again before files can go either way.", name)))
            return;
        if (Node is { } node)
            node.Forget(id);
        else
            Main.UpdateConfig(config => config.Portal.Devices.RemoveAll(device => device.Id == id));
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
        var folder = Portal.ReceiveFolder(Settings);
        Directory.CreateDirectory(folder);
        Shell.Open(folder);
    }

    private void ChangeFolder()
    {
        if (Ui.PickFolder(Ui.T("Folder for files received through Courier"), Portal.ReceiveFolder(Settings)) is not { } folder)
            return;
        Main.UpdateConfig(config => config.Portal.Folder = folder);
        ShowSettings();
        if (Node is not null)
            Ui.Info("Courier", Ui.T("The new folder is used the next time Courier starts."));
    }

    /// <summary>The firewall rule needs administrator rights, so the optional helper adds it (Windows asks first).</summary>
    private async void AllowFirewall()
    {
        firewallButton.IsEnabled = false;
        try
        {
            if (!AdminHelper.Running)
            {
                if (!Ui.Ask("Courier", Ui.T("Windows only lets a program accept connections from other PCs once an administrator allows it.\n\n"
                                           + "PC Butler starts its small helper for this, and Windows will ask for your permission. The rule covers PC Butler "
                                           + "only, and only the kind of traffic Courier uses.\n\nAllow it now?"))
                    || !Sensors.StartHelper())
                    return;
                for (var tries = 0; tries < 40 && !AdminHelper.Running; tries++)
                    await Task.Delay(500);
            }
            var done = await Task.Run(() => AdminHelper.Send("allow_portal", []));
            if (done is null || !done.Contains("portal"))
                Ui.Warn("Courier", Ui.T("The firewall rule could not be added. You can also allow PC Butler yourself when Windows asks about it."));
        }
        finally
        {
            firewallButton.IsEnabled = true;
            ShowSettings();
        }
    }

    public override void Closing()
    {
        if (Node is { } node)
            node.Changed -= OnChanged;
    }
}
