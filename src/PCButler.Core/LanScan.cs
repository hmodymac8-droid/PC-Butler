// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>One device on the local network, as the scan found it or as it was remembered.</summary>
public sealed class LanDevice
{
    public string Ip { get; set; } = "";
    /// <summary>"AA-BB-CC-DD-EE-FF", or "" when it is not known.</summary>
    public string Mac { get; set; } = "";
    /// <summary>What the network calls it (its DNS or Windows name).</summary>
    public string HostName { get; set; } = "";
    /// <summary>The maker of its network adapter, from the first half of its MAC address.</summary>
    public string Vendor { get; set; } = "";
    /// <summary>The name the user gave it, or "".</summary>
    public string Name { get; set; } = "";
    /// <summary>A guess at what it is, in English: "Router", "Printer", "Windows PC"…; "" when there is nothing to go on.</summary>
    public string Kind { get; set; } = "";
    public bool Online { get; set; }
    /// <summary>Its answer time to a ping in milliseconds; -1 when it did not answer one (it may still be online).</summary>
    public int PingMs { get; set; } = -1;
    public bool IsGateway { get; set; }
    public bool IsThisPc { get; set; }
    /// <summary>The well-known ports that took a connection: 80 a web page, 445 shared folders, 3389 Remote Desktop…</summary>
    public List<int> OpenPorts { get; set; } = [];
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>The MAC address was made up by the device (phones and tablets do this per network for privacy).</summary>
    public bool RandomMac => Mac.Length >= 2 && (Convert.ToInt32(Mac[..2], 16) & 0x02) != 0;

    /// <summary>What it is remembered by: the MAC address, which stays when the router hands out another IP.</summary>
    public string Key => Mac.Length > 0 ? Mac : Ip;

    /// <summary>The name to show: the user's, else the network's, else the maker's.</summary>
    public string DisplayName => Name.Length > 0 ? Name : HostName.Length > 0 ? HostName : Vendor;

    public uint IpNumber => IPAddress.TryParse(Ip, out var address) && address.GetAddressBytes() is { Length: 4 } b
        ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : uint.MaxValue;
}

/// <summary>The network this PC is on: its address and mask, the router, the adapter.</summary>
public sealed record LanNetwork(IPAddress Address, IPAddress Mask, IPAddress? Gateway, string Adapter, string Mac)
{
    public int PrefixLength => Mask.GetAddressBytes().Sum(part => System.Numerics.BitOperations.PopCount(part));
    public override string ToString() => $"{Network(Address, Mask)}/{PrefixLength}";

    internal static IPAddress Network(IPAddress address, IPAddress mask)
    {
        var (a, m) = (address.GetAddressBytes(), mask.GetAddressBytes());
        return new IPAddress(a.Select((part, index) => (byte)(part & m[index])).ToArray());
    }
}

/// <summary>
/// Scanning the local network: every address of this PC's network (up to 1 022, else the 254 around this PC) is
/// pinged and asked for its MAC address (ARP) at the same time, so devices that ignore pings (phones, PCs with a
/// firewall) are found too. The ones found are then asked their names (DNS, then Windows' NetBIOS) and tried on a
/// few well-known ports, to tell what they are and how to connect to them. Nothing leaves the local network except
/// the list of adapter makers, read once from Wireshark's public list (IEEE's as a fall-back) and kept for 120 days.
/// </summary>
public static class LanScanner
{
    /// <summary>The ports tried on each device, and what each one means.</summary>
    public static readonly (int Port, string Meaning)[] Ports =
    [
        (80, "Web page"), (443, "Secure web page"), (8080, "Web page"), (22, "SSH"), (445, "Shared folders"),
        (3389, "Remote Desktop"), (5900, "VNC"), (631, "Printer (IPP)"), (9100, "Printer"), (8009, "Cast (TV, speaker)"),
        (62078, "iPhone or iPad"), (554, "Camera (RTSP)"),
    ];

    /// <summary>The network the internet is reached through, or null when there is none.</summary>
    public static LanNetwork? Current()
    {
        var local = NetworkAddresses.Local();
        LanNetwork? fallback = null;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Select(entry => entry.Address)
                                        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)
                        || unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    var mac = FormatMac(adapter.GetPhysicalAddress().GetAddressBytes());
                    var network = new LanNetwork(unicast.Address, unicast.IPv4Mask, gateway, adapter.Name, mac);
                    if (unicast.Address.ToString() == local)
                        return network;
                    if (gateway is not null)
                        fallback ??= network;
                }
            }
        }
        catch (NetworkInformationException error)
        {
            Log.Warning($"LAN scan: the adapters could not be read ({error.Message})");
        }
        return fallback;
    }

    /// <summary>The addresses to try: the whole network when it is small, else the 254 next to this PC.</summary>
    public static List<IPAddress> Hosts(LanNetwork network)
    {
        var prefix = network.PrefixLength;
        var mask = network.Mask;
        if (prefix < 22)
        {
            prefix = 24;
            mask = IPAddress.Parse("255.255.255.0");
        }
        var start = Number(LanNetwork.Network(network.Address, mask));
        var count = (1u << (32 - prefix)) - 2;
        var hosts = new List<IPAddress>((int)count);
        for (var offset = 1u; offset <= count; offset++)
            hosts.Add(Address(start + offset));
        return hosts;
    }

    /// <summary>Scan the network. progress gets (done, total) as the addresses are tried.</summary>
    public static async Task<(LanNetwork? Network, List<LanDevice> Devices)> Scan(Action<int, int>? progress, CancellationToken cancel)
    {
        var network = Current();
        if (network is null)
            return (null, []);
        var vendors = LanVendors.Ready(cancel); // read or fetched while the network is swept
        var hosts = Hosts(network).Where(host => !host.Equals(network.Address)).ToList();
        var found = new ConcurrentDictionary<string, LanDevice>();
        var done = 0;
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 128, CancellationToken = cancel }, async (host, stop) =>
        {
            var arp = Task.Factory.StartNew(() => Arp(host), stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var ping = Ping(host, stop);
            var (mac, ms) = (await arp, await ping);
            if (mac.Length > 0 || ms >= 0)
                found[host.ToString()] = new LanDevice { Ip = host.ToString(), Mac = mac, PingMs = ms, Online = true, IsGateway = host.Equals(network.Gateway) };
            progress?.Invoke(Interlocked.Increment(ref done), hosts.Count);
        });

        var devices = found.Values.ToList();
        devices.Add(new LanDevice
        {
            Ip = network.Address.ToString(), Mac = network.Mac, HostName = Environment.MachineName, Online = true, PingMs = 0, IsThisPc = true,
        });
        // Names and ports, all devices at once.
        await Task.WhenAll(devices.Select(async device =>
        {
            var address = IPAddress.Parse(device.Ip);
            var ports = OpenPorts(address, cancel);
            if (device.HostName.Length == 0)
                device.HostName = await DnsName(address, cancel) is { Length: > 0 } dns ? dns : await NetBiosName(address, cancel);
            device.OpenPorts = await ports;
        }));
        try
        {
            await vendors.WaitAsync(TimeSpan.FromSeconds(20), cancel);
        }
        catch (TimeoutException)
        {
        }
        var now = DateTime.Now;
        foreach (var device in devices)
        {
            device.Vendor = LanVendors.Of(device.Mac);
            device.Kind = Guess(device);
            device.FirstSeen = device.LastSeen = now;
        }
        return (network, devices);
    }

    /// <summary>A guess at what a device is, from its ports, its name and its maker.</summary>
    public static string Guess(LanDevice device)
    {
        bool Has(int port) => device.OpenPorts.Contains(port);
        bool Named(params string[] words) => words.Any(word => device.HostName.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                                || device.Vendor.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (device.IsThisPc)
            return "This PC";
        if (device.IsGateway)
            return "Router";
        if (Has(9100) || Has(631) || Named("printer", "epson", "canon", "brother", "lexmark", "kyocera"))
            return "Printer";
        if (Has(62078) || Named("iphone", "ipad"))
            return "iPhone or iPad";
        if (Has(8009) || Named("chromecast", "roku", "firetv", "bravia", "-tv", "smarttv"))
            return "TV or speaker";
        if (Has(554) || Named("hikvision", "dahua", "camera", "ipcam", "reolink"))
            return "Camera";
        if (Has(3389) || (Has(445) && !Has(22)))
            return "Windows PC";
        if (Named("android", "galaxy", "pixel", "redmi", "xiaomi", "oneplus", "vivo", "oppo", "huawei", "honor"))
            return "Phone or tablet";
        if (Has(22) || Has(445))
            return "Computer or server";
        if (Named("macbook", "imac", "mac-mini"))
            return "Mac";
        if (device.RandomMac)
            return "Phone or tablet";
        return "";
    }

    // --- asking one device ---

    private static async Task<int> Ping(IPAddress host, CancellationToken cancel)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(900), null, null, cancel);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (Exception error) when (error is PingException or SocketException or InvalidOperationException)
        {
            return -1;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destination, uint source, byte[] mac, ref uint length);

    /// <summary>The device's MAC address, asked for on the network (ARP); "" when nothing has that address.</summary>
    private static string Arp(IPAddress host)
    {
        var mac = new byte[6];
        var length = (uint)mac.Length;
        var result = SendARP(BitConverter.ToUInt32(host.GetAddressBytes(), 0), 0, mac, ref length);
        if (result != 0 || length < 6 || mac.All(part => part == 0) || mac.All(part => part == 0xFF))
            return "";
        return FormatMac(mac);
    }

    private static async Task<List<int>> OpenPorts(IPAddress host, CancellationToken cancel)
    {
        var open = new ConcurrentBag<int>();
        await Task.WhenAll(Ports.Select(entry => entry.Port).Distinct().Select(async port =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(700);
            try
            {
                await socket.ConnectAsync(host, port, timeout.Token);
                open.Add(port);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
            }
        }));
        return [.. open.Order()];
    }

    private static async Task<string> DnsName(IPAddress host, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(2000);
            var entry = await Dns.GetHostEntryAsync(host.ToString(), timeout.Token);
            var name = entry.HostName;
            // Some routers answer with the address itself.
            if (name.Length == 0 || IPAddress.TryParse(name, out _))
                return "";
            return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".home", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase)
                ? name[..name.LastIndexOf('.')] : name;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>A Windows PC's (or NAS's) own name, asked with a NetBIOS "node status" question on UDP port 137.</summary>
    private static async Task<string> NetBiosName(IPAddress host, CancellationToken cancel)
    {
        var question = new byte[50];
        question[0] = 0x50; question[1] = 0x42; // any transaction number
        question[5] = 1; // one question
        question[12] = 0x20;
        question[13] = (byte)'C'; question[14] = (byte)'K'; // "*", encoded
        for (var index = 15; index < 45; index++)
            question[index] = (byte)'A';
        question[47] = 0x21; // NBSTAT
        question[49] = 0x01; // IN
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(1200);
            await socket.SendAsync(question, new IPEndPoint(host, 137), timeout.Token);
            var answer = (await socket.ReceiveAsync(timeout.Token)).Buffer;
            if (answer.Length < 57)
                return "";
            int count = answer[56], at = 57;
            for (var index = 0; index < count && at + 18 <= answer.Length; index++, at += 18)
            {
                var suffix = answer[at + 15];
                var group = (answer[at + 16] & 0x80) != 0;
                if (suffix == 0x00 && !group)
                    return Encoding.ASCII.GetString(answer, at, 15).Trim();
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
        }
        return "";
    }

    // --- helpers ---

    public static string FormatMac(byte[] bytes) =>
        bytes.Length >= 6 && !bytes.All(part => part == 0) ? string.Join("-", bytes.Take(6).Select(part => part.ToString("X2"))) : "";

    private static uint Number(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress Address(uint number) =>
        new([(byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number]);
}

/// <summary>
/// The makers of network adapters, by the first three bytes of a MAC address. Read from oui.txt in the data folder;
/// fetched from Wireshark's public list (IEEE's when that fails) when it is missing or older than 120 days.
/// </summary>
public static class LanVendors
{
    private static Dictionary<string, string>? table;
    private static Task? loading;
    private static readonly object Gate = new();

    private static string File_ => Path.Combine(AppInfo.DataDir, "oui.txt");

    /// <summary>Done when the list can be used (or could not be had).</summary>
    public static Task Ready(CancellationToken cancel)
    {
        lock (Gate)
            return loading is { IsFaulted: false } ? loading : loading = Task.Run(() => Load(CancellationToken.None), CancellationToken.None);
    }

    /// <summary>The maker for a MAC address; "" when not known. A made-up (private) address has no maker.</summary>
    public static string Of(string mac)
    {
        if (mac.Length < 8 || table is null)
            return "";
        var key = mac[..8].Replace("-", "").ToUpperInvariant();
        return table.GetValueOrDefault(key, "");
    }

    private static async Task Load(CancellationToken cancel)
    {
        var fresh = File.Exists(File_) && File.GetLastWriteTime(File_) > DateTime.Now.AddDays(-120);
        if (!fresh)
            await Fetch(cancel);
        try
        {
            if (File.Exists(File_))
                table = File.ReadLines(File_).Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2 && parts[0].Length == 6)
                            .GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1]);
        }
        catch (IOException error)
        {
            Log.Warning($"LAN scan: the list of adapter makers could not be read ({error.Message})");
        }
    }

    private static async Task Fetch(CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (compatible; PCButler/{AppInfo.Version})");
        var rows = new Dictionary<string, string>();
        try
        {
            // Wireshark's list: "00:00:0C<tab>Cisco<tab>Cisco Systems, Inc"; longer prefixes ("/28") are left out.
            var text = await http.GetStringAsync("https://www.wireshark.org/download/automated/data/manuf", cancel);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || parts[0].Length != 8 || parts[0].StartsWith('#'))
                    continue;
                rows.TryAdd(parts[0].Replace(":", "").ToUpperInvariant(), (parts.Length > 2 && parts[2].Length > 0 ? parts[2] : parts[1]).Trim());
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"LAN scan: Wireshark's list of adapter makers could not be read ({error.Message})");
        }
        if (rows.Count < 1000)
        {
            try
            {
                // IEEE's own list: "MA-L,002272,American Micro-Fuel Device Corp.,address"
                var text = await http.GetStringAsync("https://standards-oui.ieee.org/oui/oui.csv", cancel);
                foreach (var line in text.Split('\n').Skip(1))
                {
                    var parts = SplitCsv(line.TrimEnd('\r'));
                    if (parts.Count >= 3 && parts[1].Length == 6)
                        rows.TryAdd(parts[1].ToUpperInvariant(), parts[2].Trim());
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                Log.Info($"LAN scan: IEEE's list of adapter makers could not be read ({error.Message})");
            }
        }
        if (rows.Count < 1000)
            return;
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllLines(File_, rows.Select(row => $"{row.Key}\t{row.Value.Replace('\t', ' ')}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"LAN scan: the list of adapter makers could not be saved ({error.Message})");
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var c = line[index];
            if (c == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                    quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }
}

/// <summary>
/// The devices seen on this network before, with the names the user gave them, kept in lan-devices.json in the data
/// folder, so a device that is off still shows (as offline) and keeps its name. Used from the window's thread only.
/// </summary>
public static class LanBook
{
    private sealed class Saved
    {
        [JsonPropertyName("ip")] public string Ip { get; set; } = "";
        [JsonPropertyName("mac")] public string Mac { get; set; } = "";
        [JsonPropertyName("host")] public string HostName { get; set; } = "";
        [JsonPropertyName("vendor")] public string Vendor { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("ports")] public List<int> Ports { get; set; } = [];
        [JsonPropertyName("gateway")] public bool Gateway { get; set; }
        [JsonPropertyName("first_seen")] public DateTime FirstSeen { get; set; }
        [JsonPropertyName("last_seen")] public DateTime LastSeen { get; set; }
    }

    private static List<Saved>? saved;
    private static string File_ => Path.Combine(AppInfo.DataDir, "lan-devices.json");

    private static List<Saved> Loaded()
    {
        if (saved is not null)
            return saved;
        try
        {
            saved = File.Exists(File_) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(File_)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"LAN scan: the remembered devices could not be read ({error.Message})");
        }
        return saved ??= [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(File_, JsonSerializer.Serialize(Loaded(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"LAN scan: the remembered devices could not be saved ({error.Message})");
        }
    }

    private static string KeyOf(Saved entry) => entry.Mac.Length > 0 ? entry.Mac : entry.Ip;

    private static LanDevice Offline(Saved entry) => new()
    {
        Ip = entry.Ip, Mac = entry.Mac, HostName = entry.HostName, Vendor = entry.Vendor, Name = entry.Name, Kind = entry.Kind,
        OpenPorts = entry.Ports, IsGateway = entry.Gateway, FirstSeen = entry.FirstSeen, LastSeen = entry.LastSeen,
    };

    /// <summary>Every remembered device, all offline (before a scan).</summary>
    public static List<LanDevice> Remembered() => Loaded().Select(Offline).ToList();

    /// <summary>
    /// Take in what a scan found: each found device gets its saved name, and is remembered (or updated); the
    /// remembered ones not found are added as offline. This PC is not remembered.
    /// </summary>
    public static List<LanDevice> Merge(List<LanDevice> found)
    {
        var all = Loaded();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in found)
        {
            keys.Add(device.Key);
            if (device.IsThisPc)
            {
                device.Name = all.FirstOrDefault(entry => string.Equals(KeyOf(entry), device.Key, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
                continue;
            }
            var entry = all.FirstOrDefault(entry => string.Equals(KeyOf(entry), device.Key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new Saved { FirstSeen = device.FirstSeen };
                all.Add(entry);
            }
            device.Name = entry.Name;
            device.FirstSeen = entry.FirstSeen;
            entry.Ip = device.Ip;
            entry.Mac = device.Mac;
            if (device.HostName.Length > 0)
                entry.HostName = device.HostName;
            else
                device.HostName = entry.HostName;
            if (device.Vendor.Length > 0)
                entry.Vendor = device.Vendor;
            else
                device.Vendor = entry.Vendor;
            if (device.Kind.Length == 0)
                device.Kind = entry.Kind;
            entry.Kind = device.Kind;
            entry.Ports = device.OpenPorts;
            entry.Gateway = device.IsGateway;
            entry.LastSeen = device.LastSeen;
        }
        Save();
        // A remembered device whose address another device now has is not shown twice: it is offline under its old address.
        return [.. found, .. all.Where(entry => !keys.Contains(KeyOf(entry))).Select(Offline)];
    }

    /// <summary>Give a device a name ("" goes back to the network's name).</summary>
    public static void Rename(LanDevice device, string name)
    {
        var all = Loaded();
        var entry = all.FirstOrDefault(entry => string.Equals(KeyOf(entry), device.Key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new Saved
            {
                Ip = device.Ip, Mac = device.Mac, HostName = device.HostName, Vendor = device.Vendor, Kind = device.Kind,
                Ports = device.OpenPorts, Gateway = device.IsGateway, FirstSeen = device.FirstSeen, LastSeen = device.LastSeen,
            };
            all.Add(entry);
        }
        entry.Name = device.Name = name.Trim();
        Save();
    }

    /// <summary>Forget a device (one that is offline): it no longer shows until it is found again.</summary>
    public static void Forget(LanDevice device)
    {
        Loaded().RemoveAll(entry => string.Equals(KeyOf(entry), device.Key, StringComparison.OrdinalIgnoreCase));
        Save();
    }
}
