// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// What fills the phone: how full it is, the biggest folders among the ones the user chose
/// (tap one to look inside), big files nobody has changed for months, and PC Butler's own
/// Recycle Bin, where files deleted here or in Duplicates wait until they are put back or
/// the bin is emptied.
/// </summary>
public sealed class StoragePage : AppPage
{
    private static readonly Color[] Tones = [Theme.Primary, Color.FromArgb("#69acf3"), Theme.Amber, Color.FromArgb("#a993f0"), Color.FromArgb("#5cc6d6")];

    private readonly Label phoneFree = new() { FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.End };
    private readonly Label phoneTotal = new() { FontSize = 14, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Label phoneUsed = Theme.Faint("", 12.5);
    private readonly ContentView phoneBar = new();
    private readonly Label scanStatus = new() { FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End, LineBreakMode = LineBreakMode.HeadTruncation };
    private readonly Label folderTitle = new() { FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly Border binPill = Theme.Pill("", Theme.Amber, Theme.AmberSoft);
    private readonly Border resultsCard;
    private readonly Border bigCard;
    private readonly VerticalStackLayout folderList = new() { Spacing = 8 };
    private readonly Button upButton;
    private readonly VerticalStackLayout bigList = new() { Spacing = 6 };
    private readonly Button bigDelete;
    private readonly VerticalStackLayout binList = new() { Spacing = 6 };
    private readonly Label binText = Theme.Faint("");
    private readonly List<(BigFile File, CheckBox Box)> bigBoxes = [];
    private StorageResult? result;
    private StorageNode? shown;
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public StoragePage() : base("Storage", "See what fills your phone. Decide what stays.")
    {
        var free = new HorizontalStackLayout { Spacing = 8, Children = { phoneFree, phoneTotal } };
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 10, Children = { free, phoneBar, phoneUsed } }));
        Body.Children.Add(FoldersCard("storage", "These folders, and all the folders inside them, are measured.", () => { }));
        upButton = Theme.Plain(Tr.T("Up"), () =>
        {
            if (shown?.Parent is { } parent)
                ShowNode(parent);
        });
        var scanRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 14) };
        scanRow.Add(Theme.Accent(Tr.T("Scan"), () => _ = Scan()), 0);
        scanRow.Add(upButton, 1);
        Body.Children.Add(scanRow);
        var head = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 10 };
        head.Add(folderTitle, 0);
        head.Add(scanStatus, 1);
        // The results and the large files show once something has been scanned.
        resultsCard = Theme.CardOf(new VerticalStackLayout { Spacing = 4, Children = { head, folderList } }, 16);
        resultsCard.IsVisible = false;
        Body.Children.Add(resultsCard);
        bigDelete = Theme.Danger(Tr.T("Tick a file first"), () => _ = DeleteBig());
        bigDelete.IsEnabled = false;
        bigCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Header(Tr.T("Large & old files"), null, 16), Theme.Faint(Tr.T("Bigger than 50 MB and not changed for 3 months or more."), 12.5), bigList, bigDelete,
                new Label { Text = Tr.T("Deleted files go to the Recycle Bin first."), FontSize = 12, TextColor = Theme.Muted, HorizontalTextAlignment = TextAlignment.Center },
            },
        }, 16);
        bigCard.IsVisible = false;
        Body.Children.Add(bigCard);
        var bins = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        bins.Add(Theme.Plain(Tr.T("Put all back"), () => _ = RestoreAll()), 0);
        bins.Add(Theme.DangerGhost(Tr.T("Empty"), () => _ = EmptyBin()), 1);
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Line3(Theme.Tile(Theme.BinTile), Theme.TwoLines(new Label { Text = Tr.T("Recycle Bin"), FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text }, binText), binPill),
                binList, bins,
            },
        }, 16));
        upButton.IsEnabled = false;
    }

    protected override void Shown()
    {
        var stat = new global::Android.OS.StatFs(global::Android.OS.Environment.DataDirectory!.AbsolutePath);
        var (total, free) = (stat.TotalBytes, stat.AvailableBytes);
        var used = total > 0 ? (double)(total - free) / total : 0;
        phoneFree.Text = Tr.T("{0} free", Size(free));
        phoneTotal.Text = Tr.T("of {0}", Size(total));
        phoneUsed.Text = Tr.T("{0}% used.", Math.Round(used * 100));
        phoneBar.Content = Theme.Bar(used, used >= 0.9 ? Theme.Red : Theme.Primary, 10);
        ShowBin();
    }

    private async Task Scan()
    {
        scanning?.Cancel();
        scanning = new CancellationTokenSource();
        var cancel = scanning.Token;
        var trees = Folders.For("storage");
        if (trees.Count == 0)
        {
            await Say(Tr.T("Choose a folder to measure first."));
            return;
        }
        var current = "";
        resultsCard.IsVisible = true;
        folderTitle.Text = Tr.T("Your folders");
        scanStatus.Text = Tr.T("Looking for files…");
        var ticking = Dispatcher.CreateTimer();
        ticking.Interval = TimeSpan.FromMilliseconds(300);
        ticking.Tick += (_, _) => scanStatus.Text = current;
        ticking.Start();
        try
        {
            result = await Task.Run(() => Measure(trees, cancel, folder => current = folder), cancel);
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
        bigCard.IsVisible = true;
        ShowBig();
    }

    /// <summary>
    /// The chosen folders measured, as one tree: each folder's size, the biggest files, and
    /// the big ones listed apart. A folder inside another chosen one is counted once.
    /// </summary>
    private StorageResult Measure(List<global::Android.Net.Uri> trees, CancellationToken cancel, Action<string> progress)
    {
        docs.Clear();
        var top = new StorageNode { Name = Tr.T("Your folders"), Path = "/", IsFolder = true };
        var result = new StorageResult { Root = top };
        var roots = trees.Select(Folders.Root).OfType<Doc>().OrderBy(root => root.Path.Length).ToList();
        var seen = new List<string>();
        foreach (var root in roots)
        {
            if (seen.Any(path => root.Path.StartsWith(path + "/", StringComparison.Ordinal) || root.Path == path))
                continue;
            seen.Add(root.Path);
            var node = new StorageNode { Name = root.Path, Path = root.Path, IsFolder = true, Parent = top };
            Fill(node, root, result, cancel, progress);
            top.Children.Add(node);
            top.Size += node.Size;
            top.Files += node.Files;
        }
        top.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        result.BigFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
        return result;
    }

    private void Fill(StorageNode node, Doc folder, StorageResult result, CancellationToken cancel, Action<string> progress)
    {
        cancel.ThrowIfCancellationRequested();
        progress(folder.Path);
        var files = new List<StorageNode>();
        foreach (var child in Folders.Children(folder))
        {
            if (child.IsFolder)
            {
                var sub = new StorageNode { Name = child.Name, Path = child.Path, IsFolder = true, Parent = node };
                Fill(sub, child, result, cancel, progress);
                if (sub.Size > 0 || sub.Files > 0)
                    node.Children.Add(sub);
                node.Size += sub.Size;
                node.Files += sub.Files;
                continue;
            }
            docs[child.Path] = child;
            node.Size += child.Size;
            node.Files++;
            files.Add(new StorageNode { Name = child.Name, Path = child.Path, Size = child.Size, Files = 1, Parent = node });
            if (child.Size >= Storage.BigFileMinimum)
                result.BigFiles.Add(new BigFile(child.Path, child.Size, child.Modified));
        }
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Children.AddRange(files.Take(40));
        if (files.Count > 40)
        {
            var rest = files.Skip(40).ToList();
            node.Children.Add(new StorageNode { Name = "", Path = "", Size = rest.Sum(file => file.Size), Files = rest.Count, Parent = node });
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>A folder's biggest parts, each with a bar of its share; tapping a folder opens it.</summary>
    private void ShowNode(StorageNode node)
    {
        shown = node;
        upButton.IsEnabled = node.Parent is not null;
        folderTitle.Text = node.Parent is null ? Tr.T("Your folders") : node.Path;
        folderList.Children.Clear();
        var index = 0;
        foreach (var child in node.Children.Where(child => child.Size > 0).Take(25))
        {
            // A folder or file with its tile, name and size, and a bar of its share under it.
            var part = node.Size > 0 ? (double)child.Size / node.Size : 0;
            var name = child.Path.Length == 0 ? Tr.T("{0} smaller files", child.Files.ToString("N0")) : child.Name;
            var tile = child.IsFolder ? Theme.FolderTile : child.Path.Length == 0 ? Theme.FileTile : Theme.TileFor(child.Name);
            var title = new Label { Text = name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.MiddleTruncation };
            var size = new Label { Text = Size(child.Size), FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center };
            var bar = Meter(part, Tones[index++ % Tones.Length]);
            bar.Margin = new Thickness(48, 0, 0, 0);
            var row = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(0, 10), Children = { Theme.Line3(Theme.Tile(tile, 36), title, size), bar } };
            if (child.IsFolder && child.Children.Count > 0)
                row.Tapped(() => ShowNode(child));
            if (folderList.Children.Count > 0)
                folderList.Children.Add(new BoxView { HeightRequest = 1, Color = Theme.Line });
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
        bigDelete.Text = Tr.T("Tick a file first");
        bigDelete.IsEnabled = false;
        foreach (var file in files.Take(40))
        {
            var (row, box) = Tickable(file.Name, $"{Size(file.Size)} · {Tr.T("Last used")} {file.LastUsed:yyyy-MM-dd} · {Path.GetDirectoryName(file.Path)}", tile: Theme.TileFor(file.Name));
            box.CheckedChanged += (_, _) =>
            {
                var ticked = bigBoxes.Where(item => item.Box.IsChecked).ToList();
                bigDelete.IsEnabled = ticked.Count > 0;
                bigDelete.Text = ticked.Count == 0 ? Tr.T("Tick a file first") : Tr.T("Delete {0} · frees {1}", ticked.Count, Size(ticked.Sum(item => item.File.Size)));
            };
            bigBoxes.Add((file, box));
            bigList.Children.Add(row);
        }
    }

    private async Task DeleteBig()
    {
        var chosen = bigBoxes.Where(item => item.Box.IsChecked).Select(item => item.File).ToList();
        if (chosen.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, below.", chosen.Count), Tr.T("Delete")))
            return;
        var (count, errors) = await Task.Run(() => DuplicatesPage.Bin(chosen.Select(file => docs[file.Path])));
        var gone = chosen.Select(file => file.Path).ToHashSet();
        result?.BigFiles.RemoveAll(file => gone.Contains(file.Path) && !errors.Any(error => error.StartsWith(Path.GetFileName(file.Path) + ":")));
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
        binPill.IsVisible = items.Count > 0;
        Theme.SetPill(binPill, items.Count.ToString(), Theme.Amber, Theme.AmberSoft);
        binList.Children.Clear();
        foreach (var item in items.Take(30))
        {
            var name = new Label { Text = Path.GetFileName(item.Original), FontSize = 13.5, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.MiddleTruncation };
            var size = new Label { Text = "↩  " + Size(item.Size), FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center };
            binList.Children.Add(Theme.BoxOf(Theme.Line3(Theme.Tile(Theme.TileFor(item.Original), 32), name, size), 8).Tapped(() => _ = RestoreOne(item)));
        }
    }

    private async Task RestoreOne(RecycleBin.Entry item)
    {
        try
        {
            // Off the screen's thread: putting back a big video copies all of it.
            await Task.Run(() => RecycleBin.Restore(item.Original));
        }
        catch (IOException error)
        {
            await Say(Tr.T("{0} could not be put back: {1}", Path.GetFileName(item.Original), error.Message));
        }
        ShowBin();
    }

    private async Task RestoreAll()
    {
        var errors = await Task.Run(() =>
        {
            var problems = new List<string>();
            foreach (var item in RecycleBin.Items())
            {
                try
                {
                    RecycleBin.Restore(item.Original);
                }
                catch (IOException error)
                {
                    problems.Add($"{Path.GetFileName(item.Original)}: {error.Message}");
                }
            }
            return problems;
        });
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
