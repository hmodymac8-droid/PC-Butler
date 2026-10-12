// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>Every move and deletion, newest first, a page at a time, with Undo.</summary>
public sealed class HistoryPage : Page
{
    private readonly StatCard[] stats;
    private readonly TextBox search;
    private readonly TextBlock matchCount = Ui.Muted("", 12).Margin(12);
    private readonly ComboBox pageSize = new() { Width = 80 };
    private readonly Button previous;
    private readonly Button next;
    private readonly TextBlock pageLabel = new() { Width = 130, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView list;
    private int page; // zero-based

    public HistoryPage(MainWindow main) : base(main, "History", "Every change, all in one place.")
    {
        AddAction("Undo selected", UndoSelected, accent: true);
        AddAction("Clear history", ClearHistory);
        stats = AddStats(("Changes logged", "\uE81C"), ("Undo available", "\uE7A7"), ("Last activity", "\uE823"));

        var hint = Ui.Muted(Ui.T("Select rows with Ctrl or Shift (Ctrl+A for the whole page). Undo puts moved files back "
                                 + "and brings deleted files out of the Recycle Bin."), 11);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(hint.Margin(0, 10).Dock(Dock.Bottom));

        var panel = new CardPanel("Recent activity", "\uE81C");
        search = Ui.Field(width: 220, hint: Ui.T("Search history…"));
        search.TextChanged += (_, _) => ShowPage(0);
        panel.Controls.Children.Add(search);
        panel.Controls.Children.Add(Ui.Button(Ui.T("Refresh"), Refresh).Margin(6));
        panel.Controls.Children.Insert(0, matchCount);

        // Pages keep the list fast and readable once the log holds thousands of rows.
        var footer = panel.AddFooter();
        foreach (var size in new[] { "50", "100", "250", "500" })
            pageSize.Items.Add(size);
        pageSize.SelectedItem = "100";
        pageSize.SelectionChanged += (_, _) => ShowPage(0);
        previous = Ui.Button(Ui.T("‹ Previous"), () => ShowPage(page - 1));
        next = Ui.Button(Ui.T("Next ›"), () => ShowPage(page + 1));
        footer.Children.Add(Ui.Row(0, Ui.Text(Ui.T("Rows per page")), pageSize.Margin(6, 0, 14), previous, pageLabel.Margin(6, 0, 6), next));

        var sortable = Ui.List(("File", 320, "FileCell"), ("When", 170), ("From", 170), ("To", 190), ("Status", 90));
        // Only one page is in the list, so sorting re-reads the log, orders all of it and starts at page 1.
        sortable.SortChanged += () => ShowPage(0);
        list = sortable;
        Ui.AttachFileMenu(list, row => History.CurrentPath(((MoveRef)row.Tag!).Move), Main.RefreshPreview);
        panel.SetBody(list);
        Children.Add(panel);
    }

    private void ClearHistory()
    {
        var count = History.Load().Sum(run => run.Moves.Count);
        if (count == 0)
        {
            Ui.Info("History", Ui.T("The history is already empty."));
            return;
        }
        if (!Ui.Ask("Clear history", Ui.T("Clear all {0} entries from the history?\n\nNo file is touched: everything stays where it is now. "
                                          + "But these changes can no longer be undone from here.", count), warning: true))
            return;
        History.Clear();
        page = 0;
        Refresh();
        Main.RefreshPreview();
    }

    /// <summary>Background runs change the log while the window is open.</summary>
    public override void Shown() => Refresh();

    private void ShowPage(int number)
    {
        page = number;
        Refresh();
    }

    public void Refresh()
    {
        var needle = search.Text.Trim().ToLowerInvariant();
        int total = 0, undoable = 0;
        var matches = new List<(MoveRef Ref, string[] Cells)>(); // every row that passes the search; only one page of them is shown
        foreach (var run in Enumerable.Reverse(History.Load()))
        {
            for (var index = 0; index < run.Moves.Count; index++)
            {
                var move = run.Moves[index];
                total++;
                if (History.CanUndo(move))
                    undoable++;
                var active = History.IsActive(move);
                var source = move[0];
                var sourceFolder = Path.GetDirectoryName(source) ?? "";
                var state = active ? "Moved" : move[2] switch
                {
                    "undone" => "Undone", "deleted" => "Deleted", "erased" => "Erased", "restored" => "Restored",
                    var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..] : other,
                };
                string destination;
                if (!active && move[2] == "deleted")
                    destination = Ui.T("Recycle Bin");
                else if (!active && move[2] == "erased")
                    destination = Ui.T("Deleted for good");
                else
                    destination = move[1].Length > 0 ? Path.GetDirectoryName(move[1]) ?? "" : sourceFolder;
                string[] cells = [run.Time, sourceFolder, destination, Ui.T(state)];
                if (needle.Length > 0 && !$"{Path.GetFileName(source)} {string.Join(" ", cells)}".ToLowerInvariant().Contains(needle))
                    continue;
                matches.Add((new MoveRef(run.Time, index, move), cells));
            }
        }

        if (list is SortableList { SortColumn: >= 0 } sorted)
        {
            // Order every match, not just the page on screen. OrderBy keeps equal rows newest first.
            string Key((MoveRef Ref, string[] Cells) match) =>
                sorted.SortColumn == 0 ? Path.GetFileName(match.Ref.Move[0]) : match.Cells[sorted.SortColumn - 1];
            var comparer = Comparer<string>.Create(Ui.SmartCompare);
            matches = (sorted.SortDescending ? matches.OrderByDescending(Key, comparer) : matches.OrderBy(Key, comparer)).ToList();
        }

        var size = int.Parse((string)pageSize.SelectedItem);
        var pages = Math.Max(1, (matches.Count + size - 1) / size);
        page = Math.Clamp(page, 0, pages - 1);
        list.ItemsSource = matches.Skip(page * size).Take(size).Select(match =>
        {
            var move = match.Ref.Move;
            var folder = Path.GetFileName(Path.GetDirectoryName(move[1].Length > 0 ? move[1] : move[0]) ?? "");
            var active = History.IsActive(move);
            return new Row { Name = Path.GetFileName(move[0]), Tag = match.Ref, Cells = match.Cells, Dim = !active }
                .WithTile(Path.GetExtension(move[0]), folder, faded: !active);
        }).ToList();
        pageLabel.Text = Ui.T("Page {0} of {1}", page + 1, pages);
        previous.IsEnabled = page > 0;
        next.IsEnabled = page < pages - 1;
        matchCount.Text = needle.Length > 0 ? Ui.T("{0} match(es)", matches.Count) : "";
        stats[0].Set(total.ToString(), "", Ui.T("Every action leaves a trace"));
        stats[1].Set(undoable.ToString(), "", Ui.T("Restore files to where they were"));
        var latest = matches.Select(match => match.Ref.RunTime).DefaultIfEmpty().Max(StringComparer.Ordinal);
        stats[2].Set(latest is { Length: >= 16 } ? latest[11..16] : Ui.T("None"), latest is { Length: >= 10 } ? latest[..10] : "",
                     Ui.T("Most recent change"));
    }

    private void UndoSelected()
    {
        var selection = Ui.Selected(list).Select(row => (MoveRef)row.Tag!).Where(item => History.CanUndo(item.Move))
                          .Select(item => (item.RunTime, item.Index)).ToList();
        if (selection.Count == 0)
        {
            Ui.Info("Undo", Ui.T("Select one or more rows with the status Moved or Deleted."));
            return;
        }
        if (!Ui.Ask("Undo", Ui.T("Undo {0} change(s)?", selection.Count)))
            return;
        var (count, errors) = Sorter.UndoMoves(selection);
        Main.RefreshPreview();
        Ui.ReportResult(Ui.T("Restored {0} file(s).", count), errors);
    }
}
