// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The drop zone: a small tile that stays on top of other windows. Drag files or folders
/// onto it and choose what PC Butler does with them: sort, vault, shred or recycle. It
/// can be dragged anywhere and hidden from its right-click menu or the tray menu.
/// </summary>
public sealed class DropZoneWindow : Window
{
    private readonly Border tile;
    private readonly TextBlock label;

    public DropZoneWindow()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Drop zone");
        Width = Height = 112;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        var area = SystemParameters.WorkArea;
        Left = Tr.IsArabic ? area.Left + 24 : area.Right - Width - 24;
        Top = area.Bottom - Height - 24;

        label = Ui.Text(Ui.T("Drop here"), 12, "Muted");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var icon = new Image { Source = Ui.AppIcon(), Width = 52, Height = 52, Margin = new Thickness(0, 0, 0, 2) };
        tile = new Border
        {
            CornerRadius = new CornerRadius(18), Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1.5),
            Margin = new Thickness(6), Opacity = 0.92,
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } },
        };
        Content = tile;
        ToolTip = Ui.T("Drag files or folders here. Drag the tile to move it; right-click to hide it.");

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        MouseDoubleClick += (_, _) => App.Current.ShowMainWindow();
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item(Ui.T($"Open {AppInfo.Name}"), () => App.Current.ShowMainWindow()));
        menu.Items.Add(Ui.Item(Ui.T("Hide the drop zone"), () => App.Current.SetDropZone(false)));
        ContextMenu = menu;

        DragEnter += (_, e) => Highlight(e, true);
        DragOver += (_, e) => Highlight(e, true);
        DragLeave += (_, e) => Highlight(e, false);
        Drop += (_, e) =>
        {
            Highlight(e, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
                Dispatcher.BeginInvoke(() => Offer(dropped)); // after the drag has finished, so Explorer isn't kept waiting
        };
    }

    private void Highlight(DragEventArgs e, bool over)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        tile.BorderBrush = Ui.Paint(over && files ? "Primary" : "Line");
        tile.Opacity = over && files ? 1 : 0.92;
        label.Text = Ui.T(over && files ? "Let go" : "Drop here");
    }

    /// <summary>What to do with what was dropped: a menu right at the tile.</summary>
    private void Offer(string[] dropped)
    {
        var config = ConfigStore.Load();
        var root = config.WatchedFolders().FirstOrDefault() ?? "";
        var moves = Sorter.PlanDropped(config, dropped, root);
        var files = dropped.Where(File.Exists).ToList();
        var menu = new ContextMenu { PlacementTarget = tile, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var title = new MenuItem
        {
            Header = dropped.Length == 1 ? Path.GetFileName(dropped[0].TrimEnd('\\')) : Ui.T("{0} items", dropped.Length), IsEnabled = false,
        };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        var sort = Ui.Item(Ui.T("Sort ({0} file(s))", moves.Count), () =>
        {
            var (count, errors) = Sorter.MoveFiles(moves);
            App.Current.Workspace?.DataChanged();
            Flash(Ui.T("Sorted {0}", count), errors);
        });
        sort.IsEnabled = moves.Count > 0;
        menu.Items.Add(sort);
        var vault = Ui.Item(Ui.T("Lock in the Vault ({0} file(s))", files.Count), () =>
        {
            VaultInbox.Add(files);
            App.Current.ShowMainWindow();
        });
        vault.IsEnabled = files.Count > 0;
        menu.Items.Add(vault);
        menu.Items.Add(Ui.Item(Ui.T("Send to another PC (Courier)"), () =>
        {
            App.Current.ShowMainWindow();
            App.Current.Workspace?.SendWithPortal(dropped);
        }));
        menu.Items.Add(Ui.Item(Ui.T("Shred…"), () =>
        {
            App.Current.ShowMainWindow();
            App.Current.Workspace?.ShredLater(dropped);
        }));
        var recycle = Ui.Item(Ui.T("Move to the Recycle Bin ({0} file(s))", files.Count), () =>
        {
            var (count, errors) = Sorter.DeleteFiles(files);
            App.Current.Workspace?.DataChanged();
            Flash(Ui.T("Recycled {0}", count), errors);
        });
        recycle.IsEnabled = files.Count > 0;
        menu.Items.Add(recycle);
        menu.IsOpen = true;
    }

    /// <summary>Say what happened on the tile for a moment; problems get a proper message.</summary>
    private async void Flash(string text, List<string> errors)
    {
        if (errors.Count > 0)
        {
            Ui.ReportResult(text, errors);
            return;
        }
        label.Text = text;
        label.Foreground = Ui.Paint("Primary");
        await Task.Delay(2200);
        label.Text = Ui.T("Drop here");
        label.Foreground = Ui.Paint("Muted");
    }
}
