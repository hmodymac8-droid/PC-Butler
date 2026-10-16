// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Duplicates on the phone: the same photo saved twice, the same download from two chats.
/// In each group the oldest copy is kept and the others are ticked; ticked ones go to PC
/// Butler's Recycle Bin (on the Storage page), from where they can be put back.
/// </summary>
public sealed class DuplicatesPage : AppPage
{
    private int mode;
    private readonly Label status = new() { FontSize = 14, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center };
    private readonly Border results;
    private readonly Button clear;
    private readonly VerticalStackLayout groups = new() { Spacing = 0 };
    private readonly Button remove;
    private readonly Label removeNote = new() { Text = Tr.T("Deleted copies go to the Recycle Bin first."), FontSize = 12, TextColor = Theme.Muted, HorizontalTextAlignment = TextAlignment.Center };
    private readonly List<(DuplicateEntry Entry, CheckBox Box)> boxes = [];
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public DuplicatesPage() : base("Duplicates", "The same file kept twice or more: find the copies, keep one.")
    {
        Body.Children.Add(FoldersCard("duplicates", "Copies are looked for in these folders and all the folders inside them.", () => { }));
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Caps(Tr.T("Same file means")),
                Theme.Chips(Duplicates.Modes.Select(item => Tr.T(item.Label)).ToList(), 0, index => mode = index),
                Theme.Accent(Tr.T("Find duplicates"), () => _ = Scan()),
            },
        }));
        // What was found, and the one button that deletes the ticked copies.
        remove = Theme.Danger(Tr.T("Delete ticked"), () => _ = DeleteTicked());
        remove.IsEnabled = false;
        clear = Theme.Plain(Tr.T("Clear"), () =>
        {
            foreach (var (_, box) in boxes)
                box.IsChecked = false;
        });
        (clear.BackgroundColor, clear.TextColor, clear.Padding, clear.VerticalOptions) = (Colors.Transparent, Theme.Primary, new Thickness(12, 6), LayoutOptions.Center);
        results = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Line3(Theme.Tile(("ic_copy.png", Theme.AccentBack), 40), status, clear),
                remove,
                removeNote,
            },
        }, 16);
        results.IsVisible = false;
        Body.Children.Add(results);
        Body.Children.Add(groups);
    }

    private async Task Scan()
    {
        scanning?.Cancel();
        scanning = new CancellationTokenSource();
        var cancel = scanning.Token;
        var trees = Folders.For("duplicates");
        if (trees.Count == 0)
        {
            await Say(Tr.T("Choose a folder to look in first."));
            return;
        }
        var key = Duplicates.Modes[mode].Key;
        groups.Children.Clear();
        boxes.Clear();
        remove.IsEnabled = false;
        remove.IsVisible = removeNote.IsVisible = false;
        results.IsVisible = true;
        status.Text = Tr.T("Looking for files…");
        List<DuplicateGroup> found;
        try
        {
            found = await Task.Run(() =>
            {
                // Folders inside each other are walked once; each file is known by its path.
                docs.Clear();
                foreach (var root in trees.Select(Folders.Root).OfType<Doc>())
                    foreach (var doc in Folders.Files(root, cancel))
                        docs.TryAdd(doc.Path, doc);
                var files = docs.Values.Select(doc => new DuplicateEntry(doc.Path, doc.Size, doc.Modified)).ToList();
                Duplicates.OpenFile = Open;
                MainThread.BeginInvokeOnMainThread(() => status.Text = Tr.T("Comparing {0} file(s)…", files.Count));
                return Duplicates.Find(files, key, cancel);
            }, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var waste = found.Sum(group => group.Files.Skip(1).Sum(file => file.Size));
        status.Text = found.Count == 0 ? Tr.T("No duplicates found.")
            : Tr.T("{0} group(s) of duplicates. Deleting the extra copies frees {1}.", found.Count, Size(waste));
        foreach (var group in found.Take(150))
        {
            // The oldest copy is taken for the original, and kept; the others are ticked.
            var keep = group.Files.MinBy(file => file.Modified);
            var card = new VerticalStackLayout { Spacing = 8 };
            var name = new Label { Text = group.Files[0].Name, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.CharacterWrap, VerticalOptions = LayoutOptions.Center };
            var right = new HorizontalStackLayout
            {
                Spacing = 8, VerticalOptions = LayoutOptions.Center,
                Children = { Theme.Pill($"{group.Files.Count}×", Theme.AccentText, Theme.AccentBack), Theme.Faint(Size(group.Files[0].Size), 12).Centered() },
            };
            card.Children.Add(Theme.Line3(Theme.Tile(Theme.TileFor(group.Files[0].Name)), name, right));
            foreach (var file in group.Files)
            {
                var when = file.Modified.ToString("yyyy-MM-dd HH:mm");
                if (file == keep)
                {
                    var kept = Theme.Pill("✓ " + Tr.T("Kept"), Theme.Primary, Theme.PrimarySoft);
                    kept.VerticalOptions = LayoutOptions.Start;
                    var path = new Label { Text = file.Path, FontSize = 13.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.CharacterWrap };
                    card.Children.Add(Theme.BoxOf(Theme.Line3(kept, Theme.TwoLines(path, Theme.Faint($"{when} · " + Tr.T("stays where it is"), 12)), null, 10), 12));
                    continue;
                }
                var (row, box) = Tickable(file.Path, when, true, detailFor: ticked => $"{when} · " + Tr.T(ticked ? "will be deleted" : "tap to select"));
                box.CheckedChanged += (_, _) => Count();
                boxes.Add((file, box));
                card.Children.Add(row);
            }
            groups.Children.Add(Theme.CardOf(card, 16));
        }
        if (found.Count > 150)
            groups.Children.Add(Theme.Faint(Tr.T("The first 150 groups are shown. Delete these, then look again for the rest.")));
        remove.IsVisible = removeNote.IsVisible = found.Count > 0; // nothing found, nothing to delete
        Count();
    }

    private void Count()
    {
        var ticked = boxes.Where(item => item.Box.IsChecked).ToList();
        remove.IsEnabled = ticked.Count > 0;
        remove.Text = ticked.Count == 0 ? Tr.T("Nothing ticked") : Tr.T("Delete {0} · frees {1}", ticked.Count, Size(ticked.Sum(item => item.Entry.Size)));
        clear.IsVisible = ticked.Count > 0;
    }

    private async Task DeleteTicked()
    {
        var chosen = boxes.Where(item => item.Box.IsChecked).Select(item => docs[item.Entry.Path]).ToList();
        if (chosen.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, on the Storage page.", chosen.Count) + "\n\n" + SpaceNote, Tr.T("Delete")))
            return;
        var (count, errors) = await Task.Run(() => Bin(chosen));
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) deleted.", count) : Tr.T("{0} file(s) deleted. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        await Scan();
    }

    /// <summary>A file of the chosen folders, opened to compare contents.</summary>
    private Stream Open(string path)
    {
        try
        {
            return Folders.OpenRead(docs[path]);
        }
        catch (Java.Lang.Exception error)
        {
            throw new IOException(error.Message);
        }
    }

    /// <summary>The bin keeps a copy, so the space comes back only once it is emptied.</summary>
    public static string SpaceNote => Tr.T("The space is freed when the Recycle Bin is emptied; it empties itself after {0} days.", RecycleBin.KeepDays);

    /// <summary>Files into PC Butler's Recycle Bin, one by one.</summary>
    public static (int Count, List<string> Errors) Bin(IEnumerable<Doc> files)
    {
        var count = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                RecycleBin.Send(file);
                count++;
            }
            catch (IOException error)
            {
                errors.Add($"{file.Name}: {error.Message}");
            }
        }
        return (count, errors);
    }
}
