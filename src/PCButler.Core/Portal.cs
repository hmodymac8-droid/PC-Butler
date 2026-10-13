// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PCButler.Core;

/// <summary>How Portal behaves, as chosen on its page.</summary>
public sealed class PortalSettings
{
    /// <summary>Serve the phone's page over an encrypted connection. The phone's browser warns once, because the certificate is the PC's own.</summary>
    [JsonPropertyName("secure")] public bool Secure { get; set; }
    /// <summary>Where received files go; "" = "PC Butler Portal" inside Downloads.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
}

/// <summary>One file (or folder) on its way in or out, for the page to show.</summary>
public sealed class PortalTransfer
{
    private long done;
    private int cancelled;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    /// <summary>True: the phone is sending it to this PC. False: the phone is fetching it.</summary>
    public required bool Incoming { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>"active", "done", "failed" or "cancelled".</summary>
    public string State { get; internal set; } = "active";
    /// <summary>Where a received file ended up, once it is done.</summary>
    public string Path { get; internal set; } = "";
    public long Done => Interlocked.Read(ref done);
    public bool Cancelled => Volatile.Read(ref cancelled) != 0;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal string PartFile { get; init; } = "";

    internal void Add(long bytes) => Interlocked.Add(ref done, bytes);
    internal void Set(long bytes) => Interlocked.Exchange(ref done, bytes);

    /// <summary>Stop it from this PC. The phone is told the next time it asks or sends.</summary>
    public void Cancel()
    {
        if (State != "active")
            return;
        Volatile.Write(ref cancelled, 1);
        State = "cancelled";
    }
}

/// <summary>A file or folder the PC offers for the phone to fetch.</summary>
public sealed record PortalOffer(string Id, string Path, bool IsFolder, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

/// <summary>One line of Portal's own log of finished transfers.</summary>
public sealed record PortalLogEntry(
    [property: JsonPropertyName("time")] string Time, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("incoming")] bool Incoming,
    [property: JsonPropertyName("device")] string Device, [property: JsonPropertyName("path")] string Path);

public enum PortalState
{
    /// <summary>Running, nobody has scanned the code yet.</summary>
    Waiting,
    /// <summary>A phone opened the page and waits for Allow on the PC.</summary>
    Asking,
    /// <summary>The phone was allowed and can send and fetch.</summary>
    Connected,
    Stopped,
}

/// <summary>
/// Portal: files between this PC and a phone over the local network, with nothing going
/// through the internet. While a session runs, PC Butler serves one small web page on the
/// PC's network address. The phone reaches it by scanning a QR code, which carries a long
/// random secret; without that secret every request is answered "not found". The first
/// phone to open the page becomes the session's only device, and it can do nothing until
/// the user presses Allow on the PC after comparing a four-digit code shown on both.
///
/// Nothing listens when no session is running, and a session ends by itself after a
/// quarter of an hour without use.
/// </summary>
public sealed partial class PortalSession
{
    /// <summary>The phone sends files in pieces of at most this size, so a dropped connection costs little.</summary>
    public const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
    private const string Cookie = "pcb";

    private readonly WebApplication app;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, PortalTransfer> transfers = new();
    private readonly List<PortalTransfer> order = [];
    private readonly List<PortalOffer> offers = [];
    private readonly Timer idle;
    private string deviceSecret = "";
    private DateTime lastUse = DateTime.UtcNow;

    /// <summary>What the QR code holds: the page's address with the session's secret.</summary>
    public string Url { get; }
    public string Token { get; }
    public bool Secure { get; }
    public string Folder { get; }
    public PortalState State { get; private set; } = PortalState.Waiting;
    /// <summary>The four digits shown on both screens while the phone waits for Allow.</summary>
    public string Code { get; private set; } = "";
    /// <summary>What the phone says it is ("iPhone · Safari").</summary>
    public string Device { get; private set; } = "";
    /// <summary>Something changed: the state, the offers or the list of transfers. Raised on a server thread.</summary>
    public event Action? Changed;

    private PortalSession(WebApplication app, string url, string token, bool secure, string folder)
    {
        (this.app, Url, Token, Secure, Folder) = (app, url, token, secure, folder);
        idle = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Start serving on the given address (one of Portal.LocalAddresses). Port 0 lets Windows
    /// pick a free one. Throws IOException if the address can't be listened on.
    /// </summary>
    public static async Task<PortalSession> Start(PortalSettings settings, IPAddress address, int port = 0)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var folder = Portal.ReceiveFolder(settings);
        Directory.CreateDirectory(folder);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (settings.Secure)
            builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            // A little over one piece; anything bigger is not from the page.
            options.Limits.MaxRequestBodySize = ChunkSize + 64 * 1024;
            options.Listen(address, port, listen =>
            {
                if (settings.Secure)
                    listen.UseHttps(Portal.Certificate());
            });
        });
        var app = builder.Build();
        PortalSession? session = null;
        app.Run(context => session!.Handle(context));
        try
        {
            await app.StartAsync();
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
        {
            await app.DisposeAsync();
            throw new IOException(error.Message, error);
        }
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var url = $"{(settings.Secure ? "https" : "http")}://{address}:{new Uri(bound).Port}/p/{token}";
        session = new PortalSession(app, url, token, settings.Secure, folder);
        Log.Info($"Portal started on {address} ({(settings.Secure ? "encrypted" : "plain")})");
        return session;
    }

    public IReadOnlyList<PortalTransfer> Transfers
    {
        get
        {
            lock (gate)
                return order.ToList();
        }
    }

    public IReadOnlyList<PortalOffer> Offers
    {
        get
        {
            lock (gate)
                return offers.ToList();
        }
    }

    /// <summary>Let the waiting phone in.</summary>
    public void Approve()
    {
        lock (gate)
        {
            if (State != PortalState.Asking)
                return;
            State = PortalState.Connected;
            lastUse = DateTime.UtcNow;
        }
        Log.Info($"Portal: {Device} allowed");
        Changed?.Invoke();
    }

    /// <summary>Turn the phone away (or disconnect it). The next phone to scan the code can ask again.</summary>
    public void Refuse()
    {
        lock (gate)
        {
            if (State is not (PortalState.Asking or PortalState.Connected))
                return;
            (deviceSecret, Device, Code, State) = ("", "", "", PortalState.Waiting);
        }
        Changed?.Invoke();
    }

    /// <summary>Offer a file or a folder for the phone to fetch. Returns null if it isn't there.</summary>
    public PortalOffer? Offer(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var folder = Directory.Exists(path);
        if (!folder && !File.Exists(path))
            return null;
        long size;
        try
        {
            size = folder ? FilesUnder(path).Sum(file => new FileInfo(file).Length) : new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        PortalOffer offer;
        lock (gate)
        {
            if (offers.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } already)
                return already;
            offer = new PortalOffer(NewId(), path, folder, size);
            offers.Add(offer);
            lastUse = DateTime.UtcNow;
        }
        Changed?.Invoke();
        return offer;
    }

    public void RemoveOffer(string id)
    {
        lock (gate)
            offers.RemoveAll(offer => offer.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Stop serving. Transfers in flight end; unfinished pieces stay for a later session to clear.</summary>
    public async Task Stop()
    {
        lock (gate)
        {
            if (State == PortalState.Stopped)
                return;
            State = PortalState.Stopped;
        }
        idle.Dispose();
        foreach (var transfer in Transfers)
            transfer.Cancel();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
        }
        await app.DisposeAsync();
        Log.Info("Portal stopped");
        Changed?.Invoke();
    }

    private void CheckIdle()
    {
        bool stale;
        lock (gate)
            stale = State != PortalState.Stopped && DateTime.UtcNow - lastUse > IdleLimit && order.All(transfer => transfer.State != "active");
        if (stale)
            _ = Stop();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private static IEnumerable<string> FilesUnder(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        });

    // --- the phone's requests ---

    [GeneratedRegex("^[A-Za-z0-9_-]{8,40}$")] private static partial Regex IdPattern();

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static Task Json(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private async Task Handle(HttpContext context)
    {
        var parts = (context.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Without the secret from the QR code there is nothing here at all.
        if (parts.Length < 2 || parts[0] != "p" || !Same(parts[1], Token) || State == PortalState.Stopped)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var method = context.Request.Method;
        var what = parts.Length > 2 ? parts[2] : "";
        try
        {
            if (what == "" && method == "GET")
            {
                await Page(context);
                return;
            }
            if (what == "hello" && method == "POST")
            {
                await Hello(context);
                return;
            }
            // Everything else is only for the one phone that was allowed.
            var mine = context.Request.Cookies.TryGetValue(Cookie, out var secret) && deviceSecret.Length > 0 && Same(secret ?? "", deviceSecret);
            if (!mine || State != PortalState.Connected)
            {
                await Json(context, 403, new { approved = false, waiting = mine && State == PortalState.Asking });
                return;
            }
            var id = parts.Length > 3 ? parts[3] : "";
            switch (what, method)
            {
                case ("state", "GET"):
                    await Json(context, 200, new
                    {
                        approved = true, pc = Environment.MachineName, chunk = ChunkSize,
                        offers = Offers.Select(offer => new { id = offer.Id, name = offer.Name + (offer.IsFolder ? ".zip" : ""), size = offer.Size, folder = offer.IsFolder }),
                    });
                    break;
                case ("upload", "GET") when IdPattern().IsMatch(id):
                    if (transfers.TryGetValue(id, out var known) && known.Incoming)
                        await Json(context, known.Cancelled ? 410 : 200, new { received = known.Done, done = known.State == "done" });
                    else
                        await Json(context, 404, new { received = 0 });
                    break;
                case ("upload", "PUT") when IdPattern().IsMatch(id):
                    await Upload(context, id);
                    break;
                case ("upload", "DELETE") when IdPattern().IsMatch(id):
                    if (transfers.TryGetValue(id, out var dropped) && dropped.Incoming && dropped.State == "active")
                    {
                        dropped.Cancel();
                        await dropped.Gate.WaitAsync();
                        try
                        {
                            TryDelete(dropped.PartFile);
                        }
                        finally
                        {
                            dropped.Gate.Release();
                        }
                        Changed?.Invoke();
                    }
                    await Json(context, 200, new { });
                    break;
                case ("file", "GET"):
                    await Download(context, id);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // The phone went away mid-request, or the disk refused: the piece is asked for again.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 500;
        }
    }

    private static Task Page(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        // The page is self-contained: no scripts, styles, fonts or pictures from anywhere else.
        headers.ContentSecurityPolicy = "default-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        return context.Response.Body.WriteAsync(Portal.PageHtml).AsTask();
    }

    /// <summary>The page says who it is. The first phone to do so becomes the session's device and waits for Allow.</summary>
    private async Task Hello(HttpContext context)
    {
        var raise = false;
        bool approved, known;
        string code;
        lock (gate)
        {
            known = context.Request.Cookies.TryGetValue(Cookie, out var secret) && deviceSecret.Length > 0 && Same(secret ?? "", deviceSecret);
            if (!known && deviceSecret.Length == 0)
            {
                deviceSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                Code = RandomNumberGenerator.GetInt32(0, 10000).ToString("0000");
                Device = Portal.DeviceName(context.Request.Headers.UserAgent.ToString());
                State = PortalState.Asking;
                lastUse = DateTime.UtcNow;
                context.Response.Cookies.Append(Cookie, deviceSecret, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Secure, Path = "/p/" + Token, IsEssential = true,
                });
                (known, raise) = (true, true);
            }
            (approved, code) = (known && State == PortalState.Connected, known ? Code : "");
        }
        if (raise)
            Changed?.Invoke();
        if (!known)
            await Json(context, 403, new { busy = true });
        else
            await Json(context, 200, new { approved, code, pc = Environment.MachineName });
    }

    /// <summary>
    /// One piece of a file from the phone. The piece must start exactly where the last one
    /// ended; if it doesn't, the answer says where to continue, which is how a broken
    /// transfer picks up again.
    /// </summary>
    private async Task Upload(HttpContext context, string id)
    {
        var query = context.Request.Query;
        if (!long.TryParse(query["size"], out var size) || size < 0 || !long.TryParse(query["offset"], out var offset) || offset < 0)
        {
            await Json(context, 400, new { });
            return;
        }
        var transfer = transfers.GetOrAdd(id, _ => new PortalTransfer
        {
            Id = id, Name = Portal.SafeName(query["name"].ToString()), Size = size, Incoming = true,
            PartFile = System.IO.Path.Combine(Folder, ".portal-parts", id + ".part"),
        });
        var isNew = false;
        lock (gate)
        {
            if (!order.Contains(transfer))
            {
                order.Add(transfer);
                isNew = true;
            }
            lastUse = DateTime.UtcNow;
        }
        if (!transfer.Incoming || transfer.Size != size)
        {
            await Json(context, 400, new { });
            return;
        }
        if (isNew)
        {
            Changed?.Invoke();
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(Folder)!);
            if (drive.AvailableFreeSpace < size + (64L << 20))
            {
                transfer.State = "failed";
                Changed?.Invoke();
                await Json(context, 507, new { full = true });
                return;
            }
        }
        if (transfer.Cancelled || transfer.State == "failed")
        {
            await Json(context, 410, new { });
            return;
        }
        if (transfer.State == "done")
        {
            await Json(context, 200, new { received = size, done = true });
            return;
        }
        // One piece at a time per file.
        if (!await transfer.Gate.WaitAsync(0))
        {
            await Json(context, 409, new { received = transfer.Done, busy = true });
            return;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(transfer.PartFile)!);
            await using (var part = new FileStream(transfer.PartFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                if (offset != part.Length)
                {
                    transfer.Set(part.Length);
                    await Json(context, 409, new { received = part.Length });
                    return;
                }
                part.Seek(0, SeekOrigin.End);
                var buffer = new byte[1 << 16];
                var room = size - part.Length;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (transfer.Cancelled)
                        break;
                    if (read > room)
                    {
                        // More than it said it would send: not from the page.
                        transfer.State = "failed";
                        break;
                    }
                    await part.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    room -= read;
                    transfer.Add(read);
                }
                await part.FlushAsync();
                transfer.Set(part.Length);
            }
            if (transfer.Cancelled || transfer.State == "failed")
            {
                TryDelete(transfer.PartFile);
                Changed?.Invoke();
                await Json(context, transfer.Cancelled ? 410 : 400, new { });
                return;
            }
            if (transfer.Done == size)
            {
                var destination = Sorter.UniqueDestination(Folder, transfer.Name);
                File.Move(transfer.PartFile, destination);
                (transfer.Path, transfer.State) = (destination, "done");
                Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), transfer.Name, size, true, Device, destination));
                Log.Info($"Portal received {destination}");
                Changed?.Invoke();
            }
            lock (gate)
                lastUse = DateTime.UtcNow;
            await Json(context, 200, new { received = transfer.Done, done = transfer.State == "done" });
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    /// <summary>The phone fetches an offered file (the browser may ask for a part of it to resume) or a folder as one zip.</summary>
    private async Task Download(HttpContext context, string id)
    {
        PortalOffer? offer;
        lock (gate)
        {
            offer = offers.FirstOrDefault(item => item.Id == id);
            lastUse = DateTime.UtcNow;
        }
        if (offer is null || (!offer.IsFolder && !File.Exists(offer.Path)) || (offer.IsFolder && !Directory.Exists(offer.Path)))
        {
            context.Response.StatusCode = 404;
            return;
        }
        var name = offer.Name + (offer.IsFolder ? ".zip" : "");
        var transfer = transfers.AddOrUpdate("out-" + offer.Id,
            _ => new PortalTransfer { Id = "out-" + offer.Id, Name = name, Size = offer.Size, Incoming = false },
            // Fetching it again starts a fresh line; resuming a part keeps the same one.
            (_, old) => old.State == "active" && !old.Cancelled ? old : new PortalTransfer { Id = "out-" + offer.Id, Name = name, Size = offer.Size, Incoming = false });
        lock (gate)
        {
            if (!order.Contains(transfer))
                order.Add(transfer);
        }
        Changed?.Invoke();
        var response = context.Response;
        response.ContentType = offer.IsFolder ? "application/zip" : "application/octet-stream";
        response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(name)}";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-store";
        var finished = false;
        try
        {
            if (offer.IsFolder)
            {
                await SendZip(context, offer, transfer);
                finished = !transfer.Cancelled;
            }
            else
            {
                finished = await SendFile(context, offer, transfer);
            }
        }
        finally
        {
            if (transfer.Cancelled)
                context.Abort();
            else if (finished)
            {
                transfer.State = "done";
                Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), name, offer.Size, false, Device, offer.Path));
            }
            Changed?.Invoke();
        }
    }

    [GeneratedRegex(@"^bytes=(\d*)-(\d*)$")] private static partial Regex RangePattern();

    /// <summary>Returns true when the file's last byte has gone out.</summary>
    private static async Task<bool> SendFile(HttpContext context, PortalOffer offer, PortalTransfer transfer)
    {
        await using var file = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        long start = 0, end = file.Length - 1;
        var response = context.Response;
        response.Headers.AcceptRanges = "bytes";
        var range = RangePattern().Match(context.Request.Headers.Range.ToString());
        if (range.Success && file.Length > 0)
        {
            var (first, last) = (range.Groups[1].Value, range.Groups[2].Value);
            if (first.Length == 0 && long.TryParse(last, out var tail))
                start = Math.Max(0, file.Length - tail);
            else if (long.TryParse(first, out start) && last.Length > 0 && long.TryParse(last, out var until))
                end = Math.Min(until, end);
            if (start > end)
            {
                response.StatusCode = 416;
                response.Headers.ContentRange = $"bytes */{file.Length}";
                return false;
            }
            response.StatusCode = 206;
            response.Headers.ContentRange = $"bytes {start}-{end}/{file.Length}";
        }
        response.ContentLength = end - start + 1;
        file.Seek(start, SeekOrigin.Begin);
        transfer.Set(start);
        var buffer = new byte[1 << 17];
        var left = end - start + 1;
        while (left > 0 && !transfer.Cancelled)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), context.RequestAborted);
            if (read == 0)
                break;
            await response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            left -= read;
            transfer.Add(read);
        }
        return left == 0 && end == file.Length - 1;
    }

    /// <summary>A folder goes as one zip, made as it is sent (stored, not squeezed: on a local network that is the faster way).</summary>
    private static async Task SendZip(HttpContext context, PortalOffer offer, PortalTransfer transfer)
    {
        // The zip writer writes as it goes, which this server only allows when told so.
        if (context.Features.Get<IHttpBodyControlFeature>() is { } control)
            control.AllowSynchronousIO = true;
        var root = System.IO.Path.GetDirectoryName(offer.Path.TrimEnd('\\', '/')) ?? offer.Path;
        await context.Response.StartAsync();
        using var zip = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        var buffer = new byte[1 << 17];
        foreach (var path in FilesUnder(offer.Path))
        {
            if (transfer.Cancelled)
                return;
            var entry = zip.CreateEntry(System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), CompressionLevel.NoCompression);
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                await using var target = entry.Open();
                int read;
                while ((read = await file.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (transfer.Cancelled)
                        return;
                    await target.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    transfer.Add(read);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A file that can't be read is left out of the zip.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>What Portal needs around a session: addresses, the receive folder, the firewall, the log, the QR code's content.</summary>
public static partial class Portal
{
    public const string FirewallRule = "PC Butler Portal";

    /// <summary>The session that is running, if any. There is one at a time.</summary>
    public static PortalSession? Current { get; set; }

    public static string ReceiveFolder(PortalSettings settings) =>
        settings.Folder.Length > 0 ? settings.Folder : System.IO.Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Portal");

    private static byte[]? pageHtml;

    /// <summary>The phone's page: one file with its styles and script inside, built into the program.</summary>
    public static byte[] PageHtml
    {
        get
        {
            if (pageHtml is not null)
                return pageHtml;
            using var stream = typeof(Portal).Assembly.GetManifestResourceStream("PCButler.Core.portal.html")!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return pageHtml = memory.ToArray();
        }
    }

    /// <summary>
    /// The PC's addresses on local networks, the likeliest first: a wired or Wi-Fi adapter
    /// that is up and has a gateway. Virtual adapters and VPNs come last.
    /// </summary>
    public static List<(IPAddress Address, string Adapter)> LocalAddresses()
    {
        var found = new List<(IPAddress, string, int)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses.Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !item.Address.Equals(IPAddress.Any));
            var real = adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                       && !Regex.IsMatch(adapter.Description, "virtual|vmware|hyper-v|vpn|tap|loopback|bluetooth", RegexOptions.IgnoreCase);
            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address)
                    && !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    found.Add((address.Address, adapter.Name, (gateway ? 0 : 2) + (real ? 0 : 1)));
            }
        }
        return found.OrderBy(item => item.Item3).Select(item => (item.Item1, item.Item2)).ToList();
    }

    /// <summary>
    /// True if Windows marks a connected network as Public (a café, an airport). The firewall
    /// rule Portal asks for covers Private networks only, so on a Public one the phone can't reach it.
    /// </summary>
    public static bool OnPublicNetwork()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null)
                return false;
            dynamic manager = Activator.CreateInstance(type)!;
            foreach (dynamic network in manager.GetNetworks(1)) // connected ones
            {
                if ((int)network.GetCategory() == 0)
                    return true;
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException
                                          or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException)
        {
        }
        return false;
    }

    private static string Netsh(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return process.ExitCode == 0 ? output : "";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>True if the Windows firewall already lets other devices reach this copy of the program. Needs no special rights.</summary>
    public static bool FirewallAllows(string? program = null) =>
        Netsh("advfirewall", "firewall", "show", "rule", $"name={FirewallRule}", "verbose")
            .Contains(program ?? Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Let devices on Private networks reach this program (administrator only; the helper calls
    /// it). The rule names this program's own file, so it opens nothing for anything else.
    /// </summary>
    public static bool AllowThroughFirewall()
    {
        var program = Environment.ProcessPath!;
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Netsh("advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", $"program={program}",
              "protocol=TCP", "profile=private", "enable=yes");
        return FirewallAllows(program);
    }

    [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$", RegexOptions.IgnoreCase)] private static partial Regex Reserved();

    /// <summary>
    /// A file name the phone gave, made safe to create here: no folders, no characters or
    /// names Windows refuses, never empty. "..\..\evil.exe" becomes "evil.exe".
    /// </summary>
    public static string SafeName(string name)
    {
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = Unsafe().Replace(name, "_").Trim().TrimEnd('.', ' ');
        if (name.Length > 180)
            name = name[..(180 - System.IO.Path.GetExtension(name).Length)] + System.IO.Path.GetExtension(name);
        if (name.Length == 0 || name.All(character => character == '.'))
            name = "file";
        return Reserved().IsMatch(name) ? "_" + name : name;
    }

    /// <summary>"iPhone · Safari" from what the browser says about itself.</summary>
    public static string DeviceName(string userAgent)
    {
        string Pick(params (string Mark, string Name)[] choices) =>
            choices.FirstOrDefault(choice => userAgent.Contains(choice.Mark, StringComparison.OrdinalIgnoreCase)).Name ?? "";
        var device = Pick(("iPhone", "iPhone"), ("iPad", "iPad"), ("Android", "Android"), ("Windows", "Windows PC"), ("Macintosh", "Mac"), ("Linux", "Linux"));
        var browser = Pick(("Edg", "Edge"), ("SamsungBrowser", "Samsung Internet"), ("Firefox", "Firefox"), ("FxiOS", "Firefox"),
                           ("CriOS", "Chrome"), ("Chrome", "Chrome"), ("Safari", "Safari"));
        var name = string.Join(" · ", new[] { device, browser }.Where(part => part.Length > 0));
        return name.Length > 0 ? name : "Unknown device";
    }

    // --- the log of finished transfers ---

    private static readonly object LogGate = new();
    private static string LogFile => System.IO.Path.Combine(AppInfo.DataDir, "portal-history.json");
    private const int LogKept = 300;

    /// <summary>Finished transfers, newest first.</summary>
    public static List<PortalLogEntry> History()
    {
        lock (LogGate)
        {
            try
            {
                return File.Exists(LogFile) ? JsonSerializer.Deserialize<List<PortalLogEntry>>(File.ReadAllText(LogFile)) ?? [] : [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    internal static void AddToLog(PortalLogEntry entry)
    {
        lock (LogGate)
        {
            var log = History();
            log.Insert(0, entry);
            WriteLog(log.Take(LogKept).ToList());
        }
    }

    public static void ClearHistory()
    {
        lock (LogGate)
            WriteLog([]);
    }

    private static void WriteLog(List<PortalLogEntry> log)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(LogFile, JsonSerializer.Serialize(log));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save Portal's history: {error.Message}");
        }
    }

    /// <summary>Pieces of files that never finished, from sessions that ended: cleared when a new one starts.</summary>
    public static void ClearParts(string folder)
    {
        try
        {
            var parts = System.IO.Path.Combine(folder, ".portal-parts");
            if (Directory.Exists(parts))
                Directory.Delete(parts, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --- the encrypted connection's certificate ---

    private static string CertificateFile => System.IO.Path.Combine(AppInfo.DataDir, "portal-certificate.bin");

    /// <summary>
    /// This PC's own certificate for the encrypted mode, made once and kept (its key protected
    /// by Windows for this user). No authority vouches for it, which is why a phone's browser
    /// warns the first time; it stays the same afterwards, so the phone only asks once.
    /// </summary>
    public static X509Certificate2 Certificate()
    {
        try
        {
            if (File.Exists(CertificateFile))
            {
                var saved = ProtectedData.Unprotect(File.ReadAllBytes(CertificateFile), null, DataProtectionScope.CurrentUser);
                var loaded = X509CertificateLoader.LoadPkcs12(saved, null);
                if (loaded.NotAfter > DateTime.Now.AddDays(7))
                    return loaded;
            }
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=PC Butler Portal ({Environment.MachineName})", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);
        foreach (var (address, _) in LocalAddresses())
            names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        var exported = made.Export(X509ContentType.Pkcs12);
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllBytes(CertificateFile, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warning($"Portal's certificate could not be saved: {error.Message}");
        }
        return X509CertificateLoader.LoadPkcs12(exported, null);
    }
}

/// <summary>The QR code a phone scans to open Portal's page.</summary>
public static class PortalQr
{
    /// <summary>A PNG of the code, dark on white (the way cameras read it best), with a quiet border.</summary>
    public static byte[] Png(string text, int pixelsPerModule = 10)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule, [8, 10, 14], [255, 255, 255]);
    }
}
