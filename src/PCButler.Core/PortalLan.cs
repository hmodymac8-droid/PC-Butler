// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PCButler.Core;

/// <summary>
/// Finding a PC or phone on the same network (the same Wi-Fi or cable) in a second, without
/// the internet. Through the public directory, two devices behind the same router only find
/// each other's public address, and many routers will not send a packet from inside back in
/// (no "hairpin"), so the link never opened. Here a device asks the whole local network, by
/// broadcast, "who has ID 205 937?", and the one that has it answers with the port of its
/// link; both then send to each other, which opens the path at once (and lets the answer
/// through Windows' firewall). Who the other side really is, is still settled by its certificate.
/// </summary>
public sealed class PortalLan : IDisposable
{
    /// <summary>The port every Courier listens on for these questions.</summary>
    public const int Port = 47391;
    private const string Ask = "pcbutler-courier-lan1 ask", Here = "pcbutler-courier-lan1 here";

    private readonly Socket? listener;
    private readonly Func<string, bool> mine;
    private readonly Func<int> linkPort;
    private readonly Action<IPEndPoint> reach;
    private readonly CancellationTokenSource stopping = new();

    /// <summary>
    /// mine tells whether an ID is this device's; linkPort is the port of its IPv4 link; reach
    /// opens a path towards the link of a device that asked for it.
    /// </summary>
    public PortalLan(Func<string, bool> mine, Func<int> linkPort, Action<IPEndPoint> reach)
    {
        (this.mine, this.linkPort, this.reach) = (mine, linkPort, reach);
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Another program of PC Butler's (or a second user's) may listen too: all of them hear the question.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            listener = socket;
            _ = Task.Run(Listen);
        }
        catch (SocketException error)
        {
            // Asking still works; only being found on this network does not.
            Log.Info($"Courier: can't listen for devices on this network ({error.Message})");
        }
    }

    private async Task Listen()
    {
        var buffer = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!stopping.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await listener!.ReceiveFromAsync(buffer, SocketFlags.None, any, stopping.Token);
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
            var parts = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes).Split(' ');
            // "pcbutler-courier-lan1 ask 205937 51234": the ID asked for, and the port of the asker's link.
            if (parts.Length != 4 || $"{parts[0]} {parts[1]}" != Ask || !mine(parts[2]) || !int.TryParse(parts[3], out var theirs) || theirs is < 1 or > 65535)
                continue;
            var answer = Encoding.ASCII.GetBytes($"{Here} {parts[2]} {linkPort()}");
            try
            {
                listener.SendTo(answer, from);
            }
            catch (SocketException)
            {
            }
            reach(new IPEndPoint(from.Address, theirs));
        }
    }

    /// <summary>
    /// Ask this network for the device with that ID. Returns the address of its link as soon
    /// as it answers, or nothing after "patience" (it is not on this network, or not running).
    /// </summary>
    public async Task<List<IPEndPoint>> Find(string number, int myLinkPort, TimeSpan patience, CancellationToken cancel = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (SocketException)
        {
            return [];
        }
        var question = Encoding.ASCII.GetBytes($"{Ask} {number} {myLinkPort}");
        var targets = Broadcasts();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, stopping.Token);
        limit.CancelAfter(patience);
        // Asked a few times: a packet can be lost, and a phone's Wi-Fi may be dozing.
        _ = Task.Run(async () =>
        {
            for (var round = 0; round < 4 && !limit.IsCancellationRequested; round++)
            {
                foreach (var target in targets)
                {
                    try
                    {
                        socket.SendTo(question, target);
                    }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    {
                    }
                }
                try
                {
                    await Task.Delay(350, limit.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        var found = new List<IPEndPoint>();
        var buffer = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (found.Count == 0)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, limit.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            var parts = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes).Split(' ');
            if (parts.Length == 4 && $"{parts[0]} {parts[1]}" == Here && parts[2] == number && int.TryParse(parts[3], out var port) && port is > 0 and <= 65535)
                found.Add(new IPEndPoint(((IPEndPoint)result.RemoteEndPoint).Address, port));
        }
        return found;
    }

    /// <summary>Where to ask: the broadcast address of every network this device is on, and the general one.</summary>
    private static List<IPEndPoint> Broadcasts()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is not { } mask || mask.Equals(IPAddress.Any))
                        continue;
                    var bytes = address.Address.GetAddressBytes();
                    var masks = mask.GetAddressBytes();
                    for (var index = 0; index < 4; index++)
                        bytes[index] |= (byte)~masks[index];
                    var target = new IPEndPoint(new IPAddress(bytes), Port);
                    if (!targets.Contains(target))
                        targets.Add(target);
                }
            }
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException or NotImplementedException)
        {
            // The general broadcast alone.
        }
        return targets;
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener?.Dispose();
    }
}
