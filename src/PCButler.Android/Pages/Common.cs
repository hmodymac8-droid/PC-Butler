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
                Padding = new Thickness(18, 24, 18, 32),
                Children = { Theme.Title(Tr.T(title), 28), Theme.Faint(Tr.T(description)).Margin(0, 4, 0, 18), Body },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
    protected View FoldersCard(string page, string intro, Action changed)
    {
        var list = new VerticalStackLayout { Spacing = 6 };
        void Show()
        {
            list.Children.Clear();
            var folders = Folders.For(page);
            if (folders.Count == 0)
                list.Children.Add(Theme.Faint(Tr.T("No folder chosen yet."), 12.5));
            foreach (var tree in folders)
            {
                var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
                row.Add(Theme.Body("📁  " + Folders.Label(tree), 14).Margin(0, 8, 0, 0), 0);
                row.Add(Theme.Plain("✕", async () =>
                {
                    if (!await Ask(Tr.T("Stop using {0} here? Nothing in it is changed.", Folders.Label(tree)), Tr.T("Remove")))
                        return;
                    Folders.Remove(page, tree);
                    Show();
                    changed();
                }), 1);
                list.Children.Add(row);
            }
        }
        Show();
        return Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Theme.Title(Tr.T("Folders"), 18),
                Theme.Faint(Tr.T(intro)),
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

    /// <summary>A line with a tick box, the way lists of files are chosen.</summary>
    public static (View Row, CheckBox Box) Tickable(string title, string detail, bool ticked = false)
    {
        var box = new CheckBox { IsChecked = ticked, Color = Theme.Primary, VerticalOptions = LayoutOptions.Center };
        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { Theme.Body(title, 14), Theme.Faint(detail, 12) } };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 6 };
        row.Add(box, 0);
        row.Add(texts, 1);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => box.IsChecked = !box.IsChecked;
        texts.GestureRecognizers.Add(tap);
        return (row, box);
    }

    /// <summary>A bar that shows how full something is.</summary>
    public static View Meter(double part, Color color)
    {
        var bar = new ProgressBar { Progress = Math.Clamp(part, 0, 1), ProgressColor = color, HeightRequest = 8 };
        return bar;
    }
}
