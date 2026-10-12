// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>What each version brought, newest first. Opens by itself once after an update, and from Help &amp; about.</summary>
public sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("What's new");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(640, area.Width - 40);
        Height = Math.Min(640, area.Height - 40);
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var log = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var entry in Changelog.Entries(Tr.IsArabic))
        {
            var current = entry.Version == AppInfo.Version;
            var heading = Ui.Row(10, Ui.Text(Ui.T("Version {0}", entry.Version), 16, current ? "Primary" : "Text", FontWeights.Bold));
            if (current)
                heading.Children.Add(Ui.Muted(Ui.T("the version you have"), 12).Margin(0, 4));
            log.Children.Add(heading.Margin(0, log.Children.Count == 0 ? 0 : 18, 0, 6));
            foreach (var change in entry.Changes)
            {
                var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = Ui.Text("•", brush: current ? "Primary" : "Muted");
                dot.VerticalAlignment = VerticalAlignment.Top;
                line.Children.Add(dot.Margin(2, 0, 8).Dock(Dock.Left));
                line.Children.Add(Ui.Text(change, brush: current ? "Text" : "Muted", wrap: true));
                log.Children.Add(line);
            }
        }

        var page = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4).Dock(Dock.Top));
        var title = Ui.Text(Ui.T("What's new"), 27, weight: FontWeights.Bold);
        title.FontFamily = Ui.DisplayFont;
        page.Children.Add(title.Margin(0, 0, 0, 16).Dock(Dock.Top));
        var close = Ui.Button(Ui.T("Close"), Close, accent: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.IsDefault = close.IsCancel = true;
        page.Children.Add(close.Margin(0, 16, 0, 0).Dock(Dock.Bottom));
        page.Children.Add(new ScrollViewer { Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = page;
    }
}
