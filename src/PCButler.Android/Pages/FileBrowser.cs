// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// The phone's files, folder by folder, to tick the ones wanted. Used where the real file
/// matters (moving it into the Vault, sending it with Courier), which Android's own picker,
/// handing over a copy, can't do.
/// </summary>
public sealed class FileBrowser : ContentPage
{
    private readonly TaskCompletionSource<List<string>> result = new();
    private readonly HashSet<string> ticked = new(StringComparer.Ordinal);
    private readonly VerticalStackLayout list = new() { Spacing = 4 };
    private readonly Label where = Theme.Faint("");
    private readonly Button done;
    private string folder;

    private FileBrowser(string title, string start)
    {
        Title = title;
        folder = start;
        BackgroundColor = Theme.Background;
        done = Theme.Accent(Tr.T("Choose"), () => Close(ticked.ToList()));
        var up = Theme.Plain(Tr.T("Up"), () =>
        {
            if (Path.GetDirectoryName(folder) is { } parent && folder != AppPage.PhoneRoot)
                Show(parent);
        });
        var cancel = Theme.Plain(Tr.T("Cancel"), () => Close([]));
        var bottom = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 10, Padding = new Thickness(18, 10) };
        bottom.Add(up, 0);
        bottom.Add(cancel, 1);
        bottom.Add(done, 2);
        var layout = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
        layout.Add(new VerticalStackLayout { Padding = new Thickness(18, 24, 18, 8), Children = { Theme.Title(title, 22), where } }, 0, 0);
        layout.Add(new ScrollView { Content = list, Padding = new Thickness(18, 0) }, 0, 1);
        layout.Add(bottom, 0, 2);
        Content = layout;
        Show(start);
    }

    /// <summary>Ask for files; returns the ones ticked (none when cancelled).</summary>
    public static async Task<List<string>> Pick(INavigation navigation, string title, string? start = null)
    {
        var page = new FileBrowser(title, start ?? AppPage.PhoneRoot);
        await navigation.PushModalAsync(page);
        return await page.result.Task;
    }

    private async void Close(List<string> chosen)
    {
        result.TrySetResult(chosen);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (folder != AppPage.PhoneRoot && Path.GetDirectoryName(folder) is { } parent)
            Show(parent);
        else
            Close([]);
        return true;
    }

    private void Show(string path)
    {
        folder = path;
        where.Text = "/" + AppPage.Short(path);
        list.Children.Clear();
        IEnumerable<string> folders = [], files = [];
        try
        {
            folders = Directory.EnumerateDirectories(path).Where(item => !Path.GetFileName(item).StartsWith('.')).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            files = Directory.EnumerateFiles(path).Where(item => !Path.GetFileName(item).StartsWith('.')).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            list.Children.Add(Theme.Faint(Tr.T("This folder can't be read.")));
        }
        foreach (var sub in folders)
        {
            list.Children.Add(Theme.Row("📁  " + Path.GetFileName(sub), () => Show(sub)));
        }
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var (row, box) = AppPage.Tickable(info.Name, $"{AppPage.Size(info.Length)}  ·  {info.LastWriteTime:yyyy-MM-dd}", ticked.Contains(file));
            box.CheckedChanged += (_, e) =>
            {
                if (e.Value)
                    ticked.Add(file);
                else
                    ticked.Remove(file);
                done.Text = ticked.Count == 0 ? Tr.T("Choose") : Tr.T("Choose ({0})", ticked.Count);
            };
            list.Children.Add(row);
        }
        if (!folders.Any() && !files.Any())
            list.Children.Add(Theme.Faint(Tr.T("This folder is empty.")));
    }
}
