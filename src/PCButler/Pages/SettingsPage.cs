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

    public SettingsPage(MainWindow main) : base(main, "Settings", "Your folders. Your rules.")
    {
        var config = main.Config;
        AddAction("Save settings", Save, accent: true);
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
        foreach (var (_, label) in Languages)
            language.Items.Add(label);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, choice => choice.Key == config.Language));
        buttons.Children.Add(language.Margin(6, 0, 0).Dock(Dock.Right));
        buttons.Children.Add(Ui.Text(Ui.T("Language")).Dock(Dock.Right));
        grid.Children.Add(buttons.At(9, 0, 3));
        // More settings than a short window can show: the page scrolls rather than squeezing the list.
        Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
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
