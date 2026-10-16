// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;
using PCButler.Core.Search;

namespace PCButler;

/// <summary>
/// One line of the results, made only while it is on screen: it reads its name, folder, size and
/// date from the index once, the first time the list draws it. Two lines are the same line when
/// they show the same row of the same result, so selection survives scrolling.
///
/// Drawing never waits for the index: while it is being changed, saved or sorted, the line shows
/// blank and fills in a moment later. Waiting there froze the window ("Not Responding").
/// </summary>
public sealed class SearchRow(SearchResult result, int position) : INotifyPropertyChanged
{
    private sealed record Seen(string Name, string Folder, bool IsFolder, long Size, DateTime? Modified);

    private static readonly HashSet<SearchRow> Waiting = new(ReferenceEqualityComparer.Instance);
    private static DispatcherTimer? retry;
    private Seen? seen;

    public SearchResult Result { get; } = result;
    public int Position { get; } = position;
    public int Row => Result.Rows[Position];
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the list draws: read without waiting, or null for now.</summary>
    private Seen? Shown
    {
        get
        {
            if (seen is null && !Load(wait: false))
                Retry(this);
            return seen;
        }
    }

    /// <summary>What an action (open, copy, rename, delete) needs: waits for the index if it must.</summary>
    private Seen Sure
    {
        get
        {
            if (seen is null)
                Load(wait: true);
            return seen!;
        }
    }

    private bool Load(bool wait)
    {
        var index = Result.Index;
        if (wait)
            index.Gate.EnterReadLock();
        else if (!index.Gate.TryEnterReadLock(0))
            return false;
        try
        {
            var row = Row;
            seen = new Seen(index.Name(row), index.FolderOf(row), index.IsFolder(row), index.Size(row), index.Modified(row));
            return true;
        }
        finally
        {
            index.Gate.ExitReadLock();
        }
    }

    /// <summary>Lines that could not be read are tried again a little later, and redrawn once read.</summary>
    private static void Retry(SearchRow row)
    {
        Waiting.Add(row);
        if (retry is null)
        {
            retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            retry.Tick += (_, _) =>
            {
                foreach (var waiting in Waiting.ToList())
                {
                    if (!waiting.Load(wait: false))
                        break; // still busy; the others wait on the same lock
                    Waiting.Remove(waiting);
                    waiting.PropertyChanged?.Invoke(waiting, new PropertyChangedEventArgs(string.Empty));
                }
                if (Waiting.Count == 0)
                    retry!.Stop();
            };
        }
        retry.Start();
    }

    // Drawn in the list.
    public string Title => Shown?.Name ?? "";
    public string Place => Shown?.Folder ?? "";
    public string Glyph => Shown is { } line ? line.IsFolder ? "" : "" : "";
    public Brush GlyphBrush => Ui.Paint(Shown?.IsFolder == true ? "Amber" : "Muted");
    public string Size => Shown is { Size: >= 0 } line ? Ui.FormatSize(line.Size) : "";
    public string Modified => Shown?.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    /// <summary>The list's row style asks every row whether to draw it faded.</summary>
    public bool Dim => false;

    // Used by the actions.
    public string Name => Sure.Name;
    public string Folder => Sure.Folder;
    public bool IsFolder => Sure.IsFolder;
    public string FullPath => Sure.Folder.Length == 0 ? Sure.Name : SearchIndex.Join(Sure.Folder, Sure.Name);

    public override bool Equals(object? other) => other is SearchRow row && row.Result == Result && row.Position == Position;
    public override int GetHashCode() => HashCode.Combine(Result, Position);
    public override string ToString() => Title;
}

/// <summary>
/// The results as a list the window can show: it answers "how many" and "which one at this
/// place" without making an object per result, so a million results cost nothing until drawn.
/// </summary>
public sealed class ResultList(SearchResult result) : IList
{
    public SearchResult Result { get; } = result;
    public int Count => Result.Rows.Length;
    public object? this[int index] { get => new SearchRow(Result, index); set => throw new NotSupportedException(); }
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int IndexOf(object? value) => value is SearchRow row && row.Result == Result && row.Position < Count ? row.Position : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (var position = 0; position < Count; position++)
            array.SetValue(new SearchRow(Result, position), index + position);
    }

    public IEnumerator GetEnumerator()
    {
        for (var position = 0; position < Count; position++)
            yield return new SearchRow(Result, position);
    }
}

/// <summary>
/// The results list of the Search page and the search box: virtualized over the rows of a
/// result, columns that sort when their heading is clicked (the search runs again in the new
/// order), and the actions on the chosen lines (open, open folder, copy path, rename, delete).
/// </summary>
public sealed class SearchResults : ListView
{
    private static readonly (SearchSort Sort, string Title)[] Columns =
        [(SearchSort.Name, "Name"), (SearchSort.Path, "Path"), (SearchSort.Size, "Size"), (SearchSort.Modified, "Date modified")];
    private readonly GridView view = new() { AllowsColumnReorder = false };
    private readonly bool compact;

    public SearchSort Sort { get; private set; } = SearchSort.Name;
    public bool Descending { get; private set; }
    public SearchResult? Result => (ItemsSource as ResultList)?.Result;
    /// <summary>The user asked for another order: the owner runs the search again.</summary>
    public event Action? SortChanged;
    /// <summary>Something was opened (the search box closes then).</summary>
    public event Action? Opened;
    /// <summary>The selection changed (for the status line).</summary>
    public event Action? ChoiceChanged;

    public SearchResults(bool compact = false)
    {
        this.compact = compact;
        SetResourceReference(StyleProperty, typeof(ListView));
        var rowStyle = new Style(typeof(ListViewItem), (Style)Application.Current.FindResource(typeof(ListViewItem)));
        rowStyle.Setters.Add(new Setter(MinHeightProperty, 32.0));
        ItemContainerStyle = rowStyle;
        SelectionMode = SelectionMode.Extended;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ScrollViewer.SetIsDeferredScrollingEnabled(this, false);
        View = view;
        view.Columns.Add(new GridViewColumn { Header = Ui.T("Name"), Width = compact ? 260 : 320, CellTemplate = NameCell() });
        view.Columns.Add(new GridViewColumn { Header = Ui.T("Path"), Width = compact ? 330 : 440, CellTemplate = TextCell(nameof(SearchRow.Place), muted: true) });
        if (!compact)
        {
            view.Columns.Add(new GridViewColumn { Header = Ui.T("Size"), Width = 100, CellTemplate = TextCell(nameof(SearchRow.Size), right: true) });
            view.Columns.Add(new GridViewColumn { Header = Ui.T("Date modified"), Width = 150, CellTemplate = TextCell(nameof(SearchRow.Modified)) });
        }
        // The path column takes up the slack.
        SizeChanged += (_, _) =>
        {
            var others = view.Columns.Where((_, index) => index != 1).Sum(column => column.Width);
            view.Columns[1].Width = Math.Max(160, ActualWidth - others - 30);
        };
        AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClicked));
        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && RowAt(e.OriginalSource) is not null)
                OpenChosen();
        };
        PreviewKeyDown += OnKey;
        SelectionChanged += (_, _) => ChoiceChanged?.Invoke();
        ContextMenuOpening += (_, e) =>
        {
            if (SelectedItems.Count == 0)
            {
                e.Handled = true;
                return;
            }
            ContextMenu = Menu();
        };
        ShowArrows();
    }

    private static DataTemplate NameCell()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(SearchRow.Glyph)));
        glyph.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(SearchRow.GlyphBrush)));
        glyph.SetValue(TextBlock.FontFamilyProperty, Ui.IconFont);
        glyph.SetValue(TextBlock.FontSizeProperty, 15.0);
        glyph.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(glyph);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SearchRow.Title)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }

    private static DataTemplate TextCell(string property, bool muted = false, bool right = false)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        if (muted)
            text.SetValue(TextBlock.ForegroundProperty, Ui.Paint("Muted"));
        if (right)
        {
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            text.SetValue(MarginProperty, new Thickness(0, 0, 12, 0));
        }
        return new DataTemplate { VisualTree = text };
    }

    private void HeaderClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column })
            return;
        var index = view.Columns.IndexOf(column);
        if (index < 0)
            return;
        var sort = Columns[index].Sort;
        if (sort == Sort)
            Descending = !Descending;
        else
            (Sort, Descending) = (sort, sort is SearchSort.Size or SearchSort.Modified); // biggest and newest first
        ShowArrows();
        SortChanged?.Invoke();
    }

    private void ShowArrows()
    {
        for (var index = 0; index < view.Columns.Count; index++)
            view.Columns[index].Header = Ui.T(Columns[index].Title) + (Columns[index].Sort == Sort ? (Descending ? "  ▼" : "  ▲") : "");
    }

    /// <summary>Show a result. A refresh of the same search keeps the place in the list and the chosen lines where they still exist.</summary>
    public void Show(SearchResult result, bool keepPlace = false)
    {
        var scroller = FindScroller(this);
        var offset = scroller?.VerticalOffset ?? 0;
        var chosen = keepPlace && SelectedItems.Count < 1000 ? SelectedItems.OfType<SearchRow>().Select(row => row.Row).ToHashSet() : [];
        ItemsSource = new ResultList(result);
        if (!keepPlace)
        {
            if (result.Rows.Length > 0)
                ScrollIntoView(Items[0]);
            return;
        }
        if (chosen.Count > 0 && chosen.Count < 1000)
        {
            for (var position = 0; position < result.Rows.Length && SelectedItems.Count < chosen.Count; position++)
                if (chosen.Contains(result.Rows[position]))
                    SelectedItems.Add(new SearchRow(result, position));
        }
        Dispatcher.BeginInvoke(() => FindScroller(this)?.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroller)
                return scroller;
            if (FindScroller(child) is { } found)
                return found;
        }
        return null;
    }

    private static SearchRow? RowAt(object source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ListViewItem { DataContext: SearchRow row })
                return row;
        }
        return null;
    }

    /// <summary>The chosen lines, in list order.</summary>
    public List<SearchRow> Chosen() => SelectedItems.OfType<SearchRow>().OrderBy(row => row.Position).ToList();

    /// <summary>Choose the first line (the search box does this as you type).</summary>
    public void ChooseFirst()
    {
        if (Items.Count > 0)
            SelectedIndex = 0;
    }

    /// <summary>Move the choice up or down a line, from the search box.</summary>
    public void Step(int by)
    {
        if (Items.Count == 0)
            return;
        SelectedIndex = Math.Clamp(SelectedIndex + by, 0, Items.Count - 1);
        ScrollIntoView(SelectedItem);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter when control:
                OpenFolderChosen();
                break;
            case Key.Enter:
                OpenChosen();
                break;
            case Key.C when control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                CopyNames();
                break;
            case Key.C when control:
                CopyPaths();
                break;
            case Key.F2:
                RenameChosen();
                break;
            case Key.Delete:
                DeleteChosen();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Open", OpenChosen));
        menu.Items.Add(Ui.Item("Open folder", OpenFolderChosen));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Copy full path", CopyPaths));
        menu.Items.Add(Ui.Item("Copy name", CopyNames));
        menu.Items.Add(new Separator());
        var rename = Ui.Item("Rename…", RenameChosen);
        rename.IsEnabled = SelectedItems.Count == 1;
        menu.Items.Add(rename);
        menu.Items.Add(Ui.Item("Delete (to Recycle Bin)", DeleteChosen));
        return menu;
    }

    // --- actions ---

    public void OpenChosen()
    {
        var chosen = Chosen().Take(10).ToList();
        foreach (var row in chosen)
        {
            var path = row.FullPath;
            if (!File.Exists(path) && !Directory.Exists(path))
                Ui.Warn("Open", Ui.T("This file is not there any more."));
            else if (Shell.Open(path) is { } error)
                Ui.Warn("Open", Ui.T("Could not open it:\n{0}", error));
        }
        if (chosen.Count > 0)
            Opened?.Invoke();
    }

    public void OpenFolderChosen()
    {
        if (Chosen().FirstOrDefault() is not { } row)
            return;
        var path = row.FullPath;
        try
        {
            // Selected in its folder, for files and folders alike.
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            else if (Directory.Exists(row.Folder))
                Shell.Open(row.Folder);
            Opened?.Invoke();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            Ui.Warn("Open folder", Ui.T("Could not open it:\n{0}", error.Message));
        }
    }

    public void CopyPaths() => Copy(Chosen().Select(row => row.FullPath));

    public void CopyNames() => Copy(Chosen().Select(row => row.Name));

    private static void Copy(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Ui.Warn("Copy", Ui.T("The clipboard is busy. Try again."));
        }
    }

    public void RenameChosen()
    {
        if (Chosen() is not [var row])
            return;
        var path = row.FullPath;
        var folder = Path.GetDirectoryName(path);
        if (folder is null || (!File.Exists(path) && !Directory.Exists(path)))
        {
            Ui.Warn("Rename", Ui.T("This file is not there any more."));
            return;
        }
        var owner = Window.GetWindow(this)!;
        if (TextDialog.Ask(owner, "Rename", Ui.T("New name for “{0}”:", row.Name), row.Name) is not { Length: > 0 } name || name == row.Name)
            return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.', ' ').Length == 0)
        {
            Ui.Warn("Rename", Ui.T("A name cannot contain \\ / : * ? \" < > |"));
            return;
        }
        var target = Path.Combine(folder, name);
        // Only a change of case may land on the same name; anything else already there stays untouched.
        if ((File.Exists(target) || Directory.Exists(target)) && !target.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Warn("Rename", Ui.T("There is already a file or folder called “{0}” there.", name));
            return;
        }
        try
        {
            if (Directory.Exists(path))
                Directory.Move(path, target);
            else
                File.Move(path, target);
            Log.Info($"Renamed {path} to {name}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Ui.Warn("Rename", Ui.T("Could not rename it:\n{0}", error.Message));
        }
    }

    /// <summary>Chosen files go to the Recycle Bin, logged in History so they can be brought back. Folders are left alone.</summary>
    public void DeleteChosen()
    {
        var chosen = Chosen();
        if (chosen.Count == 0)
            return;
        var files = chosen.Where(row => !row.IsFolder).Select(row => row.FullPath).Where(File.Exists).ToList();
        var folders = chosen.Count(row => row.IsFolder);
        if (files.Count == 0)
        {
            Ui.Info("Delete", Ui.T(folders > 0 ? "Folders are not deleted from the search results. Use Open folder and delete it in File Explorer."
                                               : "This file is not there any more."));
            return;
        }
        var question = files.Count == 1
            ? Ui.T("Move this file to the Recycle Bin?\n\n{0}\n\nYou can bring it back from the History tab.", Path.GetFileName(files[0]))
            : Ui.T("Move {0} files to the Recycle Bin?\n\nYou can bring them back from the History tab.", files.Count);
        if (folders > 0)
            question += "\n\n" + Ui.T("The {0} folder(s) among them are left alone.", folders);
        if (!Ui.Ask("Delete", question))
            return;
        var (count, errors) = Sorter.DeleteFiles(files);
        if (errors.Count > 0)
            Ui.ReportResult(Ui.T("Moved {0} file(s) to the Recycle Bin.", count), errors);
        App.Current.Workspace?.History.Refresh();
    }
}
