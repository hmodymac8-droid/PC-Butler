// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCButler.Core;

namespace PCButler;

/// <summary>The folders to sort, the rules, the destination folders and the language.</summary>
public sealed partial class SettingsPage : Page
{
    private static readonly (string Key, string Label)[] AfterRunChoices = [("window", "Show a report window"), ("notify", "Only a tray notification")];
    private static readonly (string Key, string Label)[] Languages = [("", "Automatic"), ("en", "English"), ("ar", "العربية")];

    private readonly ListBox folders = new() { Height = 64 };
    private readonly List<string> folderValues;
    private readonly TextBox age;
    private readonly TextBox interval;
    private readonly ComboBox afterRun = new() { Width = 260 };
    private static readonly (string Key, string Label)[] SortModes =
        [("interval", "Every few minutes"), ("daily", "Once a day"), ("weekly", "Once a week")];
    private static readonly (string Key, string Label)[] CleanModes =
        [("off", "Off"), ("daily", "Once a day"), ("weekly", "Once a week")];
    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private readonly ComboBox sortMode = new() { Width = 190 };
    private readonly ComboBox cleanMode = new() { Width = 190 };
    private readonly ComboBox scheduleDay = new() { Width = 140 };
    private readonly TextBox scheduleTime;
    private readonly TextBox other;
    private readonly TextBox ignore;
    private readonly ComboBox language = new() { Width = 130 };
    private readonly ListView categories;
    private readonly CheckBox autostart;
    private readonly CheckBox dropZone;

    public override void ShowSwitches()
    {
        Match(autostart, Autostart.IsEnabled());
        Match(dropZone, Main.Config.DropZone);
    }

    public SettingsPage(MainWindow main) : base(main, "Settings", "Your folders. Your rules.")
    {
        var config = main.Config;

        // Switches that take effect the moment they are flipped; everything below them waits for Save settings.
        var quick = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var quickTitle = Ui.Text(Ui.T("Applied at once"), 16, weight: FontWeights.Bold).Margin(0, 0, 0, 8);
        quickTitle.FontFamily = Ui.DisplayFont;
        quick.Children.Add(quickTitle);
        // Two separate things: whether PC Butler starts with Windows, and whether it sorts by itself.
        autostart = Ui.Switch(Ui.T("Start PC Butler when Windows starts"), Autostart.IsEnabled(), MainWindow.SetAutostart);
        quick.Children.Add(autostart);
        quick.Children.Add(Ui.Muted(Ui.T("It waits in the taskbar tray, for automatic sorting, updates and the drop zone."), 12, wrap: true)
                             .Margin(0, 2, 0, 0));
        main.AutoSortSwitch.Content = Ui.T("Sort automatically");
        quick.Children.Add(main.AutoSortSwitch.Margin(0, 12, 0, 0));
        quick.Children.Add(Ui.Muted(Ui.T("Moves files on the schedule below, without asking, while PC Butler is running."), 12, wrap: true)
                             .Margin(0, 2, 0, 0));
        quick.Children.Add(Ui.Switch(Ui.T("Show the “PC Butler” menu when I right-click files or folders"), config.ExplorerMenu, on =>
        {
            Main.UpdateConfig(saved => saved.ExplorerMenu = on);
            if (App.SyncExplorerMenu(on) is { } error)
                Ui.Warn("File Explorer", error);
        }).Margin(0, 12, 0, 0));
        quick.Children.Add(Ui.Muted(Ui.T("Sort files into their category folders, sort a folder's loose files, lock files in the Vault or shred them. "
                                         + "On Windows 11 the menu is under “Show more options”."), 12, wrap: true).Margin(0, 2, 0, 0));
        if (ExplorerMenu.HasShortMenu)
        {
            // Windows 11 only: its short right-click menu, or the classic full one (where the entry above is one click away).
            CheckBox? classic = null;
            classic = Ui.Switch(Ui.T("Use the classic full right-click menu instead of Windows 11's short one"), ExplorerMenu.ClassicMenu, on =>
            {
                if (ExplorerMenu.SetClassicMenu(on) is { } error)
                {
                    Ui.Warn("File Explorer", error);
                    return;
                }
                if (Ui.Ask("File Explorer", Ui.T("File Explorer uses the new menu after it restarts.\n\nRestart it now? Open folder windows will "
                                                 + "close, and the taskbar disappears for a moment. Otherwise it changes the next time you sign in.")))
                    Task.Run(ExplorerMenu.RestartExplorer);
            });
            quick.Children.Add(classic.Margin(0, 12, 0, 0));
            quick.Children.Add(Ui.Muted(Ui.T("The classic menu shows every entry at once, including the “PC Butler” menu. Switch it off to get "
                                             + "Windows 11's short menu back."), 12, wrap: true).Margin(0, 2, 0, 0));
        }
        dropZone = Ui.Switch(Ui.T("Show the drop zone"), config.DropZone, on => App.Current.SetDropZone(on));
        quick.Children.Add(dropZone.Margin(0, 12, 0, 0));
        quick.Children.Add(Ui.Muted(Ui.T("A small tile that stays on top of other windows. Drag files or folders onto it to sort them, lock them in "
                                         + "the Vault, shred them or recycle them. Drag the tile to move it."), 12, wrap: true).Margin(0, 2, 0, 0));
        AddSandbox(quick);
        AddWindowsUpdates(quick);
        AddPcName(quick);
        quick.Children.Add(new Border { Height = 1, Background = Ui.Paint("Line"), Margin = new Thickness(0, 18, 0, 0) });

        // Save settings sits at the bottom, always in view, because nothing in the form below applies until it is pressed.
        var save = Ui.Button(Ui.T("Save settings"), Save, accent: true);
        save.MinWidth = 220;
        save.Padding = new Thickness(26, 11, 26, 11);
        save.FontSize = 15;
        save.FontWeight = FontWeights.SemiBold;
        var saveBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        saveBar.Children.Add(save.Dock(Dock.Right));
        saveBar.Children.Add(Ui.Muted(Ui.T("Folders, rules and schedule are applied when you save.")).Margin(0, 0, 14, 0).Dock(Dock.Right));
        var general = new DockPanel();
        general.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = saveBar }.Dock(Dock.Bottom));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < 10; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = index == 8 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        grid.Children.Add(Ui.Text(Ui.T("Folders to sort")).Margin(0, 6, 12).At(0, 0));
        folderValues = [.. config.Folders];
        ShowFolders();
        var folderMenu = new ContextMenu();
        folderMenu.Items.Add(Ui.Item("Sort only this folder now", () =>
        {
            if (folders.SelectedIndex >= 0)
                Main.Sorter.SortFolderNow(folderValues[folders.SelectedIndex]);
        }));
        folders.ContextMenu = folderMenu;
        folders.ContextMenuOpening += (_, e) => e.Handled = folders.SelectedIndex < 0;
        folders.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(folders, (DependencyObject)e.OriginalSource) is ListBoxItem item)
                item.IsSelected = true;
        };
        grid.Children.Add(folders.Margin(6, 0, 6).At(0, 1));
        var folderButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        folderButtons.Children.Add(Ui.Button(Ui.T("Add…"), AddFolder));
        folderButtons.Children.Add(Ui.Button(Ui.T("Remove"), RemoveFolder).Margin(0, 4));
        grid.Children.Add(folderButtons.At(0, 2));

        void Setting(int row, string label, FrameworkElement field, string hint = "")
        {
            grid.Children.Add(Ui.Text(Ui.T(label)).Margin(0, 10, 12).At(row, 0));
            grid.Children.Add(Ui.Row(8, field, Ui.Muted(hint.Length > 0 ? Ui.T(hint) : "")).Margin(6, 10).At(row, 1, 2));
        }

        age = Ui.Field(config.MinAgeDays.ToString(), 80);
        Setting(1, "Move files older than (days)", age, "a category can have its own number");
        // When automatic sorting and cleaning run: every few minutes, or at a set time each day or week.
        interval = Ui.Field(config.ScanIntervalMinutes.ToString(), 80);
        foreach (var (_, label) in SortModes)
            sortMode.Items.Add(Ui.T(label));
        sortMode.SelectedIndex = Math.Max(0, Array.FindIndex(SortModes, choice => choice.Key == config.Schedule.Sort));
        var minutes = Ui.Row(8, interval, Ui.Muted(Ui.T("minutes between checks; 0 turns automatic sorting off")));
        void ShowInterval() => minutes.Visibility = sortMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        sortMode.SelectionChanged += (_, _) => ShowInterval();
        ShowInterval();
        Setting(2, "Automatic sorting runs", Ui.Row(8, sortMode, minutes));
        foreach (var (_, label) in CleanModes)
            cleanMode.Items.Add(Ui.T(label));
        cleanMode.SelectedIndex = Math.Max(0, Array.FindIndex(CleanModes, choice => choice.Key == config.Schedule.Clean));
        foreach (var day in Days)
            scheduleDay.Items.Add(Ui.T(day));
        scheduleDay.SelectedIndex = Math.Clamp(config.Schedule.Day, 0, 6);
        scheduleTime = Ui.Field(config.Schedule.Time, 70);
        Setting(3, "Automatic cleaning", Ui.Row(8, cleanMode, Ui.Text(Ui.T("Daily and weekly runs start at")), scheduleTime, scheduleDay));
        foreach (var (_, label) in AfterRunChoices)
            afterRun.Items.Add(Ui.T(label));
        afterRun.SelectedIndex = Math.Max(0, Array.FindIndex(AfterRunChoices, choice => choice.Key == config.AfterRun));
        Setting(4, "After an automatic run", afterRun);
        other = Ui.Field(config.OtherFolder, 260);
        Setting(5, "Folder for all other file types", other, "leave blank to leave those files alone");
        ignore = Ui.Field(string.Join(", ", config.Ignore), 480);
        Setting(6, "Never move (names or patterns)", ignore, "e.g. *.torrent, taxes*");

        var section = Ui.Text(Ui.T("Destination folders"), 16, weight: FontWeights.Bold).Margin(0, 18, 0, 8);
        section.FontFamily = Ui.DisplayFont;
        grid.Children.Add(section.At(7, 0, 3));
        categories = Ui.List(("Destination folder", 220, "FileCell"), ("Extensions", 430), ("Move after", 110), ("Clean up after", 130));
        categories.SelectionMode = SelectionMode.Single;
        categories.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                EditCategory();
        };
        categories.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Row>(config.Categories.Select(CategoryRow));
        grid.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), Child = categories, MinHeight = 200 }.At(8, 0, 3));

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Ui.Button(Ui.T("Add…"), AddCategory));
        buttons.Children.Add(Ui.Button(Ui.T("Edit…"), EditCategory).Margin(6, 0, 6));
        buttons.Children.Add(Ui.Button(Ui.T("Remove"), RemoveCategory));
        grid.Children.Add(buttons.At(9, 0, 3));
        // More settings than a short window can show: the page scrolls rather than squeezing the list.
        // The language comes first, at the top of the page; like the form below, it applies when you save.
        foreach (var (_, label) in Languages)
            language.Items.Add(label);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, choice => choice.Key == config.Language));
        var languageRow = Ui.Row(10, Ui.Text(Ui.T("Language"), 16, weight: FontWeights.Bold), language,
                                 Ui.Muted(Ui.T("Applied when you save."), 12)).Margin(0, 0, 0, 18);
        ((TextBlock)languageRow.Children[0]).FontFamily = Ui.DisplayFont;
        foreach (UIElement child in languageRow.Children)
            ((FrameworkElement)child).VerticalAlignment = VerticalAlignment.Center;
        var all = new DockPanel();
        all.Children.Add(languageRow.Dock(Dock.Top));
        all.Children.Add(quick.Dock(Dock.Top));
        all.Children.Add(grid);
        general.Children.Add(new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        tabs.Items.Add(new TabItem { Header = Ui.T("General"), Content = new Border { Padding = new Thickness(18, 16, 18, 12), Child = general }, Tag = "general" });
        tabs.Items.Add(new TabItem { Header = Ui.T("Search"), Content = SearchTab(), Tag = "search" });
        tabs.Items.Add(new TabItem { Header = Ui.T("Hotkeys"), Content = HotkeysTab(), Tag = "hotkeys" });
        Children.Add(tabs);
    }

    private readonly TabControl tabs = new();

    /// <summary>Open the page at one of its tabs ("general", "search", "hotkeys").</summary>
    public void ShowTab(string key)
    {
        Main.ShowPage("settings");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(tab => (string?)tab.Tag == key) ?? tabs.SelectedItem;
    }

    /// <summary>Hotkeys: one shortcut per action, working in any program, applied the moment it is typed.</summary>
    private ScrollViewer HotkeysTab()
    {
        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        rows.Children.Add(Ui.Muted(Ui.T("These shortcuts work whatever program is in front, while PC Butler is running (it can stay in the tray). Click a "
                                        + "box and press the keys, with Ctrl, Alt, Shift or Win. Backspace clears it."), 12, wrap: true).Margin(24, 8, 24, 10));
        // A box records the keys pressed in it: a modifier and a key, or Backspace to clear.
        static void Record(TextBox field, Action<string> set) => field.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
                return;
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (pressed is Key.Back or Key.Delete)
                    set("");
                return; // a key alone would take it from every program
            }
            set(Shortcut.Format(Keyboard.Modifiers, pressed));
        };
        var first = true;
        string Current(string action) => Hotkeys.Effective(Main.Config.Hotkeys).GetValueOrDefault(action, "");
        // An action with a default shortcut keeps "" when cleared, so the default does not come back.
        void Store(Config config, string action, string keys)
        {
            if (keys.Length > 0 || Hotkeys.Defaults.ContainsKey(action))
                config.Hotkeys[action] = keys;
            else
                config.Hotkeys.Remove(action);
        }
        foreach (var (key, label, detail) in Hotkeys.Actions)
        {
            var field = Ui.Field(Current(key), 190, Ui.T("None"));
            field.IsReadOnly = true;
            void Set(string keys)
            {
                var before = Current(key);
                var taken = keys.Length > 0 ? Hotkeys.Actions.FirstOrDefault(other => other.Key != key && Current(other.Key) == keys).Label : null;
                if (taken is not null || (keys.Length > 0 && keys == Main.Config.Clipboard.Shortcut && Main.Config.Clipboard.Enabled))
                {
                    Ui.Warn("Hotkeys", Ui.T("{0} is already the shortcut for “{1}”.", keys, Ui.T(taken ?? "Clipboard history")));
                    return;
                }
                Main.UpdateConfig(config => Store(config, key, keys));
                if (App.Current.Keys.Apply(Main.Config.Hotkeys).TryGetValue(key, out var problem))
                {
                    var freed = problem.StartsWith(Hotkeys.HeldByWindows, StringComparison.Ordinal)
                                && OfferExplorerKey(keys, problem[Hotkeys.HeldByWindows.Length..]);
                    if (!freed)
                    {
                        if (!problem.StartsWith(Hotkeys.HeldByWindows, StringComparison.Ordinal))
                            Ui.Warn("Hotkeys", problem);
                        Main.UpdateConfig(config => Store(config, key, before));
                        App.Current.Keys.Apply(Main.Config.Hotkeys);
                    }
                }
                ReturnExplorerKeys();
                field.Text = Current(key);
            }
            Record(field, Set);
            rows.Children.Add(Ui.SettingRow(Ui.T(label), detail.Length > 0 ? Ui.T(detail) : "", Ui.Row(6, field, Ui.Button(Ui.T("Clear"), () => Set(""))), first));
            first = false;
        }
        // The clipboard history keeps its own shortcut (the Clipboard page shows the same one); it is changed here just the same.
        var clipboard = Ui.Field(Main.Config.Clipboard.Shortcut, 190, Ui.T("None"));
        clipboard.IsReadOnly = true;
        void SetClipboard(string keys)
        {
            var before = Main.Config.Clipboard.Shortcut;
            var taken = keys.Length > 0 ? Hotkeys.Actions.FirstOrDefault(other => Current(other.Key) == keys).Label : null;
            if (taken is not null)
            {
                Ui.Warn("Hotkeys", Ui.T("{0} is already the shortcut for “{1}”.", keys, Ui.T(taken)));
                return;
            }
            Main.UpdateConfig(config => config.Clipboard.Shortcut = keys);
            if (App.Current.Clips.Apply(Main.Config.Clipboard) is { } problem)
            {
                Ui.Warn("Hotkeys", problem);
                Main.UpdateConfig(config => config.Clipboard.Shortcut = before);
                App.Current.Clips.Apply(Main.Config.Clipboard);
            }
            clipboard.Text = Main.Config.Clipboard.Shortcut;
        }
        Record(clipboard, SetClipboard);
        rows.Children.Add(Ui.SettingRow(Ui.T("Clipboard history"),
                                        Ui.T(Main.Config.Clipboard.Enabled ? "Opens the list of what you copied."
                                                                           : "Opens the list of what you copied. It works once the clipboard manager is switched on, on the Clipboard page."),
                                        Ui.Row(6, clipboard, Ui.Button(Ui.T("Clear"), () => SetClipboard("")))));
        rows.Children.Add(Ui.Muted(Ui.T("Shortcuts with the Win key (such as Win + F, which Windows normally keeps for Feedback Hub) are taken over while "
                                        + "PC Butler runs. They do not work while a program running as administrator is in front."), 12, wrap: true)
                             .Margin(24, 14, 24, 10));
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>Windows Sandbox: a switch (administrator rights and a restart), and a button to open it once it is on.</summary>
    private static void AddSandbox(StackPanel quick)
    {
        // Windows takes a moment to say whether the feature is on: the row is built at once and filled in when it has.
        var state = WindowsSandbox.State.Off;
        var showing = false; // set while the switch is put back, so that is not taken as a click
        CheckBox? toggle = null;
        var open = Ui.Button(Ui.T("Open Windows Sandbox"), () =>
        {
            if (!WindowsSandbox.Open())
                Ui.Warn("Windows Sandbox", Ui.T("Windows Sandbox could not be started. If you just turned it on, restart the PC first."));
        });
        open.Visibility = state == WindowsSandbox.State.On ? Visibility.Visible : Visibility.Collapsed;
        toggle = Ui.Switch(Ui.T("Windows Sandbox"), state == WindowsSandbox.State.On, async on =>
        {
            if (showing)
                return;
            void PutBack()
            {
                showing = true;
                toggle!.IsChecked = !on;
                showing = false;
            }
            if (!Ui.Ask("Windows Sandbox", Ui.T(on
                    ? "Turn on Windows Sandbox?\n\nWindows asks for administrator rights, and the PC must restart before you can use it."
                    : "Turn off Windows Sandbox?\n\nWindows asks for administrator rights, and the change is finished by a restart.")))
            {
                PutBack();
                return;
            }
            toggle!.IsEnabled = false;
            var error = await Task.Run(() => WindowsSandbox.Set(on));
            toggle.IsEnabled = true;
            if (error is not null)
            {
                PutBack();
                if (error.Length > 0)
                    Ui.Warn("Windows Sandbox", Ui.T("Windows Sandbox could not be changed: {0}\n\nIt needs Windows Pro, Enterprise or Education, "
                                                    + "and virtualization turned on in the PC's firmware (BIOS/UEFI).", error));
                return;
            }
            open.Visibility = Visibility.Collapsed; // usable only after the restart
            if (Ui.Ask("Windows Sandbox", Ui.T("Done. Restart the PC now to finish?\n\nSave your work in other programs first.")))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true })?.Dispose();
        });
        quick.Children.Add(Ui.Row(12, toggle, open).Margin(0, 12, 0, 0));
        toggle.IsEnabled = false;
        var note = Ui.Muted(Ui.T("A throwaway copy of Windows for trying files and programs safely. Everything in it is deleted when you close it."), 12, wrap: true);
        quick.Children.Add(note.Margin(0, 2, 0, 0));
        toggle.Dispatcher.BeginInvoke(async () =>
        {
            state = await Task.Run(WindowsSandbox.Current);
            showing = true;
            toggle.IsChecked = state == WindowsSandbox.State.On;
            showing = false;
            toggle.IsEnabled = state != WindowsSandbox.State.NotAvailable;
            open.Visibility = state == WindowsSandbox.State.On ? Visibility.Visible : Visibility.Collapsed;
            if (state == WindowsSandbox.State.NotAvailable)
                note.Text = Ui.T("Windows Sandbox needs Windows Pro, Enterprise or Education.");
        });
    }

    /// <summary>Stop Windows updates: a switch that holds them back until it is switched off again (administrator rights).</summary>
    private static void AddWindowsUpdates(StackPanel quick)
    {
        var showing = false;
        CheckBox? toggle = null;
        toggle = Ui.Switch(Ui.T("Stop Windows updates"), TuneUp.IsApplied(TuneUp.WindowsUpdate), async on =>
        {
            if (showing)
                return;
            void PutBack()
            {
                showing = true;
                toggle!.IsChecked = !on;
                showing = false;
            }
            if (on && !Ui.Ask("Windows updates", Ui.T("Stop Windows updates?\n\nWindows will not download or install updates until you switch this "
                                                      + "off again. That includes security fixes, so the PC is less protected the longer it stays on."), warning: true))
            {
                PutBack();
                return;
            }
            if (!await Admin.Ensure("Windows updates", Ui.T("Changing Windows updates needs administrator rights.")))
            {
                PutBack();
                return;
            }
            toggle!.IsEnabled = false;
            var result = await Task.Run(() => on ? TuneUp.Apply([TuneUp.WindowsUpdate], null) : TuneUp.Undo(tweak => tweak.Key == TuneUp.WindowsUpdate));
            toggle.IsEnabled = true;
            if (result.NeedsHelper.Count > 0 || result.Changed == 0)
            {
                PutBack();
                Ui.Warn("Windows updates", Ui.T("Windows updates could not be changed."));
                return;
            }
            Ui.Info("Windows updates", Ui.T(on ? "Windows updates are stopped. Windows Update in Windows' own Settings shows them as paused."
                                               : "Windows updates are back on. Open Windows Update and press Check for updates to catch up."));
        });
        quick.Children.Add(toggle.Margin(0, 12, 0, 0));
        quick.Children.Add(Ui.Muted(Ui.T("Holds back all Windows updates, security fixes included, until you switch it off. Needs administrator rights."),
                                    12, wrap: true).Margin(0, 2, 0, 0));
    }

    /// <summary>The PC's name in Windows, with a button that changes it (administrator rights and a restart).</summary>
    private static void AddPcName(StackPanel quick)
    {
        var field = Ui.Field(PcName.Pending, 200);
        field.MaxLength = 15;
        Button? rename = null;
        rename = Ui.Button(Ui.T("Rename"), async () =>
        {
            var name = field.Text.Trim();
            if (name.Equals(PcName.Pending, StringComparison.OrdinalIgnoreCase))
                return;
            if (!PcName.Valid(name))
            {
                Ui.Warn("PC name", Ui.T("A PC name is 1 to 15 English letters, digits and hyphens. It cannot be only digits, or begin or end with a hyphen."));
                return;
            }
            if (!await Admin.Ensure("PC name", Ui.T("Renaming the PC needs administrator rights.")))
                return;
            rename!.IsEnabled = false;
            var done = await Task.Run(() => AdminHelper.Send("rename_pc", [name]));
            rename.IsEnabled = true;
            if (done is not { Count: 1 })
            {
                Ui.Warn("PC name", Ui.T("Windows did not accept the new name."));
                return;
            }
            if (Ui.Ask("PC name", Ui.T("This PC will be called {0} after it restarts.\n\nRestart now? Save your work in other programs first.", name)))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true })?.Dispose();
        });
        var label = Ui.Text(Ui.T("PC name"));
        label.VerticalAlignment = VerticalAlignment.Center;
        quick.Children.Add(Ui.Row(10, label, field, rename).Margin(0, 14, 0, 0));
        quick.Children.Add(Ui.Muted(Ui.T("The name other devices on the network, and other PCs in Courier, see. Needs administrator rights and a restart."),
                                    12, wrap: true).Margin(0, 2, 0, 0));
    }

    private System.Collections.ObjectModel.ObservableCollection<Row> CategoryRows => (System.Collections.ObjectModel.ObservableCollection<Row>)categories.ItemsSource;

    private static Row CategoryRow(Category category) => new Row
    {
        Name = category.Folder, Tag = category,
        Cells = [string.Join(" ", category.Extensions),
                 category.MinAgeDays is { } days ? Ui.T("{0} days", days) : Ui.T("general"),
                 category.RecycleAfterDays > 0 ? Ui.T("{0} days", category.RecycleAfterDays) : Ui.T("never")],
    }.WithTile("", Path.GetFileName(category.Folder));

    /// <summary>The never-move list changed elsewhere (the Sorter page's "Never move this file").</summary>
    public void ShowIgnore() => ignore.Text = string.Join(", ", Main.Config.Ignore);

    private void ShowFolders()
    {
        folders.Items.Clear();
        foreach (var folder in folderValues)
            folders.Items.Add(folder.Length > 0 ? folder : Ui.T("Downloads (automatic): {0}", ConfigStore.DetectDownloadsFolder()));
    }

    private void AddFolder()
    {
        if (Ui.PickFolder("Folders to sort") is not { } chosen)
            return;
        var folder = Path.GetFullPath(chosen);
        if (!folderValues.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            folderValues.Add(folder);
            ShowFolders();
        }
    }

    private void RemoveFolder()
    {
        if (folders.SelectedIndex < 0)
            return;
        folderValues.RemoveAt(folders.SelectedIndex);
        ShowFolders();
    }

    private void AddCategory()
    {
        if (CategoryDialog.Ask(Window.GetWindow(this)!, null) is { } category)
            CategoryRows.Add(CategoryRow(category));
    }

    private void EditCategory()
    {
        if (categories.SelectedItem is not Row row)
            return;
        if (CategoryDialog.Ask(Window.GetWindow(this)!, (Category)row.Tag!) is { } category)
            CategoryRows[CategoryRows.IndexOf(row)] = CategoryRow(category);
    }

    private void RemoveCategory()
    {
        if (categories.SelectedItem is Row row)
            CategoryRows.Remove(row);
    }

    [GeneratedRegex(@"[,;\n]+")]
    private static partial Regex ListSeparators();

    private void Save()
    {
        if (!int.TryParse(age.Text.Trim(), out var days) || !int.TryParse(interval.Text.Trim(), out var minutes) || days < 0 || minutes < 0)
        {
            Ui.Warn("Settings", Ui.T("Days and minutes must be whole numbers, 0 or more."));
            return;
        }
        if (!TimeSpan.TryParseExact(scheduleTime.Text.Trim(), ["h\\:mm", "hh\\:mm"], System.Globalization.CultureInfo.InvariantCulture, out var startAt)
            || startAt >= TimeSpan.FromDays(1))
        {
            Ui.Warn("Settings", Ui.T("Write the time as hours and minutes, like 09:00 or 18:30."));
            return;
        }
        if (folderValues.Count == 0)
        {
            folderValues.Add("");
            ShowFolders();
        }
        var chosenLanguage = Languages[Math.Max(0, language.SelectedIndex)].Key;
        var languageChanged = chosenLanguage != Main.Config.Language;
        var wasOn = Main.Config.AutoSort;
        Main.UpdateConfig(config =>
        {
            config.Language = chosenLanguage;
            config.Folders = [.. folderValues];
            config.MinAgeDays = days;
            config.ScanIntervalMinutes = minutes;
            config.Categories = CategoryRows.Select(row => (Category)row.Tag!).ToList();
            config.OtherFolder = other.Text.Trim();
            config.Ignore = ListSeparators().Split(ignore.Text).Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
            config.AfterRun = AfterRunChoices[Math.Max(0, afterRun.SelectedIndex)].Key;
            var schedule = config.Schedule;
            var (sort, clean) = (SortModes[Math.Max(0, sortMode.SelectedIndex)].Key, CleanModes[Math.Max(0, cleanMode.SelectedIndex)].Key);
            var (time, day) = (startAt.ToString("hh\\:mm"), Math.Max(0, scheduleDay.SelectedIndex));
            // A changed schedule starts counting afresh, so nothing runs the moment it is saved.
            if (sort != schedule.Sort || time != schedule.Time || day != schedule.Day)
                schedule.LastSort = "";
            if (clean != schedule.Clean || time != schedule.Time || day != schedule.Day)
                schedule.LastClean = "";
            (schedule.Sort, schedule.Clean, schedule.Time, schedule.Day) = (sort, clean, time, day);
        });
        var message = Ui.T("Settings saved. The Sorter page shows the result.");
        if (minutes == 0 && Main.Config.Schedule.Sort == "interval")
        {
            if (wasOn)
                message = Ui.T("Settings saved. The Sorter page shows the result.\n\nAutomatic sorting has been turned off because the interval is 0.");
            Main.UpdateConfig(config => config.AutoSort = false);
        }
        if (languageChanged)
        {
            // Every label was made in the old language, so the window reopens in the new one.
            Ui.Info("Settings", Ui.T("Settings saved. The window will reopen in the new language."));
            Tr.Language = Main.Config.EffectiveLanguage();
            App.Current.ReopenMainWindow();
            return;
        }
        Main.RefreshPreview();
        Main.RefreshBackgroundStatus();
        Ui.Info("Settings", message);
    }
}
