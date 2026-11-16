// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Clipboard: bits of text kept for later (typed, copied here, or copied on a connected PC),
/// pinned to the top, copied back with one tap, or put on a connected PC's clipboard. With
/// "Share with my PCs" on, text a PC copies lands on this phone's clipboard, and what was
/// copied here goes to the PCs when PC Butler comes back to the screen.
/// </summary>
public sealed class ClipboardPage : AppPage
{
    private readonly VerticalStackLayout list = new() { Spacing = 0 };
    private readonly Label count = Theme.Faint("", 12.5);
    private readonly Editor typed = new() { Placeholder = Tr.T("Type or paste text…"), AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 120, TextColor = Theme.Text, PlaceholderColor = Theme.Muted, FontSize = 15 };
    private readonly VerticalStackLayout addBox;
    private readonly Switch share = new() { OnColor = Theme.Primary, ThumbColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly Switch floating = new() { OnColor = Theme.Primary, ThumbColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly Switch watch = new() { OnColor = Theme.Primary, ThumbColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private bool showing; // the switches are being set to what is on, not by the user
    private Action closeAdd = () => { };
    private const int PageSize = 30;   // cards built at a time; "Show more" adds the next ones
    private const int PreviewLength = 400; // a long text shows its start only (up to 1 MB can arrive from a PC)
    private int shown = PageSize;
    private bool visible, queued, built;

    public ClipboardPage() : base("Clipboard", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("Saved text & PC sharing"), 13));
        HeaderRight.Content = Theme.IconButton("i_plus_w.png", OpenAdd);
        share.IsToggled = ClipboardSync.On;
        share.Toggled += (_, e) => ClipboardSync.SetOn(e.Value);
        Body.Children.Add(Theme.CardOf(Theme.Line3(null, Theme.TwoLines(Theme.Body(Tr.T("Share with my PCs"), 14.5),
                                                                        Theme.Faint(Tr.T("Text copied on a connected PC lands here, ready to paste. What you copy here goes to the PCs when you come back to PC Butler."), 12)),
                                                   share, 8), 16));
        floating.Toggled += (_, e) =>
        {
            if (showing)
                return;
            ClipBubble.SetOn(e.Value);
            if (e.Value && !ClipBubble.Allowed)
                ClipBubble.AskAllowed();
        };
        Body.Children.Add(Theme.CardOf(Theme.Line3(null, Theme.TwoLines(Theme.Body(Tr.T("Floating button"), 14.5),
                                                                        Theme.Faint(Tr.T("PC Butler's logo floats over every app while Courier is on. Tap it to send what's copied to the PC; drag it anywhere."), 12)),
                                                   floating, 8), 16));
#if !PLAY_STORE
        // Android decides this one in its Accessibility settings: the switch opens them (not in the Google Play build).
        watch.Toggled += (_, e) =>
        {
            if (!showing && e.Value != CopyWatcher.On)
                CopyWatcher.OpenSettings();
        };
        Body.Children.Add(Theme.CardOf(Theme.Line3(null, Theme.TwoLines(Theme.Body(Tr.T("Send what I copy by itself"), 14.5),
                                                                        Theme.Faint(Tr.T("Whatever you copy in any app goes to the connected PC at once, even with PC Butler closed. Android allows it only through Accessibility: switch on \"PC Butler: send what I copy\" there."), 12)),
                                                   watch, 8), 16));
#endif
        var sendCopied = Theme.Accent(Tr.T("Send what's copied to PC"), () => _ = SendCopied(), "i_send_k.png");
        var paste = Theme.Tonal(Tr.T("Save what's copied"), () => _ = SaveCopied(), "i_clipboard_m.png");
        (sendCopied.Margin, paste.Margin) = (new Thickness(0, 12, 0, 0), new Thickness(0, 10, 0, 0));
        Body.Children.Add(sendCopied);
        Body.Children.Add(paste);
        Body.Children.Add(Theme.Section(Tr.T("Saved snippets"), count));
        Body.Children.Add(list);
        var save = Theme.Accent(Tr.T("Save text"), () =>
        {
            ClipboardSync.Add(typed.Text ?? "");
            typed.Text = "";
            closeAdd();
        });
        addBox = new VerticalStackLayout { Spacing = 12, Children = { Theme.BoxOf(typed, 12, Theme.Card), save } };
        list.Children.Add(Theme.CardOf(Theme.Faint(Tr.T("Loading…"), 13), 16));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!visible)
        {
            visible = true;
            ClipboardSync.Changed += OnChanged;
            ClipBubble.Back += OnBack;
        }
        share.IsToggled = ClipboardSync.On;
        Sync();
        Queue();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        visible = false;
        ClipboardSync.Changed -= OnChanged;
        ClipBubble.Back -= OnBack;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Queue);

    private void OnBack() => MainThread.BeginInvokeOnMainThread(Sync);

    /// <summary>One rebuild for any burst of changes, after the page has had a frame to show.</summary>
    private void Queue()
    {
        if (queued)
            return;
        queued = true;
        Dispatcher.Dispatch(async () =>
        {
            // The first time, reading the saved file happens off the UI thread.
            var items = built ? ClipboardSync.Snapshot() : await Task.Run(ClipboardSync.Snapshot);
            queued = false;
            built = true;
            Show(items);
        });
    }

    /// <summary>The floating button and the copy watcher as Android has them now (the user may come back from its settings).</summary>
    private void Sync()
    {
        showing = true;
        floating.IsToggled = ClipBubble.On && ClipBubble.Allowed;
#if !PLAY_STORE
        watch.IsToggled = CopyWatcher.On;
#endif
        showing = false;
    }

    private void OpenAdd() => closeAdd = OpenSheet(Tr.T("Add text"), addBox);

    private async Task SaveCopied()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            await Say(Tr.T("Nothing is copied right now."));
            return;
        }
        ClipboardSync.Add(text);
    }

    /// <summary>What is copied right now, onto the clipboard of every connected PC.</summary>
    private async Task SendCopied()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            await Say(Tr.T("Nothing is copied right now."));
            return;
        }
        var (sent, problem) = await ClipboardSync.SendToAll(text);
        if (sent > 0)
            ClipboardSync.Add(text);
        await Say(sent > 0 ? Tr.T("On the PC's clipboard now: press Ctrl+V there to paste it.") : problem ?? Tr.T("Connect to a PC on the Courier page first."));
    }

    private void Show(List<Snippet> items)
    {
        count.Text = items.Count.ToString();
        var cards = new List<View>();
        if (items.Count == 0)
            cards.Add(Theme.CardOf(Theme.Empty("i_clipboard_m.png", Tr.T("No saved text"), Tr.T("Copy something in any app, then tap Save what's copied."))));
        foreach (var snippet in items.Take(shown))
            cards.Add(Card(snippet));
        if (items.Count > shown)
        {
            var more = Theme.Tonal(Tr.T("Show more ({0})", items.Count - shown), () => { shown += PageSize; Queue(); });
            more.Margin = new Thickness(0, 4, 0, 12);
            cards.Add(more);
        }
        // Swapped in one go, so the page lays out once instead of once per card.
        list.Children.Clear();
        foreach (var card in cards)
            list.Children.Add(card);
    }

    private static string Preview(string text) => text.Length <= PreviewLength ? text : text[..PreviewLength].TrimEnd() + "…";

    private View Card(Snippet snippet)
    {
        var label = new HorizontalStackLayout { Spacing = 6 };
        if (snippet.Pinned)
            label.Children.Add(Theme.Icon("i_pin_m.png", 13));
        label.Children.Add(Theme.Faint((snippet.Pinned ? Tr.T("Pinned") : snippet.Saved.ToString("g"))
                                       + (snippet.From.Length > 0 ? "  ·  " + Tr.T("From {0}", snippet.From) : ""), 11.5));
        var text = new Label { Text = Preview(snippet.Text), FontSize = 14.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 6 };
        Border copy = null!;
        copy = Theme.IconButton("i_copy_w.png", async () =>
        {
            await Clipboard.Default.SetTextAsync(snippet.Text);
            Theme.SetIcon(copy, "i_check_m.png");
            await Task.Delay(1400);
            Theme.SetIcon(copy, "i_copy_w.png");
        }, Theme.SurfaceRaised, 40);
        var pin = Theme.IconButton(snippet.Pinned ? "i_pin_off_w.png" : "i_pin_w.png", () => ClipboardSync.TogglePin(snippet), Theme.SurfaceRaised, 40);
        var delete = Theme.IconButton("i_trash_2_r.png", () => ClipboardSync.Remove(snippet), Theme.DangerBack, 40);
        var send = Theme.Tonal(Tr.T("Send to PC"), () => _ = Send(snippet), "i_send_m.png");
        (send.HeightRequest, send.MinimumHeightRequest, send.Padding, send.FontSize) = (40, 40, new Thickness(14, 0), 13);
        var buttons = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8 };
        buttons.Add(copy, 0);
        buttons.Add(pin, 1);
        buttons.Add(delete, 2);
        send.HorizontalOptions = LayoutOptions.End;
        buttons.Add(send, 3);
        return Theme.CardOf(new VerticalStackLayout { Spacing = 10, Children = { label, text, buttons } }, 16);
    }

    /// <summary>The text onto a connected PC's clipboard; a PC with an older PC Butler gets it as a small text file.</summary>
    private async Task Send(Snippet snippet)
    {
        var ready = Courier.Targets(Courier.Node).Where(target => target.Ready).ToList();
        if (Courier.Node is not { } node || ready.Count == 0)
        {
            await Say(Tr.T("Connect to a PC on the Courier page first."));
            return;
        }
        var target = ready[0];
        if (ready.Count > 1)
        {
            var name = await Choose(Tr.T("Send to"), ready.Select(item => new SheetChoice("i_monitor_w.png", item.Name)));
            if (ready.FirstOrDefault(item => item.Name == name) is not { } picked)
                return;
            target = picked;
        }
        if (await node.SendClipboard(target.Tag, snippet.Text) is not { } problem)
        {
            ActivityLog.Record("clipboard", "Text sent to a PC", target.Name);
            await Say(Tr.T("On {0}'s clipboard now: press Ctrl+V there to paste it.", target.Name));
            return;
        }
        if (problem != PortalNode.OldVersion)
        {
            await Say(problem);
            return;
        }
        var file = Path.Combine(FileSystem.CacheDirectory, $"Text from phone {DateTime.Now:yyyy-MM-dd HHmmss}.txt");
        await File.WriteAllTextAsync(file, snippet.Text);
        if (Courier.Send(node, target.Tag, [file]) is null)
        {
            await Say(Tr.T("None of those files could be read."));
            return;
        }
        ActivityLog.Record("clipboard", "Text sent to a PC", target.Name);
        await Say(Tr.T("Sent to {0}. It arrives there as a text file.", target.Name));
    }
}
