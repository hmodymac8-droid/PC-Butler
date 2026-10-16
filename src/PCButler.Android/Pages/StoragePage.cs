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

    private readonly Label phoneFree = new() { FontSize = 34, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
    private readonly Label phoneTotal = Theme.Faint("", 13);
    private readonly Label phoneUsed = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, VerticalOptions = LayoutOptions.Start };
    private readonly ContentView phoneBar = new();
    private readonly Label scanStatus = new() { FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End, LineBreakMode = LineBreakMode.HeadTruncation };
    private readonly Label folderTitle = new() { FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.HeadTruncation };
    private readonly Border binPill = Theme.Pill("", Theme.Amber, Theme.AmberSoft);
    private readonly View firstScan;
    private readonly Border resultsCard;
    private readonly View bigSection;
    private readonly Border bigCard;
    private readonly VerticalStackLayout folderList = new() { Spacing = 0 };
    private readonly Border upButton;
    private readonly VerticalStackLayout bigList = new() { Spacing = 0 };
    private readonly Grid binButtons;
    private readonly VerticalStackLayout binList = new() { Spacing = 6 };
    private readonly Label binText = Theme.Faint("", 12.5);
    private StorageResult? result;
    private StorageNode? shown;
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public StoragePage() : base("Storage")
    {
        HeaderRight.Content = Theme.IconButton("i_scan_line_w.png", () => _ = Scan());
        // How full the phone is: the free space big, the total and the share used next to it.
        var numbers = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        numbers.Add(new VerticalStackLayout { Spacing = 0, Children = { phoneFree, phoneTotal } }, 0);
        numbers.Add(phoneUsed, 1);
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 14, Children = { numbers, phoneBar } }, 20));
        Body.Children.Add(Theme.Section(Tr.T("Scan folders")));
        Body.Children.Add(FoldersChips("storage", () => { }));
        firstScan = Theme.CardOf(Theme.Empty("i_chart_pie_m.png", Tr.T("See what fills your phone"), Tr.T("These folders, and all the folders inside them, are measured."),
                                             Theme.Accent(Tr.T("Scan now"), () => _ = Scan(), "i_scan_line_k.png"))).Margin(0, 12, 0, 12);
        Body.Children.Add(firstScan);
        // Back up one folder: an arrow before the folder's name, there only inside a folder.
        upButton = Theme.IconButton("i_arrow_left_w.png", () =>
        {
            if (shown?.Parent is { } parent)
                ShowNode(parent);
        }, Theme.SurfaceRaised, 34);
        if (Tr.IsArabic)
            upButton.RotationY = 180;
        upButton.IsVisible = false;
        var head = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10, Padding = new Thickness(0, 0, 0, 6) };
        head.Add(upButton, 0);
        head.Add(folderTitle, 1);
        head.Add(scanStatus, 2);
        // The results and the large files show once something has been scanned.
        resultsCard = Theme.CardOf(new VerticalStackLayout { Spacing = 4, Children = { head, folderList } }, 16);
        resultsCard.IsVisible = false;
        Body.Children.Add(resultsCard.Margin(0, 12, 0, 12));
        bigSection = Theme.Section(Tr.T("Large & old files"));
        bigSection.IsVisible = false;
        Body.Children.Add(bigSection);
        bigCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Theme.Hint(Tr.T("Bigger than 50 MB and not changed for 3 months or more.")), bigList },
        }, 16);
        bigCard.IsVisible = false;
        Body.Children.Add(bigCard);
        var bins = binButtons = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        bins.Add(Theme.Tonal(Tr.T("Put all back"), () => _ = RestoreAll(), "i_undo_2_m.png"), 0);
        bins.Add(Theme.Danger(Tr.T("Empty"), () => _ = EmptyBin(), "i_trash_2_r.png"), 1);
        Body.Children.Add(Theme.Section(Tr.T("Recycle Bin"), binPill));
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Theme.Line3(Theme.Tile(Theme.BinTile), binText), binList, bins },
        }, 16));
    }

    protected override void Shown()
    {
        var stat = new global::Android.OS.StatFs(global::Android.OS.Environment.DataDirectory!.AbsolutePath);
        var (total, free) = (stat.TotalBytes, stat.AvailableBytes);
        var used = total > 0 ? (double)(total - free) / total : 0;
        phoneFree.Text = Tr.T("{0} free", Size(free));
        phoneTotal.Text = Tr.T("of {0}", Size(total));
        phoneUsed.Text = $"{Math.Round(used * 100)}%";
        phoneUsed.TextColor = used >= 0.9 ? Theme.Red : Theme.Primary;
        phoneBar.Content = Theme.Bar(used, used >= 0.9 ? Theme.Red : Theme.Primary, 12);
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
        firstScan.IsVisible = false;
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
        bigCard.IsVisible = bigSection.IsVisible = true;
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
        upButton.IsVisible = node.Parent is not null;
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
            bar.Margin = new Thickness(50, 0, 0, 0);
            var opens = child.IsFolder && child.Children.Count > 0;
            var end = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center, Children = { size } };
            if (opens)
                end.Children.Add(Theme.Icon(Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png", 16));
            var row = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(0, 10), Children = { Theme.Line3(Theme.Tile(tile, 38), title, end), bar } };
            if (opens)
                row.Tapped(() => ShowNode(child));
            if (folderList.Children.Count > 0)
                folderList.Children.Add(Theme.Divider());
            folderList.Children.Add(row);
        }
    }

    /// <summary>Big files nobody has changed for months, each with its own delete button.</summary>
    private void ShowBig()
    {
        bigList.Children.Clear();
        var files = result is null ? [] : Storage.LargeAndOld(result, Storage.BigFileMinimum, 3);
        if (files.Count == 0)
            bigList.Children.Add(Theme.Empty("i_circle_check_m.png", Tr.T("No files match.")));
        foreach (var file in files.Take(40))
        {
            var name = new Label { Text = file.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
            var detail = Theme.Faint($"{Size(file.Size)}  ·  {Tr.T("Last used")} {file.LastUsed:yyyy-MM-dd}", 12);
            detail.LineBreakMode = LineBreakMode.TailTruncation;
            var chosen = file;
            var trash = Theme.IconButton("i_trash_2_r.png", () => _ = DeleteBig(chosen), Theme.DangerBack, 38);
            var row = Theme.Line3(Theme.Tile(Theme.TileFor(file.Name), 38), Theme.TwoLines(name, detail), trash);
            row.Padding = new Thickness(0, 10);
            if (bigList.Children.Count > 0)
                bigList.Children.Add(Theme.Divider());
            bigList.Children.Add(row);
        }
    }

    private async Task DeleteBig(BigFile file)
    {
        if (!await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, below.", 1) + "\n\n" + file.Name + "\n\n" + DuplicatesPage.SpaceNote, Tr.T("Delete")))
            return;
        var (_, errors) = await Task.Run(() => DuplicatesPage.Bin([docs[file.Path]]));
        if (errors.Count == 0)
        {
            result?.BigFiles.RemoveAll(item => item.Path == file.Path);
            ActivityLog.Record("storage", "Large file deleted", $"{file.Name}  ·  {Size(file.Size)}");
        }
        else
            await Say(string.Join("\n", errors));
        ShowBig();
        ShowBin();
        Shown();
    }

    // --- PC Butler's Recycle Bin ---

    private void ShowBin()
    {
        var items = RecycleBin.Items();
        binText.Text = items.Count == 0 ? Tr.T("Empty. Files deleted in Duplicates or here wait in it until put back.")
            : Tr.T("{0} file(s), {1}. Tap one to put it back where it was.", items.Count, Size(items.Sum(item => item.Size))) + " " + Tr.T("Emptying it frees that space.");
        binText.VerticalOptions = LayoutOptions.Center;
        binPill.IsVisible = binButtons.IsVisible = items.Count > 0;
        Theme.SetPill(binPill, items.Count.ToString(), Theme.Amber, Theme.AmberSoft);
        binList.Children.Clear();
        foreach (var item in items.Take(30))
        {
            var name = new Label { Text = Path.GetFileName(item.Original), FontSize = 13.5, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.MiddleTruncation };
            var size = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, Children = { Theme.Faint(Size(item.Size), 12).Centered(), Theme.Icon("i_undo_2_m.png", 16) } };
            binList.Children.Add(Theme.BoxOf(Theme.Line3(Theme.Tile(Theme.TileFor(item.Original), 34), name, size), 10).Tapped(() => _ = RestoreOne(item)));
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
