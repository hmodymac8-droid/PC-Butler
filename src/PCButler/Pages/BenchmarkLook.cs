// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The Benchmark page's two cards for arranging the overlay yourself: which readings it
/// shows and in what order, and how it looks (layout, size, place, colours).
/// </summary>
public static class BenchmarkLook
{
    private static readonly Dictionary<string, string> RowNames = new()
    {
        ["fps"] = "Frames per second", ["frame_time"] = "Frame time", ["cpu"] = "Processor", ["gpu"] = "Graphics card",
        ["vram"] = "Video memory (VRAM)", ["fan"] = "Graphics card fan", ["ram"] = "Memory (RAM)", ["net"] = "Network speed", ["clock"] = "Clock",
    };

    private static readonly string[] Corners = ["Top left", "Top right", "Bottom left", "Bottom right"];
    private static readonly string[] Accents = ["#42D8A5", "#4DA3FF", "#FFC857", "#FF6B6B", "#C77DFF", "#00E5FF", "#FF8FAB", "#FFFFFF"];
    private static readonly string[] Texts = ["#E8EBF0", "#FFFFFF", "#FFE066", "#9BE7FF", "#B9FBC0", "#FFB3B3"];
    private static readonly string[] Backgrounds = ["#101216", "#000000", "#101828", "#1F1F1F", "#2B1B3D", "#0F2A1D"];

    /// <summary>Which readings the overlay shows, top to bottom: a tick box and two arrows for each.</summary>
    public static CardPanel Rows(Func<OverlaySettings> settings, Action<Action<OverlaySettings>> change)
    {
        var card = new CardPanel("What it shows, and in what order", "");
        var list = new StackPanel { Margin = new Thickness(24, 10, 24, 12) };
        void Fill()
        {
            list.Children.Clear();
            var order = settings().OrderedRows();
            for (var index = 0; index < order.Count; index++)
            {
                var (key, place) = (order[index], index);
                var box = new CheckBox { Content = Ui.T(RowNames[key]), IsChecked = settings().Shows(key), VerticalAlignment = VerticalAlignment.Center, MinWidth = 260 };
                box.Click += (_, _) => change(overlay => overlay.SetShown(key, box.IsChecked == true));
                void Move(int by)
                {
                    change(overlay =>
                    {
                        var rows = overlay.OrderedRows();
                        rows.RemoveAt(place);
                        rows.Insert(place + by, key);
                        overlay.Order = rows;
                    });
                    Fill();
                }
                var up = Ui.Button("▲", () => Move(-1));
                var down = Ui.Button("▼", () => Move(1));
                (up.IsEnabled, down.IsEnabled) = (place > 0, place < order.Count - 1);
                (up.ToolTip, down.ToolTip) = (Ui.T("Move up"), Ui.T("Move down"));
                (up.Padding, down.Padding) = (new Thickness(9, 2, 9, 2), new Thickness(9, 2, 9, 2));
                var number = Ui.Muted((place + 1).ToString(), 12);
                number.Width = 22;
                number.VerticalAlignment = VerticalAlignment.Center;
                list.Children.Add(Ui.Row(8, number, box, up, down).Margin(0, 3, 0, 3));
            }
        }
        Fill();
        card.SetBody(list);
        return card;
    }

    /// <summary>How the overlay looks: layout, size, where it sits, and its colours.</summary>
    public static CardPanel Look(Func<OverlaySettings> settings, Action<Action<OverlaySettings>> change)
    {
        var card = new CardPanel("How it looks", "");
        var rows = new StackPanel();
        void Fill()
        {
            rows.Children.Clear();
            var now = settings();
            var layout = new ComboBox { Width = 190 };
            layout.Items.Add(Ui.T("A column"));
            layout.Items.Add(Ui.T("One line"));
            layout.SelectedIndex = now.Horizontal ? 1 : 0;
            layout.SelectionChanged += (_, _) => change(overlay => overlay.Horizontal = layout.SelectedIndex == 1);
            rows.Children.Add(Ui.SettingRow(Ui.T("Layout"), Ui.T("A column in the corner, or one line along the edge of the screen."), layout, first: true));

            rows.Children.Add(Ui.SettingRow(Ui.T("Size"), "", Slide(60, 250, now.Scale, "%", value => change(overlay => overlay.Scale = value))));

            var corner = new ComboBox { Width = 190 };
            foreach (var name in Corners)
                corner.Items.Add(Ui.T(name));
            corner.SelectedIndex = Math.Clamp(now.Corner, 0, 3);
            corner.SelectionChanged += (_, _) => change(overlay => overlay.Corner = Math.Max(0, corner.SelectedIndex));
            rows.Children.Add(Ui.SettingRow(Ui.T("Corner"), "", corner));
            rows.Children.Add(Ui.SettingRow(Ui.T("Distance from the side"), Ui.T("Slide it along the edge, to keep it clear of the game's own map or health bar."),
                                            Slide(0, 1500, now.OffsetX, "px", value => change(overlay => overlay.OffsetX = value))));
            rows.Children.Add(Ui.SettingRow(Ui.T("Distance from the top or bottom"), "",
                                            Slide(0, 1000, now.OffsetY, "px", value => change(overlay => overlay.OffsetY = value))));

            rows.Children.Add(Ui.SettingRow(Ui.T("Highlight colour"), Ui.T("The frames per second."), Swatches(Accents, now.Accent, hex => change(overlay => overlay.Accent = hex), Fill)));
            rows.Children.Add(Ui.SettingRow(Ui.T("Text colour"), "", Swatches(Texts, now.Text, hex => change(overlay => overlay.Text = hex), Fill)));
            rows.Children.Add(Ui.SettingRow(Ui.T("Background colour"), "", Swatches(Backgrounds, now.Background, hex => change(overlay => overlay.Background = hex), Fill)));
            rows.Children.Add(Ui.SettingRow(Ui.T("Background strength"), Ui.T("0 is no background at all: only the numbers."),
                                            Slide(0, 100, now.Opacity, "%", value => change(overlay => overlay.Opacity = value))));
            rows.Children.Add(Ui.SettingRow(Ui.T("Start again"), Ui.T("Puts the look and the order back as they came."), Ui.Button(Ui.T("Reset the look"), () =>
            {
                change(overlay => overlay.ResetLook());
                Fill();
            })));
        }
        Fill();
        card.SetBody(rows);
        return card;
    }

    private static StackPanel Slide(int from, int to, int value, string unit, Action<int> changed)
    {
        var label = Ui.Muted($"{value} {unit}", 12);
        label.Width = 64;
        label.VerticalAlignment = VerticalAlignment.Center;
        var slider = new Slider { Minimum = from, Maximum = to, Value = Math.Clamp(value, from, to), Width = 240, IsSnapToTickEnabled = true, TickFrequency = to - from > 300 ? 10 : 5, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) =>
        {
            label.Text = $"{(int)slider.Value} {unit}";
            changed((int)slider.Value);
        };
        return Ui.Row(10, slider, label);
    }

    /// <summary>A row of colours to pick from, and a box for any other colour as #RRGGBB.</summary>
    private static StackPanel Swatches(string[] choices, string current, Action<string> pick, Action refresh)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in choices)
        {
            var chosen = hex.Equals(current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), ToolTip = hex,
                BorderBrush = Ui.Paint(chosen ? "Primary" : "Line"), BorderThickness = new Thickness(chosen ? 3 : 1), VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                pick(hex);
                refresh();
            };
            row.Children.Add(swatch);
        }
        var custom = Ui.Field(current, 96, "#RRGGBB");
        custom.FlowDirection = FlowDirection.LeftToRight;
        void Take()
        {
            var typed = custom.Text.Trim();
            if (typed.Equals(current, StringComparison.OrdinalIgnoreCase))
                return;
            if (!OverlaySettings.IsColour(typed))
            {
                custom.Text = current;
                return;
            }
            pick(typed.ToUpperInvariant());
            refresh();
        }
        custom.LostFocus += (_, _) => Take();
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                Take();
        };
        row.Children.Add(custom);
        return row;
    }
}
