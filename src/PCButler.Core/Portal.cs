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

namespace PCButler.Core;

/// <summary>A PC that was paired with this one. Its id is the fingerprint of its certificate, which is what "trusted" means.</summary>
public sealed class PortalDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Take what it sends without asking each time.</summary>
    [JsonPropertyName("auto_accept")] public bool AutoAccept { get; set; }
    /// <summary>Its internet address when last connected ("1.2.3.4:5000"), tried first the next time before looking it up.</summary>
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
}

/// <summary>How Courier behaves, and the PCs it trusts.</summary>
public sealed class PortalSettings
{
    /// <summary>Where received files go; "" = "PC Butler Courier" inside Downloads.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    /// <summary>The PCs this one was paired with: it trusts them, by their certificate.</summary>
    [JsonPropertyName("devices")] public List<PortalDevice> Devices { get; set; } = [];
    /// <summary>True once Courier was switched on here: from then on it starts with PC Butler (when AutoStart is on).</summary>
    [JsonPropertyName("used")] public bool Used { get; set; }
    /// <summary>Start Courier with PC Butler, so this PC can be reached, and stays connected to its PCs, without a click.</summary>
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; } = true;
    /// <summary>Show the small Courier window, for dropping files and watching transfers, while connected.</summary>
    [JsonPropertyName("widget")] public bool Widget { get; set; } = true;
    /// <summary>The list of PCs: the ones added by their ID and the ones paired with, kept to connect again later.</summary>
    [JsonPropertyName("saved")] public List<PortalSaved> Saved { get; set; } = [];
    /// <summary>What happens when a new PC asks to connect: "ask" (the user allows or refuses it) or "password".</summary>
    [JsonPropertyName("incoming")] public string Incoming { get; set; } = Ask;
    /// <summary>The password new PCs must give, as Portal.HashPassword made it; "" = none set.</summary>
    [JsonPropertyName("password")] public string Password { get; set; } = "";

    /// <summary>Before 1.22 a PC was the host or a client ("host", "client"); read once, to bring its settings over.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    /// <summary>Before 1.22: the host a client connected to.</summary>
    [JsonPropertyName("host_number")] public string HostNumber { get; set; } = "";

    public const string Ask = "ask", WithPassword = "password";
}

/// <summary>A PC on the list, by its ID, under the name the user gave it.</summary>
public sealed class PortalSaved
{
    /// <summary>Its ID, the six digits.</summary>
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Kept connected: looked for whenever Courier runs, and found again when the link drops, until Disconnect.</summary>
    [JsonPropertyName("stay")] public bool Stay { get; set; }
    /// <summary>Before 1.22: connected to when Courier started. Read once, it becomes Stay.</summary>
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
}

/// <summary>Another PC running Courier, as this one sees it right now.</summary>
public sealed class PortalPeer
{
    public required string Id { get; init; }
    public string Name { get; internal set; } = "";
    public DateTime LastSeen { get; internal set; }
    public bool Trusted { get; internal set; }
    public bool AutoAccept { get; internal set; }
    /// <summary>Its address at the other end of the open path across the internet; null while no path is open.</summary>
    public IPEndPoint? Tunnel { get; internal set; }
    public bool Online => Tunnel is not null && (DateTime.UtcNow - LastSeen).TotalSeconds < 40;
    public string Number => Portal.NumberOf(Id);
}

/// <summary>One batch of files on its way in or out, for the page to show.</summary>
public sealed class PortalTransfer
{
    private readonly System.Diagnostics.Stopwatch moving = new();
    private long done;
    private int cancelled;
    private int paused;
    private bool pausedThere;
    private string state = "active";
    private long sampleAt = Environment.TickCount64, sampleDone;
    private double rate;

    public PortalTransfer() => moving.Start();

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required int Files { get; init; }
    /// <summary>True: the other PC is sending it here.</summary>
    public required bool Incoming { get; init; }
    public required string Peer { get; init; }
    /// <summary>The other PC's id.</summary>
    internal string PeerId { get; init; } = "";
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>"waiting" (for the other PC to accept), "active", "done", "failed", "refused" or "cancelled".</summary>
    public string State
    {
        get => state;
        internal set
        {
            state = value;
            Clock();
        }
    }
    /// <summary>Why it failed, in the user's language.</summary>
    public string Note { get; internal set; } = "";
    /// <summary>Where the received files ended up.</summary>
    public string Path { get; internal set; } = "";
    public long Done => Interlocked.Read(ref done);
    public bool Cancelled => Volatile.Read(ref cancelled) != 0;
    public bool Running => State is "waiting" or "active";
    /// <summary>Held on this PC until Resume.</summary>
    public bool Paused => Volatile.Read(ref paused) != 0;
    /// <summary>Held on the other PC; only its user can let it go on.</summary>
    public bool PausedThere
    {
        get => pausedThere;
        internal set
        {
            pausedThere = value;
            Clock();
        }
    }
    /// <summary>How long it has been moving: waiting for an answer and pauses are not counted.</summary>
    public TimeSpan Elapsed => moving.Elapsed;
    /// <summary>Bytes a second over the last few seconds; 0 while nothing moves.</summary>
    public double Speed => State == "active" && !Paused && !PausedThere && Environment.TickCount64 - Volatile.Read(ref sampleAt) < 5000 ? rate : 0;
    /// <summary>How long the rest takes at the present speed; null when that can't be told.</summary>
    public TimeSpan? Remaining => Speed is > 0 and var speed && State == "active" ? TimeSpan.FromSeconds(Math.Max(0, Size - Done) / speed) : null;

    /// <summary>The request carrying the piece that is on its way, so a pause takes effect at once.</summary>
    internal CancellationTokenSource? Flight { get; set; }

    internal void Add(long bytes)
    {
        var total = Interlocked.Add(ref done, bytes);
        var now = Environment.TickCount64;
        lock (moving)
        {
            var since = now - sampleAt;
            if (since < 1000)
                return;
            // A long gap (a pause, a lost connection) says nothing about the speed now.
            var latest = since > 5000 ? 0 : Math.Max(0, total - sampleDone) * 1000.0 / since;
            rate = rate <= 0 || latest <= 0 ? latest : rate * 0.7 + latest * 0.3;
            (sampleAt, sampleDone) = (now, total);
        }
    }

    private void Clock()
    {
        lock (moving)
        {
            if (state == "active" && !Paused && !pausedThere)
                moving.Start();
            else
                moving.Stop();
        }
    }

    /// <summary>Stop it from this PC. The other PC is told with its next request.</summary>
    public void Cancel()
    {
        if (!Running)
            return;
        Volatile.Write(ref cancelled, 1);
        State = "cancelled";
        try
        {
            Flight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Hold it where it is. What has arrived stays, and Resume carries on from there.</summary>
    public void Pause()
    {
        if (State != "active" || Paused)
            return;
        Volatile.Write(ref paused, 1);
        Clock();
        try
        {
            Flight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Resume()
    {
        Volatile.Write(ref paused, 0);
        lock (moving)
            (sampleAt, sampleDone, rate) = (Environment.TickCount64, Done, 0);
        Clock();
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
/// Courier: files between PCs running PC Butler on different networks, across the internet.
///
/// Every PC is the same: it has its own ID, can be reached by it, and connects to the PCs on
/// its list by theirs, as many as it likes at once. Files go straight between two connected
/// PCs. Every connection is encrypted, and each PC proves who it is with its own
/// certificate; a PC's id is that certificate's fingerprint.
///
/// Two PCs must be paired before anything can be sent. The PC that connects (its user typed
/// the other's ID) trusts the other at once; the other PC's user allows it, or a password
/// lets it in. Pairing shows the same six-digit code on both screens, made in a way nobody
/// in between can steer (each side commits to its random part before seeing the other's).
/// After that the two recognise each other by certificate.
///
/// Nothing is open to the local network: the encrypted connection is only ever carried
/// over the paths across the internet, and nothing listens while Courier is off.
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
        /// <summary>Where it is written: the receive folder.</summary>
        public required string Root { get; init; }
    }

    private readonly X509Certificate2 certificate;
    private readonly PortalSettings settings;
    private readonly Action save;
    private readonly object gate = new();
    private readonly Dictionary<string, PortalPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PortalTransfer> transfers = [];
    private readonly List<PortalPairing> pairings = [];
    private readonly ConcurrentDictionary<string, IncomingJob> jobs = new();
    private readonly CancellationTokenSource stopping = new();
    private bool stopped;

    /// <summary>This PC's id: the fingerprint of its certificate.</summary>
    public string Id { get; }
    public string Name { get; } = Portal.DeviceName;
    public string Folder { get; }
    /// <summary>Something changed: devices, pairings, requests or transfers. Raised on a background thread.</summary>
    public event Action? Changed;
    /// <summary>Another PC is asking for something the user must answer (a pairing, or files to accept).</summary>
    public event Action<string>? Attention;
    /// <summary>Files from another PC arrived, all of them: the transfer says what and where. Raised on a background thread.</summary>
    public event Action<PortalTransfer>? Received;

    private PortalNode(X509Certificate2 certificate, PortalSettings settings, Action save)
    {
        (this.certificate, this.settings, this.save) = (certificate, settings, save);
        Id = Portal.Fingerprint(certificate);
        Folder = Portal.ReceiveFolder(settings);
        PortalSavedList.Start(settings);
        foreach (var device in settings.Devices)
            peers[device.Id] = new PortalPeer { Id = device.Id, Name = device.Name, Trusted = true, AutoAccept = device.AutoAccept };
    }

    /// <summary>
    /// Start Courier. "save" is called when the trusted PCs or the list change, to store the
    /// settings. local: true keeps the paths on this PC's loopback address and leaves out the
    /// public directory and the router (the tests do).
    /// </summary>
    public static Task<PortalNode> Start(PortalSettings settings, Action? save = null, X509Certificate2? certificate = null, bool local = false)
    {
        certificate ??= Portal.Certificate();
        Directory.CreateDirectory(Portal.ReceiveFolder(settings));
        // Nothing listens on a network port: requests arrive over the paths across the internet, and are answered right there.
        var node = new PortalNode(certificate, settings, save ?? (() => { }));
        node.StartInternet(local);
        _ = Task.Run(node.Housekeeping);
        Log.Info($"Courier started, ID {node.Number}");
        return Task.FromResult(node);
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
        await StopInternet();
        save();
        foreach (var transfer in Transfers)
            transfer.Cancel();
        Log.Info("Courier stopped");
        Changed?.Invoke();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    private void Raise() => Changed?.Invoke();

    /// <summary>Paths that closed are noticed, and requests nobody answered are dropped.</summary>
    private async Task Housekeeping()
    {
        while (!stopping.IsCancellationRequested)
        {
            RefreshPaths();
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
    private HttpClient Client(IPEndPoint via, string? expectedId, out Func<string?> seen)
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
        // The encrypted connection, carried by a stream on the open path across the internet.
        handler.ConnectCallback = async (_, cancel) => await (LinkFor(via) ?? throw new IOException("Courier is off.")).Connect(via, cancel);
        return new HttpClient(handler) { BaseAddress = new Uri("https://portal.direct"), Timeout = Timeout.InfiniteTimeSpan };
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
        if (peer.Tunnel is not { } via)
            return null;
        var attempt = AttemptFor(peer.Number);
        try
        {
            using var client = Client(via, expectedId: null, out var seen);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            using var started = await client.PostAsJsonAsync($"{Api}/pair/start", new { name = Name, commit = HashOf(nonce) }, limit.Token);
            var id = seen();
            if (!started.IsSuccessStatusCode || id is null || id == Id || !string.Equals(id, peer.Id, StringComparison.OrdinalIgnoreCase))
                return null;
            var answer = await started.Content.ReadFromJsonAsync<JsonElement>(limit.Token);
            var theirs = answer.GetProperty("nonce").GetString() ?? "";
            if (!NoncePattern().IsMatch(theirs))
                return null;
            var pairing = new PortalPairing
            {
                PeerId = id, PeerName = Portal.CleanLabel(answer.GetProperty("name").GetString() ?? peer.Name), Incoming = false,
                Code = PairingCode(Id, id, nonce, theirs), Via = via,
            };
            // Only now is this side's random part given away: the other side's is already fixed.
            // The password goes with it when the other PC wants one (the connection is already encrypted, and its certificate checked).
            using var revealed = await client.PostAsJsonAsync($"{Api}/pair/reveal", new { nonce, password = attempt.Password }, limit.Token);
            if (revealed.StatusCode == HttpStatusCode.Unauthorized)
            {
                attempt.PasswordAsk = attempt.Password.Length == 0 ? PortalPasswordAsk.Needed : PortalPasswordAsk.Wrong;
                attempt.Password = "";
                Raise();
                Attention?.Invoke(Tr.T("{0} asks for a password.", peer.Name));
                return null;
            }
            if (!revealed.IsSuccessStatusCode)
                return null;
            attempt.PasswordAsk = PortalPasswordAsk.None;
            attempt.Password = "";
            lock (gate)
            {
                pairings.RemoveAll(old => old.PeerId == id);
                pairings.Add(pairing);
            }
            // This side asked for it, by the other PC's own ID (which its certificate proves), so it trusts that PC at once.
            // The other PC decides on its side: its user allows it, or the password let it in already.
            Confirm(pairing);
            var reply = await revealed.Content.ReadAsStringAsync(limit.Token);
            attempt.Waiting = !reply.Contains("\"paired\":true", StringComparison.Ordinal);
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
                peer = peers[pairing.PeerId] = new PortalPeer { Id = pairing.PeerId };
            // Paired means trusted: what it sends is taken without a click (it can be switched to asking first).
            (peer.Trusted, peer.Name, peer.AutoAccept) = (true, pairing.PeerName, true);
            if (pairing.Via is not null)
                (peer.Tunnel, peer.LastSeen) = (pairing.Via, DateTime.UtcNow);
            // Every PC paired with is on the list, whichever side asked, so it can be connected to again later.
            settings.Devices.RemoveAll(device => device.Id.Equals(pairing.PeerId, StringComparison.OrdinalIgnoreCase));
            settings.Devices.Add(new PortalDevice { Id = peer.Id, Name = peer.Name, AutoAccept = true, Address = peer.Tunnel?.Address.ToString() ?? "", Port = peer.Tunnel?.Port ?? 0 });
            peer.Name = settings.Devices[^1].Name = PortalSavedList.Remember(settings, peer.Number, pairing.PeerName);
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
            Incoming = false, State = "waiting", Peer = peer.Name, PeerId = peer.Id,
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

        if (peer.Tunnel is not { } via)
        {
            Fail(Tr.T("the other PC is not connected"));
            Raise();
            return;
        }
        using var client = Client(via, peer.Id, out _);
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
                    if (transfer.Paused)
                    {
                        // Held on this PC: say so to the other one, and wait here until Resume or Cancel.
                        await Hold(client, transfer, true);
                        while (transfer.Paused && !transfer.Cancelled)
                            await Task.Delay(200, stop);
                        if (!transfer.Cancelled)
                            await Hold(client, transfer, false);
                    }
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
                        using var flight = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        transfer.Flight = flight;
                        using var answer = await client.PutAsync($"{Api}/send/{transfer.Id}/{index}?offset={offset}", piece, flight.Token);
                        transfer.Flight = null;
                        if (answer.StatusCode == HttpStatusCode.Locked)
                        {
                            // Held on the other PC: ask now and then until its user lets it go on.
                            transfer.PausedThere = true;
                            Raise();
                            while (!transfer.Cancelled && !transfer.Paused
                                   && (await client.GetFromJsonAsync<JsonElement>($"{Api}/send/{transfer.Id}", stop)).GetProperty("state").GetString() == "paused")
                                await Task.Delay(1000, stop);
                            transfer.PausedThere = false;
                            Raise();
                            continue;
                        }
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
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested && (transfer.Paused || transfer.Cancelled))
                    {
                        // Pause or Cancel cut the piece short; what arrived of it stays on the other PC.
                        transfer.Flight = null;
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
            Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), transfer.Name, transfer.Size, false, transfer.Peer, ""));
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

    /// <summary>Tell the receiving PC that the sender holds this transfer, or lets it go on. A PC that does not know the request is not troubled.</summary>
    private async Task Hold(HttpClient client, PortalTransfer transfer, bool on)
    {
        try
        {
            using var answer = await client.PostAsync($"{Api}/send/{transfer.Id}/hold?on={(on ? 1 : 0)}", null, stopping.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
        }
        Raise();
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

    private static Task Json(PortalExchange context, int status, object body) => context.Json(status, body);

    private async Task Handle(PortalExchange context)
    {
        var path = context.Path;
        var shown = context.ClientCertificate;
        if (!path.StartsWith(Api + "/", StringComparison.Ordinal) || shown is null || stopped)
        {
            context.Fail(404);
            return;
        }
        var caller = Portal.Fingerprint(shown);
        var parts = path[(Api.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = context.Method;
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
                if (context.Via is { } across)
                    peer.Tunnel = across;
            }
            switch (parts)
            {
                case ["ping"] when method == "GET":
                    // A PC that connected here asks now and then: the answer says it is let in, and keeps it shown as connected.
                    await Json(context, 200, new { });
                    break;
                case ["disconnect"] when method == "POST":
                    Disconnected(peer, context.Via, forget: context.Query("forget") == "1");
                    await Json(context, 200, new { });
                    break;
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
                    await Json(context, 200, new { state = job.Transfer.Cancelled ? "cancelled" : job.Transfer.Paused && job.State == "accepted" ? "paused" : job.State });
                    break;
                case ["send", var id, "hold"] when method == "POST" && JobPattern().IsMatch(id) && jobs.TryGetValue(id, out var job) && job.PeerId == peer.Id:
                    job.Transfer.PausedThere = context.Query("on") == "1";
                    await Json(context, 200, new { });
                    Raise();
                    break;
                case ["send", var id, var number] when method == "PUT" && JobPattern().IsMatch(id) && int.TryParse(number, out var index)
                                                       && jobs.TryGetValue(id, out var job) && job.PeerId == peer.Id:
                    await Piece(context, job, index);
                    break;
                default:
                    context.Fail(404);
                    break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            // The other PC went away mid-request, or the disk refused: it asks again.
            context.Fail(500);
        }
    }

    /// <summary>Another PC starts pairing: remember the hash of its random part and answer with this side's.</summary>
    private async Task PairStart(PortalExchange context, string caller)
    {
        var body = await context.ReadJson();
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
                context.Fail(429);
                return;
            }
            pairings.Add(new PortalPairing
            {
                PeerId = caller, PeerName = Portal.CleanLabel(body.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""), Incoming = true,
                Commit = commit, NonceMine = nonce, Via = context.Via,
            });
        }
        await Json(context, 200, new { name = Name, nonce });
    }

    /// <summary>The other PC gives its random part away. If it matches the hash sent first, the code is known and the user is asked.</summary>
    private async Task PairReveal(PortalExchange context, string caller)
    {
        var body = await context.ReadJson();
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
        if (settings.Incoming == PortalSettings.WithPassword)
        {
            // With a password, the right one lets the PC in at once, and anything else turns it away.
            var given = body.TryGetProperty("password", out var typed) && typed.ValueKind == JsonValueKind.String ? typed.GetString() ?? "" : "";
            if (!PasswordTries.Allow(caller) || !Portal.CheckPassword(given, settings.Password))
            {
                pairing.State = "failed";
                if (given.Length > 0)
                    PasswordTries.Failed(caller);
                Log.Info($"Courier: {pairing.PeerName} gave {(given.Length == 0 ? "no" : "a wrong")} password");
                await Json(context, 401, new { password = given.Length == 0 ? "needed" : "wrong" });
                Raise();
                return;
            }
            pairing.Code = PairingCode(caller, Id, nonce, pairing.NonceMine);
            Confirm(pairing);
            await Json(context, 200, new { paired = true });
            return;
        }
        pairing.Code = PairingCode(caller, Id, nonce, pairing.NonceMine);
        await Json(context, 200, new { });
        Raise();
        Attention?.Invoke(Tr.T("{0} wants to connect to this PC.", pairing.PeerName));
    }

    /// <summary>A paired PC says what it wants to send. It waits until the user accepts, unless that PC is accepted automatically.</summary>
    private async Task Offer(PortalExchange context, PortalPeer peer)
    {
        var body = await context.ReadJson();
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
        var root = Folder;
        var total = files.Sum(file => file.Item2);
        Directory.CreateDirectory(root);
        if (Portal.FreeSpace(root) < total + (64L << 20))
        {
            await Json(context, 507, new { full = true });
            return;
        }
        var transfer = new PortalTransfer
        {
            Id = id, Name = Portal.CleanLabel(body.TryGetProperty("name", out var name) ? name.GetString() ?? "" : files[0].Item1), Size = total,
            Files = files.Count, Incoming = true, State = "waiting", Peer = peer.Name, PeerId = peer.Id,
        };
        var job = new IncomingJob { Id = id, PeerId = peer.Id, Files = files, Transfer = transfer, Root = root };
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

    private static string PartFile(IncomingJob job, int index) => System.IO.Path.Combine(job.Root, ".portal-parts", $"{job.Id}-{index}.part");

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
    private async Task Piece(PortalExchange context, IncomingJob job, int index)
    {
        if (job.Transfer.Cancelled || job.State == "refused")
        {
            await Json(context, 410, new { });
            return;
        }
        if (job.Transfer.Paused)
        {
            await Json(context, 423, new { });
            return;
        }
        job.Transfer.PausedThere = false;
        if (job.State != "accepted" || index < 0 || index >= job.Files.Count || !long.TryParse(context.Query("offset"), out var offset) || offset < 0)
        {
            await Json(context, 400, new { });
            return;
        }
        var (path, size) = job.Files[index];
        await job.Gate.WaitAsync(context.Aborted);
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
                while ((read = await context.Body.ReadAsync(buffer, context.Aborted)) > 0)
                {
                    if (job.Transfer.Cancelled || job.Transfer.Paused)
                        break;
                    if (read > room)
                    {
                        // More than it said it would send.
                        tooMuch = true;
                        break;
                    }
                    await part.WriteAsync(buffer.AsMemory(0, read), context.Aborted);
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
            if (job.Transfer.Paused && length < size)
            {
                // Held in the middle of a piece: what arrived is kept, and the sender carries on from there.
                await Json(context, 423, new { });
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
                    job.Transfer.Path = job.Tops.Count == 1 ? job.Tops.Values.First() : job.Root;
                    Portal.AddToLog(new PortalLogEntry(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), job.Transfer.Name, job.Transfer.Size, true, job.Transfer.Peer, job.Transfer.Path));
                    Log.Info($"Courier received {job.Transfer.Name} from {job.Transfer.Peer}");
                    Raise();
                    Received?.Invoke(job.Transfer);
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
            job.Tops[segments[0]] = top = Sorter.UniqueDestination(job.Root, segments[0]);
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

    /// <summary>This device's name as other PCs see it.</summary>
    public static string DeviceName =>
#if ANDROID
        $"{(global::Android.OS.Build.Manufacturer)} {(global::Android.OS.Build.Model)}".Trim();
#else
        Environment.MachineName;
#endif

    private static string Netsh(params string[] arguments)
    {
#if !WINDOWS
        return "";
#else
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
#endif
    }

    /// <summary>True if the Windows firewall already lets other PCs reach this copy of the program. Needs no special rights.</summary>
    public static bool FirewallAllows(string? program = null) =>
        Netsh("advfirewall", "firewall", "show", "rule", $"name={FirewallRule}", "verbose")
            .Contains(program ?? Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Let clients across the internet reach the Courier host (administrator only; the helper
    /// calls it). The rule names this program's own file and UDP only, so it opens nothing for
    /// anything else; it covers every kind of network, because a host may be on any.
    /// </summary>
    public static bool AllowThroughFirewall()
    {
        var program = Environment.ProcessPath!;
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Netsh("advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", $"program={program}",
              "protocol=UDP", "profile=any", "enable=yes");
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

    /// <summary>Room left where a folder is (on a phone, the drive root is not where the app's files live, so the folder itself is asked).</summary>
    public static long FreeSpace(string folder)
    {
#if ANDROID
        return new global::Android.OS.StatFs(folder).AvailableBytes;
#else
        return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!).AvailableFreeSpace;
#endif
    }

    /// <summary>Before 1.22 a host kept here, for a moment, files one client sent to another; whatever was left is removed.</summary>
    public static void ClearRelay() => TryDeleteFolder(System.IO.Path.Combine(AppInfo.DataDir, "courier-relay"));

    internal static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
                var saved = Unprotect(File.ReadAllBytes(CertificateFile));
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
            File.WriteAllBytes(CertificateFile, Protect(exported));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warning($"Courier's certificate could not be saved: {error.Message}");
        }
        return X509CertificateLoader.LoadPkcs12(exported, null);
    }

#if WINDOWS
    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
#else
    // The app's own folder can't be read by other apps on Android.
    private static byte[] Protect(byte[] data) => data;
    private static byte[] Unprotect(byte[] data) => data;
#endif

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
