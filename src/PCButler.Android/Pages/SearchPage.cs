// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Search: files by name (or folder) in every folder the user gave PC Butler, narrowed by kind.
/// The folders are read once when the page opens; typing then filters at once.
/// </summary>
public sealed class SearchPage : AppPage
{
    private static readonly (string Key, string Label)[] Kinds =
        [("", "All"), ("documents", "Documents"), ("images", "Photos"), ("videos", "Videos"), ("audio", "Music"), ("apk", "APKs"), ("archives", "Archives")];
    private readonly Entry query = new() { Placeholder = Tr.T("File name or folder…"), TextColor = Theme.Text, PlaceholderColor = Theme.Muted, FontSize = 15 };
    private readonly Label count = Theme.Faint("", 12.5);
    private readonly Label status = Theme.Faint("", 13);
    private readonly VerticalStackLayout results = new() { Spacing = 0 };
    private List<Doc> files = [];
    private string kind = "";
    private bool loaded;

    public SearchPage() : base("Search", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("Your accessible files"), 13));
        HeaderRight.Content = Theme.IconButton("i_refresh_cw_w.png", () => _ = Load());
        Body.Children.Add(Theme.Field(query, Theme.Icon("i_search_m.png", 20).Margin(0, 0, 10, 0)));
        query.TextChanged += (_, _) => Show();
        Body.Children.Add(Theme.Chips(Kinds.Select(item => Tr.T(item.Label)).ToList(), 0, index => { kind = Kinds[index].Key; Show(); }).Margin(0, 12, 0, 0));
        Body.Children.Add(status.Margin(4, 12, 4, 0));
        Body.Children.Add(Theme.Section(Tr.T("Results"), count));
        Body.Children.Add(Theme.CardOf(results, 0));
        Body.Children.Add(Theme.Hint(Tr.T("Only folders you allow are included.")));
    }

    protected override void Shown()
    {
        if (!loaded)
            _ = Load();
    }

    private async Task Load()
    {
        loaded = true;
        if (!ToolKit.HasFolders)
        {
            files = [];
            Show();
            return;
        }
        status.IsVisible = true;
        status.Text = Tr.T("Looking for files…");
        files = await Task.Run(() => ToolKit.AllFiles(CancellationToken.None));
        Show();
    }

    private void Show()
    {
        status.IsVisible = !ToolKit.HasFolders;
        status.Text = Tr.T("Add a folder on the Sorter or Storage page first: Android only lets PC Butler see folders you pick.");
        var needle = (query.Text ?? "").Trim();
        var found = files.Where(file => (kind.Length == 0 || ToolKit.Kind(file.Name) == kind)
                                        && (needle.Length == 0 || file.Path.Contains(needle, StringComparison.CurrentCultureIgnoreCase)))
                         .OrderByDescending(file => file.Modified).ToList();
        count.Text = found.Count.ToString("N0");
        results.Children.Clear();
        if (found.Count == 0)
            results.Children.Add(Theme.Empty("i_search_m.png", Tr.T("No matching files"), Tr.T("Try another name or file type")));
        foreach (var file in found.Take(200))
        {
            var row = ToolKit.FileRow(file, Theme.Icon(Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png", 16));
            row.Padding = new Thickness(16, 10);
            row.Tapped(() => _ = Details(file));
            if (results.Children.Count > 0)
                results.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
            results.Children.Add(row);
        }
        if (found.Count > 200)
            results.Children.Add(Theme.Faint(Tr.T("…and {0} more", found.Count - 200), 12.5).Margin(16, 8, 16, 12));
    }

    private async Task Details(Doc file)
    {
        string open = Tr.T("Open"), copy = Tr.T("Copy the location");
        var choice = await Choose(file.Name, [new SheetChoice("i_external_link_w.png", open), new SheetChoice("i_copy_w.png", copy)], Theme.TileFor(file.Name));
        if (choice == open && !ToolKit.Open(file))
            await Say(Tr.T("No app on this phone opens this file."));
        else if (choice == copy)
            await Clipboard.Default.SetTextAsync(file.Path);
    }
}
