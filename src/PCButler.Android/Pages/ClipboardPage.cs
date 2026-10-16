// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Clipboard: bits of text kept for later (typed, or what is copied right now), pinned to the
/// top, copied back with one tap, or sent to a connected PC through Courier as a text file.
/// Nothing watches the clipboard in the background: text is added only when the user asks.
/// </summary>
public sealed class ClipboardPage : AppPage
{
    private sealed class Snippet
    {
        public long Id { get; set; }
        public string Text { get; set; } = "";
        public bool Pinned { get; set; }
        public DateTime Saved { get; set; }
    }

    private static string StoreFile => Path.Combine(AppInfo.DataDir, "snippets.json");
    private readonly VerticalStackLayout list = new() { Spacing = 0 };
    private readonly Label count = Theme.Faint("", 12.5);
    private readonly Editor typed = new() { Placeholder = Tr.T("Type or paste text…"), AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 120, TextColor = Theme.Text, PlaceholderColor = Theme.Muted, FontSize = 15 };
    private readonly VerticalStackLayout addBox;
    private Action closeAdd = () => { };
    private List<Snippet> snippets = Load();

    public ClipboardPage() : base("Clipboard", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("Saved text & PC sharing"), 13));
        HeaderRight.Content = Theme.IconButton("i_plus_w.png", OpenAdd);
        var paste = Theme.Accent(Tr.T("Save what's copied"), () => _ = SaveCopied(), "i_clipboard_k.png");
        Body.Children.Add(paste);
        Body.Children.Add(Theme.Hint(Tr.T("Text is added only when you tap. Nothing watches the clipboard in the background.")));
        Body.Children.Add(Theme.Section(Tr.T("Saved snippets"), count));
        Body.Children.Add(list);
        var save = Theme.Accent(Tr.T("Save text"), () =>
        {
            Add(typed.Text ?? "");
            typed.Text = "";
            closeAdd();
        });
        addBox = new VerticalStackLayout { Spacing = 12, Children = { Theme.BoxOf(typed, 12, Theme.Card), save } };
        Show();
    }

    private static List<Snippet> Load()
    {
        try
        {
            return File.Exists(StoreFile) ? JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(StoreFile)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        File.WriteAllText(StoreFile, JsonSerializer.Serialize(snippets));
        Show();
    }

    private void OpenAdd() => closeAdd = OpenSheet(Tr.T("Add text"), addBox);

    private void Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        snippets.RemoveAll(item => item.Text == text); // the same text once, on top
        snippets.Insert(0, new Snippet { Id = DateTime.UtcNow.Ticks, Text = text, Saved = DateTime.Now });
        Save();
    }

    private async Task SaveCopied()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            await Say(Tr.T("Nothing is copied right now."));
            return;
        }
        Add(text);
    }

    private void Show()
    {
        count.Text = snippets.Count.ToString();
        list.Children.Clear();
        if (snippets.Count == 0)
            list.Children.Add(Theme.CardOf(Theme.Empty("i_clipboard_m.png", Tr.T("No saved text"), Tr.T("Copy something in any app, then tap Save what's copied."))));
        foreach (var snippet in snippets.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.Id))
        {
            var label = new HorizontalStackLayout { Spacing = 6 };
            if (snippet.Pinned)
                label.Children.Add(Theme.Icon("i_pin_m.png", 13));
            label.Children.Add(Theme.Faint(snippet.Pinned ? Tr.T("Pinned") : snippet.Saved.ToString("g"), 11.5));
            var text = new Label { Text = snippet.Text, FontSize = 14.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 6 };
            Border copy = null!;
            copy = Theme.IconButton("i_copy_w.png", async () =>
            {
                await Clipboard.Default.SetTextAsync(snippet.Text);
                Theme.SetIcon(copy, "i_check_m.png");
                await Task.Delay(1400);
                Theme.SetIcon(copy, "i_copy_w.png");
            }, Theme.SurfaceRaised, 40);
            var pin = Theme.IconButton(snippet.Pinned ? "i_pin_off_w.png" : "i_pin_w.png", () => { snippet.Pinned = !snippet.Pinned; Save(); }, Theme.SurfaceRaised, 40);
            var delete = Theme.IconButton("i_trash_2_r.png", () => { snippets.Remove(snippet); Save(); }, Theme.DangerBack, 40);
            var send = Theme.Tonal(Tr.T("Send to PC"), () => _ = Send(snippet), "i_send_m.png");
            (send.HeightRequest, send.MinimumHeightRequest, send.Padding, send.FontSize) = (40, 40, new Thickness(14, 0), 13);
            var buttons = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8 };
            buttons.Add(copy, 0);
            buttons.Add(pin, 1);
            buttons.Add(delete, 2);
            send.HorizontalOptions = LayoutOptions.End;
            buttons.Add(send, 3);
            list.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 10, Children = { label, text, buttons } }, 16));
        }
    }

    /// <summary>The text to a connected PC, as a small text file through Courier.</summary>
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
