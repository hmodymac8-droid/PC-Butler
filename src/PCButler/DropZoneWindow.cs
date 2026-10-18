// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The drop zone: a little butler who stays on top of other windows. Drag files or folders
/// onto him and choose what PC Butler does with them: sort, vault, shred or recycle. He
/// reacts to the PC: he naps when nobody has used it for a while, sweats when it runs hot,
/// raises his arms to catch what is dragged over him and smiles when he has it. He can be
/// dragged anywhere and hidden from his right-click menu or the tray menu.
/// </summary>
public sealed class DropZoneWindow : Window
{
    /// <summary>Minutes without a key or mouse press before he takes a nap.</summary>
    private const int NapMinutes = 3;
    /// <summary>Degrees (processor or graphics card) at which he starts to sweat.</summary>
    private const int HotDegrees = 80;
    /// <summary>Without a temperature to go by, a processor this busy for a while makes him sweat too.</summary>
    private const int BusyPercent = 85;

    private readonly Butler butler = new();
    private readonly Border bubble;
    private readonly TextBlock label;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool dragging;
    private long pleasedUntil;
    private int busyTicks;

    public DropZoneWindow()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Drop zone");
        Width = 124;
        Height = 164;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Not quite transparent: fully see-through pixels would let drops and clicks fall through to the desktop.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        var area = SystemParameters.WorkArea;
        Left = Tr.IsArabic ? area.Left + 16 : area.Right - Width - 16;
        Top = area.Bottom - Height - 12;

        label = Ui.Text(Ui.T("Drop here"), 11.5, "Muted", FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        bubble = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1.2),
            Padding = new Thickness(9, 3, 9, 4), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 120, Opacity = 0.95, Child = label,
        };
        var figure = new Viewbox { Width = 100, Height = 120, Child = butler, HorizontalAlignment = HorizontalAlignment.Center };
        Content = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { figure, bubble.Margin(0, 2, 0, 0) } };
        ToolTip = Ui.T("Drag files or folders onto the butler. Drag him to move him; right-click to hide him.");

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        MouseDoubleClick += (_, _) => App.Current.ShowMainWindow();
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item(Ui.T($"Open {AppInfo.Name}"), () => App.Current.ShowMainWindow()));
        menu.Items.Add(Ui.Item(Ui.T("Hide the drop zone"), () => App.Current.SetDropZone(false)));
        ContextMenu = menu;

        DragEnter += (_, e) => Highlight(e, true);
        DragOver += (_, e) => Highlight(e, true);
        DragLeave += (_, e) =>
        {
            // Passing from the butler to his speech bubble raises a leave too: only leaving the window counts.
            var at = e.GetPosition(this);
            if (at.X <= 0 || at.Y <= 0 || at.X >= ActualWidth || at.Y >= ActualHeight)
                Highlight(e, false);
        };
        Drop += (_, e) =>
        {
            Highlight(e, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
            {
                Please(Ui.T("Got it!"));
                Dispatcher.BeginInvoke(() => Offer(dropped)); // after the drag has finished, so Explorer isn't kept waiting
            }
        };

        watch.Tick += (_, _) => React();
        Loaded += (_, _) =>
        {
            React();
            watch.Start();
        };
        Closed += (_, _) => watch.Stop();
    }

    /// <summary>Look at the PC and let the butler show how it is doing (unless he is busy catching or pleased).</summary>
    private void React()
    {
        if (dragging || Environment.TickCount64 < pleasedUntil)
            return;
        var sample = App.Current.Usage.Latest;
        var degrees = Math.Max(Sensors.Latest()?.CpuTemperature ?? Sensors.MonitorApp()?.Cpu ?? 0, sample?.Gpu?.Temperature ?? 0);
        busyTicks = sample is { Cpu: >= BusyPercent } ? busyTicks + 1 : 0;
        var hot = degrees >= HotDegrees || (degrees <= 0 && busyTicks >= 5);
        var napping = !hot && IdleFor() >= TimeSpan.FromMinutes(NapMinutes);
        butler.Mood = hot ? ButlerMood.Hot : napping ? ButlerMood.Napping : ButlerMood.Ready;
        label.Text = hot ? degrees > 0 ? Ui.T("Phew, {0}°C!", (int)Math.Round(degrees)) : Ui.T("Phew, busy!")
            : napping ? Ui.T("Zzz…") : Ui.T("Drop here");
        label.Foreground = Ui.Paint(hot ? "Amber" : "Muted");
        butler.ToolTip = butler.Mood switch
        {
            ButlerMood.Hot => degrees > 0 ? Ui.T("The PC is running hot ({0}°C).", (int)Math.Round(degrees)) : Ui.T("The processor has been working flat out."),
            ButlerMood.Napping => Ui.T("Napping: nobody has used the PC for a few minutes."),
            _ => null,
        };
    }

    /// <summary>How long since the last key or mouse press, anywhere in Windows.</summary>
    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time) : TimeSpan.Zero;
    }

    /// <summary>A happy face and a hop for a moment, with something to say.</summary>
    private void Please(string text)
    {
        pleasedUntil = Environment.TickCount64 + 2200;
        butler.Mood = ButlerMood.Pleased;
        label.Text = text;
        label.Foreground = Ui.Paint("Primary");
        var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2300) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            React();
        };
        back.Start();
    }

    private void Highlight(DragEventArgs e, bool over)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        var catching = over && files;
        if (catching == dragging)
            return;
        dragging = catching;
        bubble.BorderBrush = Ui.Paint(catching ? "Primary" : "Line");
        if (catching)
        {
            butler.Mood = ButlerMood.Catching;
            label.Text = Ui.T("Let go");
            label.Foreground = Ui.Paint("Primary");
        }
        else
            React();
    }

    /// <summary>What to do with what was dropped: a menu right by the butler.</summary>
    private void Offer(string[] dropped)
    {
        var config = ConfigStore.Load();
        var root = config.WatchedFolders().FirstOrDefault() ?? "";
        var moves = Sorter.PlanDropped(config, dropped, root);
        var files = dropped.Where(File.Exists).ToList();
        var menu = new ContextMenu { PlacementTarget = bubble, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var title = new MenuItem
        {
            Header = dropped.Length == 1 ? Path.GetFileName(dropped[0].TrimEnd('\\')) : Ui.T("{0} items", dropped.Length), IsEnabled = false,
        };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        var sort = Ui.Item(Ui.T("Sort ({0} file(s))", moves.Count), () =>
        {
            var (count, errors) = Sorter.MoveFiles(moves);
            App.Current.Workspace?.DataChanged();
            Flash(Ui.T("Sorted {0}", count), errors);
        });
        sort.IsEnabled = moves.Count > 0;
        menu.Items.Add(sort);
        var vault = Ui.Item(Ui.T("Lock in the Vault ({0} file(s))", files.Count), () =>
        {
            VaultInbox.Add(files);
            App.Current.ShowMainWindow();
        });
        vault.IsEnabled = files.Count > 0;
        menu.Items.Add(vault);
        menu.Items.Add(Ui.Item(Ui.T("Send to another PC (Courier)"), () =>
        {
            App.Current.ShowMainWindow();
            App.Current.Workspace?.SendWithPortal(dropped);
        }));
        menu.Items.Add(Ui.Item(Ui.T("Shred…"), () =>
        {
            App.Current.ShowMainWindow();
            App.Current.Workspace?.ShredLater(dropped);
        }));
        var recycle = Ui.Item(Ui.T("Move to the Recycle Bin ({0} file(s))", files.Count), () =>
        {
            var (count, errors) = Sorter.DeleteFiles(files);
            App.Current.Workspace?.DataChanged();
            Flash(Ui.T("Recycled {0}", count), errors);
        });
        recycle.IsEnabled = files.Count > 0;
        menu.Items.Add(recycle);
        menu.IsOpen = true;
    }

    /// <summary>Say what happened by the butler for a moment; problems get a proper message.</summary>
    private void Flash(string text, List<string> errors)
    {
        if (errors.Count > 0)
        {
            Ui.ReportResult(text, errors);
            return;
        }
        Please(text);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
