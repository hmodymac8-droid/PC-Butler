// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCButler.Core;

namespace PCButler;

/// <summary>Small building blocks shared by every page: colours, text, buttons, lists, cards.</summary>
public static class Ui
{
    public static Brush Paint(string key) => (Brush)Application.Current.FindResource(key);
    public static Brush Hex(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    // Every text size in the program is multiplied by this, so the type can grow or shrink in one place.
    public const double TextScale = 1.15;
    public static FontFamily DisplayFont => (FontFamily)Application.Current.FindResource("DisplayFont");

    public const string Eyebrow = "YOUR DIGITAL SPACE, SORTED";
    private static readonly string[] Tones = ["#42d8a5", "#5aabf1", "#edb755", "#b697f1", "#5cc6d6", "#d99a6c", "#9ccf6a"];

    /// <summary>Drive bars, one colour per drive: the app's green, then blue, amber and purple.</summary>
    public static Brush DriveTone(int index) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(new[] { "#42d8a5", "#5aabf1", "#edb755", "#b697f1" }[index % 4]));
    private static readonly Dictionary<string, string> ToneOf = new(StringComparer.OrdinalIgnoreCase);

    public static string T(string english, params object[] arguments) => Tr.T(english, arguments);

    public static string FormatSize(long size)
    {
        double value = size;
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (value < 1024 || unit == "GB")
                return unit == "B" ? $"{value:0} {unit}" : $"{value:0.0} {unit}";
            value /= 1024;
        }
        return "";
    }

    public static TextBlock Text(string text, double size = 14, string brush = "Text", FontWeight? weight = null, bool wrap = false)
    {
        var block = new TextBlock { Text = text, FontSize = size * TextScale, Foreground = Paint(brush), VerticalAlignment = VerticalAlignment.Center };
        if (weight is { } w)
            block.FontWeight = w;
        if (wrap)
            block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    public static TextBlock Muted(string text, double size = 14, bool wrap = false) => Text(text, size, "Muted", wrap: wrap);

    public static TextBlock Glyph(string code, double size = 15, string brush = "Muted") => new()
    {
        Text = code, FontFamily = IconFont, FontSize = size, Foreground = Paint(brush), VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent)
            button.Style = (Style)Application.Current.FindResource("AccentButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    public static CheckBox Switch(string text, bool on, Action<bool>? changed = null)
    {
        var box = new CheckBox { Content = text, IsChecked = on, Style = (Style)Application.Current.FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center };
        if (changed is not null)
        {
            box.Checked += (_, _) => changed(true);
            box.Unchecked += (_, _) => changed(false);
        }
        return box;
    }

    public static TextBox Field(string text = "", double width = double.NaN, string hint = "")
    {
        var box = new TextBox { Text = text, Tag = hint };
        if (!double.IsNaN(width))
            box.Width = width;
        return box;
    }

    /// <summary>
    /// A box for a Courier ID: digits only, typed or pasted (Arabic-Indic digits become 0-9,
    /// anything else is left out), at most nine. Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A and Backspace work as in any box.
    /// </summary>
    public static TextBox DigitsOnly(TextBox box, int most = 9)
    {
        static string Digits(string text) => new(text.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠'))
                                                             : c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c)
                                                     .Where(char.IsAsciiDigit).ToArray());
        box.MaxLength = most;
        InputMethod.SetIsInputMethodEnabled(box, false);
        box.PreviewTextInput += (_, e) =>
        {
            var digits = Digits(e.Text);
            e.Handled = true;
            if (digits.Length > 0)
                Put(digits);
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            e.CancelCommand();
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string text)
                Put(Digits(text));
        });
        // Typed or pasted digits replace the selection, up to the most there may be.
        void Put(string digits)
        {
            var start = box.SelectionStart;
            var kept = box.Text.Remove(start, box.SelectionLength);
            digits = digits[..Math.Min(digits.Length, Math.Max(0, most - kept.Length))];
            box.Text = kept.Insert(start, digits);
            box.SelectionStart = start + digits.Length;
            box.SelectionLength = 0;
        }
        return box;
    }

    /// <summary>Children side by side, each followed by the given gap.</summary>
    public static StackPanel Row(double gap, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (child is FrameworkElement element && panel.Children.Count > 0)
                element.Margin = new Thickness(element.Margin.Left + gap, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
            panel.Children.Add(child);
        }
        return panel;
    }

    public static T Margin<T>(this T element, double left, double top = 0, double right = 0, double bottom = 0) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static T Dock<T>(this T element, Dock side) where T : UIElement
    {
        DockPanel.SetDock(element, side);
        return element;
    }

    public static T At<T>(this T element, int row, int column, int columnSpan = 1) where T : UIElement
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static BitmapImage AppIcon() => new(new Uri("pack://application:,,,/PCButler;component/icon.png"));

    /// <summary>The tone for a destination folder: rows going to the same folder share one.</summary>
    public static string Tone(string group)
    {
        if (!ToneOf.TryGetValue(group, out var tone))
            ToneOf[group] = tone = Tones[ToneOf.Count % Tones.Length];
        return tone;
    }

    /// <summary>`amount` of `color` laid over `base`, like CSS color-mix.</summary>
    public static Color Mix(string color, string @base, double amount)
    {
        Color a = (Color)ColorConverter.ConvertFromString(color), b = (Color)ColorConverter.ConvertFromString(@base);
        byte Blend(byte x, byte y) => (byte)Math.Round(x * amount + y * (1 - amount));
        return Color.FromRgb(Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }

    /// <summary>
    /// The small rounded badge of a list row, tinted with the row's folder colour. label is a few
    /// letters drawn on it, normally the file extension.
    /// </summary>
    public static (Brush Back, Brush Fore, string Text, double FontSize) Tile(string label, string group = "", bool faded = false)
    {
        label = label.TrimStart('.').ToUpperInvariant();
        if (label.Length > 4)
            label = label[..4];
        Brush back, fore;
        if (faded)
            (back, fore) = (Paint("Secondary"), Paint("Dim"));
        else if (group.Length == 0 || group.Equals("other", StringComparison.OrdinalIgnoreCase))
            (back, fore) = (Paint("Secondary"), Paint("Muted"));
        else
        {
            var tone = Tone(group);
            (back, fore) = (new SolidColorBrush(Mix(tone, "#101216", 0.14)), Hex(tone));
        }
        return (back, fore, label, label.Length <= 3 ? 10.5 : 8.5);
    }

    // --- settings cards ---

    /// <summary>A row of a settings card: a title and a line of explanation on one side, its control on the other.</summary>
    public static Border SettingRow(string title, string detail, FrameworkElement? control, bool first = false) =>
        SettingRow(Text(title, 13, weight: FontWeights.Bold), Muted(detail, 12), control, first);

    public static Border SettingRow(TextBlock title, TextBlock detail, FrameworkElement? control, bool first = false)
    {
        var row = new DockPanel { Margin = new Thickness(24, 16, 24, 16), MinHeight = 40 };
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(24, 0, 0, 0);
            row.Children.Add(control.Dock(System.Windows.Controls.Dock.Right));
        }
        detail.TextWrapping = TextWrapping.Wrap;
        // A row without explanation (or one whose text comes later) shows just its title.
        detail.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(TextBlock.Text)) { Source = detail, Converter = new EmptyHides() });
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail.Margin(0, 4, 0, 0) } });
        return new Border
        {
            Child = row, Background = Brushes.Transparent,
            BorderBrush = Paint("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };
    }

    /// <summary>Hides a text while it is empty.</summary>
    private sealed class EmptyHides : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // --- message boxes (Windows' own, like the Python program used) ---

    /// <summary>Arabic message boxes read right to left.</summary>
    public static MessageBoxOptions Reading => Tr.IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    public static void Info(string title, string message) =>
        Show(message, T(title), MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Reading);

    public static void Warn(string title, string message) =>
        Show(message, T(title), MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Reading);

    public static bool Ask(string title, string message, bool warning = false) =>
        Show(message, T(title), MessageBoxButton.YesNo, warning ? MessageBoxImage.Warning : MessageBoxImage.Question,
                        MessageBoxResult.No, Reading) == MessageBoxResult.Yes;

    private static Window? Owner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
                                         MessageBoxResult defaultResult, MessageBoxOptions options) =>
        Owner() is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image, defaultResult, options)
            : MessageBox.Show(message, title, buttons, image, defaultResult, options);

    /// <summary>"Done": the outcome of a batch, listing up to ten failures.</summary>
    public static void ReportResult(string message, List<string> errors)
    {
        if (errors.Count == 0)
        {
            Info("Done", message);
            return;
        }
        var shown = string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? "\n…" : "");
        Warn("Done", T("{0}\n\n{1} failed:\n{2}", message, errors.Count, shown));
    }

    public static string? PickFolder(string title, string? start = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T(title) };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    /// <summary>Dark title bar and window colours for every window of the program.</summary>
    public static void StyleWindow(Window window)
    {
        window.Background = Paint("Background");
        window.Foreground = Paint("Text");
        window.FontFamily = (FontFamily)Application.Current.FindResource("TextFont");
        window.FontSize = 14 * TextScale;
        // Arabic flips the whole layout: sidebar on the right, lists reading from the right.
        window.FlowDirection = Tr.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        // Ideal keeps the letter shapes smooth; Display snaps them to whole pixels, which looks jagged.
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var dark = 1;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            // Windows 11: paint the title bar in the sidebar colour (COLORREF is 0x00BBGGRR).
            var caption = 0x00090705;
            const int DWMWA_CAPTION_COLOR = 35;
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        };
        try
        {
            window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/PCButler;component/logo.ico"));
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    // --- lists ---

    /// <summary>
    /// A dark list with columns. first = (title, width, cell template: "FileCell", "CheckCell" or
    /// "PlainCell"); the other columns show Row.Cells in order. The first column takes up the
    /// slack, and gives some back when the window is narrow.
    /// </summary>
    public static SortableList List((string Title, double Width, string Template) first, params (string Title, double Width)[] columns)
    {
        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(new GridViewColumn
        {
            Header = T(first.Title), Width = first.Width, CellTemplate = (DataTemplate)Application.Current.FindResource(first.Template),
        });
        for (var index = 0; index < columns.Length; index++)
        {
            view.Columns.Add(new GridViewColumn
            {
                Header = T(columns[index].Title), Width = columns[index].Width,
                DisplayMemberBinding = new Binding($"Cells[{index}]"),
            });
        }
        var list = new SortableList { View = view, SelectionMode = SelectionMode.Extended };
        // Let the first column take up the slack, as the old program's lists did.
        list.SizeChanged += (_, _) =>
        {
            var others = view.Columns.Skip(1).Sum(column => column.Width);
            view.Columns[0].Width = Math.Max(Math.Min(first.Width, 160), list.ActualWidth - others - 30);
        };
        return list;
    }

    public static List<Row> Selected(ListView list) => list.SelectedItems.Cast<Row>().ToList();

    /// <summary>
    /// Compare two cells the way a person would: "9.8 KB" before "1.2 MB", "7 days" before
    /// "12 days", and plain text without regard to case. Dates are written year first, so they
    /// already sort correctly as text.
    /// </summary>
    public static int SmartCompare(string a, string b)
    {
        double? x = SortNumber(a), y = SortNumber(b);
        if (x is not null && y is not null && x != y)
            return x.Value.CompareTo(y.Value);
        if (x is null != y is null)
            return x is null ? 1 : -1; // numbers before words such as "never"
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>The number a cell starts with (sizes in bytes), or null for text and dates.</summary>
    private static double? SortNumber(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*\d{1,4}[-:/]\d"))
            return null; // a date or a time
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(-?\d+(?:[.,]\d+)?)\s*(B|KB|MB|GB|TB)?\b");
        if (!match.Success)
            return null;
        if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var number))
            return null;
        return number * match.Groups[2].Value switch
        {
            "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, "TB" => 1024d * 1024 * 1024 * 1024, _ => 1d,
        };
    }

    /// <summary>
    /// The right-click menu of a file list (show in Explorer, open, delete), and double-click to
    /// show the file in Explorer. pathOf returns where a row's file is now, or null.
    /// </summary>
    public static void AttachFileMenu(ListView list, Func<Row, string?> pathOf, Action onChange,
                                      params (string Label, Action<string> Action)[] extra)
    {
        string? Current()
        {
            var path = list.SelectedItem is Row row ? pathOf(row) : null;
            if (path is null || !File.Exists(path))
            {
                Info("File", T("This file is not there any more."));
                return null;
            }
            return path;
        }

        void Show()
        {
            if (Current() is { } path)
                Shell.Reveal(path);
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Show in File Explorer", Show));
        menu.Items.Add(Item("Open / Run", () =>
        {
            if (Current() is { } path && Shell.Open(path) is { } error)
                Warn("Open", T("Could not open it:\n{0}", error));
        }));
        menu.Items.Add(new Separator());
        foreach (var (label, action) in extra)
            menu.Items.Add(Item(label, () =>
            {
                if (Current() is { } path)
                    action(path);
            }));
        menu.Items.Add(Item("Delete (to Recycle Bin)", () =>
        {
            if (Current() is not { } path || !Ask("Delete", T("Move this file to the Recycle Bin?\n\n{0}\n\nYou can bring it back from the History tab.", Path.GetFileName(path))))
                return;
            var (_, errors) = Sorter.DeleteFiles([path]);
            if (errors.Count > 0)
                Warn("Delete", T("Could not delete it:\n{0}", errors[0]));
            else
                onChange();
        }));
        list.ContextMenu = menu;
        list.ContextMenuOpening += (_, e) => e.Handled = list.SelectedItem is null;
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && RowAt(e.OriginalSource) is not null)
                Show();
        };
    }

    public static MenuItem Item(string label, Action action)
    {
        var item = new MenuItem { Header = T(label) };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The row under a mouse event's source, or null (e.g. over a column heading).</summary>
    public static Row? RowAt(object source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ListViewItem { DataContext: Row row })
                return row;
        }
        return null;
    }
}

/// <summary>
/// Text with Arabic in it reads right to left; anything else (file names, paths, sizes) stays
/// left to right. Theme.xaml applies this to every TextBlock, so the layout itself is not mirrored.
/// </summary>
public sealed class TextDirection : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is string text && text.Any(c => c is >= '\u0600' and <= '\u06FF') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A list whose columns sort when their heading is clicked: first ascending, then descending,
/// then back to the order the page gave. The heading shows an arrow while it sorts.
/// </summary>
public sealed class SortableList : ListView
{
    private string[]? titles; // the headings without arrows
    private bool normalizing;

    /// <summary>The sorted column (0 = the first, with the names), or -1 for the page's own order.</summary>
    public int SortColumn { get; private set; } = -1;
    public bool SortDescending { get; private set; }
    /// <summary>Raised when the user clicks a heading (pages that show only part of their data re-read it).</summary>
    public event Action? SortChanged;

    public SortableList()
    {
        // Styles are looked up by exact type, so a class derived from ListView has to ask for ListView's look.
        SetResourceReference(StyleProperty, typeof(ListView));
        AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClicked));
    }

    private void HeaderClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || View is not GridView view)
            return;
        var index = view.Columns.IndexOf(column);
        if (index < 0)
            return;
        titles ??= view.Columns.Select(c => c.Header as string ?? "").ToArray();
        if (SortColumn != index)
            (SortColumn, SortDescending) = (index, false);
        else if (!SortDescending)
            SortDescending = true;
        else
            SortColumn = -1;
        for (var i = 0; i < view.Columns.Count; i++)
            view.Columns[i].Header = titles[i] + (i == SortColumn ? (SortDescending ? "  ▼" : "  ▲") : "");
        ApplySort();
        SortChanged?.Invoke();
    }

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        if (normalizing)
            return;
        if (newValue is not null and not System.Collections.IList)
        {
            // Sorting needs a real list, not a query that is worked out again on every pass.
            normalizing = true;
            ItemsSource = newValue.Cast<object>().ToList();
            normalizing = false;
        }
        ApplySort();
    }

    private void ApplySort()
    {
        if (ItemsSource is not null && CollectionViewSource.GetDefaultView(ItemsSource) is ListCollectionView view)
            view.CustomSort = SortColumn < 0 ? null : new RowOrder(SortColumn, SortDescending);
    }

    private sealed class RowOrder(int column, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? a, object? b)
        {
            if (a is not Row x || b is not Row y)
                return 0;
            // Groups keep their place, with the group's heading on top; sorting orders the rows inside.
            if (x.SortGroup != y.SortGroup)
                return x.SortGroup.CompareTo(y.SortGroup);
            if (x.SortHeader != y.SortHeader)
                return x.SortHeader ? -1 : 1;
            var result = Ui.SmartCompare(Key(x), Key(y));
            return descending ? -result : result;
        }

        private string Key(Row row) => column == 0 ? row.Name : column - 1 < row.Cells.Length ? row.Cells[column - 1] : "";
    }
}

/// <summary>One line of a list. Cells are the text of the columns after the first.</summary>
public sealed class Row : INotifyPropertyChanged
{
    private bool isChecked;
    private string[] cells = [];
    private string name = "";

    public string Name { get => name; set { name = value; Changed(nameof(Name)); } }
    public string[] Cells { get => cells; set { cells = value; Changed(nameof(Cells)); } }
    public Brush TileBack { get; set; } = Brushes.Transparent;
    public Brush TileFore { get; set; } = Brushes.Transparent;
    public string TileText { get; set; } = "";
    public double TileFontSize { get; set; } = 10.5;
    /// <summary>Shown in the muted colour (undone, switched off, a group heading).</summary>
    public bool Dim { get; set; }
    public bool Checked { get => isChecked; set { if (isChecked != value) { isChecked = value; Changed(nameof(Checked)); CheckedChanged?.Invoke(this); } } }
    public Visibility CheckVisibility { get; set; } = Visibility.Visible;
    public Thickness Indent { get; set; } = new(16, 0, 0, 0);
    /// <summary>Rows with the same number stay together when the list is sorted (duplicate groups).</summary>
    public int SortGroup { get; set; }
    /// <summary>The heading of its group: always above the group's other rows.</summary>
    public bool SortHeader { get; set; }
    /// <summary>What the row stands for (a planned move, a vault item...).</summary>
    public object? Tag { get; set; }
    public Action<Row>? CheckedChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>What screen readers and UI Automation call the row.</summary>
    public override string ToString() => Name;

    public Row WithTile(string extension, string group = "", bool faded = false)
    {
        (TileBack, TileFore, TileText, TileFontSize) = Ui.Tile(extension, group, faded);
        return this;
    }

    /// <summary>Set the tick without telling the listener (used when the page itself changes it).</summary>
    public void SetChecked(bool value)
    {
        isChecked = value;
        Changed(nameof(Checked));
    }
}

/// <summary>A summary card: a caption, a big number with its unit, and a note.</summary>
public sealed class StatCard : Border
{
    private readonly TextBlock value = new() { FontSize = 27 * Ui.TextScale, FontWeight = FontWeights.Bold, Foreground = Ui.Paint("Text"), FontFamily = Ui.DisplayFont };
    private readonly TextBlock unit = Ui.Muted("", 15).Margin(8, 0, 0, 5);
    private readonly TextBlock note = Ui.Muted("", 12).Margin(0, 6, 0, 0);

    public StatCard(string caption, string glyph)
    {
        Widgets.StyleCard(this);
        Padding = new Thickness(18, 14, 18, 14);
        var top = new DockPanel { LastChildFill = false };
        top.Children.Add(Ui.Muted(Ui.T(caption), 13));
        top.Children.Add(Ui.Glyph(glyph, 15).Dock(Dock.Right));
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        var numbers = Ui.Row(0, value, unit).Margin(0, 8);
        Child = new StackPanel { Children = { top, numbers, note } };
    }

    public void Set(string number, string unitText = "", string? noteText = null)
    {
        value.Text = number;
        unit.Text = unitText;
        if (noteText is not null)
            note.Text = noteText;
    }

    /// <summary>Three cards side by side, as at the top of most pages.</summary>
    public static (Grid Row, StatCard[] Cards) MakeRow(params (string Caption, string Glyph)[] cards)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        var made = new StatCard[cards.Length];
        for (var index = 0; index < cards.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            made[index] = new StatCard(cards[index].Caption, cards[index].Glyph).At(0, index);
            made[index].Margin = new Thickness(index == 0 ? 0 : 6, 0, index == cards.Length - 1 ? 0 : 6, 0);
            grid.Children.Add(made[index]);
        }
        return (grid, made);
    }
}

/// <summary>A bordered card with a heading row, a body (usually a list) and an optional footer row.</summary>
public sealed class CardPanel : Border
{
    private readonly TextBlock count = Ui.Muted("", 12);
    private readonly TextBlock heading;
    private readonly Border countBadge;
    private readonly DockPanel layout = new();
    private readonly Border headRow;
    private readonly DockPanel head;
    private UIElement? body;

    /// <summary>The right side of the heading row: search box, switches, status text.</summary>
    public StackPanel Controls { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public CardPanel(string title, string glyph)
    {
        Widgets.StyleCard(this);
        countBadge = new Border
        {
            Background = Ui.Paint("Secondary"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(12, 0, 0, 0), Child = count, Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        head = new DockPanel { Margin = new Thickness(20, 14, 20, 14), LastChildFill = false };
        head.Children.Add(Ui.Glyph(glyph, 15, "Primary"));
        heading = Ui.Text(Ui.T(title), 15, weight: FontWeights.Bold);
        head.Children.Add(heading.Margin(10));
        head.Children.Add(countBadge);
        head.Children.Add(Controls.Dock(Dock.Right));
        headRow = new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
        layout.Children.Add(headRow.Dock(Dock.Top));
        Child = layout;
    }

    /// <summary>
    /// Let the card fold: a click on its heading row shows or hides the body and the heading's
    /// controls, with an arrow that shows which. Call after SetBody.
    /// </summary>
    public void MakeFoldable(bool open)
    {
        var arrow = Ui.Glyph("", 12);
        arrow.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Insert(0, arrow);
        headRow.Background = System.Windows.Media.Brushes.Transparent; // so the whole row takes clicks
        headRow.Cursor = Cursors.Hand;
        void Show(bool shown)
        {
            open = shown;
            arrow.Text = shown ? "\uE70D" : (Tr.IsArabic ? "\uE76B" : "\uE76C");
            var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            Controls.Visibility = visibility;
            if (body is not null)
                body.Visibility = visibility;
            headRow.BorderThickness = new Thickness(0, 0, 0, shown ? 1 : 0);
        }
        headRow.MouseLeftButtonUp += (_, e) =>
        {
            // The heading's own buttons and lists do their own thing.
            for (var at = e.OriginalSource as DependencyObject; at is not null && at != headRow; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            {
                if (at == Controls)
                    return;
            }
            Show(!open);
        };
        Show(open);
    }

    /// <summary>The heading, for a card whose subject changes (already in the user's language).</summary>
    public string Title
    {
        get => heading.Text;
        set => heading.Text = value;
    }

    public void SetCount(string text)
    {
        count.Text = text;
        countBadge.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Add the footer row (call before SetBody) and return it for the page to fill.</summary>
    public DockPanel AddFooter()
    {
        var footer = new DockPanel { Margin = new Thickness(20, 10, 20, 10), LastChildFill = false };
        layout.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }.Dock(Dock.Bottom));
        return footer;
    }

    public void SetBody(UIElement body)
    {
        this.body = body;
        layout.Children.Add(body);
    }
}

/// <summary>"Move to Recycle Bin / Delete for good / Cancel".</summary>
public sealed class DeleteHowDialog : Window
{
    /// <summary>"recycle", "permanent" or null.</summary>
    public string? Answer { get; private set; }

    private DeleteHowDialog(string message)
    {
        Title = Ui.T("Delete");
        Ui.StyleWindow(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var recycle = Ui.Button(Ui.T("Move to Recycle Bin"), () => Choose("recycle"), accent: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(recycle);
        buttons.Children.Add(Ui.Button(Ui.T("Delete for good"), () => Choose("permanent")).Margin(6));
        buttons.Children.Add(Ui.Button(Ui.T("Cancel"), Close).Margin(6));
        var text = Ui.Text(message, wrap: true);
        text.MaxWidth = 460;
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { text, Ui.Muted(Ui.T("Files sent to the Recycle Bin can be brought back from History.")).Margin(0, 10), buttons },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter)
                Choose("recycle");
        };
        Loaded += (_, _) => recycle.Focus();
    }

    private void Choose(string answer)
    {
        Answer = answer;
        Close();
    }

    public static string? Ask(Window owner, string message)
    {
        var dialog = new DeleteHowDialog(message) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Answer;
    }
}
