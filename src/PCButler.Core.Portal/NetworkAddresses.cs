// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCButler.Core;

/// <summary>This PC's addresses: on its own network, and as the internet sees it.</summary>
public static class NetworkAddresses
{
    /// <summary>The address on the local network: that of the adapter the internet is reached through.</summary>
    public static string Local()
    {
        try
        {
            // Nothing is sent: a UDP socket only works out which adapter would be used.
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("1.1.1.1", 53);
            return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return "";
        }
    }

    /// <summary>The address the internet sees, as Cloudflare reports it back; "" when there is no connection.</summary>
    public static async Task<string> Public(CancellationToken cancel = default) =>
        await Public(System.Net.Sockets.AddressFamily.InterNetwork, cancel) is { Length: > 0 } four ? four
            : await Public(System.Net.Sockets.AddressFamily.InterNetworkV6, cancel);

    /// <summary>Asked over IPv4 first, as that is the address people mean; over IPv6 when the connection has no IPv4.</summary>
    private static async Task<string> Public(System.Net.Sockets.AddressFamily family, CancellationToken cancel)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, stop) =>
                {
                    var socket = new System.Net.Sockets.Socket(family, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, stop);
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, stop);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            var text = await client.GetStringAsync("https://speed.cloudflare.com/cdn-cgi/trace", cancel);
            var line = text.Split('\n').FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal));
            return line is not null && System.Net.IPAddress.TryParse(line[3..].Trim(), out var address) ? address.ToString() : "";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            return "";
        }
    }
}
