// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// The Sorter on the phone: what has been sitting in the folders the user chose for a while
/// goes into category folders there (Pictures, Documents, Videos…), the same rules as on the
/// PC. Nothing moves until Sort now is pressed, and the last run can be undone.
/// </summary>
public sealed class SorterPage : AppPage
{
    private readonly Label summary = new() { FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label summarySize = Theme.Faint("", 13);
    private readonly Label detail = Theme.Faint("", 12.5);
    private readonly VerticalStackLayout groups = new() { Spacing = 0 };
    private readonly Label previewCount = Theme.Faint("", 12.5);
    private readonly Entry age = new DigitsEntry { WidthRequest = 70, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label lastRun = Theme.Faint("");
    private readonly Button undo;
    private readonly Button sort;
    private List<PhoneMove> planned = [];
    private bool busy, again;

    public SorterPage() : base("Sorter")
    {
        HeaderRight.Content = Theme.IconButton("i_refresh_cw_w.png", () => _ = Plan());
        sort = Theme.Accent(Tr.T("Sort now"), () => _ = SortNow(), "i_wand_sparkles_k.png");
        sort.VerticalOptions = LayoutOptions.Center;
        // The summary: how many files are ready, and Sort now next to it.
        var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 12 };
        top.Add(new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { summary, summarySize } }, 0);
        top.Add(sort, 1);
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 6, Children = { top, detail } }, 20));
        Body.Children.Add(Theme.Section(Tr.T("Folders")));
        Body.Children.Add(FoldersChips("sorter", () => _ = Plan()));
        Body.Children.Add(Theme.Section(Tr.T("Preview"), previewCount));
        Body.Children.Add(groups);
        // Settings and history, small, at the end.
        age.Unfocused += (_, _) => SaveAge();
        age.Completed += (_, _) => SaveAge();
        var ageBox = Theme.BoxOf(age, 0, Theme.SurfaceRaised);
        (ageBox.HeightRequest, ageBox.VerticalOptions) = (44, LayoutOptions.Center);
        undo = Theme.Tonal(Tr.T("Undo the last sort"), () => _ = Undo(), "i_undo_2_m.png");
        Body.Children.Add(Theme.Section(Tr.T("Settings")));
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Line3(null, Theme.Body(Tr.T("Move files older than (days)")).Centered(), ageBox),
                Theme.Hint(Tr.T("Give new files a little time before sorting. 0 sorts everything.")),
                Theme.Divider().Margin(0, 4, 0, 4),
                Theme.Line3(Theme.Icon("i_clock_g.png", 18), lastRun.Centered()),
                undo,
            },
        }));
    }

    protected override void Shown()
    {
        age.Text = ConfigStore.Load().MinAgeDays.ToString();
        ShowHistory();
        _ = Plan();
    }

    private void SaveAge()
    {
        if (!int.TryParse(age.Text, out var days) || days is < 0 or > 3650)
        {
            age.Text = ConfigStore.Load().MinAgeDays.ToString();
            return;
        }
        var config = ConfigStore.Load();
        if (config.MinAgeDays == days)
            return;
        config.MinAgeDays = days;
        ConfigStore.Save(config);
        _ = Plan();
    }

    private async Task Plan()
    {
        if (busy)
        {
            again = true; // a folder was added while looking: look once more after
            return;
        }
        busy = true;
        again = false;
        var trees = Folders.For("sorter");
        summary.Text = trees.Count == 0 ? Tr.T("Choose a folder to sort first.") : Tr.T("Looking at your folders…");
        try
        {
            var config = ConfigStore.Load();
            planned = trees.Count == 0 ? [] : await Task.Run(() => PhoneSorter.Plan(config, trees));
        }
        finally
        {
            busy = false;
        }
        if (again)
        {
            await Plan();
            return;
        }
        if (trees.Count > 0)
            summary.Text = planned.Count == 0 ? Tr.T("Nothing to sort right now.") : Tr.T("{0} file(s)", planned.Count);
        summary.FontSize = planned.Count == 0 ? 18 : 30;
        sort.IsVisible = trees.Count > 0;
        summarySize.Text = planned.Count == 0 ? "" : Tr.T("{0} ready to sort", Size(planned.Sum(move => move.File.Size)));
        summarySize.IsVisible = summarySize.Text.Length > 0;
        detail.Text = trees.Count == 0 ? "" : Tr.T("Each file goes into the folder for its type, next to it.");
        detail.IsVisible = detail.Text.Length > 0;
        sort.IsEnabled = planned.Count > 0;
        previewCount.Text = planned.Count == 0 ? "" : Tr.T("{0} file(s)", planned.Count);
        groups.Children.Clear();
        if (trees.Count > 0 && planned.Count == 0)
            groups.Children.Add(Theme.CardOf(Theme.Empty("i_sparkles_m.png", Tr.T("Nothing to sort right now."), Tr.T("Each file goes into the folder for its type, next to it."))));
        var first = true;
        foreach (var group in planned.GroupBy(move => move.Folder).OrderByDescending(group => group.Count()))
        {
            // One card a type: its tile and folder, a count; tapping it shows or hides the files.
            var list = new VerticalStackLayout { Spacing = 0, IsVisible = first };
            foreach (var move in group.Take(8))
            {
                var name = new Label { Text = move.File.Name, FontSize = 13.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center };
                var size = new Label { Text = Size(move.File.Size), FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center };
                var row = Theme.Line3(Theme.Icon(Theme.TileFor(move.File.Name).Icon.Replace("_m.png", "_g.png"), 16), name, size, 10);
                row.Padding = new Thickness(2, 9);
                list.Children.Add(Theme.Divider());
                list.Children.Add(row);
            }
            if (group.Count() > 8)
                list.Children.Add(Theme.Faint(Tr.T("…and {0} more", group.Count() - 8), 12.5).Margin(2, 6, 0, 0));
            var title = new Label { Text = $"{group.Key}  ·  {group.Count()}", FontSize = 15.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
            var where = Theme.Faint((Tr.IsArabic ? "← " : "→ ") + $"{group.First().Root.Name}/{group.Key}" + (group.Select(move => move.Root.Path).Distinct().Count() > 1 ? " …" : "")
                                    + $"  ·  {Size(group.Sum(move => move.File.Size))}", 12);
            var chevron = Theme.Icon(first ? "i_chevron_down_g.png" : Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png", 18);
            var head = Theme.Line3(Theme.Tile(Theme.TileForCategory(group.Key)), Theme.TwoLines(title, where), chevron);
            head.Tapped(() =>
            {
                list.IsVisible = !list.IsVisible;
                chevron.Source = list.IsVisible ? "i_chevron_down_g.png" : Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png";
            });
            groups.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 10, Children = { head, list } }, 16));
            first = false;
        }
    }

    private async Task SortNow()
    {
        if (planned.Count == 0 || busy)
            return;
        busy = true;
        try
        {
            var moves = planned;
            var (count, errors) = await Task.Run(() => PhoneSorter.Move(moves));
            if (count > 0)
                ActivityLog.Record("sorter", "Files sorted", Tr.T("{0} file(s)", count));
            await Say(errors.Count == 0 ? Tr.T("Sorted {0} file(s).", count) : Tr.T("Sorted {0} file(s). {1} could not be moved:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        }
        finally
        {
            busy = false;
        }
        ShowHistory();
        await Plan();
    }

    private void ShowHistory()
    {
        var last = PhoneSorter.Last();
        lastRun.Text = last is null ? Tr.T("Nothing sorted yet.") : Tr.T("Last sort: {0}, {1} file(s).", last.Time, last.Moves.Count);
        undo.IsEnabled = undo.IsVisible = last is not null;
    }

    private async Task Undo()
    {
        if (PhoneSorter.Last() is null || !await Ask(Tr.T("Put the files of the last sort back where they were?"), Tr.T("Undo")))
            return;
        var (count, errors) = await Task.Run(PhoneSorter.UndoLast);
        if (count > 0)
            ActivityLog.Record("sorter", "Sort undone", Tr.T("{0} file(s)", count));
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) put back.", count) : Tr.T("{0} file(s) put back. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        ShowHistory();
        await Plan();
    }
}
