// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// See what takes up the space: a map of the biggest folders and files, and a list of
/// large files that have not been used for a long time.
/// </summary>
public sealed class StoragePage : Page
{
    private static readonly (string Label, long Bytes)[] Sizes =
    [
        ("100 MB", 100L << 20), ("500 MB", 500L << 20), ("1 GB", 1L << 30), ("5 GB", 5L << 30),
    ];
    private static readonly (string Label, int Months)[] Ages =
    [
        ("Any time", 0), ("1 month", 1), ("3 months", 3), ("6 months", 6), ("1 year", 12),
    ];

    private readonly Button scanButton;
    private readonly Button stopButton;
    private readonly StatCard[] stats;
    private readonly ComboBox location = new() { Width = 320 };
    private readonly List<string> locations = [];
    private readonly TextBlock status = Ui.Muted("");
    private readonly ProgressBar progress = new() { Width = 140, IsIndeterminate = true, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TreemapView map = new();
    private readonly TextBlock mapPath = Ui.Muted("", 13);
    private readonly Button upButton;
    private readonly StackPanel mapChosen;
    private readonly TextBlock mapChosenText = Ui.Text("", 13, weight: FontWeights.SemiBold);
    private readonly ComboBox minimumSize = new() { Width = 110 };
    private readonly ComboBox unusedFor = new() { Width = 130 };
    private readonly ListView bigList;
    private readonly TextBlock bigSummary = Ui.Muted("");
    private readonly StackPanel drives = new() { Margin = new Thickness(6, 4, 6, 4) };
    private TabControl tabs = null!;
    private StorageResult? result;
    private CancellationTokenSource? cancel;
    private bool busy;

    public StoragePage(MainWindow main) : base(main, "A little more breathing room.", "See what fills your disk. Decide what stays.")
    {
        scanButton = AddAction("Scan now", Scan, accent: true);
        stopButton = AddAction("Stop scan", () => cancel?.Cancel());
        stopButton.IsEnabled = false;
        stats = AddStats(("Space scanned", ""), ("Files counted", ""), ("Biggest folder", ""));
        foreach (var card in stats)
            card.Set("—");
        stats[0].Set("—", "", Ui.T("Nothing is changed by a scan"));

        // Where to look: your own folder, a whole drive, or any folder.
        AddLocation(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Ui.T("Your user folder"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true }))
            AddLocation(drive.RootDirectory.FullName, Ui.T("Drive {0}", drive.Name.TrimEnd('\\')));
        location.SelectedIndex = 0;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
        top.Children.Add(Ui.Row(0, Ui.Text(Ui.T("Look in")), location.Margin(12), Ui.Button(Ui.T("Browse…"), Browse).Margin(6)));
        top.Children.Add(Ui.Row(0, status, progress).Dock(Dock.Right));
        Children.Add(top.Dock(Dock.Top));

        tabs = new TabControl();

        // The map: every rectangle is a folder or file, sized by the space it takes.
        upButton = Ui.Button(Ui.T("Up"), () => { if (map.Node?.Parent is { } parent) ShowNode(parent); });
        var mapBar = new DockPanel { LastChildFill = false };
        mapBar.Children.Add(upButton);
        mapBar.Children.Add(mapPath.Margin(10));
        mapBar.Children.Add(Ui.Muted(Ui.T("Click a folder to look inside. Ctrl+click to choose several. Right-click for more.")).Dock(Dock.Right));
        // What was chosen with Ctrl+click, and what can be done with it.
        mapChosen = Ui.Row(8, mapChosenText, Ui.Button(Ui.T("Move drives…"), () => MoveDrives(map.Marked.ToList())),
                           Ui.Button(Ui.T("Clear"), map.ClearMarks));
        mapChosen.Visibility = Visibility.Collapsed;
        mapBar.Children.Add(mapChosen.Margin(16));
        map.MarksChanged += () =>
        {
            mapChosen.Visibility = map.Marked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            mapChosenText.Text = Ui.T("{0} chosen", map.Marked.Count);
        };
        map.MoveDrives += items => MoveDrives(items);
        var mapBody = new DockPanel { Margin = new Thickness(10) };
        mapBody.Children.Add(mapBar.Dock(Dock.Bottom));
        mapBody.Children.Add(new Border { Child = map, Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true });
        map.Opened += node =>
        {
            if (node.IsFolder)
                ShowNode(node);
        };
        tabs.Items.Add(new TabItem { Header = Ui.T("Space map"), Content = mapBody });

        // Large files nobody has touched for a while: the easiest space to win back.
        foreach (var (label, _) in Sizes)
            minimumSize.Items.Add(label);
        minimumSize.SelectedIndex = 2;
        foreach (var (label, _) in Ages)
            unusedFor.Items.Add(Ui.T(label));
        unusedFor.SelectedIndex = 3;
        minimumSize.SelectionChanged += (_, _) => RenderBigFiles();
        unusedFor.SelectionChanged += (_, _) => RenderBigFiles();
        bigList = Ui.List(("File name", 360, "CheckFileCell"), ("Size", 100), ("Last used", 140), ("Folder", 380));
        Ui.AttachFileMenu(bigList, row => ((BigFile)row.Tag!).Path, AfterDelete, ("Move drives…", _ => MoveDrives(Chosen().Select(file => file.Path).ToList())));
        var bigBar = new DockPanel { LastChildFill = false };
        bigBar.Children.Add(Ui.Row(0, Ui.Text(Ui.T("Bigger than")), minimumSize.Margin(8), Ui.Text(Ui.T("Not used for")).Margin(16),
                                   unusedFor.Margin(8), bigSummary.Margin(16)));
        bigBar.Children.Add(Ui.Row(8, Ui.Button(Ui.T("Select all"), () => TickAll(true)), Ui.Button(Ui.T("Select none"), () => TickAll(false)),
                                   Ui.Button(Ui.T("Move drives…"), () => MoveDrives(Chosen().Select(file => file.Path).ToList())),
                                   Ui.Button(Ui.T("Move selected to Recycle Bin"), DeleteSelected, accent: true)).Dock(Dock.Right));
        var bigBody = new DockPanel { Margin = new Thickness(10) };
        bigBody.Children.Add(bigBar.Dock(Dock.Bottom));
        bigBody.Children.Add(new Border { Child = bigList, Margin = new Thickness(0, 0, 0, 8) });
        tabs.Items.Add(new TabItem { Header = Ui.T("Large & old files"), Content = bigBody });

        // Every drive at a glance: how full it is, and a click to measure it.
        var drivesTab = new TabItem { Header = Ui.T("All drives"),
                                      Content = new ScrollViewer { Content = drives, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == tabs && tabs.SelectedItem == drivesTab)
                ShowDrives(); // free space changes, and drives come and go
        };
        tabs.Items.Add(drivesTab);
        ShowDrives();

        Children.Add(tabs);
        status.Text = Ui.T("Choose where to look, then click Scan now.");
        ShowNode(null);
    }

    /// <summary>One row per ready drive: its name, the free space, and a bar of the space used.</summary>
    private void ShowDrives()
    {
        drives.Children.Clear();
        var index = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            long total, free;
            string label;
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                total = drive.TotalSize;
                free = drive.TotalFreeSpace;
                label = drive.VolumeLabel;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (total <= 0)
                continue;
            var root = drive.RootDirectory.FullName;
            var letter = drive.Name.TrimEnd('\\');
            var tone = Ui.DriveTone(index++);

            var name = new TextBlock { FontSize = 13 * Ui.TextScale, Foreground = Ui.Paint("Text"), VerticalAlignment = VerticalAlignment.Center };
            name.Inlines.Add(new System.Windows.Documents.Run(label.Length > 0 ? label : Ui.T("Local disk")) { FontWeight = FontWeights.Bold });
            name.Inlines.Add(new System.Windows.Documents.Run($"  ({letter})") { Foreground = Ui.Paint("Muted") });
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(Ui.Muted(Ui.T("{0} free of {1}", Ui.FormatSize(free), Ui.FormatSize(total)), 12).Dock(Dock.Right));
            head.Children.Add(Ui.Row(10, Ui.Glyph("\uEDA2", 14), name));

            // The bar: the used part in the drive's colour over a dim track.
            var used = (double)(total - free) / total;
            var bar = new Grid { Height = 6 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(used, 0.001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - used, 0.001), GridUnitType.Star) });
            var track = new Border { Background = Ui.Paint("Raised"), CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Border { Background = used >= 0.9 ? Ui.Paint("Red") : tone, CornerRadius = new CornerRadius(3) });

            var row = new Border
            {
                Padding = new Thickness(14, 12, 14, 14), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { head, bar } },
                ToolTip = Ui.T("Click to measure this drive."),
            };
            row.MouseEnter += (_, _) => row.Background = Ui.Paint("Secondary");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => MeasureDrive(root);
            drives.Children.Add(row);
        }
        if (drives.Children.Count == 0)
            drives.Children.Add(Ui.Muted(Ui.T("No drives found.")).Margin(14, 12));
    }

    /// <summary>Picks the drive in "Look in", shows the map and starts measuring it.</summary>
    private void MeasureDrive(string root)
    {
        if (busy)
            return;
        var index = locations.FindIndex(known => Sorter.Norm(known) == Sorter.Norm(root));
        if (index < 0)
        {
            AddLocation(root, Ui.T("Drive {0}", root.TrimEnd('\\')));
            index = locations.Count - 1;
        }
        location.SelectedIndex = index;
        tabs.SelectedIndex = 0;
        Scan();
    }

    private void AddLocation(string path, string label)
    {
        locations.Add(path);
        location.Items.Add(label == path ? path : $"{label}  ({path})");
    }

    private void Browse()
    {
        if (Ui.PickFolder("Folder to measure") is not { } folder)
            return;
        var index = locations.FindIndex(known => Sorter.Norm(known) == Sorter.Norm(folder));
        if (index < 0)
        {
            AddLocation(folder, folder);
            index = locations.Count - 1;
        }
        location.SelectedIndex = index;
    }

    private void Scan()
    {
        if (busy || location.SelectedIndex < 0)
            return;
        var root = locations[location.SelectedIndex];
        if (!Directory.Exists(root))
        {
            Ui.Warn("Storage", Ui.T("Not a folder:\n{0}", root));
            return;
        }
        busy = true;
        scanButton.IsEnabled = false;
        stopButton.IsEnabled = true;
        progress.Visibility = Visibility.Visible;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var current = Ui.T("Looking for files…");
        StorageResult? scanned = null;
        Exception? failure = null;
        var stopped = false;
        // Measuring a whole drive takes a while, so it runs off the window's thread.
        var worker = new Thread(() =>
        {
            try
            {
                scanned = Storage.Scan(root, token, folder => current = folder);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failure = error;
            }
        }) { IsBackground = true };
        worker.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (worker.IsAlive)
            {
                status.Text = current;
                return;
            }
            timer.Stop();
            busy = false;
            scanButton.IsEnabled = true;
            stopButton.IsEnabled = false;
            progress.Visibility = Visibility.Collapsed;
            if (failure is not null)
            {
                status.Text = Ui.T("The scan failed.");
                Ui.Warn("Storage", Ui.T("The scan failed:\n{0}", failure.Message));
            }
            else if (stopped || scanned is null)
                status.Text = Ui.T("Scan stopped.");
            else
                ShowResult(scanned);
        };
        timer.Start();
    }

    private void ShowResult(StorageResult scanned)
    {
        result = scanned;
        map.ClearMarks();
        var root = scanned.Root;
        var parts = Ui.FormatSize(root.Size).Split(' ');
        stats[0].Set(parts[0], parts[1], root.Path);
        stats[1].Set(root.Files.ToString("N0"), "", scanned.Skipped > 0
            ? Ui.T("{0} folder(s) could not be read", scanned.Skipped) : Ui.T("Every folder was read"));
        var biggest = root.Children.FirstOrDefault(child => child.IsFolder);
        stats[2].Set(biggest?.Name ?? "—", "", biggest is null ? "" : Ui.FormatSize(biggest.Size));
        status.Text = Ui.T("{0} in {1} file(s).", Ui.FormatSize(root.Size), root.Files.ToString("N0"));
        ShowNode(root);
        RenderBigFiles();
    }

    private void ShowNode(StorageNode? node)
    {
        map.Show(node);
        mapPath.Text = node is null ? "" : $"{node.Path}   ·   {Ui.FormatSize(node.Size)}";
        upButton.IsEnabled = node?.Parent is not null;
    }

    private void RenderBigFiles()
    {
        if (result is null || minimumSize.SelectedIndex < 0 || unusedFor.SelectedIndex < 0)
        {
            bigList.ItemsSource = new List<Row>();
            bigSummary.Text = "";
            return;
        }
        var files = Storage.LargeAndOld(result, Sizes[minimumSize.SelectedIndex].Bytes, Ages[unusedFor.SelectedIndex].Months);
        bigList.ItemsSource = files.Select(file => new Row
        {
            Name = file.Name, Tag = file,
            Cells = [Ui.FormatSize(file.Size), file.LastUsed.ToString("yyyy-MM-dd"), Path.GetDirectoryName(file.Path) ?? ""],
        }.WithTile(Path.GetExtension(file.Path), "storage")).ToList();
        bigSummary.Text = files.Count > 0
            ? Ui.T("{0} file(s), {1}", files.Count, Ui.FormatSize(files.Sum(file => file.Size)))
            : Ui.T("No files match.");
    }

    /// <summary>The files ticked in the list; with none ticked, the rows selected (Ctrl or Shift for several).</summary>
    private List<BigFile> Chosen()
    {
        var rows = bigList.ItemsSource as IEnumerable<Row> ?? [];
        var ticked = rows.Where(row => row.Checked).ToList();
        return (ticked.Count > 0 ? ticked : Ui.Selected(bigList)).Select(row => (BigFile)row.Tag!).ToList();
    }

    private void TickAll(bool on)
    {
        foreach (var row in bigList.ItemsSource as IEnumerable<Row> ?? [])
            row.Checked = on;
    }

    /// <summary>Move files or folders to another drive with a link left behind; History can move them back.</summary>
    private void MoveDrives(List<string> paths)
    {
        if (paths.Count == 0)
        {
            Ui.Info("Storage", Ui.T("Tick one or more files in the list first."));
            return;
        }
        if (!DriveMoveWindow.Move(Window.GetWindow(this), paths))
            return;
        map.ClearMarks();
        AfterDelete();
        status.Text = Ui.T("Moved. Scan again to see the new sizes.");
    }

    private void DeleteSelected()
    {
        var files = Chosen();
        if (files.Count == 0)
        {
            Ui.Info("Storage", Ui.T("Tick one or more files in the list first."));
            return;
        }
        if (!Ui.Ask("Storage", Ui.T("Move {0} file(s) ({1}) to the Recycle Bin?\n\nYou can bring them back from History.",
                                    files.Count, Ui.FormatSize(files.Sum(file => file.Size)))))
            return;
        var (count, errors) = Sorter.DeleteFiles(files.Select(file => file.Path).ToList());
        AfterDelete();
        Ui.ReportResult(Ui.T("Sent {0} file(s) to the Recycle Bin. You can bring them back from History.", count), errors);
    }

    /// <summary>Files deleted from the list leave it; the map keeps its numbers until the next scan.</summary>
    private void AfterDelete()
    {
        result?.BigFiles.RemoveAll(file => !File.Exists(file.Path) || DriveMover.IsLink(file.Path));
        RenderBigFiles();
        Main.RefreshPreview();
    }
}

/// <summary>
/// A treemap: the children of a folder as rectangles sized by the space they take. Clicking
/// a folder opens it; right-clicking offers to show the item in File Explorer.
/// </summary>
public sealed class TreemapView : Canvas
{
    /// <summary>Children beyond this many are drawn together as one "everything else" block.</summary>
    private const int MaxBlocks = 60;
    private static readonly string[] Tones = ["#42d8a5", "#5aabf1", "#edb755", "#b697f1", "#5cc6d6", "#d99a6c", "#9ccf6a"];

    public StorageNode? Node { get; private set; }
    public event Action<StorageNode>? Opened;
    /// <summary>The paths chosen with Ctrl+click, kept while moving around the map.</summary>
    public HashSet<string> Marked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? MarksChanged;
    /// <summary>"Move drives…" from a block's right-click menu: the chosen paths, that block's included.</summary>
    public event Action<List<string>>? MoveDrives;

    public void ClearMarks()
    {
        Marked.Clear();
        MarksChanged?.Invoke();
        Draw();
    }

    private void ToggleMark(string path)
    {
        if (!Marked.Remove(path))
            Marked.Add(path);
        MarksChanged?.Invoke();
        Draw();
    }

    public TreemapView()
    {
        Background = Ui.Paint("Card");
        // The map itself never mirrors; only its labels follow the language.
        FlowDirection = FlowDirection.LeftToRight;
        SizeChanged += (_, _) => Draw();
    }

    public void Show(StorageNode? node)
    {
        Node = node;
        Draw();
    }

    private void Draw()
    {
        Children.Clear();
        if (Node is null || ActualWidth < 20 || ActualHeight < 20)
        {
            if (Node is null && ActualWidth >= 20)
            {
                var hint = Ui.Muted(Ui.T("Scan to see what takes up the space."));
                SetLeft(hint, 16);
                SetTop(hint, 14);
                Children.Add(hint);
            }
            return;
        }
        var items = Node.Children.Where(child => child.Size > 0).ToList();
        if (items.Count > MaxBlocks)
        {
            var rest = items.Skip(MaxBlocks - 1).ToList();
            items = items.Take(MaxBlocks - 1).ToList();
            items.Add(new StorageNode { Name = "", Path = "", Size = rest.Sum(i => i.Size), Files = rest.Sum(i => i.Files), Parent = Node });
        }
        if (items.Count == 0)
        {
            var empty = Ui.Muted(Ui.T("This folder is empty."));
            SetLeft(empty, 16);
            SetTop(empty, 14);
            Children.Add(empty);
            return;
        }
        var places = Storage.Treemap(items.Select(item => (double)item.Size).ToList(), ActualWidth, ActualHeight);
        for (var index = 0; index < items.Count && index < places.Count; index++)
        {
            var item = items[index];
            var (x, y, width, height) = places[index];
            if (width < 2 || height < 2)
                continue;
            var tone = (Color)ColorConverter.ConvertFromString(item.IsFolder ? Tones[index % Tones.Length] : "#7a7d82");
            var card = (Color)ColorConverter.ConvertFromString("#101216");
            Color Mix(double amount) => Color.FromRgb((byte)(tone.R * amount + card.R * (1 - amount)),
                                                      (byte)(tone.G * amount + card.G * (1 - amount)),
                                                      (byte)(tone.B * amount + card.B * (1 - amount)));
            var name = item.Path.Length == 0 ? Ui.T("{0} smaller files", item.Files.ToString("N0")) : item.Name;
            var block = new Border
            {
                Width = Math.Max(width - 2, 1), Height = Math.Max(height - 2, 1),
                Background = new SolidColorBrush(Mix(item.IsFolder ? 0.22 : 0.16)),
                BorderBrush = new SolidColorBrush(Mix(0.55)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Cursor = item.IsFolder ? Cursors.Hand : Cursors.Arrow, ClipToBounds = true,
                ToolTip = $"{name}\n{Ui.FormatSize(item.Size)}" + (item.IsFolder ? "\n" + Ui.T("{0} file(s)", item.Files.ToString("N0")) : ""),
            };
            if (width > 70 && height > 34)
            {
                var label = new StackPanel { Margin = new Thickness(8, 6, 6, 4) };
                var title = Ui.Text(name, 13, weight: item.IsFolder ? FontWeights.Bold : FontWeights.Normal);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Children.Add(title);
                if (height > 52)
                    label.Children.Add(Ui.Muted(Ui.FormatSize(item.Size), 12));
                block.Child = label;
            }
            var marked = item.Path.Length > 0 && Marked.Contains(item.Path);
            Brush Edge() => marked ? Ui.Paint("Primary") : new SolidColorBrush(Mix(0.55));
            block.BorderBrush = Edge();
            if (marked)
            {
                block.BorderThickness = new Thickness(2);
                block.Background = new SolidColorBrush(Mix(0.4));
            }
            block.MouseEnter += (_, _) => block.BorderBrush = Ui.Paint("Text");
            block.MouseLeave += (_, _) => block.BorderBrush = Edge();
            block.MouseLeftButtonUp += (_, _) =>
            {
                // Ctrl+click chooses (or unchooses) it; a plain click opens a folder.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && item.Path.Length > 0)
                    ToggleMark(item.Path);
                else
                    Opened?.Invoke(item);
            };
            if (item.Path.Length > 0)
            {
                var menu = new ContextMenu();
                if (item.IsFolder)
                {
                    menu.Items.Add(Ui.Item("Look inside", () => Opened?.Invoke(item)));
                    menu.Items.Add(Ui.Item("Open in File Explorer", () => Shell.Open(item.Path)));
                }
                else
                    menu.Items.Add(Ui.Item("Show in File Explorer", () => Shell.Reveal(item.Path)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Ui.Item(marked ? "Unchoose" : "Choose (Ctrl+click)", () => ToggleMark(item.Path)));
                menu.Items.Add(Ui.Item("Move drives…", () => MoveDrives?.Invoke(Marked.Append(item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
                block.ContextMenu = menu;
            }
            SetLeft(block, x + 1);
            SetTop(block, y + 1);
            Children.Add(block);
        }
    }
}
