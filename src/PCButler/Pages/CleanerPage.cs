// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>Find junk files (temporary files, caches, crash dumps...) and clean the ticked kinds.</summary>
public sealed class CleanerPage : Page
{
    private readonly Button scanButton;
    private readonly Button stopButton;
    private readonly Button cleanButton;
    private readonly StatCard[] stats;
    private readonly TextBlock summary;
    private readonly ProgressBar progress = new() { Width = 140, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ListView list;
    private readonly Dictionary<string, Row> rows = [];
    private readonly HashSet<string> ticked;
    private Dictionary<string, CleanerFound> found = []; // category key -> what the last scan found
    private CancellationTokenSource? cancel;
    private bool busy;

    public CleanerPage(MainWindow main) : base(main, "Cleaner", "Clear the clutter. Make room for what’s next.")
    {
        scanButton = AddAction("Scan now", Scan, accent: true);
        stopButton = AddAction("Stop scan", () => cancel?.Cancel());
        stopButton.IsEnabled = false;
        stats = AddStats(("Space to recover", "\uEDA2"), ("Items selected", "\uE71C"), ("Last scan", "\uE823"));
        stats[0].Set("—", "", Ui.T("Ticked items, after a scan"));
        stats[2].Set(Ui.T("Never"), "", Ui.T("Only files older than {0} hours", Cleaner.MinAgeHours));

        var panel = new CardPanel("What to clean", "\uE74D");
        panel.SetCount(Ui.T("{0} items", Cleaner.Categories.Count));
        summary = Ui.Muted(Ui.T("Nothing is deleted until you click Clean."));
        panel.Controls.Children.Add(summary);
        panel.Controls.Children.Add(progress);
        var footer = panel.AddFooter();
        cleanButton = Ui.Button(Ui.T("Clean checked"), Clean, accent: true).Dock(Dock.Right);
        cleanButton.IsEnabled = false;
        footer.Children.Add(cleanButton);
        footer.Children.Add(Ui.Muted(Ui.T("Only files left untouched for {0} hours are included. Everything cleaned is listed in History.",
                                          Cleaner.MinAgeHours)));

        list = Ui.List(("What to clean", 250, "CheckCell"), ("Files", 70), ("Size", 90), ("Details", 520));
        list.SelectionMode = SelectionMode.Single;
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && list.SelectedItem is Row row)
            {
                row.Checked = !row.Checked;
                e.Handled = true;
            }
        };
        list.MouseDoubleClick += (_, e) =>
        {
            // Double-click opens the folder the files are in.
            if (Ui.RowAt(e.OriginalSource) is { Tag: string key } && found.TryGetValue(key, out var result) && result.Folders.Count > 0)
                Shell.Open(result.Folders[0]);
        };
        var saved = main.Config.Cleaner.Categories;
        ticked = (saved ?? Cleaner.Categories.Where(category => category.Checked).Select(category => category.Key)).ToHashSet();
        foreach (var category in Cleaner.Categories)
        {
            var row = new Row { Name = Ui.T(category.Label), Tag = category.Key };
            row.SetChecked(ticked.Contains(category.Key));
            row.CheckedChanged = changed => Toggle(category.Key, changed.Checked);
            rows[category.Key] = row;
            Paint(category.Key);
        }
        list.ItemsSource = rows.Values.ToList();
        stats[1].Set(ticked.Count.ToString(), Ui.T("of {0}", Cleaner.Categories.Count), Ui.T("Your personal files are untouched"));
        panel.SetBody(list);
        Children.Add(panel);
    }

    private void Paint(string key)
    {
        found.TryGetValue(key, out var result);
        rows[key].Cells = [result is null ? "" : result.Files.Count.ToString(), result is null ? "" : Ui.FormatSize(result.Size),
                           Ui.T(Cleaner.ByKey(key).Description)];
    }

    private void Toggle(string key, bool on)
    {
        if (on)
            ticked.Add(key);
        else
            ticked.Remove(key);
        Main.UpdateConfig(config => config.Cleaner.Categories = [.. ticked.Order(StringComparer.Ordinal)]);
        stats[1].Set(ticked.Count.ToString(), Ui.T("of {0}", Cleaner.Categories.Count));
        UpdateSummary();
    }

    private List<CleanerFound> Chosen() => found.Where(pair => ticked.Contains(pair.Key)).Select(pair => pair.Value).ToList();

    private void UpdateSummary()
    {
        if (found.Count == 0)
            return;
        var chosen = Chosen();
        var count = chosen.Sum(result => result.Files.Count);
        var size = chosen.Sum(result => result.Size);
        var parts = Ui.FormatSize(size).Split(' ');
        stats[0].Set(parts[0], parts[1]);
        summary.Text = count > 0 ? Ui.T("{0} file(s), {1} can be cleaned.", count, Ui.FormatSize(size)) : Ui.T("Nothing to clean in the ticked items.");
        cleanButton.IsEnabled = count > 0;
    }

    private void SetBusy(bool value, bool determinate = false)
    {
        busy = value;
        scanButton.IsEnabled = !value;
        stopButton.IsEnabled = value;
        if (value)
        {
            cleanButton.IsEnabled = false;
            progress.IsIndeterminate = !determinate;
            progress.Value = 0;
        }
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Run work off the window's thread, showing the status it reports, then finish back on it.
    /// Returns false if it failed (the user has been told) or was stopped before it began.
    /// </summary>
    private async Task<(bool Done, T Result)> RunJob<T>(string status, Func<CancellationToken, Action<string, double?>, T> work)
    {
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        string text = Ui.T(status);
        double? fraction = null;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        ticker.Tick += (_, _) =>
        {
            summary.Text = text;
            if (fraction is { } value)
                progress.Value = value * 100;
        };
        ticker.Start();
        try
        {
            var result = await Task.Run(() => work(token, (newText, newFraction) => (text, fraction) = (newText, newFraction)));
            return (true, result);
        }
        catch (OperationCanceledException)
        {
            summary.Text = Ui.T("Scan stopped.");
            return (false, default!);
        }
        catch (Exception error) // shown to the user, not lost with the thread
        {
            summary.Text = Ui.T("Something went wrong.");
            Ui.Warn("Cleaner", error.Message);
            return (false, default!);
        }
        finally
        {
            ticker.Stop();
            SetBusy(false);
        }
    }

    /// <summary>The scan the program starts by itself at launch, as if Scan now were pressed.</summary>
    public void ScanNow()
    {
        if (Cleaner.Categories.Any(category => ticked.Contains(category.Key)))
            Scan(); // with nothing ticked there is nothing to look for, and no need to say so at launch
    }

    private async void Scan()
    {
        if (busy)
            return;
        var keys = Cleaner.Categories.Select(category => category.Key).Where(ticked.Contains).ToList();
        if (keys.Count == 0)
        {
            Ui.Info("Cleaner", Ui.T("Tick at least one item to look for."));
            return;
        }
        found = [];
        foreach (var category in Cleaner.Categories)
            Paint(category.Key);
        SetBusy(true);
        var (done, results) = await RunJob("Looking for junk files…", (token, report) =>
            Cleaner.Scan(keys, token, (text, arguments) =>
                report(Ui.T(text, arguments.Select(argument => argument is string label ? Ui.T(label) : argument).ToArray()), null)));
        if (!done)
            return;
        found = results.ToDictionary(result => result.Category.Key);
        stats[2].Set(DateTime.Now.ToString("HH:mm"), Ui.T("today"));
        foreach (var key in found.Keys)
            Paint(key);
        UpdateSummary();
    }

    private async void Clean()
    {
        var chosen = Chosen();
        var paths = chosen.SelectMany(result => result.Files).ToList();
        if (paths.Count == 0 || busy)
            return;
        var how = DeleteHowDialog.Ask(Window.GetWindow(this)!, Ui.T(
            "Clean {0} file(s) ({1})?\n\nFiles a program is using are skipped. Deleting for good frees the space now; "
            + "the Recycle Bin holds on to it until it is emptied.", paths.Count, Ui.FormatSize(chosen.Sum(result => result.Size))));
        if (how is null)
            return;
        var permanent = how == "permanent";
        var sizes = new Dictionary<string, long>();
        foreach (var path in paths)
        {
            try
            {
                sizes[path] = new FileInfo(path).Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                sizes[path] = 0;
            }
        }
        var roots = chosen.SelectMany(result => result.Folders).ToList();
        SetBusy(true, determinate: true);
        var stopped = false;
        var (done, result) = await RunJob("Cleaning…", (token, report) =>
        {
            var outcome = Sorter.DeleteFiles(paths, permanent, (number, count) =>
                report(Ui.T("Cleaning {0} of {1}…", number, count), (double)number / count), token);
            stopped = token.IsCancellationRequested;
            Cleaner.RemoveEmptyFolders(roots);
            return outcome;
        });
        if (!done)
            return;
        var (count, errors) = result;
        var gone = paths.Where(path => !File.Exists(path)).ToHashSet();
        foreach (var item in found.Values)
        {
            item.Files.RemoveAll(gone.Contains);
            item.Size = item.Files.Sum(path => sizes.GetValueOrDefault(path));
            Paint(item.Category.Key);
        }
        UpdateSummary();
        Main.RefreshPreview();
        // Built from whole sentences so each can be translated.
        var freed = Ui.FormatSize(gone.Sum(path => sizes.GetValueOrDefault(path)));
        var message = permanent ? Ui.T("Cleaned {0} file(s), {1} deleted for good. They are listed in History.", count, freed)
                                : Ui.T("Cleaned {0} file(s), {1} sent to the Recycle Bin. They are listed in History.", count, freed);
        if (stopped)
            message += " " + Ui.T("Stopped before the end.");
        if (errors.Count > 0)
            message += "\n\n" + Ui.T("{0} file(s) were skipped because a program is using them or they need administrator rights.", errors.Count);
        Ui.Info("Cleaner", message);
    }

    public override void Closing() => cancel?.Cancel();
}
