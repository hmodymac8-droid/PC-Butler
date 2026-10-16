// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>What every page of the app shares: a title, sizes and dates in words, the folders card.</summary>
public abstract class AppPage : ContentPage
{
    protected readonly VerticalStackLayout Body = new() { Spacing = 0 };

    protected AppPage(string title, string description)
    {
        Title = Tr.T(title);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 22, 18, 32),
                Children = { PageHeader(title, description), Body },
            },
        };
    }

    private static readonly string[] Order = ["Courier", "Sorter", "Duplicates", "Storage", "Vault"];

    /// <summary>The top of a page: "02 · SORTER" in small letters, the title, and what the page is for.</summary>
    public static View PageHeader(string title, string description)
    {
        var number = Array.IndexOf(Order, title) + 1;
        var kicker = new Label
        {
            Text = $"{number:00} · {Tr.T(title).ToUpper(System.Globalization.CultureInfo.CurrentCulture)}", FontSize = 11.5, CharacterSpacing = 2.5,
            FontFamily = "monospace", TextColor = Theme.Kicker,
        };
        return new VerticalStackLayout
        {
            Spacing = 4, Margin = new Thickness(2, 0, 2, 18),
            Children = { kicker, Theme.Title(Tr.T(title), 30), Theme.Faint(Tr.T(description), 14) },
        };
    }

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

    /// <summary>
    /// The card listing the folders this page works in, with "Add folder…" (Android's folder
    /// picker) and a way to take one off. changed: after the list changed.
    /// </summary>
    private Action? showFolders;

    protected View FoldersCard(string page, string intro, Action changed)
    {
        var list = new VerticalStackLayout { Spacing = 8 };
        void Show()
        {
            list.Children.Clear();
            var folders = Folders.For(page);
            if (folders.Count == 0)
                list.Children.Add(Theme.BoxOf(Theme.Faint(Tr.T("No folder chosen yet."), 13)));
            foreach (var tree in folders)
            {
                var remove = Theme.Plain("✕", async () =>
                {
                    if (!await Ask(Tr.T("Stop using {0} here? Nothing in it is changed.", Folders.Label(tree)), Tr.T("Remove")))
                        return;
                    Folders.Remove(page, tree);
                    Show();
                    changed();
                });
                (remove.BackgroundColor, remove.TextColor, remove.Padding, remove.VerticalOptions) = (Colors.Transparent, Theme.Muted, new Thickness(12, 6), LayoutOptions.Center);
                var name = new Label { Text = Folders.Label(tree), FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.MiddleTruncation };
                list.Children.Add(Theme.BoxOf(Theme.Line3(Theme.Tile(Theme.FolderTile, 34), name, remove), 8));
            }
        }
        Show();
        showFolders = Show;
        var shared = page == "sorter" ? "" : Tr.T("Duplicates and Storage use the same folders.");
        return Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Header(Tr.T("Folders")),
                Theme.Faint(Tr.T(intro) + (shared.Length > 0 ? " " + shared : "")),
                list,
                Theme.Plain(Tr.T("Add folder…"), async () =>
                {
                    if (!await Folders.Add(page))
                        return;
                    Show();
                    changed();
                }),
                Theme.Faint(Tr.T("Android lets apps use only the folders you choose. It doesn't offer Download itself or the whole phone: choose the folders inside them instead."), 12),
            },
        });
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
    public static readonly Color TickedBack = Color.FromArgb("#132a22");

    /// <summary>A bar that shows how full something is.</summary>
    public static View Meter(double part, Color color) => Theme.Bar(part, color, 8);
}
