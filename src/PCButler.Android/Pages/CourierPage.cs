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
public sealed class CourierPage : ContentPage
{
    private readonly VerticalStackLayout running = new() { Spacing = 0 };
    private readonly Entry connectId = new() { Placeholder = "000 000 000", FontSize = 24, FontFamily = "monospace", Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.Center };
    // The status card: a round badge (a pulsing dot while waiting, a monitor once connected), two lines, a pill.
    private readonly Border statusBadge = new() { WidthRequest = 42, HeightRequest = 42, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(), VerticalOptions = LayoutOptions.Center };
    private readonly BoxView statusDot = new() { WidthRequest = 11, HeightRequest = 11, CornerRadius = 6, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Image statusMonitor = new() { Source = "ic_monitor.png", WidthRequest = 22, HeightRequest = 22, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Label statusText = new() { FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
    private readonly Label statusDetail = Theme.Faint("", 12.5);
    private readonly Border statusPill = Theme.Pill("", Theme.Amber, Theme.AmberSoft);
    private readonly Label idText = new() { FontSize = 27, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace", LineBreakMode = LineBreakMode.NoWrap, VerticalOptions = LayoutOptions.Center };
    private readonly Label idCaption = Theme.Caps("");
    private readonly Label idHelp = Theme.Faint("", 12.5);
    private readonly Label copyText = new() { FontSize = 13.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly Image copyIcon = new() { Source = "ic_copy.png", WidthRequest = 16, HeightRequest = 16, VerticalOptions = LayoutOptions.Center };
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
    private readonly Dictionary<PortalTransfer, (ProgressBar Bar, Label Detail, Button Cancel, Button Pause, Label Times)> rows = [];
    private readonly Label savedTitle = Theme.Title("", 18);
    private readonly VerticalStackLayout savedRows = new() { Spacing = 8 };
    private readonly VerticalStackLayout incomingBox = new() { Spacing = 10 };
    private readonly Button askButton, passwordRuleButton;
    private readonly Entry newPassword = new() { IsPassword = true, Placeholder = "••••", TextColor = Theme.Text };
    private readonly Button savePassword;
    private readonly Label passwordNote = Theme.Faint("", 12.5);
    private bool pickingPassword;
    private string asksShown = "", targetsShown = "", transfersShown = "\0", savedShown = "\0";
    private bool pulsing;

    public CourierPage()
    {
        Title = Tr.T("Courier");
        // --- this phone: where it stands, and its own ID ---
        var copy = new Border
        {
            BackgroundColor = Theme.SurfaceRaised, StrokeThickness = 0, Padding = new Thickness(14, 9), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout { Spacing = 6, Children = { copyIcon, copyText } },
        };
        copy.Tapped(CopyId);
        idText.Tapped(CopyId);
        copyText.Text = Tr.T("Copy");
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Theme.Line3(statusBadge, Theme.TwoLines(statusText, statusDetail), statusPill),
                idCaption.Margin(0, 14, 0, 0),
                Theme.Line3(null, idText, copy, 0),
                idHelp,
            },
        }));
        // --- connecting to a PC by its ID: it joins the list and stays connected ---
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Line3(Theme.Tile(("ic_monitor.png", Theme.PrimarySoft), 40), Theme.Title(Tr.T("Connect to a PC"), 19).Centered()),
                Theme.Faint(Tr.T("Type the ID the PC shows in its Courier. It joins your list and stays connected.")),
                Theme.BoxOf(connectId, 4), Theme.Accent(Tr.T("Connect"), () => _ = ConnectTyped()),
            },
        }));
        running.Children.Add(asks);
        sharedNote = Theme.BoxOf(Theme.Line3(new Image { Source = "ic_share.png", WidthRequest = 17, HeightRequest = 17, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) }, sharedText, null, 10),
                                 12, Theme.PrimarySoft);
        var send = Theme.Accent(Tr.T("Send files…"), () => _ = PickAndSend());
        sendCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Theme.Header(Tr.T("Send"), sendPill), sharedNote, waitingText, targets, send },
        });
        running.Children.Add(sendCard);
        transfersCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Header(Tr.T("Transfers"), transfersPill), transfers,
                Theme.Hint("ic_bell.png", Tr.T("Transfers keep going in the background — check the notification.")),
            },
        });
        running.Children.Add(transfersCard);

        // --- the PCs: every one connected to or paired with ---
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                savedTitle, Theme.Faint(Tr.T("Tap a PC to connect or disconnect it, rename it or remove it. Connected PCs stay connected until you disconnect them."), 12.5),
                savedRows,
            },
        }));

        // --- how new PCs get in ---
        askButton = Theme.Plain(Tr.T("Ask me"), () => SetRule(PortalSettings.Ask));
        passwordRuleButton = Theme.Plain(Tr.T("Password"), () => SetRule(PortalSettings.WithPassword));
        savePassword = Theme.Accent(Tr.T("Save password"), () => _ = SavePassword());
        var rules = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        rules.Add(askButton, 0);
        rules.Add(passwordRuleButton, 1);
        incomingBox.Children.Add(Theme.Caps(Tr.T("When a new PC connects").ToUpper(System.Globalization.CultureInfo.CurrentCulture)));
        incomingBox.Children.Add(rules);
        incomingBox.Children.Add(passwordNote);
        incomingBox.Children.Add(Theme.BoxOf(newPassword, 4));
        incomingBox.Children.Add(savePassword);

        // --- settings ---
        startStop = Theme.Plain("", () => _ = Toggle());
        storageButton = Theme.Plain(Tr.T("Allow"), async () => { await Access.AskToSaveToDownload(); Refresh(); });
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Header(Tr.T("Settings")),
                Theme.BoxOf(Theme.Line3(Theme.Tile(Theme.FolderTile, 36),
                                        Theme.TwoLines(new Label { Text = "Download/PC Butler Courier", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text },
                                                       Theme.Faint(Tr.T("Received files land here."), 12))), 10),
                incomingBox, storageText, storageButton, startStop,
            },
        }));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 22, 18, 24),
                Children = { AppPage.PageHeader("Courier", "Files between your phone and your PCs, across the internet."), running },
            },
        };
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(700), () =>
        {
            Refresh();
            SendShared();
            return true;
        });
        Courier.Changed += () => MainThread.BeginInvokeOnMainThread(Refresh);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
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
        var number = Portal.NormalizeNumber(connectId.Text ?? "");
        if (!Portal.IsNumber(number) || number == Courier.OwnNumber)
        {
            await DisplayAlert(Tr.T("Courier"), Tr.T(number == Courier.OwnNumber ? "That is this phone's own ID. Type the ID the PC shows in its Courier."
                                                                                  : "Type the ID exactly as the other PC shows it: nine digits, like 418 205 937."), Tr.T("OK"));
            return;
        }
        connectId.Text = "";
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
                var name = await DisplayActionSheet(Tr.T("Send to"), Tr.T("Cancel"), null, ready.Select(item => item.Name).ToArray());
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
        (startStop.BackgroundColor, startStop.TextColor) = node is null ? (Theme.Primary, Theme.PrimaryText) : (Theme.Secondary, Theme.Red);
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
        (statusText.Text, statusDetail.Text) = node is null ? (Tr.T("Courier is off"), Tr.T("Start it so PCs can reach this phone, and it can connect to them."))
            : connected ? (ready.Count == 1 ? Tr.T("Connected to {0}", ready[0].Name) : Tr.T("{0} PC(s) connected.", ready.Count), Tr.T("It stays on in the background."))
            : (Tr.T("Reachable by its ID"), Tr.T("No PC is connected yet."));
        statusDetail.IsVisible = statusDetail.Text.Length > 0;
        // The badge: a monitor once connected, a pulsing amber dot while waiting, a grey dot when off.
        statusBadge.Content = connected ? statusMonitor : statusDot;
        statusBadge.BackgroundColor = connected ? Theme.PrimarySoft : node is null ? Theme.Secondary : Theme.AmberSoft;
        statusDot.Color = node is null ? Theme.Muted : Theme.Amber;
        Pulse(node is not null && !connected);
        if (connected)
            Theme.SetPill(statusPill, Tr.T("Connected"), Theme.Primary, Theme.PrimarySoft);
        else if (node is null)
            Theme.SetPill(statusPill, Tr.T("Off"), Theme.Muted, Theme.Secondary);
        else
            Theme.SetPill(statusPill, Tr.T("Waiting"), Theme.Amber, Theme.AmberSoft);
        // The ID shows whether Courier runs or not: it is the phone's own, and stays the same.
        idCaption.Text = Tr.T("This phone's own ID:").TrimEnd(':').ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        idText.Text = Portal.ShowNumber(Courier.OwnNumber);
        idHelp.Text = Tr.T("Type it on your PCs to connect them to this phone. It stays the same.");
        idHelp.IsVisible = true;
    }

    /// <summary>The waiting dot breathes, so it is plain that Courier is still at work.</summary>
    private async void Pulse(bool on)
    {
        if (!on || pulsing)
        {
            if (!on)
                statusDot.Opacity = 1;
            pulsing = on && pulsing;
            return;
        }
        pulsing = true;
        while (pulsing && statusBadge.Content == statusDot && statusDot.Color == Theme.Amber)
        {
            await statusDot.FadeTo(0.35, 800);
            await statusDot.FadeTo(1, 800);
        }
        pulsing = false;
        statusDot.Opacity = 1;
    }

    /// <summary>The ID to the clipboard, and "Copied" on the button for a moment.</summary>
    private async void CopyId()
    {
        await Clipboard.SetTextAsync(idText.Text);
        (copyText.Text, copyText.TextColor, copyIcon.Source) = (Tr.T("Copied"), Theme.Primary, "ic_check.png");
        await Task.Delay(1600);
        (copyText.Text, copyText.TextColor, copyIcon.Source) = (Tr.T("Copy"), Theme.Text, "ic_copy.png");
    }

    private void ShowAsks(PortalNode? node)
    {
        // A PC that wants in is allowed or refused here (with a password set, the right one lets it in by itself).
        var pairings = node?.Pairings.Where(pairing => pairing is { Incoming: true, Code.Length: > 0 }).ToList() ?? [];
        var requests = node?.Requests ?? [];
        var passwords = node?.PasswordAsks ?? [];
        var key = string.Join("|", pairings.Select(pairing => pairing.PeerId + pairing.Code)) + "#" + string.Join("|", requests.Select(request => request.Id))
                  + "#" + string.Join("|", passwords.Select(item => item.Number + item.Ask));
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
        savedTitle.Text = items.Count == 0 ? Tr.T("Your PCs") : Tr.T("Your PCs ({0})", items.Count);
        var key = string.Join("|", items.Select(item => $"{item.Number}{item.Name}{item.Stay}{item.Words}")) + (node is null);
        if (key == savedShown)
            return;
        savedShown = key;
        savedRows.Children.Clear();
        if (items.Count == 0)
            savedRows.Children.Add(Theme.Faint(Tr.T("No PC yet. Connect to one by its ID above; PCs that connect to this phone are added here too.")));
        foreach (var item in items)
        {
            var on = item.State == PortalState.Connected;
            var name = new Label { Text = item.Name, FontSize = 14.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.TailTruncation };
            var line = Theme.TwoLines(name, Theme.Faint(Portal.ShowNumber(item.Number) + "  ·  " + item.Words, 12));
            View? pill = on ? Theme.Pill(Tr.T("Connected"), Theme.Primary, Theme.PrimarySoft)
                : item.State is PortalState.NotConnected ? null : Theme.Pill(Tr.T("Waiting"), Theme.Amber, Theme.AmberSoft);
            var row = Theme.BoxOf(Theme.Line3(Theme.Tile(("ic_monitor.png", on ? Theme.PrimarySoft : Theme.Secondary), 34), line, pill), 8);
            var chosen = item;
            row.Tapped(() => _ = Manage(chosen.Number, chosen.Name));
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
        var choices = new List<string>();
        if (saved is { Stay: true } || linked)
            choices.Add(disconnect);
        if (!(saved is { Stay: true } && node is not null) && node?.StateOf(number) != PortalState.Connected)
            choices.Add(connect);
        choices.Add(rename);
        if (peer is { Trusted: true })
            choices.Add(auto);
        var choice = await DisplayActionSheet(label, Tr.T("Cancel"), remove, choices.ToArray());
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
        (askButton.BackgroundColor, askButton.TextColor) = password ? (Theme.Secondary, Theme.Text) : (Theme.Primary, Theme.PrimaryText);
        (passwordRuleButton.BackgroundColor, passwordRuleButton.TextColor) = password ? (Theme.Primary, Theme.PrimaryText) : (Theme.Secondary, Theme.Text);
        passwordNote.Text = Tr.T(!password ? "You allow each new PC here." : settings.Password.Length > 0 && !pickingPassword ? "New PCs that give the password connect without your answer. Type a new one to change it."
                                                                     : "Type the password new PCs must give.");
        newPassword.IsVisible = savePassword.IsVisible = password;
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
            var state = target.Ready ? Theme.Pill(Tr.T("Connected"), Theme.Primary, Theme.PrimarySoft) : Theme.Pill(Tr.T("Off"), Theme.Muted, Theme.Secondary);
            var row = Theme.BoxOf(Theme.Line3(Theme.Tile(("ic_monitor.png", target.Ready ? Theme.PrimarySoft : Theme.Secondary), 34), name, state), 8);
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
        var key = string.Join("|", shown.Select(transfer => transfer.Id));
        if (key != transfersShown)
        {
            transfersShown = key;
            transfers.Children.Clear();
            foreach (var gone in rows.Keys.Where(transfer => !shown.Contains(transfer)).ToList())
                rows.Remove(gone);
            if (shown.Count == 0)
                transfers.Children.Add(Theme.Empty("ic_inbox.png", Tr.T("Nothing has been sent or received yet.")));
            foreach (var transfer in shown)
            {
                var bar = Theme.Bar(0);
                var detail = Theme.Faint("", 12);
                var cancel = Theme.Plain("✕", () => { transfer.Cancel(); Refresh(); });
                (cancel.Padding, cancel.BackgroundColor, cancel.TextColor, cancel.VerticalOptions) = (new Thickness(10, 4), Colors.Transparent, Theme.Muted, LayoutOptions.Center);
                var pause = Theme.Plain("", () => { Courier.TogglePause(transfer); Refresh(); });
                (pause.Padding, pause.BackgroundColor, pause.TextColor, pause.VerticalOptions) = (new Thickness(10, 4), Colors.Transparent, Theme.Primary, LayoutOptions.Center);
                var times = Theme.Faint("", 12);
                var name = new Label { Text = (transfer.Incoming ? "↓ " : "↑ ") + transfer.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
                var head = Theme.Line3(Theme.Tile(Theme.TileFor(transfer.Name), 36), Theme.TwoLines(name, detail), cancel);
                var foot = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                foot.Add(times.Margin(0, 0, 8, 0), 0);
                foot.Add(pause, 1);
                times.VerticalOptions = LayoutOptions.Center;
                transfers.Children.Add(Theme.BoxOf(new VerticalStackLayout { Spacing = 10, Children = { head, bar, foot } }, 12));
                rows[transfer] = (bar, detail, cancel, pause, times);
            }
        }
        foreach (var transfer in shown)
        {
            var (bar, detail, cancel, pause, times) = rows[transfer];
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

internal static class ViewExtensions
{
    public static T Margin<T>(this T view, double left, double top, double right, double bottom) where T : View
    {
        view.Margin = new Thickness(left, top, right, bottom);
        return view;
    }
}
