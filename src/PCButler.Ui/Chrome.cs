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

    /// <summary>A tool's own window: a small "Board" button at the far end, which opens the board.</summary>
    public void AddBoard(Action open)
    {
        if (Child is not DockPanel row)
            return;
        var button = new Button
        {
            Content = Ui.Row(6, Ui.Glyph("\uF0E2", 14, "Primary"), Ui.Text(Ui.T("Board"), 13)),
            ToolTip = Ui.T("Open the board with every tool"),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => open();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Insert(0, button);
    }

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
