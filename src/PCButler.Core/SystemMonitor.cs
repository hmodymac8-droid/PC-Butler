// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCButler.Core;

public sealed record DriveUsage(string Name, long Total, long Free)
{
    public long Used => Total - Free;
}

/// <summary>The graphics card: usage and temperature are null when Windows won't tell without administrator rights.</summary>
public sealed record GpuStatus(string Name, int? Usage, int? Temperature, int? Fan = null, long? MemoryUsed = null, long? MemoryTotal = null);

/// <summary>One reading of how busy the PC is.</summary>
public sealed record UsageSample(int Cpu, long MemoryTotal, long MemoryUsed, GpuStatus? Gpu)
{
    public int MemoryPercent => MemoryTotal > 0 ? (int)(MemoryUsed * 100 / MemoryTotal) : 0;
}

/// <summary>
/// Watches processor, memory and graphics-card use on a background thread, for the Home
/// page and the tray icon. Everything here works without administrator rights, which is
/// why processor temperature and fan speeds are not offered: Windows only gives those
/// to a driver running as administrator.
/// </summary>
public sealed class UsageMonitor
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private readonly AutoResetEvent wake = new(false);
    private volatile bool stopping;
    private long lastIdle, lastTotal;
    private Dictionary<string, CounterSample> lastGpu = [];
    private int highCpu, highMemory;
    private DateTime lastAlert = DateTime.MinValue;

    /// <summary>The newest reading (null until the first one, a couple of seconds after Start).</summary>
    public UsageSample? Latest { get; private set; }
    /// <summary>Read the graphics card too. Costs a little, so the Home page switches it on only while it is shown.</summary>
    public volatile bool WantGpu;
    public event Action<UsageSample>? Updated;
    /// <summary>The processor or the memory has been nearly full for a while (message, arguments).</summary>
    public event Action<string, object[]>? Alert;

    public const int IntervalSeconds = 2;
    private const int CpuAlertPercent = 90, MemoryAlertPercent = 92;
    /// <summary>How long it must stay that high before warning: two minutes for the processor, one for memory.</summary>
    private const int CpuAlertSamples = 120 / IntervalSeconds, MemoryAlertSamples = 60 / IntervalSeconds;
    private const int MinutesBetweenAlerts = 30;

    public void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "PC Butler usage monitor" }.Start();
    }

    public void Stop()
    {
        stopping = true;
        wake.Set();
    }

    private void Loop()
    {
        ReadCpu(); // the first reading only sets the baseline
        while (!stopping)
        {
            wake.WaitOne(TimeSpan.FromSeconds(IntervalSeconds));
            if (stopping)
                break;
            try
            {
                var (total, available) = Booster.MemoryStatus();
                var sample = new UsageSample(ReadCpu(), total, total - available, WantGpu ? ReadGpu() : null);
                Latest = sample;
                Updated?.Invoke(sample);
                CheckAlerts(sample);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Log.Warning($"Usage reading failed: {error.Message}");
            }
        }
    }

    /// <summary>Processor use since the previous call, 0-100.</summary>
    private int ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;
        var total = kernel + user; // kernel time includes idle time
        long idleDelta = idle - lastIdle, totalDelta = total - lastTotal;
        (lastIdle, lastTotal) = (idle, total);
        return totalDelta <= 0 ? 0 : (int)Math.Clamp(100 - idleDelta * 100 / totalDelta, 0, 100);
    }

    private void CheckAlerts(UsageSample sample)
    {
        highCpu = sample.Cpu >= CpuAlertPercent ? highCpu + 1 : 0;
        highMemory = sample.MemoryPercent >= MemoryAlertPercent ? highMemory + 1 : 0;
        if ((DateTime.Now - lastAlert).TotalMinutes < MinutesBetweenAlerts)
            return;
        if (highMemory >= MemoryAlertSamples)
        {
            lastAlert = DateTime.Now;
            Alert?.Invoke("Memory is {0}% full. Closing programs you don't need will speed things up.", [sample.MemoryPercent]);
        }
        else if (highCpu >= CpuAlertSamples)
        {
            lastAlert = DateTime.Now;
            Alert?.Invoke("The processor has been at {0}% for two minutes. The Booster page shows what is using it.", [sample.Cpu]);
        }
    }

    // --- graphics card ---

    private static string? gpuName;

    /// <summary>The name of the first graphics adapter Windows lists.</summary>
    public static string GpuName()
    {
        if (gpuName is not null)
            return gpuName;
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var found = new List<string>();
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
            {
                if (!name.All(char.IsDigit))
                    continue;
                using var adapter = adapters!.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description && description.Length > 0)
                    found.Add(description);
            }
            // Remote-desktop and fallback adapters are listed too; the real card is the one that isn't one of those.
            string[] notReal = ["virtual", "remote", "basic display", "mirror", "indirect"];
            return gpuName = found.FirstOrDefault(description => !notReal.Any(word =>
                       description.Contains(word, StringComparison.OrdinalIgnoreCase))) ?? found.FirstOrDefault() ?? "";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return gpuName = "";
    }

    private static long? videoMemoryTotal;

    /// <summary>The graphics card's own memory in bytes: how much is in use (as Task Manager's "dedicated GPU memory") and how much there is.</summary>
    public static (long? Used, long? Total) VideoMemory()
    {
        long? used = null;
        try
        {
            var data = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            if (data.Contains("Dedicated Usage"))
            {
                // One instance per adapter; the card in use is the one holding the most.
                foreach (InstanceData instance in data["Dedicated Usage"]!.Values)
                    used = Math.Max(used ?? 0, instance.RawValue);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
        if (videoMemoryTotal is null)
        {
            long most = 0;
            try
            {
                using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                foreach (var name in adapters?.GetSubKeyNames() ?? [])
                {
                    if (!name.All(char.IsDigit))
                        continue;
                    using var adapter = adapters!.OpenSubKey(name);
                    if (adapter?.GetValue("HardwareInformation.qwMemorySize") is long size)
                        most = Math.Max(most, size);
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
            videoMemoryTotal = most;
        }
        return (used, videoMemoryTotal > 0 ? videoMemoryTotal : null);
    }

    private GpuStatus? ReadGpu()
    {
        if (ReadGpuItself() is not { } status)
            return null;
        var (used, total) = VideoMemory();
        return status with { MemoryUsed = used, MemoryTotal = total };
    }

    private GpuStatus? ReadGpuItself()
    {
        // NVIDIA's own tool reports both use and temperature, with no special rights.
        var nvidia = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (File.Exists(nvidia))
        {
            try
            {
                var start = new ProcessStartInfo(nvidia, "--query-gpu=utilization.gpu,temperature.gpu,name --format=csv,noheader,nounits")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using var process = Process.Start(start)!;
                var line = process.StandardOutput.ReadLine() ?? "";
                process.WaitForExit(2000);
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length >= 3 && int.TryParse(parts[0], out var use) && int.TryParse(parts[1], out var temperature))
                    return new GpuStatus(parts[2], use, temperature);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
            }
        }
        // Any other card: Windows' own counters give the use, and its graphics kernel the temperature (as Task Manager shows them).
        int? usage = null;
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            if (data.Contains("Utilization Percentage"))
            {
                var current = new Dictionary<string, CounterSample>();
                double sum = 0;
                foreach (InstanceData instance in data["Utilization Percentage"]!.Values)
                {
                    if (!instance.InstanceName.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        continue;
                    current[instance.InstanceName] = instance.Sample;
                    if (lastGpu.TryGetValue(instance.InstanceName, out var before))
                        sum += CounterSample.Calculate(before, instance.Sample);
                }
                if (lastGpu.Count > 0)
                    usage = (int)Math.Clamp(Math.Round(sum), 0, 100);
                lastGpu = current;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or UnauthorizedAccessException)
        {
        }
        var name = GpuName();
        var (degrees, fan) = GpuSensors.Read();
        return name.Length == 0 && usage is null && degrees is null ? null : new GpuStatus(name, usage, degrees, fan);
    }

    // --- things that don't need watching ---

    /// <summary>When Windows last started.</summary>
    public static DateTime LastRestart => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static List<DriveUsage> Drives()
    {
        var drives = new List<DriveUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive is { DriveType: DriveType.Fixed, IsReady: true })
                    drives.Add(new DriveUsage(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return drives;
    }
}

/// <summary>One thing that cost points in the neatness score, as text for the user.</summary>
public sealed record NeatnessNote(string Text, object[] Arguments, int Lost);

/// <summary>
/// A 0-100 score for how tidy the PC is: files waiting to be sorted, junk that can be
/// cleaned, a crowded desktop, a nearly full disk and many startup apps each cost points.
/// Clearing them raises the score.
/// </summary>
public static class Neatness
{
    private static string FormatSize(long size) =>
        size >= 1L << 30 ? $"{size / (double)(1L << 30):0.0} GB" : $"{size / (double)(1L << 20):0} MB";

    public static (int Score, List<NeatnessNote> Notes) Compute(Config config, CancellationToken cancel = default)
    {
        var notes = new List<NeatnessNote>();
        void Lose(int points, string text, params object[] arguments)
        {
            if (points > 0)
                notes.Add(new NeatnessNote(text, arguments, points));
        }

        // Up to 25: files old enough to sort that are still waiting.
        var waiting = Sorter.PlanMoves(config).Count;
        Lose(Math.Min(25, (waiting + 1) / 2), "{0} file(s) are waiting to be sorted", waiting);

        // Up to 25: junk the Cleaner would remove from your temporary files (1 point per 200 MB).
        long junk = 0;
        try
        {
            junk = Cleaner.Scan(["user_temp"], cancel).Sum(found => found.Size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        Lose((int)Math.Min(25, junk / (200L << 20)), "{0} of temporary files can be cleaned", FormatSize(junk));

        // Up to 15: more than ten loose files on the desktop.
        var desktop = 0;
        try
        {
            var folder = ConfigStore.ShellFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            desktop = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Count(file => !Sorter.IsSkipped(file)) : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        Lose(Math.Min(15, Math.Max(0, desktop - 10) / 3), "{0} files are loose on the desktop", desktop);

        // Up to 20: less than a fifth of the Windows drive is free.
        var system = UsageMonitor.Drives().FirstOrDefault(drive =>
            Environment.SystemDirectory.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase));
        if (system is { Total: > 0 })
        {
            var free = (int)(system.Free * 100 / system.Total);
            Lose(Math.Min(20, Math.Max(0, 20 - free)), "Only {0}% of drive {1} is free", free, system.Name);
        }

        // Up to 15: more than eight apps start with Windows.
        var startup = Booster.StartupItems().Count(item => item.Enabled);
        Lose(Math.Min(15, Math.Max(0, startup - 8) * 3 / 2), "{0} apps start with Windows", startup);

        return (Math.Max(0, 100 - notes.Sum(note => note.Lost)), notes.OrderByDescending(note => note.Lost).ToList());
    }
}
