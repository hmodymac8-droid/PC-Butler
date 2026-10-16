// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Cleaner: what can usually go, found in the folders the user gave PC Butler: app installers
/// (APKs), files of 100 MB or more, and downloads nobody has touched for three months; plus
/// PC Butler's own cache and Recycle Bin. Nothing is removed until it is ticked and confirmed.
/// Android doesn't let an app clear other apps' caches.
/// </summary>
public sealed class CleanerPage : AppPage
{
    private const long Big = 100L * 1024 * 1024;
    private readonly Label total = new() { FontSize = 36, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace" };
    private readonly Label status = Theme.Faint("", 13);
    private readonly Button scan;
    private readonly Button remove;
    private readonly View listSection;
    private readonly VerticalStackLayout list = new() { Spacing = 0 };
    private readonly Label ownText = Theme.Faint("", 12.5);
    private readonly Button ownButton;
    private readonly List<(Doc File, string Why)> found = [];
    private readonly HashSet<Doc> ticked = [];

    public CleanerPage() : base("Cleaner", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("Review before removing"), 13));
        scan = Theme.Accent(Tr.T("Scan files"), () => _ = Scan(), "i_search_k.png");
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Theme.Faint(Tr.T("Potential space"), 12.5), total, status, scan.Margin(0, 8, 0, 0) },
        }, 20));
        Body.Children.Add(Theme.Hint(Tr.T("Installers (APKs), files of 100 MB or more, and downloads untouched for 3 months, in the folders you chose. Android doesn't let an app clear other apps' cache.")));
        listSection = Theme.Section(Tr.T("Choose files"));
        listSection.IsVisible = false;
        Body.Children.Add(listSection);
        Body.Children.Add(list);
        remove = Theme.Danger(Tr.T("Remove selected"), () => _ = Remove(), "i_trash_2_r.png");
        remove.IsVisible = false;
        Body.Children.Add(remove.Margin(0, 4, 0, 0));
        // PC Butler's own leftovers: its cache and its Recycle Bin.
        ownButton = Theme.Tonal(Tr.T("Clear"), () => _ = ClearOwn(), "i_trash_2_m.png");
        Body.Children.Add(Theme.Section(Tr.T("PC Butler's own files")));
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Theme.Line3(Theme.Tile(("i_smartphone_m.png", Theme.PrimarySoft)), ownText), ownButton },
        }, 16));
        total.Text = "—";
    }

    protected override void Shown() => ShowOwn();

    private static long CacheSize()
    {
        try
        {
            return new DirectoryInfo(FileSystem.CacheDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void ShowOwn()
    {
        var cache = CacheSize();
        var bin = RecycleBin.Items().Sum(item => item.Size);
        ownText.Text = Tr.T("Cache {0}  ·  Recycle Bin {1}", Size(cache), Size(bin));
        ownText.VerticalOptions = LayoutOptions.Center;
        ownButton.IsVisible = cache + bin > 0;
    }

    private async Task ClearOwn()
    {
        var bin = RecycleBin.Items();
        if (!await Ask(bin.Count == 0 ? Tr.T("Clear PC Butler's cache?")
                                       : Tr.T("Clear PC Butler's cache, and delete the {0} file(s) in its Recycle Bin for good? This cannot be undone.", bin.Count), Tr.T("Clear")))
            return;
        var freed = CacheSize() + bin.Sum(item => item.Size);
        await Task.Run(() =>
        {
            foreach (var entry in new DirectoryInfo(FileSystem.CacheDirectory).EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry is DirectoryInfo folder)
                        folder.Delete(true);
                    else
                        entry.Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // in use: it goes next time
                }
            }
            RecycleBin.Empty();
        });
        ActivityLog.Record("cleaner", "PC Butler's cache cleared", Size(freed));
        ShowOwn();
    }

    private async Task Scan()
    {
        if (!ToolKit.HasFolders)
        {
            await Say(Tr.T("Add a folder on the Sorter or Storage page first: Android only lets PC Butler see folders you pick."));
            return;
        }
        scan.IsEnabled = false;
        status.Text = Tr.T("Looking for files…");
        List<Doc> files;
        try
        {
            files = await Task.Run(() => ToolKit.AllFiles(CancellationToken.None));
        }
        finally
        {
            scan.IsEnabled = true;
        }
        var old = DateTime.Now.AddMonths(-3);
        found.Clear();
        ticked.Clear();
        foreach (var file in files.OrderByDescending(file => file.Size))
        {
            var why = ToolKit.Kind(file.Name) == "apk" ? Tr.T("Installer")
                : file.Size >= Big ? Tr.T("Large file")
                : file.Path.Contains("Download", StringComparison.OrdinalIgnoreCase) && file.Modified < old ? Tr.T("Old download")
                : null;
            if (why is not null && file.CanDelete)
                found.Add((file, why));
        }
        scan.Text = Tr.T("Scan again");
        status.Text = found.Count == 0 ? "" : Tr.T("{0} file(s) found", found.Count);
        total.Text = Size(found.Sum(item => item.File.Size));
        Show();
    }

    private void Show()
    {
        list.Children.Clear();
        listSection.IsVisible = true;
        if (found.Count == 0)
            list.Children.Add(Theme.CardOf(Theme.Empty("i_circle_check_m.png", Tr.T("All clear"), Tr.T("Nothing to clean in your folders."))));
        var card = new VerticalStackLayout { Spacing = 0 };
        foreach (var (file, why) in found.Take(150))
        {
            var mark = new Border { WidthRequest = 22, HeightRequest = 22, StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(), StrokeThickness = 2, VerticalOptions = LayoutOptions.Center };
            void Paint()
            {
                var on = ticked.Contains(file);
                mark.Stroke = on ? Theme.Red : Theme.Muted;
                mark.BackgroundColor = on ? Theme.Red : Colors.Transparent;
                mark.Content = on ? Theme.Icon("i_check_k.png", 13) : null;
            }
            Paint();
            var row = ToolKit.FileRow(file, new HorizontalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center, Children = { Theme.Pill(why, Theme.Amber, Theme.AmberSoft), mark } });
            row.Padding = new Thickness(16, 10);
            row.Tapped(() =>
            {
                if (!ticked.Add(file))
                    ticked.Remove(file);
                Paint();
                Count();
            });
            if (card.Children.Count > 0)
                card.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
            card.Children.Add(row);
        }
        if (found.Count > 0)
            list.Children.Add(Theme.CardOf(card, 0));
        Count();
    }

    private void Count()
    {
        remove.IsVisible = found.Count > 0;
        remove.IsEnabled = ticked.Count > 0;
        remove.Text = ticked.Count == 0 ? Tr.T("Tick a file first") : Tr.T("Remove {0} · frees {1}", ticked.Count, Size(ticked.Sum(file => file.Size)));
    }

    private async Task Remove()
    {
        var chosen = ticked.ToList();
        if (chosen.Count == 0 || !await Ask(Tr.T("Delete {0} file(s) for good? This frees {1} at once, and cannot be undone.", chosen.Count, Size(chosen.Sum(file => file.Size))), Tr.T("Delete")))
            return;
        var (count, freed, errors) = await Task.Run(() =>
        {
            var (done, bytes, problems) = (0, 0L, new List<string>());
            foreach (var file in chosen)
            {
                try
                {
                    if (!Folders.Delete(file.Uri))
                        throw new IOException(Tr.T("the folder's app doesn't let it be removed"));
                    (done, bytes) = (done + 1, bytes + file.Size);
                }
                catch (Exception error) when (error is IOException or Java.Lang.Exception)
                {
                    problems.Add($"{file.Name}: {error.Message}");
                }
            }
            return (done, bytes, problems);
        });
        if (count > 0)
            ActivityLog.Record("cleaner", "Files removed", Tr.T("{0} file(s), {1}", count, Size(freed)));
        found.RemoveAll(item => chosen.Contains(item.File) && !errors.Any(error => error.StartsWith(item.File.Name + ":", StringComparison.Ordinal)));
        ticked.Clear();
        total.Text = Size(found.Sum(item => item.File.Size));
        status.Text = Tr.T("{0} freed", Size(freed));
        if (errors.Count > 0)
            await Say(string.Join("\n", errors.Take(6)));
        Show();
    }
}
