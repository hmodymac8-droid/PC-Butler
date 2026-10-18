// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCButler;

/// <summary>
/// The Internet page's "right now" list: each app using the internet, with its share of the
/// traffic as a bar and a percentage, and its speed. Built to stay still: speeds are averaged
/// over the last <see cref="AverageSeconds"/> seconds, an app stays listed until it has been
/// quiet for <see cref="KeepSeconds"/> seconds, rows are updated in place, and the order (by
/// what each app moved in the last minute) is looked at again only every few seconds.
/// </summary>
public sealed class LiveNetList : Border
{
    public const int AverageSeconds = 10;
    public const int KeepSeconds = 60;
    private const int ReorderEvery = 5;
    private const int MostApps = 12;
    private const double BarWidth = 200;

    private sealed class App
    {
        public readonly long[] Down = new long[KeepSeconds], Up = new long[KeepSeconds];
        public required Grid View;
        public required Border Fill;
        public required TextBlock Percent, Speed;
        public long Recent, Minute, RecentDown, RecentUp;
    }

    private readonly Dictionary<string, App> apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel rows = new();
    private readonly TextBlock empty = Ui.Muted("", 13, wrap: true);
    private int second;
    private int ticks;

    public LiveNetList()
    {
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 8, 14, 8);
        MinHeight = 90;
        var header = Line(Ui.Muted(Ui.T("App"), 12), Ui.Muted(Ui.T("Share of the traffic"), 12), new TextBlock(), Ui.Muted(Ui.T("Speed (average of 10 s)"), 12));
        header.Margin = new Thickness(0, 0, 0, 6);
        empty.Margin = new Thickness(0, 6, 0, 6);
        Child = new StackPanel { Children = { header, rows, empty } };
        Clear(Ui.T("Nothing is using the internet right now."));
    }

    /// <summary>Show a message instead of the apps (no helper, tracker off) and forget what was counted.</summary>
    public void Clear(string message)
    {
        apps.Clear();
        rows.Children.Clear();
        empty.Text = message;
        empty.Visibility = Visibility.Visible;
    }

    /// <summary>Once a second: the bytes each app received and sent in that second.</summary>
    public void Add(IReadOnlyDictionary<string, (long Down, long Up)> thisSecond)
    {
        second = (second + 1) % KeepSeconds;
        foreach (var (name, (down, up)) in thisSecond)
        {
            if (down + up <= 0)
                continue;
            if (!apps.TryGetValue(name, out var app))
                apps[name] = app = Make(name);
            app.Down[second] = down;
            app.Up[second] = up;
        }
        foreach (var (name, app) in apps)
        {
            if (!thisSecond.ContainsKey(name))
                app.Down[second] = app.Up[second] = 0;
            (app.RecentDown, app.RecentUp, app.Minute) = (0, 0, 0);
            for (var back = 0; back < KeepSeconds; back++)
            {
                var index = (second - back + KeepSeconds) % KeepSeconds;
                app.Minute += app.Down[index] + app.Up[index];
                if (back < AverageSeconds)
                {
                    app.RecentDown += app.Down[index];
                    app.RecentUp += app.Up[index];
                }
            }
            app.Recent = app.RecentDown + app.RecentUp;
        }
        // An app quiet for a whole minute leaves the list.
        foreach (var name in apps.Where(item => item.Value.Minute == 0).Select(item => item.Key).ToList())
        {
            rows.Children.Remove(apps[name].View);
            apps.Remove(name);
        }

        var total = Math.Max(1, apps.Values.Sum(app => app.Recent));
        foreach (var app in apps.Values)
        {
            var share = (double)app.Recent / total;
            app.Percent.Text = app.Recent == 0 ? "0%" : share < 0.01 ? "<1%" : $"{share * 100:0}%";
            app.Speed.Text = $"↓ {Ui.FormatSize(app.RecentDown / AverageSeconds)}/s   ↑ {Ui.FormatSize(app.RecentUp / AverageSeconds)}/s";
            // The bar glides to its new length rather than jumping.
            app.Fill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(2, share * BarWidth), TimeSpan.FromMilliseconds(800))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        // The order moves slowly: by the last minute's traffic, and only every few seconds (or when an app joins).
        var shown = rows.Children.Count;
        if (++ticks % ReorderEvery == 0 || shown != Math.Min(MostApps, apps.Count))
        {
            var order = apps.Values.OrderByDescending(app => app.Minute).Take(MostApps).Select(app => (UIElement)app.View).ToList();
            if (!order.SequenceEqual(rows.Children.Cast<UIElement>()))
            {
                rows.Children.Clear();
                foreach (var view in order)
                    rows.Children.Add(view);
            }
        }
        empty.Text = Ui.T("Nothing is using the internet right now.");
        empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static App Make(string name)
    {
        var fill = new Border { Width = 2, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Paint("Primary"), HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Grid { Width = BarWidth, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Ui.Paint("Raised") });
        track.Children.Add(fill);
        var percent = Ui.Text("", 13, weight: FontWeights.SemiBold);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        var speed = Ui.Muted("", 12.5);
        var label = Ui.Text(name, 13.5);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var view = Line(label, track, percent, speed);
        view.Margin = new Thickness(0, 4, 0, 4);
        return new App { View = view, Fill = fill, Percent = percent, Speed = speed };
    }

    /// <summary>One row of four columns: name, bar, percentage, speed.</summary>
    private static Grid Line(FrameworkElement name, FrameworkElement bar, FrameworkElement percent, FrameworkElement speed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth + 16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.Children.Add(name.At(0, 0));
        grid.Children.Add(bar.At(0, 1));
        grid.Children.Add(percent.At(0, 2));
        speed.Margin = new Thickness(18, 0, 0, 0);
        grid.Children.Add(speed.At(0, 3));
        foreach (FrameworkElement child in grid.Children)
            child.VerticalAlignment = VerticalAlignment.Center;
        return grid;
    }
}
