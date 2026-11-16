// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Scan LAN: the devices on this PC's network (the same Wi-Fi or cable), with their IP and MAC addresses, names,
/// makers and whether they are on now. Each can be copied, connected to (web page, shared folders, Remote Desktop…),
/// given a name of the user's own, which is remembered, and looked at in full.
/// </summary>
public sealed class LanPage : Page
{
    private readonly StatCard[] stats;
    private readonly Button scanButton;
    private readonly SortableList list;
    private readonly ProgressBar progress = new() { Width = 220, Height = 6, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Maximum = 1 };
    private readonly TextBlock status = Ui.Muted("");
    private readonly Button connectButton;
    private List<LanDevice> devices = [];
    private LanNetwork? network;
    private CancellationTokenSource? running;
    private bool scannedOnce;

    public LanPage(MainWindow main) : base(main, "Scan LAN", "Which devices are on your network?")
    {
        scanButton = AddAction("Scan network", ToggleScan, accent: true);
        stats = AddStats(("Online now", ""), ("Known devices", ""), ("Network", ""));

        var hint = Ui.Muted(Ui.T("Every address on your network is asked for its MAC address and pinged, so phones and PCs that ignore pings are found too. "
                                 + "Nothing is sent outside your network, except that the list of adapter makers is read once from Wireshark's public list."), 11, wrap: true);
        hint.TextAlignment = TextAlignment.Center;
        Children.Add(hint.Margin(40, 10, 40, 0).Dock(Dock.Bottom));

        var panel = new CardPanel("Devices on your network", "");
        panel.Controls.Children.Add(Ui.Button(Ui.T("Copy"), () => WithSelected(CopyIp)));
        connectButton = Ui.Button(Ui.T("Connect"), () => WithSelected(device => OpenConnectMenu(device, connectButton!)));
        panel.Controls.Children.Add(connectButton.Margin(6));
        panel.Controls.Children.Add(Ui.Button(Ui.T("Name…"), () => WithSelected(Rename)).Margin(6));
        panel.Controls.Children.Add(Ui.Button(Ui.T("Info"), () => WithSelected(ShowInfo)).Margin(6));
        var footer = panel.AddFooter();
        footer.Children.Add(progress.Dock(Dock.Right));
        footer.Children.Add(status);
        list = Ui.List(("Name", 240, "PlainCell"), ("IP address", 130), ("MAC address", 165), ("Maker", 200), ("Kind", 140), ("Status", 170));
        list.SelectionMode = SelectionMode.Single;
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is { Tag: LanDevice device })
                ShowInfo(device);
        };
        list.ContextMenuOpening += (_, e) =>
        {
            if (list.SelectedItem is Row { Tag: LanDevice device })
                list.ContextMenu = RowMenu(device);
            else
                e.Handled = true;
        };
        list.ContextMenu = new ContextMenu();
        panel.SetBody(list);
        Children.Add(panel);

        devices = LanBook.Remembered();
        ShowDevices();
        status.Text = Ui.T("Press Scan network.");
    }

    public override void Shown()
    {
        // The first time the page is opened, it looks at once.
        if (!scannedOnce && running is null)
            ToggleScan();
    }

    private void WithSelected(Action<LanDevice> action)
    {
        if (list.SelectedItem is Row { Tag: LanDevice device })
            action(device);
        else
            Ui.Info("Scan LAN", Ui.T("Choose a device in the list first."));
    }

    // --- scanning ---

    private async void ToggleScan()
    {
        if (running is not null)
        {
            running.Cancel();
            return;
        }
        scannedOnce = true;
        running = new CancellationTokenSource();
        scanButton.Content = Ui.T("Stop");
        progress.Value = 0;
        progress.Visibility = Visibility.Visible;
        status.Text = Ui.T("Looking for devices…");
        try
        {
            var token = running.Token;
            var (found, all) = await Task.Run(() => LanScanner.Scan((done, total) => Dispatcher.BeginInvoke(() =>
            {
                progress.Value = (double)done / Math.Max(1, total);
                status.Text = done < total ? Ui.T("Looking for devices… {0} of {1} addresses", done, total) : Ui.T("Asking the devices their names…");
            }), token), token);
            network = found;
            if (found is null)
            {
                status.Text = Ui.T("This PC is not connected to a network.");
                return;
            }
            devices = LanBook.Merge(all);
            ShowDevices();
            status.Text = Ui.T("Done. {0} devices are on your network now.", devices.Count(device => device.Online));
        }
        catch (OperationCanceledException)
        {
            status.Text = Ui.T("Scan stopped.");
        }
        finally
        {
            running.Dispose();
            running = null;
            scanButton.Content = Ui.T("Scan network");
            progress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowDevices()
    {
        var selected = (list.SelectedItem as Row)?.Tag is LanDevice chosen ? chosen.Key : null;
        var ordered = devices.OrderByDescending(device => device.Online).ThenByDescending(device => device.IsThisPc)
                             .ThenByDescending(device => device.IsGateway).ThenBy(device => device.IpNumber).ToList();
        var rows = ordered.Select(device => new Row
        {
            Name = NameOf(device), Dim = !device.Online, Tag = device,
            Cells = [device.Ip, device.Mac.Length > 0 ? device.Mac : "—", MakerOf(device), KindOf(device), StatusOf(device)],
        }).ToList();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(row => ((LanDevice)row.Tag!).Key == selected);
        var online = devices.Count(device => device.Online);
        stats[0].Set(online.ToString(), "", scannedOnce ? Ui.T("Answered this scan") : Ui.T("Not scanned yet"));
        stats[1].Set(devices.Count(device => !device.IsThisPc).ToString(), "", Ui.T("Seen on this network so far"));
        network ??= LanScanner.Current();
        stats[2].Set(network?.ToString() ?? "—", "", network is null ? Ui.T("Not connected") : Ui.T("Router: {0}", network.Gateway?.ToString() ?? "—"));
    }

    private static string NameOf(LanDevice device)
    {
        var name = device.DisplayName.Length > 0 ? device.DisplayName : Ui.T("Unknown device");
        return device.IsThisPc ? Ui.T("{0} (this PC)", name) : name;
    }

    private static string MakerOf(LanDevice device) =>
        device.Vendor.Length > 0 ? device.Vendor : device.RandomMac ? Ui.T("Private address") : "—";

    private static string KindOf(LanDevice device) => device.Kind.Length > 0 ? Ui.T(device.Kind) : "—";

    private static string StatusOf(LanDevice device) =>
        device.Online
            ? device.PingMs >= 0 ? Ui.T("Online, {0} ms", device.PingMs) : Ui.T("Online")
            : device.LastSeen > DateTime.MinValue ? Ui.T("Offline, seen {0}", device.LastSeen.ToString("yyyy-MM-dd HH:mm")) : Ui.T("Offline");

    // --- what can be done with a device ---

    private ContextMenu RowMenu(LanDevice device)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Copy IP address", () => CopyIp(device)));
        if (device.Mac.Length > 0)
            menu.Items.Add(Ui.Item("Copy MAC address", () => Admin.Copy(device.Mac)));
        menu.Items.Add(Ui.Item("Copy all details", () => Admin.Copy(Details(device))));
        menu.Items.Add(new Separator());
        var connect = new MenuItem { Header = Ui.T("Connect") };
        foreach (var item in ConnectItems(device))
            connect.Items.Add(item);
        menu.Items.Add(connect);
        menu.Items.Add(Ui.Item("Name…", () => Rename(device)));
        menu.Items.Add(Ui.Item("Info", () => ShowInfo(device)));
        if (!device.Online && !device.IsThisPc)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Forget this device", () => Forget(device)));
        }
        return menu;
    }

    private static void CopyIp(LanDevice device) => Admin.Copy(device.Ip);

    private void OpenConnectMenu(LanDevice device, Button under)
    {
        var menu = new ContextMenu { PlacementTarget = under, Placement = PlacementMode.Bottom };
        foreach (var item in ConnectItems(device))
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    /// <summary>
    /// The ways to reach a device: those its open ports call for first, then the usual ones, which may still work
    /// (a firewall can hide a port from the scan), and Courier for sending files to a PC or phone.
    /// </summary>
    private List<object> ConnectItems(LanDevice device)
    {
        var ip = device.Ip;
        bool Has(int port) => device.OpenPorts.Contains(port);
        var found = new List<object>();
        var others = new List<object>();
        void Add(bool detected, string label, Action action) => (detected ? found : others).Add(Ui.Item(label, action));

        Add(Has(80) || device.IsGateway, "Open its web page", () => Open($"http://{ip}/"));
        if (Has(8080))
            Add(true, "Open its web page (port 8080)", () => Open($"http://{ip}:8080/"));
        Add(Has(443), "Open its secure web page", () => Open($"https://{ip}/"));
        Add(Has(445), "Open its shared folders", () => Run("explorer.exe", $"\\\\{ip}"));
        Add(Has(3389), "Remote Desktop", () => Run("mstsc.exe", $"/v:{ip}"));
        if (Has(22))
            Add(true, "SSH (in a terminal)", () => Run("cmd.exe", $"/k ssh {ip}"));
        if (Has(5900))
            Add(true, "VNC", () => Open($"vnc://{ip}"));
        if (Has(631))
            Add(true, "Printer settings page", () => Open($"http://{ip}:631/"));
        if (Has(554))
            Add(true, "Open the camera stream (RTSP)", () => Open($"rtsp://{ip}/"));
        var items = new List<object>(found);
        if (found.Count > 0 && others.Count > 0)
            items.Add(new Separator());
        items.AddRange(others);
        items.Add(new Separator());
        items.Add(Ui.Item("Send files with Courier", () => Main.ShowPage("portal")));
        items.Add(Ui.Item("Ping it in a terminal", () => Run("cmd.exe", $"/k ping -t {ip}")));
        return items;
    }

    private static void Open(string target)
    {
        if (Shell.Open(target) is { } error)
            Ui.Warn("Connect", Ui.T("Could not open it:\n{0}", error));
    }

    private static void Run(string program, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Ui.Warn("Connect", Ui.T("Could not open it:\n{0}", error.Message));
        }
    }

    private void Rename(LanDevice device)
    {
        var name = TextDialog.Ask(Window.GetWindow(this)!, "Name",
                                  Ui.T("A name for this device, shown instead of what the network calls it. Leave it empty to go back to that."),
                                  device.Name.Length > 0 ? device.Name : device.HostName);
        if (name is null)
            return;
        LanBook.Rename(device, name == device.HostName ? "" : name);
        ShowDevices();
    }

    private void Forget(LanDevice device)
    {
        if (!Ui.Ask("Forget this device", Ui.T("Forget {0}? It shows again when a scan finds it.", NameOf(device))))
            return;
        LanBook.Forget(device);
        devices.Remove(device);
        ShowDevices();
    }

    /// <summary>Everything known about a device, one "label: value" per line.</summary>
    private static List<(string Label, string Value)> Facts(LanDevice device)
    {
        var services = string.Join(", ", device.OpenPorts.Select(port =>
            $"{Ui.T(LanScanner.Ports.FirstOrDefault(entry => entry.Port == port).Meaning ?? "")} ({port})"));
        return
        [
            ("Name", NameOf(device)),
            ("Network name", device.HostName.Length > 0 ? device.HostName : "—"),
            ("IP address", device.Ip),
            ("MAC address", device.Mac.Length > 0 ? device.Mac : "—"),
            ("Maker", MakerOf(device)),
            ("Kind", KindOf(device)),
            ("Status", StatusOf(device)),
            ("Open services", services.Length > 0 ? services : Ui.T("None found")),
            ("First seen", device.FirstSeen > DateTime.MinValue ? device.FirstSeen.ToString("yyyy-MM-dd HH:mm") : "—"),
            ("Last seen", device.LastSeen > DateTime.MinValue ? device.LastSeen.ToString("yyyy-MM-dd HH:mm") : "—"),
        ];
    }

    private static string Details(LanDevice device) =>
        string.Join(Environment.NewLine, Facts(device).Select(fact => $"{Ui.T(fact.Label)}: {fact.Value}"));

    private void ShowInfo(LanDevice device) => LanInfoDialog.Show(Window.GetWindow(this)!, Facts(device), Details(device), device.RandomMac);

    public override bool CanClose()
    {
        running?.Cancel();
        return true;
    }

    /// <summary>A device's details, each one selectable, with Copy all.</summary>
    private sealed class LanInfoDialog : Dialog
    {
        private LanInfoDialog(Window owner, List<(string Label, string Value)> facts, string all, bool randomMac) : base(owner, "Device info")
        {
            var body = new StackPanel { Margin = new Thickness(18), MinWidth = 420, MaxWidth = 620 };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < facts.Count; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Ui.Muted(Ui.T(facts[index].Label)).Margin(0, 4, 16, 4);
                Grid.SetRow(label, index);
                grid.Children.Add(label);
                var value = new TextBox
                {
                    Text = facts[index].Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                    Foreground = Ui.Paint("Text"), Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                Grid.SetRow(value, index);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }
            body.Children.Add(grid);
            if (randomMac)
                body.Children.Add(Ui.Muted(Ui.T("This device made up its MAC address (phones and tablets do this on each network for privacy), so its maker can't be told from it."),
                                           12, wrap: true).Margin(0, 10, 0, 0));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(Ui.Button(Ui.T("Copy all"), () => Admin.Copy(all)));
            buttons.Children.Add(Ui.Button(Ui.T("Close"), Close, accent: true).Margin(6));
            body.Children.Add(buttons);
            Content = body;
        }

        public static void Show(Window owner, List<(string Label, string Value)> facts, string all, bool randomMac) =>
            new LanInfoDialog(owner, facts, all, randomMac).ShowDialog();

        protected override void Accept() => Close();
    }
}
