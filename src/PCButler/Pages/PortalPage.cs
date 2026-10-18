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
/// Courier: files between PCs on different networks, across the internet. One page, one way
/// of working: this PC has its own ID (always the same), any other PC connects to it by that
/// ID, and this PC connects to others by theirs. Every PC connected to or paired with is on
/// the list, where it can be connected, disconnected (on both sides) or removed (kicked out).
/// </summary>
public sealed class PortalPage : Page
{
    private readonly Button startButton;
    private readonly StackPanel asks = new();
    private readonly StackPanel status = new() { Margin = new Thickness(24, 18, 24, 0) };
    private readonly TextBox connectBox;
    private readonly CheckBox autoStartSwitch;
    private readonly CheckBox widgetSwitch;
    private readonly CheckBox clipboardSwitch;
    private readonly TextBlock firewallText = Ui.Muted("", 12);
    private readonly Button firewallButton;
    private readonly Border firewallRow;
    private readonly TextBlock folderText = Ui.Muted("", 12);
    private readonly ComboBox incomingBox = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button passwordButton;
    private readonly CardPanel pcCard;
    private readonly ListView pcList;
    private readonly Button connectButton, disconnectButton, askAgainButton;
    private readonly TextBlock queuedText = Ui.Text("", 13, "Primary");
    private readonly CardPanel transferCard;
    private readonly ListView transferList;
    private readonly ObservableCollection<Row> transferRows = [];
    private readonly Dictionary<PortalTransfer, (Row Row, long Done, DateTime At, double Speed)> shown = [];
    /// <summary>Files handed over from the drop zone or File Explorer, waiting for a PC to be chosen.</summary>
    private readonly List<string> queued = [];
    private readonly Dictionary<string, PortalState> passwordsAsked = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PortalNode? watched;
    private bool showing;
    private bool? firewallAllowed;
    private bool askingFirewall;

    private PortalSettings Settings => Main.Config.Portal;

    public PortalPage(MainWindow main) : base(main, "Courier", "Files between your PCs, across the internet.")
    {
        startButton = AddAction("Start Courier", Toggle, accent: true);
        var page = new StackPanel();

        // --- questions come first: they wait for an answer ---
        page.Children.Add(asks);

        // --- this PC: its ID, and connecting to another one ---
        var statusCard = new CardPanel("This PC", "\uE7F4") { Margin = new Thickness(0, 0, 6, 18) };
        connectBox = Ui.DigitsOnly(Ui.Field(width: 170, hint: "205937"));
        connectBox.FlowDirection = FlowDirection.LeftToRight;
        connectBox.FontSize = 16 * Ui.TextScale;
        connectBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                ConnectTyped();
        };
        // The box to connect stays put while the lines above it change, so typing in it is never interrupted.
        var connectPart = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        connectPart.Children.Add(Ui.Text(Ui.T("Connect to a PC"), 13, weight: FontWeights.Bold));
        connectPart.Children.Add(Ui.Muted(Ui.T("Type the ID the other PC shows in its Courier. It joins your list and stays connected."), 12, wrap: true).Margin(0, 4, 0, 8));
        connectPart.Children.Add(Ui.Row(8, connectBox, Ui.Button(Ui.T("Connect"), ConnectTyped, accent: true)));
        statusCard.SetBody(new StackPanel { Children = { status, connectPart } });

        // --- settings ---
        autoStartSwitch = Ui.Switch("", true, on => { if (!showing) Main.UpdateConfig(config => config.Portal.AutoStart = on); });
        widgetSwitch = Ui.Switch("", true, on =>
        {
            if (showing)
                return;
            Main.UpdateConfig(config => config.Portal.Widget = on);
            Courier.WidgetOn = on;
            App.Current.CourierWidgetChanged();
        });
        clipboardSwitch = Ui.Switch("", true, on =>
        {
            if (showing)
                return;
            Main.UpdateConfig(config => config.Portal.ShareClipboard = on);
            App.Current.ClipShare.Apply(on);
        });
        var rows = new StackPanel();
        incomingBox.Items.Add(new ComboBoxItem { Content = Ui.T("Ask me to allow it"), Tag = PortalSettings.Ask });
        incomingBox.Items.Add(new ComboBoxItem { Content = Ui.T("Ask for a password"), Tag = PortalSettings.WithPassword });
        incomingBox.SelectionChanged += (_, _) =>
        {
            if (!showing)
                ChangeIncoming();
        };
        passwordButton = Ui.Button(Ui.T("Password…"), () => SetPassword());
        passwordButton.VerticalAlignment = VerticalAlignment.Center;
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("When a new PC connects"), 13, weight: FontWeights.Bold),
                                        Ui.Muted(Ui.T("Allow each new PC yourself, or let in the ones that give the password."), 12), Ui.Row(6, incomingBox, passwordButton),
                                        first: true));
        rows.Children.Add(Ui.SettingRow(Ui.T("Start with PC Butler"), Ui.T("Reachable, and connected to your PCs, as soon as PC Butler runs, without a click."), autoStartSwitch));
        rows.Children.Add(Ui.SettingRow(Ui.T("Show the Courier widget"), Ui.T("A small window while PCs are connected: drop files on it to send them, and watch them go."),
                                        widgetSwitch));
        rows.Children.Add(Ui.SettingRow(Ui.T("Share the clipboard"), Ui.T("Text copied here goes to your connected PCs and phones, and text they send lands on this clipboard, ready for Ctrl+V."),
                                        clipboardSwitch));
        firewallButton = Ui.Button(Ui.T("Allow"), AllowFirewall);
        firewallRow = Ui.SettingRow(Ui.Text(Ui.T("Windows firewall"), 13, weight: FontWeights.Bold), firewallText, firewallButton);
        rows.Children.Add(firewallRow);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Received files go to"), 13, weight: FontWeights.Bold), folderText,
                                        Ui.Row(6, Ui.Button(Ui.T("Open"), OpenFolder), Ui.Button(Ui.T("Change…"), ChangeFolder))));
        var settingsCard = new CardPanel("Settings", "\uE713") { Margin = new Thickness(6, 0, 0, 18) };
        settingsCard.SetBody(rows);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        top.Children.Add(statusCard.At(0, 0));
        top.Children.Add(settingsCard.At(0, 1));
        page.Children.Add(top);

        // --- the PCs: every one connected to or paired with; drop files on a row to send them ---
        pcCard = new CardPanel("Your PCs", "\uE8AB") { Margin = new Thickness(0, 0, 0, 18) };
        connectButton = Ui.Button(Ui.T("Connect"), ConnectSelected, accent: true);
        pcCard.Controls.Children.Add(connectButton);
        disconnectButton = Ui.Button(Ui.T("Disconnect"), DisconnectSelected).Margin(6);
        pcCard.Controls.Children.Add(disconnectButton);
        askAgainButton = Ui.Button(Ui.T("Ask again"), AskAgain).Margin(6);
        pcCard.Controls.Children.Add(askAgainButton);
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Send files…"), SendFiles).Margin(6));
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Send folder…"), SendFolder).Margin(6));
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Send clipboard"), SendClipboard).Margin(6));
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Rename…"), RenameSelected).Margin(6));
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Ask before taking files: on/off"), ToggleAuto).Margin(6));
        pcCard.Controls.Children.Add(Ui.Button(Ui.T("Remove"), RemoveSelected).Margin(6));
        var footer = pcCard.AddFooter();
        footer.Children.Add(Ui.Muted(Ui.T("Connected PCs stay connected, also after a restart, until you press Disconnect. Remove kicks a PC out: it must ask, and be allowed, again."), 12));
        footer.Children.Add(queuedText.Dock(Dock.Right));
        pcList = Ui.List(("PC", 240, "PlainCell"), ("ID", 120), ("Status", 330), ("Files from it", 170), ("Address", 210));
        pcList.MinHeight = 120;
        pcList.SelectionMode = SelectionMode.Single;
        pcList.AllowDrop = true;
        pcList.SelectionChanged += (_, _) => ShowButtons();
        pcList.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                SendTo(ReadyPeer(Ui.RowAt(e.OriginalSource) ?? pcList.SelectedItem as Row), dropped);
        };
        pcList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left || Ui.RowAt(e.OriginalSource) is not { } row)
                return;
            if (ReadyPeer(row) is not null)
                SendFiles();
            else
                Connect((string)row.Tag!);
        };
        pcCard.SetBody(pcList);
        page.Children.Add(pcCard);

        // --- what is moving, and what has moved ---
        transferCard = new CardPanel("Transfers", "\uE896");
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Pause or resume selected"), PauseSelected));
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Cancel selected"), CancelSelected).Margin(6, 0, 0, 0));
        transferCard.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory).Margin(6));
        transferList = Ui.List(("Name", 320, "PlainCell"), ("Direction", 230), ("Progress", 200), ("Speed", 110), ("Time", 210), ("Status", 300));
        transferList.MinHeight = 160;
        transferList.ItemsSource = transferRows;
        transferCard.SetBody(transferList);
        page.Children.Add(transferCard);

        Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        timer.Tick += (_, _) => ShowTransfers();
        IsVisibleChanged += (_, _) => timer.IsEnabled = IsVisible;
        Courier.StateChanged += OnStateChanged;
        ShowAll();
    }

    public override void Shown() => ShowAll();

    private void OnChanged() => Dispatcher.BeginInvoke(ShowAll);

    private void OnStateChanged() => Dispatcher.BeginInvoke(ShowAll);

    /// <summary>Follow whichever node is running (started here, by the widget, or when PC Butler started).</summary>
    private void Watch()
    {
        var node = Courier.Node;
        if (node == watched)
            return;
        if (watched is not null)
            watched.Changed -= OnChanged;
        watched = node;
        if (node is not null)
            node.Changed += OnChanged;
    }

    /// <summary>Files from elsewhere in the program (the drop zone): sent at once if there is one PC to send to, otherwise kept until one is chosen.</summary>
    public void Queue(IEnumerable<string> paths)
    {
        queued.AddRange(paths.Where(path => File.Exists(path) || Directory.Exists(path)));
        var targets = Courier.Targets(Courier.Node).Where(target => target.Ready).ToList();
        if (targets.Count == 1)
            SendTo(targets[0].Tag, []);
        ShowAll();
    }

    // --- connecting ---

    private async Task StartNow()
    {
        startButton.IsEnabled = false;
        if (await Courier.Start() is { } problem)
            Ui.Warn("Courier", problem);
        startButton.IsEnabled = true;
        ShowAll();
    }

    private async void Toggle()
    {
        if (Courier.Node is not null)
        {
            await Courier.Stop();
            ShowAll();
            return;
        }
        await StartNow();
    }

    private void ConnectTyped()
    {
        var number = Portal.NormalizeNumber(connectBox.Text);
        if (!Portal.IsNumber(number) || number == Courier.OwnNumber)
        {
            Ui.Info("Courier", Ui.T(number == Courier.OwnNumber ? "That is this PC's own ID. Type the ID the other PC shows in its Courier."
                                                               : "Type the ID exactly as the other PC shows it: six digits, like 205 937."));
            connectBox.Focus();
            return;
        }
        connectBox.Text = "";
        Connect(number);
    }

    /// <summary>Connect to a PC, and stay connected to it: through the running Courier, or Courier starts for it.</summary>
    private void Connect(string number)
    {
        passwordsAsked.Remove(number);
        if (Courier.Node is { } node)
        {
            node.Connect(number);
        }
        else
        {
            Main.UpdateConfig(config =>
            {
                PortalSavedList.Add(config.Portal, number, "");
                PortalSavedList.Find(config.Portal, number)!.Stay = true;
            });
            _ = StartNow();
        }
        ShowAll();
    }

    private string? ChosenNumber()
    {
        if ((pcList.SelectedItem as Row)?.Tag is string number)
            return number;
        Ui.Info("Courier", Ui.T("Select a PC in the list first."));
        return null;
    }

    private string LabelOf(string number) =>
        Settings.Saved.FirstOrDefault(saved => saved.Number == number) is { } saved ? PortalSavedList.Label(saved) : Portal.ShowNumber(number);

    private void ConnectSelected()
    {
        if (ChosenNumber() is { } number)
            Connect(number);
    }

    private async void DisconnectSelected()
    {
        if (ChosenNumber() is not { } number)
            return;
        if (Courier.Node is { } node)
            await node.Disconnect(number);
        else
            Main.UpdateConfig(config => { if (PortalSavedList.Find(config.Portal, number) is { } saved) saved.Stay = false; });
        ShowAll();
    }

    /// <summary>Kick a PC out: disconnected on both sides, off the list, and no longer trusted.</summary>
    private async void RemoveSelected()
    {
        if (ChosenNumber() is not { } number)
            return;
        if (!Ui.Ask("Courier", Ui.T("Remove {0}? It is disconnected and taken off the list, and it will have to ask, and be allowed, again before files can go either way.",
                                    LabelOf(number))))
            return;
        if (Courier.Node is { } node)
            await node.Disconnect(number, forget: true);
        else
            Main.UpdateConfig(config => PortalSavedList.Remove(config.Portal, number));
        ShowAll();
    }

    private void RenameSelected()
    {
        if (ChosenNumber() is not { } number)
            return;
        var current = Settings.Saved.FirstOrDefault(saved => saved.Number == number)?.Name ?? "";
        if (TextDialog.Ask(Window.GetWindow(this), Ui.T("Rename"), Ui.T("A new name for {0}:", LabelOf(number)), current) is not { Length: > 0 } name)
            return;
        if (Courier.Node is { } node)
            node.ChangeSaved(settings => PortalSavedList.Rename(settings, number, name));
        else
            Main.UpdateConfig(config => PortalSavedList.Rename(config.Portal, number, name));
        ShowAll();
    }

    private void ToggleAuto()
    {
        if (Courier.Node is { } node && ChosenNumber() is { } number && node.PeerOf(number) is { Trusted: true } peer)
            node.SetAutoAccept(peer.Id, !peer.AutoAccept);
        else
            Ui.Info("Courier", Ui.T("Select a paired PC first."));
    }

    private async void AskAgain()
    {
        if (Courier.Node is { } node && ChosenNumber() is { } number && node.PeerOf(number) is { Online: true } peer
            && await node.Pair(peer) is null && node.StateOf(number) is not (PortalState.NeedsPassword or PortalState.WrongPassword))
            Ui.Warn("Courier", Ui.T("{0} did not answer. Check that Courier is running on it.", peer.Name));
        ShowAll();
    }

    // --- how new PCs get in ---

    private void ChangeIncoming()
    {
        var rule = (incomingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? PortalSettings.Ask;
        if (rule == PortalSettings.WithPassword && Settings.Password.Length == 0 && !SetPassword())
        {
            ShowSettings(); // no password: it stays on asking
            return;
        }
        Main.UpdateConfig(config => config.Portal.Incoming = rule);
        Courier.Node?.UseIncoming(rule, Settings.Password);
        ShowSettings();
    }

    /// <summary>Set the password new PCs give. Choosing one also switches to asking for it.</summary>
    private bool SetPassword()
    {
        var hash = "";
        var done = PasswordsDialog.Show(Window.GetWindow(this), Ui.T("Courier password"),
                                        Ui.T("New PCs that give this password connect without your answer. PCs already connected are not asked."),
                                        ["Password", "Type it again"], Ui.T("Save"), typed =>
                                        {
                                            if (typed[0].Length < 4)
                                                return Ui.T("Use at least 4 characters.");
                                            if (typed[0] != typed[1])
                                                return Ui.T("The two passwords are not the same.");
                                            hash = Portal.HashPassword(typed[0]);
                                            return null;
                                        });
        if (!done)
            return false;
        Main.UpdateConfig(config => (config.Portal.Password, config.Portal.Incoming) = (hash, PortalSettings.WithPassword));
        Courier.Node?.UseIncoming(PortalSettings.WithPassword, hash);
        ShowSettings();
        return true;
    }

    /// <summary>A PC this one connects to wants its password.</summary>
    private void AskPassword(string number)
    {
        if (Courier.Node is not { } node)
            return;
        var state = node.StateOf(number);
        if (state is not (PortalState.NeedsPassword or PortalState.WrongPassword))
            return;
        var name = node.PeerOf(number)?.Name ?? LabelOf(number);
        PasswordsDialog.Show(Window.GetWindow(this), Ui.T("Courier password"),
                             Ui.T(state == PortalState.WrongPassword ? "That password was not right. Type it again." : "{0} asks for a password.", name),
                             ["Password"], Ui.T("Connect"), typed =>
                             {
                                 if (typed[0].Length == 0)
                                     return Ui.T("Type the password first.");
                                 node.GivePassword(number, typed[0]);
                                 return null;
                             });
        ShowAll();
    }

    // --- showing ---

    private void ShowAll()
    {
        Watch();
        var node = Courier.Node;
        startButton.Content = Ui.T(node is null ? "Start Courier" : "Stop Courier");
        ShowStatus(node);
        ShowAsks(node);
        ShowSettings();
        ShowPcs(node);
        ShowTransfers();
    }

    /// <summary>The ID, big, with a button that copies it.</summary>
    private static UIElement IdLine(string number)
    {
        var text = new TextBox
        {
            Text = Portal.ShowNumber(number), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Ui.Paint("Primary"),
            FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = Ui.Button(Ui.T("Copy"), () =>
        {
            try
            {
                Clipboard.SetText(Portal.ShowNumber(number));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        });
        copy.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(14, text, copy);
    }

    private void Line(string text, string brush = "Muted") =>
        status.Children.Add(Ui.Text(text, 12.5, brush, wrap: true).Margin(0, 8, 0, 0));

    /// <summary>This PC's ID (the same every time), how well others get through to it, and the box to connect to another PC.</summary>
    private void ShowStatus(PortalNode? node)
    {
        status.Children.Clear();
        status.Children.Add(Ui.Row(10, Ui.Text("●", 12, node is null ? "Muted" : "Primary"),
                                   Ui.Text(Ui.T(node is null ? "Courier is off" : "Courier is on"), 18, weight: FontWeights.SemiBold)));
        status.Children.Add(Ui.Muted(Ui.T("This PC's ID: give it to your other PCs, so they can connect to this one. It stays the same."), 12, wrap: true).Margin(0, 12, 0, 0));
        status.Children.Add(IdLine(Courier.OwnNumber));
        if (node is null)
            Line(Ui.T("Start Courier so other PCs can reach this one, and it can connect to them."));
        else if (!node.MappingTried)
            Line(Ui.T("Asking the router to open a port for Courier…"));
        else if (node.Mapping is { } given)
            Line(Ui.T("The router forwards a port to this PC ({0}), so PCs on any connection, mobile data included, can reach it.", given.Method), "Primary");
        else if (node.PublicAddress6 is not null)
            Line(Ui.T("PCs with IPv6 connect straight away. Others need UPnP turned on in this PC's router, unless their own connection lets them through."));
        else
            Line(Ui.T("The router did not open a port. Most home connections still get through; turning on UPnP in the router makes every connection work."), "Amber");
    }

    /// <summary>What waits for an answer: pairings (the code to compare), passwords other PCs ask for, and files another PC wants to send.</summary>
    private void ShowAsks(PortalNode? node)
    {
        asks.Children.Clear();
        if (node is null)
            return;
        foreach (var pairing in node.Pairings.Where(pairing => pairing is { Incoming: true, Code.Length: > 0 }))
        {
            var code = new TextBlock
            {
                Text = pairing.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 34, FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0),
            };
            var allow = Ui.Button(Ui.T("Allow"), () => node.Confirm(pairing), accent: true);
            asks.Children.Add(Ask(Ui.T("{0} wants to connect to this PC", pairing.PeerName),
                                  Ui.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."),
                                  code, allow, Ui.Button(Ui.T("Refuse"), () => node.Refuse(pairing))));
        }
        foreach (var saved in Settings.Saved)
        {
            var state = node.StateOf(saved.Number);
            if (state is not (PortalState.NeedsPassword or PortalState.WrongPassword))
            {
                passwordsAsked.Remove(saved.Number);
                continue;
            }
            var number = saved.Number;
            var name = node.PeerOf(number)?.Name ?? PortalSavedList.Label(saved);
            asks.Children.Add(Ask(Ui.T("{0} asks for a password", name),
                                  Ui.T(state == PortalState.WrongPassword ? "That password was not right. Type it again." : "Type the password set on that PC for new PCs."),
                                  null, Ui.Button(Ui.T("Type the password…"), () => AskPassword(number), accent: true)));
            // Asked once by itself; after that, the button above asks again.
            if (passwordsAsked.GetValueOrDefault(number) != state)
            {
                passwordsAsked[number] = state;
                Dispatcher.BeginInvoke(() => AskPassword(number));
            }
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
        showing = true;
        autoStartSwitch.IsChecked = Settings.AutoStart;
        incomingBox.SelectedIndex = Settings.Incoming == PortalSettings.WithPassword ? 1 : 0;
        passwordButton.Visibility = Settings.Incoming == PortalSettings.WithPassword ? Visibility.Visible : Visibility.Collapsed;
        widgetSwitch.IsChecked = Settings.Widget;
        clipboardSwitch.IsChecked = Settings.ShareClipboard;
        showing = false;
        // Every PC takes connections it did not start, so the firewall must let them in.
        // Asking the firewall runs a Windows tool, which takes a moment: asked once in the background, then remembered.
        if (firewallAllowed is null && !askingFirewall)
        {
            askingFirewall = true;
            Task.Run(() => Portal.FirewallAllows()).ContinueWith(done => Dispatcher.BeginInvoke(() =>
            {
                (firewallAllowed, askingFirewall) = (done.Result, false);
                ShowSettings();
            }));
        }
        firewallRow.Visibility = firewallAllowed ?? true ? Visibility.Collapsed : Visibility.Visible;
        firewallText.Text = Ui.T("Windows may block the other PCs until PC Butler is allowed. Needed once; Windows asks for permission.");
        folderText.Text = Portal.ReceiveFolder(Settings);
        folderText.FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>Every PC on the list, with where it stands. It shows while Courier is off too, to connect, rename or remove.</summary>
    private void ShowPcs(PortalNode? node)
    {
        var chosen = (pcList.SelectedItem as Row)?.Tag as string;
        var rows = Settings.Saved.Where(saved => saved.Number != Courier.OwnNumber).Select(saved =>
        {
            var peer = node?.PeerOf(saved.Number);
            var state = node?.StateOf(saved.Number) ?? PortalState.NotConnected;
            var name = peer is { Name.Length: > 0 } && saved.Name.Length == 0 ? peer.Name : PortalSavedList.Label(saved);
            var paired = node is null ? Settings.Devices.Any(device => device.Id.Length >= 15 && Portal.NumberOf(device.Id) == saved.Number) : peer is { Trusted: true };
            var auto = node is null ? Settings.Devices.FirstOrDefault(device => device.Id.Length >= 15 && Portal.NumberOf(device.Id) == saved.Number)?.AutoAccept == true
                                    : peer is { AutoAccept: true };
            return new Row
            {
                Name = name, Tag = saved.Number, Dim = state != PortalState.Connected,
                Cells =
                [
                    Portal.ShowNumber(saved.Number),
                    node is null ? Ui.T(saved.Stay ? "Connects when Courier starts" : "Courier is off") : Courier.Describe(state, name),
                    Ui.T(!paired ? "Not paired yet" : auto ? "Taken at once" : "Asks first"),
                    peer?.Tunnel?.ToString() ?? "",
                ],
            };
        }).ToList();
        pcList.ItemsSource = rows;
        pcList.SelectedItem = rows.FirstOrDefault(row => (string?)row.Tag == chosen) ?? (rows.Count == 1 ? rows[0] : null);
        pcCard.Title = rows.Count == 0 ? Ui.T("Your PCs") : Ui.T("Your PCs ({0})", rows.Count);
        queuedText.Text = queued.Count > 0 ? Ui.T("{0} item(s) ready: choose a PC and press Send files", queued.Count) : "";
        ShowButtons();
    }

    /// <summary>Connect for a PC that is not connected, Disconnect for one that is (or is being looked for).</summary>
    private void ShowButtons()
    {
        var number = (pcList.SelectedItem as Row)?.Tag as string;
        var node = Courier.Node;
        var stays = number is not null && Settings.Saved.FirstOrDefault(saved => saved.Number == number)?.Stay == true;
        var state = number is null || node is null ? PortalState.NotConnected : node.StateOf(number);
        var linked = node is not null && number is not null && node.PeerOf(number) is { Online: true };
        disconnectButton.Visibility = stays || linked ? Visibility.Visible : Visibility.Collapsed;
        connectButton.Visibility = state == PortalState.Connected || (stays && node is not null) ? Visibility.Collapsed : Visibility.Visible;
        askAgainButton.Visibility = state is PortalState.Asking or PortalState.WaitingForThem ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The node's transfers with live progress on top, then the log of earlier ones.</summary>
    private void ShowTransfers()
    {
        var live = Courier.Node?.Transfers ?? [];
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
            var state = Courier.State(transfer);
            entry.Row.Cells =
            [
                Ui.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer),
                active ? $"{percent}%  ·  {Ui.FormatSize(done)} / {Ui.FormatSize(transfer.Size)}" : Ui.FormatSize(transfer.Size),
                active && !transfer.Paused && !transfer.PausedThere && entry.Speed > 0 ? Ui.FormatSize((long)entry.Speed) + "/s" : "",
                transfer.State is "active" or "done" ? Courier.Times(transfer) : "",
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

    /// <summary>The PC a row is, when it can take files right now.</summary>
    private static PortalPeer? ReadyPeer(Row? row) =>
        row?.Tag is string number && Courier.Targets(Courier.Node).FirstOrDefault(target => target.Ready && target.Tag.Number == number) is { } target ? target.Tag : null;

    /// <summary>Where the chosen row sends to (the only connected PC when none is chosen), or null with a word to the user.</summary>
    private PortalPeer? Chosen()
    {
        if (Courier.Node is null)
        {
            Ui.Info("Courier", Ui.T("Start Courier first."));
            return null;
        }
        var ready = Courier.Targets(Courier.Node).Where(target => target.Ready).ToList();
        if (pcList.SelectedItem is Row row)
        {
            if (ReadyPeer(row) is { } peer)
                return peer;
            Ui.Info("Courier", Ui.T("{0} is not connected right now.", row.Name));
            return null;
        }
        if (ready.Count == 1)
            return ready[0].Tag;
        Ui.Info("Courier", Ui.T(ready.Count == 0 ? "No PC is connected right now." : "Select a PC in the list first."));
        return null;
    }

    private void SendTo(PortalPeer? target, IEnumerable<string> paths)
    {
        queued.AddRange(paths);
        target ??= Chosen();
        if (target is null || Courier.Node is not { } node)
        {
            // Nowhere to send yet: they are kept until a PC is chosen.
            ShowAll();
            return;
        }
        var all = queued.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        queued.Clear();
        if (all.Count > 0 && Courier.Send(node, target, all) is null)
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
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to send to {0}", target.Name) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SendTo(target, dialog.FileNames);
    }

    /// <summary>What is copied on this PC, as text, onto the chosen PC's or phone's clipboard.</summary>
    private async void SendClipboard()
    {
        if (Chosen() is not { } target || Courier.Node is not { } node)
            return;
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            text = "";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Ui.Info("Courier", Ui.T("No text is copied right now."));
            return;
        }
        var problem = await node.SendClipboard(target, text);
        if (problem == PortalNode.OldVersion)
            problem = Ui.T("The other side has an older PC Butler: update it to share the clipboard.");
        Ui.Info("Courier", problem ?? Ui.T("The copied text is on {0}'s clipboard now.", target.Name));
    }

    private void SendFolder()
    {
        if (Chosen() is { } target && Ui.PickFolder(Ui.T("Folder to send to {0}", target.Name)) is { } folder)
            SendTo(target, [folder]);
    }

    /// <summary>Hold the selected transfers where they are, or let the held ones go on.</summary>
    private void PauseSelected()
    {
        var chosen = Ui.Selected(transferList).Select(row => row.Tag).OfType<PortalTransfer>().Where(transfer => transfer.State == "active").ToList();
        if (chosen.Count == 0)
        {
            Ui.Info("Courier", Ui.T("Select a transfer that is moving first."));
            return;
        }
        foreach (var transfer in chosen)
        {
            if (transfer.Paused)
                transfer.Resume();
            else
                transfer.Pause();
        }
        ShowTransfers();
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
        if (Courier.Node is not null)
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
            firewallAllowed = null; // asked again
            ShowSettings();
        }
    }

    public override void Closing()
    {
        Courier.StateChanged -= OnStateChanged;
        if (watched is not null)
            watched.Changed -= OnChanged;
    }
}
