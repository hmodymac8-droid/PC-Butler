// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>History: what PC Butler did on this phone, newest first, all of it or files or tools only.</summary>
public sealed class HistoryPage : AppPage
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["sorter"] = "i_wand_sparkles_m.png", ["duplicates"] = "i_copy_m.png", ["storage"] = "i_chart_pie_m.png", ["cleaner"] = "i_sparkles_m.png",
        ["courier"] = "i_send_m.png", ["apps"] = "i_app_window_m.png", ["benchmark"] = "i_gauge_m.png", ["clipboard"] = "i_clipboard_m.png",
    };
    private readonly VerticalStackLayout list = new() { Spacing = 0 };
    private readonly Label count = Theme.Faint("", 12.5);
    private int filter;

    public HistoryPage() : base("History", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("PC Butler activity only"), 13));
        HeaderRight.Content = Theme.IconButton("i_trash_2_w.png", () => _ = ClearAll());
        Body.Children.Add(Theme.Chips([Tr.T("All"), Tr.T("Files"), Tr.T("Tools")], 0, index => { filter = index; Show(); }));
        Body.Children.Add(Theme.Section(Tr.T("Recent activity"), count));
        Body.Children.Add(Theme.CardOf(list, 0));
    }

    protected override void Shown() => Show();

    private void Show()
    {
        var items = ActivityLog.Items().Where(item => filter == 0 || ActivityLog.FileKinds.Contains(item.Kind) == (filter == 1)).ToList();
        count.Text = items.Count.ToString();
        list.Children.Clear();
        if (items.Count == 0)
            list.Children.Add(Theme.Empty("i_history_m.png", Tr.T("No activity yet"), Tr.T("Your PC Butler actions appear here")));
        foreach (var item in items)
        {
            var title = new Label { Text = Tr.T(item.Text), FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
            var detail = Theme.Faint(item.Detail.Length > 0 ? $"{item.Detail}  ·  {When(item.Time)}" : When(item.Time), 12);
            var row = Theme.Line3(Theme.Tile((Icons.GetValueOrDefault(item.Kind, "i_history_m.png"), Theme.PrimarySoft), 40), Theme.TwoLines(title, detail));
            row.Padding = new Thickness(16, 12);
            if (list.Children.Count > 0)
                list.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
            list.Children.Add(row);
        }
    }

    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? Tr.T("Today {0}", time.ToString("t")) : time.Date == DateTime.Today.AddDays(-1) ? Tr.T("Yesterday {0}", time.ToString("t")) : time.ToString("g");

    private async Task ClearAll()
    {
        if (ActivityLog.Items().Count == 0 || !await Ask(Tr.T("Clear the activity history? Files and saved text are not touched."), Tr.T("Clear")))
            return;
        ActivityLog.Clear();
        Show();
    }
}
