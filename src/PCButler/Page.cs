// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows;

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

    protected ToolWindow Main { get; }
    /// <summary>The right side of the header: the page's main buttons, the first one rightmost.</summary>
    protected StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

    protected Page(ToolWindow main, string title, string descriptionText)
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
