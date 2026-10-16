// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Courier as the whole program sees it: starting and stopping it (at launch, from its page,
/// or from the widget), the PCs files can go to, and sending to one of them.
/// </summary>
public static class Courier
{
    private static bool starting;

    /// <summary>A length of time as 0:42, 12:05 or 1:02:33.</summary>
    public static string Time(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    /// <summary>"2:10 elapsed · 0:45 left" for a transfer that has started moving; "" before that.</summary>
    public static string Times(PortalTransfer transfer)
    {
        if (transfer.Elapsed < TimeSpan.FromSeconds(1))
            return "";
        var text = Ui.T("{0} elapsed", Time(transfer.Elapsed));
        return transfer.Remaining is { } left ? text + "  ·  " + Ui.T("{0} left", Time(left)) : text;
    }

    /// <summary>What a transfer is doing, in words.</summary>
    public static string State(PortalTransfer transfer) => Ui.T(transfer.State switch
    {
        "waiting" => transfer.Incoming ? "Waiting for your answer" : "Waiting for the other PC to accept",
        "done" => "Done", "cancelled" => "Cancelled", "refused" => "Refused", "failed" => "Failed",
        _ => transfer.Paused ? "Paused" : transfer.PausedThere ? "Paused on the other PC" : "Moving…",
    });

    /// <summary>The running node, if any.</summary>
    public static PortalNode? Node => Portal.Current is { Stopped: false } node ? node : null;

    /// <summary>The widget setting, kept here so it is not read from disk every second.</summary>
    public static bool WidgetOn { get; set; } = true;

    /// <summary>Courier started or stopped. Raised on the window's thread.</summary>
    public static event Action? StateChanged;

    /// <summary>One PC files can go to: the host, a client of the host, or (through the host) another client.</summary>
    public sealed record Target(string Name, object Tag, bool Ready);

    /// <summary>A change to the saved settings, made through the window when it is open so it sees the change.</summary>
    public static void UpdateConfig(Action<Config> change)
    {
        if (App.Current.Workspace is { } window)
        {
            window.UpdateConfig(change);
            return;
        }
        var config = ConfigStore.Load();
        change(config);
        ConfigStore.Save(config);
    }

    /// <summary>Start Courier with the saved settings. Returns why it could not, or null.</summary>
    public static async Task<string?> Start()
    {
        if (Node is not null || starting)
            return null;
        var settings = ConfigStore.Load().Portal;
        if (settings.Role is not (PortalSettings.Host or PortalSettings.Client))
            return Ui.T("Choose first whether this PC shares (the host) or connects to another PC (a client).");
        if (settings.Role == PortalSettings.Client && !Portal.IsNumber(settings.HostNumber) && settings.Devices.Count == 0)
            return Ui.T("Type the ID of the PC to connect to first: the six digits it shows in Courier.");
        starting = true;
        try
        {
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            Portal.ClearRelay();
            // The test hook keeps Courier on this PC only, so trying the page never opens a port to the internet.
            var local = Environment.GetEnvironmentVariable("PCBUTLER_PORTAL_LOOPBACK") == "1";
            var node = await PortalNode.Start(settings, () => Application.Current.Dispatcher.BeginInvoke(() =>
                UpdateConfig(config => { config.Portal.Devices = settings.Devices.ToList(); config.Portal.HostNumber = settings.HostNumber; })), local: local);
            node.Attention += App.Current.PortalNeedsAttention;
            Portal.Current = node;
            return null;
        }
        catch (IOException error)
        {
            Log.Warning($"Courier could not start: {error.Message}");
            return Ui.T("Courier could not start.") + "\n\n" + error.Message;
        }
        finally
        {
            starting = false;
            StateChanged?.Invoke();
        }
    }

    public static async Task Stop()
    {
        if (Node is { } node)
            await node.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>Where files can go from this PC right now, and whether each is connected.</summary>
    public static List<Target> Targets(PortalNode? node)
    {
        if (node is null)
            return [];
        if (node.IsHost)
            return node.Peers.Where(peer => peer.Trusted).Select(peer => new Target(peer.Name, peer, peer.Online)).ToList();
        var host = node.HostPeer;
        var list = new List<Target>();
        if (host is not null)
            list.Add(new Target(host.Name, host, host is { Online: true, Trusted: true }));
        list.AddRange(node.OtherClients.Select(client => new Target(client.Name, client, client.Online && host is { Online: true, Trusted: true })));
        return list;
    }

    /// <summary>Send to a target from Targets. Null when nothing could be read or it is no longer there.</summary>
    public static PortalTransfer? Send(PortalNode node, object target, IReadOnlyList<string> paths) => target switch
    {
        PortalPeer peer => node.Send(peer, paths),
        PortalClient client when node.HostPeer is { } host => node.Send(host, paths, client),
        _ => null,
    };

    /// <summary>True while there is something to show in the widget: a PC connected, a transfer, or a question.</summary>
    public static bool Busy(PortalNode node) =>
        Targets(node).Any(target => target.Ready) || node.Transfers.Any(transfer => transfer.Running)
        || node.Pairings.Any(pairing => pairing.Code.Length > 0) || node.Requests.Count > 0;
}
