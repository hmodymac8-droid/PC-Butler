// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// What automatic runs moved since the user last approved, with Undo all and Approve.
/// Only one is open at a time; it picks up further runs by itself.
/// </summary>
public sealed class ReportWindow : Window
{
    private readonly TextBlock heading = Ui.Text("", 27, weight: FontWeights.Bold);
    private readonly Button undoButton;
    private readonly ListView list;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<MoveRef> pending = [];
    private string shownKey = "";

    public ReportWindow()
    {
        Ui.StyleWindow(this);
        Title = Ui.T($"{AppInfo.Name}: files were moved");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(900, area.Width - 40);
        Height = Math.Min(620, area.Height - 40);
        MinWidth = Math.Min(660, area.Width - 40);
        MinHeight = Math.Min(420, area.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var page = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4).Dock(Dock.Top));
        heading.FontFamily = Ui.DisplayFont;
        page.Children.Add(heading.Dock(Dock.Top));
        page.Children.Add(Ui.Muted(Ui.T("Approve to keep them where they are now, or Undo to put them all back. Right-click a file for more."))
                            .Margin(0, 4, 0, 16).Dock(Dock.Top));
        // The buttons come before the list, so a small window shrinks the list, not them.
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Ui.Button(Ui.T("Approve"), Approve, accent: true).Dock(Dock.Right));
        undoButton = Ui.Button(Ui.T("Undo all"), Undo).Margin(0, 0, 6, 0).Dock(Dock.Right);
        buttons.Children.Add(undoButton);
        page.Children.Add(buttons.Dock(Dock.Bottom));

        var panel = new CardPanel("Moved files", "\uE8B7");
        list = Ui.List(("File", 360, "FileCell"), ("Moved to", 300), ("When", 170));
        Ui.AttachFileMenu(list, row => ((MoveRef)row.Tag!).Move[1], () => Refresh(force: true));
        panel.SetBody(list);
        page.Children.Add(panel);
        Content = page;

        Refresh(force: true);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        // Come to the front once, without staying above other windows.
        Topmost = true;
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
    }

    private void Refresh(bool force = false)
    {
        var now = History.PendingMoves();
        var key = string.Join("\n", now.Select(item => $"{item.RunTime}|{item.Index}|{string.Join("|", item.Move)}"));
        if (!force && key == shownKey)
            return;
        shownKey = key;
        pending = now;
        list.ItemsSource = pending.Select(item => new Row
        {
            Name = Path.GetFileName(item.Move[0]), Tag = item,
            Cells = [Path.GetDirectoryName(item.Move[1]) ?? "", item.RunTime],
        }.WithTile(Path.GetExtension(item.Move[0]), Path.GetFileName(Path.GetDirectoryName(item.Move[1]) ?? ""))).ToList();
        heading.Text = pending.Count > 0 ? Ui.T("{0} file(s) were moved automatically", pending.Count) : Ui.T("Nothing is waiting for review");
        undoButton.IsEnabled = pending.Count > 0;
    }

    private void Approve()
    {
        History.ApprovePending();
        Close();
    }

    private void Undo()
    {
        var (count, errors) = Sorter.UndoMoves(pending.Select(item => (item.RunTime, item.Index)).ToList());
        History.ApprovePending();
        App.Current.Workspace?.RefreshPreview();
        Ui.ReportResult(Ui.T("Restored {0} file(s). They will not be moved again automatically.", count), errors);
        Close();
    }
}
