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
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public StoragePage() : base("Storage", "See what fills your phone. Decide what stays.")
    {
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 8, Children = { phoneText, phoneBar } }));
        Body.Children.Add(FoldersCard("storage", "These folders, and all the folders inside them, are measured.", () => { }));
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
                new HorizontalStackLayout { Spacing = 10, Children = { Theme.Accent(Tr.T("Scan"), () => _ = Scan()), upButton } },
                scanStatus, folderTitle, folderList,
            },
        }));
        bigDelete = Theme.Accent(Tr.T("Delete ticked"), () => _ = DeleteBig());
        bigDelete.IsEnabled = false;
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Theme.Title(Tr.T("Large & old files"), 18), Theme.Faint(Tr.T("Bigger than 50 MB and not changed for 3 months or more.")), bigList, bigDelete },
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
        var stat = new global::Android.OS.StatFs(global::Android.OS.Environment.DataDirectory!.AbsolutePath);
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
        var trees = Folders.For("storage");
        if (trees.Count == 0)
        {
            await Say(Tr.T("Choose a folder to measure first."));
            return;
        }
        var current = "";
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
            var (row, box) = Tickable(file.Name, $"{Size(file.Size)}  ·  {Tr.T("Last used")} {file.LastUsed:yyyy-MM-dd}  ·  {Path.GetDirectoryName(file.Path)}");
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
