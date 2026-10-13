// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
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
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PCButler.Core;

/// <summary>A PC that was paired with this one. Its id is the fingerprint of its certificate, which is what "trusted" means.</summary>
public sealed class PortalDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Take what it sends without asking each time.</summary>
    [JsonPropertyName("auto_accept")] public bool AutoAccept { get; set; }
    /// <summary>Where it was last seen, to find it again when the network keeps announcements from getting through.</summary>
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
}

/// <summary>How Portal behaves, and the PCs it trusts.</summary>
public sealed class PortalSettings
{
    /// <summary>Where received files go; "" = "PC Butler Courier" inside Downloads.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("devices")] public List<PortalDevice> Devices { get; set; } = [];
    /// <summary>Let other PCs find this one by its ID over the internet. Off until the user turns it on.</summary>
    [JsonPropertyName("reachable")] public bool Reachable { get; set; }
}

/// <summary>Another PC running Portal, as this one sees it right now.</summary>
public sealed class PortalPeer
{
    public required string Id { get; init; }
    public string Name { get; internal set; } = "";
    public string Address { get; internal set; } = "";
    public int Port { get; internal set; }
    public DateTime LastSeen { get; internal set; }
    public bool Trusted { get; internal set; }
    public bool AutoAccept { get; internal set; }
    /// <summary>Set while this PC is reached over the internet: its address at the other end of the open path.</summary>
    public IPEndPoint? Tunnel { get; internal set; }
    public bool Internet => Tunnel is not null;
    public bool Online => (DateTime.UtcNow - LastSeen).TotalSeconds < 12;
}

/// <summary>One batch of files on its way in or out, for the page to show.</summary>
public sealed class PortalTransfer
{
    private long done;
    private int cancelled;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required int Files { get; init; }
    /// <summary>True: the other PC is sending it here.</summary>
    public required bool Incoming { get; init; }
    public required string Peer { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>"waiting" (for the other PC to accept), "active", "done", "failed", "refused" or "cancelled".</summary>
    public string State { get; internal set; } = "active";
    /// <summary>Why it failed, in the user's language.</summary>
    public string Note { get; internal set; } = "";
    /// <summary>Where the received files ended up.</summary>
    public string Path { get; internal set; } = "";
    public long Done => Interlocked.Read(ref done);
    public bool Cancelled => Volatile.Read(ref cancelled) != 0;
    public bool Running => State is "waiting" or "active";

    internal void Add(long bytes) => Interlocked.Add(ref done, bytes);

    /// <summary>Stop it from this PC. The other PC is told with its next request.</summary>
    public void Cancel()
    {
        if (!Running)
            return;
        Volatile.Write(ref cancelled, 1);
        State = "cancelled";
    }
}

/// <summary>A pairing in progress: both PCs show the same six digits, and each user confirms on their own PC.</summary>
public sealed class PortalPairing
{
    public required string PeerId { get; init; }
    public required string PeerName { get; init; }
    /// <summary>"123 456". Empty until both sides have exchanged what the code is made from.</summary>
    public string Code { get; internal set; } = "";
    /// <summary>True when the other PC started it.</summary>
    public required bool Incoming { get; init; }
    /// <summary>"waiting", "confirmed" (by this PC's user), "refused" or "failed".</summary>
    public string State { get; internal set; } = "waiting";
    internal DateTime Started { get; } = DateTime.UtcNow;
    internal string Commit { get; init; } = "";
    internal string NonceMine { get; init; } = "";
    internal string Address { get; init; } = "";
    internal int Port { get; init; }
    internal IPEndPoint? Via { get; init; }
}

/// <summary>Files another PC wants to send, waiting for Accept.</summary>
public sealed class PortalRequest
{
    public required string Id { get; init; }
    public required string PeerId { get; init; }
    public required string PeerName { get; init; }
    public required string Name { get; init; }
    public required int Files { get; init; }
    public required long Size { get; init; }
}

/// <summary>One line of Portal's own log of finished transfers.</summary>
public sealed record PortalLogEntry(
    [property: JsonPropertyName("time")] string Time, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("incoming")] bool Incoming,
    [property: JsonPropertyName("device")] string Device, [property: JsonPropertyName("path")] string Path);

/// <summary>
/// Portal: files between two PCs running PC Butler, straight over the local network.
///
/// While it is on, this PC listens on one port and announces itself on the network so
/// other PCs can list it. Every connection is encrypted, and each PC proves who it is
/// with its own certificate; a PC's id is that certificate's fingerprint.
///
/// Two PCs must be paired before anything can be sent. Pairing shows the same six-digit
/// code on both screens, made in a way nobody sitting between them on the network can
/// steer (each side commits to its random part before seeing the other's), and each user
/// confirms on their own PC. After that the two recognise each other by certificate.
///
/// Nothing listens while Portal is off.
/// </summary>
public sealed partial class PortalNode
{
    /// <summary>Files travel in pieces of this size, so a dropped connection costs little and the transfer picks up where it was.</summary>
    public const int ChunkSize = 8 * 1024 * 1024;
    public const int DiscoveryPort = 53531;
    private const string Api = "/portal/v1";
    private static readonly TimeSpan RequestLife = TimeSpan.FromMinutes(3);

    private sealed class IncomingJob
    {
        public required string Id { get; init; }
        public required string PeerId { get; init; }
        public required List<(string Path, long Size)> Files { get; init; }
        public required PortalTransfer Transfer { get; init; }
        public string State = "pending"; // pending, accepted, refused
        public DateTime Asked = DateTime.UtcNow;
        public readonly Dictionary<string, string> Tops = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> Finished = [];
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    private readonly PortalSettings settings;
    private readonly Action save;
    private readonly object gate = new();
    private readonly Dictionary<string, PortalPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PortalTransfer> transfers = [];
    private readonly List<PortalPairing> pairings = [];
    private readonly ConcurrentDictionary<string, IncomingJob> jobs = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly UdpClient? radio;
    private bool stopped;

    /// <summary>This PC's id: the fingerprint of its certificate.</summary>
    public string Id { get; }
    public string Name { get; } = Environment.MachineName;
    public IPAddress Address { get; }
    public int Port { get; }
    public string Folder { get; }
    /// <summary>Something changed: devices, pairings, requests or transfers. Raised on a background thread.</summary>
    public event Action? Changed;
    /// <summary>Another PC is asking for something the user must answer (a pairing, or files to accept).</summary>
    public event Action<string>? Attention;

    private PortalNode(WebApplication app, X509Certificate2 certificate, PortalSettings settings, Action save, IPAddress address, int port, UdpClient? radio)
    {
        (this.app, this.certificate, this.settings, this.save, Address, Port, this.radio) = (app, certificate, settings, save, address, port, radio);
        Id = Portal.Fingerprint(certificate);
        Folder = Portal.ReceiveFolder(settings);
        foreach (var device in settings.Devices)
        {
            peers[device.Id] = new PortalPeer
            {
                Id = device.Id, Name = device.Name, Address = device.Address, Port = device.Port, Trusted = true, AutoAccept = device.AutoAccept,
            };
        }
    }

    /// <summary>
    /// Start listening on the given address (one of Portal.LocalAddresses). Port 0 lets
    /// Windows pick a free one. "save" is called when the trusted devices change, to store
    /// the settings. discovery: false leaves out the local network announcements, internet:
    /// false the socket for other networks, directory: false the public directory (the tests do).
    /// </summary>
    public static async Task<PortalNode> Start(PortalSettings settings, IPAddress address, Action? save = null, int port = 0,
                                               bool discovery = true, X509Certificate2? certificate = null, bool internet = true, bool directory = true)
    {
        certificate ??= Portal.Certificate();
        Directory.CreateDirectory(Portal.ReceiveFolder(settings));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = ChunkSize + 64 * 1024;
            options.Listen(address, port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certificate,
                // Every caller shows its own certificate. None is signed by an authority; who it is gets checked by fingerprint.
                ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                ClientCertificateValidation = (_, _, _) => true,
            }));
        });
        var app = builder.Build();
        PortalNode? node = null;
        app.Run(context => node!.Handle(context));
        try
        {
            await app.StartAsync();
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
        {
            await app.DisposeAsync();
            throw new IOException(error.Message, error);
        }
        var bound = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        UdpClient? radio = null;
        if (discovery)
        {
            try
            {
                radio = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                radio.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                radio.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (SocketException error)
            {
                // Without announcements the other PC can still be added by its address.
                Log.Warning($"Courier can't announce itself: {error.Message}");
                radio?.Dispose();
                radio = null;
            }
        }
        node = new PortalNode(app, certificate, settings, save ?? (() => { }), address, bound, radio);
        if (radio is not null)
        {
            _ = Task.Run(node.Listen);
            _ = Task.Run(node.Announce);
        }
        if (internet)
            node.StartInternet(directory);
        _ = Task.Run(node.Housekeeping);
        Log.Info($"Courier started on {address}:{bound}");
        return node;
    }

    public bool Stopped => stopped;

    public IReadOnlyList<PortalPeer> Peers
    {
        get
        {
            lock (gate)
                return peers.Values.OrderByDescending(peer => peer.Trusted).ThenBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<PortalTransfer> Transfers
    {
        get
        {
            lock (gate)
                return transfers.ToList();
        }
    }

    /// <summary>Pairings that are still being settled.</summary>
    public IReadOnlyList<PortalPairing> Pairings
    {
        get
        {
            lock (gate)
                return pairings.Where(pairing => pairing.State == "waiting").ToList();
        }
    }

    /// <summary>Files other PCs want to send, waiting for Accept.</summary>
    public IReadOnlyList<PortalRequest> Requests =>
        jobs.Values.Where(job => job.State == "pending").OrderBy(job => job.Asked).Select(job => new PortalRequest
        {
            Id = job.Id, PeerId = job.PeerId, PeerName = job.Transfer.Peer, Name = job.Transfer.Name, Files = job.Files.Count, Size = job.Transfer.Size,
        }).ToList();

    public async Task Stop()
    {
        lock (gate)
        {
            if (stopped)
                return;
            stopped = true;
        }
        stopping.Cancel();
        radio?.Dispose();
        StopInternet();
        // Remember where the trusted PCs were last seen, to find them again next time.
        lock (gate)
        {
            foreach (var device in settings.Devices)
            {
                if (peers.TryGetValue(device.Id, out var peer) && peer.Address.Length > 0 && peer.Port > 0)
                    (device.Address, device.Port) = (peer.Address, peer.Port);
            }
        }
        save();
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
        Log.Info("Courier stopped");
        Changed?.Invoke();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    private void Raise() => Changed?.Invoke();

    // --- finding other PCs ---

    private sealed record Beacon(
        [property: JsonPropertyName("app")] string App, [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("port")] int Port);

    /// <summary>Say "here I am" to the local network every couple of seconds.</summary>
    private async Task Announce()
    {
        var packet = JsonSerializer.SerializeToUtf8Bytes(new Beacon("pcbutler-portal", Id, Name, Port));
        while (!stopping.IsCancellationRequested)
        {
            foreach (var target in Portal.BroadcastAddresses(Address))
            {
                try
                {
                    await radio!.SendAsync(packet, new IPEndPoint(target, DiscoveryPort), stopping.Token);
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
            try
            {
                await Task.Delay(2000, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task Listen()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var packet = await radio!.ReceiveAsync(stopping.Token);
                if (packet.Buffer.Length > 600)
                    continue;
                var beacon = JsonSerializer.Deserialize<Beacon>(packet.Buffer);
                if (beacon is { App: "pcbutler-portal", Port: > 0 and < 65536 } && IdPattern().IsMatch(beacon.Id) && beacon.Id != Id)
                    Seen(beacon.Id, Portal.CleanLabel(beacon.Name), packet.RemoteEndPoint.Address.ToString(), beacon.Port);
            }
            catch (Exception error) when (error is JsonException or SocketException)
            {
                // Not one of ours, or the network hiccuped.
            }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Seen(string id, string name, string address, int port)
    {
        bool isNew;
        lock (gate)
        {
            isNew = !peers.TryGetValue(id, out var peer) || !peer.Online || peer.Address != address || peer.Port != port;
            peer ??= peers[id] = new PortalPeer { Id = id };
            // An announcement is not proof of who sent it, so it never renames a PC that is already trusted.
            if (!peer.Trusted || peer.Name.Length == 0)
                peer.Name = name;
            (peer.Address, peer.Port, peer.LastSeen, peer.Tunnel) = (address, port, DateTime.UtcNow, null);
        }
        if (isNew)
            Raise();
    }

    /// <summary>
    /// Look for a PC at an address typed by the user, for networks that keep announcements
    /// from getting through. Returns it, or null if no Portal answers there.
    /// </summary>
    public async Task<PortalPeer?> AddByAddress(string host, int port)
    {
        try
        {
            using var client = Client(host, port, expectedId: null, out var seen);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(6));
            var who = await client.GetFromJsonAsync<JsonElement>($"{Api}/who", limit.Token);
            var id = seen();
            if (id is null || id == Id || who.GetProperty("id").GetString() != id)
                return null;
            Seen(id, Portal.CleanLabel(who.GetProperty("name").GetString() ?? ""), host, port);
            lock (gate)
                return peers[id];
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Trusted PCs whose announcements don't arrive are asked directly now and then; old requests are dropped.</summary>
    private async Task Housekeeping()
    {
        while (!stopping.IsCancellationRequested)
        {
            List<PortalPeer> quiet;
            lock (gate)
                quiet = peers.Values.Where(peer => peer is { Trusted: true, Online: false, Tunnel: null, Address.Length: > 0, Port: > 0 }).ToList();
            RefreshPaths();
            foreach (var peer in quiet)
            {
                try
                {
                    using var client = Client(peer.Address, peer.Port, peer.Id, out _);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(3));
                    using var answer = await client.GetAsync($"{Api}/who", limit.Token);
                    if (answer.IsSuccessStatusCode)
                    {
                        peer.LastSeen = DateTime.UtcNow;
                        Raise();
                    }
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                }
            }
            var changed = false;
            foreach (var job in jobs.Values.Where(job => job.State == "pending" && DateTime.UtcNow - job.Asked > RequestLife))
            {
                (job.State, job.Transfer.State, changed) = ("refused", "refused", true);
            }
            lock (gate)
            {
                foreach (var pairing in pairings.Where(pairing => pairing.State == "waiting" && DateTime.UtcNow - pairing.Started > RequestLife))
                    (pairing.State, changed) = ("failed", true);
            }
            if (changed)
                Raise();
            try
            {
                await Task.Delay(8000, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // --- talking to another PC ---

    /// <summary>
    /// A connection to another PC that shows this PC's certificate and only goes through if
    /// the other end shows the expected one. With no expectation (while pairing) any
    /// certificate is let through and "seen" tells afterwards whose it was.
    /// </summary>
    private HttpClient Client(string host, int port, string? expectedId, out Func<string?> seen, IPEndPoint? via = null)
    {
        string? remote = null;
        seen = () => remote;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(6), UseProxy = false, AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [certificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                RemoteCertificateValidationCallback = (_, shown, _, _) =>
                {
                    if (shown is null)
                        return false;
                    remote = Portal.Fingerprint(shown);
                    return expectedId is null || string.Equals(remote, expectedId, StringComparison.OrdinalIgnoreCase);
                },
            },
        };
        if (via is not null && link is { } path)
        {
            // Over the internet: the same encrypted connection, carried by a stream on the open path instead of a network socket.
            handler.ConnectCallback = async (_, cancel) => await path.Connect(via, cancel);
            return new HttpClient(handler) { BaseAddress = new Uri("https://portal.direct"), Timeout = Timeout.InfiniteTimeSpan };
        }
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}"), Timeout = Timeout.InfiniteTimeSpan };
    }

    // --- pairing ---

    /// <summary>
    /// The six digits both screens show. They depend on both certificates and on a random
    /// part from each side; the starting side fixes its part (by sending only its hash)
    /// before it sees the other's, so neither side, nor anyone between them, can steer the result.
    /// </summary>
    public static string PairingCode(string starterId, string answererId, string starterNonce, string answererNonce)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"pcbutler-portal-pair|{starterId.ToUpperInvariant()}|{answererId.ToUpperInvariant()}|{starterNonce}|{answererNonce}"));
        var number = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return $"{number / 1000:000} {number % 1000:000}";
    }

    private static string HashOf(string nonce) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));

    /// <summary>
    /// Start pairing with a PC from the list. Returns once both sides can show the code;
    /// the user then compares it with the other screen and calls Confirm or Refuse.
    /// Returns null if the other PC can't be reached or doesn't play along.
    /// </summary>
    public async Task<PortalPairing?> Pair(PortalPeer peer)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        try
        {
            using var client = Client(peer.Address, peer.Port, expectedId: null, out var seen, peer.Tunnel);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            using var started = await client.PostAsJsonAsync($"{Api}/pair/start", new { name = Name, port = Port, commit = HashOf(nonce) }, limit.Token);
            var id = seen();
            if (!started.IsSuccessStatusCode || id is null || id == Id)
                return null;
            var answer = await started.Content.ReadFromJsonAsync<JsonElement>(limit.Token);
            var theirs = answer.GetProperty("nonce").GetString() ?? "";
            if (!NoncePattern().IsMatch(theirs))
                return null;
            var pairing = new PortalPairing
            {
                PeerId = id, PeerName = Portal.CleanLabel(answer.GetProperty("name").GetString() ?? peer.Name), Incoming = false,
                Code = PairingCode(Id, id, nonce, theirs), Address = peer.Address, Port = peer.Port, Via = peer.Tunnel,
            };
            // Only now is this side's random part given away: the other side's is already fixed.
            using var revealed = await client.PostAsJsonAsync($"{Api}/pair/reveal", new { nonce }, limit.Token);
            if (!revealed.IsSuccessStatusCode)
                return null;
            lock (gate)
            {
                pairings.RemoveAll(old => old.PeerId == id);
                pairings.Add(pairing);
            }
            Raise();
            return pairing;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The user saw the same code on both screens: trust that PC from now on.</summary>
    public void Confirm(PortalPairing pairing)
    {
        lock (gate)
        {
            if (pairing.State != "waiting" || pairing.Code.Length == 0)
                return;
            pairing.State = "confirmed";
            if (!peers.TryGetValue(pairing.PeerId, out var peer))
                peer = peers[pairing.PeerId] = new PortalPeer { Id = pairing.PeerId, Address = pairing.Address, Port = pairing.Port };
            (peer.Trusted, peer.Name) = (true, pairing.PeerName);
            if (pairing.Via is not null)
                (peer.Tunnel, peer.LastSeen) = (pairing.Via, DateTime.UtcNow);
            else if (pairing.Address.Length > 0 && pairing.Port > 0)
                (peer.Address, peer.Port) = (pairing.Address, pairing.Port);
            settings.Devices.RemoveAll(device => device.Id.Equals(pairing.PeerId, StringComparison.OrdinalIgnoreCase));
            settings.Devices.Add(new PortalDevice { Id = peer.Id, Name = peer.Name, Address = peer.Address, Port = peer.Port });
        }
        save();
        Log.Info($"Courier: paired with {pairing.PeerName}");
        Raise();
    }

    public void Refuse(PortalPairing pairing)
    {
        if (pairing.State == "waiting")
            pairing.State = "refused";
        Raise();
    }

    /// <summary>Stop trusting a PC. It has to be paired again before it can send anything.</summary>
    public void Forget(string id)
    {
        lock (gate)
        {
            if (peers.TryGetValue(id, out var peer))
                (peer.Trusted, peer.AutoAccept) = (false, false);
            settings.Devices.RemoveAll(device => device.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        save();
        Raise();
    }

    public void SetAutoAccept(string id, bool on)
    {
        lock (gate)
        {
            if (!peers.TryGetValue(id, out var peer) || !peer.Trusted)
                return;
            peer.AutoAccept = on;
            foreach (var device in settings.Devices.Where(device => device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                device.AutoAccept = on;
        }
        save();
        Raise();
    }

    // --- sending ---

    /// <summary>
    /// Send files and whole folders to a paired PC. Returns at once with the transfer to
    /// watch; the work happens in the background. Null if nothing in "paths" exists.
    /// </summary>
    public PortalTransfer? Send(PortalPeer peer, IEnumerable<string> paths)
    {
        var files = new List<(string Source, string Path, long Size)>();
        var tops = new List<string>();
        foreach (var path in paths.Select(System.IO.Path.GetFullPath))
        {
            try
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                if (File.Exists(path))
                {
                    files.Add((path, name, new FileInfo(path).Length));
                    tops.Add(name);
                }
                else if (Directory.Exists(path))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(path, "*", options))
                        files.Add((file, name + "/" + System.IO.Path.GetRelativePath(path, file).Replace('\\', '/'), new FileInfo(file).Length));
                    tops.Add(name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (files.Count == 0)
            return null;
        var transfer = new PortalTransfer
        {
            Id = NewId(), Name = tops.Count == 1 ? tops[0] : $"{tops[0]} +{tops.Count - 1}", Size = files.Sum(file => file.Size), Files = files.Count,
            Incoming = false, Peer = peer.Name, State = "waiting",
        };
        lock (gate)
            transfers.Add(transfer);
        Raise();
        _ = Task.Run(() => RunSend(peer, transfer, files));
        return transfer;
    }

    private async Task RunSend(PortalPeer peer, PortalTransfer transfer, List<(string Source, string Path, long Size)> files)
    {
        void Fail(string note)
        {
            if (transfer.Running)
                (transfer.State, transfer.Note) = ("failed", note);
        }

        using var client = Client(peer.Address, peer.Port, peer.Id, out _, peer.Tunnel);
        var stop = stopping.Token;
        try
        {
            var offer = new { id = transfer.Id, name = transfer.Name, files = files.Select(file => new { path = file.Path, size = file.Size }) };
            using (var asked = await client.PostAsJsonAsync($"{Api}/send/offer", offer, stop))
            {
                if (asked.StatusCode == HttpStatusCode.Forbidden)
                {
                    Fail(Tr.T("the other PC no longer trusts this one; pair them again"));
                    return;
                }
                if ((int)asked.StatusCode == 507)
                {
                    Fail(Tr.T("the other PC's disk is too full"));
                    return;
                }
                asked.EnsureSuccessStatusCode();
            }
            // Wait for Accept on the other PC.
            for (var waited = 0; ; waited++)
            {
                if (transfer.Cancelled)
                {
                    await client.DeleteAsync($"{Api}/send/{transfer.Id}", CancellationToken.None);
                    return;
                }
                var state = (await client.GetFromJsonAsync<JsonElement>($"{Api}/send/{transfer.Id}", stop)).GetProperty("state").GetString();
                if (state == "accepted")
                    break;
                if (state != "pending" || waited > RequestLife.TotalSeconds)
                {
                    transfer.State = "refused";
                    return;
                }
                await Task.Delay(1000, stop);
            }
            transfer.State = "active";
            Raise();

            var buffer = new byte[ChunkSize];
            for (var index = 0; index < files.Count; index++)
            {
                var (source, _, size) = files[index];
                await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                long offset = 0;
                var failures = 0;
                do
                {
                    if (transfer.Cancelled)
                    {
                        await client.DeleteAsync($"{Api}/send/{transfer.Id}", CancellationToken.None);
                        return;
                    }
                    var count = await ReadFully(file, buffer, offset, (int)Math.Min(ChunkSize, size - offset), stop);
                    if (count == 0 && offset < size)
                    {
                        Fail(Tr.T("a file changed while it was being sent"));
                        await client.DeleteAsync($"{Api}/send/{transfer.Id}", CancellationToken.None);
                        return;
                    }
                    try
                    {
                        using var piece = new ByteArrayContent(buffer, 0, count);
                        using var answer = await client.PutAsync($"{Api}/send/{transfer.Id}/{index}?offset={offset}", piece, stop);
                        if (answer.StatusCode == HttpStatusCode.Gone)
                        {
                            if (transfer.Running)
                                (transfer.State, transfer.Note) = ("cancelled", Tr.T("stopped on the other PC"));
                            return;
                        }
                        if (answer.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                            throw new HttpRequestException($"status {(int)answer.StatusCode}");
                        // Conflict: the other PC has a different amount (a piece half-arrived before); carry on from there.
                        var received = (await answer.Content.ReadFromJsonAsync<JsonElement>(stop)).GetProperty("received").GetInt64();
                        transfer.Add(received - offset);
                        offset = received;
                        failures = 0;
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException)
                    {
                        if (++failures > 8)
                        {
                            Fail(Tr.T("the connection was lost"));
                            return;
                        }
                        await Task.Delay(Math.Min(8000, 500 << failures), stop);
                    }
                }
                while (offset < size);
            }
            transfer.State = "done";
            Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), transfer.Name, transfer.Size, false, peer.Name, ""));
        }
        catch (OperationCanceledException)
        {
            transfer.Cancel();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            Fail(error is HttpRequestException ? Tr.T("the other PC could not be reached") : error.Message);
        }
        finally
        {
            Raise();
        }
    }

    /// <summary>Fill the buffer from the file at the given place; returns how much was read.</summary>
    private static async Task<int> ReadFully(FileStream file, byte[] buffer, long offset, int count, CancellationToken stop)
    {
        file.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < count)
        {
            var read = await file.ReadAsync(buffer.AsMemory(total, count - total), stop);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    // --- answering other PCs ---

    [GeneratedRegex("^[A-Fa-f0-9]{64}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Fa-f0-9]{32}$")] private static partial Regex NoncePattern();
    [GeneratedRegex("^[a-f0-9]{16,40}$")] private static partial Regex JobPattern();

    private static Task Json(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private async Task Handle(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var shown = context.Connection.ClientCertificate;
        if (!path.StartsWith(Api + "/", StringComparison.Ordinal) || shown is null || stopped)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var caller = Portal.Fingerprint(shown);
        var parts = path[(Api.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = context.Request.Method;
        try
        {
            switch (parts)
            {
                case ["who"] when method == "GET":
                    await Json(context, 200, new { id = Id, name = Name });
                    return;
                case ["pair", "start"] when method == "POST":
                    await PairStart(context, caller);
                    return;
                case ["pair", "reveal"] when method == "POST":
                    await PairReveal(context, caller);
                    return;
            }
            // Everything else is only for PCs this one was paired with.
            PortalPeer? peer;
            lock (gate)
                peers.TryGetValue(caller, out peer);
            if (peer is not { Trusted: true })
            {
                await Json(context, 403, new { paired = false });
                return;
            }
            lock (gate)
            {
                peer.LastSeen = DateTime.UtcNow;
                if (Via(context) is { } across)
                    peer.Tunnel = across;
                else if (context.Connection.RemoteIpAddress is { } from)
                    peer.Address = from.MapToIPv4().ToString();
            }
            switch (parts)
            {
                case ["send", "offer"] when method == "POST":
                    await Offer(context, peer);
                    break;
                case ["send", var id] when JobPattern().IsMatch(id) && jobs.TryGetValue(id, out var job) && job.PeerId == peer.Id:
                    if (method == "DELETE")
                    {
                        job.Transfer.Cancel();
                        if (job.State == "pending")
                            job.State = "refused";
                        await job.Gate.WaitAsync();
                        try
                        {
                            DeleteParts(job);
                        }
                        finally
                        {
                            job.Gate.Release();
                        }
                        Raise();
                    }
                    await Json(context, 200, new { state = job.Transfer.Cancelled ? "cancelled" : job.State });
                    break;
                case ["send", var id, var number] when method == "PUT" && JobPattern().IsMatch(id) && int.TryParse(number, out var index)
                                                       && jobs.TryGetValue(id, out var job) && job.PeerId == peer.Id:
                    await Piece(context, job, index);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            // The other PC went away mid-request, or the disk refused: it asks again.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 500;
        }
    }

    /// <summary>Another PC starts pairing: remember the hash of its random part and answer with this side's.</summary>
    private async Task PairStart(HttpContext context, string caller)
    {
        var body = await context.Request.ReadFromJsonAsync<JsonElement>();
        var commit = body.TryGetProperty("commit", out var value) ? value.GetString() ?? "" : "";
        if (!IdPattern().IsMatch(commit) || caller == Id)
        {
            await Json(context, 400, new { });
            return;
        }
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (gate)
        {
            // One pairing at a time per PC, and only a few waiting in all: nobody can fill the screen with requests.
            pairings.RemoveAll(old => old.PeerId == caller || old.State != "waiting");
            if (pairings.Count >= 5)
            {
                context.Response.StatusCode = 429;
                return;
            }
            pairings.Add(new PortalPairing
            {
                PeerId = caller, PeerName = Portal.CleanLabel(body.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""), Incoming = true,
                Commit = commit, NonceMine = nonce, Via = Via(context),
                Address = Via(context) is null ? context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "" : "",
                // Where it says it listens, so this PC can find it again even if its announcements never arrive.
                Port = body.TryGetProperty("port", out var port) && port.TryGetInt32(out var listens) && listens is > 0 and < 65536 ? listens : PortOf(caller),
            });
        }
        await Json(context, 200, new { name = Name, nonce });
    }

    private int PortOf(string id)
    {
        lock (gate)
            return peers.TryGetValue(id, out var peer) ? peer.Port : 0;
    }

    /// <summary>The other PC gives its random part away. If it matches the hash sent first, the code is known and the user is asked.</summary>
    private async Task PairReveal(HttpContext context, string caller)
    {
        var body = await context.Request.ReadFromJsonAsync<JsonElement>();
        var nonce = body.TryGetProperty("nonce", out var value) ? value.GetString() ?? "" : "";
        PortalPairing? pairing;
        lock (gate)
            pairing = pairings.FirstOrDefault(item => item.PeerId == caller && item is { Incoming: true, State: "waiting", Code.Length: 0 });
        if (pairing is null || !NoncePattern().IsMatch(nonce) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashOf(nonce)), Encoding.ASCII.GetBytes(pairing.Commit.ToUpperInvariant())))
        {
            if (pairing is not null)
                pairing.State = "failed";
            await Json(context, 400, new { });
            return;
        }
        pairing.Code = PairingCode(caller, Id, nonce, pairing.NonceMine);
        await Json(context, 200, new { });
        Raise();
        Attention?.Invoke(Tr.T("{0} wants to pair with this PC.", pairing.PeerName));
    }

    /// <summary>A paired PC says what it wants to send. It waits until the user accepts, unless that PC is accepted automatically.</summary>
    private async Task Offer(HttpContext context, PortalPeer peer)
    {
        var body = await context.Request.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString() ?? "";
        var files = new List<(string, long)>();
        foreach (var item in body.GetProperty("files").EnumerateArray())
        {
            var (path, size) = (Portal.SafePath(item.GetProperty("path").GetString() ?? ""), item.GetProperty("size").GetInt64());
            if (size < 0 || files.Count >= 200_000)
            {
                await Json(context, 400, new { });
                return;
            }
            files.Add((path, size));
        }
        if (!JobPattern().IsMatch(id) || files.Count == 0 || jobs.Values.Count(job => job.PeerId == peer.Id && job.State == "pending") >= 10)
        {
            await Json(context, 400, new { });
            return;
        }
        var total = files.Sum(file => file.Item2);
        if (new DriveInfo(System.IO.Path.GetPathRoot(Folder)!).AvailableFreeSpace < total + (64L << 20))
        {
            await Json(context, 507, new { full = true });
            return;
        }
        var transfer = new PortalTransfer
        {
            Id = id, Name = Portal.CleanLabel(body.TryGetProperty("name", out var name) ? name.GetString() ?? "" : files[0].Item1), Size = total,
            Files = files.Count, Incoming = true, Peer = peer.Name, State = "waiting",
        };
        var job = new IncomingJob { Id = id, PeerId = peer.Id, Files = files, Transfer = transfer };
        if (!jobs.TryAdd(id, job))
        {
            await Json(context, 409, new { });
            return;
        }
        lock (gate)
            transfers.Add(transfer);
        if (peer.AutoAccept)
            Accept(id);
        else
            Attention?.Invoke(Tr.T("{0} wants to send {1} file(s).", peer.Name, files.Count));
        await Json(context, 200, new { state = job.State });
        Raise();
    }

    /// <summary>Take the files a paired PC offered.</summary>
    public void Accept(string requestId)
    {
        if (!jobs.TryGetValue(requestId, out var job) || job.State != "pending")
            return;
        (job.State, job.Transfer.State) = ("accepted", "active");
        Raise();
    }

    public void Refuse(string requestId)
    {
        if (!jobs.TryGetValue(requestId, out var job) || job.State != "pending")
            return;
        (job.State, job.Transfer.State) = ("refused", "refused");
        Raise();
    }

    private string PartFile(IncomingJob job, int index) => System.IO.Path.Combine(Folder, ".portal-parts", $"{job.Id}-{index}.part");

    private void DeleteParts(IncomingJob job)
    {
        for (var index = 0; index < job.Files.Count; index++)
        {
            if (!job.Finished.Contains(index))
                Portal.TryDelete(PartFile(job, index));
        }
    }

    /// <summary>
    /// One piece of one file. It must start exactly where the last one ended; if it doesn't,
    /// the answer says where to continue, which is how a broken transfer picks up again.
    /// </summary>
    private async Task Piece(HttpContext context, IncomingJob job, int index)
    {
        if (job.Transfer.Cancelled || job.State == "refused")
        {
            await Json(context, 410, new { });
            return;
        }
        if (job.State != "accepted" || index < 0 || index >= job.Files.Count || !long.TryParse(context.Request.Query["offset"], out var offset) || offset < 0)
        {
            await Json(context, 400, new { });
            return;
        }
        var (path, size) = job.Files[index];
        await job.Gate.WaitAsync(context.RequestAborted);
        try
        {
            if (job.Finished.Contains(index))
            {
                await Json(context, 200, new { received = size, done = true });
                return;
            }
            var partFile = PartFile(job, index);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(partFile)!);
            long length;
            var tooMuch = false;
            await using (var part = new FileStream(partFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                if (offset != part.Length)
                {
                    await Json(context, 409, new { received = part.Length });
                    return;
                }
                part.Seek(0, SeekOrigin.End);
                var buffer = new byte[1 << 17];
                var room = size - part.Length;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (job.Transfer.Cancelled)
                        break;
                    if (read > room)
                    {
                        // More than it said it would send.
                        tooMuch = true;
                        break;
                    }
                    await part.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    room -= read;
                    job.Transfer.Add(read);
                }
                await part.FlushAsync();
                length = part.Length;
            }
            if (tooMuch)
            {
                (job.Transfer.State, job.Transfer.Note) = ("failed", Tr.T("the other PC sent more than it announced"));
                DeleteParts(job);
                Raise();
                await Json(context, 410, new { });
                return;
            }
            if (job.Transfer.Cancelled)
            {
                DeleteParts(job);
                await Json(context, 410, new { });
                return;
            }
            if (length == size)
            {
                var destination = Destination(job, path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                File.Move(partFile, destination);
                job.Finished.Add(index);
                if (job.Finished.Count == job.Files.Count)
                {
                    job.Transfer.State = "done";
                    job.Transfer.Path = job.Tops.Count == 1 ? job.Tops.Values.First() : Folder;
                    Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), job.Transfer.Name, job.Transfer.Size, true, job.Transfer.Peer, job.Transfer.Path));
                    Log.Info($"Courier received {job.Transfer.Name} from {job.Transfer.Peer}");
                    Raise();
                }
            }
            await Json(context, 200, new { received = length, done = length == size });
        }
        finally
        {
            job.Gate.Release();
        }
    }

    /// <summary>
    /// Where a received file goes. Each top-level name (a file, or a folder being sent) gets
    /// a place of its own in the Portal folder that never replaces anything already there;
    /// everything under a sent folder keeps its place inside it.
    /// </summary>
    private string Destination(IncomingJob job, string path)
    {
        var segments = path.Split('/');
        if (!job.Tops.TryGetValue(segments[0], out var top))
            job.Tops[segments[0]] = top = Sorter.UniqueDestination(Folder, segments[0]);
        return segments.Length == 1 ? top : System.IO.Path.Combine([top, .. segments[1..]]);
    }
}

/// <summary>What Portal needs around a running node: addresses, the receive folder, the firewall, the log, the certificate.</summary>
public static partial class Portal
{
    public const string FirewallRule = "PC Butler Portal";

    /// <summary>The node that is running, if any. There is one at a time.</summary>
    public static PortalNode? Current { get; set; }

    public static string ReceiveFolder(PortalSettings settings)
    {
        if (settings.Folder.Length > 0)
            return settings.Folder;
        // The feature was first called Portal; a folder made under that name stays in use.
        var old = System.IO.Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Portal");
        return Directory.Exists(old) ? old : System.IO.Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Courier");
    }

    public static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

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

    /// <summary>Where to send "here I am" so every PC on the same network hears it: the network's own broadcast address, and the general one.</summary>
    public static List<IPAddress> BroadcastAddresses(IPAddress local)
    {
        var targets = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!address.Address.Equals(local) || address.IPv4Mask is null)
                    continue;
                var (ip, mask) = (address.Address.GetAddressBytes(), address.IPv4Mask.GetAddressBytes());
                targets.Add(new IPAddress(ip.Select((part, index) => (byte)(part | ~mask[index])).ToArray()));
            }
        }
        if (IPAddress.IsLoopback(local))
            targets.Add(IPAddress.Loopback);
        else
            targets.Add(IPAddress.Broadcast);
        return targets.Distinct().ToList();
    }

    /// <summary>
    /// True if Windows marks a connected network as Public (a café, an airport). The firewall
    /// rule Portal asks for covers Private networks only, so on a Public one other PCs can't reach it.
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

    /// <summary>True if the Windows firewall already lets other PCs reach this copy of the program. Needs no special rights.</summary>
    public static bool FirewallAllows(string? program = null) =>
        Netsh("advfirewall", "firewall", "show", "rule", $"name={FirewallRule}", "verbose")
            .Contains(program ?? Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Let PCs on Private networks reach this program (administrator only; the helper calls
    /// it). The rule names this program's own file, so it opens nothing for anything else.
    /// </summary>
    public static bool AllowThroughFirewall()
    {
        var program = Environment.ProcessPath!;
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Netsh("advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", $"program={program}",
              "profile=private", "enable=yes");
        return FirewallAllows(program);
    }

    [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$", RegexOptions.IgnoreCase)] private static partial Regex Reserved();
    [GeneratedRegex(@"[\x00-\x1f\x7f]")] private static partial Regex Control();

    /// <summary>
    /// A file name from another PC, made safe to create here: no folders, no characters or
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

    /// <summary>
    /// A path inside a sent folder ("Photos/2024/a.jpg"), each part made safe on its own. A
    /// part like ".." cannot survive, so nothing is ever written outside the Portal folder.
    /// </summary>
    public static string SafePath(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).DefaultIfEmpty("file"));

    /// <summary>A PC's or a transfer's name as sent by another PC, fit to show: one line, not too long.</summary>
    public static string CleanLabel(string text)
    {
        text = Control().Replace(text, " ").Trim();
        return text.Length == 0 ? "PC" : text.Length > 60 ? text[..60] : text;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
            Log.Warning($"Could not save Courier's history: {error.Message}");
        }
    }

    /// <summary>Pieces of files that never finished, from an earlier run: cleared when Portal starts.</summary>
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

    // --- this PC's certificate ---

    private static string CertificateFile => System.IO.Path.Combine(AppInfo.DataDir, "portal-certificate.bin");

    /// <summary>
    /// This PC's own certificate, made once and kept (its key protected by Windows for this
    /// user). Its fingerprint is the PC's id: paired PCs recognise each other by it, so
    /// losing the file means pairing again.
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
        var exported = NewCertificate();
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllBytes(CertificateFile, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warning($"Courier's certificate could not be saved: {error.Message}");
        }
        return X509CertificateLoader.LoadPkcs12(exported, null);
    }

    /// <summary>A fresh certificate with its key, as PKCS#12 bytes. Valid for a long time: it is an identity, not a promise from an authority.</summary>
    public static byte[] NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=PC Butler Courier ({Environment.MachineName})", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));
        return made.Export(X509ContentType.Pkcs12);
    }
}
