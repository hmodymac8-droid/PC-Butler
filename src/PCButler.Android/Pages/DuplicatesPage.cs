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
    private static readonly (string Label, Func<string[]> Folders)[] Places =
    [
        ("Download", () => [ConfigStore.DetectDownloadsFolder()]),
        ("Photos and pictures", () => [Path.Combine(PhoneRoot, "DCIM"), Path.Combine(PhoneRoot, "Pictures")]),
        ("Chats (WhatsApp, Telegram)", () => [Path.Combine(PhoneRoot, "Android", "media"), Path.Combine(PhoneRoot, "WhatsApp"), Path.Combine(PhoneRoot, "Telegram")]),
        ("The whole phone", () => [PhoneRoot]),
    ];

    private readonly Picker place = new() { TextColor = Theme.Text, Title = "" };
    private readonly Picker mode = new() { TextColor = Theme.Text, Title = "" };
    private readonly Label status = Theme.Body("");
    private readonly VerticalStackLayout groups = new() { Spacing = 0 };
    private readonly Button remove;
    private readonly List<(DuplicateEntry Entry, CheckBox Box)> boxes = [];
    private CancellationTokenSource? scanning;

    public DuplicatesPage() : base("Duplicates", "The same file kept twice or more: find the copies, keep one.", needsAllFiles: true)
    {
        foreach (var (label, _) in Places)
            place.Items.Add(Tr.T(label));
        place.SelectedIndex = 0;
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
                Theme.Faint(Tr.T("Look in")), place, Theme.Faint(Tr.T("Same file means")), mode,
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
        var folders = Places[place.SelectedIndex].Folders().Where(Directory.Exists).ToList();
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
                var files = Duplicates.Collect(folders, recursive: true, cancel);
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
                var (row, box) = Tickable(Short(file.Path), file.Modified.ToString("yyyy-MM-dd HH:mm") + (file == keep ? "  ·  " + Tr.T("kept") : ""), file != keep);
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
        var paths = boxes.Where(item => item.Box.IsChecked).Select(item => item.Entry.Path).ToList();
        if (paths.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, on the Storage page.", paths.Count), Tr.T("Delete")))
            return;
        var (count, errors) = await Task.Run(() => Sorter.DeleteFiles(paths));
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) deleted.", count) : Tr.T("{0} file(s) deleted. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        await Scan();
    }
}
