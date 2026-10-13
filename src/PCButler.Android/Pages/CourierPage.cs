// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Courier on the phone: connect to a PC by its ID (or share from the phone), pair once,
/// then send files either way. Files shared to PC Butler from any app wait here and go to
/// the PC with one tap; what moves shows with a progress bar.
/// </summary>
public sealed class CourierPage : ContentPage
{
    private readonly VerticalStackLayout welcome = new() { Spacing = 0 };
    private readonly VerticalStackLayout running = new() { Spacing = 0 };
    private readonly Entry welcomeId = new() { Placeholder = "K7M-2QX", FontSize = 20, Keyboard = Keyboard.Text, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label statusDot = new() { Text = "●", FontSize = 14, VerticalOptions = LayoutOptions.Center };
    private readonly Label statusText = Theme.Body("", 16);
    private readonly Label statusDetail = Theme.Faint("");
    private readonly Label idText = new() { FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace" };
    private readonly Label idCaption = Theme.Faint("");
    private readonly VerticalStackLayout asks = new();
    private readonly Label waitingText = Theme.Body("");
    private readonly VerticalStackLayout targets = new() { Spacing = 8 };
    private readonly VerticalStackLayout transfers = new() { Spacing = 14 };
    private readonly Button startStop;
    private readonly Label storageText = Theme.Faint("");
    private readonly Button storageButton;
    private readonly Dictionary<PortalTransfer, (ProgressBar Bar, Label Detail, long Done, DateTime At, double Speed)> rows = [];
    private string asksShown = "", targetsShown = "", transfersShown = "";

    public CourierPage()
    {
        Title = Tr.T("Courier");
        // --- the first time: connect to a PC, or share from this phone ---
        var connect = Theme.Accent(Tr.T("Connect"), () => _ = BecomeClient());
        welcome.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Title(Tr.T("Connect to a PC"), 20),
                Theme.Faint(Tr.T("Type the ID the PC shows in its Courier, and connect. Files then go either way.")),
                welcomeId, connect,
            },
        }));
        welcome.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Title(Tr.T("Share from this phone"), 20),
                Theme.Faint(Tr.T("This phone becomes the host. It gets a short ID: give it to the PCs, so they can connect to it.")),
                Theme.Plain(Tr.T("Share from this phone"), () => _ = BecomeHost()),
            },
        }));

        // --- running: who is connected, questions, sending, what moves ---
        var copy = Theme.Plain(Tr.T("Copy"), () => _ = Clipboard.SetTextAsync(idText.Text));
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new HorizontalStackLayout { Spacing = 10, Children = { statusDot, statusText } },
                statusDetail,
                idCaption,
                new HorizontalStackLayout { Spacing = 14, Children = { idText, copy } },
            },
        }));
        running.Children.Add(asks);
        var send = Theme.Accent(Tr.T("Send files…"), () => _ = PickAndSend());
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children = { Theme.Title(Tr.T("Send"), 18), waitingText, targets, send },
        }));
        running.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 10, Children = { Theme.Title(Tr.T("Transfers"), 18), transfers } }));

        // --- settings ---
        startStop = Theme.Plain("", () => _ = Toggle());
        storageButton = Theme.Plain(Tr.T("Allow"), Access.AskForAllFiles);
        var changeRole = Theme.Plain(Tr.T("Change"), () => _ = ChangeRole());
        running.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Title(Tr.T("Settings"), 18),
                Theme.Faint(Tr.T("Received files go to the Download folder, in “PC Butler Courier”.")),
                storageText, storageButton,
                new HorizontalStackLayout { Spacing = 10, Children = { startStop, changeRole } },
            },
        }));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 24, 18, 24),
                Children = { Theme.Title(Tr.T("Courier"), 28), Theme.Faint(Tr.T("Files between your phone and your PCs, across the internet.")).Margin(0, 4, 0, 18), welcome, running },
            },
        };
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(700), () =>
        {
            Refresh();
            return true;
        });
        Courier.Changed += () => MainThread.BeginInvokeOnMainThread(Refresh);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Access.AskForNotifications();
        var settings = Courier.Settings;
        if (settings.AutoStart && settings.Role is PortalSettings.Host or PortalSettings.Client)
            await Courier.Start();
        Refresh();
    }

    // --- choices ---

    private async Task BecomeClient()
    {
        var number = Portal.NormalizeNumber(welcomeId.Text ?? "");
        if (!Portal.IsNumber(number))
        {
            await DisplayAlert(Tr.T("Courier"), Tr.T("Type the ID exactly as the other PC shows it: six letters and digits, like K7M-2QX."), Tr.T("OK"));
            return;
        }
        Courier.Update(settings => { settings.Role = PortalSettings.Client; settings.HostNumber = number; settings.Devices = []; });
        await StartNow();
    }

    private async Task BecomeHost()
    {
        Courier.Update(settings => { settings.Role = PortalSettings.Host; settings.Devices = []; });
        await StartNow();
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

    private async Task ChangeRole()
    {
        if (!await DisplayAlert(Tr.T("Courier"), Tr.T("Changing how this phone uses Courier forgets the PCs it was paired with; they will have to be paired again. Change it?"),
                                Tr.T("Change"), Tr.T("Cancel")))
            return;
        await Courier.Stop();
        Courier.Update(settings => { settings.Role = ""; settings.Devices = []; });
        Refresh();
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

    /// <summary>The files waiting go to the PC chosen, or to the only one connected.</summary>
    private async void SendWaiting(object? target)
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
        var hasRole = settings.Role is PortalSettings.Host or PortalSettings.Client;
        welcome.IsVisible = !hasRole;
        running.IsVisible = hasRole;
        startStop.Text = Tr.T(node is null ? "Start Courier" : "Stop Courier");
        var allFiles = Access.HasAllFiles;
        storageText.Text = Tr.T(allFiles ? "PC Butler can save received files and look through the phone's files."
                                         : "Allow “All files access” so received files can be saved to Download, and the Sorter, Duplicates and Storage can look through the phone.");
        storageButton.IsVisible = !allFiles;
        if (!hasRole)
            return;
        ShowStatus(node, settings);
        ShowAsks(node);
        ShowTargets(node);
        ShowTransfers(node);
    }

    private void ShowStatus(PortalNode? node, PortalSettings settings)
    {
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        if (node is null)
        {
            (statusText.Text, statusDot.TextColor, statusDetail.Text) = (Tr.T("Courier is off"), Theme.Muted, "");
        }
        else if (node.IsHost)
        {
            statusText.Text = ready.Count == 0 ? Tr.T("Waiting for the other PCs to connect.") : Tr.T("{0} PC(s) connected.", ready.Count);
            statusDot.TextColor = ready.Count > 0 ? Theme.Primary : Theme.Amber;
            statusDetail.Text = Tr.T("Keep the app open, or let it run in the background: the notification shows it is still on.");
        }
        else
        {
            var host = node.HostPeer;
            statusText.Text = host is { Online: true, Trusted: true } ? Tr.T("Connected to {0}", host.Name)
                : host is { Online: true } ? Tr.T("Connected to {0}. Compare the code shown on both to pair them.", host.Name)
                : node.Connecting ? Tr.T("Connecting to {0}…", Portal.ShowNumber(settings.HostNumber))
                : Tr.T(node.HostProblem switch
                {
                    PortalConnectProblem.NoDirectory => "This network blocks the public directory that IDs are looked up in.",
                    PortalConnectProblem.NotFound => "No PC is sharing under {0} right now. Check the ID, and that Courier runs on that PC.",
                    PortalConnectProblem.NoPath => "{0} was found, but the two networks would not let a link through.",
                    _ => "Connecting to {0}…",
                }, Portal.ShowNumber(settings.HostNumber));
            statusDot.TextColor = host is { Online: true, Trusted: true } ? Theme.Primary : Theme.Amber;
            statusDetail.Text = node.HostProblem is PortalConnectProblem.None ? "" : Tr.T("It tries again by itself.");
        }
        idCaption.Text = node?.IsHost == true ? Tr.T("Give this ID to the PCs:") : Tr.T("This phone's own ID:");
        idText.Text = node is null ? "—" : Portal.ShowNumber(node.Number);
    }

    private void ShowAsks(PortalNode? node)
    {
        var pairings = node?.Pairings.Where(pairing => pairing.Code.Length > 0).ToList() ?? [];
        var requests = node?.Requests ?? [];
        var key = string.Join("|", pairings.Select(pairing => pairing.PeerId + pairing.Code)) + "#" + string.Join("|", requests.Select(request => request.Id));
        if (key == asksShown)
            return;
        asksShown = key;
        asks.Children.Clear();
        foreach (var pairing in pairings)
        {
            asks.Children.Add(Ask(Tr.T(pairing.Incoming ? "{0} wants to join" : "Pairing with {0}", pairing.PeerName),
                                  Tr.T("Press “They match” only if the other PC shows this same code."),
                                  new Label { Text = pairing.Code, FontSize = 34, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace" },
                                  Theme.Accent(Tr.T("They match"), () => { node!.Confirm(pairing); Refresh(); }),
                                  Theme.Plain(Tr.T("Refuse"), () => { node!.Refuse(pairing); Refresh(); })));
        }
        foreach (var request in requests)
        {
            asks.Children.Add(Ask(Tr.T("{0} wants to send {1} file(s)", request.PeerName, request.Files), $"{request.Name}  ·  {Size(request.Size)}", null,
                                  Theme.Accent(Tr.T("Accept"), () => { node!.Accept(request.Id); Refresh(); }),
                                  Theme.Plain(Tr.T("Refuse"), () => { node!.Refuse(request.Id); Refresh(); })));
        }
    }

    private static View Ask(string title, string detail, View? lead, Button yes, Button no)
    {
        var body = new VerticalStackLayout { Spacing = 8, Children = { Theme.Title(title, 17), Theme.Faint(detail) } };
        if (lead is not null)
            body.Children.Add(lead);
        body.Children.Add(new HorizontalStackLayout { Spacing = 10, Children = { yes, no } });
        var card = Theme.CardOf(body);
        card.Stroke = Theme.Primary;
        return card;
    }

    private void ShowTargets(PortalNode? node)
    {
        var all = Courier.Targets(node);
        var waiting = Courier.Waiting.Count;
        waitingText.Text = waiting > 0 ? Tr.T("{0} file(s) shared from another app are ready. Tap a PC to send them.", waiting)
                         : all.Count == 0 ? Tr.T("No PC is paired yet.") : Tr.T("Choose files to send, or share them to PC Butler from any app.");
        waitingText.TextColor = waiting > 0 ? Theme.Primary : Theme.Text;
        var key = string.Join("|", all.Select(target => $"{target.Name}{target.Ready}")) + waiting;
        if (key == targetsShown)
            return;
        targetsShown = key;
        targets.Children.Clear();
        foreach (var target in all)
        {
            var row = Theme.Plain($"{(target.Ready ? "●" : "○")}  {target.Name}", () =>
            {
                if (Courier.Waiting.Count > 0)
                    SendWaiting(target.Tag);
            });
            row.IsEnabled = target.Ready;
            row.HorizontalOptions = LayoutOptions.Fill;
            targets.Children.Add(row);
        }
    }

    private void ShowTransfers(PortalNode? node)
    {
        var shown = node?.Transfers.Reverse().Take(6).ToList() ?? [];
        var key = string.Join("|", shown.Select(transfer => transfer.Id));
        if (key != transfersShown)
        {
            transfersShown = key;
            transfers.Children.Clear();
            foreach (var gone in rows.Keys.Where(transfer => !shown.Contains(transfer)).ToList())
                rows.Remove(gone);
            if (shown.Count == 0)
                transfers.Children.Add(Theme.Faint(Tr.T("Nothing has been sent or received yet.")));
            foreach (var transfer in shown)
            {
                var bar = new ProgressBar { HeightRequest = 6 };
                var detail = Theme.Faint("", 12);
                var cancel = Theme.Plain("✕", () => { transfer.Cancel(); Refresh(); });
                cancel.Padding = new Thickness(10, 4);
                var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                head.Add(Theme.Body((transfer.Incoming ? "↓  " : "↑  ") + transfer.Name, 15), 0);
                head.Add(cancel, 1);
                transfers.Children.Add(new VerticalStackLayout { Spacing = 4, Children = { head, bar, detail } });
                rows[transfer] = rows.TryGetValue(transfer, out var old) ? (bar, detail, old.Done, old.At, old.Speed) : (bar, detail, transfer.Done, DateTime.UtcNow, 0);
            }
        }
        var clock = DateTime.UtcNow;
        foreach (var transfer in shown)
        {
            var (bar, detail, done, at, speed) = rows[transfer];
            if ((clock - at).TotalSeconds >= 0.9)
                (done, at, speed) = (transfer.Done, clock, Math.Max(0, (transfer.Done - done) / (clock - at).TotalSeconds));
            rows[transfer] = (bar, detail, done, at, speed);
            var part = transfer.Size > 0 ? (double)transfer.Done / transfer.Size : 1;
            bar.Progress = transfer.State == "done" ? 1 : part;
            var direction = Tr.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer);
            detail.Text = transfer.State switch
            {
                "waiting" => $"{direction}  ·  " + Tr.T(transfer.Incoming ? "Waiting for your answer" : "Waiting for the other PC to accept"),
                "done" => $"{direction}  ·  " + Tr.T("Done") + $"  ·  {Size(transfer.Size)}",
                "active" => $"{direction}  ·  {part * 100:0}%  ·  {Size(transfer.Done)} / {Size(transfer.Size)}" + (speed > 0 ? $"  ·  {Size((long)speed)}/s" : ""),
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
