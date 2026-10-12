// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCButler.Core;

namespace PCButler;

/// <summary>The clipboard manager: turn it on or off, its shortcut and limit, and the whole history to search, pin, copy or delete.</summary>
public sealed class ClipboardPage : Page
{
    private readonly ListBox list = new() { MinHeight = 260 };
    private readonly TextBox search;
    private readonly TextBlock count = Ui.Muted("", 12);
    private readonly Button pinButton;
    private readonly TextBox shortcut;
    private readonly TextBox limit;

    public ClipboardPage(MainWindow main) : base(main, "Clipboard", "Everything you copy, ready to paste again.")
    {
        AddAction("Open the list", () => App.Current.Clips.ShowPicker(), accent: true);
        var settings = main.Config.Clipboard;
        var page = new StackPanel();

        // --- settings: each applies at once ---
        var rows = new StackPanel();
        rows.Children.Add(Ui.SettingRow(Ui.T("Clipboard manager"),
                                         Ui.T("Keeps what you copy: text, files and pictures. Items that password managers mark as private are skipped."),
                                         Ui.Switch("", settings.Enabled, on => Change(config => config.Enabled = on)), first: true));

        shortcut = Ui.Field(settings.Shortcut, 160);
        shortcut.IsReadOnly = true;
        shortcut.IsReadOnlyCaretVisible = false;
        shortcut.ToolTip = Ui.T("Click here, then press the keys you want.");
        shortcut.PreviewKeyDown += RecordShortcut;
        rows.Children.Add(Ui.SettingRow(Ui.T("Shortcut to open the list"),
                                        Ui.T("Press it in any program to pick an item and paste it there. Click the box, then press the new keys."),
                                        Ui.Row(6, shortcut, Ui.Button(Ui.T("Default"), () => SetShortcut("Ctrl+Alt+V")))));

        limit = Ui.Field(settings.Limit.ToString(), 90);
        limit.LostFocus += (_, _) => SaveLimit();
        limit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                SaveLimit();
        };
        rows.Children.Add(Ui.SettingRow(Ui.T("History limit"),
                                        Ui.T("The oldest items go first once the limit is reached. Pinned items never count. From {0} to {1}.",
                                             ClipboardSettings.MinLimit, ClipboardSettings.MaxLimit),
                                        Ui.Row(8, limit, Ui.Muted(Ui.T("items")))));
        rows.Children.Add(Ui.SettingRow(Ui.T("Keep the history after a restart"),
                                        Ui.T("Off: the history lives only while PC Butler runs, and nothing is written to disk."),
                                        Ui.Switch("", settings.KeepOnDisk, on => Change(config => config.KeepOnDisk = on))));
        rows.Children.Add(Ui.SettingRow(Ui.T("Record pictures"), Ui.T("Screenshots and copied images, besides text and files."),
                                        Ui.Switch("", settings.Pictures, on => Change(config => config.Pictures = on))));
        rows.Children.Add(Ui.SettingRow(Ui.T("Paste right away"),
                                        Ui.T("Picking an item pastes it into the program you were in. Off: it is only copied, for you to paste."),
                                        Ui.Switch("", settings.PasteOnPick, on => Change(config => config.PasteOnPick = on))));
        var card = new CardPanel("Settings", "\uE713") { Margin = new Thickness(0, 0, 0, 18) };
        card.SetBody(rows);
        page.Children.Add(card);

        // --- the history ---
        search = Ui.Field(width: 260, hint: Ui.T("Search the clipboard history…"));
        search.TextChanged += (_, _) => Fill();
        list.SelectionMode = SelectionMode.Extended;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem)
                CopySelected();
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
                DeleteSelected();
        };
        list.SelectionChanged += (_, _) => ShowPinState();
        pinButton = Ui.Button(Ui.T("Pin"), PinSelected);
        var history = new CardPanel("History", "\uE81C");
        history.Controls.Children.Add(search);
        var body = new DockPanel();
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(Ui.Button(Ui.T("Copy"), CopySelected, accent: true));
        bar.Children.Add(pinButton.Margin(6, 0, 0, 0));
        bar.Children.Add(Ui.Button(Ui.T("Delete"), DeleteSelected).Margin(6, 0, 0, 0));
        bar.Children.Add(count.Margin(12, 0, 0, 0));
        bar.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory).Dock(Dock.Right));
        body.Children.Add(bar.Dock(Dock.Bottom));
        body.Children.Add(list);
        history.SetBody(body);
        page.Children.Add(history);

        Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        ClipboardHistory.Changed += () => Dispatcher.BeginInvoke(Fill);
        Fill();
    }

    /// <summary>Save a change and apply it to the running clipboard manager.</summary>
    private void Change(Action<ClipboardSettings> change)
    {
        Main.UpdateConfig(config => change(config.Clipboard));
        if (App.Current.Clips.Apply(Main.Config.Clipboard) is { } error)
            Ui.Warn("Clipboard", error);
    }

    private void RecordShortcut(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
            return;
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (key is Key.Back or Key.Delete)
                SetShortcut(""); // no shortcut
            return; // a key alone would take it from every program
        }
        SetShortcut(Shortcut.Format(Keyboard.Modifiers, key));
    }

    private void SetShortcut(string keys)
    {
        var before = Main.Config.Clipboard.Shortcut;
        shortcut.Text = keys;
        Main.UpdateConfig(config => config.Clipboard.Shortcut = keys);
        if (App.Current.Clips.Apply(Main.Config.Clipboard) is { } error)
        {
            Ui.Warn("Clipboard", error);
            shortcut.Text = before;
            Main.UpdateConfig(config => config.Clipboard.Shortcut = before);
            App.Current.Clips.Apply(Main.Config.Clipboard);
        }
    }

    private void SaveLimit()
    {
        var value = int.TryParse(limit.Text.Trim(), out var number) ? Math.Clamp(number, ClipboardSettings.MinLimit, ClipboardSettings.MaxLimit) : 500;
        limit.Text = value.ToString();
        if (value == Main.Config.Clipboard.Limit)
            return;
        var dropped = ClipboardHistory.Entries.Count(entry => !entry.Pinned) - value;
        if (dropped > 0 && !Ui.Ask("Clipboard", Ui.T("The {0} oldest item(s) will be removed to fit the new limit. Continue?", dropped)))
        {
            limit.Text = Main.Config.Clipboard.Limit.ToString();
            return;
        }
        Change(config => config.Limit = value);
    }

    private void Fill()
    {
        var chosen = Selected().Select(entry => entry.Id).ToHashSet();
        list.Items.Clear();
        foreach (var entry in ClipboardHistory.Ordered(search.Text.Trim()))
        {
            var item = new ListBoxItem { Content = ClipboardPicker.ItemView(entry), Tag = entry, Padding = new Thickness(8, 6, 8, 6) };
            list.Items.Add(item);
            if (chosen.Contains(entry.Id))
                item.IsSelected = true;
        }
        var all = ClipboardHistory.Entries;
        count.Text = Ui.T("{0} of {1} item(s), {2} pinned", all.Count(entry => !entry.Pinned), Main.Config.Clipboard.Limit, all.Count(entry => entry.Pinned));
        ShowPinState();
    }

    private List<ClipEntry> Selected() => list.SelectedItems.OfType<ListBoxItem>().Select(item => (ClipEntry)item.Tag).ToList();

    private void ShowPinState() => pinButton.Content = Ui.T(Selected() is { Count: > 0 } chosen && chosen.All(entry => entry.Pinned) ? "Unpin" : "Pin");

    private void CopySelected()
    {
        if (Selected().FirstOrDefault() is { } entry && !App.Current.Clips.Copy(entry))
            Ui.Warn("Clipboard", Ui.T("The clipboard is busy. Try again in a moment."));
    }

    private void PinSelected()
    {
        var chosen = Selected();
        var pin = !chosen.All(entry => entry.Pinned);
        foreach (var entry in chosen)
            ClipboardHistory.SetPinned(entry, pin);
    }

    private void DeleteSelected()
    {
        foreach (var entry in Selected())
            ClipboardHistory.Remove(entry);
    }

    private void ClearHistory()
    {
        if (Ui.Ask("Clipboard", Ui.T("Remove every item from the clipboard history? Pinned items stay.")))
            ClipboardHistory.Clear(keepPinned: true);
    }
}
