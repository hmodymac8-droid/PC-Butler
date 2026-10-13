// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCButler.Core;

/// <summary>Why connecting to the host by its ID did not work.</summary>
public enum PortalConnectProblem
{
    None,
    /// <summary>Not an ID (six characters), or this PC's own.</summary>
    BadNumber,
    /// <summary>The public directory could not be reached from this network.</summary>
    NoDirectory,
    /// <summary>Nobody is announced under that ID: wrong number, or the host is not running Courier right now.</summary>
    NotFound,
    /// <summary>The host was found, but the two networks would not let a direct link through.</summary>
    NoPath,
}

/// <summary>
/// How Courier's PCs reach each other across the internet. Each PC has an ID number made
/// from its certificate. The host announces itself under its ID in the BitTorrent
/// network's public directory, over IPv4 and IPv6, and asks its router to forward a port
/// to it (UPnP or NAT-PMP) so clients can get straight through. A client looks the ID up,
/// both send to each other until a path through their routers opens (needed only when the
/// router would not forward a port), and the same encrypted connection then runs over it.
/// The client keeps that path open, and finds the host again whenever it closes.
///
/// The ID is only a way to find a PC. Who it really is, is still settled by its
/// certificate and the code compared when pairing.
/// </summary>
public sealed partial class PortalNode
{
    private PortalLink? link4, link6;
    private PortalDht? dht4, dht6;
    private PortalMapping? mapping;
    /// <summary>Connections this PC makes to itself on behalf of a PC on the other end of a path: local port -> that PC's address.</summary>
    private readonly ConcurrentDictionary<int, IPEndPoint> pumps = new();
    private readonly ConcurrentDictionary<IPEndPoint, (long At, int Times)> knocked = new();
    private List<PortalClient> otherClients = [];

    /// <summary>This PC's ID: six characters, like K7M-2QX.</summary>
    public string Number => Portal.NumberOf(Id);
    /// <summary>The port of the IPv4 socket used across the internet.</summary>
    public int LinkPort => link4?.Port ?? 0;
    public int LinkPort6 => link6?.Port ?? 0;
    /// <summary>This PC's address as the internet sees it: the port the router forwards, or what the directory saw.</summary>
    public IPEndPoint? PublicAddress =>
        mapping is { External: { } outside } ? new IPEndPoint(outside, mapping.ExternalPort) : dht4?.PublicAddress;
    /// <summary>This PC's IPv6 address as the internet sees it; null when this connection has no IPv6.</summary>
    public IPEndPoint? PublicAddress6 => dht6?.PublicAddress;
    /// <summary>The port the router forwards to this PC (host only); null when it would not, or was not asked.</summary>
    public PortalMapping? Mapping => mapping;
    /// <summary>True once the router has been asked (whatever it said).</summary>
    public bool MappingTried { get; private set; }
    /// <summary>True when this network gives every connection a new address, which usually defeats direct links.</summary>
    public bool AddressKeepsChanging => dht4?.AddressKeepsChanging ?? false;
    /// <summary>A client: why the host could not be reached the last time it was tried; None while connected or not tried yet.</summary>
    public PortalConnectProblem HostProblem { get; private set; }
    /// <summary>A client: true while it is looking for the host.</summary>
    public bool Connecting { get; private set; }
    /// <summary>A client: the host's other clients, which files can be sent to through the host.</summary>
    public IReadOnlyList<PortalClient> OtherClients => otherClients;
    /// <summary>A client: its host, once paired or found.</summary>
    public PortalPeer? HostPeer
    {
        get
        {
            lock (gate)
                return peers.Values.FirstOrDefault(peer => peer.Number == settings.HostNumber && peer.Trusted)
                       ?? peers.Values.FirstOrDefault(peer => peer.Number == settings.HostNumber);
        }
    }

    private PortalLink? LinkFor(IPEndPoint endpoint) => endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? link6 : link4;

    private IEnumerable<(PortalLink Link, PortalDht? Dht)> Links()
    {
        if (link4 is not null)
            yield return (link4, dht4);
        if (link6 is not null)
            yield return (link6, dht6);
    }

    private void StartInternet(bool local)
    {
        PortalLink Open(IPAddress address) => new(address)
        {
            // A host takes paths from any PC that knocks; a client only keeps the ones it opened.
            AllowPath = _ => IsHost, Accepted = PumpIn, MaxPaths = IsHost ? 64 : 12,
        };
        try
        {
            link4 = Open(local ? IPAddress.Loopback : IPAddress.Any);
        }
        catch (SocketException error)
        {
            Log.Warning($"Courier can't open its internet socket: {error.Message}");
            return;
        }
        if (!local)
        {
            try
            {
                link6 = Open(IPAddress.IPv6Any);
            }
            catch (SocketException error)
            {
                Log.Info($"Courier: no IPv6 here ({error.Message})");
            }
            dht4 = new PortalDht(link4);
            if (link6 is not null)
                dht6 = new PortalDht(link6);
            // The directory's computers known last time: joining is then instant instead of a few seconds.
            foreach (var (dht, file) in new[] { (dht4, NodesFile(4)), (dht6, NodesFile(6)) })
            {
                try
                {
                    if (dht is not null && File.Exists(file))
                        dht.LoadNodes(File.ReadAllText(file));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        if (IsHost)
        {
            foreach (var (_, dht) in Links())
            {
                if (dht is not null)
                    _ = Task.Run(() => KeepAnnounced(dht));
            }
            if (!local)
                _ = Task.Run(KeepMapped);
        }
        else
        {
            _ = Task.Run(() => KeepConnected(local));
        }
    }

    private static string NodesFile(int version) => Path.Combine(AppInfo.DataDir, $"courier-directory-{version}.txt");

    private async Task StopInternet()
    {
        foreach (var (dht, file) in new[] { (dht4, NodesFile(4)), (dht6, NodesFile(6)) })
        {
            try
            {
                if (dht is { Known: > 8 })
                    File.WriteAllText(file, dht.SaveNodes());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (mapping is { } given)
            await PortalNat.Unmap(given);
        link4?.Dispose();
        link6?.Dispose();
    }

    /// <summary>
    /// A PC at the other end of a path opened a stream: join it to this PC's own listener, so
    /// what arrives is handled by the encrypted connection like any other.
    /// </summary>
    private async Task PumpIn(ReliableStream stream, IPEndPoint from)
    {
        var local = 0;
        try
        {
            using var inside = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await inside.ConnectAsync(Address, Port, stopping.Token);
            local = ((IPEndPoint)inside.Client.LocalEndPoint!).Port;
            pumps[local] = from;
            await using var wire = inside.GetStream();
            var inward = stream.CopyToAsync(wire, 64 * 1024, stopping.Token);
            var outward = wire.CopyToAsync(stream, 64 * 1024, stopping.Token);
            await Task.WhenAny(inward, outward);
            // When one direction ends the other fails soon after: that is the connection closing, not a problem to report.
            foreach (var direction in new[] { inward, outward })
                _ = direction.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            pumps.TryRemove(local, out _);
            stream.Dispose();
        }
    }

    /// <summary>The address across the internet of whoever made this request. Every request comes in over a path; anything else gets nothing.</summary>
    private IPEndPoint? Via(Microsoft.AspNetCore.Http.HttpContext context) =>
        pumps.TryGetValue(context.Connection.RemotePort, out var via) && context.Connection.RemoteIpAddress is { } from && IPAddress.IsLoopback(from) ? via : null;

    // --- the host ---

    /// <summary>Ask the router to forward the host's port, and keep asking before the lease runs out.</summary>
    private async Task KeepMapped()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var given = await PortalNat.Map(LinkPort, stopping.Token);
                var changed = given?.ExternalPort != mapping?.ExternalPort || !MappingTried;
                (mapping, MappingTried) = (given, true);
                if (changed)
                {
                    Log.Info(given is null ? "Courier: the router did not forward a port (no UPnP or NAT-PMP); clients will open a path instead"
                                           : $"Courier: the router forwards {given.External}:{given.ExternalPort} to this PC ({given.Method})");
                    Raise();
                }
                await Task.Delay(TimeSpan.FromMinutes(given is null ? 10 : 25), stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is SocketException or IOException or HttpRequestException)
            {
                await Task.Delay(30_000);
            }
        }
    }

    /// <summary>Keep the host announced under its ID, and open a path towards each client that knocks.</summary>
    private async Task KeepAnnounced(PortalDht dht)
    {
        long announced = 0;
        var link = dht == dht6 ? link6! : link4!;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (await dht.Join(stopping.Token))
                {
                    var now = Environment.TickCount64;
                    if (announced == 0 || now - announced > 8 * 60_000)
                    {
                        // With a forwarded port, also announce that port itself: the router may show another one to the directory.
                        var forwarded = dht == dht4 && mapping is { } given && given.ExternalPort != link.Port ? given.ExternalPort : (int?)null;
                        await dht.Lookup(Portal.DirectoryKey(Number, knock: false), announce: true, stopping.Token, forwarded);
                        announced = now;
                        Log.Info($"Courier: announced under ID {Number} over {(dht == dht6 ? "IPv6" : "IPv4")}, seen as {dht.PublicAddress}");
                        Raise();
                    }
                    // A client that wants in leaves its address under the "knock" key: send towards it, so a path opens through both
                    // routers. Asked every few seconds, of the few computers that hold that key, so a client waits seconds, not minutes.
                    foreach (var knocker in (await dht.Poll(Portal.DirectoryKey(Number, knock: true), stopping.Token)).Take(16))
                    {
                        var (at, times) = knocked.GetValueOrDefault(knocker);
                        if (link.IsOpen(knocker) || dht.IsMe(knocker) || times >= 4 && now - at < 30 * 60_000 || now - at < 45_000)
                            continue;
                        knocked[knocker] = (now, now - at > 30 * 60_000 ? 1 : times + 1);
                        Log.Info($"Courier: a PC at {knocker} is asking to connect; opening a path towards it");
                        _ = link.PunchTo(knocker, TimeSpan.FromSeconds(25), stopping.Token).ContinueWith(done =>
                            Log.Info($"Courier: path to {knocker} {(done.Result ? "opened" : "did not open")}"), TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
                await Task.Delay(3000, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
            {
                await Task.Delay(5000);
            }
        }
    }

    // --- the client ---

    /// <summary>
    /// The client's side: stay connected to the host. When the path is closed, try the
    /// host's last address first, then look its ID up again. A host found for the first time
    /// is asked to pair. While connected, the list of the host's other clients is kept fresh.
    /// </summary>
    private async Task KeepConnected(bool local)
    {
        var lastAsked = 0L;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var number = settings.HostNumber;
                if (Portal.IsNumber(number) && number != Number)
                {
                    var host = HostPeer;
                    if (host is not { Online: true })
                    {
                        Connecting = true;
                        Raise();
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var (found, problem) = await FindHost(number, local);
                        (Connecting, HostProblem) = (false, problem);
                        Log.Info(found is { Tunnel: { } path } ? $"Courier: connected to {Portal.ShowNumber(number)} over {path} in {clock.Elapsed.TotalSeconds:0.0} s"
                                                               : $"Courier: {Portal.ShowNumber(number)} not reached in {clock.Elapsed.TotalSeconds:0} s ({problem})");
                        host = found;
                        if (host is { Trusted: false } && !Pairings.Any(pairing => pairing.PeerId == host.Id))
                            await Pair(host);
                        Raise();
                    }
                    if (host is { Online: true, Trusted: true } && Environment.TickCount64 - lastAsked > 10_000)
                    {
                        lastAsked = Environment.TickCount64;
                        await AskForClients(host);
                    }
                }
                await Task.Delay(HostPeer is { Online: true } ? 3000 : HostProblem == PortalConnectProblem.None ? 1000 : 15_000, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
            {
                Connecting = false;
                await Task.Delay(5000);
            }
        }
    }

    private async Task<(PortalPeer? Host, PortalConnectProblem Problem)> FindHost(string number, bool local)
    {
        // Where the host was last time: often still there, and found in a second instead of through the directory.
        var paired = settings.Devices.FirstOrDefault(device => Portal.NumberOf(device.Id) == number);
        if (paired is { Port: > 0 } && IPAddress.TryParse(paired.Address, out var address) && (!local || IPAddress.IsLoopback(address))
            && await ConnectDirect(new IPEndPoint(address, paired.Port), number, paired.Id) is { } known)
            return (known, PortalConnectProblem.None);
        return local ? (null, PortalConnectProblem.NotFound) : await ConnectById(number, stopping.Token, paired?.Id);
    }

    private async Task AskForClients(PortalPeer host)
    {
        try
        {
            using var client = Client(host.Tunnel!, host.Id, out _);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var listed = await client.GetFromJsonAsync<JsonElement>($"{Api}/clients", limit.Token);
            var fresh = listed.EnumerateArray()
                .Select(item => new PortalClient(item.GetProperty("id").GetString() ?? "", Portal.CleanLabel(item.GetProperty("name").GetString() ?? ""),
                                                 item.GetProperty("online").GetBoolean()))
                .Where(item => IdPattern().IsMatch(item.Id) && item.Id != Id).ToList();
            if (!fresh.SequenceEqual(otherClients))
            {
                otherClients = fresh;
                Raise();
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException
                                          or KeyNotFoundException or InvalidOperationException)
        {
        }
    }

    /// <summary>The host to connect to, by its ID. Saved, and looked for at once.</summary>
    public void SetHost(string number)
    {
        number = Portal.NormalizeNumber(number);
        if (number == settings.HostNumber)
            return;
        settings.HostNumber = number;
        HostProblem = PortalConnectProblem.None;
        otherClients = [];
        save();
        Raise();
    }

    /// <summary>
    /// Find a PC by its ID and open a path to it, over IPv4 and IPv6 at once. Takes from a few
    /// seconds to about a minute. The PC is added to the list (not paired) when it works.
    /// </summary>
    public async Task<(PortalPeer? Peer, PortalConnectProblem Problem)> ConnectById(string number, CancellationToken cancel = default, string? expectedId = null)
    {
        number = Portal.NormalizeNumber(number);
        if (!Portal.IsNumber(number) || number == Number)
            return (null, PortalConnectProblem.BadNumber);
        var joined = await Task.WhenAll(Links().Where(item => item.Dht is not null).Select(async item => (item.Link, Dht: item.Dht!, Ok: await item.Dht!.Join(cancel))));
        var directories = joined.Where(item => item.Ok).Select(item => (item.Link, item.Dht)).ToList();
        if (directories.Count == 0)
            return (null, PortalConnectProblem.NoDirectory);
        var found = false;
        var until = Environment.TickCount64 + 75_000;
        while (Environment.TickCount64 < until && !cancel.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // Over IPv4 and IPv6 at once: leave this PC's address where the host looks for knocks, and look up where the host is.
            var rounds = directories.Select(async item =>
            {
                var knock = item.Dht.Lookup(Portal.DirectoryKey(number, knock: true), announce: true, cancel);
                var at = (await item.Dht.Lookup(Portal.DirectoryKey(number, knock: false), announce: false, cancel)).Where(place => !item.Dht.IsMe(place)).ToList();
                // Start knocking on the host's door straight away. The knock left in the directory (for a host behind a router
                // that lets nothing in first) finishes in the background; nothing waits for it.
                foreach (var place in at)
                    _ = LinkFor(place)?.PunchTo(place, TimeSpan.FromSeconds(30), cancel);
                _ = knock.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return at;
            }).ToList();
            var places = (await Task.WhenAll(rounds)).SelectMany(at => at).Distinct().ToList();
            if (places.Count == 0)
            {
                await Task.Delay(2000, cancel);
                continue;
            }
            found = true;
            Log.Info($"Courier: ID {number} is announced at {string.Join(", ", places.Take(8))}; trying to open a path");
            // IPv6 first: it usually needs no path through a router at all. The host needs a moment to notice the knock; keep sending meanwhile.
            var tries = places.OrderByDescending(place => place.AddressFamily == AddressFamily.InterNetworkV6).Take(8)
                .Select(async place => LinkFor(place) is { } link && await link.PunchTo(place, TimeSpan.FromSeconds(30), cancel) ? place : null).ToList();
            while (tries.Count > 0)
            {
                var done = await Task.WhenAny(tries);
                tries.Remove(done);
                if (await done is { } open && await ConnectDirect(open, number, expectedId) is { } peer)
                    return (peer, PortalConnectProblem.None);
            }
        }
        return (null, found ? PortalConnectProblem.NoPath : PortalConnectProblem.NotFound);
    }

    /// <summary>
    /// Open a path to a PC at a known internet address and ask it who it is. With an expected
    /// ID, an answer from any other PC is not accepted.
    /// </summary>
    public async Task<PortalPeer?> ConnectDirect(IPEndPoint place, string? expectedNumber = null, string? expectedId = null)
    {
        if (LinkFor(place) is not { } link || (!link.IsOpen(place) && !await link.PunchTo(place, TimeSpan.FromSeconds(4), stopping.Token)))
            return null;
        try
        {
            using var client = Client(place, expectedId: null, out var seen);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            var who = await client.GetFromJsonAsync<JsonElement>($"{Api}/who", limit.Token);
            var id = seen();
            // A host this PC is paired with must show the very certificate it was paired with, not just any with the same ID.
            if (id is null || id == Id || who.GetProperty("id").GetString() != id || (expectedNumber is not null && Portal.NumberOf(id) != expectedNumber)
                || (expectedId is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)))
                return null;
            PortalPeer peer;
            lock (gate)
            {
                if (!peers.TryGetValue(id, out peer!))
                    peer = peers[id] = new PortalPeer { Id = id };
                if (!peer.Trusted || peer.Name.Length == 0)
                    peer.Name = Portal.CleanLabel(who.GetProperty("name").GetString() ?? "");
                (peer.Tunnel, peer.LastSeen) = (place, DateTime.UtcNow);
                // Remember where it was, to try there first next time.
                foreach (var device in settings.Devices.Where(device => device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    (device.Address, device.Port) = (place.Address.ToString(), place.Port);
            }
            save();
            Raise();
            return peer;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warning($"Courier: the PC at {place} did not identify itself: {error.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>A PC counts as present while its path is open.</summary>
    private void RefreshPaths()
    {
        var changed = false;
        lock (gate)
        {
            foreach (var peer in peers.Values.Where(peer => peer.Tunnel is not null))
            {
                if (LinkFor(peer.Tunnel!)?.IsOpen(peer.Tunnel!) == true)
                    peer.LastSeen = DateTime.UtcNow;
                else
                    (peer.Tunnel, changed) = (null, true);
            }
        }
        if (changed)
            Raise();
    }
}

public static partial class Portal
{
    /// <summary>The characters of an ID: digits and letters, without I, L, O and U, which are easily taken for others.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// A PC's ID: six characters (30 bits of its certificate's fingerprint), like "K7M2QX". It
    /// finds the PC; it does not prove who it is: pairing does that.
    /// </summary>
    public static string NumberOf(string fingerprint)
    {
        var bits = Convert.ToUInt64(fingerprint[..8], 16) >> 2;
        var text = new char[6];
        for (var index = 5; index >= 0; index--, bits >>= 5)
            text[index] = Alphabet[(int)(bits & 31)];
        return new string(text);
    }

    /// <summary>An ID as typed: any case, any spaces or dashes, and O, I, L taken for 0, 1, 1. "" when it can't be one.</summary>
    public static string NormalizeNumber(string typed)
    {
        var clean = new string(typed.ToUpperInvariant().Where(char.IsLetterOrDigit)
            .Select(character => character switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => character }).ToArray());
        return IsNumber(clean) ? clean : clean.Length == 0 ? "" : clean;
    }

    public static bool IsNumber(string number) => number.Length == 6 && number.All(Alphabet.Contains);

    /// <summary>"K7M2QX" as "K7M-2QX".</summary>
    public static string ShowNumber(string number) => IsNumber(number) ? $"{number[..3]}-{number[3..]}" : number;

    /// <summary>Where in the public directory a PC with this ID announces itself, or where others leave their address for it ("knock").</summary>
    public static byte[] DirectoryKey(string number, bool knock) =>
        SHA1.HashData(Encoding.ASCII.GetBytes($"pcbutler-courier-v2|{(knock ? "knock" : "here")}|{number}"));
}
