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
    private readonly byte[] id = RandomNumberGenerator.GetBytes(20);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>?>> waiting = new();
    private readonly List<Node> known = [];
    private readonly ConcurrentDictionary<string, int> seenAs = new();
    private int transaction;

    public PortalDht(PortalLink link)
    {
        this.link = link;
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
                if (message.GetValueOrDefault("ip") is byte[] { Length: 6 } mine)
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
            answer["nodes"] = Nearest(about, 8).SelectMany(node => Compact(node)).ToArray();
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
        var packet = Bencode.Encode(new Dictionary<string, object> { ["t"] = number, ["y"] = "q", ["q"] = question, ["a"] = arguments });
        link.Send(packet, packet.Length, whom);
        try
        {
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(1.2), cancel);
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

    private static IPEndPoint Endpoint(ReadOnlySpan<byte> six) => new(new IPAddress(six[..4]), (six[4] << 8) | six[5]);

    private static byte[] Compact(Node node)
    {
        var bytes = new byte[26];
        node.Id.CopyTo(bytes, 0);
        node.Endpoint.Address.GetAddressBytes().CopyTo(bytes, 20);
        (bytes[24], bytes[25]) = ((byte)(node.Endpoint.Port >> 8), (byte)node.Endpoint.Port);
        return bytes;
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
        if (node.Endpoint.Port == 0 || node.Endpoint.Address.GetAddressBytes()[0] is 0 or 127 or >= 224)
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

    private static IEnumerable<Node> NodesIn(Dictionary<string, object> answer)
    {
        if (answer.GetValueOrDefault("nodes") is not byte[] packed)
            yield break;
        for (var offset = 0; offset + 26 <= packed.Length; offset += 26)
            yield return new Node(packed.AsSpan(offset, 20).ToArray(), Endpoint(packed.AsSpan(offset + 20, 6)));
    }

    // --- joining, looking up, announcing ---

    /// <summary>Get to know some computers of the directory, through its well-known entrances. Returns whether any answered.</summary>
    public async Task<bool> Join(CancellationToken cancel = default)
    {
        if (Known >= 8)
            return true;
        var entrances = new List<IPEndPoint>();
        foreach (var (host, port) in Entrances)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancel);
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
    public async Task<List<IPEndPoint>> Lookup(byte[] key, bool announce, CancellationToken cancel = default)
    {
        if (!await Join(cancel))
            return [];
        var candidates = Nearest(key, 24);
        var asked = new HashSet<string>();
        var holders = new List<(Node Node, byte[] Token)>();
        var found = new HashSet<IPEndPoint>();
        for (var round = 0; round < 14 && !cancel.IsCancellationRequested; round++)
        {
            var batch = candidates.OrderBy(node => Distance(node.Id, key)).Take(16).Where(node => !asked.Contains(node.Endpoint.ToString())).Take(8).ToList();
            if (batch.Count == 0)
                break;
            foreach (var node in batch)
                asked.Add(node.Endpoint.ToString());
            var answers = await Task.WhenAll(batch.Select(node => Ask(node.Endpoint, "get_peers", new() { ["info_hash"] = key }, cancel)));
            for (var index = 0; index < batch.Count; index++)
            {
                if (answers[index] is not { } answer)
                {
                    candidates.Remove(batch[index]);
                    continue;
                }
                Remember(batch[index]);
                if (answer.GetValueOrDefault("token") is byte[] token)
                    holders.Add((batch[index], token));
                if (answer.GetValueOrDefault("values") is List<object> values)
                {
                    foreach (var value in values.OfType<byte[]>().Where(value => value.Length == 6))
                        found.Add(Endpoint(value));
                }
                foreach (var node in NodesIn(answer))
                {
                    if (candidates.All(old => !old.Endpoint.Equals(node.Endpoint)) && candidates.Count < 400)
                        candidates.Add(node);
                }
            }
        }
        if (announce)
        {
            var nearest = holders.OrderBy(holder => Distance(holder.Node.Id, key)).Take(8).ToList();
            await Task.WhenAll(nearest.Select(holder => Ask(holder.Node.Endpoint, "announce_peer", new()
            {
                ["info_hash"] = key, ["port"] = (long)link.Port, ["implied_port"] = 1L, ["token"] = holder.Token,
            }, cancel)));
        }
        found.RemoveWhere(endpoint => endpoint.Port == 0);
        return found.ToList();
    }
}
