// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The list the shortcut opens, beside the mouse: type to search, Enter (or a double-click)
/// to paste into the program that was in front, Delete to remove, Ctrl+P to pin, Esc to close.
/// A right-click on an item offers the rest: copy, search Google, tag, open its link.
/// </summary>
public sealed class ClipboardPicker : Window
{
    private readonly ClipboardManager manager;
    private readonly IntPtr target;
    private readonly TextBox search;
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock empty = Ui.Muted("", 13, wrap: true);
    private readonly Border tagBar;
    private readonly TextBlock tagFor = Ui.Muted("", 11.5);
    private readonly TextBox tagBox;
    private ClipEntry? tagging;
    private bool closing;
    /// <summary>True while its right-click menu or a dialog of its own is open: losing focus to them doesn't close the list.</summary>
    private int holds;
    private bool holding => holds > 0;

    public ClipboardPicker(ClipboardManager manager, IntPtr target)
    {
        this.manager = manager;
        this.target = target;
        Ui.StyleWindow(this);
        Title = Ui.T("Clipboard history");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 440;
        Height = 520;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        search = Ui.Field(hint: Ui.T("Search the clipboard history…"));
        search.TextChanged += (_, _) => Fill();
        var layout = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Ui.Glyph("\uE77F", 16, "Primary").Margin(0, 0, 10, 0).Dock(Dock.Left));
        head.Children.Add(search);
        layout.Children.Add(head.Dock(Dock.Top));
        layout.Children.Add(Ui.Muted(Ui.T("Enter: paste  ·  Delete: remove  ·  Ctrl+P: pin  ·  Right-click: more  ·  Esc: close"), 11.5)
                              .Margin(0, 8, 0, 0).Dock(Dock.Bottom));
        // Typing a new tag, at the foot of the list.
        tagBox = Ui.Field(hint: Ui.T("work, address…"));
        var add = Ui.Button(Ui.T("Add"), SaveTag, accent: true);
        var tagRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        tagRow.Children.Add(add.Margin(6, 0, 0, 0).Dock(Dock.Right));
        tagRow.Children.Add(tagBox);
        tagBar = new Border
        {
            Background = Ui.Paint("Secondary"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed, Child = new StackPanel { Children = { tagFor, tagRow } },
        };
        layout.Children.Add(tagBar.Dock(Dock.Bottom));
        layout.Children.Add(empty.Dock(Dock.Top));
        layout.Children.Add(list);
        Content = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = layout,
        };

        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem)
                Pick();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (!holding)
                CloseOnce();
        };
        // Right-click: the item under the mouse is chosen first, then its menu opens.
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem item)
                item.IsSelected = true;
        };
        list.ContextMenuOpening += (_, e) =>
        {
            if (Selected is not { } entry)
            {
                e.Handled = true;
                return;
            }
            var menu = ClipboardMenu.For(entry, this, Pick, () => StartTag(entry), Hold);
            menu.Opened += (_, _) => holds++;
            menu.Closed += (_, _) =>
            {
                holds = Math.Max(0, holds - 1);
                // Back to the list (a menu item that opened the browser has taken the focus away for good),
                // unless a question it asked is still up.
                if (!holding && IsVisible && !closing)
                    Activate();
            };
            list.ContextMenu = menu;
        };
        Loaded += (_, _) =>
        {
            PlaceNearMouse();
            search.Focus();
        };
        ClipboardHistory.Changed += Fill;
        Closed += (_, _) => ClipboardHistory.Changed -= Fill;
        Fill();
    }

    private void Fill()
    {
        var chosen = (list.SelectedItem as ListBoxItem)?.Tag as ClipEntry;
        list.Items.Clear();
        foreach (var entry in ClipboardHistory.Ordered(search.Text.Trim()))
            list.Items.Add(new ListBoxItem { Content = ItemView(entry), Tag = entry, Padding = new Thickness(6, 6, 6, 6) });
        empty.Text = list.Items.Count > 0 ? ""
            : ClipboardHistory.Entries.Count == 0 ? Ui.T("Nothing copied yet. Copy something, then open this list again.")
            : Ui.T("Nothing matches.");
        empty.Visibility = empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var index = chosen is null ? 0 : list.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag == chosen);
        list.SelectedIndex = list.Items.Count == 0 ? -1 : Math.Max(0, index);
    }

    /// <summary>One item: its kind, its first line (or a small picture), and when it was copied.</summary>
    public static FrameworkElement ItemView(ClipEntry entry)
    {
        var row = new DockPanel();
        var glyph = entry.Kind switch { "files" => "\uE8B7", "image" => "\uEB9F", _ => "\uE8C8" };
        row.Children.Add(Ui.Glyph(entry.Pinned ? "\uE718" : glyph, 14, entry.Pinned ? "Primary" : "Muted")
                           .Margin(0, 2, 10, 0).Dock(Dock.Left));
        row.Children.Add(Ui.Muted(When(entry.Time), 11).Margin(10, 2, 0, 0).Dock(Dock.Right));
        if (entry.Kind == "image" && System.IO.File.Exists(entry.ImageFile))
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.DecodePixelHeight = 120;
            picture.UriSource = new Uri(entry.ImageFile);
            picture.EndInit();
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new Image { Source = picture, MaxHeight = 60, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform },
                    Ui.Muted(entry.Preview, 11),
                },
            });
        }
        else
        {
            var text = Ui.Text(entry.Preview, 13);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.MaxHeight = 40;
            text.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(text);
        }
        if (entry.Tags.Count > 0)
        {
            // Its tags, under what it shows.
            var body = (FrameworkElement)row.Children[^1];
            row.Children.Remove(body);
            var tags = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var tag in entry.Tags)
            {
                tags.Children.Add(new Border
                {
                    Child = Ui.Text("#" + tag, 10.5, "Primary"), Background = Ui.Paint("PrimaryTint"), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 2),
                });
            }
            row.Children.Add(new StackPanel { Children = { body, tags } });
        }
        row.ToolTip = entry.Kind == "text" && entry.Text.Length > 0 ? entry.Text[..Math.Min(entry.Text.Length, 1500)] : null;
        return row;
    }

    /// <summary>"now", "5 min", "14:02", or a date for older items.</summary>
    public static string When(DateTime time)
    {
        var since = DateTime.Now - time;
        if (since.TotalMinutes < 1)
            return Ui.T("now");
        if (since.TotalHours < 1)
            return Ui.T("{0} min", (int)since.TotalMinutes);
        return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("yyyy-MM-dd");
    }

    private ClipEntry? Selected => (list.SelectedItem as ListBoxItem)?.Tag as ClipEntry;

    /// <summary>A question of PC Butler's is up: the list stays open meanwhile, and comes back to the front after.</summary>
    private void Hold(bool on)
    {
        holds = Math.Max(0, holds + (on ? 1 : -1));
        if (!holding && IsVisible && !closing)
            Activate();
    }

    private void StartTag(ClipEntry entry)
    {
        tagging = entry;
        tagFor.Text = Ui.T("New tag for: {0}", entry.Preview.Length > 50 ? entry.Preview[..50] + "…" : entry.Preview);
        tagBox.Text = "";
        tagBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => tagBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SaveTag()
    {
        if (tagging is not null && tagBox.Text.Trim().Length > 0)
            ClipboardMenu.AddTags(tagging, tagBox.Text);
        EndTag();
    }

    private void EndTag()
    {
        tagging = null;
        tagBar.Visibility = Visibility.Collapsed;
        search.Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // While a tag is being typed, Enter adds it and Esc puts the field away; the rest is ordinary typing.
        if (tagBox.IsKeyboardFocused)
        {
            if (e.Key == Key.Enter)
                SaveTag();
            else if (e.Key == Key.Escape)
                EndTag();
            else
                return;
            e.Handled = true;
            return;
        }
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                break;
            case Key.Enter:
                Pick();
                break;
            case Key.Down or Key.Up when list.Items.Count > 0:
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                break;
            case Key.Delete when Selected is { } entry && (search.Text.Length == 0 || !search.IsKeyboardFocused):
                ClipboardMenu.Delete([entry], Hold);
                break;
            case Key.P when control && Selected is { } entry:
                ClipboardHistory.SetPinned(entry, !entry.Pinned);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Pick()
    {
        if (Selected is not { } entry)
            return;
        CloseOnce();
        manager.Paste(entry, target);
    }

    private void CloseOnce()
    {
        if (closing)
            return;
        closing = true;
        Close();
    }

    /// <summary>Beside the mouse, kept inside the screen it is on.</summary>
    private void PlaceNearMouse()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(point.X + 8, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(point.Y + 8, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}
