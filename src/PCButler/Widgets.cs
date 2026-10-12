// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCButler.Core;

namespace PCButler;

/// <summary>A ring that fills clockwise from the top, with a number and its unit in the middle.</summary>
public sealed class Gauge : FrameworkElement
{
    private double progress;
    private string number = "—", unit = "";
    private Brush stroke = Ui.Paint("Primary");

    public Gauge(double size = 100)
    {
        Width = Height = size;
        // The ring always fills clockwise and the digits are never mirrored, also in a right-to-left window.
        FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>progress is 0-100; tone is a theme colour name ("Primary", "Blue", "Amber", "Red").</summary>
    public void Set(string numberText, string unitText, double percent, string tone = "Primary")
    {
        (number, unit, progress, stroke) = (numberText, unitText, Math.Clamp(percent, 0, 100), Ui.Paint(tone));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        const double Thickness = 3.5;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - Thickness;
        if (radius <= 0)
            return;
        drawing.DrawEllipse(null, new Pen(Ui.Paint("Raised"), Thickness), centre, radius, radius);
        var pen = new Pen(stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (progress >= 99.9)
        {
            drawing.DrawEllipse(null, pen, centre, radius, radius);
        }
        else
        {
            var angle = Math.Max(progress, 1) * 3.6 * Math.PI / 180;
            var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
            var arc = new StreamGeometry();
            using (var context = arc.Open())
            {
                context.BeginFigure(new Point(centre.X, centre.Y - radius), false, false);
                context.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true, true);
            }
            arc.Freeze();
            drawing.DrawGeometry(null, pen, arc);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var figure = new FormattedText(number, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                       new Typeface(Ui.DisplayFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                                       ActualWidth * (number.Length > 4 ? 0.19 : 0.24), Ui.Paint("Text"), dpi);
        var caption = new FormattedText(unit, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                        new Typeface((FontFamily)Application.Current.FindResource("TextFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                        ActualWidth * 0.11, Ui.Paint("Muted"), dpi) { MaxTextWidth = radius * 1.7, TextAlignment = TextAlignment.Center };
        var height = figure.Height + caption.Height;
        drawing.DrawText(figure, new Point(centre.X - figure.Width / 2, centre.Y - height / 2));
        // A centred, width-limited text is placed by the left edge of its box; Arabic inside it still reads right to left.
        drawing.DrawText(caption, new Point(centre.X - radius * 0.85, centre.Y - height / 2 + figure.Height));
    }
}

/// <summary>A horizontal track with a rounded fill, for amounts (space used, the neatness score).</summary>
public sealed class MeterBar : Grid
{
    private readonly Border fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private double share;

    public MeterBar(double height = 8)
    {
        Height = height;
        var track = new Border { Background = Ui.Paint("Raised"), CornerRadius = new CornerRadius(height / 2) };
        fill.CornerRadius = new CornerRadius(height / 2);
        fill.Background = Ui.Paint("Primary");
        Children.Add(track);
        Children.Add(fill);
        SizeChanged += (_, _) => Draw();
    }

    /// <summary>percent is 0-100.</summary>
    public void Set(double percent, string tone = "Primary") => Set(percent, Ui.Paint(tone));

    public void Set(double percent, Brush tone)
    {
        share = Math.Clamp(percent, 0, 100) / 100;
        fill.Background = tone;
        Draw();
    }

    private void Draw() => fill.Width = Math.Max(share > 0 ? Height : 0, Math.Round(ActualWidth * share));
}

/// <summary>
/// A Home-page card: a caption with its icon, a gauge beside a heading and a line of
/// detail, and a footer saying how things stand.
/// </summary>
public sealed class MetricCard : Border
{
    private readonly Gauge gauge = new(96);
    private readonly TextBlock heading = Ui.Text("", 15, weight: FontWeights.Medium, wrap: true);
    private readonly TextBlock description = Ui.Muted("", 12.5, wrap: true);
    private readonly TextBlock footer = Ui.Muted("", 11.5);
    private readonly Ellipse dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    public MetricCard(string caption, string glyph, string tag)
    {
        Widgets.StyleCard(this);
        Padding = new Thickness(18, 12, 18, 12);
        var layout = new DockPanel();
        var top = new DockPanel { LastChildFill = false };
        top.Children.Add(Ui.Muted(Ui.T(caption), 13));
        top.Children.Add(Ui.Glyph(glyph, 15).Dock(Dock.Right));
        layout.Children.Add(top.Dock(Dock.Top));
        var bottom = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(dot);
        bottom.Children.Add(footer);
        bottom.Children.Add(Ui.Muted(Ui.T(tag), 11.5).Dock(Dock.Right));
        layout.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom }.Dock(Dock.Bottom));
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        info.Children.Add(heading);
        info.Children.Add(description.Margin(0, 4, 0, 0));
        var body = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        body.Children.Add(gauge.Dock(Dock.Left));
        body.Children.Add(info);
        layout.Children.Add(body);
        Child = layout;
    }

    public void Set(string number, string unit, double percent, string tone, string headingText, string descriptionText, string footerText)
    {
        gauge.Set(number, unit, percent, tone);
        (heading.Text, description.Text, footer.Text) = (headingText, descriptionText, footerText);
        dot.Fill = Ui.Paint(tone);
    }
}

/// <summary>A Home-page card for an amount: a big figure over a bar, with a label at each end.</summary>
public sealed class AmountCard : Border
{
    private readonly TextBlock number = new() { FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Ui.Paint("Text"), FontFamily = Ui.DisplayFont };
    private readonly TextBlock unit = Ui.Muted("", 15).Margin(9, 0, 0, 6);
    private readonly MeterBar bar = new();
    private readonly TextBlock low = Ui.Muted("", 12.5), high = Ui.Muted("", 12.5);
    private readonly TextBlock footer = Ui.Muted("", 11.5), tag = Ui.Muted("", 11.5);
    private readonly Ellipse dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    public AmountCard(string caption, string glyph)
    {
        Widgets.StyleCard(this);
        Padding = new Thickness(18, 12, 18, 12);
        var layout = new DockPanel();
        var top = new DockPanel { LastChildFill = false };
        top.Children.Add(Ui.Muted(Ui.T(caption), 13));
        top.Children.Add(Ui.Glyph(glyph, 15).Dock(Dock.Right));
        layout.Children.Add(top.Dock(Dock.Top));
        var bottom = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(dot);
        bottom.Children.Add(footer);
        bottom.Children.Add(tag.Dock(Dock.Right));
        layout.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom }.Dock(Dock.Bottom));
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        var legend = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        legend.Children.Add(low);
        legend.Children.Add(high.Dock(Dock.Right));
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
        body.Children.Add(Ui.Row(0, number, unit));
        body.Children.Add(bar.Margin(0, 12, 0, 0));
        body.Children.Add(legend);
        layout.Children.Add(body);
        Child = layout;
    }

    public void Set(string numberText, string unitText, double percent, string tone, string lowText, string highText, string footerText, string tagText)
    {
        (number.Text, unit.Text, low.Text, high.Text, footer.Text, tag.Text) = (numberText, unitText, lowText, highText, footerText, tagText);
        bar.Set(percent, tone);
        dot.Fill = Ui.Paint(tone);
    }
}

public static class Widgets
{
    /// <summary>The look every card shares: the card colour, a hairline border, rounded corners.</summary>
    public static void StyleCard(Border card)
    {
        card.Background = Ui.Paint("Card");
        card.BorderBrush = Ui.Paint("Line");
        card.BorderThickness = new Thickness(1);
        card.CornerRadius = new CornerRadius(8);
    }

    /// <summary>The version in a softly lit pill, as at the top of the Home page.</summary>
    public static Border VersionPill()
    {
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Paint("Primary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        var text = new TextBlock
        {
            Text = "v" + AppInfo.Version, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Paint("Primary"), VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 15, 5), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Ui.Mix("#42d8a5", "#080a0e", 0.10)), BorderBrush = new SolidColorBrush(Ui.Mix("#42d8a5", "#080a0e", 0.30)),
            BorderThickness = new Thickness(1), Child = Ui.Row(0, dot, text),
        };
    }
}
