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

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Ui.Glyph("", 18, "Primary").Margin(0, 0, 12, 0).Dock(Dock.Left));
        top.Children.Add(box);
        Children.Add(top.Dock(Dock.Top));
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
        Run();
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
