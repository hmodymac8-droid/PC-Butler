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

    /// <summary>A received transfer whose files are still where they landed: what Open and Show in folder work on.</summary>
    public static bool CanOpen(PortalTransfer transfer) =>
        transfer is { Incoming: true, State: "done", Path.Length: > 0 } && (File.Exists(transfer.Path) || Directory.Exists(transfer.Path));

    /// <summary>Open what was received (a file with its program, several files as their folder), with a word if it is gone.</summary>
    public static void OpenReceived(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            Ui.Warn("Open", Ui.T("This file is not there any more."));
        else if (Shell.Open(path) is { } error)
            Ui.Warn("Open", Ui.T("Could not open it:\n{0}", error));
    }

    /// <summary>Show what was received selected in File Explorer.</summary>
    public static void RevealReceived(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            Ui.Warn("Show in folder", Ui.T("This file is not there any more."));
        else if (Shell.Reveal(path) is { } error)
            Ui.Warn("Show in folder", Ui.T("Could not open it:\n{0}", error));
    }

    /// <summary>The running node, if any.</summary>
    public static PortalNode? Node => Portal.Current is { Stopped: false } node ? node : null;

    /// <summary>The widget setting, kept here so it is not read from disk every second.</summary>
    public static bool WidgetOn { get; set; } = true;

    /// <summary>Courier started or stopped. Raised on the window's thread.</summary>
    public static event Action? StateChanged;

    /// <summary>One PC files can go to, and whether it is connected (and has let this PC in).</summary>
    public sealed record Target(string Name, PortalPeer Tag, bool Ready);

    /// <summary>This PC's own ID, whether Courier runs or not: it comes from this PC's certificate, made once and kept.</summary>
    public static string OwnNumber => ownNumber ??= Node?.Number ?? Portal.NumberOf(Portal.Fingerprint(Portal.Certificate()));
    private static string? ownNumber;

    /// <summary>Where a PC on the list stands, in words.</summary>
    public static string Describe(PortalState state, string name) => Ui.T(state switch
    {
        PortalState.Connected => "Connected",
        PortalState.Connecting => "Connecting…",
        PortalState.Asking => "Asking {0} to let this PC in…",
        PortalState.WaitingForThem => "Waiting for {0} to allow this PC",
        PortalState.NeedsPassword => "{0} asks for a password.",
        PortalState.WrongPassword => "That password was not right. Type it again.",
        PortalState.NoDirectory => "This network blocks the public directory that IDs are looked up in.",
        PortalState.NotFound => "Not found: Courier is not running on {0}, or the ID is wrong. It tries again by itself.",
        PortalState.NoPath => "{0} was found, but the two networks would not let a link through. It tries again by itself.",
        _ => "Not connected",
    }, name);

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
        starting = true;
        // Switched on once, it starts with PC Butler from then on (unless that setting is off).
        UpdateConfig(config => config.Portal.Used = true);
        var settings = ConfigStore.Load().Portal;
        try
        {
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            Portal.ClearRelay();
            // The test hook keeps Courier on this PC only, so trying the page never opens a port to the internet.
            var local = Environment.GetEnvironmentVariable("PCBUTLER_PORTAL_LOOPBACK") == "1";
            var node = await PortalNode.Start(settings, () => Application.Current.Dispatcher.BeginInvoke(() =>
                UpdateConfig(config => (config.Portal.Devices, config.Portal.Saved, config.Portal.Role, config.Portal.HostNumber)
                                           = (settings.Devices.ToList(), settings.Saved.ToList(), "", ""))), local: local);
            node.Attention += App.Current.PortalNeedsAttention;
            node.Received += App.Current.PortalReceived;
            node.ClipboardReceived = App.Current.ClipShare.Received;
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

    /// <summary>The paired PCs, and whether each can take files right now (connected, and it has let this PC in).</summary>
    public static List<Target> Targets(PortalNode? node)
    {
        if (node is null)
            return [];
        var ready = node.Ready;
        return node.Peers.Where(peer => peer.Trusted).Select(peer => new Target(peer.Name, peer, ready.Contains(peer))).ToList();
    }

    /// <summary>Send to a target from Targets. Null when nothing could be read.</summary>
    public static PortalTransfer? Send(PortalNode node, PortalPeer target, IReadOnlyList<string> paths) => node.Send(target, paths);

    /// <summary>True while there is something to show in the widget: a PC connected, a transfer, or a question.</summary>
    public static bool Busy(PortalNode node) =>
        Targets(node).Any(target => target.Ready) || node.Transfers.Any(transfer => transfer.Running)
        || node.Pairings.Any(pairing => pairing.Code.Length > 0) || node.Requests.Count > 0;
}
