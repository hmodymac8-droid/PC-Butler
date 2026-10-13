// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>Find extra copies of files, tick the ones to remove, and delete them.</summary>
public sealed class DuplicatesPage : Page
{
    private readonly Button scanButton;
    private readonly Button stopButton;
    private readonly RadioButton sortedSource;
    private readonly RadioButton folderSource;
    private readonly TextBox folder;
    private readonly CheckBox recursive;
    private readonly ComboBox mode = new() { Width = 430, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox extension = Ui.Field(width: 90);
    private readonly TextBox nameFilter = Ui.Field(width: 160);
    private readonly TextBox minMb = Ui.Field(width: 80);
    private readonly TextBox checkFolder = Ui.Field();
    private readonly CheckBox checkSubfolders;
    private readonly TextBlock summary;
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Width = 140, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button deleteButton;
    private readonly ListView list;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private List<DuplicateGroup> groups = []; // every group the last scan found, minus files deleted or removed since
    private List<DuplicateGroup> shown = []; // the groups that pass the filter, as listed
    private HashSet<string> checkedPaths = []; // Sorter.Norm(path) of every checked file
    private CancellationTokenSource? cancel;
    private bool scanned;
    private bool scanning;

    public DuplicatesPage(MainWindow main) : base(main, "Duplicates", "Find extra copies. Keep what matters.")
    {
        var settings = main.Config.Duplicates;
        scanButton = AddAction("Scan now", Scan, accent: true);
        stopButton = AddAction("Stop", Stop);
        stopButton.IsEnabled = false;

        // Where to look, how to compare, which to show, and which copies to tick.
        var options = new Grid();
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            options.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        for (var index = 0; index < 6; index++)
            options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock Label(string text) => Ui.Text(Ui.T(text)).Margin(0, 0, 12);

        options.Children.Add(Label("Look in").At(0, 0));
        sortedSource = new RadioButton { Content = Ui.T("The sorted folders"), GroupName = "dupsource", IsChecked = settings.Source != "folder" };
        options.Children.Add(sortedSource.At(0, 1, 2));
        recursive = Ui.Switch(Ui.T("Include subfolders"), settings.Recursive, _ => SaveSoon());
        recursive.HorizontalAlignment = HorizontalAlignment.Right;
        options.Children.Add(recursive.At(0, 3));
        folderSource = new RadioButton { Content = Ui.T("This folder:"), GroupName = "dupsource", IsChecked = settings.Source == "folder" };
        options.Children.Add(folderSource.Margin(0, 6, 12, 0).At(1, 1));
        folder = Ui.Field(settings.Folder);
        folder.GotKeyboardFocus += (_, _) => folderSource.IsChecked = true;
        var folderRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        folderRow.Children.Add(Ui.Button(Ui.T("Browse…"), BrowseFolder).Margin(6).Dock(Dock.Right));
        folderRow.Children.Add(folder);
        options.Children.Add(folderRow.At(1, 2, 2));
        sortedSource.Checked += (_, _) => SaveSoon();
        folderSource.Checked += (_, _) => SaveSoon();

        options.Children.Add(Label("Match by").Margin(0, 10, 12).At(2, 0));
        foreach (var (key, label) in Core.Duplicates.Modes)
            mode.Items.Add(new ComboBoxItem { Content = Ui.T(label), Tag = key });
        mode.SelectedIndex = Math.Max(0, Array.FindIndex(Core.Duplicates.Modes, item => item.Key == settings.Mode));
        mode.SelectionChanged += (_, _) => SaveSoon();
        options.Children.Add(mode.Margin(0, 10).At(2, 1, 2));

        options.Children.Add(Label("Show only").Margin(0, 10, 12).At(3, 0));
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, field, value) in new[] { ("Extension", extension, settings.Extension), ("Name contains", nameFilter, settings.Name),
                                                     ("At least (MB)", minMb, settings.MinMb) })
        {
            field.Text = value;
            field.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    ApplyFilter();
            };
            filters.Children.Add(Ui.Text(Ui.T(label)).Margin(0, 0, 4));
            filters.Children.Add(field.Margin(0, 0, 12));
        }
        filters.Children.Add(Ui.Button(Ui.T("Apply"), ApplyFilter));
        filters.Children.Add(Ui.Button(Ui.T("Clear"), ClearFilter).Margin(6));
        options.Children.Add(filters.At(3, 1, 3));

        options.Children.Add(Label("Check in").Margin(0, 10, 12).At(4, 0));
        checkFolder.Text = settings.CheckFolder;
        var checkRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        checkRow.Children.Add(Ui.Button(Ui.T("Use scan folder"), () => checkFolder.Text = folder.Text).Dock(Dock.Right));
        checkRow.Children.Add(Ui.Button(Ui.T("Browse…"), BrowseCheckFolder).Margin(6, 0, 6).Dock(Dock.Right));
        checkRow.Children.Add(checkFolder);
        options.Children.Add(checkRow.At(4, 1, 3));
        // On a row of their own so they stay visible when the window is narrow.
        checkSubfolders = Ui.Switch(Ui.T("Include subfolders"), settings.CheckSubfolders, _ => SaveSoon());
        var checkActions = Ui.Row(12, Ui.Button(Ui.T("Check copies in this folder"), CheckInFolder), checkSubfolders).Margin(0, 6);
        options.Children.Add(checkActions.At(5, 1, 3));
        foreach (var field in new[] { folder, extension, nameFilter, minMb, checkFolder })
            field.TextChanged += (_, _) => SaveSoon();
        Children.Add(options.Dock(Dock.Top));

        var panel = new CardPanel("Duplicate groups", "\uE8C8") { Margin = new Thickness(0, 14, 0, 0) };
        summary = Ui.Muted(Ui.T("Choose where to look and how to compare, then click Scan now."));
        panel.Controls.Children.Add(summary);
        panel.Controls.Children.Add(progress);
        var footer = panel.AddFooter();
        footer.Children.Add(CheckMenuButton());
        deleteButton = Ui.Button(Ui.T("Delete checked"), () => DeleteEntries(CheckedEntries())).Dock(Dock.Right);
        deleteButton.IsEnabled = false;
        footer.Children.Add(deleteButton);
        footer.Children.Add(Ui.Button(Ui.T("Keep first, delete the rest"),
                                      () => DeleteEntries(shown.SelectMany(group => group.Files.Skip(1)).ToList())).Margin(0, 0, 6, 0).Dock(Dock.Right));
        footer.Children.Add(Ui.Button(Ui.T("Keep newest, delete the rest"),
                                      () => DeleteEntries(shown.SelectMany(group => group.Files.OrderByDescending(e => e.Modified).Skip(1)).ToList()))
                                      .Dock(Dock.Right));

        list = Ui.List(("File", 380, "CheckCell"), ("Size", 90), ("Modified", 140), ("Folder", 160), ("Full path", 420));
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                Toggle(Ui.Selected(list));
                e.Handled = true;
            }
        };
        list.MouseDoubleClick += (_, e) =>
        {
            // Like the other lists: double-clicking a file shows it in File Explorer. The tick box only ticks.
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is { Tag: DuplicateEntry entry } && !OnTickBox(e.OriginalSource))
                ShowInExplorer(entry.Path);
        };
        list.PreviewMouseRightButtonUp += (_, e) => OpenRowMenu(e);
        panel.SetBody(list);
        Children.Add(panel);
        saveTimer.Tick += (_, _) => SaveSettings();
    }

    private static bool OnTickBox(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is CheckBox)
                return true;
            if (element is ListViewItem)
                return false;
        }
        return false;
    }

    private Button CheckMenuButton()
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) => menu.Items.Add(Ui.Item(label, action));
        Add("Check all", () => CheckBy(files => files));
        Add("Uncheck all", () => CheckBy(_ => []));
        Add("Invert", () => CheckBy(files => files.Where(e => !checkedPaths.Contains(Sorter.Norm(e.Path)))));
        menu.Items.Add(new Separator());
        Add("All but the first in each group", () => CheckBy(files => files.Skip(1)));
        Add("All but the newest in each group", () => CheckBy(files => files.OrderByDescending(e => e.Modified).Skip(1)));
        Add("The biggest in each group", () => CheckBy(files => files.Where(e => e.Size == files.Max(f => f.Size))));
        Add("The smallest in each group", () => CheckBy(files => files.Where(e => e.Size == files.Min(f => f.Size))));
        Add("Extra copies in the same folder (keep 1 per folder)", CheckSameFolder);
        Add("Every copy inside the \"Check in\" folder", CheckInFolder);
        menu.Items.Add(new Separator());
        Add("Highlighted rows", () => SetChecked(Ui.Selected(list), true));
        var button = new Button { Content = Ui.Row(8, Ui.Text(Ui.T("Check…")), Ui.Glyph("\uE70D", 10)) };
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    // --- settings ---

    private string Mode() => (string)((ComboBoxItem)mode.SelectedItem).Tag;

    private static string Clean(string text) => text.Trim().Trim('"');

    /// <summary>Save shortly after the last change, not on every key press.</summary>
    private void SaveSoon()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveSettings()
    {
        saveTimer.Stop();
        Main.UpdateConfig(config => config.Duplicates = new DuplicatesSettings
        {
            Source = folderSource.IsChecked == true ? "folder" : "sorted", Folder = Clean(folder.Text), Recursive = recursive.IsChecked == true,
            Mode = Mode(), Extension = extension.Text.Trim(), Name = nameFilter.Text.Trim(), MinMb = minMb.Text.Trim(),
            CheckFolder = Clean(checkFolder.Text), CheckSubfolders = checkSubfolders.IsChecked == true,
        });
    }

    public override void Closing()
    {
        cancel?.Cancel();
        SaveSettings();
    }

    public override void Hidden() => SaveSettings();

    private void BrowseFolder()
    {
        if (Ui.PickFolder("Folder to look for duplicates in", Clean(folder.Text)) is { } chosen)
        {
            folder.Text = Path.GetFullPath(chosen);
            folderSource.IsChecked = true;
        }
    }

    private void BrowseCheckFolder()
    {
        var start = Clean(checkFolder.Text).Length > 0 ? Clean(checkFolder.Text) : Clean(folder.Text);
        if (Ui.PickFolder("Folder whose copies to check", start) is { } chosen)
            checkFolder.Text = Path.GetFullPath(chosen);
    }

    // --- scanning ---

    private async void Scan()
    {
        if (scanning)
            return;
        List<string> folders;
        if (folderSource.IsChecked == true)
        {
            var chosen = Clean(folder.Text);
            if (chosen.Length == 0 || !Directory.Exists(chosen))
            {
                Ui.Warn("Duplicates", Ui.T("Choose the folder to look in first."));
                return;
            }
            folders = [Path.GetFullPath(chosen)];
        }
        else
        {
            folders = Core.Duplicates.SortedFolders(Main.Config);
        }
        SaveSettings();
        var scanMode = Mode();
        var subfolders = recursive.IsChecked == true;
        groups = [];
        Render();
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var status = Ui.T("Looking for files…");
        SetScanning(true);
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        ticker.Tick += (_, _) => summary.Text = status;
        ticker.Start();
        try
        {
            // Hashing can take long, so it runs off the window's thread.
            var found = await Task.Run(() =>
            {
                var files = Core.Duplicates.Collect(folders, subfolders, token);
                status = Ui.T("Comparing {0} file(s)…", files.Count);
                return Core.Duplicates.Find(files, scanMode, token, (text, arguments) => status = Ui.T(text, arguments));
            }, token);
            scanned = true;
            groups = found;
            // Identical content is safe to pre-check; files that only look alike are left to the user.
            checkedPaths = scanMode == "content"
                ? groups.SelectMany(group => group.Files.Skip(1)).Select(e => Sorter.Norm(e.Path)).ToHashSet()
                : [];
            ticker.Stop();
            Render();
        }
        catch (OperationCanceledException)
        {
            ticker.Stop();
            summary.Text = Ui.T("Scan stopped.");
        }
        catch (Exception error) // shown to the user, not lost with the thread
        {
            ticker.Stop();
            summary.Text = Ui.T("The scan failed.");
            Ui.Warn("Duplicates", Ui.T("The scan failed:\n{0}", error.Message));
        }
        finally
        {
            SetScanning(false);
        }
    }

    private void Stop()
    {
        cancel?.Cancel();
        summary.Text = Ui.T("Stopping…");
    }

    private void SetScanning(bool value)
    {
        scanning = value;
        scanButton.IsEnabled = !value;
        stopButton.IsEnabled = value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- the list ---

    private Func<DuplicateEntry, bool> Filter()
    {
        var wantedExtension = Core.Duplicates.NormText(extension.Text.Trim().TrimStart('.'));
        var needle = Core.Duplicates.NormText(nameFilter.Text.Trim());
        var minSize = double.TryParse(minMb.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb)
            ? mb * 1024 * 1024 : 0;
        return e => (wantedExtension.Length == 0 || Core.Duplicates.NormText(e.Name).EndsWith("." + wantedExtension, StringComparison.Ordinal))
                    && Core.Duplicates.NormText(e.Name).Contains(needle, StringComparison.Ordinal) && e.Size >= minSize;
    }

    private void ApplyFilter()
    {
        SaveSettings();
        Render();
    }

    private void ClearFilter()
    {
        extension.Text = nameFilter.Text = minMb.Text = "";
        ApplyFilter();
    }

    private void Render()
    {
        var rows = new List<Row>();
        shown = [];
        var passes = Filter();
        foreach (var group in groups)
        {
            var files = group.Files.Where(passes).ToList();
            if (files.Count < 2)
                continue; // a single file left after filtering is no longer a duplicate
            shown.Add(new DuplicateGroup(group.Number, files));
            var sameSize = files.Select(e => e.Size).Distinct().Count() == 1;
            rows.Add(new Row
            {
                Name = Ui.T("Group {0}:  {1} files", group.Number, files.Count), Dim = true, CheckVisibility = Visibility.Collapsed,
                Indent = new Thickness(16, 0, 0, 0), Tag = group,
                Cells = [sameSize ? Ui.FormatSize(files[0].Size) : "", "", "", ""],
                // Sorting by a column orders the files inside each group; the groups stay put.
                SortGroup = shown.Count, SortHeader = true,
            });
            foreach (var entry in files)
            {
                var parent = Path.GetDirectoryName(entry.Path) ?? "";
                var row = new Row
                {
                    Name = entry.Name, Tag = entry, Indent = new Thickness(40, 0, 0, 0), SortGroup = shown.Count,
                    Cells = [Ui.FormatSize(entry.Size), entry.Modified.ToString("yyyy-MM-dd HH:mm"),
                             Path.GetFileName(parent) is { Length: > 0 } leaf ? leaf : parent, entry.Path],
                };
                row.SetChecked(checkedPaths.Contains(Sorter.Norm(entry.Path)));
                row.CheckedChanged = changed =>
                {
                    if (changed.Checked)
                        checkedPaths.Add(Sorter.Norm(entry.Path));
                    else
                        checkedPaths.Remove(Sorter.Norm(entry.Path));
                    UpdateSummary();
                };
                rows.Add(row);
            }
        }
        list.ItemsSource = rows;
        UpdateSummary();
    }

    private IEnumerable<Row> Rows() => list.Items.Cast<Row>();

    private List<DuplicateEntry> CheckedEntries() =>
        Rows().Select(row => row.Tag).OfType<DuplicateEntry>().Where(e => checkedPaths.Contains(Sorter.Norm(e.Path))).ToList();

    private void UpdateSummary()
    {
        string text;
        if (groups.Count == 0)
            text = Ui.T(scanned ? "No duplicates found." : "Choose where to look and how to compare, then click Scan.");
        else if (shown.Count == 0)
            text = Ui.T("{0} group(s) found, but none match the filter.", groups.Count);
        else
        {
            var files = shown.Sum(group => group.Files.Count);
            var spare = shown.Sum(group => group.Files.Sum(e => e.Size) - group.Files.Max(e => e.Size));
            text = Ui.T("{0} group(s), {1} files, up to {2} to free", shown.Count, files, Ui.FormatSize(spare));
        }
        var ticked = CheckedEntries();
        if (ticked.Count > 0)
            text += Ui.T("   ·   {0} checked ({1})", ticked.Count, Ui.FormatSize(ticked.Sum(e => e.Size)));
        summary.Text = text;
        deleteButton.Content = ticked.Count > 0 ? Ui.T("Delete {0} checked", ticked.Count) : Ui.T("Delete checked");
        deleteButton.IsEnabled = ticked.Count > 0;
    }

    /// <summary>File rows for a selection; a group row stands for all of its files.</summary>
    private List<Row> FileRows(IEnumerable<Row> selection)
    {
        var all = Rows().ToList();
        var found = new List<Row>();
        foreach (var row in selection)
        {
            if (row.Tag is DuplicateGroup)
            {
                for (var index = all.IndexOf(row) + 1; index < all.Count && all[index].Tag is DuplicateEntry; index++)
                    if (!found.Contains(all[index]))
                        found.Add(all[index]);
            }
            else if (!found.Contains(row))
            {
                found.Add(row);
            }
        }
        return found;
    }

    private void SetChecked(IEnumerable<Row> selection, bool value)
    {
        foreach (var row in FileRows(selection))
        {
            var key = Sorter.Norm(((DuplicateEntry)row.Tag!).Path);
            if (value)
                checkedPaths.Add(key);
            else
                checkedPaths.Remove(key);
            row.SetChecked(value);
        }
        UpdateSummary();
    }

    /// <summary>Check them all, or uncheck them all if they already are.</summary>
    private void Toggle(IEnumerable<Row> selection)
    {
        var rows = FileRows(selection);
        SetChecked(rows, !rows.All(row => row.Checked));
    }

    /// <summary>Check the files pick(group files) returns in each listed group, and nothing else.</summary>
    private void CheckBy(Func<List<DuplicateEntry>, IEnumerable<DuplicateEntry>> pick)
    {
        checkedPaths = shown.SelectMany(group => pick(group.Files)).Select(e => Sorter.Norm(e.Path)).ToHashSet();
        foreach (var row in Rows().Where(row => row.Tag is DuplicateEntry))
            row.SetChecked(checkedPaths.Contains(Sorter.Norm(((DuplicateEntry)row.Tag!).Path)));
        UpdateSummary();
    }

    private static string ParentKey(DuplicateEntry entry) => Sorter.Norm(Path.GetDirectoryName(entry.Path) ?? "");

    private void CheckSameFolder()
    {
        static IEnumerable<DuplicateEntry> Pick(List<DuplicateEntry> files)
        {
            var first = new Dictionary<string, DuplicateEntry>();
            foreach (var e in files)
                first.TryAdd(ParentKey(e), e);
            return files.Where(e => !ReferenceEquals(first[ParentKey(e)], e));
        }

        if (!shown.Any(group => Pick(group.Files).Any()))
        {
            Ui.Info("Duplicates", Ui.T("No group has two copies in the same folder."));
            return;
        }
        CheckBy(Pick);
    }

    private void CheckInFolder()
    {
        var chosen = Clean(checkFolder.Text).Length > 0 ? Clean(checkFolder.Text) : Clean(folder.Text);
        if (chosen.Length == 0)
        {
            Ui.Info("Duplicates", Ui.T("Enter a folder in \"Check in\", or click Use scan folder."));
            return;
        }
        if (!Directory.Exists(chosen))
        {
            Ui.Warn("Duplicates", Ui.T("Not a folder:\n{0}", chosen));
            return;
        }
        var target = Sorter.Norm(Path.GetFullPath(chosen)).TrimEnd('\\');
        var subfolders = checkSubfolders.IsChecked == true;
        bool Inside(DuplicateEntry e)
        {
            var parent = ParentKey(e).TrimEnd('\\');
            return parent == target || (subfolders && parent.StartsWith(target + "\\", StringComparison.Ordinal));
        }

        if (!shown.Any(group => group.Files.Any(Inside)))
        {
            Ui.Info("Duplicates", Ui.T("None of the listed files are in\n{0}", Path.GetFullPath(chosen)));
            return;
        }
        CheckBy(files => files.Where(Inside));
    }

    // --- acting on files ---

    private void OpenRowMenu(MouseButtonEventArgs e)
    {
        if (Ui.RowAt(e.OriginalSource) is not { } clicked)
            return;
        if (!list.SelectedItems.Contains(clicked))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(clicked);
        }
        var rows = FileRows(Ui.Selected(list));
        var entries = rows.Select(row => (DuplicateEntry)row.Tag!).ToList();
        if (entries.Count == 0)
            return;
        var allChecked = rows.All(row => row.Checked);
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item(allChecked ? "Uncheck" : "Check", () => SetChecked(rows, !allChecked)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Show in File Explorer", () => ShowInExplorer(entries[0].Path)));
        menu.Items.Add(Ui.Item("Open / Run", () => OpenFiles(entries)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Remove from this list (keeps the file)", () => Forget(entries.Select(entry => Sorter.Norm(entry.Path)).ToHashSet())));
        menu.Items.Add(Ui.Item("Delete…", () => DeleteEntries(entries)));
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
            Ui.Info("File", Ui.T("This file is not there any more."));
        else if (Shell.Reveal(path) is { } error)
            Ui.Warn("File", error);
    }

    private static void OpenFiles(List<DuplicateEntry> entries)
    {
        var errors = entries.Select(entry => Shell.Open(entry.Path) is { } error ? $"{entry.Name}: {error}" : null).OfType<string>().ToList();
        if (errors.Count > 0)
            Ui.Warn("Open", Ui.T("Could not open:\n{0}", string.Join("\n", errors.Take(8))));
    }

    /// <summary>Take files off the list (deleted, or the user removed them); groups left with one file go.</summary>
    private void Forget(HashSet<string> keys)
    {
        groups = groups.Select(group => group with { Files = group.Files.Where(e => !keys.Contains(Sorter.Norm(e.Path))).ToList() })
                       .Where(group => group.Files.Count > 1).ToList();
        checkedPaths.ExceptWith(keys);
        Render();
    }

    private void DeleteEntries(List<DuplicateEntry> entries)
    {
        if (entries.Count == 0)
        {
            Ui.Info("Duplicates", Ui.T("There is nothing to delete. Check one or more files first (click the box next to a file, or use Check…)."));
            return;
        }
        var doomed = entries.Select(e => Sorter.Norm(e.Path)).ToHashSet();
        // Count against the whole group, filtered-out copies included: those would survive.
        var emptied = groups.Count(group => group.Files.All(e => doomed.Contains(Sorter.Norm(e.Path))));
        var listing = string.Join("\n", entries.Take(12).Select(e => e.Name)) + (entries.Count > 12 ? Ui.T("\n… and {0} more", entries.Count - 12) : "");
        var warning = emptied > 0 ? Ui.T("\n\nWARNING: in {0} group(s) every copy is selected, so no copy of those files will be left.", emptied) : "";
        var how = DeleteHowDialog.Ask(Window.GetWindow(this)!,
                                      Ui.T("Delete {0} file(s) ({1})?\n\n{2}{3}", entries.Count, Ui.FormatSize(entries.Sum(e => e.Size)), listing, warning));
        if (how is null)
            return;
        var permanent = how == "permanent";
        if (permanent && !Ui.Ask("Delete for good", Ui.T("The {0} file(s) will be gone for good: they won't go to the Recycle Bin and History can't bring them back.\n\nDelete them?",
                                                         entries.Count), warning: true))
            return;
        var (count, errors) = Sorter.DeleteFiles(entries.Select(e => e.Path).ToList(), permanent);
        Forget(entries.Where(e => !File.Exists(e.Path)).Select(e => Sorter.Norm(e.Path)).ToHashSet());
        Main.RefreshPreview();
        Ui.ReportResult(permanent ? Ui.T("Deleted {0} file(s) for good.", count)
                                  : Ui.T("Sent {0} file(s) to the Recycle Bin. You can bring them back from History.", count), errors);
    }
}
