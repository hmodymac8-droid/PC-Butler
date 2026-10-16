// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;
using PCButler.Core.Search;

namespace PCButler;

/// <summary>The Search tab of the Settings page, and the Win-key fallback of the Hotkeys tab.</summary>
public sealed partial class SettingsPage
{
    /// <summary>Search: which drives and folders are indexed, what is left out, how names match. Applied with its own Save button.</summary>
    private ScrollViewer SearchTab()
    {
        var saved = Main.Config.Search;
        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };

        // How the index stands, kept up to date while the tab is open.
        var state = Ui.Muted("", 12);
        var rescan = Ui.Button(Ui.T("Index again now"), () => App.Current.Search.Rescan());
        rows.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Index"), 13, weight: FontWeights.Bold), state, rescan, first: true));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void ShowState()
        {
            var engine = App.Current.Search;
            var index = engine.Index;
            state.Text = engine.State switch
            {
                SearchState.Off => Ui.T("Search is switched off."),
                SearchState.Loading => Ui.T("Loading the index…"),
                SearchState.Scanning => Ui.T("Indexing: {0:N0} found so far. Searches use the last index meanwhile.", engine.Scanned),
                _ => Ui.T("{0:N0} files and folders, {1} of memory, {2} on disk. Last walked: {3}.", index.Live, Ui.FormatSize(index.Bytes),
                          Ui.FormatSize(engine.FileBytes), engine.ScannedAt == DateTime.MinValue ? Ui.T("never") : engine.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            };
        }
        timer.Tick += (_, _) => ShowState();
        IsVisibleChanged += (_, _) => timer.IsEnabled = IsVisible;
        ShowState();

        var enabled = Ui.Switch("", saved.Enabled);
        rows.Children.Add(Ui.SettingRow(Ui.T("File search"), Ui.T("Keeps an index of the names on your drives, so the Search page and the search box find files at once. "
                                                                  + "It lives in memory while PC Butler runs."), enabled));

        // Drives: every fixed drive is ticked unless the user chose some.
        var drives = new WrapPanel();
        var all = SearchSettings.FixedDrives();
        foreach (var drive in all)
            drives.Children.Add(new CheckBox { Content = drive, IsChecked = saved.Drives is null || saved.Drives.Contains(drive, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 0, 14, 0) });
        rows.Children.Add(Ui.SettingRow(Ui.T("Drives"), Ui.T("The fixed drives to index. A big drive takes some minutes the first time; after that, changes are followed as they happen."), drives));

        var folderList = new ListBox { Height = 70, Width = 360 };
        foreach (var folder in saved.Folders)
            folderList.Items.Add(folder);
        var folderButtons = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        folderButtons.Children.Add(Ui.Button(Ui.T("Add…"), () =>
        {
            if (Ui.PickFolder("Search") is { } chosen && !folderList.Items.Contains(chosen))
                folderList.Items.Add(chosen);
        }));
        folderButtons.Children.Add(Ui.Button(Ui.T("Remove"), () =>
        {
            if (folderList.SelectedItem is { } chosen)
                folderList.Items.Remove(chosen);
        }).Margin(0, 4));
        rows.Children.Add(Ui.SettingRow(Ui.T("More folders"), Ui.T("Folders on USB drives or network shares to index too."), Ui.Row(0, folderList, folderButtons)));

        var exclude = Ui.Field(string.Join(Environment.NewLine, saved.Exclude), 360);
        exclude.AcceptsReturn = true;
        exclude.Height = 90;
        exclude.TextWrapping = TextWrapping.NoWrap;
        exclude.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        rows.Children.Add(Ui.SettingRow(Ui.T("Leave out"), Ui.T("One per line: a full folder path, or a name or pattern such as node_modules or *.tmp."), exclude));

        CheckBox Toggle(string title, string detail, bool on)
        {
            var toggle = Ui.Switch("", on);
            rows.Children.Add(Ui.SettingRow(Ui.T(title), detail.Length > 0 ? Ui.T(detail) : "", toggle));
            return toggle;
        }
        var otherUsers = Toggle("Leave out other users' folders", "The profile folders of the PC's other accounts.", saved.ExcludeOtherUsers);
        var hidden = Toggle("Leave out hidden files", "", saved.ExcludeHidden);
        var system = Toggle("Leave out system files", "", saved.ExcludeSystem);
        var size = Toggle("Keep file sizes", "For the Size column. About 8 bytes of memory per file.", saved.IndexSize);
        var date = Toggle("Keep dates modified", "For the Date modified column. About 4 bytes of memory per file.", saved.IndexModified);
        var matchCase = Toggle("Match case", "Upper and lower case must match. One search can change this with case: or nocase:.", saved.MatchCase);
        var diacritics = Toggle("Match accents and tashkeel", "Off: محمد also finds مُحَمَّد, and resume finds résumé. One search can change this with diacritics:.", saved.MatchDiacritics);
        var variants = Toggle("Arabic letter variants are the same", "أ إ آ and ا, ى and ي, ة and ه are treated as one letter.", saved.ArabicVariants);
        var hours = Ui.Field(saved.RescanHours.ToString(), 70);
        rows.Children.Add(Ui.SettingRow(Ui.T("Walk the folders again at start after (hours)"),
                                        Ui.T("Catches what changed while PC Butler was closed. 0 = only when you press Index again now."), hours));

        var save = Ui.Button(Ui.T("Save search settings"), () =>
        {
            if (!int.TryParse(hours.Text.Trim(), out var every) || every < 0)
            {
                Ui.Warn("Search", Ui.T("Hours must be a whole number, 0 or more."));
                return;
            }
            var ticked = drives.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (string)box.Content).ToList();
            var settings = new SearchSettings
            {
                Enabled = enabled.IsChecked == true,
                Drives = ticked.Count == all.Count ? null : ticked,
                Folders = folderList.Items.Cast<string>().ToList(),
                Exclude = exclude.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList(),
                ExcludeOtherUsers = otherUsers.IsChecked == true, ExcludeHidden = hidden.IsChecked == true, ExcludeSystem = system.IsChecked == true,
                IndexSize = size.IsChecked == true, IndexModified = date.IsChecked == true,
                MatchCase = matchCase.IsChecked == true, MatchDiacritics = diacritics.IsChecked == true, ArabicVariants = variants.IsChecked == true,
                RescanHours = every,
            };
            var walks = settings.Enabled && settings.Fingerprint() != Main.Config.Search.Fingerprint();
            Main.UpdateConfig(config => config.Search = settings);
            App.Current.Search.Apply(settings);
            ShowState();
            Ui.Info("Search", Ui.T(!settings.Enabled ? "Search settings saved. File search is off."
                                   : walks ? "Search settings saved. The folders are being indexed again in the background; searches keep working meanwhile."
                                   : "Search settings saved."));
        }, accent: true);
        rows.Children.Add(new Border
        {
            BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel { LastChildFill = false, Margin = new Thickness(24, 14, 24, 6), Children = { save.Dock(Dock.Right) } },
        });
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>
    /// The keyboard hook could not be used for a Win shortcut: offer to have File Explorer give
    /// up Win + that letter instead (it takes effect when File Explorer restarts).
    /// </summary>
    private bool OfferExplorerKey(string keys, string problem)
    {
        if (!Shortcut.TryParse(keys, out _, out var key) || key < System.Windows.Input.Key.A || key > System.Windows.Input.Key.Z)
        {
            Ui.Warn("Hotkeys", problem);
            return false;
        }
        var letter = key.ToString()[0];
        if (!Ui.Ask("Hotkeys", problem + "\n\n" + Ui.T("PC Butler can ask File Explorer to give up Win + {0} instead. That takes effect when File Explorer restarts, "
                                                       + "and is undone when you choose another shortcut.\n\nDo that, and restart File Explorer now?", letter)))
            return false;
        if (WinKeyHook.FreeFromExplorer(letter))
            Main.UpdateConfig(config => config.HotkeysFromExplorer += letter);
        Task.Run(ExplorerMenu.RestartExplorer).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            foreach (var (action, error) in App.Current.Keys.Apply(Main.Config.Hotkeys))
                Log.Warning($"Hotkey for {action}: {error}");
        }));
        return true;
    }

    /// <summary>Give File Explorer back the Win + letter shortcuts no PC Butler shortcut uses any more.</summary>
    private void ReturnExplorerKeys()
    {
        var taken = Main.Config.HotkeysFromExplorer;
        if (taken.Length == 0)
            return;
        var used = Hotkeys.Effective(Main.Config.Hotkeys).Values
            .Select(keys => Shortcut.TryParse(keys, out var modifiers, out var key) && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? key.ToString() : "")
            .Where(name => name.Length == 1).Select(name => name[0]).ToHashSet();
        var kept = new string(taken.Where(used.Contains).ToArray());
        foreach (var letter in taken.Where(letter => !used.Contains(letter)))
            WinKeyHook.ReturnToExplorer(letter);
        if (kept != taken)
            Main.UpdateConfig(config => config.HotkeysFromExplorer = kept);
    }
}
