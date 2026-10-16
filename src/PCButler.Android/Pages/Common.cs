// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// What every page of the app shares: the "PC Butler" bar on top, a compact title with room
/// for a status and a button, sheets from the bottom, sizes in words and the folders chips.
/// </summary>
public abstract class AppPage : ContentPage
{
    protected readonly VerticalStackLayout Body = new() { Spacing = 0 };

    /// <summary>The page's root: the bar, the scrolling content, and the sheets and floating bars over them.</summary>
    protected readonly Grid Root = new() { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };

    /// <summary>Under the title: a status pill, a count…</summary>
    protected readonly ContentView HeaderSub = new() { IsVisible = false };

    /// <summary>At the end of the title: a round button (settings, refresh, lock…).</summary>
    protected readonly ContentView HeaderRight = new() { VerticalOptions = LayoutOptions.Center };

    protected readonly ScrollView Scroller;

    protected AppPage(string title)
    {
        Title = Tr.T(title);
        var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 12, Padding = new Thickness(4, 14, 2, 8) };
        head.Add(new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, Children = { Theme.Title(Tr.T(title), 24), HeaderSub } }, 0);
        head.Add(HeaderRight, 1);
        Scroller = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = new Thickness(18, 0, 18, 32), Children = { head, Body } },
        };
        Root.Add(Bar(), 0, 0);
        Root.Add(Scroller, 0, 1);
        Content = Root;
    }

    /// <summary>The bar on top of every page: "PC Butler", Butler in the accent.</summary>
    private static View Bar()
    {
        var name = new Label
        {
            FontSize = 16, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center,
            FormattedText = new FormattedString { Spans = { new Span { Text = "PC ", TextColor = Theme.Text }, new Span { Text = "Butler", TextColor = Theme.Primary } } },
        };
        return new VerticalStackLayout
        {
            Children =
            {
                new ContentView { Content = name, Padding = new Thickness(22, 0), HeightRequest = 52 },
                Theme.Divider(),
            },
        };
    }

    protected void SetSub(View? view)
    {
        HeaderSub.Content = view;
        HeaderSub.IsVisible = view is not null;
    }

    /// <summary>A sheet of choices over this page.</summary>
    protected Task<string?> Choose(string title, IEnumerable<SheetChoice> choices, (string Icon, Color Back)? tile = null) => Sheets.Choose(Root, title, choices, tile);

    /// <summary>Any content in a sheet over this page; the returned action closes it.</summary>
    protected Action OpenSheet(string title, View content, Action? closed = null) => Sheets.Open(Root, Sheets.TitleText(title), content, closed);

    /// <summary>A bar that floats over the bottom of the page (Delete extras, what is selected).</summary>
    protected void Float(View bar)
    {
        bar.VerticalOptions = LayoutOptions.End;
        bar.Margin = new Thickness(12, 0, 12, 14);
        Root.Add(bar, 0, 1);
    }

    protected override bool OnBackButtonPressed() => Sheets.CloseTop() || base.OnBackButtonPressed();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        showFolders?.Invoke(); // the list may have changed on another page
        Shown();
    }

    /// <summary>The page came into view (again).</summary>
    protected virtual void Shown()
    {
    }

    private Action? showFolders;

    /// <summary>
    /// The folders this page works in, as chips that scroll sideways: each with ✕ to stop using
    /// it, and "+ Add folder" (Android's folder picker). changed: after the list changed.
    /// </summary>
    protected View FoldersChips(string page, Action changed)
    {
        var row = new HorizontalStackLayout { Spacing = 8 };
        void Show()
        {
            row.Children.Clear();
            foreach (var tree in Folders.For(page))
            {
                var name = new Label { Text = Folders.Label(tree), FontSize = 13.5, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.NoWrap };
                var remove = Theme.IconButton("i_x_w.png", async () =>
                {
                    if (!await Ask(Tr.T("Stop using {0} here? Nothing in it is changed.", Folders.Label(tree)), Tr.T("Remove")))
                        return;
                    Folders.Remove(page, tree);
                    Show();
                    changed();
                }, Theme.SurfaceRaised, 28);
                row.Children.Add(new Border
                {
                    BackgroundColor = Theme.Card, StrokeThickness = 0, StrokeShape = Theme.Round(19), HeightRequest = 38, Padding = new Thickness(12, 0, 5, 0),
                    Content = new HorizontalStackLayout { Spacing = 8, Children = { Theme.Icon("i_folder_open_m.png", 16), name, remove } },
                });
            }
            var add = new Border
            {
                BackgroundColor = Colors.Transparent, Stroke = Theme.Primary, StrokeThickness = 1, StrokeDashArray = [4, 3], StrokeShape = Theme.Round(19),
                HeightRequest = 38, Padding = new Thickness(12, 0),
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children = { Theme.Icon("i_plus_m.png", 16), new Label { Text = Tr.T("Add folder"), FontSize = 13.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, VerticalOptions = LayoutOptions.Center } },
                },
            };
            add.Tapped(async () =>
            {
                if (!await Folders.Add(page))
                    return;
                Show();
                changed();
            });
            row.Children.Add(add);
        }
        Show();
        showFolders = Show;
        var shared = page == "sorter" ? "" : " " + Tr.T("Duplicates and Storage use the same folders.");
        return new VerticalStackLayout
        {
            Children =
            {
                new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = row },
                Theme.Hint(Tr.T("Android only lets PC Butler see folders you pick.") + shared),
            },
        };
    }

    protected Task Say(string text) => DisplayAlert(Title, text, Tr.T("OK"));

    protected Task<bool> Ask(string text, string yes) => DisplayAlert(Title, text, yes, Tr.T("Cancel"));

    public static string Size(long bytes) => CourierPage.Size(bytes);

    /// <summary>The phone's shared storage: what the user sees as "Internal storage".</summary>
    public static string PhoneRoot => global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";

    /// <summary>
    /// A line with a tick box, the way lists of files are chosen: a dark box that turns mint
    /// while ticked, maybe with a file-type tile. detail can change its text with the tick.
    /// </summary>
    public static (View Row, CheckBox Box) Tickable(string title, string detail, bool ticked = false, (string Icon, Color Back)? tile = null,
                                                   Func<bool, string>? detailFor = null)
    {
        var box = new CheckBox { IsChecked = ticked, Color = Theme.Primary, VerticalOptions = LayoutOptions.Center };
        var under = Theme.Faint(detailFor?.Invoke(ticked) ?? detail, 12);
        var top = new Label { Text = title, FontSize = 14, TextColor = Theme.Text, LineBreakMode = LineBreakMode.CharacterWrap };
        var texts = Theme.TwoLines(top, under);
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8 };
        grid.Add(box, 0);
        if (tile is { } kind)
            grid.Add(Theme.Tile(kind, 34), 1);
        grid.Add(texts, 2);
        var row = Theme.BoxOf(grid, 8);
        void Paint()
        {
            row.BackgroundColor = box.IsChecked ? TickedBack : Theme.Surface;
            if (detailFor is not null)
                under.Text = detailFor(box.IsChecked);
        }
        box.CheckedChanged += (_, _) => Paint();
        Paint();
        texts.Tapped(() => box.IsChecked = !box.IsChecked);
        return (row, box);
    }

    /// <summary>A ticked row: the accent, a little see-through.</summary>
    public static readonly Color TickedBack = Theme.PrimarySoft;

    /// <summary>A bar that shows how full something is.</summary>
    public static View Meter(double part, Color color) => Theme.Bar(part, color, 8);
}
