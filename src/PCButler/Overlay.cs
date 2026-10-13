// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The performance overlay: a small panel in a corner of the screen with frames per second,
/// processor, graphics card, memory and temperatures. Clicks pass through it to the game.
/// It is an ordinary window kept on top, so it shows over windowed and borderless games;
/// a game in exclusive full screen draws over everything, this included.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly StackPanel lines = new();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FlowDirection = FlowDirection.LeftToRight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0D, 0x10)), CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x3D, 0xD6, 0x8C)), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 9), Child = lines,
        };
        SourceInitialized += (_, _) =>
        {
            // Transparent to the mouse, never activated, and not in Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x08000000 | 0x80000);
        };
    }

    /// <summary>Show these lines: a short label and its value, the value in the accent colour when it is the one to watch.</summary>
    public void Set(IReadOnlyList<(string Label, string Value, bool Accent)> rows)
    {
        while (lines.Children.Count > rows.Count)
            lines.Children.RemoveAt(lines.Children.Count - 1);
        while (lines.Children.Count < rows.Count)
        {
            var label = new TextBlock { Width = 44, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, FontWeight = FontWeights.SemiBold };
            lines.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1), Children = { label, value } });
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = (StackPanel)lines.Children[index];
            ((TextBlock)row.Children[0]).Text = rows[index].Label;
            var value = (TextBlock)row.Children[1];
            value.Text = rows[index].Value;
            value.Foreground = rows[index].Accent ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)) : Brushes.White;
        }
    }

    /// <summary>Put the panel in a corner of a screen given in pixels, and back on top of whatever has come up since.</summary>
    public void Place(int left, int top, int right, int bottom, int corner)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var scale = VisualTreeHelper.GetDpi(this);
        var (width, height) = ((int)(ActualWidth * scale.DpiScaleX), (int)(ActualHeight * scale.DpiScaleY));
        const int Gap = 16;
        var x = corner is 1 or 3 ? right - width - Gap : left + Gap;
        var y = corner is 2 or 3 ? bottom - height - Gap : top + Gap;
        SetWindowPos(handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
    }
}

/// <summary>
/// Runs the overlay: once a second it looks at the program in front, decides whether it is
/// a game, shows or hides the panel, and keeps the numbers for the Benchmark page's list
/// of sessions.
/// </summary>
public sealed class OverlayController
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private OverlaySettings settings = new();
    private List<string> boostApps = [];
    private OverlayWindow? window;
    private BenchmarkRecorder? recorder;
    private int recordedPid;
    private DateTime lastInFront, lastAsked;
    private readonly NetworkMeter network = new();

    public OverlayController() => timer.Tick += (_, _) => Tick();

    /// <summary>The newest reading, for the Benchmark page; null while the overlay is off.</summary>
    public BenchmarkSample? Latest { get; private set; }
    /// <summary>The game being measured now, "" when none is.</summary>
    public string Game => recorder?.Game ?? "";
    /// <summary>True while the helper is counting frames.</summary>
    public bool CountingFrames { get; private set; }
    /// <summary>Goes up each time a game session is added to the log.</summary>
    public int SessionsSaved { get; private set; }

    public void Apply(Config config)
    {
        settings = config.Overlay;
        boostApps = config.Boost.AutoApps;
        if (settings.Enabled)
        {
            timer.Start();
            Tick();
        }
        else
            Stop();
    }

    public void Stop()
    {
        timer.Stop();
        FinishSession();
        window?.Close();
        window = null;
        Latest = null;
    }

    private void FinishSession()
    {
        if (recorder?.Finish() is { } session)
        {
            BenchmarkLog.Add(session);
            SessionsSaved++;
        }
        recorder = null;
    }

    private void Tick()
    {
        var front = GameDetector.Foreground();
        var game = front is not null && GameDetector.LooksLikeGame(front, settings.Games.Concat(boostApps));
        var now = DateTime.UtcNow;

        // Frames per second come from the helper; ask it to count when it is there and not counting yet.
        var counts = settings.Fps ? FpsMeter.Latest() : null;
        CountingFrames = counts is not null;
        if (settings.Fps && counts is null && (now - lastAsked).TotalSeconds > 10 && AdminHelper.Running)
        {
            lastAsked = now;
            Task.Run(() => AdminHelper.Send("fps", ["on"], 10));
        }

        var usage = App.Current.Usage;
        usage.WantGpu = true;
        var sample = usage.Latest;
        var watched = game || settings.Always ? front : null;
        int? fps = watched is not null && counts is not null && counts.TryGetValue(watched.Pid, out var frames) ? frames : null;
        var cpuTemperature = Sensors.Latest()?.CpuTemperature ?? Sensors.MonitorApp()?.Cpu;
        var reading = new BenchmarkSample(fps, sample?.Cpu ?? 0, sample?.Gpu?.Usage, sample?.MemoryPercent ?? 0,
                                          cpuTemperature is { } degrees ? (int)Math.Round(degrees) : null, sample?.Gpu?.Temperature);
        Latest = reading;

        // The session: one per game, ended when the game has been out of sight for a while.
        if (game)
        {
            if (recorder is not null && recordedPid != front!.Pid)
                FinishSession();
            recorder ??= new BenchmarkRecorder(front!.Name);
            recordedPid = front!.Pid;
            lastInFront = now;
            recorder.Add(reading);
        }
        else if (recorder is not null && (now - lastInFront).TotalSeconds > 20)
            FinishSession();

        if (watched is null)
        {
            window?.Hide();
            return;
        }
        var rows = new List<(string, string, bool)>();
        static string Gigabytes(long bytes) => (bytes / 1073741824.0).ToString("0.0");
        if (settings.Fps)
            rows.Add(("FPS", fps?.ToString() ?? "—", true));
        if (settings.FrameTime && fps is > 0)
            rows.Add(("ms", (1000.0 / fps.Value).ToString("0.0"), false));
        if (settings.Cpu)
            rows.Add(("CPU", $"{reading.Cpu,3}%" + (settings.Temperatures && reading.CpuTemperature is { } cpu ? $"  {cpu}°C" : ""), false));
        if (settings.Gpu && (reading.Gpu is not null || reading.GpuTemperature is not null))
            rows.Add(("GPU", (reading.Gpu is { } use ? $"{use,3}%" : "  —") + (settings.Temperatures && reading.GpuTemperature is { } gpu ? $"  {gpu}°C" : ""), false));
        if (settings.VideoMemory && sample?.Gpu?.MemoryUsed is { } videoUsed)
            rows.Add(("VRAM", Gigabytes(videoUsed) + (sample.Gpu.MemoryTotal is { } videoTotal ? $" / {Gigabytes(videoTotal)} GB" : " GB"), false));
        if (settings.Fan && sample?.Gpu?.Fan is { } fan)
            rows.Add(("FAN", $"{fan} RPM", false));
        if (settings.Memory)
            rows.Add(("RAM", $"{reading.MemoryPercent,3}%" + (sample is null ? "" : $"  {Gigabytes(sample.MemoryUsed)} / {Gigabytes(sample.MemoryTotal)} GB"), false));
        if (settings.Network && network.Read() is { } speed)
            rows.Add(("NET", $"↓{NetworkMeter.Megabits(speed.Down)}  ↑{NetworkMeter.Megabits(speed.Up)} Mb/s", false));
        if (settings.Clock)
            rows.Add(("TIME", DateTime.Now.ToString("HH:mm"), false));
        if (rows.Count == 0)
        {
            window?.Hide();
            return;
        }
        window ??= new OverlayWindow();
        window.Set(rows);
        if (!window.IsVisible)
            window.Show();
        window.UpdateLayout();
        window.Place(watched.Left, watched.Top, watched.Right, watched.Bottom, settings.Corner);
    }
}

/// <summary>
/// Keyboard shortcuts that work whatever program is in front. Each action can have one;
/// they are set on the Hotkeys tab of the Settings page.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    /// <summary>(key in config.json, what the Settings page calls it, what it does).</summary>
    public static readonly (string Key, string Label, string Detail)[] Actions =
    [
        ("open", "Open PC Butler", "Brings the window up from the tray."),
        ("sort", "Sort now", "Moves the files that are due, as the tray menu's Sort now does."),
        ("boost", "Start or stop Boost", "Boost speeds up the program that is in front when you press it."),
        ("overlay", "Show or hide the performance overlay", "Switches the overlay of the Benchmark page on or off."),
        ("dropzone", "Show or hide the drop zone", ""),
        ("courier", "Open Courier", ""),
    ];

    private const int WM_HOTKEY = 0x0312, FirstId = 0xB100;
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly HwndSource source;
    private readonly List<int> registered = [];
    private readonly Action<string> run;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public Hotkeys(Action<string> run)
    {
        this.run = run;
        source = new HwndSource(new HwndSourceParameters("PC Butler hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    /// <summary>Register the shortcuts in the settings. Returns, per action, why its shortcut could not be used.</summary>
    public Dictionary<string, string> Apply(Dictionary<string, string> wanted)
    {
        foreach (var id in registered)
            UnregisterHotKey(source.Handle, id);
        registered.Clear();
        var errors = new Dictionary<string, string>();
        for (var index = 0; index < Actions.Length; index++)
        {
            if (!wanted.TryGetValue(Actions[index].Key, out var keys) || keys.Length == 0)
                continue;
            if (!Shortcut.TryParse(keys, out var modifiers, out var key))
                errors[Actions[index].Key] = Ui.T("“{0}” is not a shortcut PC Butler can use.", keys);
            else if (RegisterHotKey(source.Handle, FirstId + index, (uint)modifiers | MOD_NOREPEAT, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)))
                registered.Add(FirstId + index);
            else
                errors[Actions[index].Key] = Ui.T("{0} is already used by another program. Choose another shortcut.", keys);
        }
        return errors;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var index = wParam.ToInt32() - FirstId;
        if (message == WM_HOTKEY && index >= 0 && index < Actions.Length)
        {
            run(Actions[index].Key);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in registered)
            UnregisterHotKey(source.Handle, id);
        source.Dispose();
    }
}
