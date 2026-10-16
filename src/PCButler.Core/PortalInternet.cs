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

/// <summary>Why connecting to a PC by its ID did not work.</summary>
public enum PortalConnectProblem
{
    None,
    /// <summary>Not an ID (nine digits), or this PC's own.</summary>
    BadNumber,
    /// <summary>The public directory could not be reached from this network.</summary>
    NoDirectory,
    /// <summary>Nobody is announced under that ID: wrong number, or that PC is not running Courier right now.</summary>
    NotFound,
    /// <summary>The PC was found, but the two networks would not let a direct link through.</summary>
    NoPath,
}

/// <summary>Where a PC on the list stands, as this PC sees it.</summary>
public enum PortalState
{
    /// <summary>Not connected, and not being looked for (never connected, or Disconnect was pressed).</summary>
    NotConnected,
    Connecting,
    Connected,
    /// <summary>Found; asking it to let this PC in.</summary>
    Asking,
    /// <summary>Paired on this side; that PC's user has not allowed this one yet.</summary>
    WaitingForThem,
    NeedsPassword,
    WrongPassword,
    NoDirectory,
    NotFound,
    NoPath,
}

/// <summary>
/// How Courier's PCs reach each other across the internet. Each PC has an ID number made
/// from its certificate, and announces itself under it in the BitTorrent network's public
/// directory, over IPv4 and IPv6, and asks its router to forward a port to it (UPnP or
/// NAT-PMP) so others can get straight through. To connect, a PC looks the other's ID up,
/// both send to each other until a path through their routers opens (needed only when the
/// router would not forward a port), and the same encrypted connection then runs over it.
/// The PCs marked "stay" are kept connected: found again whenever the path closes.
///
/// The ID is only a way to find a PC. Who it really is, is still settled by its
/// certificate and the code compared when pairing.
/// </summary>
public sealed partial class PortalNode
{
    /// <summary>Connecting to one PC on the list: how far it got, and what the user typed for it.</summary>
    private sealed class Attempt
    {
        public bool Busy, Connecting, Waiting;
        public PortalConnectProblem Problem;
        public PortalPasswordAsk PasswordAsk;
        /// <summary>The password the user typed for that PC, kept in memory only until the next pairing gives it.</summary>
        public string Password = "";
        public long NextTry, LastAsked;
        public CancellationTokenSource Cancel = new();
    }

    private PortalLink? link4, link6;
    private PortalDht? dht4, dht6;
    private PortalMapping? mapping;
    private readonly ConcurrentDictionary<IPEndPoint, (long At, int Times)> knocked = new();
    private readonly Dictionary<string, Attempt> attempts = [];

    /// <summary>This PC's ID: nine digits, like 418 205 937.</summary>
    public string Number => Portal.NumberOf(Id);
    /// <summary>The port of the IPv4 socket used across the internet.</summary>
    public int LinkPort => link4?.Port ?? 0;
    public int LinkPort6 => link6?.Port ?? 0;
    /// <summary>This PC's address as the internet sees it: the port the router forwards, or what the directory saw.</summary>
    public IPEndPoint? PublicAddress =>
        mapping is { External: { } outside } ? new IPEndPoint(outside, mapping.ExternalPort) : dht4?.PublicAddress;
    /// <summary>This PC's IPv6 address as the internet sees it; null when this connection has no IPv6.</summary>
    public IPEndPoint? PublicAddress6 => dht6?.PublicAddress;
    /// <summary>The port the router forwards to this PC; null when it would not, or was not asked.</summary>
    public PortalMapping? Mapping => mapping;
    /// <summary>True once the router has been asked (whatever it said).</summary>
    public bool MappingTried { get; private set; }
    /// <summary>True when this network gives every connection a new address, which usually defeats direct links.</summary>
    public bool AddressKeepsChanging => dht4?.AddressKeepsChanging ?? false;

    private PortalLink? LinkFor(IPEndPoint endpoint) => endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? link6 : link4;

    private IEnumerable<(PortalLink Link, PortalDht? Dht)> Links()
    {
        if (link4 is not null)
            yield return (link4, dht4);
        if (link6 is not null)
            yield return (link6, dht6);
    }

    /// <summary>The PC with that ID as this one knows it right now (the trusted one first), or null.</summary>
    public PortalPeer? PeerOf(string number)
    {
        number = Portal.NormalizeNumber(number);
        lock (gate)
            return peers.Values.FirstOrDefault(peer => peer.Number == number && peer.Trusted) ?? peers.Values.FirstOrDefault(peer => peer.Number == number);
    }

    private Attempt AttemptFor(string number)
    {
        lock (gate)
        {
            if (!attempts.TryGetValue(number, out var attempt))
                attempts[number] = attempt = new Attempt();
            return attempt;
        }
    }

    /// <summary>Where a PC on the list stands.</summary>
    public PortalState StateOf(string number)
    {
        number = Portal.NormalizeNumber(number);
        var peer = PeerOf(number);
        Attempt? attempt;
        bool stay;
        lock (gate)
        {
            attempts.TryGetValue(number, out attempt);
            stay = PortalSavedList.Find(settings, number) is { Stay: true };
        }
        if (peer is { Online: true, Trusted: true })
            return attempt is { Waiting: true } ? PortalState.WaitingForThem : PortalState.Connected;
        if (attempt?.PasswordAsk is PortalPasswordAsk.Needed)
            return PortalState.NeedsPassword;
        if (attempt?.PasswordAsk is PortalPasswordAsk.Wrong)
            return PortalState.WrongPassword;
        if (peer is { Online: true })
            return PortalState.Asking;
        if (!stay)
            return PortalState.NotConnected;
        return attempt is null || attempt.Connecting ? PortalState.Connecting : attempt.Problem switch
        {
            PortalConnectProblem.NoDirectory => PortalState.NoDirectory,
            PortalConnectProblem.NotFound => PortalState.NotFound,
            PortalConnectProblem.NoPath => PortalState.NoPath,
            _ => PortalState.Connecting,
        };
    }

    /// <summary>The PCs files can go to right now: paired, connected, and (when this PC asked to connect) let in by the other side.</summary>
    public IReadOnlyList<PortalPeer> Ready => Peers.Where(peer => peer is { Trusted: true, Online: true } && StateOf(peer.Number) == PortalState.Connected).ToList();

    private void StartInternet(bool local)
    {
        PortalLink Open(IPAddress address) => new(address)
        {
            // Every PC can be reached by its ID, so a path from any PC that knocks is taken; what comes over it is still checked.
            AllowPath = _ => true, Accepted = PumpIn, MaxPaths = 64, Closed = _ => RefreshPaths(),
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
            foreach (var (_, dht) in Links())
            {
                if (dht is not null)
                    _ = Task.Run(() => KeepAnnounced(dht));
            }
            _ = Task.Run(KeepMapped);
        }
        _ = Task.Run(() => KeepConnected(local));
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
        // Disposing tells every PC with a path open that this one is gone, so none of them goes on showing it connected.
        link4?.Dispose();
        link6?.Dispose();
    }

    /// <summary>A PC at the other end of a path opened a stream: answer its requests on it, over the encrypted connection.</summary>
    private async Task PumpIn(ReliableStream stream, IPEndPoint from)
    {
        try
        {
            await PortalHttp.Serve(stream, certificate, from, Handle, stopping.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException
                                          or System.Security.Authentication.AuthenticationException)
        {
            // The other PC went away, or showed no certificate.
        }
        finally
        {
            stream.Dispose();
        }
    }

    // --- being reachable ---

    /// <summary>Ask the router to forward this PC's port, and keep asking before the lease runs out.</summary>
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
                    Log.Info(given is null ? "Courier: the router did not forward a port (no UPnP or NAT-PMP); other PCs will open a path instead"
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

    /// <summary>Keep this PC announced under its ID, and open a path towards each PC that knocks.</summary>
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
                    // A PC that wants in leaves its address under the "knock" key: send towards it, so a path opens through both
                    // routers. Asked every few seconds, of the few computers that hold that key, so the other PC waits seconds, not minutes.
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

    // --- staying connected ---

    /// <summary>
    /// Keep the PCs marked "stay" connected, each on its own: when its path is closed, try its
    /// last address first, then look its ID up again. A PC found for the first time is asked
    /// to pair. While connected, it is asked now and then whether it has let this PC in.
    /// </summary>
    private async Task KeepConnected(bool local)
    {
        while (!stopping.IsCancellationRequested)
        {
            List<string> wanted;
            lock (gate)
                wanted = settings.Saved.Where(saved => saved.Stay && Portal.IsNumber(saved.Number) && saved.Number != Number).Select(saved => saved.Number).ToList();
            var now = Environment.TickCount64;
            foreach (var number in wanted)
            {
                var attempt = AttemptFor(number);
                if (attempt.Busy || now < attempt.NextTry)
                    continue;
                attempt.Busy = true;
                _ = Task.Run(() => Reach(number, attempt, local));
            }
            try
            {
                await Task.Delay(1000, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool Stays(string number)
    {
        lock (gate)
            return PortalSavedList.Find(settings, number) is { Stay: true };
    }

    private async Task Reach(string number, Attempt attempt, bool local)
    {
        var wait = 3000;
        try
        {
            var peer = PeerOf(number);
            if (peer is not { Online: true })
            {
                attempt.Connecting = true;
                Raise();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (found, problem) = await FindPeer(number, local, attempt.Cancel.Token);
                (attempt.Connecting, attempt.Problem) = (false, problem);
                Log.Info(found is { Tunnel: { } path } ? $"Courier: connected to {Portal.ShowNumber(number)} over {path} in {clock.Elapsed.TotalSeconds:0.0} s"
                                                       : $"Courier: {Portal.ShowNumber(number)} not reached in {clock.Elapsed.TotalSeconds:0} s ({problem})");
                if (found is { Tunnel: { } opened } && !Stays(number))
                {
                    // Disconnect was pressed while it was being looked for.
                    LinkFor(opened)?.Close(opened);
                    return;
                }
                peer = found;
                if (peer is { Trusted: false } && !Pairings.Any(pairing => pairing.PeerId == peer.Id))
                    await Pair(peer);
                Raise();
                if (peer is null)
                {
                    wait = problem == PortalConnectProblem.None ? 1000 : 15_000;
                    return;
                }
            }
            else if (peer is { Trusted: false } && attempt.Password.Length > 0)
            {
                await Pair(peer); // the user typed the password the other PC asked for
                Raise();
            }
            if (peer is { Online: true, Trusted: true } && Environment.TickCount64 - attempt.LastAsked > (attempt.Waiting ? 3000 : 10_000))
            {
                attempt.LastAsked = Environment.TickCount64;
                await Ping(peer, attempt);
            }
        }
        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            attempt.Connecting = false;
            wait = 5000;
        }
        finally
        {
            attempt.NextTry = Environment.TickCount64 + wait;
            attempt.Busy = false;
        }
    }

    private async Task<(PortalPeer? Peer, PortalConnectProblem Problem)> FindPeer(string number, bool local, CancellationToken cancel)
    {
        // Where it was last time: often still there, and found in a second instead of through the directory.
        PortalDevice? paired;
        lock (gate)
            paired = settings.Devices.FirstOrDefault(device => Portal.NumberOf(device.Id) == number);
        if (paired is { Port: > 0 } && IPAddress.TryParse(paired.Address, out var address) && (!local || IPAddress.IsLoopback(address))
            && await ConnectDirect(new IPEndPoint(address, paired.Port), number, paired.Id) is { } known)
            return (known, PortalConnectProblem.None);
        return local ? (null, PortalConnectProblem.NotFound) : await ConnectById(number, cancel, paired?.Id);
    }

    /// <summary>Ask a PC this one connected to whether it has let this one in (403 until its user allows it).</summary>
    private async Task Ping(PortalPeer peer, Attempt attempt)
    {
        try
        {
            using var client = Client(peer.Tunnel!, peer.Id, out _);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            using var answer = await client.GetAsync($"{Api}/ping", limit.Token);
            var waiting = answer.StatusCode == HttpStatusCode.Forbidden;
            if (waiting != attempt.Waiting && (waiting || answer.IsSuccessStatusCode))
            {
                attempt.Waiting = waiting;
                Raise();
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Connect to a PC by its ID, and stay connected to it (also after PC Butler starts again)
    /// until Disconnect. It joins the list if it is not on it yet.
    /// </summary>
    public void Connect(string number)
    {
        number = Portal.NormalizeNumber(number);
        if (!Portal.IsNumber(number) || number == Number)
            return;
        lock (gate)
        {
            PortalSavedList.Add(settings, number, "");
            PortalSavedList.Find(settings, number)!.Stay = true;
            if (attempts.Remove(number, out var old))
                old.Cancel.Cancel();
        }
        save();
        Raise();
    }

    /// <summary>
    /// Stop being connected to a PC, on both sides: the other PC is told, so it shows this one
    /// gone at once and does not connect back by itself. Neither looks for the other until
    /// Connect is pressed again. With forget, this PC is also taken off the list and no longer
    /// trusted (it is kicked out: coming back means asking, and being allowed, again).
    /// </summary>
    public async Task Disconnect(string number, bool forget = false)
    {
        number = Portal.NormalizeNumber(number);
        lock (gate)
        {
            if (PortalSavedList.Find(settings, number) is { } saved)
                saved.Stay = false;
            if (attempts.Remove(number, out var attempt))
                attempt.Cancel.Cancel();
        }
        var peer = PeerOf(number);
        if (peer is { Tunnel: { } via })
        {
            if (peer.Trusted)
            {
                try
                {
                    using var client = Client(via, peer.Id, out _);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(6));
                    using var told = await client.PostAsync($"{Api}/disconnect?forget={(forget ? 1 : 0)}", null, limit.Token);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
                {
                    // Not reached: closing the path below still tells it, without the details.
                }
            }
            LinkFor(via)?.Close(via);
        }
        lock (gate)
        {
            foreach (var other in peers.Values.Where(other => other.Number == number))
            {
                other.Tunnel = null;
                if (forget)
                    (other.Trusted, other.AutoAccept) = (false, false);
            }
            if (forget)
                PortalSavedList.Remove(settings, number);
        }
        CancelTransfersWith(peer?.Id);
        Log.Info($"Courier: {(forget ? "removed" : "disconnected from")} {Portal.ShowNumber(number)}");
        save();
        Raise();
    }

    /// <summary>The other PC disconnected (forget: and removed this one): it is not looked for again until Connect.</summary>
    private void Disconnected(PortalPeer peer, IPEndPoint via, bool forget)
    {
        lock (gate)
        {
            if (PortalSavedList.Find(settings, peer.Number) is { } saved)
                saved.Stay = false;
            if (attempts.Remove(peer.Number, out var attempt))
                attempt.Cancel.Cancel();
            if (forget)
            {
                // It no longer trusts this PC, so this one stops trusting it too; it stays on the list, to ask again later.
                settings.Devices.RemoveAll(device => device.Id.Equals(peer.Id, StringComparison.OrdinalIgnoreCase));
                (peer.Trusted, peer.AutoAccept) = (false, false);
            }
        }
        CancelTransfersWith(peer.Id);
        Log.Info($"Courier: {peer.Name} {(forget ? "removed this PC" : "disconnected")}");
        save();
        // The answer goes back on this path first; then it is closed, and the PC shows as gone.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            lock (gate)
                peer.Tunnel = null;
            LinkFor(via)?.Close(via);
            Raise();
        });
    }

    private void CancelTransfersWith(string? peerId)
    {
        if (peerId is null)
            return;
        foreach (var transfer in Transfers.Where(transfer => transfer.Running && transfer.PeerId.Equals(peerId, StringComparison.OrdinalIgnoreCase)))
            transfer.Cancel();
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
            // Over IPv4 and IPv6 at once: leave this PC's address where the other PC looks for knocks, and look up where it is.
            var rounds = directories.Select(async item =>
            {
                var knock = item.Dht.Lookup(Portal.DirectoryKey(number, knock: true), announce: true, cancel);
                var at = (await item.Dht.Lookup(Portal.DirectoryKey(number, knock: false), announce: false, cancel)).Where(place => !item.Dht.IsMe(place)).ToList();
                // Start knocking on its door straight away. The knock left in the directory (for a PC behind a router
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
            // IPv6 first: it usually needs no path through a router at all. The other PC needs a moment to notice the knock; keep sending meanwhile.
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
            // A PC this one is paired with must show the very certificate it was paired with, not just any with the same ID.
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
    /// <summary>
    /// A PC's ID: nine digits made from its certificate's fingerprint, like "418205937". It
    /// finds the PC; it does not prove who it is: pairing does that.
    /// </summary>
    public static string NumberOf(string fingerprint) => (Convert.ToUInt64(fingerprint[..15], 16) % 1_000_000_000).ToString("D9");

    /// <summary>An ID as typed: only its digits count, so spaces and dashes do no harm.</summary>
    public static string NormalizeNumber(string typed) => new(typed.Where(char.IsAsciiDigit).ToArray());

    public static bool IsNumber(string number) => number.Length == 9 && number.All(char.IsAsciiDigit);

    /// <summary>"418205937" as "418 205 937".</summary>
    public static string ShowNumber(string number) => IsNumber(number) ? $"{number[..3]} {number[3..6]} {number[6..]}" : number;

    /// <summary>Where in the public directory a PC with this ID announces itself, or where others leave their address for it ("knock").</summary>
    public static byte[] DirectoryKey(string number, bool knock) =>
        SHA1.HashData(Encoding.ASCII.GetBytes($"pcbutler-courier-v3|{(knock ? "knock" : "here")}|{number}"));
}
