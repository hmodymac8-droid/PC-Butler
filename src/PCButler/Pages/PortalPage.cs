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
/// Courier: files between PCs on different networks, across the internet. The first time,
/// this PC either shares (it becomes the host, with a short ID to give out) or connects to
/// another PC by its ID (it becomes a client): one click either way. After that Courier
/// starts with PC Butler, stays connected, and the widget takes dropped files.
/// </summary>
public sealed class PortalPage : Page
{
    private readonly Button startButton;
    private readonly StackPanel asks = new();
    private readonly Border welcome;
    private readonly TextBox welcomeId;
    private readonly Grid top;
    private readonly StackPanel status = new() { Margin = new Thickness(24, 18, 24, 18) };
    private readonly TextBox hostNumberBox;
    private readonly Border hostNumberRow;
    private readonly CheckBox autoStartSwitch;
    private readonly CheckBox widgetSwitch;
    private readonly TextBlock roleText = Ui.Muted("", 12);
    private readonly TextBlock firewallText = Ui.Muted("", 12);
    private readonly Button firewallButton;
    private readonly Border firewallRow;
    private readonly TextBlock folderText = Ui.Muted("", 12);
    private readonly CardPanel deviceCard;
    private readonly ListView deviceList;
    private readonly Button autoButton;
    private readonly Button forgetButton;
    private readonly Button pairButton;
    private readonly Button renameButton;
    private readonly ComboBox incomingBox = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button passwordButton;
    private readonly Border incomingRow;
    private readonly CardPanel savedCard;
    private readonly ListView savedList;
    private PortalPasswordAsk passwordAsked;
    private readonly TextBlock queuedText = Ui.Text("", 13, "Primary");
    private readonly CardPanel transferCard;
    private readonly ListView transferList;
    private readonly ObservableCollection<Row> transferRows = [];
    private readonly Dictionary<PortalTransfer, (Row Row, long Done, DateTime At, double Speed)> shown = [];
    /// <summary>Files handed over from the drop zone, waiting for a PC to be chosen.</summary>
    private readonly List<string> queued = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PortalNode? watched;
    private bool showing;
    private bool? firewallAllowed;
    private bool askingFirewall;

    private PortalSettings Settings => Main.Config.Portal;
    private bool IsHost => Settings.Role == PortalSettings.Host;
    private bool HasRole => Settings.Role is PortalSettings.Host or PortalSettings.Client;

    public PortalPage(MainWindow main) : base(main, "Courier", "Files between your PCs, across the internet.")
    {
        startButton = AddAction("Start Courier", Toggle, accent: true);
        var page = new StackPanel();

        // --- the first time: share from this PC, or connect to another one ---
        welcomeId = Ui.Field(width: 170, hint: "418 205 937");
        welcomeId.FlowDirection = FlowDirection.LeftToRight;
        welcomeId.FontSize = 16;
        welcomeId.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                BecomeClient();
        };
        welcome = Welcome();
        page.Children.Add(welcome);

        // --- questions come first: they wait for an answer ---
        page.Children.Add(asks);

        // --- this PC, beside its settings ---
        var statusCard = new CardPanel("This PC", "\uE7F4") { Margin = new Thickness(0, 0, 6, 18) };
        statusCard.SetBody(status);
        hostNumberBox = Ui.Field(width: 150, hint: "418 205 937");
        hostNumberBox.FlowDirection = FlowDirection.LeftToRight;
        hostNumberBox.LostFocus += (_, _) => SaveHostNumber(hostNumberBox.Text);
        hostNumberBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                SaveHostNumber(hostNumberBox.Text);
        };
        autoStartSwitch = Ui.Switch("", true, on => { if (!showing) Main.UpdateConfig(config => config.Portal.AutoStart = on); });
        widgetSwitch = Ui.Switch("", true, on =>
        {
            if (showing)
                return;
            Main.UpdateConfig(config => config.Portal.Widget = on);
            Courier.WidgetOn = on;
            App.Current.CourierWidgetChanged();
        });
        var rows = new StackPanel();
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Role"), 13, weight: FontWeights.Bold), roleText, Ui.Button(Ui.T("Change"), ChangeRole), first: true));
        hostNumberRow = Ui.SettingRow(Ui.Text(Ui.T("The other PC's ID"), 13, weight: FontWeights.Bold),
                                      Ui.Muted(Ui.T("The nine digits the host shows. Change it to connect to another PC."), 12), hostNumberBox);
        rows.Children.Add(hostNumberRow);
        incomingBox.Items.Add(new ComboBoxItem { Content = Ui.T("Ask me to allow it"), Tag = PortalSettings.Ask });
        incomingBox.Items.Add(new ComboBoxItem { Content = Ui.T("Ask for a password"), Tag = PortalSettings.WithPassword });
        incomingBox.SelectionChanged += (_, _) =>
        {
            if (!showing)
                ChangeIncoming();
        };
        passwordButton = Ui.Button(Ui.T("Password…"), () => SetPassword());
        passwordButton.VerticalAlignment = VerticalAlignment.Center;
        incomingRow = Ui.SettingRow(Ui.Text(Ui.T("When a new PC connects"), 13, weight: FontWeights.Bold),
                                    Ui.Muted(Ui.T("Allow each new PC yourself, or let in the ones that give the password."), 12), Ui.Row(6, incomingBox, passwordButton));
        rows.Children.Add(incomingRow);
        rows.Children.Add(Ui.SettingRow(Ui.T("Start with PC Butler"), Ui.T("Connected (or reachable) as soon as PC Butler runs, without a click."), autoStartSwitch));
        rows.Children.Add(Ui.SettingRow(Ui.T("Show the Courier widget"), Ui.T("A small window while PCs are connected: drop files on it to send them, and watch them go."),
                                        widgetSwitch));
        firewallButton = Ui.Button(Ui.T("Allow"), AllowFirewall);
        firewallRow = Ui.SettingRow(Ui.Text(Ui.T("Windows firewall"), 13, weight: FontWeights.Bold), firewallText, firewallButton);
        rows.Children.Add(firewallRow);
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Received files go to"), 13, weight: FontWeights.Bold), folderText,
                                        Ui.Row(6, Ui.Button(Ui.T("Open"), OpenFolder), Ui.Button(Ui.T("Change…"), ChangeFolder))));
        var settingsCard = new CardPanel("Settings", "\uE713") { Margin = new Thickness(6, 0, 0, 18) };
        settingsCard.SetBody(rows);
        top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        top.Children.Add(statusCard.At(0, 0));
        top.Children.Add(settingsCard.At(0, 1));
        page.Children.Add(top);

        // --- a client: the PCs it can connect to, kept for later; the favourite is connected to at start ---
        savedCard = new CardPanel("Saved PCs", "\uE728") { Margin = new Thickness(0, 0, 0, 18) };
        savedCard.Controls.Children.Add(Ui.Button(Ui.T("Connect"), ConnectSaved, accent: true));
        savedCard.Controls.Children.Add(Ui.Button(Ui.T("Add…"), AddSaved).Margin(6));
        savedCard.Controls.Children.Add(Ui.Button(Ui.T("Rename…"), RenameSaved).Margin(6));
        savedCard.Controls.Children.Add(Ui.Button(Ui.T("Favourite: on/off"), FavoriteSaved).Margin(6));
        savedCard.Controls.Children.Add(Ui.Button(Ui.T("Remove"), RemoveSaved).Margin(6));
        savedCard.AddFooter().Children.Add(Ui.Muted(Ui.T("The favourite is connected to by itself when PC Butler starts. Double-click a PC to connect to it."), 12));
        savedList = Ui.List(("PC", 260, "PlainCell"), ("ID", 130), ("Status", 230), ("Favourite", 120));
        savedList.MinHeight = 100;
        savedList.SelectionMode = SelectionMode.Single;
        savedList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                ConnectSaved();
        };
        savedCard.SetBody(savedList);
        page.Children.Add(savedCard);

        // --- the other PCs: drop files on a row, or double-click it, to send ---
        deviceCard = new CardPanel("Clients", "\uE8AB") { Margin = new Thickness(0, 0, 0, 18) };
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send files…"), SendFiles, accent: true));
        deviceCard.Controls.Children.Add(Ui.Button(Ui.T("Send folder…"), SendFolder).Margin(6));
        pairButton = Ui.Button(Ui.T("Ask again"), PairWithHost).Margin(6);
        deviceCard.Controls.Children.Add(pairButton);
        renameButton = Ui.Button(Ui.T("Rename…"), RenameDevice).Margin(6);
        deviceCard.Controls.Children.Add(renameButton);
        autoButton = Ui.Button(Ui.T("Ask before taking files: on/off"), ToggleAuto).Margin(6);
        deviceCard.Controls.Children.Add(autoButton);
        forgetButton = Ui.Button(Ui.T("Forget"), ForgetSelected).Margin(6);
        deviceCard.Controls.Children.Add(forgetButton);
        var footer = deviceCard.AddFooter();
        footer.Children.Add(Ui.Muted(Ui.T("Drop files on a PC to send them, or double-click it to choose them."), 12));
        footer.Children.Add(queuedText.Dock(Dock.Right));
        deviceList = Ui.List(("PC", 260, "PlainCell"), ("Status", 200), ("Files from it", 230), ("ID", 110), ("Address", 240));
        deviceList.MinHeight = 120;
        deviceList.SelectionMode = SelectionMode.Single;
        deviceList.AllowDrop = true;
        deviceList.Drop += (_, e) =>
        {
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

    // --- the first choice ---

    private Border Welcome()
    {
        Border Choice(string glyph, string title, string text, UIElement action)
        {
            var body = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
            body.Children.Add(Ui.Glyph(glyph, 26, "Primary"));
            body.Children.Add(Ui.Text(title, 18, weight: FontWeights.SemiBold).Margin(0, 12, 0, 0));
            body.Children.Add(Ui.Muted(text, 12.5, wrap: true).Margin(0, 6, 0, 16));
            body.Children.Add(action);
            var card = new Border { Child = body };
            Widgets.StyleCard(card);
            return card;
        }

        var share = Ui.Button(Ui.T("Share from this PC"), BecomeHost, accent: true);
        share.HorizontalAlignment = HorizontalAlignment.Left;
        var connect = Ui.Button(Ui.T("Connect"), BecomeClient, accent: true);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Choice("\uE774", Ui.T("Share from this PC"),
                                 Ui.T("This PC becomes the host. It gets a short ID: give it to the other PCs, on any network, so they can connect."), share).At(0, 0));
        grid.Children.Add(Choice("\uE8AB", Ui.T("Connect to a PC"),
                                 Ui.T("Type the ID the other PC shows in its Courier, and connect. Files then go either way."),
                                 Ui.Row(8, welcomeId, connect)).At(0, 2));
        return new Border { Child = grid };
    }

    private async void BecomeHost()
    {
        Main.UpdateConfig(config => { config.Portal.Role = PortalSettings.Host; config.Portal.Devices = []; });
        await StartNow();
    }

    private async void BecomeClient()
    {
        var number = Portal.NormalizeNumber(welcomeId.Text);
        if (!Portal.IsNumber(number))
        {
            Ui.Info("Courier", Ui.T("Type the ID exactly as the other PC shows it: nine digits, like 418 205 937."));
            welcomeId.Focus();
            return;
        }
        Main.UpdateConfig(config => { config.Portal.Role = PortalSettings.Client; config.Portal.HostNumber = number; config.Portal.Devices = []; });
        await StartNow();
    }

    private async Task StartNow()
    {
        startButton.IsEnabled = false;
        if (await Courier.Start() is { } problem)
            Ui.Warn("Courier", problem);
        startButton.IsEnabled = true;
        ShowAll();
    }

    private async void ChangeRole()
    {
        if (Settings.Devices.Count > 0 && !Ui.Ask("Courier", Ui.T("Changing how this PC uses Courier forgets the PCs it was paired with; they will have to be paired again. Change it?")))
            return;
        await Courier.Stop();
        Main.UpdateConfig(config => { config.Portal.Role = ""; config.Portal.Devices = []; });
        ShowAll();
    }

    private void SaveHostNumber(string typed)
    {
        var number = Portal.NormalizeNumber(typed);
        if (number == Settings.HostNumber || !Portal.IsNumber(number))
        {
            hostNumberBox.Text = Portal.ShowNumber(Settings.HostNumber);
            return;
        }
        Connect(number);
        hostNumberBox.Text = Portal.ShowNumber(number);
    }

    /// <summary>A client connects to another PC: the running Courier switches to it, or it is the one used at the next start.</summary>
    private void Connect(string number)
    {
        if (Courier.Node is { } node)
        {
            node.ConnectTo(number);
        }
        else
        {
            Main.UpdateConfig(config =>
            {
                PortalSavedList.Add(config.Portal, number, "");
                config.Portal.HostNumber = Portal.NormalizeNumber(number);
            });
            _ = StartNow();
        }
        ShowAll();
    }

    // --- the saved PCs ---

    private void ShowSaved(PortalNode? node)
    {
        if (savedCard.Visibility != Visibility.Visible)
            return;
        var chosen = (savedList.SelectedItem as Row)?.Tag as string;
        var rows = Settings.Saved.Select(saved =>
        {
            var current = saved.Number == Settings.HostNumber;
            var state = !current ? ""
                : node is null ? Ui.T("Connected to at start")
                : node.HostPeer is { Online: true, Trusted: true } && !node.WaitingForHost ? Ui.T("Connected")
                : node.HostPeer is { Online: true } ? Ui.T("Waiting for it to allow this PC")
                : Ui.T("Connecting…");
            return new Row
            {
                Name = PortalSavedList.Label(saved), Tag = saved.Number, Dim = !current,
                Cells = [Portal.ShowNumber(saved.Number), state, saved.Favorite ? "★ " + Ui.T("Favourite") : ""],
            };
        }).ToList();
        savedList.ItemsSource = rows;
        savedList.SelectedItem = rows.FirstOrDefault(row => (string?)row.Tag == chosen) ?? rows.FirstOrDefault(row => (string?)row.Tag == Settings.HostNumber);
    }

    private string? ChosenSaved()
    {
        if ((savedList.SelectedItem as Row)?.Tag is string number)
            return number;
        Ui.Info("Courier", Ui.T("Select a PC in the list first."));
        return null;
    }

    /// <summary>A change to the list: through the running Courier when it runs (so it is saved with what it knows), straight to the settings otherwise.</summary>
    private void ChangeSaved(Action<PortalSettings> change)
    {
        if (Courier.Node is { IsHost: false } node)
            node.ChangeSaved(change);
        else
            Main.UpdateConfig(config => change(config.Portal));
        ShowAll();
    }

    private void ConnectSaved()
    {
        if (ChosenSaved() is { } number)
            Connect(number);
    }

    private void AddSaved()
    {
        var owner = Window.GetWindow(this);
        if (TextDialog.Ask(owner, Ui.T("Add a PC"), Ui.T("The PC's ID: the nine digits it shows in its Courier.")) is not { Length: > 0 } typed)
            return;
        var number = Portal.NormalizeNumber(typed);
        if (!Portal.IsNumber(number) || number == Courier.Node?.Number)
        {
            Ui.Info("Courier", Ui.T("Type the ID exactly as the other PC shows it: nine digits, like 418 205 937."));
            return;
        }
        var name = TextDialog.Ask(owner, Ui.T("Add a PC"), Ui.T("A name for it, to tell it apart (you can leave it empty)."), "") ?? "";
        ChangeSaved(settings => PortalSavedList.Add(settings, number, name));
    }

    private void RenameSaved()
    {
        if (ChosenSaved() is not { } number || Settings.Saved.FirstOrDefault(saved => saved.Number == number) is not { } saved)
            return;
        if (TextDialog.Ask(Window.GetWindow(this), Ui.T("Rename"), Ui.T("A new name for {0}:", PortalSavedList.Label(saved)), saved.Name) is { Length: > 0 } name)
            ChangeSaved(settings => PortalSavedList.Rename(settings, number, name));
    }

    private void FavoriteSaved()
    {
        if (ChosenSaved() is { } number)
            ChangeSaved(settings => PortalSavedList.ToggleFavorite(settings, number));
    }

    private void RemoveSaved()
    {
        if (ChosenSaved() is not { } number || Settings.Saved.FirstOrDefault(saved => saved.Number == number) is not { } saved)
            return;
        if (!Ui.Ask("Courier", Ui.T("Remove {0} from the list? It will have to be paired again before files can go either way.", PortalSavedList.Label(saved))))
            return;
        var wasCurrent = number == Settings.HostNumber;
        ChangeSaved(settings => PortalSavedList.Remove(settings, number));
        if (wasCurrent && Courier.Node is { } node)
            node.SetHost(Settings.HostNumber);
    }

    // --- a host: how new PCs get in ---

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

    /// <summary>A client: the host wants its password.</summary>
    private void AskPassword()
    {
        if (Courier.Node is not { IsHost: false, PasswordWanted: not PortalPasswordAsk.None } node)
            return;
        var name = node.HostPeer?.Name ?? Portal.ShowNumber(Settings.HostNumber);
        PasswordsDialog.Show(Window.GetWindow(this), Ui.T("Courier password"),
                             Ui.T(node.PasswordWanted == PortalPasswordAsk.Wrong ? "That password was not right. Type it again." : "{0} asks for a password.", name),
                             ["Password"], Ui.T("Connect"), typed =>
                             {
                                 if (typed[0].Length == 0)
                                     return Ui.T("Type the password first.");
                                 node.GivePassword(typed[0]);
                                 return null;
                             });
        ShowAll();
    }

    /// <summary>A host: another name for a client.</summary>
    private void RenameDevice()
    {
        if (Courier.Node is not { } node || (deviceList.SelectedItem as Row)?.Tag is not PortalPeer { Trusted: true } peer)
        {
            Ui.Info("Courier", Ui.T("Select a paired PC first."));
            return;
        }
        if (TextDialog.Ask(Window.GetWindow(this), Ui.T("Rename"), Ui.T("A new name for {0}:", peer.Name), peer.Name) is { Length: > 0 } name)
            node.Rename(peer.Id, name);
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

    // --- showing ---

    private void ShowAll()
    {
        Watch();
        var node = Courier.Node;
        welcome.Visibility = HasRole ? Visibility.Collapsed : Visibility.Visible;
        top.Visibility = deviceCard.Visibility = HasRole ? Visibility.Visible : Visibility.Collapsed;
        savedCard.Visibility = HasRole && !IsHost ? Visibility.Visible : Visibility.Collapsed;
        startButton.Visibility = HasRole ? Visibility.Visible : Visibility.Collapsed;
        startButton.Content = Ui.T(node is null ? "Start Courier" : "Stop Courier");
        ShowStatus(node);
        ShowAsks(node);
        ShowSettings();
        ShowDevices(node);
        ShowSaved(node);
        ShowTransfers();
    }

    /// <summary>The ID, big, with a button that copies it.</summary>
    private UIElement IdLine(string number)
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

    private void ShowStatus(PortalNode? node)
    {
        status.Children.Clear();
        if (node is null)
        {
            status.Children.Add(Ui.Text(Ui.T("Courier is off"), 18, weight: FontWeights.SemiBold));
            Line(Ui.T(IsHost ? "Start it to let the other PCs connect to this one." : "Start it to connect to the other PC."));
            return;
        }
        status.Children.Add(Ui.Row(10, Ui.Text("●", 12, "Primary"),
                                   Ui.Text(Ui.T(node.IsHost ? "Sharing from this PC" : "Connected mode"), 18, weight: FontWeights.SemiBold)));
        if (node.IsHost)
        {
            status.Children.Add(Ui.Muted(Ui.T("Give this ID to the other PCs:"), 12).Margin(0, 12, 0, 0));
            status.Children.Add(IdLine(node.Number));
            ShowHostStatus(node);
        }
        else
        {
            ShowClientStatus(node);
            status.Children.Add(Ui.Muted(Ui.T("This PC's own ID: {0}", Portal.ShowNumber(node.Number)), 11.5).Margin(0, 12, 0, 0));
        }
    }

    /// <summary>The host: how well the other PCs can get through to it.</summary>
    private void ShowHostStatus(PortalNode node)
    {
        var connected = Courier.Targets(node).Count(target => target.Ready);
        Line(connected == 0 ? Ui.T("Waiting for the other PCs to connect.") : Ui.T("{0} PC(s) connected.", connected), connected == 0 ? "Muted" : "Primary");
        if (!node.MappingTried)
            Line(Ui.T("Asking the router to open a port for Courier…"));
        else if (node.Mapping is { } given)
            Line(Ui.T("The router forwards a port to this PC ({0}), so PCs on any connection, mobile data included, can reach it.", given.Method), "Primary");
        else if (node.PublicAddress6 is not null)
            Line(Ui.T("PCs with IPv6 connect straight away. Others need UPnP turned on in this PC's router, unless their own connection lets them through."));
        else
            Line(Ui.T("The router did not open a port. Most home connections still get through; turning on UPnP in the router makes every connection work."), "Amber");
    }

    /// <summary>A client: whether it has reached the other PC, and what is in the way if not.</summary>
    private void ShowClientStatus(PortalNode node)
    {
        var host = node.HostPeer;
        var number = Portal.ShowNumber(Settings.HostNumber);
        if (host is { Online: true, Trusted: true } && node.WaitingForHost)
            Line(Ui.T("Waiting for {0} to allow this PC. Its user is asked on that PC.", host.Name), "Amber");
        else if (host is { Online: true, Trusted: true })
            Line(Ui.T("Connected to {0}. Drop files on the widget, or on its row below, to send them.", host.Name), "Primary");
        else if (host is { Online: true } && node.PasswordWanted != PortalPasswordAsk.None)
            Line(Ui.T(node.PasswordWanted == PortalPasswordAsk.Wrong ? "That password was not right. Type it again." : "{0} asks for a password.", host.Name), "Amber");
        else if (host is { Online: true })
            Line(Ui.T("Asking {0} to let this PC in…", host.Name), "Amber");
        else if (node.Connecting)
            Line(Ui.T("Connecting to {0}…", number));
        else
        {
            Line(Ui.T(node.HostProblem switch
            {
                PortalConnectProblem.NoDirectory => "This PC could not reach the public directory that IDs are looked up in. This network may block it.",
                PortalConnectProblem.NotFound => "No PC is sharing under {0} right now. Check the ID, and that Courier runs on that PC.",
                PortalConnectProblem.NoPath => "{0} was found, but the two networks would not let a link through. Turning on UPnP in that PC's router fixes this in most cases.",
                PortalConnectProblem.BadNumber => "{0} is not the ID of another PC.",
                _ => "Connecting to {0}…",
            }, number), node.HostProblem == PortalConnectProblem.None ? "Muted" : "Amber");
            if (node.HostProblem != PortalConnectProblem.None)
                Line(Ui.T("It tries again by itself."));
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
            var allow = Ui.Button(Ui.T("Allow"), () => node.Confirm(pairing), accent: true);
            asks.Children.Add(Ask(Ui.T("{0} wants to connect to this PC", pairing.PeerName),
                                  Ui.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."),
                                  code, allow, Ui.Button(Ui.T("Refuse"), () => node.Refuse(pairing))));
        }
        if (!node.IsHost && node.PasswordWanted != PortalPasswordAsk.None)
        {
            var name = node.HostPeer?.Name ?? Portal.ShowNumber(Settings.HostNumber);
            asks.Children.Add(Ask(Ui.T("{0} asks for a password", name),
                                  Ui.T(node.PasswordWanted == PortalPasswordAsk.Wrong ? "That password was not right. Type it again." : "Type the password set on that PC for new PCs."),
                                  null, Ui.Button(Ui.T("Type the password…"), AskPassword, accent: true)));
            // Asked once by itself; after that, the button above asks again.
            if (passwordAsked != node.PasswordWanted)
            {
                passwordAsked = node.PasswordWanted;
                Dispatcher.BeginInvoke(AskPassword);
            }
        }
        else
        {
            passwordAsked = PortalPasswordAsk.None;
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
        roleText.Text = Ui.T(IsHost ? "This PC shares: the others connect to it." : "This PC connects to another PC.");
        hostNumberRow.Visibility = HasRole && !IsHost ? Visibility.Visible : Visibility.Collapsed;
        if (!hostNumberBox.IsKeyboardFocused)
            hostNumberBox.Text = Portal.ShowNumber(Settings.HostNumber);
        autoStartSwitch.IsChecked = Settings.AutoStart;
        incomingRow.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        incomingBox.SelectedIndex = Settings.Incoming == PortalSettings.WithPassword ? 1 : 0;
        passwordButton.Visibility = Settings.Incoming == PortalSettings.WithPassword ? Visibility.Visible : Visibility.Collapsed;
        widgetSwitch.IsChecked = Settings.Widget;
        showing = false;
        // Only the host takes connections it did not start, so only the host needs the firewall to let them in.
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
        var allowed = firewallAllowed ?? true;
        firewallRow.Visibility = IsHost && !allowed ? Visibility.Visible : Visibility.Collapsed;
        firewallText.Text = Ui.T("Windows may block the other PCs until PC Butler is allowed. Needed once; Windows asks for permission.");
        folderText.Text = Portal.ReceiveFolder(Settings);
        folderText.FlowDirection = FlowDirection.LeftToRight;
    }

    private void ShowDevices(PortalNode? node)
    {
        var chosen = (deviceList.SelectedItem as Row)?.Tag switch { PortalPeer peer => peer.Id, PortalClient client => client.Id, PortalDevice device => device.Id, _ => "" };
        deviceCard.Title = Ui.T(IsHost ? "Connected PCs" : "PCs you can send to");
        autoButton.Visibility = node?.IsHost == true ? Visibility.Visible : Visibility.Collapsed;
        forgetButton.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        pairButton.Visibility = !IsHost && node?.HostPeer is { Online: true } waiting && (!waiting.Trusted || node.WaitingForHost) ? Visibility.Visible : Visibility.Collapsed;
        renameButton.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        List<Row> rows;
        if (node is null)
        {
            // While Courier is off, the PCs paired before are still listed, so they can be forgotten.
            rows = Settings.Devices.Select(device => new Row
            {
                Name = device.Name, Tag = device, Dim = true,
                Cells = [Ui.T("Courier is off"), "", Portal.ShowNumber(Portal.NumberOf(device.Id)), ""],
            }).ToList();
        }
        else if (node.IsHost)
        {
            rows = node.Peers.Where(peer => peer.Trusted).Select(peer => new Row
            {
                Name = peer.Name.Length > 0 ? peer.Name : "PC", Tag = peer, Dim = !peer.Online,
                Cells =
                [
                    Ui.T(peer.Online ? "Connected" : "Not connected"), Ui.T(peer.AutoAccept ? "Taken at once" : "Asks first"),
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
                        Ui.T(host.Online ? "Connected" : "Not connected"), Ui.T(host.Trusted ? "Taken at once" : "Not paired yet"),
                        Portal.ShowNumber(host.Number), host.Tunnel?.ToString() ?? "",
                    ],
                });
            }
            rows.AddRange(node.OtherClients.Select(client => new Row
            {
                Name = client.Name, Tag = client, Dim = !client.Online,
                Cells = [Ui.T(client.Online ? "Connected, through the host" : "Not connected"), "", Portal.ShowNumber(Portal.NumberOf(client.Id)), ""],
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
        transferCard.Visibility = HasRole || transferRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- sending ---

    /// <summary>Where the chosen row sends to (the only connected PC when none is chosen), or null with a word to the user.</summary>
    private object? Chosen()
    {
        if (Courier.Node is not { } node)
        {
            Ui.Info("Courier", Ui.T("Start Courier first."));
            return null;
        }
        var targets = Courier.Targets(node);
        var ready = targets.Where(target => target.Ready).ToList();
        var tag = (deviceList.SelectedItem as Row)?.Tag ?? (ready.Count == 1 ? ready[0].Tag : null);
        var target = targets.FirstOrDefault(item => ReferenceEquals(item.Tag, tag));
        if (target is null)
            Ui.Info("Courier", Ui.T(ready.Count == 0 ? "No PC is connected right now." : "Select a PC in the list first."));
        else if (!target.Ready)
            Ui.Info("Courier", Ui.T("{0} is not connected right now.", target.Name));
        else
            return target.Tag;
        return null;
    }

    private void SendTo(object? target, IEnumerable<string> paths)
    {
        queued.AddRange(paths);
        target = target is not null && Courier.Targets(Courier.Node).Any(item => ReferenceEquals(item.Tag, target) && item.Ready) ? target : Chosen();
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
        if (Courier.Node is { HostPeer: { Online: true } host } node && await node.Pair(host) is null && node.PasswordWanted == PortalPasswordAsk.None)
            Ui.Warn("Courier", Ui.T("{0} did not answer. Check that Courier is running on it.", host.Name));
        ShowAll();
    }

    private void ToggleAuto()
    {
        if (Courier.Node is { } node && (deviceList.SelectedItem as Row)?.Tag is PortalPeer { Trusted: true } peer)
            node.SetAutoAccept(peer.Id, !peer.AutoAccept);
        else
            Ui.Info("Courier", Ui.T("Select a connected PC first."));
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
        if (Courier.Node is { } node)
            node.Forget(id);
        else
            Main.UpdateConfig(config => config.Portal.Devices.RemoveAll(device => device.Id == id));
        ShowAll();
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
