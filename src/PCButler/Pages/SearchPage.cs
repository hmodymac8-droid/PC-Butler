// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCButler.Core.Search;

namespace PCButler;

/// <summary>
/// Search: every file and folder on the indexed drives, found as you type. The search runs on a
/// background thread; a newer keystroke cancels the one before.
/// </summary>
public sealed class SearchPage : Page
{
    private readonly TextBox box;
    private readonly SearchResults results = new();
    private readonly TextBlock status = Ui.Muted("", 12.5);
    private readonly TextBlock state = Ui.Muted("", 12.5);
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    // A search kept unchanged this long goes into the recent searches.
    private readonly DispatcherTimer remember = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly WrapPanel recent = new() { Margin = new Thickness(30, 0, 0, 10) };
    private CancellationTokenSource? running;
    private bool changedWhileAway;

    public SearchPage(MainWindow main) : base(main, "Search", "Every file and folder on your drives, found as you type.")
    {
        AddAction("Search settings", () => main.Settings.ShowTab("search"));
        box = Ui.Field(hint: Ui.T("Search files and folders…  (space = and, | = or, ! = not, *.pdf, ext:jpg;png, folder:, path:)"));
        box.FontSize = 16;
        box.TextChanged += (_, _) =>
        {
            typing.Stop();
            typing.Start();
            remember.Stop();
            remember.Start();
        };
        remember.Tick += (_, _) =>
        {
            remember.Stop();
            SearchHistory.Add(box.Text);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down:
                    results.Focus();
                    results.Step(results.SelectedIndex < 0 ? 0 : 1);
                    break;
                case Key.Enter:
                    SearchHistory.Add(box.Text);
                    results.ChooseFirst();
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                        results.OpenFolderChosen();
                    else
                        results.OpenChosen();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        };
        typing.Tick += (_, _) =>
        {
            typing.Stop();
            Run();
        };
        // Files that come and go while the page is open show up in the list within a second.
        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            if (IsVisible)
                Run(keepPlace: true);
            else
                changedWhileAway = true;
        };
        results.SortChanged += () => Run();
        results.ChoiceChanged += ShowStatus;
        results.Opened += () => SearchHistory.Add(box.Text);
        SearchHistory.Changed += ShowRecent;

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Ui.Glyph("", 18, "Primary").Margin(0, 0, 12, 0).Dock(Dock.Left));
        top.Children.Add(box);
        Children.Add(top.Dock(Dock.Top));
        Children.Add(recent.Dock(Dock.Top));
        ShowRecent();
        var bar = new DockPanel { Margin = new Thickness(4, 8, 4, 0) };
        bar.Children.Add(state.Dock(Dock.Right));
        bar.Children.Add(status);
        Children.Add(bar.Dock(Dock.Bottom));
        Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = results });

        var engine = App.Current.Search;
        engine.Changed += OnIndexChanged;
        engine.StateChanged += OnStateChanged;
        ShowState();
    }

    public override void Shown()
    {
        box.Focus();
        box.SelectAll();
        if (results.Result is null || changedWhileAway)
        {
            changedWhileAway = false;
            Run(keepPlace: results.Result is not null);
        }
    }

    /// <summary>Open the page with a search already typed (from the search box).</summary>
    public void SearchFor(string text)
    {
        box.Text = text;
        box.CaretIndex = text.Length;
        box.Focus();
        Run();
    }

    /// <summary>The recent searches under the box: a click runs one again, a right-click takes it out.</summary>
    private void ShowRecent()
    {
        recent.Children.Clear();
        var entries = SearchHistory.Entries;
        recent.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0)
            return;
        recent.Children.Add(Ui.Muted(Ui.T("Recent searches"), 12.5).Margin(0, 0, 10, 4));
        foreach (var entry in entries.Take(12))
        {
            var chip = Ui.Button(entry.Length > 40 ? entry[..40] + "…" : entry, () => SearchFor(entry));
            chip.ToolTip = entry;
            chip.FontSize = 12.5;
            chip.Padding = new Thickness(10, 2, 10, 2);
            chip.Margin = new Thickness(0, 0, 6, 4);
            chip.ContextMenu = new ContextMenu { Items = { Ui.Item("Remove from recent searches", () => SearchHistory.Remove(entry)) } };
            recent.Children.Add(chip);
        }
        var clear = Ui.Text(Ui.T("Clear"), 12.5, "Primary").Margin(6, 0, 0, 4);
        clear.Cursor = Cursors.Hand;
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.MouseLeftButtonUp += (_, _) => SearchHistory.Clear();
        recent.Children.Add(clear);
    }

    private void OnIndexChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!refresh.IsEnabled)
            refresh.Start();
    });

    private void OnStateChanged() => Dispatcher.BeginInvoke(ShowState, DispatcherPriority.Background);

    private void ShowState()
    {
        var engine = App.Current.Search;
        state.Text = engine.State switch
        {
            SearchState.Off => Ui.T("Search is switched off in Settings."),
            SearchState.Loading => Ui.T("Loading the index…"),
            SearchState.Scanning when engine.Index.Live > 0 => Ui.T("Updating the index: {0:N0} found so far. Searches use the last index meanwhile.", engine.Scanned),
            SearchState.Scanning => Ui.T("Indexing your drives: {0:N0} found so far…", engine.Scanned),
            _ => Ui.T("{0:N0} files and folders indexed", engine.Index.Live),
        };
    }

    private async void Run(bool keepPlace = false)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        var (query, sort, descending) = (box.Text, results.Sort, results.Descending);
        var engine = App.Current.Search;
        try
        {
            var result = await Task.Run(() => engine.Search(query, sort, descending, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            results.Show(result, keepPlace);
            ShowStatus();
            // A big search (millions of results) is not run again every second while files change.
            refresh.Interval = TimeSpan.FromMilliseconds(Math.Clamp(result.Took.TotalMilliseconds * 10, 1000, 10000));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowStatus()
    {
        if (results.Result is not { } result)
            return;
        var text = Ui.T("{0:N0} results", result.Rows.Length) + "  ·  " + Ui.T("{0} ms", (int)result.Took.TotalMilliseconds);
        var count = results.SelectedItems.Count;
        if (count > 0)
        {
            long bytes = 0;
            // The window never waits for the index here: the size is left out while it is busy.
            if (count <= 10000 && result.Index.Gate.TryEnterReadLock(0))
            {
                try
                {
                    foreach (var row in results.SelectedItems.OfType<SearchRow>())
                        bytes += Math.Max(0, result.Index.Size(row.Row));
                }
                finally
                {
                    result.Index.Gate.ExitReadLock();
                }
            }
            text += "  ·  " + Ui.T("{0:N0} chosen", count) + (bytes > 0 ? $" ({Ui.FormatSize(bytes)})" : "");
        }
        status.Text = text;
    }
}
