// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Courier on the phone, one page and one way of working: the phone has its own ID (always
/// the same) that PCs connect to, and it connects to PCs by theirs. Every PC connected to or
/// paired with is on the list, where it can be connected, disconnected (on both sides) or
/// removed. Files shared to PC Butler from any app wait here and go to the PC with one tap;
/// what moves shows with a progress bar.
/// </summary>
public sealed class CourierPage : AppPage
{
    private readonly VerticalStackLayout running = new() { Spacing = 0 };
    private readonly IdPad connectId = new();
    private readonly Border statusPill = Theme.Pill("", Theme.Amber, Theme.AmberSoft);
    private readonly Label idText = new() { FontSize = 38, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace", CharacterSpacing = 2, LineBreakMode = LineBreakMode.NoWrap, VerticalOptions = LayoutOptions.Center };
    private readonly Label idCaption = Theme.Faint("", 12.5);
    private readonly Label idHelp = Theme.Faint("", 12.5);
    private readonly Border copyButton;
    private readonly Border idCard;
    private readonly Button startButton;
    private readonly VerticalStackLayout asks = new();
    private readonly Border sendPill = Theme.Pill("", Theme.Primary, Theme.PrimarySoft);
    private readonly Label sharedText = new() { FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Border sharedNote;
    private readonly Label waitingText = Theme.Faint("", 13.5);
    private readonly VerticalStackLayout targets = new() { Spacing = 8 };
    private readonly Border transfersPill = Theme.Pill("", Theme.Primary, Theme.PrimarySoft);
    private readonly VerticalStackLayout transfers = new() { Spacing = 8 };
    private readonly Border sendCard;
    private readonly Border transfersCard;
    private readonly Button startStop;
    private readonly Label storageText = Theme.Faint("");
    private readonly Button storageButton;
    private readonly Dictionary<PortalTransfer, (ProgressBar Bar, Label Detail, Border Cancel, Button Pause, Label Times, HorizontalStackLayout Actions)> rows = [];
    private readonly Label savedCount = Theme.Faint("", 12.5);
    private readonly VerticalStackLayout savedRows = new() { Spacing = 0 };
    private readonly VerticalStackLayout incomingBox = new() { Spacing = 10 };
    private readonly VerticalStackLayout settingsBox;
    private readonly Button askButton, passwordRuleButton;
    private readonly Entry newPassword = new() { IsPassword = true, Placeholder = "••••" };
    private readonly Button savePassword;
    private readonly Label passwordNote = Theme.Faint("", 12.5);
    private bool pickingPassword;
    private readonly HashSet<string> popped = [];
    private bool popping;
    private string asksShown = "", targetsShown = "", transfersShown = "\0", savedShown = "\0";
    private bool onScreen;

    public CourierPage() : base("Courier")
    {
        SetSub(statusPill);
        HeaderRight.Content = Theme.IconButton("i_settings_2_w.png", OpenSettings);

        // --- this phone's own ID, big, with a round copy button ---
        copyButton = Theme.IconButton("i_copy_m.png", CopyId, Theme.PrimarySoft, 48);
        idText.Tapped(CopyId);
        idCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 4,
            Children = { idCaption, Theme.Line3(null, idText, copyButton, 8), idHelp },
        }, 20);
        idCard.Margin = new Thickness(0);
        running.Children.Add(idCard);
        startButton = Theme.Accent(Tr.T("Start Courier"), () => _ = StartNow(), "i_power_k.png");
        startButton.Margin = new Thickness(0, 12, 0, 0);
        running.Children.Add(startButton);
        running.Children.Add(asks.Margin(0, 12, 0, 0));

        // --- what moves, and sending: only once a PC is connected ---
        transfersCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Theme.Header(Tr.T("Transfers"), transfersPill), transfers, Theme.Hint(Tr.T("Transfers keep going in the background — check the notification.")) },
        }, 16);
        running.Children.Add(transfersCard);
        sharedNote = Theme.BoxOf(Theme.Line3(Theme.Icon("i_share_2_m.png", 17), sharedText, null, 10), 12, Theme.PrimarySoft);
        var send = Theme.Accent(Tr.T("Send files…"), () => _ = PickAndSend(), "i_send_k.png");
        sendCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Theme.Header(Tr.T("Send"), sendPill), sharedNote, waitingText, targets, send },
        }, 16);
        running.Children.Add(sendCard);

        // --- connecting to a PC by its ID: one box, paste at its end, Connect next to it ---
        var connectButton = Theme.Accent(Tr.T("Connect"), () => _ = ConnectTyped());
        (connectButton.IsEnabled, connectButton.HeightRequest, connectButton.CornerRadius) = (false, 56, 18);
        connectId.Changed += () => connectButton.IsEnabled = Portal.IsNumber(connectId.Digits);
        connectId.Submitted += () => _ = ConnectTyped();
        // A real keyboard types into the box while this page is on the screen.
        KeyInput.Handler = key => onScreen && connectId.Key(key);
        var connect = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        connect.Add(connectId, 0);
        connect.Add(connectButton, 1);
        running.Children.Add(Theme.Section(Tr.T("Connect to a PC")));
        running.Children.Add(connect);
        running.Children.Add(Theme.Hint(Tr.T("Find the 6-digit ID in PC Butler on Windows.")));

        // --- the PCs: every one connected to or paired with ---
        running.Children.Add(Theme.Section(Tr.T("Your PCs"), savedCount));
        running.Children.Add(Theme.CardOf(savedRows, 0));

        // --- how new PCs get in, where files land, on and off: in the settings sheet ---
        askButton = Theme.Plain(Tr.T("Ask me"), () => SetRule(PortalSettings.Ask));
        passwordRuleButton = Theme.Plain(Tr.T("Password"), () => SetRule(PortalSettings.WithPassword));
        savePassword = Theme.Accent(Tr.T("Save password"), () => _ = SavePassword());
        var rules = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6 };
        rules.Add(askButton, 0);
        rules.Add(passwordRuleButton, 1);
        incomingBox.Children.Add(Theme.Caps(Tr.T("When a new PC connects")));
        incomingBox.Children.Add(Theme.BoxOf(rules, 4, Theme.SurfaceRaised));
        incomingBox.Children.Add(passwordNote);
        incomingBox.Children.Add(Theme.PasswordField(newPassword));
        incomingBox.Children.Add(savePassword);
        startStop = Theme.Danger("", () => _ = Toggle(), "i_power_r.png");
        storageButton = Theme.Tonal(Tr.T("Allow"), async () => { await Access.AskToSaveToDownload(); Refresh(); });
        var folder = Theme.BoxOf(Theme.Line3(Theme.Icon("i_folder_open_m.png", 20),
                                             Theme.TwoLines(new Label { Text = "Download/PC Butler Courier", FontSize = 14, FontAttributes = FontAttributes.Bold },
                                                            Theme.Faint(Tr.T("Received files land here."), 12)), null, 12), 12, Theme.SurfaceRaised);
        settingsBox = new VerticalStackLayout
        {
            Spacing = 14,
            Children = { Theme.Caps(Tr.T("Received files")), folder, incomingBox, storageText, storageButton, startStop },
        };

        Body.Children.Add(running);
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(700), () =>
        {
            Refresh();
            SendShared();
            return true;
        });
        Courier.Changed += () => MainThread.BeginInvokeOnMainThread(Refresh);
    }

    private void OpenSettings()
    {
        Refresh();
        OpenSheet(Tr.T("Courier settings"), settingsBox);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        onScreen = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        onScreen = true;
        await Access.AskForNotifications();
        var settings = Courier.Settings;
        // Once switched on (before 1.22: once a role was chosen), it starts with the app; files shared from another app need it running, whatever the setting says.
        if (settings.AutoStart && (settings.Used || settings.Role.Length > 0) || Courier.Waiting.Count > 0)
            await Courier.Start();
        Refresh();
        SendShared();
    }

    // --- connecting ---

    private async Task ConnectTyped()
    {
        var number = Portal.NormalizeNumber(connectId.Digits);
        if (!Portal.IsNumber(number) || number == Courier.OwnNumber)
        {
            await DisplayAlert(Tr.T("Courier"), Tr.T(number == Courier.OwnNumber ? "That is this phone's own ID. Type the ID the PC shows in its Courier."
                                                                                  : "Type the ID exactly as the other PC shows it: six digits, like 205 937."), Tr.T("OK"));
            return;
        }
        connectId.Clear();
        await Connect(number);
    }

    /// <summary>Connect to a PC and stay connected to it: through the running Courier, or Courier starts for it.</summary>
    private async Task Connect(string number)
    {
        if (Courier.Node is { } node)
        {
            node.Connect(number);
        }
        else
        {
            Courier.Update(settings =>
            {
                PortalSavedList.Add(settings, number, "");
                PortalSavedList.Find(settings, number)!.Stay = true;
            });
            await StartNow();
        }
        savedShown = "\0";
        Refresh();
    }

    private async Task StartNow()
    {
        if (await Courier.Start() is { } problem)
            await DisplayAlert(Tr.T("Courier"), problem, Tr.T("OK"));
        Refresh();
    }

    private async Task Toggle()
    {
        if (Courier.Node is not null)
            await Courier.Stop();
        else
            await StartNow();
    }

    // --- sending ---

    private async Task PickAndSend()
    {
        var picked = await FilePicker.Default.PickMultipleAsync();
        var paths = picked?.Where(file => file is not null).Select(file => file!.FullPath).Where(File.Exists).ToList() ?? [];
        if (paths.Count > 0)
            Courier.Queue(paths);
        SendWaiting(null);
    }

    /// <summary>
    /// Files shared from another app go by themselves: at once to the only PC connected, or
    /// as soon as one connects; with several connected, the user picks one, once.
    /// </summary>
    private void SendShared()
    {
        if (!Courier.SendWhenReady || Courier.Waiting.Count == 0 || Courier.Node is not { } node)
            return;
        var ready = Courier.Targets(node).Where(item => item.Ready).ToList();
        if (ready.Count == 0)
            return; // keep waiting: the PC may still be connecting
        Courier.SendWhenReady = false;
        SendWaiting(ready.Count == 1 ? ready[0].Tag : null);
    }

    /// <summary>The files waiting go to the PC chosen, or to the only one connected.</summary>
    private async void SendWaiting(PortalPeer? target)
    {
        if (Courier.Node is not { } node || Courier.Waiting.Count == 0)
            return;
        var ready = Courier.Targets(node).Where(item => item.Ready).ToList();
        if (target is null)
        {
            if (ready.Count == 0)
            {
                await DisplayAlert(Tr.T("Courier"), Tr.T("No PC is connected right now. The files wait here until one is."), Tr.T("OK"));
                return;
            }
            if (ready.Count > 1)
            {
                var name = await Choose(Tr.T("Send to"), ready.Select(item => new SheetChoice("i_monitor_w.png", item.Name)));
                target = ready.FirstOrDefault(item => item.Name == name)?.Tag;
                if (target is null)
                    return;
            }
            else
            {
                target = ready[0].Tag;
            }
        }
        if (Courier.Send(node, target, Courier.TakeWaiting()) is null)
            await DisplayAlert(Tr.T("Courier"), Tr.T("None of those files could be read."), Tr.T("OK"));
        Refresh();
    }

    // --- showing ---

    private void Refresh()
    {
        var settings = Courier.Settings;
        var node = Courier.Node;
        startStop.Text = Tr.T(node is null ? "Start Courier" : "Stop Courier");
        (startStop.BackgroundColor, startStop.TextColor) = node is null ? (Theme.Primary, Theme.PrimaryText) : (Theme.DangerBack, Theme.Red);
        var canSave = Access.CanSaveToDownload;
        storageText.Text = canSave ? "" : Tr.T("Allow PC Butler to save to Download, so received files can be kept.");
        storageText.IsVisible = storageButton.IsVisible = !canSave;
        // Sending and transfers mean nothing until a PC is connected: they show only then
        // (Send also while files shared from another app wait, so it is plain they will go).
        var connected = Courier.Targets(node).Any(target => target.Ready);
        sendCard.IsVisible = connected || Courier.Waiting.Count > 0;
        transfersCard.IsVisible = connected || node?.Transfers.Count > 0;
        ShowStatus(node);
        ShowAsks(node);
        ShowSaved(node, settings);
        ShowIncoming(settings);
        ShowTargets(node);
        ShowTransfers(node);
    }

    private void ShowStatus(PortalNode? node)
    {
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        var connected = ready.Count > 0;
        if (connected)
            Theme.SetPill(statusPill, ready.Count == 1 ? Tr.T("Connected to {0}", ready[0].Name) : Tr.T("{0} PC(s) connected.", ready.Count).TrimEnd('.'), Theme.Primary, Theme.PrimarySoft);
        else if (node is null)
            Theme.SetPill(statusPill, Tr.T("Stopped"), Theme.Muted, Theme.Secondary);
        else
            Theme.SetPill(statusPill, Tr.T("Waiting for a PC"), Theme.Amber, Theme.AmberSoft);
        // The ID shows whether Courier runs or not: it is the phone's own, and stays the same.
        idCaption.Text = Tr.T("This phone's ID").ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        idText.Text = Portal.ShowNumber(Courier.OwnNumber);
        idHelp.Text = Tr.T(node is null ? "Start Courier to be reachable" : "Enter this on your PC to connect");
        idCard.Opacity = node is null ? 0.6 : 1;
        startButton.IsVisible = node is null;
    }

    /// <summary>The ID to the clipboard, and a tick on the button for a moment.</summary>
    private async void CopyId()
    {
        await Clipboard.SetTextAsync(idText.Text);
        Theme.SetIcon(copyButton, "i_check_m.png");
        await Task.Delay(1600);
        Theme.SetIcon(copyButton, "i_copy_m.png");
    }

    private void ShowAsks(PortalNode? node)
    {
        // A PC that wants in is allowed or refused here (with a password set, the right one lets it in by itself).
        var pairings = node?.Pairings.Where(pairing => pairing is { Incoming: true, Code.Length: > 0 }).ToList() ?? [];
        var requests = node?.Requests ?? [];
        var passwords = node?.PasswordAsks ?? [];
        var key = string.Join("|", pairings.Select(pairing => pairing.PeerId + pairing.Code)) + "#" + string.Join("|", requests.Select(request => request.Id))
                  + "#" + string.Join("|", passwords.Select(item => item.Number + item.Ask));
        if (node is not null)
            PopUp(node);
        if (key == asksShown)
            return;
        asksShown = key;
        asks.Children.Clear();
        foreach (var pairing in pairings)
        {
            asks.Children.Add(Ask(Tr.T("{0} wants to connect to this phone", pairing.PeerName),
                                  Tr.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."),
                                  new Label { Text = pairing.Code, FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace" },
                                  Theme.Accent(Tr.T("Allow"), () => { node!.Confirm(pairing); Refresh(); }),
                                  Theme.Plain(Tr.T("Refuse"), () => { node!.Refuse(pairing); Refresh(); })));
        }
        foreach (var (number, password) in passwords)
        {
            var typed = new Entry { IsPassword = true, Placeholder = Tr.T("Password"), TextColor = Theme.Text };
            var name = node!.PeerOf(number)?.Name ?? Portal.ShowNumber(number);
            asks.Children.Add(Ask(Tr.T("{0} asks for a password", name),
                                  Tr.T(password == PortalPasswordAsk.Wrong ? "That password was not right. Type it again." : "Type the password set on that PC for new PCs."),
                                  Theme.BoxOf(typed, 4),
                                  Theme.Accent(Tr.T("Connect"), () =>
                                  {
                                      if ((typed.Text ?? "").Length > 0)
                                          node.GivePassword(number, typed.Text!);
                                      Refresh();
                                  }), null));
        }
        foreach (var request in requests)
        {
            asks.Children.Add(Ask(Tr.T("{0} wants to send {1} file(s)", request.PeerName, request.Files), $"{request.Name}  ·  {Size(request.Size)}", null,
                                  Theme.Accent(Tr.T("Accept"), () => { node!.Accept(request.Id); Refresh(); }),
                                  Theme.Plain(Tr.T("Refuse"), () => { node!.Refuse(request.Id); Refresh(); })));
        }
    }

    /// <summary>
    /// A PC that wants to connect, or to send files, is asked about in a pop-up, once each, one
    /// at a time. Back or a question settled elsewhere leaves the card above, where it can still be answered.
    /// </summary>
    private async void PopUp(PortalNode node)
    {
        if (popping)
            return;
        var pairing = node.Pairings.FirstOrDefault(item => item is { Incoming: true, Code.Length: > 0 } && !popped.Contains("pair " + item.PeerId + item.Code));
        var request = pairing is null ? node.Requests.FirstOrDefault(item => !popped.Contains("send " + item.Id)) : null;
        if (pairing is null && request is null)
            return;
        popping = true;
        try
        {
            var page = Shell.Current?.CurrentPage ?? this;
            if (pairing is not null)
            {
                popped.Add("pair " + pairing.PeerId + pairing.Code);
                var answer = await page.DisplayActionSheetAsync(
                    Tr.T("{0} wants to connect to this phone", pairing.PeerName) + "\n\n" + Tr.T("Code: {0}", pairing.Code) + "\n"
                    + Tr.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."),
                    null, null, Tr.T("Allow"), Tr.T("Refuse"));
                if (pairing.State == "waiting" && answer == Tr.T("Allow"))
                    node.Confirm(pairing);
                else if (pairing.State == "waiting" && answer == Tr.T("Refuse"))
                    node.Refuse(pairing);
            }
            else
            {
                popped.Add("send " + request!.Id);
                var answer = await page.DisplayActionSheetAsync(
                    Tr.T("{0} wants to send {1} file(s)", request.PeerName, request.Files) + "\n\n" + $"{request.Name}  ·  {Size(request.Size)}",
                    null, null, Tr.T("Accept"), Tr.T("Refuse"));
                if (node.Requests.Any(item => item.Id == request.Id))
                {
                    if (answer == Tr.T("Accept"))
                        node.Accept(request.Id);
                    else if (answer == Tr.T("Refuse"))
                        node.Refuse(request.Id);
                }
            }
        }
        finally
        {
            popping = false;
        }
        Refresh();
    }

    private static View Ask(string title, string detail, View? lead, Button yes, Button? no)
    {
        var body = new VerticalStackLayout { Spacing = 8, Children = { Theme.Title(title, 17), Theme.Faint(detail) } };
        if (lead is not null)
            body.Children.Add(lead);
        var buttons = new HorizontalStackLayout { Spacing = 10, Children = { yes } };
        if (no is not null)
            buttons.Children.Add(no);
        body.Children.Add(buttons);
        var card = Theme.CardOf(body);
        card.Stroke = Theme.Primary;
        return card;
    }

    // --- the PCs ---

    private void ShowSaved(PortalNode? node, PortalSettings settings)
    {
        var items = settings.Saved.Where(saved => saved.Number != Courier.OwnNumber).Select(saved =>
        {
            var state = node?.StateOf(saved.Number) ?? PortalState.NotConnected;
            var name = node?.PeerOf(saved.Number) is { Name.Length: > 0 } peer && saved.Name.Length == 0 ? peer.Name : PortalSavedList.Label(saved);
            var words = node is null ? Tr.T(saved.Stay ? "Connects when Courier starts" : "Courier is off") : Courier.Describe(state, name);
            return (saved.Number, Name: name, saved.Stay, State: state, Words: words);
        }).ToList();
        savedCount.Text = items.Count == 0 ? "" : items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var key = string.Join("|", items.Select(item => $"{item.Number}{item.Name}{item.Stay}{item.Words}")) + (node is null);
        if (key == savedShown)
            return;
        savedShown = key;
        savedRows.Children.Clear();
        if (items.Count == 0)
            savedRows.Children.Add(Theme.Empty("i_monitor_m.png", Tr.T("No PC yet"), Tr.T("No PC yet. Connect to one by its ID above; PCs that connect to this phone are added here too.")));
        foreach (var item in items)
        {
            var on = item.State == PortalState.Connected;
            var name = new Label { Text = item.Name, FontSize = 14.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.TailTruncation };
            var line = Theme.TwoLines(name, Theme.Faint(Portal.ShowNumber(item.Number) + "  ·  " + item.Words, 12));
            View? pill = on ? Theme.Pill(Tr.T("Connected"), Theme.Primary, Theme.PrimarySoft)
                : item.State is PortalState.NotConnected ? null : Theme.Pill(Tr.T("Waiting"), Theme.Amber, Theme.AmberSoft);
            var end = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
            if (pill is not null)
                end.Children.Add(pill);
            end.Children.Add(Theme.Icon("i_ellipsis_vertical_g.png", 18));
            var row = Theme.Line3(Theme.Tile(on ? ("i_monitor_m.png", Theme.PrimarySoft) : Theme.MonitorTile, 40), line, end);
            row.Padding = new Thickness(16, 12);
            var chosen = item;
            row.Tapped(() => _ = Manage(chosen.Number, chosen.Name));
            if (savedRows.Children.Count > 0)
                savedRows.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
            savedRows.Children.Add(row);
        }
    }

    /// <summary>A change to the list: through the running Courier when it runs, so what it shows follows; straight to the settings otherwise.</summary>
    private void ChangeSaved(Action<PortalSettings> change)
    {
        if (Courier.Node is { } node)
            node.ChangeSaved(change);
        else
            Courier.Update(change);
        savedShown = "\0";
        Refresh();
    }

    private async Task Manage(string number, string label)
    {
        var node = Courier.Node;
        var saved = PortalSavedList.Find(Courier.Settings, number);
        var linked = node?.PeerOf(number) is { Online: true };
        var peer = node?.PeerOf(number);
        string connect = Tr.T("Connect"), disconnect = Tr.T("Disconnect"), rename = Tr.T("Rename"), remove = Tr.T("Remove"),
               auto = Tr.T(peer is { AutoAccept: true } ? "Ask before taking its files" : "Take its files without asking");
        var choices = new List<SheetChoice>();
        if (saved is { Stay: true } || linked)
            choices.Add(new("i_power_w.png", disconnect));
        if (!(saved is { Stay: true } && node is not null) && node?.StateOf(number) != PortalState.Connected)
            choices.Add(new("i_power_m.png", connect));
        choices.Add(new("i_pencil_w.png", rename));
        if (peer is { Trusted: true })
            choices.Add(new("i_check_w.png", auto));
        choices.Add(new("i_trash_2_r.png", remove, Danger: true));
        var choice = await Choose(label, choices, Theme.MonitorTile);
        if (choice == connect)
        {
            await Connect(number);
        }
        else if (choice == disconnect)
        {
            if (node is not null)
                await node.Disconnect(number);
            else
                Courier.Update(settings => { if (PortalSavedList.Find(settings, number) is { } item) item.Stay = false; });
        }
        else if (choice == rename)
        {
            var name = await DisplayPromptAsync(rename, Tr.T("A new name for {0}:", label), Tr.T("Save"), Tr.T("Cancel"), initialValue: saved?.Name ?? "");
            if (!string.IsNullOrWhiteSpace(name))
                ChangeSaved(settings => PortalSavedList.Rename(settings, number, name));
        }
        else if (choice == auto && node is not null && peer is not null)
        {
            node.SetAutoAccept(peer.Id, !peer.AutoAccept);
        }
        else if (choice == remove && await DisplayAlert(Tr.T("Courier"),
                     Tr.T("Remove {0}? It is disconnected and taken off the list, and it will have to ask, and be allowed, again before files can go either way.", label),
                     remove, Tr.T("Cancel")))
        {
            if (node is not null)
                await node.Disconnect(number, forget: true);
            else
                Courier.Update(settings => PortalSavedList.Remove(settings, number));
        }
        savedShown = "\0";
        Refresh();
    }

    // --- how new PCs get in ---

    private void ShowIncoming(PortalSettings settings)
    {
        var password = settings.Incoming == PortalSettings.WithPassword || pickingPassword;
        (askButton.BackgroundColor, askButton.TextColor) = password ? (Colors.Transparent, Theme.Muted) : (Theme.Primary, Theme.PrimaryText);
        (passwordRuleButton.BackgroundColor, passwordRuleButton.TextColor) = password ? (Theme.Primary, Theme.PrimaryText) : (Colors.Transparent, Theme.Muted);
        passwordNote.Text = Tr.T(!password ? "You allow each new PC here." : settings.Password.Length > 0 && !pickingPassword ? "New PCs that give the password connect without your answer. Type a new one to change it."
                                                                     : "Type the password new PCs must give.");
        newPassword.IsVisible = savePassword.IsVisible = password;
        if (newPassword.Parent?.Parent is Border field)
            field.IsVisible = password;
    }

    private void SetRule(string rule)
    {
        // Asking for a password starts only once there is one: until then the box shows, and the phone still asks.
        pickingPassword = rule == PortalSettings.WithPassword && Courier.Settings.Password.Length == 0;
        if (!pickingPassword)
        {
            Courier.Update(saved => saved.Incoming = rule);
            Courier.Node?.UseIncoming(rule, Courier.Settings.Password);
        }
        ShowIncoming(Courier.Settings);
        if (pickingPassword)
            newPassword.Focus();
    }

    private async Task SavePassword()
    {
        var typed = newPassword.Text ?? "";
        if (typed.Length < 4)
        {
            await DisplayAlert(Tr.T("Courier"), Tr.T("Use at least 4 characters."), Tr.T("OK"));
            return;
        }
        var hash = await Task.Run(() => Portal.HashPassword(typed));
        Courier.Update(saved => (saved.Password, saved.Incoming) = (hash, PortalSettings.WithPassword));
        Courier.Node?.UseIncoming(PortalSettings.WithPassword, hash);
        (newPassword.Text, pickingPassword) = ("", false);
        ShowIncoming(Courier.Settings);
        await DisplayAlert(Tr.T("Courier"), Tr.T("The password is saved. New PCs that give it connect without your answer."), Tr.T("OK"));
    }

    private void ShowTargets(PortalNode? node)
    {
        var all = Courier.Targets(node);
        var waiting = Courier.Waiting.Count;
        // Files shared from another app show in a mint note; otherwise a faint line says what can be done.
        sharedText.Text = waiting > 0 && Courier.SendWhenReady ? Tr.T("{0} file(s) shared from another app go as soon as a PC is connected.", waiting)
                        : waiting > 0 ? Tr.T("{0} file(s) shared from another app are ready. Tap a PC to send them.", waiting) : "";
        sharedNote.IsVisible = waiting > 0;
        waitingText.Text = waiting > 0 ? "" : all.Count == 0 ? Tr.T("No PC is paired yet.") : Tr.T("Choose files to send, or share them to PC Butler from any app.");
        waitingText.IsVisible = waitingText.Text.Length > 0;
        var ready = all.Where(target => target.Ready).ToList();
        sendPill.IsVisible = ready.Count > 0;
        if (ready.Count > 0)
            Theme.SetPill(sendPill, ready.Count == 1 ? ready[0].Name : Tr.T("{0} PC(s) connected.", ready.Count).TrimEnd('.'), Theme.Primary, Theme.PrimarySoft);
        var key = string.Join("|", all.Select(target => $"{target.Name}{target.Ready}")) + waiting;
        if (key == targetsShown)
            return;
        targetsShown = key;
        targets.Children.Clear();
        foreach (var target in all)
        {
            var name = new Label { Text = target.Name, FontSize = 14.5, FontAttributes = FontAttributes.Bold, TextColor = target.Ready ? Theme.Text : Theme.Muted, VerticalOptions = LayoutOptions.Center };
            var state = target.Ready ? Theme.Pill(Tr.T("Connected"), Theme.Primary, Theme.PrimarySoft) : Theme.Pill(Tr.T("Off"), Theme.Muted, Theme.SurfaceRaised);
            var row = Theme.BoxOf(Theme.Line3(Theme.Tile(target.Ready ? ("i_monitor_m.png", Theme.PrimarySoft) : Theme.MonitorTile, 36), name, state), 10);
            if (target.Ready)
                row.Tapped(() =>
                {
                    if (Courier.Waiting.Count > 0)
                        SendWaiting(target.Tag);
                });
            targets.Children.Add(row);
        }
    }

    private void ShowTransfers(PortalNode? node)
    {
        var shown = node?.Transfers.Reverse().Take(6).ToList() ?? [];
        var moving = shown.Count(transfer => transfer.State is "active" or "waiting");
        transfersPill.IsVisible = moving > 0;
        if (moving > 0)
            Theme.SetPill(transfersPill, Tr.T("{0} in progress", moving), Theme.Primary, Theme.PrimarySoft);
        // Earlier transfers (also from before the app last started) follow, so their files can still be opened.
        var finished = shown.Where(transfer => transfer.State == "done").Select(transfer => (transfer.Name, transfer.Size)).ToList();
        var earlier = Portal.History().Where(item => !finished.Remove((item.Name, item.Size))).Take(20).ToList();
        var key = string.Join("|", shown.Select(transfer => transfer.Id)) + "#" + string.Join("|", earlier.Select(item => item.Time + item.Name));
        if (key != transfersShown)
        {
            transfersShown = key;
            transfers.Children.Clear();
            foreach (var gone in rows.Keys.Where(transfer => !shown.Contains(transfer)).ToList())
                rows.Remove(gone);
            if (shown.Count == 0 && earlier.Count == 0)
                transfers.Children.Add(Theme.Empty("i_inbox_m.png", Tr.T("Nothing has been sent or received yet.")));
            foreach (var transfer in shown)
            {
                var bar = Theme.Bar(0);
                var detail = Theme.Faint("", 12);
                var cancel = Theme.IconButton("i_x_w.png", () => { transfer.Cancel(); Refresh(); }, Theme.SurfaceRaised, 34);
                var pause = Theme.Link("", () => { Courier.TogglePause(transfer); Refresh(); });
                var times = Theme.Faint("", 12);
                var name = new Label { Text = (transfer.Incoming ? "↓ " : "↑ ") + transfer.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
                var head = Theme.Line3(Theme.Tile(Theme.TileFor(transfer.Name), 36), Theme.TwoLines(name, detail), cancel);
                var foot = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                foot.Add(times.Margin(0, 0, 8, 0), 0);
                foot.Add(pause, 1);
                times.VerticalOptions = LayoutOptions.Center;
                // What can be done with its file: open it, share it, or open the folder it is in.
                var actions = FileActions(() => FileOf(transfer), transfer.Name);
                var box = Theme.BoxOf(new VerticalStackLayout { Spacing = 10, Children = { head, bar, foot, actions } }, 14);
                box.Tapped(() =>
                {
                    if (transfer.State == "done") // while it moves, a tap is for its own buttons
                        _ = MoreFor(FileOf(transfer), transfer.Name);
                });
                transfers.Children.Add(box);
                rows[transfer] = (bar, detail, cancel, pause, times, actions);
            }
            if (earlier.Count > 0)
                transfers.Children.Add(Theme.Caps(Tr.T("Earlier")).Margin(4, 8, 0, 0));
            foreach (var item in earlier)
            {
                var name = new Label { Text = (item.Incoming ? "↓ " : "↑ ") + item.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
                var detail = Theme.Faint(Tr.T(item.Incoming ? "From {0}" : "To {0}", item.Device) + $"  ·  {Size(item.Size)}  ·  {item.Time}", 12);
                var path = Locate(item.Path, item.Name, item.Size, item.Incoming);
                var children = new VerticalStackLayout { Spacing = 10, Children = { Theme.Line3(Theme.Tile(Theme.TileFor(item.Name), 36), Theme.TwoLines(name, detail), null) } };
                if (path is not null)
                {
                    var actions = FileActions(() => path, item.Name);
                    actions.IsVisible = true;
                    children.Children.Add(actions);
                }
                var box = Theme.BoxOf(children, 14);
                box.Tapped(() => _ = MoreFor(path, item.Name));
                transfers.Children.Add(box);
            }
        }
        foreach (var transfer in shown)
        {
            var (bar, detail, cancel, pause, times, actions) = rows[transfer];
            actions.IsVisible = FileOf(transfer) is not null;
            cancel.IsVisible = transfer.State is "active" or "waiting"; // a finished one has nothing left to stop
            // Only what is moving can be held, and only this phone's own pause can be let go here.
            pause.IsVisible = transfer.State == "active";
            pause.Text = Tr.T(transfer.Paused ? "Resume" : "Pause");
            times.Text = transfer.State is "active" or "done" ? Courier.Times(transfer) : "";
            var held = Courier.Held(transfer);
            var speed = transfer.Speed;
            var part = transfer.Size > 0 ? (double)transfer.Done / transfer.Size : 1;
            bar.Progress = transfer.State == "done" ? 1 : part;
            var direction = Tr.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer);
            detail.Text = transfer.State switch
            {
                "waiting" => $"{direction}  ·  " + Tr.T(transfer.Incoming ? "Waiting for your answer" : "Waiting for the other PC to accept"),
                "done" => $"{direction}  ·  " + Tr.T("Done") + $"  ·  {Size(transfer.Size)}",
                "active" => $"{direction}  ·  {part * 100:0}%  ·  {Size(transfer.Done)} / {Size(transfer.Size)}" + (held.Length > 0 ? $"  ·  {held}" : speed > 0 ? $"  ·  {Size((long)speed)}/s" : ""),
                _ => $"{direction}  ·  " + Tr.T(transfer.State switch { "cancelled" => "Cancelled", "refused" => "Refused", _ => "Failed" })
                     + (transfer.Note.Length > 0 ? $": {transfer.Note}" : ""),
            };
        }
    }

    /// <summary>The Open, Share and Folder buttons under a transfer; hidden until there is a file.</summary>
    private HorizontalStackLayout FileActions(Func<string?> path, string name) => new()
    {
        Spacing = 2, IsVisible = false,
        Children =
        {
            Theme.Link(Tr.T("Open"), () => _ = OpenFile(path(), name)),
            Theme.Link(Tr.T("Share…"), () => _ = ShareFile(path(), name)),
            Theme.Link(Tr.T("Show in folder"), () => _ = ShowInFolder(path())),
        },
    };

    /// <summary>The file or folder a transfer is about on this phone: what arrived (once it is all here), or what was sent. null when it is not there.</summary>
    private static string? FileOf(PortalTransfer transfer) => transfer switch
    {
        { Incoming: true, State: not "done" } => null,
        _ => Locate(transfer.Path, transfer.Name, transfer.Size, transfer.Incoming),
    };

    /// <summary>
    /// Where a transfer's file is: its saved path, or, when that is gone or was never kept, a file
    /// of the same name (and size) in the Courier folder or in Download.
    /// </summary>
    private static string? Locate(string saved, string name, long size, bool incoming)
    {
        if (saved.Length > 0 && (File.Exists(saved) || Directory.Exists(saved)))
            return saved;
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var download = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        foreach (var folder in new[] { Courier.Node?.Folder ?? Portal.ReceiveFolder(Courier.Settings), download ?? "" }.Where(folder => folder.Length > 0))
        {
            try
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate) && (size <= 0 || new FileInfo(candidate).Length == size))
                    return candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>Tapping a transfer: everything that can be done with its file, in a sheet.</summary>
    private async Task MoreFor(string? path, string name)
    {
        if (path is null)
            return;
        var open = Tr.T("Open");
        var share = Tr.T("Share…");
        var folder = Tr.T("Show in folder");
        var copy = Tr.T("Copy full path");
        var choices = new List<SheetChoice>();
        if (File.Exists(path))
            choices.Add(new("i_external_link_m.png", open));
        choices.Add(new("i_share_2_m.png", share));
        choices.Add(new("i_folder_open_m.png", folder));
        choices.Add(new("i_copy_m.png", copy));
        var picked = await Choose(name, choices, Theme.TileFor(name));
        if (picked == open)
            await OpenFile(path, name);
        else if (picked == share)
            await ShareFile(path, name);
        else if (picked == folder)
            await ShowInFolder(path);
        else if (picked == copy)
            await Clipboard.Default.SetTextAsync(path);
    }

    /// <summary>The files a path holds: itself, or the files in it (at most 100) for a folder.</summary>
    private static List<string> FilesIn(string? path)
    {
        if (path is null)
            return [];
        if (File.Exists(path))
            return [path];
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(100).ToList() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task OpenFile(string? path, string name)
    {
        if (path is null || !File.Exists(path))
        {
            if (path is not null && Directory.Exists(path))
                await ShowInFolder(path); // several files arrive as a folder: that folder is opened
            else
                await DisplayAlert(Tr.T("Courier"), Tr.T("This file is not there any more."), Tr.T("OK"));
            return;
        }
        try
        {
            App.LeavingOnPurpose();
            await Launcher.Default.OpenAsync(new OpenFileRequest(name, new ReadOnlyFile(path)));
        }
        catch (Exception error)
        {
            Log.Warning($"A Courier file could not be opened: {error.Message}");
            await DisplayAlert(Tr.T("Courier"), Tr.T("No app on this phone can open this file."), Tr.T("OK"));
        }
    }

    private async Task ShareFile(string? path, string name)
    {
        var files = FilesIn(path);
        if (files.Count == 0)
        {
            await DisplayAlert(Tr.T("Courier"), Tr.T("This file is not there any more."), Tr.T("OK"));
            return;
        }
        try
        {
            App.LeavingOnPurpose();
            if (files.Count == 1)
                await Share.Default.RequestAsync(new ShareFileRequest(name, new ShareFile(files[0])));
            else
                await Share.Default.RequestAsync(new ShareMultipleFilesRequest(name, files.Select(file => new ShareFile(file)).ToList()));
        }
        catch (Exception error)
        {
            Log.Warning($"A Courier file could not be shared: {error.Message}");
            await DisplayAlert(Tr.T("Courier"), Tr.T("The file could not be shared."), Tr.T("OK"));
        }
    }

    /// <summary>
    /// Open the folder a file is in (a folder itself), in the phone's Files app. Only folders in the shared
    /// storage (Download and the like) can be shown; for others, and when no app takes it, the
    /// folder's path is shown instead.
    /// </summary>
    private async Task ShowInFolder(string? path)
    {
        if (path is null)
            return;
        var folder = Directory.Exists(path) ? path.TrimEnd('/') : Path.GetDirectoryName(path);
        if (folder is null)
            return;
        var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        if (folder.StartsWith(root, StringComparison.Ordinal))
        {
            var relative = folder[root.Length..].Trim('/');
            var uri = global::Android.Provider.DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + relative);
            foreach (var type in new[] { global::Android.Provider.DocumentsContract.Document.MimeTypeDir, "resource/folder" })
            {
                try
                {
                    var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
                    intent.SetDataAndType(uri, type);
                    intent.AddFlags(global::Android.Content.ActivityFlags.NewTask | global::Android.Content.ActivityFlags.GrantReadUriPermission);
                    App.LeavingOnPurpose();
                    global::Android.App.Application.Context.StartActivity(intent);
                    return;
                }
                catch (global::Android.Content.ActivityNotFoundException)
                {
                }
            }
        }
        await DisplayAlert(Tr.T("Show in folder"), Tr.T("The file is in:\n{0}", folder), Tr.T("OK"));
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.0} {units[unit]}";
    }
}
