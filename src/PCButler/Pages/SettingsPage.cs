// SPDX-License-Identifier: GPL-3.0-only
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

    public SettingsPage(MainWindow main) : base(main, "Your folders. Your rules.", "A few thoughtful settings to make PC Butler feel like yours.")
    {
        var config = main.Config;
        // In the header, which never scrolls away: nothing in the cards below applies until it is pressed.
        AddAction("Save settings", Save, accent: true);
        var page = new StackPanel();

        // --- Applied at once: switches that take effect the moment they are flipped ---
        var quick = new StackPanel();
        quick.Children.Add(Ui.SettingRow(Ui.T("Automatic sorting"), Ui.T("Sorts in the background on the schedule below, and starts when you sign in."),
                                         main.BackgroundButton, first: true));
        quick.Children.Add(Ui.SettingRow(Ui.T("Show the “PC Butler” menu when I right-click files or folders"),
                                         Ui.T("Sort files into their category folders, sort a folder's loose files, lock files in the Vault or shred them. "
                                              + "On Windows 11 the menu is under “Show more options”."),
                                         Ui.Switch("", config.ExplorerMenu, on =>
                                         {
                                             Main.UpdateConfig(saved => saved.ExplorerMenu = on);
                                             if (App.SyncExplorerMenu(on) is { } error)
                                                 Ui.Warn("File Explorer", error);
                                         })));
        if (ExplorerMenu.HasShortMenu)
        {
            // Windows 11 only: its short right-click menu, or the classic full one (where the menu above is one click away).
            quick.Children.Add(Ui.SettingRow(Ui.T("Use the classic full right-click menu instead of Windows 11's short one"),
                                             Ui.T("The classic menu shows every entry at once, including the “PC Butler” menu. Switch it off to get "
                                                  + "Windows 11's short menu back."),
                                             Ui.Switch("", ExplorerMenu.ClassicMenu, on =>
                                             {
                                                 if (ExplorerMenu.SetClassicMenu(on) is { } error)
                                                 {
                                                     Ui.Warn("File Explorer", error);
                                                     return;
                                                 }
                                                 if (Ui.Ask("File Explorer", Ui.T("File Explorer uses the new menu after it restarts.\n\nRestart it now? Open folder windows will "
                                                                                  + "close, and the taskbar disappears for a moment. Otherwise it changes the next time you sign in.")))
                                                     Task.Run(ExplorerMenu.RestartExplorer);
                                             })));
        }
        quick.Children.Add(Ui.SettingRow(Ui.T("Show the drop zone"),
                                         Ui.T("A small tile that stays on top of other windows. Drag files or folders onto it to sort them, lock them in "
                                              + "the Vault, shred them or recycle them. Drag the tile to move it."),
                                         Ui.Switch("", config.DropZone, on => App.Current.SetDropZone(on))));
        page.Children.Add(Card("Applied at once", "\uE945", quick));

        // --- Sorting preferences: saved with Save settings ---
        var sorting = new StackPanel();
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
        folders.Width = 420;
        var folderButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
        folderButtons.Children.Add(Ui.Button(Ui.T("Add…"), AddFolder));
        folderButtons.Children.Add(Ui.Button(Ui.T("Remove"), RemoveFolder).Margin(0, 4));
        sorting.Children.Add(Ui.SettingRow(Ui.T("Folders to sort"),
                                           Ui.T("Your Downloads folder is the default starting point. Right-click a folder to sort only it now."),
                                           Ui.Row(0, folders, folderButtons), first: true));

        age = Ui.Field(config.MinAgeDays.ToString(), 80);
        sorting.Children.Add(Ui.SettingRow(Ui.T("Move files older than (days)"),
                                           Ui.T("Give new downloads a little time before sorting. A category can have its own number."),
                                           Ui.Row(8, age, Ui.Muted(Ui.T("days")))));

        // When automatic sorting and cleaning run: every few minutes, or at a set time each day or week.
        interval = Ui.Field(config.ScanIntervalMinutes.ToString(), 80);
        foreach (var (_, label) in SortModes)
            sortMode.Items.Add(Ui.T(label));
        sortMode.SelectedIndex = Math.Max(0, Array.FindIndex(SortModes, choice => choice.Key == config.Schedule.Sort));
        var minutes = Ui.Row(8, interval, Ui.Muted(Ui.T("minutes")));
        void ShowInterval() => minutes.Visibility = sortMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        sortMode.SelectionChanged += (_, _) => ShowInterval();
        ShowInterval();
        sorting.Children.Add(Ui.SettingRow(Ui.T("Automatic sorting runs"),
                                           Ui.T("Choose how often PC Butler checks for files. 0 minutes turns automatic sorting off."),
                                           Ui.Row(8, minutes, sortMode)));

        foreach (var (_, label) in CleanModes)
            cleanMode.Items.Add(Ui.T(label));
        cleanMode.SelectedIndex = Math.Max(0, Array.FindIndex(CleanModes, choice => choice.Key == config.Schedule.Clean));
        foreach (var day in Days)
            scheduleDay.Items.Add(Ui.T(day));
        scheduleDay.SelectedIndex = Math.Clamp(config.Schedule.Day, 0, 6);
        scheduleTime = Ui.Field(config.Schedule.Time, 70);
        sorting.Children.Add(Ui.SettingRow(Ui.T("Automatic cleaning"),
                                           Ui.T("Clean temporary files on your schedule. Daily and weekly runs, sorting and cleaning alike, start at this time and day."),
                                           Ui.Row(8, cleanMode, scheduleTime, scheduleDay)));

        foreach (var (_, label) in AfterRunChoices)
            afterRun.Items.Add(Ui.T(label));
        afterRun.SelectedIndex = Math.Max(0, Array.FindIndex(AfterRunChoices, choice => choice.Key == config.AfterRun));
        sorting.Children.Add(Ui.SettingRow(Ui.T("After an automatic run"), Ui.T("See exactly what was moved and where, or just get a notification."), afterRun));

        other = Ui.Field(config.OtherFolder, 220);
        sorting.Children.Add(Ui.SettingRow(Ui.T("Folder for all other file types"), Ui.T("leave blank to leave those files alone"), other));
        ignore = Ui.Field(string.Join(", ", config.Ignore), 260, Ui.T("e.g. *.torrent, taxes*"));
        sorting.Children.Add(Ui.SettingRow(Ui.T("Never move (names or patterns)"), Ui.T("File names or patterns to leave alone, separated by commas."), ignore));

        foreach (var (_, label) in Languages)
            language.Items.Add(label);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, choice => choice.Key == config.Language));
        sorting.Children.Add(Ui.SettingRow(Ui.T("Language"), "", language));
        page.Children.Add(Card("Sorting preferences", "\uE71C", sorting));

        // --- Destination folders: which file types go where ---
        categories = Ui.List(("Destination folder", 220, "FileCell"), ("Extensions", 430), ("Move after", 110), ("Clean up after", 130));
        categories.SelectionMode = SelectionMode.Single;
        categories.Height = 320;
        categories.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                EditCategory();
        };
        categories.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Row>(config.Categories.Select(CategoryRow));
        var destinations = new CardPanel("Destination folders", "\uE8B7") { Margin = new Thickness(0, 0, 0, 12) };
        var addFolder = new Button { Content = Ui.Row(8, Ui.Glyph("\uE710", 12, "Text"), Ui.Text(Ui.T("Add folder"), 13)) };
        addFolder.Click += (_, _) => AddCategory();
        destinations.Controls.Children.Add(Ui.Button(Ui.T("Remove"), RemoveCategory));
        destinations.Controls.Children.Add(Ui.Button(Ui.T("Edit…"), EditCategory).Margin(6, 0, 6));
        destinations.Controls.Children.Add(addFolder);
        destinations.SetBody(categories);
        page.Children.Add(destinations);

        // More settings than a short window can show: the page scrolls.
        Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
    }

    /// <summary>A bordered card with a heading and rows.</summary>
    private static CardPanel Card(string title, string glyph, UIElement rows)
    {
        var card = new CardPanel(title, glyph) { Margin = new Thickness(0, 0, 0, 18) };
        card.SetBody(rows);
        return card;
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
            MainWindow.SetAutostart(false);
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
