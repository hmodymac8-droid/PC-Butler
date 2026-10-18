// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// What each version brought, newest first. Opens by itself once after an update, short:
/// only the versions since the one used before, each change cut to its first sentence, with
/// "Show the details" for the rest. From Help &amp; about it shows every version in full.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    private readonly StackPanel log = new() { Margin = new Thickness(0, 0, 12, 0) };

    /// <summary>since: the version used before this update (only what came after it, in short); null for the whole history.</summary>
    public WhatsNewWindow(string? since = null)
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

        var page = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var close = Ui.Button(Ui.T("Close"), Close, accent: true);
        close.IsDefault = close.IsCancel = true;
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        bottom.Children.Add(close.Dock(Dock.Right));
        if (since is not null)
        {
            Show(since, brief: true);
            if (log.Children.Count == 0)
                Show(null, brief: true); // an unknown older version: the latest one, in short
            Button? details = null;
            details = Ui.Button(Ui.T("Show the details"), () =>
            {
                Show(null, brief: false);
                details!.Visibility = Visibility.Collapsed;
            });
            bottom.Children.Add(details.Dock(Dock.Left));
        }
        else
            Show(null, brief: false);

        page.Children.Add(Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4).Dock(Dock.Top));
        var title = Ui.Text(Ui.T("What's new"), 27, weight: FontWeights.Bold);
        title.FontFamily = Ui.DisplayFont;
        page.Children.Add(title.Margin(0, 0, 0, 16).Dock(Dock.Top));
        page.Children.Add(bottom.Dock(Dock.Bottom));
        page.Children.Add(new ScrollViewer { Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = page;
    }

    /// <summary>Fill the list: the versions after since (or all of them; with since null and brief, only the newest), in short or in full.</summary>
    private void Show(string? since, bool brief)
    {
        log.Children.Clear();
        var entries = Changelog.Entries(Tr.IsArabic);
        var shown = since is not null && Version.TryParse(since, out var before)
            ? entries.Where(entry => Version.TryParse(entry.Version, out var version) && version > before).ToList()
            : brief ? entries.Take(1).ToList() : entries;
        foreach (var entry in shown)
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
                line.Children.Add(Ui.Text(brief ? Changelog.Headline(change) : change, brush: current ? "Text" : "Muted", wrap: true));
                log.Children.Add(line);
            }
        }
    }
}
