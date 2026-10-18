// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The panel a click on the tray icon opens, like Windows' quick settings: what PC Butler
/// is doing, the processor and memory, a tile for each background feature (lit when on, a
/// click switches it), the update when there is one, and Open and Exit. It closes
/// when anything else is clicked. A double-click on the icon opens the main window instead.
/// </summary>
public sealed class TrayFlyout : Window
{
    private readonly TextBlock state = Ui.Text("", 11.5);
    private readonly Border statePill;
    private readonly TextBlock cpuValue = Ui.Text("—", 18, weight: FontWeights.SemiBold);
    private readonly TextBlock memoryValue = Ui.Text("—", 18, weight: FontWeights.SemiBold);
    private readonly Rectangle cpuBar = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 1.5, RadiusY = 1.5 };
    private readonly Rectangle memoryBar = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 1.5, RadiusY = 1.5 };
    private readonly UniformGrid tiles = new() { Columns = 3 };
    private readonly Border update;
    private readonly TextBlock updateText = Ui.Text("", 12.5, "Primary", weight: FontWeights.SemiBold);
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closing;

    /// <summary>When it last closed, so the click on the icon that closed it doesn't open it again at once.</summary>
    public static long ClosedAt { get; private set; }

    public TrayFlyout()
    {
        Ui.StyleWindow(this);
        Title = AppInfo.Name;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        // --- the name, and what the sorter is doing ---
        var icon = new Image { Source = Ui.AppIcon(), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        statePill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 4), Child = state, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(statePill.Dock(Dock.Right));
        head.Children.Add(icon.Dock(Dock.Left));
        head.Children.Add(new TextBlock
        {
            Text = AppInfo.Name, FontFamily = Ui.DisplayFont, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Ui.Paint("Text"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // --- the processor and the memory ---
        var readings = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        readings.ColumnDefinitions.Add(new ColumnDefinition());
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        readings.ColumnDefinitions.Add(new ColumnDefinition());
        readings.Children.Add(Reading(Ui.T("Processor"), cpuValue, cpuBar).At(0, 0));
        readings.Children.Add(Reading(Ui.T("Memory"), memoryValue, memoryBar).At(0, 2));

        // --- the update, when there is one ---
        update = new Border
        {
            Background = Ui.Paint("PrimaryTint"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
            Child = Ui.Row(10, Ui.Glyph("\uE896", 15, "Primary"), updateText),
        };
        update.MouseLeftButtonUp += (_, _) => Act(() => App.Current.InstallAvailableUpdate());

        // --- Resume yesterday, when there is something from before the restart to open again ---
        var resume = new Border
        {
            Background = Ui.Paint("Secondary"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
        };
        if (App.Current.ResumeAvailable is { } previous)
        {
            var words = new StackPanel { Children = { Ui.Text(Ui.T("Resume yesterday"), 12.5, "Primary", FontWeights.SemiBold), Ui.Muted(App.ResumeSummary(previous), 11.5) } };
            resume.Child = Ui.Row(10, Ui.Glyph("\uE81C", 15, "Primary"), words);
            resume.ToolTip = Ui.T("Open again the apps, folders and files that were open before Windows restarted.");
            resume.Visibility = Visibility.Visible;
            resume.MouseLeftButtonUp += (_, _) => Act(() => App.Current.ResumeYesterday());
        }

        // --- Open and Exit ---
        var open = Ui.Button(Ui.T($"Open {AppInfo.Name}"), () => Act(() => App.Current.ShowMainWindow()), accent: true);
        var exit = Ui.Button(Ui.T("Exit"), () => Act(App.Current.ExitProgram));
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.Children.Add(open.At(0, 0));
        bottom.Children.Add(exit.At(0, 2));

        Content = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4 },
            Child = new StackPanel { Children = { head, readings, tiles, update, resume, bottom } },
        };

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Dismiss();
        };
        Closing += (_, _) => closing = true;
        Closed += (_, _) => ClosedAt = Environment.TickCount64;
        refresh.Tick += (_, _) => Fill();
        refresh.Start();
        Loaded += (_, _) => Place();
        Fill();
    }

    private static Border Reading(string name, TextBlock value, Rectangle bar)
    {
        var track = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        track.Children.Add(new Rectangle { Height = 3, Fill = Ui.Paint("Raised"), RadiusX = 1.5, RadiusY = 1.5 });
        track.Children.Add(bar);
        return new Border
        {
            Background = Ui.Paint("Secondary"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 10),
            Child = new StackPanel { Children = { Ui.Muted(name, 11.5), value, track } },
        };
    }

    /// <summary>Do it, and get out of the way.</summary>
    private void Act(Action action)
    {
        Dismiss();
        action();
    }

    /// <summary>Close it, unless it is already closing (WPF throws on a second Close while a window closes).</summary>
    public void Dismiss()
    {
        if (closing)
            return;
        closing = true;
        refresh.Stop();
        Close();
    }

    /// <summary>Everything shown, from what the program is doing now.</summary>
    private void Fill()
    {
        var config = ConfigStore.Load();
        var (text, brush) = !config.AutoSortOn ? (Ui.T("Sorting off"), "Muted") : config.IsPaused ? (Ui.T("Paused"), "Amber") : (Ui.T("Sorting on"), "Primary");
        state.Text = text;
        state.Foreground = Ui.Paint(brush);
        statePill.Background = Ui.Paint(brush == "Primary" ? "PrimaryTint" : "Secondary");

        if (App.Current.Usage.Latest is { } sample)
        {
            cpuValue.Text = $"{sample.Cpu}%";
            memoryValue.Text = $"{sample.MemoryPercent}%";
            SetBar(cpuBar, sample.Cpu);
            SetBar(memoryBar, sample.MemoryPercent);
        }

        var updateInfo = App.Current.Background.Update;
        update.Visibility = updateInfo is null ? Visibility.Collapsed : Visibility.Visible;
        updateText.Text = updateInfo is null ? "" : Ui.T("Install version {0}", updateInfo.Version);

        // The tiles: lit when what they stand for is on.
        var courier = Courier.Node;
        var connected = Courier.Targets(courier).Count(target => target.Ready);
        tiles.Children.Clear();
        // One tile per background feature: a click switches it on or off and the panel stays open.
        foreach (var feature in App.Current.Features())
        {
            var name = feature.Key == "courier" && connected > 0 ? Ui.T("Courier · {0}", connected) : Ui.T(feature.Name);
            tiles.Children.Add(Tile(feature.Glyph, name, feature.On, true, () =>
            {
                // Read again: it may have changed since the panel last looked.
                if (App.Current.Features().FirstOrDefault(each => each.Key == feature.Key) is { } now)
                    App.Current.SetFeature(now.Key, !now.On);
            }, stayOpen: true));
        }
    }

    private static void SetBar(Rectangle bar, int percent)
    {
        bar.Width = Math.Max(0, Math.Min(100, percent)) / 100.0 * 140;
        bar.Fill = Ui.Paint(percent >= 90 ? "Red" : percent >= 70 ? "Amber" : "Primary");
    }

    /// <summary>One tile: its icon and name, lit when on. Most close the panel; a switch keeps it open to show the change.</summary>
    private Border Tile(string glyph, string name, bool on, bool enabled, Action action, bool stayOpen = false)
    {
        var label = Ui.Text(name, 11.5, on ? "Primary" : "Text");
        // The whole name, on two lines when it needs them.
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        var mark = Ui.Glyph(glyph, 18, on ? "Primary" : "Muted");
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Border
        {
            Background = Ui.Paint(on ? "PrimaryTint" : "Secondary"), CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 11, 6, 9),
            Margin = new Thickness(3), Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.45,
            BorderBrush = Ui.Paint(on ? "Primary" : "Secondary"), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { mark, label.Margin(0, 6, 0, 0) } }, ToolTip = name,
        };
        if (!enabled)
            return tile;
        tile.MouseEnter += (_, _) => tile.BorderBrush = Ui.Paint("ControlLine");
        tile.MouseLeave += (_, _) => tile.BorderBrush = Ui.Paint(on ? "Primary" : "Secondary");
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (stayOpen)
            {
                action();
                Fill();
            }
            else
                Act(action);
        };
        return tile;
    }

    /// <summary>Above the taskbar, by the mouse (which is on the tray icon), kept inside the screen.</summary>
    private void Place()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = cursor.X / dpi.DpiScaleX;
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(x - ActualWidth / 2, area.Left, area.Right - ActualWidth);
        // The taskbar is usually at the bottom; when it is at the top, open below it.
        var y = cursor.Y / dpi.DpiScaleY;
        Top = y > area.Top + area.Height / 2 ? area.Bottom - ActualHeight : area.Top;
        Activate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}
