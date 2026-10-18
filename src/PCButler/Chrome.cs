// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// One page of the workspace: the eyebrow, a big title and a one-line description, with the
/// page's main buttons on the right (Actions), and the page's content below.
/// </summary>
public abstract class Page : DockPanel
{
    private readonly TextBlock eyebrow;
    private readonly TextBlock description;
    private readonly DockPanel header;
    private readonly StackPanel heading;
    private readonly List<FrameworkElement> statRows = [];

    protected MainWindow Main { get; }
    /// <summary>The right side of the header: the page's main buttons, the first one rightmost.</summary>
    protected StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

    protected Page(MainWindow main, string title, string descriptionText)
    {
        Main = main;
        Margin = new Thickness(30, 24, 30, 12);
        header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(Actions.Dock(Dock.Right));
        eyebrow = Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4);
        var titleText = Ui.Text(Ui.T(title), 27, weight: FontWeights.Bold);
        titleText.FontFamily = Ui.DisplayFont;
        description = Ui.Muted(Ui.T(descriptionText)).Margin(0, 4);
        heading = new StackPanel { Children = { eyebrow, titleText, description } };
        header.Children.Add(heading);
        Children.Add(header.Dock(Dock.Top));
    }

    /// <summary>Something was switched on or off outside the page (the tray panel, a hotkey): show the saved state.</summary>
    public virtual void ShowSwitches()
    {
    }

    /// <summary>Tick or untick a switch to match what is saved. Its handler sees a value that is already applied.</summary>
    protected static void Match(CheckBox box, bool on)
    {
        if (box.IsChecked != on)
            box.IsChecked = on;
    }

    /// <summary>The Home page shows the version in a pill where other pages have their headline.</summary>
    protected void ShowVersionInsteadOfHeading()
    {
        heading.Visibility = Visibility.Collapsed;
        header.Margin = new Thickness(0, 0, 0, 12);
        header.Children.Add(Widgets.VersionPill());
    }

    /// <summary>Add a page button to the header, to the left of those added before.</summary>
    protected Button AddAction(string text, Action onClick, bool accent = false)
    {
        var button = Ui.Button(Ui.T(text), onClick, accent);
        if (Actions.Children.Count > 0)
            button.Margin = new Thickness(0, 0, 6, 0);
        Actions.Children.Insert(0, button);
        return button;
    }

    /// <summary>The summary cards at the top of the page, hidden with the small print when the window is short.</summary>
    protected StatCard[] AddStats(params (string Caption, string Glyph)[] cards)
    {
        var (row, made) = StatCard.MakeRow(cards);
        statRows.Add(row);
        Children.Add(row.Dock(Dock.Top));
        return made;
    }

    /// <summary>Called when the window gets too short (true) or tall enough again (false).</summary>
    public void SetCompact(bool compact)
    {
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        eyebrow.Visibility = description.Visibility = visibility;
        header.Margin = new Thickness(0, 0, 0, compact ? 12 : 20);
        foreach (var row in statRows)
            row.Visibility = visibility;
    }

    /// <summary>Called each time the page comes into view.</summary>
    public virtual void Shown()
    {
    }

    /// <summary>False keeps the window open (a job is still running).</summary>
    public virtual bool CanClose() => true;

    /// <summary>Called as the window closes.</summary>
    public virtual void Closing()
    {
    }

    /// <summary>Called when the window is closed to the tray: it is kept, hidden, to open again at once.</summary>
    public virtual void Hidden()
    {
    }
}

/// <summary>
/// Navigation down the left: the brand, the pages and the groups that fold open, and at the bottom the disk space,
/// Settings, Help &amp; about and a link to support the creator.
/// </summary>
public sealed class Sidebar : Border
{
    private readonly StackPanel main = new() { Margin = new Thickness(14, 0, 14, 0) };
    private readonly StackPanel bottomItems = new();
    private readonly Dictionary<string, NavItem> items = [];
    /// <summary>Each folding group: its heading, the panel holding its pages, and their keys.</summary>
    private readonly Dictionary<string, (NavItem Heading, StackPanel Pages, string[] Keys)> groups = [];
    private readonly Action<string> onSelect;
    private readonly StackPanel storage;
    private readonly Border barFill = new() { Height = 5, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid bar = new() { Height = 5, Background = Ui.Paint("Raised"), Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock used = Ui.Muted("", 11);
    private readonly TextBlock total = Ui.Muted("", 11);
    private string? selected;
    private double share;

    public Sidebar(Action<string> onSelect)
    {
        this.onSelect = onSelect;
        Width = 242;
        Background = Ui.Paint("SidebarBack");
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(0, 0, 1, 0);
        var layout = new DockPanel();

        // The brand: the logo, and the name ending in an accent full stop. It stays in every language.
        var mark = new Image { Source = Ui.AppIcon(), Width = 66, Height = 66 };
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        var name = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 20 * Ui.TextScale, FontWeight = FontWeights.Bold, Foreground = Ui.Paint("Text"),
                                   VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        name.Inlines.Add(AppInfo.Name);
        name.Inlines.Add(new System.Windows.Documents.Run(".") { Foreground = Ui.Paint("Primary") });
        var brand = Ui.Row(0, mark, name).Margin(14, 18, 8, 20);
        layout.Children.Add(brand.Dock(Dock.Top));

        // The disk the downloads are on, like the design's "Local storage" bar.
        var bottom = new StackPanel { Margin = new Thickness(14, 0, 14, 16) };
        storage = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        storage.Children.Add(new Border { Height = 1, Background = Ui.Paint("Line"), Margin = new Thickness(0, 0, 0, 16) });
        storage.Children.Add(Ui.Row(8, Ui.Glyph("\uEDA2", 13), Ui.Text(Ui.T("Local storage"), 12)));
        bar.Children.Add(barFill);
        bar.SizeChanged += (_, _) => DrawBar();
        storage.Children.Add(bar);
        var numbers = new DockPanel { LastChildFill = false };
        numbers.Children.Add(used);
        numbers.Children.Add(total.Dock(Dock.Right));
        storage.Children.Add(numbers);
        bottom.Children.Add(storage);
        bottom.Children.Add(bottomItems);

        var support = Ui.Text(Ui.T("Support the creator") + " ↗", 12, "Primary", FontWeights.SemiBold).Margin(14, 12, 14, 0);
        support.Cursor = Cursors.Hand;
        support.MouseLeftButtonUp += (_, _) => Shell.Open(AppInfo.DonateUrl);
        bottom.Children.Add(support);
        layout.Children.Add(bottom.Dock(Dock.Bottom));
        // Open groups can make the list taller than a short window.
        layout.Children.Add(new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Child = layout;
    }

    public void Add(string key, string glyph, string text, bool bottom = false)
    {
        var item = new NavItem(glyph, text, () => onSelect(key));
        (bottom ? bottomItems : main).Children.Add(item);
        items[key] = item;
    }

    /// <summary>
    /// A group that folds open under its heading when clicked (Files: Sorter, Duplicates, Storage),
    /// and folds shut on a second click. Only one group is open at a time.
    /// </summary>
    public void AddGroup(string key, string glyph, string text, IReadOnlyList<(string Key, string Glyph, string Name)> pages)
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        var heading = new NavItem(glyph, text, () => Fold(key), folding: true);
        main.Children.Add(heading);
        main.Children.Add(panel);
        foreach (var (page, pageGlyph, name) in pages)
        {
            var item = new NavItem(pageGlyph, name, () => onSelect(page), inGroup: true);
            panel.Children.Add(item);
            items[page] = item;
        }
        groups[key] = (heading, panel, pages.Select(page => page.Key).ToArray());
    }

    /// <summary>Open a group (closing the others), or close it when it is already open.</summary>
    private void Fold(string key, bool open = false)
    {
        var opening = open || groups[key].Pages.Visibility != Visibility.Visible;
        foreach (var (name, (heading, pages, _)) in groups)
        {
            var shown = opening && name == key;
            pages.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            heading.SetOpen(shown);
        }
    }

    public void SetBadge(string key, string text)
    {
        items[key].SetBadge(text);
        // A closed group shows its pages' count on its heading.
        if (groups.Values.FirstOrDefault(entry => entry.Keys.Contains(key)) is { Heading: { } heading })
            heading.SetBadge(text);
    }

    /// <summary>Mark the current page, opening its group.</summary>
    public void Select(string key)
    {
        if (selected is not null)
            items[selected].SetCurrent(false);
        selected = key;
        items[key].SetCurrent(true);
        if (groups.FirstOrDefault(entry => entry.Value.Keys.Contains(key)).Key is { } group)
            Fold(group, open: true);
    }

    /// <summary>Fill the storage bar from the drive the folder is on.</summary>
    public void ShowStorage(string folder)
    {
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!);
            const double Gb = 1024.0 * 1024 * 1024;
            var usedBytes = drive.TotalSize - drive.TotalFreeSpace;
            share = drive.TotalSize > 0 ? (double)usedBytes / drive.TotalSize : 0;
            used.Text = Ui.T("{0} GB used", (usedBytes / Gb).ToString("0"));
            total.Text = $"{drive.TotalSize / Gb:0} GB";
            DrawBar();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            storage.Visibility = Visibility.Collapsed;
        }
    }

    private void DrawBar()
    {
        barFill.Background = Ui.Paint(share > 0.9 ? "Red" : share > 0.75 ? "Amber" : "Primary");
        barFill.Width = Math.Round(bar.ActualWidth * share);
    }
}

/// <summary>One sidebar entry: a glyph, the page name and an optional count badge.</summary>
public sealed class NavItem : Border
{
    private static readonly Brush CurrentFill = Ui.Paint("PrimaryTint");
    private static readonly Brush BadgeFill = new SolidColorBrush(Ui.Mix("#42d8a5", "#050709", 0.12));
    private readonly TextBlock glyph;
    private readonly TextBlock text;
    private readonly TextBlock badge = new() { FontSize = 11 * Ui.TextScale, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border badgeBox;
    private readonly Ellipse currentDot = new() { Width = 5, Height = 5, Fill = Ui.Paint("Primary"), VerticalAlignment = VerticalAlignment.Center,
                                                 Margin = new Thickness(0, 0, 15, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock? chevron;
    private readonly Action onClick;
    private bool current;
    private bool isOpen;

    /// <summary>A sidebar entry. A folding one is a group's heading, with an arrow; one in a group is indented under it.</summary>
    public NavItem(string glyphCode, string label, Action onClick, bool folding = false, bool inGroup = false)
    {
        this.onClick = onClick;
        System.Windows.Automation.AutomationProperties.SetName(this, label);
        Height = inGroup ? 36 : 40;
        CornerRadius = new CornerRadius(6);
        Margin = new Thickness(0, 2, 0, 2);
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        glyph = Ui.Glyph(glyphCode, inGroup ? 14 : 16).Margin(inGroup ? 34 : 16);
        text = Ui.Text(label, inGroup ? 13.5 : 14).Margin(inGroup ? 12 : 14);
        badgeBox = new Border { Background = BadgeFill, Padding = new Thickness(6, 1, 6, 1), Child = badge, Margin = new Thickness(0, 0, 14, 0),
                                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var layout = new DockPanel { LastChildFill = false };
        layout.Children.Add(glyph);
        layout.Children.Add(text);
        layout.Children.Add(currentDot.Dock(Dock.Right));
        if (folding)
        {
            chevron = Ui.Glyph("\uE70D", 10).Margin(0, 0, 14, 0);
            layout.Children.Add(chevron.Dock(Dock.Right));
        }
        layout.Children.Add(badgeBox.Dock(Dock.Right));
        Child = layout;
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) => Paint();
        MouseLeftButtonUp += (_, _) => onClick();
        Paint();
    }

    /// <summary>Screen readers and UI Automation see each entry as a button they can press.</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(NavItem item) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(item),
                                             System.Windows.Automation.Provider.IInvokeProvider
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(NavItem);

        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface pattern) =>
            pattern == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(pattern);

        public void Invoke() => item.onClick();
    }

    public void SetCurrent(bool value)
    {
        current = value;
        Paint();
    }

    /// <summary>A group's heading: the arrow points up while its pages show, and its count hides (each page shows its own).</summary>
    public void SetOpen(bool open)
    {
        if (chevron is null)
            return;
        chevron.Text = open ? "\uE70E" : "\uE70D";
        badgeBox.Visibility = !open && badge.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        isOpen = open;
    }

    public void SetBadge(string value)
    {
        badge.Text = value;
        if (chevron is not null && isOpen)
        {
            badgeBox.Visibility = Visibility.Collapsed;
            return;
        }
        badgeBox.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Paint()
    {
        Background = current ? CurrentFill : IsMouseOver ? Ui.Paint("Secondary") : Brushes.Transparent;
        var ink = Ui.Paint(current ? "Primary" : IsMouseOver ? "Text" : "Muted");
        glyph.Foreground = text.Foreground = badge.Foreground = ink;
        if (chevron is not null)
            chevron.Foreground = ink;
        currentDot.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>A strip across the top of the page area: where you are, and how things stand.</summary>
public sealed class TopBar : Border
{
    private readonly TextBlock page = Ui.Text("", 15, weight: FontWeights.SemiBold);
    private readonly TextBlock pageGlyph = Ui.Glyph("", 16);
    private readonly TextBlock dot = Ui.Text("●", 11, "Primary");
    private readonly TextBlock status = Ui.Muted("", 13).Margin(8);

    public TopBar()
    {
        Height = 58;
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(0, 0, 0, 1);
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(30, 0, 30, 0) };
        row.Children.Add(Ui.Row(0, pageGlyph, page.Margin(10)));
        row.Children.Add(Ui.Row(0, dot, status, Ui.Glyph("\uEA18", 15, "Primary").Margin(14)).Dock(Dock.Right));
        Child = row;
    }

    public void SetPage(string glyph, string title) => (pageGlyph.Text, page.Text) = (glyph, title);

    public void SetStatus(string text, Brush color)
    {
        status.Text = text;
        dot.Foreground = color;
    }
}

/// <summary>
/// The launch screen: the app icon between two corner marks, the name, the tagline, three bars
/// that grow in, and a link to go straight to the workspace. Shown while the program starts, and
/// again from Help &amp; about. It closes itself after a few seconds, or at a click or key press.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly Action? onClose;
    private readonly DispatcherTimer closer;
    private readonly DispatcherTimer grower = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Rectangle[] bars = new Rectangle[3];
    private int step;
    private bool closed;

    public SplashWindow(Action? onClose = null, double seconds = 2.4)
    {
        this.onClose = onClose;
        Ui.StyleWindow(this);
        Title = AppInfo.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(1);

        var frame = new DockPanel { Margin = new Thickness(32, 24, 32, 24) };
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(Ui.Text(AppInfo.Name.ToUpperInvariant(), 11, weight: FontWeights.Bold));
        head.Children.Add(Ui.Muted(Ui.T("Desktop edition"), 11).Dock(Dock.Right));
        frame.Children.Add(head.Dock(Dock.Top));
        frame.Children.Add(new Border { Height = 1, Background = Ui.Paint("Line"), Margin = new Thickness(0, 16, 0, 0) }.Dock(Dock.Top));

        var foot = new DockPanel { LastChildFill = false };
        foot.Children.Add(Ui.Text("●", 9, "Primary"));
        foot.Children.Add(Ui.Muted(Ui.T("A fresh start."), 11).Margin(8));
        foot.Children.Add(Ui.Muted(Ui.T("Less clutter. More clarity."), 11).Dock(Dock.Right));
        frame.Children.Add(foot.Dock(Dock.Bottom));
        frame.Children.Add(new Border { Height = 1, Background = Ui.Paint("Line"), Margin = new Thickness(0, 0, 0, 16) }.Dock(Dock.Bottom));

        // The icon between two corner marks, top-left and bottom-right, like the design.
        const double Stage = 150, Mark = 16;
        // The artwork is not mirrored in a right-to-left window.
        var stage = new Canvas { Width = Stage, Height = Stage, FlowDirection = FlowDirection.LeftToRight };
        stage.Children.Add(new Polyline { Points = [new(0.5, Mark), new(0.5, 0.5), new(Mark, 0.5)], Stroke = Ui.Paint("ControlLine"), StrokeThickness = 1 });
        stage.Children.Add(new Polyline { Points = [new(Stage - Mark, Stage - 1.5), new(Stage - 1.5, Stage - 1.5), new(Stage - 1.5, Stage - Mark)],
                                           Stroke = Ui.Paint("ControlLine"), StrokeThickness = 1 });
        var icon = new Image { Source = Ui.AppIcon(), Width = 118, Height = 118 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(icon, (Stage - 118) / 2);
        Canvas.SetTop(icon, (Stage - 118) / 2);
        stage.Children.Add(icon);

        // "PC Butler." with "Butler." in the accent; the name stays as it is in every language.
        var parts = AppInfo.Name.Split(' ', 2);
        var title = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 35 * Ui.TextScale, FontWeight = FontWeights.Bold, Foreground = Ui.Paint("Text"),
                                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        title.Inlines.Add(parts[0] + " ");
        title.Inlines.Add(new System.Windows.Documents.Run((parts.Length > 1 ? parts[1] : "") + ".") { Foreground = Ui.Paint("Primary") });
        var tagline = Ui.Muted(Ui.T("Your digital space, sorted."), 15).Margin(0, 8);
        tagline.HorizontalAlignment = HorizontalAlignment.Center;

        var barRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Height = 3 };
        var colors = new[] { "Primary", "", "ControlLine" };
        for (var index = 0; index < 3; index++)
        {
            var slot = new Grid { Width = (116 - 10) / 3.0, Margin = new Thickness(index == 0 ? 0 : 5, 0, 0, 0) };
            bars[index] = new Rectangle { Fill = colors[index].Length > 0 ? Ui.Paint(colors[index]) : Ui.Hex("#5aabf1"), Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
            slot.Children.Add(bars[index]);
            barRow.Children.Add(slot);
        }

        var enter = Ui.Muted(Ui.T("Open workspace  →"), 13).Margin(0, 16);
        enter.HorizontalAlignment = HorizontalAlignment.Center;
        enter.Cursor = Cursors.Hand;
        enter.MouseEnter += (_, _) => enter.Foreground = Ui.Paint("Text");
        enter.MouseLeave += (_, _) => enter.Foreground = Ui.Paint("Muted");

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { stage, title, tagline, barRow, enter } };
        frame.Children.Add(center);
        Content = frame;

        MouseLeftButtonUp += (_, _) => CloseSplash();
        KeyDown += (_, _) => CloseSplash();
        grower.Tick += (_, _) => GrowBars();
        grower.Start();
        closer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        closer.Tick += (_, _) => CloseSplash();
        closer.Start();
        Loaded += (_, _) => Activate();
    }

    /// <summary>The three bars scale in one after another, like the design's animation.</summary>
    private void GrowBars()
    {
        step++;
        for (var index = 0; index < bars.Length; index++)
        {
            var progress = Math.Clamp((step - index * 3) / 18.0, 0, 1);
            bars[index].Width = ((116 - 10) / 3.0) * progress;
        }
        if (step >= 24)
            grower.Stop();
    }

    private void CloseSplash()
    {
        if (closed)
            return;
        closed = true;
        closer.Stop();
        grower.Stop();
        Close();
        onClose?.Invoke();
    }
}
