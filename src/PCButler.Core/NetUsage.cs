// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>What went over the network on one day, all adapters together.</summary>
public sealed class NetDay
{
    [JsonPropertyName("sent")] public long Sent { get; set; }
    [JsonPropertyName("received")] public long Received { get; set; }
}

/// <summary>
/// The internet tracker's record: bytes sent and received per day, in net-usage.json, kept
/// for <see cref="KeptDays"/> days. Counted from the network adapters themselves, so it
/// needs no special rights and covers every program (which program used what is in
/// <see cref="AppUsageLog"/>, while the administrator helper counts it).
/// </summary>
public static class NetUsageLog
{
    public const int KeptDays = 400;
    private static readonly object Gate = new();
    private static Dictionary<string, NetDay>? days;
    private static bool dirty;

    private static string File_ => Path.Combine(AppInfo.DataDir, "net-usage.json");

    private static Dictionary<string, NetDay> Days
    {
        get
        {
            if (days is not null)
                return days;
            try
            {
                days = File.Exists(File_) ? JsonSerializer.Deserialize<Dictionary<string, NetDay>>(File.ReadAllText(File_)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warning($"The record of internet use could not be read: {error.Message}");
            }
            return days ??= [];
        }
    }

    private static string DayOf(DateTime when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static void Record(DateTime when, long sent, long received)
    {
        if (sent <= 0 && received <= 0)
            return;
        lock (Gate)
        {
            var day = DayOf(when);
            if (!Days.TryGetValue(day, out var total))
                Days[day] = total = new NetDay();
            total.Sent += sent;
            total.Received += received;
            dirty = true;
        }
    }

    /// <summary>Every day from the first to the last (both included) that has something recorded, the latest first.</summary>
    public static List<(DateTime Day, NetDay Use)> Between(DateTime first, DateTime last)
    {
        var (from, to) = (DayOf(first), DayOf(last));
        lock (Gate)
        {
            return Days.Where(item => string.CompareOrdinal(item.Key, from) >= 0 && string.CompareOrdinal(item.Key, to) <= 0)
                       .OrderByDescending(item => item.Key, StringComparer.Ordinal)
                       .Select(item => (DateTime.ParseExact(item.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                                        new NetDay { Sent = item.Value.Sent, Received = item.Value.Received }))
                       .ToList();
        }
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
                Log.Warning($"The record of internet use could not be saved: {error.Message}");
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Days.Clear();
            dirty = true;
        }
        Save();
    }
}

/// <summary>
/// Reads the network adapters' byte counters every few seconds and adds what went over them
/// since the last look to <see cref="NetUsageLog"/>. Loopback and tunnel adapters are left
/// out, so a VPN's traffic is not counted twice. An adapter whose counters start again from
/// nothing (it was switched off and on) is not counted for that one look.
/// </summary>
public sealed class NetUsageRecorder : IDisposable
{
    public const int IntervalSeconds = 2;

    private readonly Timer timer;
    private readonly object gate = new();
    private Dictionary<string, (long Sent, long Received)> last = [];
    private long lastAt;
    private long lastSave = Environment.TickCount64;
    private bool disposed;

    /// <summary>Bytes a second down and up at the last look; (0, 0) until two looks were made.</summary>
    public (long Down, long Up) Speed { get; private set; }

    public NetUsageRecorder() => timer = new Timer(_ => Tick(), null, 0, IntervalSeconds * 1000);

    private void Tick()
    {
        if (!Monitor.TryEnter(gate))
            return;
        try
        {
            if (disposed)
                return;
            var now = new Dictionary<string, (long Sent, long Received)>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                var totals = adapter.GetIPStatistics();
                now[adapter.Id] = (totals.BytesSent, totals.BytesReceived);
            }
            long sent = 0, received = 0;
            foreach (var (id, (nowSent, nowReceived)) in now)
            {
                if (!last.TryGetValue(id, out var before) || nowSent < before.Sent || nowReceived < before.Received)
                    continue;
                sent += nowSent - before.Sent;
                received += nowReceived - before.Received;
            }
            var at = Environment.TickCount64;
            var seconds = (at - lastAt) / 1000.0;
            if (last.Count > 0 && seconds > 0.2)
                Speed = ((long)(received / seconds), (long)(sent / seconds));
            (last, lastAt) = (now, at);
            NetUsageLog.Record(DateTime.Now, sent, received);
            if (Environment.TickCount64 - lastSave > 5 * 60_000)
            {
                lastSave = Environment.TickCount64;
                NetUsageLog.Save();
            }
        }
        catch (Exception error) when (error is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
            Log.Seldom("net usage " + error.Message, TimeSpan.FromHours(1), $"WARNING Counting internet use failed: {error.Message}");
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    public void Dispose()
    {
        lock (gate)
            disposed = true;
        timer.Dispose();
        NetUsageLog.Save();
    }
}
