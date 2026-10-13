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
    /// <summary>Not twelve digits, or this PC's own ID.</summary>
    BadNumber,
    /// <summary>The public directory could not be reached from this network.</summary>
    NoDirectory,
    /// <summary>Nobody is announced under that ID: wrong number, or that PC is not reachable right now.</summary>
    NotFound,
    /// <summary>The PC was found, but the two networks would not let a direct link through.</summary>
    NoPath,
}

/// <summary>
/// The part of a Portal node that reaches PCs on other networks. Each PC has an ID number
/// made from its certificate. A PC that is switched to "reachable" announces itself under
/// its ID in the BitTorrent network's public directory; another PC looks the ID up there,
/// both send to each other until a path through their routers opens, and the same
/// encrypted connection Portal uses on a local network then runs over that path.
///
/// The ID is only a way to find a PC. Who it really is, is still settled by its
/// certificate and the code compared when pairing.
/// </summary>
public sealed partial class PortalNode
{
    private PortalLink? link;
    private PortalDht? dht;
    /// <summary>Connections this PC makes to itself on behalf of a PC on the other end of a path: local port -> that PC's address.</summary>
    private readonly ConcurrentDictionary<int, IPEndPoint> pumps = new();
    private readonly ConcurrentDictionary<IPEndPoint, (long At, int Times)> knocked = new();
    private volatile bool reachable;

    /// <summary>This PC's ID, twelve digits.</summary>
    public string Number => Portal.NumberOf(Id);
    /// <summary>The port of the socket used across the internet; 0 when that part is off.</summary>
    public int LinkPort => link?.Port ?? 0;
    public IPEndPoint? PublicAddress => dht?.PublicAddress;
    /// <summary>True when this network gives every connection a new address, which usually defeats direct links.</summary>
    public bool AddressKeepsChanging => dht?.AddressKeepsChanging ?? false;

    /// <summary>Whether other PCs can find this one by its ID and open a path to it.</summary>
    public bool Reachable
    {
        get => reachable;
        set
        {
            if (reachable == value)
                return;
            reachable = value;
            settings.Reachable = value;
            save();
            Raise();
        }
    }

    private void StartInternet(bool directory)
    {
        try
        {
            link = new PortalLink(IPAddress.IsLoopback(Address) ? IPAddress.Loopback : IPAddress.Any)
            {
                AllowPath = _ => reachable, Accepted = PumpIn,
            };
        }
        catch (SocketException error)
        {
            Log.Warning($"Courier can't open its internet socket: {error.Message}");
            return;
        }
        reachable = settings.Reachable;
        if (directory)
        {
            dht = new PortalDht(link);
            _ = Task.Run(KeepAnnounced);
        }
    }

    private void StopInternet() => link?.Dispose();

    /// <summary>
    /// A PC at the other end of a path opened a stream: join it to this PC's own listener, so
    /// what arrives is handled exactly like a connection from the local network.
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
            await Task.WhenAny(stream.CopyToAsync(wire, 64 * 1024, stopping.Token), wire.CopyToAsync(stream, 64 * 1024, stopping.Token));
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

    /// <summary>The address across the internet of whoever made this request, if it came in over a path.</summary>
    private IPEndPoint? Via(Microsoft.AspNetCore.Http.HttpContext context) =>
        pumps.TryGetValue(context.Connection.RemotePort, out var via) && context.Connection.RemoteIpAddress is { } from
        && (IPAddress.IsLoopback(from) || from.MapToIPv4().Equals(Address)) ? via : null;

    /// <summary>Keep this PC announced under its ID while it is reachable, and answer the PCs that knock.</summary>
    private async Task KeepAnnounced()
    {
        long announced = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (reachable && await dht!.Join(stopping.Token))
                {
                    var now = Environment.TickCount64;
                    if (announced == 0 || now - announced > 8 * 60_000)
                    {
                        await dht.Lookup(Portal.DirectoryKey(Number, knock: false), announce: true, stopping.Token);
                        announced = now;
                        Log.Info($"Courier: announced under ID {Number}; this PC is seen from the internet as {dht.PublicAddress}");
                        Raise();
                    }
                    // Whoever wants to reach this PC leaves its address under the "knock" key. Send towards each, so a path can open.
                    foreach (var knocker in (await dht.Lookup(Portal.DirectoryKey(Number, knock: true), announce: false, stopping.Token)).Take(8))
                    {
                        var (at, times) = knocked.GetValueOrDefault(knocker);
                        if (link!.IsOpen(knocker) || dht.IsMe(knocker) || times >= 4 && now - at < 30 * 60_000 || now - at < 45_000)
                            continue;
                        knocked[knocker] = (now, now - at > 30 * 60_000 ? 1 : times + 1);
                        Log.Info($"Courier: a PC at {knocker} is asking to connect; opening a path towards it");
                        _ = link.PunchTo(knocker, TimeSpan.FromSeconds(25), stopping.Token).ContinueWith(done =>
                            Log.Info($"Courier: path to {knocker} {(done.Result ? "opened" : "did not open")}"), TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
                else if (!reachable)
                {
                    announced = 0;
                }
                await Task.Delay(reachable ? 12_000 : 3000, stopping.Token);
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

    /// <summary>
    /// Find a PC by its ID and open a path to it. Takes from a few seconds to about a minute.
    /// The PC is added to the list (not paired) when it works.
    /// </summary>
    public async Task<(PortalPeer? Peer, PortalConnectProblem Problem)> ConnectById(string number, CancellationToken cancel = default)
    {
        number = new string(number.Where(char.IsDigit).ToArray());
        if (number.Length != 12 || number == Number)
            return (null, PortalConnectProblem.BadNumber);
        if (dht is null || link is null || !await dht.Join(cancel))
            return (null, PortalConnectProblem.NoDirectory);
        var found = false;
        var until = Environment.TickCount64 + 75_000;
        while (Environment.TickCount64 < until && !cancel.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // Leave this PC's address where the other one looks for knocks, then look up where it is.
            var knock = dht.Lookup(Portal.DirectoryKey(number, knock: true), announce: true, cancel);
            var places = (await dht.Lookup(Portal.DirectoryKey(number, knock: false), announce: false, cancel)).Where(place => !dht.IsMe(place)).ToList();
            await knock;
            if (places.Count == 0)
            {
                await Task.Delay(3000, cancel);
                continue;
            }
            found = true;
            Log.Info($"Courier: ID {number} is announced at {string.Join(", ", places.Take(6))}; trying to open a path");
            // The other PC needs a moment to notice the knock; keep sending towards it meanwhile.
            var tries = places.Take(6).Select(async place => await link.PunchTo(place, TimeSpan.FromSeconds(30), cancel) ? place : null).ToList();
            while (tries.Count > 0)
            {
                var done = await Task.WhenAny(tries);
                tries.Remove(done);
                if (await done is { } open && await ConnectDirect(open, number) is { } peer)
                    return (peer, PortalConnectProblem.None);
            }
        }
        return (null, found ? PortalConnectProblem.NoPath : PortalConnectProblem.NotFound);
    }

    /// <summary>
    /// Open a path to a PC at a known internet address and ask it who it is. With an expected
    /// ID, an answer from any other PC is not accepted.
    /// </summary>
    public async Task<PortalPeer?> ConnectDirect(IPEndPoint place, string? expectedNumber = null)
    {
        if (link is null || (!link.IsOpen(place) && !await link.PunchTo(place, TimeSpan.FromSeconds(6), stopping.Token)))
            return null;
        try
        {
            using var client = Client("", 0, expectedId: null, out var seen, place);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            var who = await client.GetFromJsonAsync<JsonElement>($"{Api}/who", limit.Token);
            var id = seen();
            if (id is null || id == Id || who.GetProperty("id").GetString() != id || (expectedNumber is not null && Portal.NumberOf(id) != expectedNumber))
                return null;
            PortalPeer peer;
            lock (gate)
            {
                if (!peers.TryGetValue(id, out peer!))
                    peer = peers[id] = new PortalPeer { Id = id };
                if (!peer.Trusted || peer.Name.Length == 0)
                    peer.Name = Portal.CleanLabel(who.GetProperty("name").GetString() ?? "");
                (peer.Tunnel, peer.LastSeen) = (place, DateTime.UtcNow);
            }
            Raise();
            return peer;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warning($"Courier: the PC at {place} did not identify itself: {error.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Peers reached over the internet count as present while their path is open.</summary>
    private void RefreshPaths()
    {
        if (link is null)
            return;
        var changed = false;
        lock (gate)
        {
            foreach (var peer in peers.Values.Where(peer => peer.Tunnel is not null))
            {
                if (link.IsOpen(peer.Tunnel!))
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
    /// <summary>A PC's twelve-digit ID, made from its certificate's fingerprint. It finds the PC; it does not prove who it is.</summary>
    public static string NumberOf(string fingerprint) =>
        (Convert.ToUInt64(fingerprint[..15], 16) % 1_000_000_000_000).ToString("000000000000");

    /// <summary>"482019337615" as "482 019 337 615".</summary>
    public static string ShowNumber(string number) =>
        number.Length == 12 ? $"{number[..3]} {number[3..6]} {number[6..9]} {number[9..]}" : number;

    /// <summary>Where in the public directory a PC with this ID announces itself, or where others leave their address for it ("knock").</summary>
    public static byte[] DirectoryKey(string number, bool knock) =>
        SHA1.HashData(Encoding.ASCII.GetBytes($"pcbutler-portal-v1|{(knock ? "knock" : "here")}|{number}"));
}
