// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// What fills the phone: how full it is, the biggest folders (tap one to look inside), big
/// files nobody has opened for months, and PC Butler's own Recycle Bin, where files deleted
/// here or in Duplicates wait until they are put back or the bin is emptied.
/// </summary>
public sealed class StoragePage : AppPage
{
    private static readonly Color[] Tones = [Theme.Primary, Color.FromArgb("#69acf3"), Theme.Amber, Color.FromArgb("#a993f0"), Color.FromArgb("#5cc6d6")];

    private readonly Label phoneText = Theme.Body("", 16);
    private readonly ContentView phoneBar = new();
    private readonly Label scanStatus = Theme.Faint("");
    private readonly Label folderTitle = Theme.Title("", 16);
    private readonly VerticalStackLayout folderList = new() { Spacing = 8 };
    private readonly Button upButton;
    private readonly VerticalStackLayout bigList = new() { Spacing = 6 };
    private readonly Button bigDelete;
    private readonly VerticalStackLayout binList = new() { Spacing = 6 };
    private readonly Label binText = Theme.Faint("");
    private readonly List<(BigFile File, CheckBox Box)> bigBoxes = [];
    private StorageResult? result;
    private StorageNode? shown;
    private CancellationTokenSource? scanning;

    public StoragePage() : base("Storage", "See what fills your phone. Decide what stays.", needsAllFiles: true)
    {
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 8, Children = { phoneText, phoneBar } }));
        upButton = Theme.Plain(Tr.T("Up"), () =>
        {
            if (shown?.Parent is { } parent)
                ShowNode(parent);
        });
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new HorizontalStackLayout { Spacing = 10, Children = { Theme.Accent(Tr.T("Scan the phone"), () => _ = Scan()), upButton } },
                scanStatus, folderTitle, folderList,
            },
        }));
        bigDelete = Theme.Accent(Tr.T("Delete ticked"), () => _ = DeleteBig());
        bigDelete.IsEnabled = false;
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Theme.Title(Tr.T("Large & old files"), 18), Theme.Faint(Tr.T("Bigger than 50 MB and not opened for 3 months or more.")), bigList, bigDelete },
        }));
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Theme.Title(Tr.T("Recycle Bin"), 18), binText, binList,
                new HorizontalStackLayout { Spacing = 10, Children = { Theme.Plain(Tr.T("Put all back"), () => _ = RestoreAll()), Theme.Plain(Tr.T("Empty"), () => _ = EmptyBin()) } },
            },
        }));
        upButton.IsEnabled = false;
    }

    protected override void Shown()
    {
        var stat = new global::Android.OS.StatFs(PhoneRoot);
        var (total, free) = (stat.TotalBytes, stat.AvailableBytes);
        phoneText.Text = Tr.T("{0} free of {1}", Size(free), Size(total));
        phoneBar.Content = Meter(total > 0 ? (double)(total - free) / total : 0, (total - free) * 10 >= total * 9 ? Theme.Red : Theme.Primary);
        ShowBin();
    }

    private async Task Scan()
    {
        scanning?.Cancel();
        scanning = new CancellationTokenSource();
        var cancel = scanning.Token;
        var current = "";
        scanStatus.Text = Tr.T("Looking for files…");
        var ticking = Dispatcher.CreateTimer();
        ticking.Interval = TimeSpan.FromMilliseconds(300);
        ticking.Tick += (_, _) => scanStatus.Text = Short(current);
        ticking.Start();
        try
        {
            result = await Task.Run(() => Storage.Scan(PhoneRoot, cancel, folder => current = folder), cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            ticking.Stop();
        }
        scanStatus.Text = Tr.T("{0} in {1} file(s).", Size(result.Root.Size), result.Root.Files.ToString("N0"));
        ShowNode(result.Root);
        ShowBig();
    }

    /// <summary>A folder's biggest parts, each with a bar of its share; tapping a folder opens it.</summary>
    private void ShowNode(StorageNode node)
    {
        shown = node;
        upButton.IsEnabled = node.Parent is not null;
        folderTitle.Text = node.Parent is null ? Tr.T("Internal storage") : Short(node.Path);
        folderList.Children.Clear();
        var index = 0;
        foreach (var child in node.Children.Where(child => child.Size > 0).Take(25))
        {
            var part = node.Size > 0 ? (double)child.Size / node.Size : 0;
            var name = child.Path.Length == 0 ? Tr.T("{0} smaller files", child.Files.ToString("N0")) : (child.IsFolder ? "📁  " : "") + child.Name;
            var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            head.Add(Theme.Body(name, 14), 0);
            head.Add(Theme.Faint(Size(child.Size), 12.5), 1);
            var row = new VerticalStackLayout { Spacing = 4, Children = { head, Meter(part, Tones[index++ % Tones.Length]) } };
            if (child.IsFolder && child.Children.Count > 0)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => ShowNode(child);
                row.GestureRecognizers.Add(tap);
            }
            folderList.Children.Add(row);
        }
    }

    private void ShowBig()
    {
        bigList.Children.Clear();
        bigBoxes.Clear();
        var files = result is null ? [] : Storage.LargeAndOld(result, Storage.BigFileMinimum, 3);
        if (files.Count == 0)
            bigList.Children.Add(Theme.Faint(Tr.T("No files match.")));
        foreach (var file in files.Take(40))
        {
            var (row, box) = Tickable(file.Name, $"{Size(file.Size)}  ·  {Tr.T("Last used")} {file.LastUsed:yyyy-MM-dd}  ·  {Short(Path.GetDirectoryName(file.Path) ?? "")}");
            box.CheckedChanged += (_, _) =>
            {
                var ticked = bigBoxes.Where(item => item.Box.IsChecked).ToList();
                bigDelete.IsEnabled = ticked.Count > 0;
                bigDelete.Text = ticked.Count == 0 ? Tr.T("Delete ticked") : Tr.T("Delete {0} ({1})", ticked.Count, Size(ticked.Sum(item => item.File.Size)));
            };
            bigBoxes.Add((file, box));
            bigList.Children.Add(row);
        }
    }

    private async Task DeleteBig()
    {
        var paths = bigBoxes.Where(item => item.Box.IsChecked).Select(item => item.File.Path).ToList();
        if (paths.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, below.", paths.Count), Tr.T("Delete")))
            return;
        var (count, errors) = await Task.Run(() => Sorter.DeleteFiles(paths));
        result?.BigFiles.RemoveAll(file => !File.Exists(file.Path));
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) deleted.", count) : Tr.T("{0} file(s) deleted. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        ShowBig();
        ShowBin();
        Shown();
    }

    // --- PC Butler's Recycle Bin ---

    private void ShowBin()
    {
        var items = RecycleBin.Items();
        binText.Text = items.Count == 0 ? Tr.T("Empty. Files deleted in Duplicates or here wait in it until put back.")
            : Tr.T("{0} file(s), {1}. Tap one to put it back where it was.", items.Count, Size(items.Sum(item => item.Size)));
        binList.Children.Clear();
        foreach (var item in items.Take(30))
        {
            binList.Children.Add(Theme.Row($"↩  {Path.GetFileName(item.Original)}  ·  {Size(item.Size)}", () => _ = RestoreOne(item)));
        }
    }

    private async Task RestoreOne(RecycleBin.Entry item)
    {
        try
        {
            RecycleBin.Restore(item.Original);
        }
        catch (IOException error)
        {
            await Say(Tr.T("{0} could not be put back: {1}", Path.GetFileName(item.Original), error.Message));
        }
        ShowBin();
    }

    private async Task RestoreAll()
    {
        var errors = new List<string>();
        foreach (var item in RecycleBin.Items())
        {
            try
            {
                RecycleBin.Restore(item.Original);
            }
            catch (IOException error)
            {
                errors.Add($"{Path.GetFileName(item.Original)}: {error.Message}");
            }
        }
        if (errors.Count > 0)
            await Say(string.Join("\n", errors.Take(8)));
        ShowBin();
    }

    private async Task EmptyBin()
    {
        var items = RecycleBin.Items();
        if (items.Count == 0 || !await Ask(Tr.T("Delete the {0} file(s) in the Recycle Bin for good? This cannot be undone.", items.Count), Tr.T("Empty")))
            return;
        RecycleBin.Empty();
        ShowBin();
        Shown();
    }
}
