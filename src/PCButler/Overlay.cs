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
    private readonly Border panel;
    private Brush accent = Brushes.White, text = Brushes.White;
    private double scale = 1;
    private (int X, int Y) offset = (16, 16);
    private string look = "";

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
        Content = panel = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = lines };
        // A soft dark edge around the letters keeps them readable over a bright scene, however faint the panel is.
        lines.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.85 };
        SourceInitialized += (_, _) =>
        {
            // Transparent to the mouse, never activated, and not in Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x08000000 | 0x80000);
        };
    }

    private static Color Parse(string hex, string fallback) =>
        (Color)ColorConverter.ConvertFromString(OverlaySettings.IsColour(hex) ? hex : fallback);

    /// <summary>Take the look from the settings: colours, size, a column or one line, and how far from the corner.</summary>
    public new void Style(OverlaySettings settings)
    {
        var wanted = $"{settings.Accent}|{settings.Text}|{settings.Background}|{settings.Opacity}|{settings.Scale}|{settings.Horizontal}";
        offset = (Math.Max(0, settings.OffsetX), Math.Max(0, settings.OffsetY));
        if (wanted == look)
            return;
        look = wanted;
        var back = Parse(settings.Background, OverlaySettings.DefaultBackground);
        var highlight = Parse(settings.Accent, OverlaySettings.DefaultAccent);
        var strength = (byte)(Math.Clamp(settings.Opacity, 0, 100) * 255 / 100);
        accent = new SolidColorBrush(highlight);
        text = new SolidColorBrush(Parse(settings.Text, OverlaySettings.DefaultText));
        scale = Math.Clamp(settings.Scale, 60, 250) / 100.0;
        panel.Background = new SolidColorBrush(Color.FromArgb(strength, back.R, back.G, back.B));
        panel.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(strength / 4), 255, 255, 255));
        panel.Padding = new Thickness(14 * scale, 9 * scale, 14 * scale, 10 * scale);
        lines.Orientation = settings.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
        lines.Children.Clear(); // built again at the new size
    }

    /// <summary>Show these lines: a short label and its value, the value in the highlight colour when it is the one to watch.</summary>
    public void Set(IReadOnlyList<(string Label, string Value, bool Accent)> rows)
    {
        var inLine = lines.Orientation == Orientation.Horizontal;
        while (lines.Children.Count > rows.Count)
            lines.Children.RemoveAt(lines.Children.Count - 1);
        while (lines.Children.Count < rows.Count)
        {
            var label = new TextBlock
            {
                FontFamily = Ui.DisplayFont, FontSize = 11 * scale, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6,
                Margin = new Thickness(0, 0, 8 * scale, 0),
            };
            if (!inLine)
                label.Width = 42 * scale;
            var value = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 14 * scale, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Children = { label, value },
                Margin = inLine ? new Thickness(lines.Children.Count == 0 ? 0 : 18 * scale, 0, 0, 0) : new Thickness(0, scale, 0, scale),
            });
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = (StackPanel)lines.Children[index];
            var label = (TextBlock)row.Children[0];
            (label.Text, label.Foreground) = (rows[index].Label, text);
            var value = (TextBlock)row.Children[1];
            value.Text = rows[index].Value.TrimStart(); // the padding was for a fixed-width font
            value.Foreground = rows[index].Accent ? accent : text;
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
        // Never off the screen it belongs to, however far it was slid.
        var x = Math.Clamp(corner is 1 or 3 ? right - width - offset.X : left + offset.X, left, Math.Max(left, right - width));
        var y = Math.Clamp(corner is 2 or 3 ? bottom - height - offset.Y : top + offset.Y, top, Math.Max(top, bottom - height));
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
    private bool helperOffered; // the helper is offered once each run: a "no" is not asked again
    // Counting frames in this program itself: null = not tried yet, true = Windows allows this account to, false = it needs the helper.
    private bool? countsItself;
    private DateTime itselfSince;
    private bool itselfSawFrames;

    /// <summary>True when this program counts frames with no helper: Windows allows that to accounts in its "Performance Log Users" group.</summary>
    public bool CountsItself => countsItself == true;

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
        if (countsItself == true)
            FpsMeter.Stop();
        countsItself = null;
        FinishSession();
        window?.Close();
        window = null;
        Latest = null;
    }

    private static bool StillRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return error is System.ComponentModel.Win32Exception; // there, but not ours to look at
        }
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
        // First try to count frames here, with no helper: Windows lets some accounts do that. Only if it refuses is the helper needed.
        if (settings.Fps && countsItself is null)
        {
            countsItself = FpsMeter.Start(AppInfo.DataDir);
            (itselfSince, itselfSawFrames) = (now, false);
            Log.Info(countsItself == true ? "Counting frames without the helper" : "Counting frames needs the helper on this account");
        }
        var counts = settings.Fps ? FpsMeter.Latest() : null;
        itselfSawFrames |= counts is { Count: > 0 };
        if (countsItself == true && !itselfSawFrames && (now - itselfSince).TotalSeconds > 15)
        {
            // Windows let the count start but hands over nothing (the desktop alone shows frames all the time): the helper it is.
            FpsMeter.Stop();
            countsItself = false;
            Log.Info("No frames were reported without the helper; it is needed after all");
        }
        CountingFrames = counts is not null;
        var helper = AdminHelper.Running;
        var needsHelper = countsItself == false;
        if (settings.Fps && needsHelper && counts is null && (now - lastAsked).TotalSeconds > 10 && helper)
        {
            lastAsked = now;
            Task.Run(() => AdminHelper.Send("fps", ["on"], 10));
        }
        // A game has come up and nothing is counting its frames: start the helper, if the user asked for that.
        if (game && needsHelper && settings is { Fps: true, AutoHelper: true } && !helper && !helperOffered)
        {
            helperOffered = true;
            Log.Info($"A game is in front ({front!.Name}); starting the helper to count its frames");
            Task.Run(Sensors.StartHelper);
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
        else if (recorder is not null && ((now - lastInFront).TotalMinutes > 10 || !StillRunning(recordedPid)))
            FinishSession(); // the game was closed, or left alone for a long while; a look at another window does not end it

        if (watched is null)
        {
            window?.Hide();
            return;
        }
        var rows = new List<(string, string, bool)>();
        static string Gigabytes(long bytes) => (bytes / 1073741824.0).ToString("0.0");
        // Each reading the user ticked, in the order they put them; one that the PC cannot give is left out.
        foreach (var row in settings.OrderedRows().Where(settings.Shows))
        {
            (string, string, bool)? line = row switch
            {
                // Why there is no number, when there is none: nobody is counting, counting has just begun, or this program shows no frames.
                "fps" => ("FPS", fps?.ToString() ?? (needsHelper && !helper ? Ui.T("helper is off") : counts is null ? "…" : "—"), true),
                "frame_time" when fps is > 0 => ("ms", (1000.0 / fps.Value).ToString("0.0"), false),
                "cpu" => ("CPU", $"{reading.Cpu,3}%" + (settings.Temperatures && reading.CpuTemperature is { } cpu ? $"  {cpu}°C" : ""), false),
                "gpu" when reading.Gpu is not null || reading.GpuTemperature is not null =>
                    ("GPU", (reading.Gpu is { } use ? $"{use,3}%" : "  —") + (settings.Temperatures && reading.GpuTemperature is { } gpu ? $"  {gpu}°C" : ""), false),
                "vram" when sample?.Gpu?.MemoryUsed is { } videoUsed =>
                    ("VRAM", Gigabytes(videoUsed) + (sample.Gpu.MemoryTotal is { } videoTotal ? $" / {Gigabytes(videoTotal)} GB" : " GB"), false),
                "fan" when sample?.Gpu?.Fan is { } fan => ("FAN", $"{fan} RPM", false),
                "ram" => ("RAM", $"{reading.MemoryPercent,3}%" + (sample is null ? "" : $"  {Gigabytes(sample.MemoryUsed)} / {Gigabytes(sample.MemoryTotal)} GB"), false),
                "net" when network.Read() is { } speed => ("NET", $"↓{NetworkMeter.Megabits(speed.Down)}  ↑{NetworkMeter.Megabits(speed.Up)} Mb/s", false),
                "clock" => ("TIME", DateTime.Now.ToString("HH:mm"), false),
                _ => null,
            };
            if (line is { } shown)
                rows.Add(shown);
        }
        if (rows.Count == 0)
        {
            window?.Hide();
            return;
        }
        window ??= new OverlayWindow();
        window.Style(settings);
        window.Set(rows);
        if (!window.IsVisible)
            window.Show();
        window.UpdateLayout();
        window.Place(watched.Left, watched.Top, watched.Right, watched.Bottom, settings.Corner);
    }
}

/// <summary>
/// Keyboard shortcuts that work whatever program is in front. Each action can have one;
/// they are set on the Hotkeys tab of the Settings page. Shortcuts with the Win key go through
/// a keyboard hook (WinKeyHook), since Windows keeps most of them for itself; the others are
/// registered with Windows.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    /// <summary>(key in config.json, what the Settings page calls it, what it does).</summary>
    public static readonly (string Key, string Label, string Detail)[] Actions =
    [
        ("open", "Open PC Butler", "Brings the window up from the tray."),
        ("search", "Open the search box", "Finds any file or folder on your drives from whatever program is in front."),
        ("searchpage", "Open the Search page", ""),
        ("sort", "Sort now", "Moves the files that are due, as the tray menu's Sort now does."),
        ("boost", "Start or stop Boost", "Boost speeds up the program that is in front when you press it."),
        ("overlay", "Show or hide the performance overlay", "Switches the overlay of the Benchmark page on or off."),
        ("dropzone", "Show or hide the drop zone", ""),
        ("courier", "Open Courier", ""),
    ];

    /// <summary>Shortcuts an action has until the user changes them; cleared, they are saved as "" so the default does not come back.</summary>
    public static readonly Dictionary<string, string> Defaults = new() { ["search"] = "Win+F" };

    /// <summary>The saved shortcuts with the defaults filled in.</summary>
    public static Dictionary<string, string> Effective(Dictionary<string, string> saved)
    {
        var all = new Dictionary<string, string>(saved);
        foreach (var (action, keys) in Defaults)
            all.TryAdd(action, keys);
        return all;
    }

    /// <summary>Starts the message for a Win shortcut Windows would not give up: the Hotkeys tab then offers to take it from File Explorer.</summary>
    public const string HeldByWindows = "held-by-windows:";

    private const int WM_HOTKEY = 0x0312, FirstId = 0xB100;
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly HwndSource source;
    private readonly List<int> registered = [];
    private readonly Action<string> run;
    private readonly WinKeyHook hook;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public Hotkeys(Action<string> run)
    {
        this.run = run;
        hook = new WinKeyHook(index => run(Actions[index].Key));
        source = new HwndSource(new HwndSourceParameters("PC Butler hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    /// <summary>Register the shortcuts in the settings (defaults filled in). Returns, per action, why its shortcut could not be used.</summary>
    public Dictionary<string, string> Apply(Dictionary<string, string> saved)
    {
        var wanted = Effective(saved);
        foreach (var id in registered)
            UnregisterHotKey(source.Handle, id);
        registered.Clear();
        var errors = new Dictionary<string, string>();
        var withWin = new List<(System.Windows.Input.ModifierKeys Modifiers, int Key, int Index, string Keys)>();
        for (var index = 0; index < Actions.Length; index++)
        {
            if (!wanted.TryGetValue(Actions[index].Key, out var keys) || keys.Length == 0)
                continue;
            if (!Shortcut.TryParse(keys, out var modifiers, out var key))
                errors[Actions[index].Key] = Ui.T("“{0}” is not a shortcut PC Butler can use.", keys);
            else if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows))
                withWin.Add((modifiers, System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), index, keys));
            else if (RegisterHotKey(source.Handle, FirstId + index, (uint)modifiers | MOD_NOREPEAT, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)))
                registered.Add(FirstId + index);
            else
                errors[Actions[index].Key] = Ui.T("{0} is already used by another program. Choose another shortcut.", keys);
        }
        if (!hook.Set(withWin.Select(item => (item.Modifiers, item.Key, item.Index)).ToList()))
        {
            // No hook: Windows' own registration takes a Win shortcut only once File Explorer has let go of it.
            foreach (var (modifiers, key, index, keys) in withWin)
            {
                if (RegisterHotKey(source.Handle, FirstId + index, (uint)modifiers | MOD_NOREPEAT, (uint)key))
                    registered.Add(FirstId + index);
                else
                    errors[Actions[index].Key] = HeldByWindows + Ui.T("Windows keeps {0} for itself, and PC Butler could not take it over.", keys);
            }
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
        hook.Dispose();
        source.Dispose();
    }
}
