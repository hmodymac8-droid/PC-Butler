// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>What every page of the app shares: a title, the "All files access" banner, sizes and dates in words.</summary>
public abstract class AppPage : ContentPage
{
    private readonly Border allFiles;
    protected readonly VerticalStackLayout Body = new() { Spacing = 0 };

    protected AppPage(string title, string description, bool needsAllFiles)
    {
        Title = Tr.T(title);
        allFiles = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Title(Tr.T("Allow “All files access”"), 17),
                Theme.Faint(Tr.T("This part of PC Butler looks through the phone's files. Android asks you to allow that once, on its own settings page.")),
                Theme.Accent(Tr.T("Allow"), Access.AskForAllFiles),
            },
        });
        allFiles.Stroke = Theme.Amber;
        allFiles.IsVisible = false;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 24, 18, 32),
                Children = { Theme.Title(Tr.T(title), 28), Theme.Faint(Tr.T(description)).Margin(0, 4, 0, 18), allFiles, Body },
            },
        };
        NeedsAllFiles = needsAllFiles;
    }

    private bool NeedsAllFiles { get; }

    /// <summary>True when the page may look through the phone (or does not need to).</summary>
    protected bool Allowed => !NeedsAllFiles || Access.HasAllFiles;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        allFiles.IsVisible = !Allowed;
        Body.IsEnabled = Allowed;
        Body.Opacity = Allowed ? 1 : 0.4;
        Shown();
    }

    /// <summary>The page came into view (again).</summary>
    protected virtual void Shown()
    {
    }

    protected Task Say(string text) => DisplayAlert(Title, text, Tr.T("OK"));

    protected Task<bool> Ask(string text, string yes) => DisplayAlert(Title, text, yes, Tr.T("Cancel"));

    public static string Size(long bytes) => CourierPage.Size(bytes);

    /// <summary>The phone's shared storage: what the user sees as "Internal storage".</summary>
    public static string PhoneRoot => global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";

    /// <summary>A path shown from the phone's storage on, as people know it ("Download/Pictures/a.jpg").</summary>
    public static string Short(string path) =>
        path.StartsWith(PhoneRoot, StringComparison.Ordinal) ? path[PhoneRoot.Length..].TrimStart('/') : path;

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
