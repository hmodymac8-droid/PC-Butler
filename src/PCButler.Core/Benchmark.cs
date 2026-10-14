// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PCButler.Core;

/// <summary>The performance overlay, as set on the Benchmark page.</summary>
public sealed class OverlaySettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>Start the administrator helper by itself when a game is detected, so frames are counted without a visit to the page.</summary>
    [JsonPropertyName("auto_helper")] public bool AutoHelper { get; set; }
    /// <summary>Show it all the time, not only while a game is in front.</summary>
    [JsonPropertyName("always")] public bool Always { get; set; }
    /// <summary>0 top left, 1 top right, 2 bottom left, 3 bottom right.</summary>
    [JsonPropertyName("corner")] public int Corner { get; set; }
    [JsonPropertyName("fps")] public bool Fps { get; set; } = true;
    [JsonPropertyName("cpu")] public bool Cpu { get; set; } = true;
    [JsonPropertyName("gpu")] public bool Gpu { get; set; } = true;
    [JsonPropertyName("memory")] public bool Memory { get; set; } = true;
    [JsonPropertyName("temperatures")] public bool Temperatures { get; set; } = true;
    /// <summary>The graphics card's own memory in use.</summary>
    [JsonPropertyName("video_memory")] public bool VideoMemory { get; set; } = true;
    /// <summary>Milliseconds per frame, next to the frames per second.</summary>
    [JsonPropertyName("frame_time")] public bool FrameTime { get; set; }
    [JsonPropertyName("fan")] public bool Fan { get; set; }
    /// <summary>Download and upload speed of the whole PC.</summary>
    [JsonPropertyName("network")] public bool Network { get; set; }
    [JsonPropertyName("clock")] public bool Clock { get; set; }
    /// <summary>The readings in the order the user put them; any left out follow in the usual order.</summary>
    [JsonPropertyName("order")] public List<string> Order { get; set; } = [];
    /// <summary>All readings on one line instead of in a column.</summary>
    [JsonPropertyName("horizontal")] public bool Horizontal { get; set; }
    /// <summary>Size in percent of the usual one.</summary>
    [JsonPropertyName("scale")] public int Scale { get; set; } = 100;
    /// <summary>Pixels from the side and from the top or bottom of the screen, counted from the chosen corner.</summary>
    [JsonPropertyName("offset_x")] public int OffsetX { get; set; } = 16;
    [JsonPropertyName("offset_y")] public int OffsetY { get; set; } = 16;
    /// <summary>Colours as #RRGGBB: the frames per second, the other numbers, the panel behind them.</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = DefaultAccent;
    [JsonPropertyName("text")] public string Text { get; set; } = DefaultText;
    [JsonPropertyName("background")] public string Background { get; set; } = DefaultBackground;
    /// <summary>How solid the panel is, 0 (only the numbers) to 100.</summary>
    [JsonPropertyName("opacity")] public int Opacity { get; set; } = 35;

    // The program's own colours: its green, its soft white, its card. The panel is mostly see-through by default.
    public const string DefaultAccent = "#42D8A5", DefaultText = "#E8EBF0", DefaultBackground = "#101216";

    /// <summary>Every reading the overlay can show, in the order it comes in.</summary>
    public static readonly string[] Rows = ["fps", "frame_time", "cpu", "gpu", "vram", "fan", "ram", "net", "clock"];

    /// <summary>The readings in the user's order: what they arranged first, then whatever that list does not mention.</summary>
    public List<string> OrderedRows() => Order.Where(Rows.Contains).Distinct().Concat(Rows.Where(row => !Order.Contains(row))).ToList();

    public bool Shows(string row) => row switch
    {
        "fps" => Fps, "frame_time" => FrameTime, "cpu" => Cpu, "gpu" => Gpu, "vram" => VideoMemory, "fan" => Fan, "ram" => Memory, "net" => Network, "clock" => Clock,
        _ => false,
    };

    public void SetShown(string row, bool on)
    {
        switch (row)
        {
            case "fps": Fps = on; break;
            case "frame_time": FrameTime = on; break;
            case "cpu": Cpu = on; break;
            case "gpu": Gpu = on; break;
            case "vram": VideoMemory = on; break;
            case "fan": Fan = on; break;
            case "ram": Memory = on; break;
            case "net": Network = on; break;
            case "clock": Clock = on; break;
        }
    }

    /// <summary>True for a colour written as #RRGGBB.</summary>
    public static bool IsColour(string text) => text.Length == 7 && text[0] == '#' && text[1..].All(Uri.IsHexDigit);

    /// <summary>The look and the order as they came; what is shown, and where games are detected, stays.</summary>
    public void ResetLook()
    {
        var fresh = new OverlaySettings();
        (Order, Horizontal, Scale, Corner, OffsetX, OffsetY) = ([], fresh.Horizontal, fresh.Scale, fresh.Corner, fresh.OffsetX, fresh.OffsetY);
        (Accent, Text, Background, Opacity) = (fresh.Accent, fresh.Text, fresh.Background, fresh.Opacity);
    }

    /// <summary>Program file names (game.exe) always taken for a game, full screen or not.</summary>
    [JsonPropertyName("games")] public List<string> Games { get; set; } = [];
}

/// <summary>The window in front, and whether it fills its screen without a title bar.</summary>
public sealed record ForegroundApp(int Pid, string Name, string Title, bool FullScreen, int Left, int Top, int Right, int Bottom);

/// <summary>Telling a game from everything else, with nothing but what any program may ask Windows.</summary>
public static class GameDetector
{
    /// <summary>Programs that fill the screen without being games: browsers, players, the desktop itself.</summary>
    public static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "PCButler.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "iexplore.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe", "PotPlayerMini64.exe",
        "POWERPNT.EXE", "WINWORD.EXE", "EXCEL.EXE", "Photos.exe", "Microsoft.Photos.exe", "mstsc.exe", "LockApp.exe", "SearchHost.exe",
        "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "TextInputHost.exe", "ApplicationFrameHost.exe", "dwm.exe", "ScreenClippingHost.exe",
        "Zoom.exe", "Teams.exe", "ms-teams.exe", "obs64.exe", "Netflix.exe", "Spotify.exe", "Code.exe", "devenv.exe", "WindowsTerminal.exe",
        "steamwebhelper.exe", "EpicGamesLauncher.exe", "vmware.exe", "VirtualBoxVM.exe",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    /// <summary>The window in front now, with the bounds of the screen it is on; null when there is none.</summary>
    public static ForegroundApp? Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
            return null;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(window, out var rect) || !GetMonitorInfo(MonitorFromWindow(window, 2), ref info))
            return null;
        var screen = info.Monitor;
        const int Caption = 0x00C00000;
        var covers = rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        var bare = (GetWindowLong(window, -16) & Caption) != Caption;
        var title = new StringBuilder(200);
        GetWindowText(window, title, title.Capacity);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new ForegroundApp((int)pid, process.ProcessName + ".exe", title.ToString(), covers && bare, screen.Left, screen.Top, screen.Right, screen.Bottom);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A game is a program on the user's own list, or one that fills the screen without a
    /// title bar and is not a browser, a player or part of Windows.
    /// </summary>
    public static bool LooksLikeGame(ForegroundApp app, IEnumerable<string> known)
    {
        if (known.Any(name => name.Trim().Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
            return true;
        return app.FullScreen && app.Title.Length > 0 && !NeverGames.Contains(app.Name) && !Booster.ProtectedNames.Contains(app.Name);
    }
}

/// <summary>
/// Frames per second of other programs. Windows only tells an administrator when a program
/// shows a frame, so the counting is done by the helper: it listens to the graphics
/// system's "frame shown" events and writes each program's count to fps.json once a second.
/// Nothing is added to the game itself, so anti-cheat systems have nothing to object to.
/// </summary>
public static class FpsMeter
{
    private static FpsSession? session;

    public static string FileIn(string directory) => Path.Combine(directory, "fps.json");

    /// <summary>The helper's side: start counting. False when Windows refuses (not an administrator).</summary>
    public static bool Start(string directory)
    {
        try
        {
            session ??= new FpsSession(FileIn(directory));
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning($"Frame counting could not start: {error.Message}");
            return false;
        }
    }

    public static void Stop()
    {
        session?.Dispose();
        session = null;
    }

    /// <summary>The newest counts (process id -> frames a second), or null when nobody is counting.</summary>
    public static Dictionary<int, int>? Latest(string? directory = null)
    {
        try
        {
            var reading = JsonSerializer.Deserialize<FpsReading>(File.ReadAllText(FileIn(directory ?? AppInfo.DataDir)));
            return reading is not null && (DateTime.UtcNow - reading.Time).TotalSeconds < 4 ? reading.Fps : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public sealed class FpsReading
    {
        [JsonPropertyName("time")] public DateTime Time { get; set; }
        [JsonPropertyName("fps")] public Dictionary<int, int> Fps { get; set; } = [];
    }

    /// <summary>
    /// What to show for one program from two kinds of count: those of the program's own
    /// "show this frame" calls, and those of the graphics kernel, which also sees programs
    /// that draw another way. The program's own count wins when there is one.
    /// </summary>
    public static int Choose(int own, int kernel) => own > 0 ? own : kernel;

    private sealed class FpsSession : IDisposable
    {
        private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

        private readonly TraceEventSession trace;
        private readonly ConcurrentDictionary<int, int> own = new(), kernel = new();
        private readonly Timer timer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string file;

        public FpsSession(string file)
        {
            this.file = file;
            // One name, so a session a crashed helper left behind is taken over instead of piling up.
            trace = new TraceEventSession("PCButlerFps") { StopOnDispose = true };
            trace.EnableProvider(Dxgi, TraceEventLevel.Verbose);
            trace.EnableProvider(D3D9, TraceEventLevel.Verbose);
            trace.EnableProvider(DxgKrnl, TraceEventLevel.Informational, 0x8000000); // its "present" events only
            trace.Source.AllEvents += Count;
            new Thread(() =>
            {
                try
                {
                    trace.Source.Process();
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }) { IsBackground = true, Name = "PC Butler frame counter" }.Start();
            timer = new Timer(_ => Write(), null, 1000, 1000);
        }

        private void Count(TraceEvent data)
        {
            var (id, provider) = ((int)data.ID, data.ProviderGuid);
            if ((provider == Dxgi && id == 42) || (provider == D3D9 && id == 1))
                own.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
            else if (provider == DxgKrnl && id == 184)
                kernel.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
        }

        private void Write()
        {
            var seconds = Math.Max(0.2, clock.Elapsed.TotalSeconds);
            clock.Restart();
            var (mine, theirs) = (own.ToArray(), kernel.ToArray());
            own.Clear();
            kernel.Clear();
            var reading = new FpsReading { Time = DateTime.UtcNow };
            foreach (var pid in mine.Select(pair => pair.Key).Concat(theirs.Select(pair => pair.Key)).Distinct())
            {
                var fps = (int)Math.Round(Choose(mine.FirstOrDefault(pair => pair.Key == pid).Value, theirs.FirstOrDefault(pair => pair.Key == pid).Value) / seconds);
                if (fps > 0 && pid > 0)
                    reading.Fps[pid] = fps;
            }
            try
            {
                var temp = file + ".new";
                File.WriteAllText(temp, JsonSerializer.Serialize(reading));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            trace.Dispose();
            try
            {
                File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>How much the PC is sending and receiving, from Windows' own counters of its network adapters.</summary>
public sealed class NetworkMeter
{
    private long lastReceived = -1, lastSent;
    private long lastAt;

    /// <summary>Bytes a second down and up since the previous call; null the first time.</summary>
    public (long Down, long Up)? Read()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || adapter.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;
                var totals = adapter.GetIPStatistics();
                received += totals.BytesReceived;
                sent += totals.BytesSent;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return null;
        }
        var now = Environment.TickCount64;
        var seconds = (now - lastAt) / 1000.0;
        (long, long)? speed = lastReceived >= 0 && seconds > 0.2 && received >= lastReceived && sent >= lastSent
            ? ((long)((received - lastReceived) / seconds), (long)((sent - lastSent) / seconds)) : null;
        (lastReceived, lastSent, lastAt) = (received, sent, now);
        return speed;
    }

    /// <summary>A speed in the units people know from their connection: megabits a second.</summary>
    public static string Megabits(long bytesPerSecond)
    {
        var megabits = bytesPerSecond * 8 / 1_000_000.0;
        return megabits >= 100 ? $"{megabits:0}" : megabits >= 10 ? $"{megabits:0.0}" : $"{megabits:0.00}";
    }
}

/// <summary>One reading while a game runs. Null where the PC could not say.</summary>
public sealed record BenchmarkSample(int? Fps, int Cpu, int? Gpu, int MemoryPercent, int? CpuTemperature, int? GpuTemperature);

/// <summary>A game session in numbers, as the Benchmark page lists it.</summary>
public sealed class BenchmarkSession
{
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; }
    [JsonPropertyName("fps_average")] public int? FpsAverage { get; set; }
    /// <summary>The frame rate the slowest one second in a hundred stayed above: what stutter feels like.</summary>
    [JsonPropertyName("fps_low")] public int? FpsLow { get; set; }
    [JsonPropertyName("fps_high")] public int? FpsHigh { get; set; }
    [JsonPropertyName("cpu_average")] public int CpuAverage { get; set; }
    [JsonPropertyName("gpu_average")] public int? GpuAverage { get; set; }
    [JsonPropertyName("cpu_hottest")] public int? CpuHottest { get; set; }
    [JsonPropertyName("gpu_hottest")] public int? GpuHottest { get; set; }
}

/// <summary>Collects readings while a game is in front and turns them into a session when it ends.</summary>
public sealed class BenchmarkRecorder(string game)
{
    private readonly List<BenchmarkSample> samples = [];
    private readonly DateTime started = DateTime.Now;

    public string Game => game;
    public int Count => samples.Count;

    public void Add(BenchmarkSample sample) => samples.Add(sample);

    /// <summary>The session so far; null while there is too little to say anything (under ten readings).</summary>
    public BenchmarkSession? Finish(double secondsPerSample = 1)
    {
        if (samples.Count < 10)
            return null;
        var fps = samples.Where(sample => sample.Fps is > 0).Select(sample => sample.Fps!.Value).Order().ToList();
        var gpu = samples.Where(sample => sample.Gpu is not null).Select(sample => sample.Gpu!.Value).ToList();
        return new BenchmarkSession
        {
            Game = game, Started = started.ToString("yyyy-MM-dd HH:mm"), Seconds = (int)(samples.Count * secondsPerSample),
            FpsAverage = fps.Count > 0 ? (int)Math.Round(fps.Average()) : null,
            FpsLow = fps.Count > 0 ? fps[Math.Max(0, (int)Math.Ceiling(fps.Count * 0.01) - 1)] : null,
            FpsHigh = fps.Count > 0 ? fps[^1] : null,
            CpuAverage = (int)Math.Round(samples.Average(sample => sample.Cpu)),
            GpuAverage = gpu.Count > 0 ? (int)Math.Round(gpu.Average()) : null,
            CpuHottest = samples.Max(sample => sample.CpuTemperature),
            GpuHottest = samples.Max(sample => sample.GpuTemperature),
        };
    }
}

/// <summary>The sessions kept in benchmark.json, newest first.</summary>
public static class BenchmarkLog
{
    private const int Kept = 40;
    private static readonly object Gate = new();

    public static string LogFile => Path.Combine(AppInfo.DataDir, "benchmark.json");

    public static List<BenchmarkSession> All()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(LogFile) ? JsonSerializer.Deserialize<List<BenchmarkSession>>(File.ReadAllText(LogFile)) ?? [] : [];
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public static void Add(BenchmarkSession session)
    {
        var all = All();
        all.Insert(0, session);
        Write(all.Take(Kept).ToList());
    }

    public static void Clear() => Write([]);

    private static void Write(List<BenchmarkSession> sessions)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                File.WriteAllText(LogFile, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Could not save the benchmark log: {error.Message}");
            }
        }
    }
}
