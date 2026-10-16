// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PCButler.Core;

/// <summary>Recording what each app uses, for the report on the Benchmark page.</summary>
public sealed class UsageSettings
{
    /// <summary>Record how much each app uses of the processor, graphics card, memory, disk and internet.</summary>
    [JsonPropertyName("record")] public bool Record { get; set; } = true;
}

/// <summary>What one app used over some time: one day, or a whole report's period.</summary>
public sealed class AppUse
{
    /// <summary>How long it ran, in seconds.</summary>
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
    /// <summary>Processor time, in seconds of one core.</summary>
    [JsonPropertyName("cpu")] public double CpuSeconds { get; set; }
    /// <summary>How long the graphics card was busy for it, in seconds (its busiest engine, as Task Manager counts).</summary>
    [JsonPropertyName("gpu")] public double GpuSeconds { get; set; }
    [JsonPropertyName("read")] public long Read { get; set; }
    [JsonPropertyName("written")] public long Written { get; set; }
    /// <summary>Internet (and local network) traffic; counted only while the administrator helper runs.</summary>
    [JsonPropertyName("sent")] public long Sent { get; set; }
    [JsonPropertyName("received")] public long Received { get; set; }
    /// <summary>Seconds of that time with the traffic counted, so the report can say when it was not.</summary>
    [JsonPropertyName("net_seconds")] public double NetSeconds { get; set; }
    [JsonPropertyName("memory_peak")] public long MemoryPeak { get; set; }
    /// <summary>Memory in use multiplied by the seconds it was in use: divided by Seconds, the average.</summary>
    [JsonPropertyName("memory_seconds")] public double MemorySeconds { get; set; }

    [JsonIgnore] public long AverageMemory => Seconds > 0 ? (long)(MemorySeconds / Seconds) : 0;

    /// <summary>The processor's share it took while it ran, of the whole processor (all cores), in percent.</summary>
    public double CpuPercent(int cores) => Seconds > 0 ? Math.Min(100, CpuSeconds / Seconds / Math.Max(1, cores) * 100) : 0;

    /// <summary>The graphics card's share it took while it ran, in percent.</summary>
    [JsonIgnore] public double GpuPercent => Seconds > 0 ? Math.Min(100, GpuSeconds / Seconds * 100) : 0;

    public void Add(AppUse other)
    {
        Seconds += other.Seconds;
        CpuSeconds += other.CpuSeconds;
        GpuSeconds += other.GpuSeconds;
        Read += other.Read;
        Written += other.Written;
        Sent += other.Sent;
        Received += other.Received;
        NetSeconds += other.NetSeconds;
        MemoryPeak = Math.Max(MemoryPeak, other.MemoryPeak);
        MemorySeconds += other.MemorySeconds;
    }
}

/// <summary>One line of a report: an app and what it used in the period.</summary>
public sealed record AppUseRow(string App, AppUse Use);

/// <summary>
/// The record of what each app used, a total per app per day, kept in app-usage.json for
/// <see cref="KeptDays"/> days. Apps are known by the name of their program file ("chrome").
/// </summary>
public static class AppUsageLog
{
    public const int KeptDays = 90;
    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, AppUse>>? days;
    private static bool dirty;

    private static string File_ => Path.Combine(AppInfo.DataDir, "app-usage.json");

    private static Dictionary<string, Dictionary<string, AppUse>> Days
    {
        get
        {
            if (days is not null)
                return days;
            try
            {
                days = File.Exists(File_) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AppUse>>>(File.ReadAllText(File_)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warning($"The record of app usage could not be read: {error.Message}");
            }
            return days ??= [];
        }
    }

    private static string DayOf(DateTime when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Add what the apps used since the last time, to the day it was used on.</summary>
    public static void Record(DateTime when, IReadOnlyDictionary<string, AppUse> used)
    {
        lock (Gate)
        {
            var day = DayOf(when);
            if (!Days.TryGetValue(day, out var apps))
                Days[day] = apps = new Dictionary<string, AppUse>(StringComparer.OrdinalIgnoreCase);
            foreach (var (app, use) in used)
            {
                if (!apps.TryGetValue(app, out var total))
                    apps[app] = total = new AppUse();
                total.Add(use);
            }
            dirty = true;
        }
    }

    /// <summary>Every app's totals from the first day to the last (both included), the busiest processor user first.</summary>
    public static List<AppUseRow> Report(DateTime first, DateTime last)
    {
        var (from, to) = (DayOf(first), DayOf(last));
        var totals = new Dictionary<string, AppUse>(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            foreach (var (day, apps) in Days.Where(item => string.CompareOrdinal(item.Key, from) >= 0 && string.CompareOrdinal(item.Key, to) <= 0))
            {
                foreach (var (app, use) in apps)
                {
                    if (!totals.TryGetValue(app, out var total))
                        totals[app] = total = new AppUse();
                    total.Add(use);
                }
            }
        }
        return totals.Select(item => new AppUseRow(item.Key, item.Value)).OrderByDescending(row => row.Use.CpuSeconds).ThenBy(row => row.App, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The first day anything was recorded, or null.</summary>
    public static DateTime? FirstDay
    {
        get
        {
            lock (Gate)
                return Days.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } day ? DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>Write the record (days beyond the kept ones are dropped). Does nothing when nothing changed.</summary>
    public static void Save()
    {
        lock (Gate)
        {
            if (!dirty)
                return;
            var oldest = DayOf(DateTime.Now.AddDays(-KeptDays));
            foreach (var day in Days.Keys.Where(day => string.CompareOrdinal(day, oldest) < 0).ToList())
                Days.Remove(day);
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var temporary = File_ + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Days));
                File.Move(temporary, File_, overwrite: true);
                dirty = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"The record of app usage could not be saved: {error.Message}");
            }
        }
    }

    /// <summary>Forget everything recorded.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Days.Clear();
            dirty = true;
        }
        Save();
    }

    /// <summary>A report as a CSV file (comma-separated, for Excel or any spreadsheet), in megabytes and hours.</summary>
    public static string Csv(IEnumerable<AppUseRow> rows, int cores)
    {
        static string Cell(string text) => text.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        var invariant = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("App,Time running (hours),Processor time (core hours),Processor (% while running),Graphics card time (hours),"
                                    + "Graphics card (% while running),Memory average (MB),Memory peak (MB),Disk read (MB),Disk written (MB),"
                                    + "Internet sent (MB),Internet received (MB),Internet counted for (hours)\r\n");
        foreach (var (app, use) in rows)
        {
            csv.Append(Cell(app)).Append(',')
               .Append((use.Seconds / 3600).ToString("0.###", invariant)).Append(',')
               .Append((use.CpuSeconds / 3600).ToString("0.###", invariant)).Append(',')
               .Append(use.CpuPercent(cores).ToString("0.#", invariant)).Append(',')
               .Append((use.GpuSeconds / 3600).ToString("0.###", invariant)).Append(',')
               .Append(use.GpuPercent.ToString("0.#", invariant)).Append(',')
               .Append((use.AverageMemory / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.MemoryPeak / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.Read / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.Written / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.Sent / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.Received / 1048576.0).ToString("0.#", invariant)).Append(',')
               .Append((use.NetSeconds / 3600).ToString("0.###", invariant)).Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>Read the record from the disk again the next time it is asked for (what was not saved is dropped).</summary>
    public static void Reload()
    {
        lock (Gate)
            (days, dirty) = (null, false);
    }
}

/// <summary>
/// Looks at every running app every few seconds and adds what each used since the last look
/// to <see cref="AppUsageLog"/>: processor time and disk traffic (Windows keeps both per
/// program), memory, the graphics card's use (Windows' own counters, as Task Manager shows)
/// and, while the administrator helper runs, internet traffic (<see cref="NetMeter"/>).
/// Only what is counted between two looks is added, never what a program did before
/// recording started. Needs no special rights; programs Windows does not let it look into
/// (a few of its own) are left out.
/// </summary>
public sealed class AppUsageRecorder : IDisposable
{
    public const int IntervalSeconds = 5;

    private sealed record Seen(string App, long CpuTicks, long Read, long Written);

    private readonly Timer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private Dictionary<(int Pid, long Started), Seen> last = [];
    private Dictionary<string, CounterSample> lastGpu = [];
    private Dictionary<int, (long Sent, long Received)> lastNet = [];
    private long lastSave = Environment.TickCount64;
    private bool disposed;

    public AppUsageRecorder() => timer = new Timer(_ => Tick(), null, 1000, IntervalSeconds * 1000);

    private void Tick()
    {
        if (!Monitor.TryEnter(gate))
            return; // a look that takes long is not stacked up
        try
        {
            if (disposed)
                return;
            var seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var gpu = GpuByProcess();
            var net = NetMeter.Latest();
            var netDelta = new Dictionary<int, (long Sent, long Received)>();
            if (net is not null)
            {
                foreach (var (pid, (sent, received)) in net)
                {
                    var (sentBefore, receivedBefore) = lastNet.GetValueOrDefault(pid);
                    // The counts start again from nothing when the helper starts again.
                    netDelta[pid] = (sent >= sentBefore ? sent - sentBefore : sent, received >= receivedBefore ? received - receivedBefore : received);
                }
                lastNet = net;
            }
            var used = new Dictionary<string, AppUse>(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<(int, long), Seen>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var pid = process.Id;
                    if (pid <= 4)
                        continue; // Idle and System: Windows itself, not an app
                    string app;
                    long memory;
                    try
                    {
                        (app, memory) = (process.ProcessName, process.WorkingSet64);
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        continue; // it ended meanwhile
                    }
                    if (Counters(pid) is not var (started, cpuTicks, read, written))
                        continue;
                    var key = (pid, started);
                    seen[key] = new Seen(app, cpuTicks, read, written);
                    if (!used.TryGetValue(app, out var use))
                        used[app] = use = new AppUse();
                    // Every copy of a program counts (a browser runs many): its memory adds up, and the app ran while any copy did.
                    use.MemoryPeak += memory;
                    use.MemorySeconds += memory * seconds;
                    use.Seconds = seconds;
                    if (last.TryGetValue(key, out var before))
                    {
                        use.CpuSeconds += Math.Max(0, cpuTicks - before.CpuTicks) / 10_000_000.0;
                        use.Read += Math.Max(0, read - before.Read);
                        use.Written += Math.Max(0, written - before.Written);
                    }
                    // A browser draws in a process of its own: the app's copies add up, to at most the whole time.
                    if (gpu.TryGetValue(pid, out var busy))
                        use.GpuSeconds = Math.Min(seconds, use.GpuSeconds + busy / 100 * seconds);
                    if (netDelta.TryGetValue(pid, out var traffic))
                    {
                        use.Sent += traffic.Sent;
                        use.Received += traffic.Received;
                    }
                    if (net is not null)
                        use.NetSeconds = seconds;
                }
            }
            last = seen;
            AppUsageLog.Record(DateTime.Now, used);
            if (Environment.TickCount64 - lastSave > 60_000)
            {
                lastSave = Environment.TickCount64;
                AppUsageLog.Save();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning($"Recording app usage failed once: {error.Message}");
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    /// <summary>When a process started, and its processor time and disk traffic so far; null when Windows does not let it be looked into.</summary>
    private static (long Started, long CpuTicks, long Read, long Written)? Counters(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
                return null;
            var io = GetProcessIoCounters(handle, out var counters) ? counters : default;
            return (created, kernel + user, (long)io.ReadTransferCount, (long)io.WriteTransferCount);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Each process's use of the graphics card, in percent of its busiest engine, from Windows' "GPU Engine" counters.</summary>
    private Dictionary<int, double> GpuByProcess()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            if (!data.Contains("Utilization Percentage"))
                return result;
            var current = new Dictionary<string, CounterSample>();
            foreach (InstanceData instance in data["Utilization Percentage"]!.Values)
            {
                current[instance.InstanceName] = instance.Sample;
                // "pid_1234_luid_0x..._phys_0_eng_3_engtype_3D"
                var name = instance.InstanceName;
                if (!name.StartsWith("pid_", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(4, Math.Max(0, name.IndexOf('_', 4) - 4)), out var pid))
                    continue;
                if (lastGpu.TryGetValue(name, out var before))
                    result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Clamp(CounterSample.Calculate(before, instance.Sample), 0, 100));
            }
            lastGpu = current;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
        return result;
    }

    public void Dispose()
    {
        lock (gate)
            disposed = true;
        timer.Dispose();
        AppUsageLog.Save();
    }

    private const int QueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

/// <summary>
/// Internet traffic per program, counted by the administrator helper from Windows' own
/// network events (Microsoft-Windows-Kernel-Network), the same way it counts frames: the
/// helper writes net.json once a second with each process's bytes sent and received since
/// it started counting. Only the sizes are read, never what was sent.
/// </summary>
public static class NetMeter
{
    private static NetSession? session;

    public static string FileIn(string directory) => Path.Combine(directory, "net.json");

    /// <summary>The helper's side: start counting. False when Windows refuses (not an administrator).</summary>
    public static bool Start(string directory)
    {
        try
        {
            session ??= new NetSession(FileIn(directory));
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warning($"Counting internet use could not start: {error.Message}");
            return false;
        }
    }

    public static void Stop()
    {
        session?.Dispose();
        session = null;
    }

    /// <summary>Bytes sent and received per process id since counting started, or null when nobody is counting.</summary>
    public static Dictionary<int, (long Sent, long Received)>? Latest(string? directory = null)
    {
        try
        {
            var reading = JsonSerializer.Deserialize<NetReading>(File.ReadAllText(FileIn(directory ?? AppInfo.DataDir)));
            return reading is not null && (DateTime.UtcNow - reading.Time).TotalSeconds < 6
                ? reading.Use.Where(item => item.Value is [_, _]).ToDictionary(item => item.Key, item => (item.Value[0], item.Value[1]))
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public sealed class NetReading
    {
        [JsonPropertyName("time")] public DateTime Time { get; set; }
        /// <summary>Process id -> [bytes sent, bytes received].</summary>
        [JsonPropertyName("use")] public Dictionary<int, long[]> Use { get; set; } = [];
    }

    /// <summary>The events that carry traffic: TCP and UDP, sent and received, over IPv4 and IPv6. Each starts with the process id and the size.</summary>
    public static bool IsSend(int id) => id is 10 or 26 or 42 or 58;
    public static bool IsReceive(int id) => id is 11 or 27 or 43 or 59;

    private sealed class NetSession : IDisposable
    {
        private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

        private readonly TraceEventSession trace;
        private readonly ConcurrentDictionary<int, long[]> totals = new();
        private readonly Timer timer;
        private readonly string file;

        public NetSession(string file)
        {
            this.file = file;
            // One name, so a session a crashed helper left behind is taken over instead of piling up.
            trace = new TraceEventSession("PCButlerNet") { StopOnDispose = true };
            trace.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x10 | 0x20); // its IPv4 and IPv6 events
            trace.Source.AllEvents += Count;
            new Thread(() =>
            {
                try
                {
                    trace.Source.Process();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                }
            }) { IsBackground = true, Name = "PC Butler internet counter" }.Start();
            timer = new Timer(_ =>
            {
                try
                {
                    Write();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                }
            }, null, 1000, 1000);
        }

        private void Count(TraceEvent data)
        {
            if (data.ProviderGuid != KernelNetwork)
                return;
            var id = (int)data.ID;
            var send = IsSend(id);
            if (!send && !IsReceive(id))
                return;
            var payload = data.EventData();
            if (payload.Length < 8)
                return;
            var (pid, size) = (BitConverter.ToInt32(payload, 0), BitConverter.ToUInt32(payload, 4));
            var counts = totals.GetOrAdd(pid, _ => new long[2]);
            Interlocked.Add(ref counts[send ? 0 : 1], size);
        }

        private void Write()
        {
            var reading = new NetReading { Time = DateTime.UtcNow };
            foreach (var (pid, counts) in totals)
                reading.Use[pid] = [Interlocked.Read(ref counts[0]), Interlocked.Read(ref counts[1])];
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
            try
            {
                trace.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
            }
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
