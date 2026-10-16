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
    private readonly Label summary = new() { FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label summarySize = Theme.Faint("", 14);
    private readonly Label detail = Theme.Faint("");
    private readonly VerticalStackLayout groups = new() { Spacing = 10 };
    private readonly Entry age = new() { Keyboard = Keyboard.Numeric, WidthRequest = 80, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label lastRun = Theme.Faint("");
    private readonly Button undo;
    private readonly Button sort;
    private List<PhoneMove> planned = [];
    private bool busy, again;

    public SorterPage() : base("Sorter", "Your folders sorted by type, after the files have sat there a while.")
    {
        sort = Theme.Accent(Tr.T("Sort now"), () => _ = SortNow());
        Body.Children.Add(FoldersCard("sorter", "The loose files in these folders go into a folder for their type, inside each one.", () => _ = Plan()));
        var buttons = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Add(sort, 0);
        buttons.Add(Theme.Plain(Tr.T("Refresh"), () => _ = Plan()), 1);
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 4,
            Children = { summary, summarySize, detail, buttons },
        }));
        Body.Children.Add(groups);
        age.Unfocused += (_, _) => SaveAge();
        age.Completed += (_, _) => SaveAge();
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Theme.Title(Tr.T("Settings"), 18),
                new HorizontalStackLayout { Spacing = 10, Children = { Theme.Body(Tr.T("Move files older than (days)")).Margin(0, 10, 0, 0), age } },
                Theme.Faint(Tr.T("Give new files a little time before sorting. 0 sorts everything.")),
            },
        }));
        undo = Theme.Plain(Tr.T("Undo the last sort"), () => _ = Undo());
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 8, Children = { Theme.Title(Tr.T("History"), 18), lastRun, undo } }));
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
        summarySize.Text = planned.Count == 0 ? "" : Tr.T("{0} ready to sort", Size(planned.Sum(move => move.File.Size)));
        summarySize.IsVisible = summarySize.Text.Length > 0;
        detail.Text = trees.Count == 0 ? "" : Tr.T("Each file goes into the folder for its type, next to it.");
        detail.IsVisible = detail.Text.Length > 0;
        sort.IsEnabled = planned.Count > 0;
        groups.Children.Clear();
        foreach (var group in planned.GroupBy(move => move.Folder).OrderByDescending(group => group.Count()))
        {
            // One card a type: its tile and folder, a count, then the files with their sizes.
            var list = new VerticalStackLayout { Spacing = 0 };
            foreach (var move in group.Take(6))
            {
                var name = new Label { Text = move.File.Name, FontSize = 13.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center };
                var size = new Label { Text = Size(move.File.Size), FontSize = 12, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center };
                var row = Theme.Line3(null, name, size, 10);
                row.Padding = new Thickness(4, 9);
                list.Children.Add(new BoxView { HeightRequest = 1, Color = Theme.Line });
                list.Children.Add(row);
            }
            if (group.Count() > 6)
                list.Children.Add(Theme.Faint(Tr.T("…and {0} more", group.Count() - 6), 12.5).Margin(4, 6, 0, 0));
            var title = new Label { Text = group.Key, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
            var where = Theme.Faint($"→ {group.First().Root.Name}/{group.Key}" + (group.Select(move => move.Root.Path).Distinct().Count() > 1 ? " …" : ""), 12);
            var pill = Theme.Pill($"{group.Count()} · {Size(group.Sum(move => move.File.Size))}", Theme.Text, Theme.Secondary);
            groups.Children.Add(Theme.CardOf(new VerticalStackLayout
            {
                Spacing = 8,
                Children = { Theme.Line3(Theme.Tile(Theme.TileForCategory(group.Key)), Theme.TwoLines(title, where), pill), list },
            }, 16));
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
        undo.IsEnabled = last is not null;
    }

    private async Task Undo()
    {
        if (PhoneSorter.Last() is null || !await Ask(Tr.T("Put the files of the last sort back where they were?"), Tr.T("Undo")))
            return;
        var (count, errors) = await Task.Run(PhoneSorter.UndoLast);
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) put back.", count) : Tr.T("{0} file(s) put back. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        ShowHistory();
        await Plan();
    }
}
