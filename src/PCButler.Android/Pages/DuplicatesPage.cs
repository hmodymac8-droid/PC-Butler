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
    private readonly Picker mode = new() { TextColor = Theme.Text, Title = "" };
    private readonly Label status = Theme.Body("");
    private readonly VerticalStackLayout groups = new() { Spacing = 0 };
    private readonly Button remove;
    private readonly List<(DuplicateEntry Entry, CheckBox Box)> boxes = [];
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public DuplicatesPage() : base("Duplicates", "The same file kept twice or more: find the copies, keep one.")
    {
        Body.Children.Add(FoldersCard("duplicates", "Copies are looked for in these folders and all the folders inside them.", () => { }));
        foreach (var (_, label) in Duplicates.Modes)
            mode.Items.Add(Tr.T(label));
        mode.SelectedIndex = 0;
        remove = Theme.Accent(Tr.T("Delete ticked"), () => _ = DeleteTicked());
        remove.IsEnabled = false;
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Theme.Faint(Tr.T("Same file means")), mode,
                new HorizontalStackLayout { Spacing = 10, Children = { Theme.Accent(Tr.T("Find duplicates"), () => _ = Scan()), remove } },
                status,
            },
        }));
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
        var key = Duplicates.Modes[mode.SelectedIndex].Key;
        groups.Children.Clear();
        boxes.Clear();
        remove.IsEnabled = false;
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
            var card = new VerticalStackLayout { Spacing = 6 };
            card.Children.Add(Theme.Title($"{group.Files[0].Name}  ·  {group.Files.Count}×  ·  {Size(group.Files[0].Size)}", 15));
            foreach (var file in group.Files)
            {
                var (row, box) = Tickable(file.Path, file.Modified.ToString("yyyy-MM-dd HH:mm") + (file == keep ? "  ·  " + Tr.T("kept") : ""), file != keep);
                box.CheckedChanged += (_, _) => Count();
                boxes.Add((file, box));
                card.Children.Add(row);
            }
            groups.Children.Add(Theme.CardOf(card, 14));
        }
        if (found.Count > 150)
            groups.Children.Add(Theme.Faint(Tr.T("The first 150 groups are shown. Delete these, then look again for the rest.")));
        Count();
    }

    private void Count()
    {
        var ticked = boxes.Where(item => item.Box.IsChecked).ToList();
        remove.IsEnabled = ticked.Count > 0;
        remove.Text = ticked.Count == 0 ? Tr.T("Delete ticked") : Tr.T("Delete {0} ({1})", ticked.Count, Size(ticked.Sum(item => item.Entry.Size)));
    }

    private async Task DeleteTicked()
    {
        var chosen = boxes.Where(item => item.Box.IsChecked).Select(item => docs[item.Entry.Path]).ToList();
        if (chosen.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, on the Storage page.", chosen.Count), Tr.T("Delete")))
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
