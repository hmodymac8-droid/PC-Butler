// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>Megabits per second down and up, and the server's response time in milliseconds.</summary>
public sealed record SpeedResult(
    [property: JsonPropertyName("download")] double Download, [property: JsonPropertyName("upload")] double Upload,
    [property: JsonPropertyName("ping")] int Ping)
{
    /// <summary>"yyyy-MM-dd HH:mm", set when the result is saved.</summary>
    [JsonPropertyName("time")] public string Time { get; init; } = "";
}

/// <summary>
/// Measures the internet connection against Cloudflare's public speed-test servers
/// (speed.cloudflare.com): a few small requests for the response time, then several
/// downloads at once for a few seconds, then several uploads of random data. Nothing
/// about the PC is sent; Cloudflare sees the connection's address, as any site does.
/// </summary>
public static class SpeedTest
{
    private const string Server = "https://speed.cloudflare.com";
    private const int Streams = 4;
    private static readonly TimeSpan DownloadTime = TimeSpan.FromSeconds(6), UploadTime = TimeSpan.FromSeconds(5),
                                     RampUp = TimeSpan.FromSeconds(1);

    public static string HistoryFile => Path.Combine(AppInfo.DataDir, "speedtests.json");
    private const int HistoryKept = 200;

    /// <summary>Earlier results, newest first.</summary>
    public static List<SpeedResult> History()
    {
        try
        {
            return File.Exists(HistoryFile) ? JsonSerializer.Deserialize<List<SpeedResult>>(File.ReadAllText(HistoryFile)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Add a result to the history and return the history.</summary>
    public static List<SpeedResult> Remember(SpeedResult result)
    {
        var history = History();
        history.Insert(0, result with { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
        Write(history.Take(HistoryKept).ToList());
        return history;
    }

    public static void ClearHistory() => Write([]);

    private static void Write(List<SpeedResult> history)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(HistoryFile, JsonSerializer.Serialize(history));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the speed-test history: {error.Message}");
        }
    }

    /// <summary>Sends random bytes until the time is up, counting them.</summary>
    private sealed class RandomUpload(TimeSpan time, Action<int> sent, CancellationToken cancel) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[64 * 1024];
            Random.Shared.NextBytes(buffer);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < time && !cancel.IsCancellationRequested)
            {
                await stream.WriteAsync(buffer, cancel);
                await stream.FlushAsync(cancel);
                sent(buffer.Length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// Run the whole test. progress gets the stage ("ping", "download", "upload") and the
    /// speed so far in megabits per second. Throws HttpRequestException when offline.
    /// </summary>
    public static async Task<SpeedResult> Run(Action<string, double>? progress = null, CancellationToken cancel = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Streams * 2 }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PCButler/{AppInfo.Version}");

        // Response time: the quickest of a few empty requests (the first also pays for connecting, so it doesn't count).
        progress?.Invoke("ping", 0);
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync($"{Server}/__down?bytes=0", cancel);
            response.EnsureSuccessStatusCode();
            if (attempt > 0)
                best = Math.Min(best, clock.ElapsedMilliseconds);
        }

        var download = await Measure("download", DownloadTime, async (count, stop) =>
        {
            var buffer = new byte[64 * 1024];
            // The server refuses very large requests, so each stream asks for 25 MB again and again until the time is up.
            while (!stop.IsCancellationRequested)
            {
                using var response = await client.GetAsync($"{Server}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, stop);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(stop);
                for (int read; (read = await stream.ReadAsync(buffer, stop)) > 0;)
                    count(read);
            }
        }, progress, cancel);

        var upload = await Measure("upload", UploadTime, async (count, stop) =>
        {
            using var response = await client.PostAsync($"{Server}/__up", new RandomUpload(UploadTime, count, stop), stop);
        }, progress, cancel);

        return new SpeedResult(download, upload, (int)best);
    }

    /// <summary>Run several transfers at once for the given time; megabits per second after the first second.</summary>
    private static async Task<double> Measure(string stage, TimeSpan time, Func<Action<int>, CancellationToken, Task> transfer,
                                              Action<string, double>? progress, CancellationToken cancel)
    {
        long total = 0, atRampUp = 0;
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Exception? failure = null;
        var transfers = Enumerable.Range(0, Streams).Select(_ => Task.Run(async () =>
        {
            try
            {
                await transfer(bytes => Interlocked.Add(ref total, bytes), stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                failure ??= error;
            }
        })).ToList();

        var ramped = false;
        double speed = 0;
        while (clock.Elapsed < time && !transfers.All(task => task.IsCompleted))
        {
            await Task.Delay(200, cancel);
            if (!ramped && clock.Elapsed >= RampUp)
            {
                (ramped, atRampUp) = (true, Interlocked.Read(ref total));
                clock.Restart();
                time -= RampUp;
                continue;
            }
            if (ramped && clock.Elapsed.TotalSeconds > 0.1)
            {
                speed = (Interlocked.Read(ref total) - atRampUp) * 8 / clock.Elapsed.TotalSeconds / 1e6;
                progress?.Invoke(stage, speed);
            }
        }
        stop.Cancel();
        await Task.WhenAll(transfers);
        cancel.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref total) == 0 && failure is not null)
            throw new HttpRequestException(failure.Message, failure);
        return speed;
    }
}
