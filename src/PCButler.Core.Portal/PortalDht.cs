// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace PCButler.Core;

/// <summary>
/// "Bencode", the simple format the BitTorrent network speaks: byte strings, whole
/// numbers, lists, and dictionaries with keys in order. Here: byte[], long, List&lt;object&gt;
/// and Dictionary&lt;string, object&gt;.
/// </summary>
public static class Bencode
{
    public static byte[] Encode(object value)
    {
        var output = new MemoryStream();
        Write(output, value);
        return output.ToArray();
    }

    private static void Write(Stream output, object value)
    {
        switch (value)
        {
            case byte[] bytes:
                output.Write(Encoding.ASCII.GetBytes(bytes.Length + ":"));
                output.Write(bytes);
                break;
            case string text:
                Write(output, Encoding.UTF8.GetBytes(text));
                break;
            case long or int:
                output.Write(Encoding.ASCII.GetBytes($"i{Convert.ToInt64(value)}e"));
                break;
            case List<object> list:
                output.WriteByte((byte)'l');
                foreach (var item in list)
                    Write(output, item);
                output.WriteByte((byte)'e');
                break;
            case Dictionary<string, object> dictionary:
                output.WriteByte((byte)'d');
                foreach (var (key, item) in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Write(output, Encoding.Latin1.GetBytes(key));
                    Write(output, item);
                }
                output.WriteByte((byte)'e');
                break;
            default:
                throw new ArgumentException("not a value this format has");
        }
    }

    /// <summary>Returns null for anything that is not well-formed; packets come from strangers.</summary>
    public static object? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var position = 0;
            var value = Read(data, ref position, 0);
            return position == data.Length ? value : null;
        }
        catch (Exception error) when (error is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static object Read(ReadOnlySpan<byte> data, ref int position, int depth)
    {
        if (depth > 8)
            throw new FormatException();
        switch ((char)data[position])
        {
            case 'i':
            {
                var end = position + 1 + data[(position + 1)..].IndexOf((byte)'e');
                var number = long.Parse(Encoding.ASCII.GetString(data[(position + 1)..end]));
                position = end + 1;
                return number;
            }
            case 'l':
            {
                position++;
                var list = new List<object>();
                while (data[position] != 'e')
                    list.Add(Read(data, ref position, depth + 1));
                position++;
                return list;
            }
            case 'd':
            {
                position++;
                var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                while (data[position] != 'e')
                {
                    if (Read(data, ref position, depth + 1) is not byte[] key)
                        throw new FormatException();
                    dictionary[Encoding.Latin1.GetString(key)] = Read(data, ref position, depth + 1);
                }
                position++;
                return dictionary;
            }
            default:
            {
                var colon = position + data[position..].IndexOf((byte)':');
                var length = int.Parse(Encoding.ASCII.GetString(data[position..colon]));
                if (length < 0 || colon < position || colon + 1 + length > data.Length)
                    throw new FormatException();
                position = colon + 1 + length;
                return data.Slice(colon + 1, length).ToArray();
            }
        }
    }
}

/// <summary>
/// A small client for the BitTorrent network's public directory (its "DHT"): thousands of
/// ordinary computers that together remember, for a 20-byte key, which addresses announced
/// themselves under it. Portal uses it as the place where a PC's ID number leads to its
/// current address, so that no server of PC Butler's own is needed. No file ever goes
/// near it. The computers in it see this PC's internet address, as they do of anyone who
/// uses a torrent program.
/// </summary>
public sealed class PortalDht
{
    private sealed record Node(byte[] Id, IPEndPoint Endpoint);

    private static readonly (string Host, int Port)[] Entrances =
    [
        ("router.bittorrent.com", 6881), ("dht.transmissionbt.com", 6881), ("router.utorrent.com", 6881), ("dht.libtorrent.org", 25401),
    ];

    private readonly PortalLink link;
    /// <summary>The IPv6 directory is a network of its own (BEP 32): addresses there are 16 bytes, and its lists of computers are called "nodes6".</summary>
    private readonly bool six;
    private readonly byte[] id = RandomNumberGenerator.GetBytes(20);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>?>> waiting = new();
    private readonly List<Node> known = [];
    private readonly ConcurrentDictionary<string, int> seenAs = new();
    /// <summary>For each key looked up: the computers nearest to it that answered, so it can be asked again in one quick round.</summary>
    private readonly ConcurrentDictionary<string, (long At, List<Node> Nearest)> nearestTo = new();
    private int transaction;

    public PortalDht(PortalLink link)
    {
        this.link = link;
        six = link.Family == AddressFamily.InterNetworkV6;
        link.Other = OnPacket;
    }

    public int Known
    {
        get
        {
            lock (known)
                return known.Count;
        }
    }

    /// <summary>This PC's address as the internet sees it, according to most of the computers that answered. Null before any did.</summary>
    public IPEndPoint? PublicAddress =>
        seenAs.IsEmpty ? null : IPEndPoint.Parse(seenAs.OrderByDescending(pair => pair.Value).First().Key);

    /// <summary>
    /// True when different computers see this PC under different port numbers: the router
    /// gives every connection a new one, so another PC can't know where to send, and
    /// direct links from the internet usually fail.
    /// </summary>
    public bool AddressKeepsChanging => seenAs.Count(pair => pair.Value >= 3) > 1;

    /// <summary>True for an address that is this PC itself, as any computer of the directory reported it (the directory hands that back at times).</summary>
    public bool IsMe(IPEndPoint endpoint) =>
        seenAs.ContainsKey(endpoint.ToString()) || (endpoint.Port == link.Port && seenAs.Keys.Any(mine => IPEndPoint.Parse(mine).Address.Equals(endpoint.Address)));

    // --- packets ---

    private void OnPacket(byte[] packet, IPEndPoint from)
    {
        if (Bencode.Decode(packet) is not Dictionary<string, object> message || message.GetValueOrDefault("y") is not byte[] { Length: 1 } kind)
            return;
        if (kind[0] == 'r' || kind[0] == 'e')
        {
            if (message.GetValueOrDefault("t") is byte[] number && waiting.TryRemove(Convert.ToHexString(number) + from, out var asker))
            {
                if (message.GetValueOrDefault("ip") is byte[] mine && mine.Length == PeerSize)
                    seenAs.AddOrUpdate(Endpoint(mine).ToString(), 1, (_, count) => count + 1);
                asker.TrySetResult(kind[0] == 'r' ? message.GetValueOrDefault("r") as Dictionary<string, object> : null);
            }
            return;
        }
        // A question from another computer in the directory. Answer briefly, like a polite member: who this is, and the nearest others it knows.
        if (kind[0] != 'q' || message.GetValueOrDefault("t") is not byte[] { Length: <= 16 } theirs || message.GetValueOrDefault("q") is not byte[] question
            || message.GetValueOrDefault("a") is not Dictionary<string, object> asked)
            return;
        var answer = new Dictionary<string, object> { ["id"] = id };
        var name = Encoding.ASCII.GetString(question);
        var about = (asked.GetValueOrDefault("target") ?? asked.GetValueOrDefault("info_hash")) as byte[];
        if (name is "find_node" or "get_peers" && about is { Length: 20 })
        {
            answer[NodesKey] = Nearest(about, 8).SelectMany(node => Compact(node)).ToArray();
            if (name == "get_peers")
                answer["token"] = new byte[] { 1, 2, 3, 4 };
        }
        var reply = Bencode.Encode(new Dictionary<string, object> { ["t"] = theirs, ["y"] = "r", ["r"] = answer });
        link.Send(reply, reply.Length, from);
    }

    private async Task<Dictionary<string, object>?> Ask(IPEndPoint whom, string question, Dictionary<string, object> arguments, CancellationToken cancel)
    {
        var number = BitConverter.GetBytes((ushort)Interlocked.Increment(ref transaction));
        var key = Convert.ToHexString(number) + whom;
        var answer = new TaskCompletionSource<Dictionary<string, object>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[key] = answer;
        arguments["id"] = id;
        if (six && question is "find_node" or "get_peers")
            arguments["want"] = new List<object> { "n6" };
        var packet = Bencode.Encode(new Dictionary<string, object> { ["t"] = number, ["y"] = "q", ["q"] = question, ["a"] = arguments });
        link.Send(packet, packet.Length, whom);
        try
        {
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(1), cancel);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            waiting.TryRemove(key, out _);
        }
    }

    /// <summary>An address and port as the directory packs them: 4 + 2 bytes, or 16 + 2 on IPv6.</summary>
    private int PeerSize => six ? 18 : 6;
    private int NodeSize => 20 + PeerSize;
    private string NodesKey => six ? "nodes6" : "nodes";

    private static IPEndPoint Endpoint(ReadOnlySpan<byte> packed) =>
        new(new IPAddress(packed[..^2]), (packed[^2] << 8) | packed[^1]);

    private byte[] Compact(Node node)
    {
        var bytes = new byte[NodeSize];
        node.Id.CopyTo(bytes, 0);
        node.Endpoint.Address.GetAddressBytes().CopyTo(bytes, 20);
        (bytes[^2], bytes[^1]) = ((byte)(node.Endpoint.Port >> 8), (byte)node.Endpoint.Port);
        return bytes;
    }

    /// <summary>An address another computer may be reached at: on IPv6 only global ones (2000::/3), never this network's own.</summary>
    private bool Usable(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return six ? address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20
                   : address.AddressFamily == AddressFamily.InterNetwork && bytes[0] is not (0 or 127 or >= 224);
    }

    private static BigInteger Distance(byte[] a, byte[] b)
    {
        Span<byte> xor = stackalloc byte[20];
        for (var index = 0; index < 20; index++)
            xor[index] = (byte)(a[index] ^ b[index]);
        return new BigInteger(xor, isUnsigned: true, isBigEndian: true);
    }

    private List<Node> Nearest(byte[] target, int count)
    {
        lock (known)
            return known.OrderBy(node => Distance(node.Id, target)).Take(count).ToList();
    }

    private void Remember(Node node)
    {
        if (node.Endpoint.Port == 0 || !Usable(node.Endpoint.Address))
            return;
        lock (known)
        {
            if (known.Any(old => old.Endpoint.Equals(node.Endpoint)))
                return;
            known.Add(node);
            if (known.Count > 600)
                known.RemoveRange(0, 100);
        }
    }

    private IEnumerable<Node> NodesIn(Dictionary<string, object> answer)
    {
        if (answer.GetValueOrDefault(NodesKey) is not byte[] packed)
            yield break;
        for (var offset = 0; offset + NodeSize <= packed.Length; offset += NodeSize)
            yield return new Node(packed.AsSpan(offset, 20).ToArray(), Endpoint(packed.AsSpan(offset + 20, PeerSize)));
    }

    // --- joining, looking up, announcing ---

    /// <summary>Get to know some computers of the directory, through its well-known entrances. Returns whether any answered.</summary>
    public async Task<bool> Join(CancellationToken cancel = default) => Known >= 8 || await Enter(cancel);

    /// <summary>Ask the well-known entrances for computers of the directory.</summary>
    private async Task<bool> Enter(CancellationToken cancel)
    {
        var entrances = new List<IPEndPoint>();
        foreach (var (host, port) in Entrances)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, link.Family, cancel);
                entrances.AddRange(addresses.Take(2).Select(address => new IPEndPoint(address, port)));
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException)
            {
            }
        }
        var answers = await Task.WhenAll(entrances.Select(entrance => Ask(entrance, "find_node", new() { ["target"] = id }, cancel)));
        foreach (var answer in answers.OfType<Dictionary<string, object>>())
        {
            foreach (var node in NodesIn(answer))
                Remember(node);
        }
        return Known > 0;
    }

    /// <summary>
    /// Find the addresses announced under a key, walking towards the computers responsible
    /// for it. With announce: true, this PC is then announced under the key too (the
    /// directory records the address it sees the announcement come from).
    /// </summary>
    public async Task<List<IPEndPoint>> Lookup(byte[] key, bool announce, CancellationToken cancel = default, int? forwardedPort = null)
    {
        if (!await Join(cancel))
            return [];
        var candidates = Nearest(key, 32);
        var asked = new HashSet<string>();
        var holders = new List<(Node Node, byte[] Token)>();
        var found = new HashSet<IPEndPoint>();
        var restarted = false;
        for (var round = 0; round < 14 && !cancel.IsCancellationRequested; round++)
        {
            // Looking (not announcing), the first answer that holds addresses is enough: the PC is found.
            if (!announce && found.Count > 0)
                break;
            var batch = candidates.OrderBy(node => Distance(node.Id, key)).Take(20).Where(node => !asked.Contains(node.Endpoint.ToString())).Take(12).ToList();
            if (batch.Count == 0)
                break;
            foreach (var node in batch)
                asked.Add(node.Endpoint.ToString());
            var answers = await Task.WhenAll(batch.Select(node => Ask(node.Endpoint, "get_peers", new() { ["info_hash"] = key }, cancel)));
            if (round == 0 && !restarted && answers.All(answer => answer is null) && !cancel.IsCancellationRequested)
            {
                restarted = true;
                // The computers remembered from last time have all gone: start again from the entrances.
                lock (known)
                    known.Clear();
                if (!await Enter(cancel))
                    return [];
                (candidates, round) = (Nearest(key, 32), -1);
                asked.Clear();
                continue;
            }
            for (var index = 0; index < batch.Count; index++)
            {
                if (answers[index] is not { } answer)
                {
                    candidates.Remove(batch[index]);
                    // Forgotten too: computers that left the directory would otherwise be asked first at every lookup, a second lost each time.
                    if (!cancel.IsCancellationRequested)
                        lock (known)
                            known.RemoveAll(node => node.Endpoint.Equals(batch[index].Endpoint));
                    continue;
                }
                Remember(batch[index]);
                if (answer.GetValueOrDefault("token") is byte[] token)
                    holders.Add((batch[index], token));
                if (answer.GetValueOrDefault("values") is List<object> values)
                {
                    foreach (var value in values.OfType<byte[]>().Where(value => value.Length == PeerSize))
                    {
                        if (Endpoint(value) is var peer && Usable(peer.Address))
                            found.Add(peer);
                    }
                }
                foreach (var node in NodesIn(answer))
                {
                    if (candidates.All(old => !old.Endpoint.Equals(node.Endpoint)) && candidates.Count < 400)
                        candidates.Add(node);
                }
            }
        }
        if (holders.Count > 0)
            nearestTo[Convert.ToHexString(key)] = (Environment.TickCount64, holders.OrderBy(holder => Distance(holder.Node.Id, key)).Take(16).Select(holder => holder.Node).ToList());
        if (announce)
        {
            var nearest = holders.OrderBy(holder => Distance(holder.Node.Id, key)).Take(8).ToList();
            await Task.WhenAll(nearest.Select(holder => Ask(holder.Node.Endpoint, "announce_peer", new()
            {
                ["info_hash"] = key, ["port"] = (long)link.Port, ["implied_port"] = 1L, ["token"] = holder.Token,
            }, cancel)));
            // A port the router was asked to forward is announced as itself too (the router may show the directory another one).
            if (forwardedPort is { } port)
            {
                await Task.WhenAll(nearest.Select(holder => Ask(holder.Node.Endpoint, "announce_peer", new()
                {
                    ["info_hash"] = key, ["port"] = (long)port, ["implied_port"] = 0L, ["token"] = holder.Token,
                }, cancel)));
            }
        }
        found.RemoveWhere(endpoint => endpoint.Port == 0);
        return found.ToList();
    }

    /// <summary>
    /// What is announced under a key now, asked in one round of the computers that were
    /// nearest to it last time (a full lookup every few minutes keeps that list right). Quick
    /// enough to ask every few seconds.
    /// </summary>
    public async Task<List<IPEndPoint>> Poll(byte[] key, CancellationToken cancel = default)
    {
        if (!nearestTo.TryGetValue(Convert.ToHexString(key), out var known) || Environment.TickCount64 - known.At > 4 * 60_000)
            return await Lookup(key, announce: false, cancel);
        var answers = await Task.WhenAll(known.Nearest.Select(node => Ask(node.Endpoint, "get_peers", new() { ["info_hash"] = key }, cancel)));
        if (answers.Count(answer => answer is not null) < 3)
            return await Lookup(key, announce: false, cancel);
        var found = new HashSet<IPEndPoint>();
        foreach (var values in answers.Select(answer => answer?.GetValueOrDefault("values")).OfType<List<object>>())
        {
            foreach (var value in values.OfType<byte[]>().Where(value => value.Length == PeerSize))
            {
                if (Endpoint(value) is { Port: > 0 } peer && Usable(peer.Address))
                    found.Add(peer);
            }
        }
        return found.ToList();
    }

    // --- remembering the directory between runs ---

    /// <summary>The computers of the directory this one knows, to start from next time instead of from the well-known entrances.</summary>
    public string SaveNodes()
    {
        lock (known)
            return string.Join("\n", known.Take(300).Select(node => $"{Convert.ToHexString(node.Id)} {node.Endpoint}"));
    }

    public void LoadNodes(string saved)
    {
        foreach (var line in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && parts[0].Length == 40 && IPEndPoint.TryParse(parts[1], out var endpoint)
                && endpoint.AddressFamily == link.Family)
            {
                try
                {
                    Remember(new Node(Convert.FromHexString(parts[0]), endpoint));
                }
                catch (FormatException)
                {
                }
            }
        }
    }
}
