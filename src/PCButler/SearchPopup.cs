// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;
using PCButler.Core.Palette;
using PCButler.Core.Search;

namespace PCButler;

/// <summary>A line of the command palette's list: a command, a calculator answer, or a prefix to type.</summary>
public sealed class PaletteRow
{
    public required string Id { get; init; }
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Keys { get; init; } = "";
    public Visibility KeysShown => Keys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Action Run { get; init; } = () => { };
    /// <summary>Typed into the box instead of running something (file, =).</summary>
    public string? Prefix { get; init; }
    /// <summary>Counted in palette-use.json when run.</summary>
    public bool Counts { get; init; }
}

/// <summary>
/// The command palette, and the search box the Win + F shortcut opens: one window over any
/// program. Opened for commands (Alt + Space unless changed) it lists PC Butler's pages and
/// actions, matched as you type in English or Arabic, with the files that match underneath;
/// "file " searches only files, "=" works out a sum, "?" lists these. Opened for files
/// (Win + F) it is the file search box as before. Enter runs or opens the chosen line,
/// Ctrl+Enter opens a file's folder, the arrows choose, Esc closes.
///
/// To stay light it is built the first time it is wanted and then only hidden, so later it
/// comes up at once; hidden, it lets go of its lists and does no work at all.
/// </summary>
public sealed class SearchPopup : Window
{
    private const string FilePrefix = "file ";
    private const int MostCommands = 40, CommandsWithFiles = 6;

    private readonly TextBox box;
    private readonly TextBlock boxGlyph = Ui.Glyph("", 17, "Primary");
    private readonly ListBox commands = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false };
    private readonly Border commandsFrame;
    private readonly TextBlock filesLabel = Ui.Muted(Ui.T("Files"), 11.5);
    private readonly Border filesFrame;
    private readonly Grid lists = new();
    private readonly SearchResults results = new(compact: true);
    private readonly TextBlock status = Ui.Muted("", 11.5);
    private readonly TextBlock keysHelp = Ui.Muted("", 11.5);
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private List<(PaletteCommand Command, string[] Words)>? all;
    private Dictionary<string, string> shortcuts = [];
    private CancellationTokenSource? running;
    private bool forCommands, inFiles, filesShown;

    /// <summary>The language the window was built in: another one needs a new window.</summary>
    public string MadeForLanguage { get; } = Tr.Language;

    public SearchPopup()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Search");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 780;
        Height = 540;

        box = Ui.Field();
        box.FontSize = 16 * Ui.TextScale;
        box.TextChanged += (_, _) =>
        {
            ShowCommands();
            typing.Stop();
            if (filesShown)
                typing.Start();
            else
                CancelFiles();
        };
        typing.Tick += (_, _) =>
        {
            typing.Stop();
            Run();
        };
        results.Opened += () => SearchHistory.Add(FileQuery());
        results.Opened += Dismiss;
        results.SortChanged += Run;

        commands.FontSize = 14 * Ui.TextScale;
        commands.ItemTemplate = RowTemplate();
        commands.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && commands.ContainerFromElement(source) is ListBoxItem { DataContext: PaletteRow row })
                RunRow(row);
        };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(boxGlyph.Margin(0, 0, 10, 0).Dock(Dock.Left));
        head.Children.Add(box);
        var everything = Ui.Text(Ui.T("All results on the Search page") + " →", 11.5, "Primary");
        everything.Cursor = Cursors.Hand;
        everything.MouseLeftButtonUp += (_, _) => OpenPage();
        var foot = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        foot.Children.Add(everything.Dock(Dock.Right));
        foot.Children.Add(keysHelp.Margin(0, 0, 12, 0));

        commandsFrame = new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 4, 0, 4), Child = commands };
        var files = new DockPanel();
        files.Children.Add(filesLabel.Margin(4, 8, 0, 4).Dock(Dock.Top));
        files.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = results });
        filesFrame = new Border { Child = files };
        lists.RowDefinitions.Add(new RowDefinition());
        lists.RowDefinitions.Add(new RowDefinition());
        lists.Children.Add(commandsFrame.At(0, 0));
        lists.Children.Add(filesFrame.At(1, 0));

        var layout = new DockPanel();
        layout.Children.Add(head.Dock(Dock.Top));
        layout.Children.Add(foot.Dock(Dock.Bottom));
        layout.Children.Add(status.Margin(2, 6, 2, 0).Dock(Dock.Bottom));
        layout.Children.Add(lists);
        Content = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = layout,
        };

        PreviewKeyDown += OnKey;
        // Another program in front hides it; a question of PC Butler's own (rename, delete) does not.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && !OursInFront())
                Dismiss();
        }, DispatcherPriority.ApplicationIdle);
        // Closed only when PC Butler exits or the language changed; otherwise Esc only hides it.
        Closing += (_, _) => running?.Cancel();
    }

    /// <summary>Bring it up empty, for commands or for files, over the program in front.</summary>
    public void Open(bool forCommands)
    {
        this.forCommands = forCommands;
        all ??= PaletteCommands.All().Select(command => (command, Words(command))).ToList();
        shortcuts = Hotkeys.Effective(ConfigStore.Load().Hotkeys);
        boxGlyph.Text = forCommands ? "" : "";
        box.Tag = forCommands ? Ui.T("Search commands and files… (? lists the shortcuts)") : Ui.T("Search files and folders…");
        keysHelp.Text = forCommands
            ? Ui.T("Enter: run or open  ·  Ctrl+Enter: open folder  ·  ↑ ↓: choose  ·  Esc: close")
            : Ui.T("Enter: open  ·  Ctrl+Enter: open folder  ·  ↑ ↓: choose  ·  Esc: close");
        if (box.Text.Length > 0)
            box.Text = ""; // shows the lists again
        else
            ShowCommands();
        if (!filesShown)
            CancelFiles();
        else
            Run();
        if (!IsVisible)
            Show();
        Place();
        BringToFront(this);
        box.Focus();
    }

    /// <summary>Hide it and let go of what it showed.</summary>
    private void Dismiss()
    {
        if (!IsVisible)
            return;
        typing.Stop();
        running?.Cancel();
        Hide();
        commands.ItemsSource = null;
        results.ItemsSource = null;
        status.Text = "";
    }

    /// <summary>The texts a command is found by: its name, group and keywords in English and in the language shown.</summary>
    private static string[] Words(PaletteCommand command)
    {
        var words = new List<string> { FuzzyMatch.Fold(command.Title), FuzzyMatch.Fold(command.Keywords), FuzzyMatch.Fold(command.Group) };
        if (Tr.IsArabic)
            words.AddRange([FuzzyMatch.Fold(Ui.T(command.Title)), FuzzyMatch.Fold(Ui.T(command.Group))]);
        return words.Where(word => word.Length > 0).Distinct().ToArray();
    }

    /// <summary>What the files list searches for, the prefix taken off.</summary>
    private string FileQuery()
    {
        var text = box.Text;
        return forCommands && text.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ? text[FilePrefix.Length..] : text;
    }

    /// <summary>Fill the commands list for what is typed, and decide whether files show under it.</summary>
    private void ShowCommands()
    {
        var text = box.Text;
        var trimmed = text.Trim();
        var rows = new List<PaletteRow>();
        if (!forCommands)
            filesShown = true;
        else if (text.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) || trimmed.Equals("file", StringComparison.OrdinalIgnoreCase))
            filesShown = true;
        else if (trimmed.StartsWith('='))
        {
            filesShown = false;
            var sum = trimmed[1..];
            if (sum.Trim().Length == 0)
                rows.Add(new PaletteRow { Id = "calc", Glyph = "", Title = Ui.T("Type a sum: 12*7, (3+4)/2, 15% * 80"),
                                          Detail = Ui.T("Sizes and speeds too: 5 GB in MB, 4 GB at 100 Mbps") });
            else
                rows.Add(Answer(sum) ?? new PaletteRow { Id = "calc", Glyph = "", Title = Ui.T("Not a sum the calculator understands yet") });
        }
        else if (trimmed is "?" or "؟")
        {
            filesShown = false;
            rows.Add(new PaletteRow { Id = "help:file", Glyph = "", Title = Ui.T("Search files and folders"), Keys = "file", Prefix = FilePrefix });
            rows.Add(new PaletteRow { Id = "help:calc", Glyph = "", Title = Ui.T("Calculator"), Keys = "=", Prefix = "=" });
            rows.Add(new PaletteRow { Id = "help:commands", Glyph = "", Title = Ui.T("Commands and files: just type"), Prefix = "" });
        }
        else
        {
            filesShown = trimmed.Length > 0;
            if (Calculator.LooksLikeMath(trimmed) && Answer(trimmed) is { } answer)
                rows.Add(answer);
            var query = FuzzyMatch.Fold(trimmed);
            var matched = new List<(PaletteCommand Command, int Score, int Used, int Order)>();
            for (var index = 0; index < all!.Count; index++)
            {
                var (command, words) = all[index];
                var score = query.Length == 0 ? 0 : words.Max(word => FuzzyMatch.Score(query, word));
                if (score < 0 || command.Available?.Invoke() == false)
                    continue;
                matched.Add((command, score, PaletteUsage.Count(command.Id), index));
            }
            var most = filesShown ? CommandsWithFiles : MostCommands;
            rows.AddRange(matched.OrderByDescending(match => match.Score).ThenByDescending(match => match.Used).ThenBy(match => match.Order)
                                 .Take(most).Select(match => Row(match.Command)));
        }
        commands.ItemsSource = rows;
        commandsFrame.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        filesFrame.Visibility = filesShown ? Visibility.Visible : Visibility.Collapsed;
        filesLabel.Visibility = filesShown && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Commands alone fill the window; with files under them, they take only the room they need.
        lists.RowDefinitions[0].Height = filesShown ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        lists.RowDefinitions[1].Height = filesShown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!filesShown)
            status.Text = "";
        inFiles = rows.Count == 0;
        if (rows.Count > 0)
        {
            commands.SelectedIndex = 0;
            commands.ScrollIntoView(commands.SelectedItem);
            results.UnselectAll();
        }
        else if (filesShown)
            results.ChooseFirst();
    }

    private PaletteRow Row(PaletteCommand command) => new()
    {
        Id = command.Id,
        Glyph = command.Glyph,
        Title = Ui.T(command.Title),
        Detail = Ui.T(command.Group),
        Keys = command.Prefix is { } prefix ? prefix.Trim() : command.Hotkey is { } hotkey ? shortcuts.GetValueOrDefault(hotkey, "") : "",
        Run = command.Run,
        Prefix = command.Prefix,
        Counts = true,
    };

    /// <summary>The calculator's answer as a line; Enter copies it.</summary>
    private static PaletteRow? Answer(string sum) =>
        Calculator.TryEvaluate(sum, out var answer)
            ? new PaletteRow
            {
                Id = "calc", Glyph = "", Title = "= " + answer.Shown, Detail = Ui.T("Enter copies the answer"),
                Run = () =>
                {
                    try
                    {
                        Clipboard.SetText(answer.Copy);
                    }
                    catch (ExternalException)
                    {
                        // another program holds the clipboard; nothing lost but the copy
                    }
                },
            }
            : null;

    private void RunRow(PaletteRow row)
    {
        if (row.Counts)
            PaletteUsage.Add(row.Id);
        if (row.Prefix is { } prefix)
        {
            box.Text = prefix;
            box.CaretIndex = prefix.Length;
            box.Focus();
            return;
        }
        Dismiss();
        row.Run();
    }

    /// <summary>A command line: its icon, its name, where it belongs, and its shortcut on the far side.</summary>
    private static DataTemplate RowTemplate()
    {
        var line = new FrameworkElementFactory(typeof(DockPanel));
        line.SetValue(MinHeightProperty, 30.0);
        var keys = new FrameworkElementFactory(typeof(Border));
        keys.SetValue(DockPanel.DockProperty, Dock.Right);
        keys.SetValue(Border.BorderBrushProperty, Ui.Paint("Line"));
        keys.SetValue(Border.BackgroundProperty, Ui.Paint("Secondary"));
        keys.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        keys.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        keys.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
        keys.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        keys.SetBinding(VisibilityProperty, new Binding(nameof(PaletteRow.KeysShown)));
        var keysText = new FrameworkElementFactory(typeof(TextBlock));
        keysText.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteRow.Keys)));
        keysText.SetValue(TextBlock.FontSizeProperty, 11.5 * Ui.TextScale);
        keysText.SetValue(TextBlock.ForegroundProperty, Ui.Paint("Muted"));
        keysText.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight); // Ctrl+Alt reads the same in Arabic
        keys.AppendChild(keysText);
        line.AppendChild(keys);
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(DockPanel.DockProperty, Dock.Left);
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteRow.Glyph)));
        glyph.SetValue(TextBlock.FontFamilyProperty, Ui.IconFont);
        glyph.SetValue(TextBlock.FontSizeProperty, 16.0);
        glyph.SetValue(TextBlock.ForegroundProperty, Ui.Paint("Primary"));
        glyph.SetValue(WidthProperty, 30.0);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        line.AppendChild(glyph);
        var texts = new FrameworkElementFactory(typeof(StackPanel));
        texts.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        texts.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteRow.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        texts.AppendChild(title);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteRow.Detail)));
        detail.SetValue(TextBlock.ForegroundProperty, Ui.Paint("Muted"));
        detail.SetValue(TextBlock.FontSizeProperty, 12 * Ui.TextScale);
        detail.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        detail.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        texts.AppendChild(detail);
        line.AppendChild(texts);
        return new DataTemplate { VisualTree = line };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                break;
            case Key.Enter when control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                OpenPage();
                break;
            case Key.Enter when box.IsKeyboardFocused:
                if (!inFiles && commands.SelectedItem is PaletteRow row)
                {
                    RunRow(row);
                    break;
                }
                if (results.SelectedIndex < 0)
                    results.ChooseFirst();
                if (control)
                    results.OpenFolderChosen();
                else
                    results.OpenChosen();
                break;
            case Key.Down or Key.Up when box.IsKeyboardFocused:
                Step(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.PageDown or Key.PageUp when box.IsKeyboardFocused:
                Step(e.Key == Key.PageDown ? 12 : -12);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Move the choice: down through the commands, then on into the files, and back up.</summary>
    private void Step(int by)
    {
        var count = commands.Items.Count;
        if (!inFiles)
        {
            var next = commands.SelectedIndex + by;
            if (next >= count && filesShown && results.Items.Count > 0)
            {
                inFiles = true;
                commands.SelectedIndex = -1;
                results.ChooseFirst();
                results.ScrollIntoView(results.SelectedItem);
                return;
            }
            if (count > 0)
            {
                commands.SelectedIndex = Math.Clamp(next, 0, count - 1);
                commands.ScrollIntoView(commands.SelectedItem);
            }
            return;
        }
        if (by < 0 && results.SelectedIndex + by < 0 && count > 0)
        {
            inFiles = false;
            results.UnselectAll();
            commands.SelectedIndex = count - 1;
            commands.ScrollIntoView(commands.SelectedItem);
            return;
        }
        results.Step(by);
    }

    private void CancelFiles()
    {
        running?.Cancel();
        running = null;
        results.ItemsSource = null;
    }

    private async void Run()
    {
        running?.Cancel();
        if (!filesShown)
            return;
        var cancel = new CancellationTokenSource();
        running = cancel;
        var (query, sort, descending) = (FileQuery(), results.Sort, results.Descending);
        var engine = App.Current.Search;
        try
        {
            var result = await Task.Run(() => engine.Search(query, sort, descending, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || !IsVisible || !filesShown)
                return;
            results.Show(result);
            if (inFiles)
                results.ChooseFirst();
            else
                results.UnselectAll();
            status.Text = engine.State == SearchState.Scanning && engine.Index.Live == 0
                ? Ui.T("Indexing your drives: {0:N0} found so far…", engine.Scanned)
                : engine.State == SearchState.Off ? Ui.T("Search is switched off in Settings.")
                : Ui.T("{0:N0} results", result.Rows.Length);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenPage()
    {
        var text = FileQuery();
        Dismiss();
        App.Current.ShowPage("search");
        App.Current.Workspace?.SearchPage.SearchFor(text);
    }

    /// <summary>Across the screen the mouse is on, a fifth of the way down, like a launcher.</summary>
    private void Place()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
        var area = SystemParameters.WorkArea;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var (left, top, width, height) = (screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY, screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
        if (width <= 0 || height <= 0 || !new Rect(left, top, width, height).Contains(point))
            (left, top, width, height) = (area.Left, area.Top, area.Width, area.Height);
        Left = left + Math.Max(0, (width - Width) / 2);
        Top = top + Math.Max(0, Math.Min(height / 5, height - Height));
    }

    /// <summary>
    /// Bring the box in front of the program the user is in. A key caught by the keyboard hook
    /// does not give PC Butler the right to take the focus (a registered shortcut does), so it
    /// joins the input of the window in front for a moment, which Windows allows.
    /// </summary>
    public static void BringToFront(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var front = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(front, out _);
        var ours = GetCurrentThreadId();
        var joined = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            SetForegroundWindow(handle);
            BringWindowToTop(handle);
        }
        finally
        {
            if (joined)
                AttachThreadInput(ours, theirs, false);
        }
        window.Activate();
    }

    private static bool OursInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == (uint)Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
