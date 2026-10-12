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
        for (var index = 0; index < 9; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = index == 7 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

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
        interval = Ui.Field(config.ScanIntervalMinutes.ToString(), 80);
        Setting(2, "Background check every (minutes)", interval, "0 turns automatic sorting off");
        foreach (var (_, label) in AfterRunChoices)
            afterRun.Items.Add(Ui.T(label));
        afterRun.SelectedIndex = Math.Max(0, Array.FindIndex(AfterRunChoices, choice => choice.Key == config.AfterRun));
        Setting(3, "After an automatic run", afterRun);
        other = Ui.Field(config.OtherFolder, 260);
        Setting(4, "Folder for all other file types", other, "leave blank to leave those files alone");
        ignore = Ui.Field(string.Join(", ", config.Ignore), 480);
        Setting(5, "Never move (names or patterns)", ignore, "e.g. *.torrent, taxes*");

        var section = Ui.Text(Ui.T("Destination folders"), 16, weight: FontWeights.Bold).Margin(0, 18, 0, 8);
        section.FontFamily = Ui.DisplayFont;
        grid.Children.Add(section.At(6, 0, 3));
        categories = Ui.List(("Destination folder", 220, "FileCell"), ("Extensions", 430), ("Move after", 110), ("Clean up after", 130));
        categories.SelectionMode = SelectionMode.Single;
        categories.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                EditCategory();
        };
        categories.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Row>(config.Categories.Select(CategoryRow));
        grid.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), Child = categories, MinHeight = 120 }.At(7, 0, 3));

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Ui.Button(Ui.T("Add…"), AddCategory));
        buttons.Children.Add(Ui.Button(Ui.T("Edit…"), EditCategory).Margin(6, 0, 6));
        buttons.Children.Add(Ui.Button(Ui.T("Remove"), RemoveCategory));
        foreach (var (_, label) in Languages)
            language.Items.Add(label);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, choice => choice.Key == config.Language));
        buttons.Children.Add(language.Margin(6, 0, 0).Dock(Dock.Right));
        buttons.Children.Add(Ui.Text(Ui.T("Language")).Dock(Dock.Right));
        grid.Children.Add(buttons.At(8, 0, 3));
        Children.Add(grid);
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
        });
        var message = Ui.T("Settings saved. The Sorter page shows the result.");
        if (minutes == 0)
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
