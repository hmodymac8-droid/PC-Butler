// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCButler.Core;

namespace PCButler;

/// <summary>The files due to move, where each will go, and the buttons that move them.</summary>
public sealed class SorterPage : Page
{
    private readonly Button moveButton;
    private readonly StatCard[] stats;
    private readonly CardPanel panel;
    private readonly TextBox search;
    private readonly CheckBox showAll;
    private readonly TextBlock summary = Ui.Muted("");
    private readonly ListView list;
    private List<PlannedMove> planned = []; // every planned move; the list shows those matching the search
    private readonly HashSet<string> excluded = []; // files taken off the list with Exclude, until the next refresh

    /// <summary>How many files the list shows.</summary>
    public int Count => list.Items.Count;

    public SorterPage(MainWindow main) : base(main, "Let’s tidy things up.", "A little organization. A lot more peace of mind.")
    {
        moveButton = AddAction("Organize files", Move, accent: true);
        // What automatic sorting moved, to approve or undo.
        AddAction("Review moved files", () => App.Current.ShowReport());
        stats = AddStats(("Ready to organize", "\uE8A5"), ("Space to sort", "\uEDA2"), ("Destination folders", "\uE8B7"));

        var note = Ui.Row(6, Ui.Glyph("\uE72E", 11), Ui.Muted(Ui.T("\uE72E  Your files stay on your device. Every move can be undone.").Replace("\uE72E", "").Trim(), 11));
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 12, 0, 0);
        Children.Add(note.Dock(Dock.Bottom));

        panel = new CardPanel("Downloads", "\uE8B7");
        search = Ui.Field(width: 200, hint: Ui.T("Search files…"));
        search.TextChanged += (_, _) => Render();
        // Off: only files old enough to move. On: every file that has a destination,
        // so the user can pick new ones to sort by hand.
        showAll = Ui.Switch(Ui.T("Show all files"), false, _ => Refresh()).Margin(16, 0, 16);
        panel.Controls.Children.Add(search);
        panel.Controls.Children.Add(showAll);
        panel.Controls.Children.Add(Ui.Button(Ui.T("Refresh"), Refresh));

        var footer = panel.AddFooter();
        footer.Children.Add(summary);
        footer.Children.Add(Ui.Button(Ui.T("Move selected"), MoveSelected).Dock(Dock.Right));
        footer.Children.Add(Ui.Button(Ui.T("Exclude"), ExcludeSelected).Margin(0, 0, 6, 0).Dock(Dock.Right));
        footer.Children.Add(Ui.Button(Ui.T("Undo last move"), Undo).Margin(0, 0, 6, 0).Dock(Dock.Right));
        footer.Children.Add(Ui.Button(Ui.T("Sort all now"), SortAllNow).Margin(0, 0, 6, 0).Dock(Dock.Right));

        list = Ui.List(("File name", 380, "FileCell"), ("Age", 90), ("Size", 100), ("Destination", 220), ("In", 120));
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
                ExcludeSelected();
        };
        Ui.AttachFileMenu(list, row => ((PlannedMove)row.Tag!).Source, Main.RefreshPreview,
                          ("Move only the selected file(s) now", _ => MoveSelected()), ("Never move this file", NeverMove));
        panel.SetBody(list);
        Children.Add(panel);
    }

    public void Refresh()
    {
        planned = Sorter.PlanMoves(Main.Config, everything: showAll.IsChecked == true);
        excluded.Clear();
        Render();
    }

    private void Render()
    {
        var needle = search.Text.Trim().ToLowerInvariant();
        var rows = new List<Row>();
        foreach (var move in planned)
        {
            if (excluded.Contains(move.Source) || (needle.Length > 0 && !move.Name.ToLowerInvariant().Contains(needle)))
                continue;
            var root = move.Root.TrimEnd('\\') + "\\";
            var destination = move.Folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? move.Folder[root.Length..] : move.Folder;
            rows.Add(new Row
            {
                Name = move.Name, Tag = move,
                Cells = [Ui.T("{0} days", move.Age.ToString("0")), Ui.FormatSize(move.Size), move.Delete ? Ui.T("Recycle Bin (deleted)") : destination,
                         Path.GetFileName(move.Root.TrimEnd('\\'))],
            }.WithTile(Path.GetExtension(move.Name), Path.GetFileName(move.Folder)));
        }
        list.ItemsSource = rows;
        UpdateSummary();
    }

    private List<PlannedMove> ListedMoves() => list.Items.Cast<Row>().Select(row => (PlannedMove)row.Tag!).ToList();

    private void UpdateSummary()
    {
        var moves = ListedMoves();
        var count = moves.Count;
        var size = moves.Sum(move => move.Size);
        if (showAll.IsChecked == true)
            summary.Text = count > 0 ? Ui.T("{0} file(s) of any age · {1} total", count, Ui.FormatSize(size)) : Ui.T("No files to sort");
        else
            summary.Text = count > 0 ? Ui.T("{0} file(s) ready to move · {1} total", count, Ui.FormatSize(size))
                                     : Ui.T("All tidied up! No files are old enough to move yet.");
        moveButton.Content = Ui.T("Organize {0} file(s)", count);
        moveButton.IsEnabled = count > 0;
        panel.SetCount(Ui.T("{0} file(s)", count));
        Main.SetBadge("preview", count > 0 ? count.ToString() : "");
        var parts = Ui.FormatSize(size).Split(' ');
        stats[0].Set(count.ToString(), Ui.T("files"), showAll.IsChecked == true ? Ui.T("Any age") : Ui.T("Older than {0} days", Main.Config.MinAgeDays));
        stats[1].Set(parts[0], parts[1], Ui.T("A cleaner Downloads folder"));
        stats[2].Set(moves.Where(move => !move.Delete).Select(move => move.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(), "",
                     Ui.T("Everything in the right place"));
    }

    /// <summary>A question about sorting, with a word on the files that will be deleted rather than moved.</summary>
    private static string WithDeletes(string question, IReadOnlyCollection<PlannedMove> moves) =>
        moves.Count(move => move.Delete) is var deleting and > 0
            ? question + "\n\n" + Ui.T("{0} of them go to the Recycle Bin instead, as their category deletes them. History can bring them back.", deleting)
            : question;

    /// <summary>What a run did, in words.</summary>
    private static string Done(SortResult result) =>
        result.Deleted > 0 ? Ui.T("Moved {0} file(s) and sent {1} to the Recycle Bin.", result.Count - result.Deleted, result.Deleted)
                           : Ui.T("Moved {0} file(s).", result.Count);

    private void ExcludeSelected()
    {
        foreach (var row in Ui.Selected(list))
            excluded.Add(((PlannedMove)row.Tag!).Source);
        Render();
    }

    private void NeverMove(string path)
    {
        Main.UpdateConfig(config => config.Ignore = [.. config.Ignore, Path.GetFileName(path)]);
        Main.Settings.ShowIgnore();
        Refresh();
    }

    private void Move()
    {
        var moves = ListedMoves();
        if (!Ui.Ask("Move files", WithDeletes(Ui.T("Move the {0} listed file(s)?", moves.Count), moves)))
            return;
        var result = Sorter.MoveFiles(moves);
        Main.RefreshPreview();
        Ui.ReportResult(Done(result), result.Errors);
    }

    /// <summary>Move just the highlighted rows and leave the rest for later.</summary>
    private void MoveSelected()
    {
        var moves = Ui.Selected(list).Select(row => (PlannedMove)row.Tag!).ToList();
        if (moves.Count == 0)
        {
            Ui.Info("Move selected", Ui.T("Select one or more files in the list first (Ctrl or Shift for several)."));
            return;
        }
        if (!Ui.Ask("Move files", WithDeletes(Ui.T("Move only the {0} selected file(s)?", moves.Count), moves)))
            return;
        var result = Sorter.MoveFiles(moves);
        Main.RefreshPreview();
        Ui.ReportResult(Done(result), result.Errors);
    }

    /// <summary>Sort one of the folders from Settings right away, by the usual rules.</summary>
    public void SortFolderNow(string folder)
    {
        var name = folder.Length > 0 ? folder : ConfigStore.DetectDownloadsFolder();
        var moves = Sorter.PlanMoves(Main.Config, onlyFolders: [folder]);
        if (moves.Count == 0)
        {
            Ui.Info("Sort folder", Ui.T("Nothing to sort in\n{0}\n\nNo files there are old enough yet.", name));
            return;
        }
        if (!Ui.Ask("Sort folder", WithDeletes(Ui.T("Move {0} file(s) in\n{1}\ninto their category folders?", moves.Count, name), moves)))
            return;
        var result = Sorter.MoveFiles(moves);
        Main.RefreshPreview();
        Ui.ReportResult(Done(result), result.Errors);
    }

    /// <summary>Move every file that has a destination, however new it is.</summary>
    private void SortAllNow()
    {
        var moves = Sorter.PlanMoves(Main.Config, everything: true);
        if (moves.Count == 0)
        {
            Ui.Info("Sort all", Ui.T("There are no files to sort."));
            return;
        }
        if (!Ui.Ask("Sort all", WithDeletes(Ui.T("Move all {0} file(s) now, including new ones and ones you put back with Undo?\n\n"
                                                 + "Files on the never-move list stay. You can undo this from History.", moves.Count), moves)))
            return;
        var result = Sorter.MoveFiles(moves);
        Main.RefreshPreview();
        Ui.ReportResult(Done(result), result.Errors);
    }

    private void Undo()
    {
        var run = History.LastActiveRun();
        if (run is null)
        {
            Ui.Info("Undo", Ui.T("There is no move to undo."));
            return;
        }
        var remaining = run.Moves.Count(History.IsActive);
        if (!Ui.Ask("Undo", Ui.T("Put back the {0} file(s) moved on {1}?", remaining, run.Time)))
            return;
        var (count, errors) = Sorter.UndoLast(run.Time);
        Main.RefreshPreview();
        Ui.ReportResult(Ui.T("Restored {0} file(s).", count), errors);
    }
}
