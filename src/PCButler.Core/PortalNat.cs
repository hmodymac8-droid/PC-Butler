// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCButler.Core;

/// <summary>A port this PC's router was asked to forward to it: how, and where it is seen from the internet.</summary>
public sealed record PortalMapping(string Method, IPAddress? External, int ExternalPort, int InternalPort);

/// <summary>
/// Asks the home router to forward one UDP port to this PC, the way games and torrent
/// programs do, so PCs on other networks can reach the Courier host without both sides
/// having to open a path at the same moment. Two ways are tried: NAT-PMP / PCP (Apple and
/// many others) and UPnP (most home routers). Both only work when the router allows it;
/// a router that doesn't simply leaves Courier to the usual path opening.
/// </summary>
public static partial class PortalNat
{
    private const int NatPmpPort = 5351;
    private static readonly IPEndPoint Ssdp = new(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>Ask for the port; null when the router has no way to ask, or said no. Lifetime is renewed by asking again.</summary>
    public static async Task<PortalMapping?> Map(int port, CancellationToken cancel = default)
    {
        if (Gateway() is { } gateway && await NatPmp(gateway, port, 3600, cancel) is { } pmp)
            return pmp;
        return await Upnp(port, cancel, remove: false);
    }

    /// <summary>Give the port back to the router (when Courier stops).</summary>
    public static async Task Unmap(PortalMapping mapping)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (mapping.Method == "NAT-PMP" && Gateway() is { } gateway)
                await NatPmp(gateway, mapping.InternalPort, 0, limit.Token);
            else if (mapping.Method == "UPnP")
                await Upnp(mapping.InternalPort, limit.Token, remove: true, externalPort: mapping.ExternalPort);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or HttpRequestException)
        {
        }
    }

    /// <summary>The default gateway of the connection that leads to the internet (IPv4).</summary>
    public static IPAddress? Gateway()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var gateway = adapter.GetIPProperties().GatewayAddresses
                .Select(item => item.Address).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
            if (gateway is not null)
                return gateway;
        }
        return null;
    }

    /// <summary>This PC's address on the network that leads to the given host (what the router must forward to).</summary>
    private static IPAddress? LocalAddressTowards(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 9); // no packet is sent: this only picks the route
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // --- NAT-PMP (RFC 6886) ---

    private static async Task<PortalMapping?> NatPmp(IPAddress gateway, int port, int lifetime, CancellationToken cancel)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var request = new byte[12];
        (request[0], request[1]) = (0, 1); // version 0, map a UDP port
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)(lifetime == 0 ? 0 : port));
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), (uint)lifetime);
        var answer = await Exchange(socket, request, new IPEndPoint(gateway, NatPmpPort), 16, cancel);
        if (ParseNatPmpMapping(answer) is not { } external || lifetime == 0)
            return null;
        IPAddress? publicAddress = null;
        var address = await Exchange(socket, [0, 0], new IPEndPoint(gateway, NatPmpPort), 12, cancel);
        if (address is { Length: >= 12 } && address[1] == 128 && BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(2)) == 0)
            publicAddress = new IPAddress(address.AsSpan(8, 4));
        return new PortalMapping("NAT-PMP", publicAddress, external, port);
    }

    /// <summary>The external port in a NAT-PMP answer to a UDP mapping, or null when the router refused.</summary>
    public static int? ParseNatPmpMapping(byte[]? answer) =>
        answer is { Length: >= 16 } && answer[0] == 0 && answer[1] == 129 && BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2)) == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(10)) : null;

    private static async Task<byte[]?> Exchange(UdpClient socket, byte[] request, IPEndPoint to, int least, CancellationToken cancel)
    {
        // RFC 6886: ask again after 250 ms, then 500 ms. A router without NAT-PMP never answers.
        foreach (var wait in new[] { 250, 500, 1000 })
        {
            await socket.SendAsync(request, to, cancel);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(wait);
            try
            {
                var reply = await socket.ReceiveAsync(limit.Token);
                if (reply.RemoteEndPoint.Address.Equals(to.Address) && reply.Buffer.Length >= least)
                    return reply.Buffer;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
                return null;
            }
        }
        return null;
    }

    // --- UPnP Internet Gateway Device ---

    private static async Task<PortalMapping?> Upnp(int port, CancellationToken cancel, bool remove, int externalPort = 0)
    {
        foreach (var location in await FindGateways(cancel))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var description = await http.GetStringAsync(location, cancel);
                if (ParseControl(description, location) is not var (control, service))
                    continue;
                if (remove)
                {
                    await Soap(http, control, service, "DeletePortMapping",
                               $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{externalPort}</NewExternalPort><NewProtocol>UDP</NewProtocol>", cancel);
                    return null;
                }
                if (LocalAddressTowards(IPAddress.Parse(location.Host)) is not { } inside)
                    continue;
                // The same port number outside when free; some routers refuse leases, which then last until removed.
                foreach (var outside in new[] { port, port + 1, port + 2, 30000 + port % 30000 })
                {
                    foreach (var lease in new[] { 3600, 0 })
                    {
                        var (ok, _) = await Soap(http, control, service, "AddPortMapping",
                            $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{outside}</NewExternalPort><NewProtocol>UDP</NewProtocol>"
                            + $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{inside}</NewInternalClient><NewEnabled>1</NewEnabled>"
                            + $"<NewPortMappingDescription>PC Butler Courier</NewPortMappingDescription><NewLeaseDuration>{lease}</NewLeaseDuration>", cancel);
                        if (!ok)
                            continue;
                        var (_, body) = await Soap(http, control, service, "GetExternalIPAddress", "", cancel);
                        var external = Regex.Match(body, @"<NewExternalIPAddress>([^<]+)<") is { Success: true } match
                                       && IPAddress.TryParse(match.Groups[1].Value.Trim(), out var parsed) ? parsed : null;
                        return new PortalMapping("UPnP", external, outside, port);
                    }
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Xml.XmlException or UriFormatException or FormatException)
            {
            }
        }
        return null;
    }

    /// <summary>Ask the network who is a gateway; each answer names where its description is.</summary>
    private static async Task<List<Uri>> FindGateways(CancellationToken cancel)
    {
        var found = new List<Uri>();
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        foreach (var kind in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1",
                                     "urn:schemas-upnp-org:device:InternetGatewayDevice:2" })
        {
            var search = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {kind}\r\n\r\n");
            await socket.SendAsync(search, Ssdp, cancel);
        }
        var until = Environment.TickCount64 + 2500;
        while (Environment.TickCount64 < until)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter((int)Math.Max(1, until - Environment.TickCount64));
            try
            {
                var reply = await socket.ReceiveAsync(limit.Token);
                if (ParseSsdpLocation(Encoding.ASCII.GetString(reply.Buffer)) is { } location && found.All(known => known != location))
                    found.Add(location);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
        return found;
    }

    /// <summary>The LOCATION of an SSDP answer, when it points into the local network over plain HTTP.</summary>
    public static Uri? ParseSsdpLocation(string answer)
    {
        var match = Regex.Match(answer, @"^LOCATION:\s*(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) && uri.Scheme == "http"
               && IPAddress.TryParse(uri.Host, out var host) && host.AddressFamily == AddressFamily.InterNetwork ? uri : null;
    }

    /// <summary>From a gateway's description: where to send requests, and as which service (the IP or the PPP connection).</summary>
    public static (Uri Control, string Service)? ParseControl(string description, Uri location)
    {
        var document = XDocument.Parse(description);
        var baseText = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "URLBase")?.Value.Trim();
        var root = baseText is { Length: > 0 } && Uri.TryCreate(baseText, UriKind.Absolute, out var given) ? given : location;
        foreach (var service in document.Descendants().Where(element => element.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(element => element.Name.LocalName == "serviceType")?.Value.Trim() ?? "";
            var control = service.Elements().FirstOrDefault(element => element.Name.LocalName == "controlURL")?.Value.Trim() ?? "";
            if (Regex.IsMatch(type, @"^urn:schemas-upnp-org:service:WAN(IP|PPP)Connection:\d$") && control.Length > 0
                && Uri.TryCreate(root, control, out var url) && url.Host == location.Host)
                return (url, type);
        }
        return null;
    }

    private static async Task<(bool Ok, string Body)> Soap(HttpClient http, Uri control, string service, string action, string arguments, CancellationToken cancel)
    {
        var envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
                       + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
                       + $"<u:{action} xmlns:u=\"{service}\">{arguments}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        using var response = await http.SendAsync(request, cancel);
        return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancel));
    }
}
