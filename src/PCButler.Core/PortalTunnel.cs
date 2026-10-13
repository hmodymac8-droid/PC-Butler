// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PCButler.Core;

/// <summary>
/// A reliable, ordered stream of bytes carried in UDP packets, for a direct link between
/// two PCs across the internet. Packets can be lost, repeated or arrive out of order; this
/// numbers them, acknowledges what arrives, sends again what doesn't, and slows down when
/// the path is losing packets. It protects nothing by itself: the encrypted connection that
/// Portal runs through it does that.
/// </summary>
public sealed class ReliableStream : Stream
{
    /// <summary>Bytes of data per packet: small enough to cross any ordinary internet path in one piece.</summary>
    public const int PacketData = 1180;
    private const int ReceiveLimit = 12 * 1024 * 1024; // unread bytes the receiver holds before telling the sender to wait
    private const int MaxWindow = 4096, MinWindow = 32;
    private const byte Data = 5, Ack = 6, Fin = 7, Reset = 8;

    private sealed class Sent
    {
        public required byte[] Packet;
        public long At;
        public int Times;
        public bool Arrived;
    }

    private readonly Action<byte[], int> send;
    private readonly Action? closed;
    private readonly object gate = new();
    // sending
    private readonly SortedDictionary<uint, Sent> unacked = [];
    private readonly SemaphoreSlim room = new(0);
    private uint nextSend, peerWindow = 64;
    private double window = 32, threshold = 1024;
    private double smoothed = 200, variance = 100;
    private long lastLossCut, lastHeard = Environment.TickCount64;
    private bool finSent;
    // receiving
    private readonly Dictionary<uint, byte[]> early = [];
    private readonly Queue<byte[]> ready = new();
    private ArraySegment<byte> head; // what is left of the packet being read
    private readonly SemaphoreSlim arrived = new(0);
    private uint nextReceive;
    private uint? finAt;
    private int unread, sinceAck;
    private bool ackDue, ended, failed;

    /// <summary>send(buffer, length) puts one packet on the wire; closed is called once when the stream is over.</summary>
    public ReliableStream(Action<byte[], int> send, Action? closed = null) => (this.send, this.closed) = (send, closed);

    public bool Failed => failed;

    /// <summary>The stream's state in a line, for the log and for finding out why a link is slow.</summary>
    public string Describe()
    {
        lock (gate)
            return $"window {window:0} (other side allows {peerWindow}), waiting for acknowledgement {unacked.Count}, round trip {smoothed:0} ms, sent {nextSend}, received {nextReceive}, held early {early.Count}, unread {unread}";
    }
    public bool Over { get; private set; }
    private double Timeout => Math.Clamp(smoothed + 4 * variance, 100, 2500);

    // --- packets in ---

    /// <summary>Hand over one packet that arrived for this stream (without the link's own header).</summary>
    public void OnPacket(byte type, ReadOnlySpan<byte> body)
    {
        var acknowledge = false;
        lock (gate)
        {
            lastHeard = Environment.TickCount64;
            switch (type)
            {
                case Data or Fin when body.Length >= 4:
                    var number = BinaryPrimitives.ReadUInt32BigEndian(body);
                    if (type == Fin)
                        finAt = number + 1;
                    if (number - nextReceive < MaxWindow && (unread < ReceiveLimit || body.Length == 4))
                    {
                        if (number == nextReceive)
                        {
                            Deliver(body[4..].ToArray());
                            while (early.Remove(nextReceive, out var held))
                                Deliver(held);
                        }
                        else
                        {
                            early.TryAdd(number, body[4..].ToArray());
                        }
                    }
                    if (finAt == nextReceive && !ended)
                    {
                        ended = true;
                        Wake(arrived);
                    }
                    // Every second packet is acknowledged at once, the rest by the timer: quick without doubling the traffic.
                    ackDue = true;
                    acknowledge = ++sinceAck >= 2 || type == Fin || early.Count > 0;
                    break;
                case Ack when body.Length >= 16:
                    OnAck(BinaryPrimitives.ReadUInt32BigEndian(body), BinaryPrimitives.ReadUInt32BigEndian(body[4..]), BinaryPrimitives.ReadUInt64BigEndian(body[8..]));
                    break;
                case Reset:
                    Fail();
                    break;
            }
        }
        if (acknowledge)
            SendAck();
    }

    private void Deliver(byte[] data)
    {
        nextReceive++;
        if (data.Length == 0)
            return;
        ready.Enqueue(data);
        unread += data.Length;
        Wake(arrived);
    }

    private static void Wake(SemaphoreSlim signal)
    {
        if (signal.CurrentCount == 0)
            signal.Release();
    }

    private void OnAck(uint upTo, uint theirWindow, ulong beyond)
    {
        peerWindow = Math.Max(theirWindow, 1);
        var now = Environment.TickCount64;
        var freed = 0;
        Sent? newest = null;
        while (unacked.Count > 0)
        {
            var (number, first) = unacked.First();
            if (upTo - number is 0 or > MaxWindow * 2) // not yet acknowledged
                break;
            unacked.Remove(number);
            freed++;
            newest = first;
        }
        // The round trip is timed on the newest packet this acknowledgement covers, and only if it was sent once and not
        // already reported: older ones were waiting for a gap to be filled, which says nothing about the path's speed.
        if (newest is { Times: 1, Arrived: false })
            Measure(now - newest.At);
        // Packets the other side already holds beyond a gap: mark them, and send the gap again at once.
        uint highest = 0;
        var any = false;
        for (var bit = 0; bit < 64; bit++)
        {
            if ((beyond & (1UL << bit)) == 0)
                continue;
            var number = upTo + 1 + (uint)bit;
            if (unacked.TryGetValue(number, out var held) && !held.Arrived)
            {
                held.Arrived = true;
                if (held.Times == 1)
                    Measure(now - held.At);
            }
            (highest, any) = (number, true);
        }
        if (any)
        {
            var lost = false;
            foreach (var (number, packet) in unacked)
            {
                if (highest - number is 0 or > MaxWindow * 2)
                    break;
                // Three later packets arrived and this one did not: it is lost. Sent again once per round trip, not on every acknowledgement.
                if (packet.Arrived || highest - number < 3 || now - packet.At < Math.Max(15, smoothed * 1.25))
                    continue;
                (packet.At, lost) = (now, true);
                packet.Times++;
                send(packet.Packet, packet.Packet.Length);
            }
            if (lost)
                SlowDown(now);
        }
        if (freed > 0)
        {
            window = Math.Min(MaxWindow, window < threshold ? window + freed : window + (double)freed / window);
            Wake(room);
        }
    }

    private void Measure(long sample)
    {
        variance = 0.75 * variance + 0.25 * Math.Abs(smoothed - sample);
        smoothed = 0.875 * smoothed + 0.125 * sample;
    }

    /// <summary>A packet was lost: halve the pace, at most once per round trip.</summary>
    private void SlowDown(long now)
    {
        if (now - lastLossCut < smoothed)
            return;
        lastLossCut = now;
        threshold = Math.Max(MinWindow, window * 0.7);
        window = threshold;
    }

    private void SendAck()
    {
        var packet = new byte[17];
        lock (gate)
        {
            packet[0] = Ack;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), nextReceive);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), (uint)Math.Clamp((ReceiveLimit - unread) / PacketData, 0, MaxWindow));
            ulong beyond = 0;
            foreach (var number in early.Keys)
            {
                var distance = number - nextReceive - 1;
                if (distance < 64)
                    beyond |= 1UL << (int)distance;
            }
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(9), beyond);
            (ackDue, sinceAck) = (false, 0);
        }
        send(packet, packet.Length);
    }

    /// <summary>Called a hundred times a second by the link: late acknowledgements, packets to send again, a dead other side.</summary>
    public void Tick()
    {
        bool acknowledge;
        lock (gate)
        {
            if (Over)
                return;
            var now = Environment.TickCount64;
            if (now - lastHeard > 45_000 && (unacked.Count > 0 || !ended))
            {
                Fail();
                return;
            }
            var timeout = Timeout;
            var late = false;
            foreach (var packet in unacked.Values)
            {
                if (packet.Arrived || now - packet.At < timeout * Math.Min(8, 1 << Math.Min(packet.Times - 1, 3)))
                    continue;
                if (packet.Times > 40)
                {
                    Fail();
                    return;
                }
                (packet.At, late) = (now, true);
                packet.Times++;
                send(packet.Packet, packet.Packet.Length);
            }
            if (late)
                SlowDown(now);
            acknowledge = ackDue;
            if (finSent && unacked.Count == 0 && ended)
                Finish();
        }
        if (acknowledge)
            SendAck();
    }

    private void Fail()
    {
        failed = true;
        Finish();
    }

    private void Finish()
    {
        if (Over)
            return;
        Over = true;
        ended = true;
        Wake(arrived);
        Wake(room);
        closed?.Invoke();
    }

    // --- the stream ---

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
    {
        while (buffer.Length > 0)
        {
            var size = Math.Min(PacketData, buffer.Length);
            await Queue(Data, buffer[..size], cancel);
            buffer = buffer[size..];
        }
    }

    private async ValueTask Queue(byte type, ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        while (true)
        {
            lock (gate)
            {
                if (failed || finSent && type == Data)
                    throw new IOException("The link to the other PC was lost.");
                if (unacked.Count < Math.Min(window, peerWindow))
                {
                    var packet = new byte[5 + data.Length];
                    packet[0] = type;
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), nextSend);
                    data.CopyTo(packet.AsMemory(5));
                    unacked[nextSend++] = new Sent { Packet = packet, At = Environment.TickCount64, Times = 1 };
                    send(packet, packet.Length);
                    return;
                }
            }
            await room.WaitAsync(50, cancel); // woken when packets are acknowledged; the time limit covers a missed wake-up
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        while (true)
        {
            var size = 0;
            var reopened = false;
            lock (gate)
            {
                if (head.Count == 0 && ready.Count > 0)
                    head = ready.Dequeue();
                if (head.Count > 0)
                {
                    // The encryption layer above first asks for nothing at all, just to be told when data is there.
                    if (buffer.Length == 0)
                        return 0;
                    size = Math.Min(head.Count, buffer.Length);
                    head.AsSpan(0, size).CopyTo(buffer.Span);
                    head = head[size..];
                    var before = unread;
                    unread -= size;
                    // The sender was told to wait; once half the room is free again, tell it so.
                    reopened = before >= ReceiveLimit / 2 && unread < ReceiveLimit / 2;
                }
                else if (ended)
                {
                    return failed ? throw new IOException("The link to the other PC was lost.") : 0;
                }
            }
            if (size > 0)
            {
                if (reopened)
                    SendAck();
                return size;
            }
            await arrived.WaitAsync(cancel);
        }
    }

    /// <summary>No more data from this side. The other side reads to the end and sees the stream close.</summary>
    public async Task Complete()
    {
        lock (gate)
        {
            if (finSent || failed)
                return;
        }
        try
        {
            await Queue(Fin, default, CancellationToken.None);
        }
        catch (IOException)
        {
        }
        lock (gate)
            finSent = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Over)
        {
            var reset = new byte[] { Reset };
            send(reset, 1);
            lock (gate)
                Fail();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancel) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => WriteAsync(buffer.AsMemory(offset, count), cancel).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// One UDP socket shared by everything Portal does across the internet: asking the public
/// directory where a PC is, opening a path through both routers ("hole punching": each side
/// sends to the other until a packet gets through), and the reliable streams that then
/// carry the encrypted connection. Using one socket matters: the address the directory
/// learns for this PC is then the very one the other PC must send to.
/// </summary>
public sealed class PortalLink : IDisposable
{
    private const byte Magic = 0xB7;
    private const byte Punch = 1, PunchBack = 2, Open = 3, Opened = 4, Ping = 9;
    private const int MaxStreamsPerPath = 24;

    private sealed class Path
    {
        public long LastHeard = Environment.TickCount64;
        public bool Confirmed;
        public int Streams;
    }

    private readonly Socket socket;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<IPEndPoint, Path> paths = new();
    private readonly ConcurrentDictionary<(IPEndPoint, ulong), ReliableStream> streams = new();
    private readonly ConcurrentDictionary<(IPEndPoint, ulong), TaskCompletionSource<bool>> opening = new();

    public int Port { get; }
    public AddressFamily Family { get; }
    /// <summary>How many other PCs can have a path open at once (a Courier host keeps one per client).</summary>
    public int MaxPaths { get; set; } = 12;
    /// <summary>A packet that is not Portal's own (the directory's traffic) arrived.</summary>
    public Action<byte[], IPEndPoint>? Other { get; set; }
    /// <summary>The other PC opened a stream. Runs on a background thread.</summary>
    public Func<ReliableStream, IPEndPoint, Task>? Accepted { get; set; }
    /// <summary>Whether a PC that this one did not contact first may open a path (the "reachable" switch).</summary>
    public Func<IPEndPoint, bool> AllowPath { get; set; } = _ => false;

    public PortalLink(IPAddress address, int port = 0)
    {
        Family = address.AddressFamily;
        socket = new Socket(Family, SocketType.Dgram, ProtocolType.Udp);
        if (Family == AddressFamily.InterNetworkV6)
            socket.DualMode = false; // IPv4 has a link of its own
        socket.ReceiveBufferSize = socket.SendBufferSize = 4 * 1024 * 1024;
        // Windows otherwise reports "connection reset" on the socket when some packet could not be delivered.
        const int SIO_UDP_CONNRESET = -1744830452;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
        }
        socket.Bind(new IPEndPoint(address, port));
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _ = Task.Run(Receive);
        _ = Task.Run(Clock);
    }

    public void Send(byte[] packet, int length, IPEndPoint to)
    {
        try
        {
            socket.SendTo(packet, 0, length, SocketFlags.None, to);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
        }
    }

    private void SendControl(byte type, ulong stream, IPEndPoint to)
    {
        var packet = new byte[10];
        (packet[0], packet[1]) = (Magic, type);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(2), stream);
        Send(packet, packet.Length, to);
    }

    public bool IsOpen(IPEndPoint endpoint) => paths.TryGetValue(endpoint, out var path) && path.Confirmed;

    /// <summary>Paths that are open right now.</summary>
    public List<IPEndPoint> OpenPaths => paths.Where(item => item.Value.Confirmed).Select(item => item.Key).ToList();

    /// <summary>
    /// Keep sending to the other PC's public address until it answers, which opens a way
    /// through both routers as long as the other PC is doing the same. Returns whether it answered.
    /// </summary>
    public async Task<bool> PunchTo(IPEndPoint endpoint, TimeSpan patience, CancellationToken cancel = default)
    {
        if (paths.Count >= MaxPaths && !paths.ContainsKey(endpoint))
            return false;
        var path = paths.GetOrAdd(endpoint, _ => new Path());
        var until = Environment.TickCount64 + (long)patience.TotalMilliseconds;
        while (!path.Confirmed && Environment.TickCount64 < until && !cancel.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            SendControl(Punch, 0, endpoint);
            try
            {
                await Task.Delay(250, cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        if (!path.Confirmed)
            paths.TryRemove(endpoint, out _);
        return path.Confirmed;
    }

    /// <summary>Open a stream to a PC whose path is open. Throws IOException if it doesn't answer.</summary>
    public async Task<ReliableStream> Connect(IPEndPoint endpoint, CancellationToken cancel = default)
    {
        if (!paths.TryGetValue(endpoint, out var path) || !path.Confirmed)
            throw new IOException("There is no open path to the other PC.");
        var id = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8)) | 1; // odd: opened from this side
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        opening[(endpoint, id)] = answer;
        try
        {
            for (var attempt = 0; attempt < 20 && !answer.Task.IsCompleted; attempt++)
            {
                SendControl(Open, id, endpoint);
                await Task.WhenAny(answer.Task, Task.Delay(300, cancel));
            }
            if (!answer.Task.IsCompleted)
                throw new IOException("The other PC did not answer.");
        }
        finally
        {
            opening.TryRemove((endpoint, id), out _);
        }
        return Add(endpoint, id);
    }

    private ReliableStream Add(IPEndPoint endpoint, ulong id)
    {
        var header = new byte[10];
        (header[0], header[1]) = (Magic, 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2), id);
        var stream = new ReliableStream((packet, length) =>
        {
            // [magic][type][stream id] then the stream's own packet without its type byte.
            var wire = new byte[9 + length];
            wire[0] = Magic;
            wire[1] = packet[0];
            header.AsSpan(2, 8).CopyTo(wire.AsSpan(2));
            packet.AsSpan(1, length - 1).CopyTo(wire.AsSpan(10));
            Send(wire, wire.Length, endpoint);
        }, () =>
        {
            if (streams.TryRemove((endpoint, id), out _) && paths.TryGetValue(endpoint, out var path))
                Interlocked.Decrement(ref path.Streams);
        });
        streams[(endpoint, id)] = stream;
        if (paths.TryGetValue(endpoint, out var owner))
            Interlocked.Increment(ref owner.Streams);
        return stream;
    }

    private async Task Receive()
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(Family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!stopping.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stopping.Token);
            }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var from = (IPEndPoint)result.RemoteEndPoint;
            var size = result.ReceivedBytes;
            if (size < 10 || buffer[0] != Magic)
            {
                Other?.Invoke(buffer.AsSpan(0, size).ToArray(), from);
                continue;
            }
            var type = buffer[1];
            var id = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(2));
            if (type is Punch or PunchBack)
            {
                // Someone this PC is punching towards, or (when reachable) anyone who knocks.
                if (!paths.TryGetValue(from, out var path))
                {
                    if (paths.Count >= MaxPaths || !AllowPath(from))
                        continue;
                    path = paths.GetOrAdd(from, _ => new Path());
                }
                (path.Confirmed, path.LastHeard) = (true, Environment.TickCount64);
                if (type == Punch)
                    SendControl(PunchBack, 0, from);
                continue;
            }
            if (!paths.TryGetValue(from, out var known) || !known.Confirmed)
                continue; // nothing is accepted from an address no path was opened with
            known.LastHeard = Environment.TickCount64;
            switch (type)
            {
                case Ping:
                    break;
                case Open:
                    if (!streams.ContainsKey((from, id)))
                    {
                        if (known.Streams >= MaxStreamsPerPath || Accepted is null)
                            break;
                        var stream = Add(from, id);
                        _ = Task.Run(() => Accepted(stream, from));
                    }
                    SendControl(Opened, id, from);
                    break;
                case Opened:
                    if (opening.TryGetValue((from, id), out var waiting))
                        waiting.TrySetResult(true);
                    break;
                default:
                    if (streams.TryGetValue((from, id), out var target))
                        target.OnPacket(type, buffer.AsSpan(10, size - 10));
                    break;
            }
        }
    }

    /// <summary>The link's heartbeat: the streams' timers, a small packet now and then to keep the routers' doors open, and forgetting silent paths.</summary>
    private async Task Clock()
    {
        var ticks = 0;
        while (!stopping.IsCancellationRequested)
        {
            foreach (var stream in streams.Values)
                stream.Tick();
            if (++ticks % 1000 == 0)
            {
                var now = Environment.TickCount64;
                foreach (var (endpoint, path) in paths)
                {
                    if (!path.Confirmed)
                        continue;
                    if (now - path.LastHeard > 75_000)
                        paths.TryRemove(endpoint, out _);
                    else
                        SendControl(Ping, 0, endpoint);
                }
            }
            try
            {
                await Task.Delay(10, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        foreach (var stream in streams.Values)
            stream.Dispose();
        socket.Dispose();
    }
}
