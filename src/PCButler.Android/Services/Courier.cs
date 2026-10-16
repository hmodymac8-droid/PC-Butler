// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.Content;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// Courier on the phone: started with the app once its role is chosen, the PCs files can go
/// to, and the files shared to PC Butler from other apps, waiting until a PC is connected.
/// </summary>
public static class Courier
{
    private static bool starting;
    private static readonly List<string> waiting = [];

    public static PortalNode? Node => Portal.Current is { Stopped: false } node ? node : null;

    /// <summary>A length of time as 0:42, 12:05 or 1:02:33.</summary>
    public static string Time(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    /// <summary>"2:10 elapsed · 0:45 left" for a transfer that has started moving; "" before that.</summary>
    public static string Times(PortalTransfer transfer)
    {
        if (transfer.Elapsed < TimeSpan.FromSeconds(1))
            return "";
        var text = Tr.T("{0} elapsed", Time(transfer.Elapsed));
        return transfer.Remaining is { } left ? text + "  ·  " + Tr.T("{0} left", Time(left)) : text;
    }

    /// <summary>Why a moving transfer is not moving, or "".</summary>
    public static string Held(PortalTransfer transfer) =>
        transfer.Paused ? Tr.T("Paused") : transfer.PausedThere ? Tr.T("Paused on the other PC") : "";

    /// <summary>Pause it if it moves, let it go on if this phone paused it.</summary>
    public static void TogglePause(PortalTransfer transfer)
    {
        if (transfer.Paused)
            transfer.Resume();
        else
            transfer.Pause();
    }

    /// <summary>Started, stopped, or files shared from another app arrived.</summary>
    public static event Action? Changed;

    public sealed record Target(string Name, object Tag, bool Ready);

    public static PortalSettings Settings => ConfigStore.Load().Portal;

    public static void Update(Action<PortalSettings> change)
    {
        var config = ConfigStore.Load();
        change(config.Portal);
        ConfigStore.Save(config);
    }

    /// <summary>Start with the saved settings. Returns why it could not, or null.</summary>
    public static async Task<string?> Start()
    {
        if (Node is not null || starting)
            return null;
        var settings = Settings;
        if (settings.Role is not (PortalSettings.Host or PortalSettings.Client))
            return Tr.T("Choose first whether this phone shares (the host) or connects to another PC (a client).");
        starting = true;
        try
        {
            Portal.ClearParts(Portal.ReceiveFolder(settings));
            var node = await PortalNode.Start(settings, () => Update(saved => { saved.Devices = settings.Devices.ToList(); saved.HostNumber = settings.HostNumber; }));
            Portal.Current = node;
            CourierService.Begin();
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Not only IOException: a refused socket or a broken certificate land here too, and must not take the app down.
            Log.Warning($"Courier could not start: {error.Message}");
            return Tr.T("Courier could not start.") + "\n\n" + error.Message;
        }
        finally
        {
            starting = false;
            Changed?.Invoke();
        }
    }

    public static async Task Stop()
    {
        if (Node is { } node)
            await node.Stop();
        CourierService.End();
        Changed?.Invoke();
    }

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

    public static PortalTransfer? Send(PortalNode node, object target, IReadOnlyList<string> paths) => target switch
    {
        PortalPeer peer => node.Send(peer, paths),
        PortalClient client when node.HostPeer is { } host => node.Send(host, paths, client),
        _ => null,
    };

    /// <summary>Files shared from another app: kept until a PC is chosen to send them to.</summary>
    public static IReadOnlyList<string> Waiting => waiting;

    /// <summary>
    /// True while files shared from another app are waiting to go by themselves: to the only
    /// PC connected, as soon as one is (with several, the user is asked once which one).
    /// </summary>
    public static bool SendWhenReady { get; set; }

    public static void Queue(IEnumerable<string> paths, bool sendWhenReady = false)
    {
        waiting.AddRange(paths);
        SendWhenReady |= sendWhenReady;
        Changed?.Invoke();
    }

    public static List<string> TakeWaiting()
    {
        var all = waiting.ToList();
        waiting.Clear();
        SendWhenReady = false;
        Changed?.Invoke();
        return all;
    }
}

/// <summary>Files shared to PC Butler from other apps arrive as content addresses: copied to the app's own cache, then queued for Courier.</summary>
public static class SharedFiles
{
    private static string Root => Path.Combine(FileSystem.CacheDirectory, "shared");

    /// <summary>At start: the copies of an earlier run are no longer waiting for anything (the queue lives in memory), so they go.</summary>
    public static void ClearOld()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Old shared files could not be removed: {error.Message}");
        }
    }

    public static void Take(Context context, Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple))
            return;
        var addresses = new List<global::Android.Net.Uri>();
        if (intent.Action == Intent.ActionSend && intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri one)
            addresses.Add(one);
        else if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } many)
            addresses.AddRange(many.OfType<global::Android.Net.Uri>());
        if (addresses.Count == 0)
            return;
        var folder = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var copied = new List<string>();
        foreach (var address in addresses)
        {
            try
            {
                var name = Portal.SafeName(NameOf(context, address));
                var target = Sorter.UniqueDestination(folder, name);
                using var input = context.ContentResolver!.OpenInputStream(address)!;
                using var output = File.Create(target);
                input.CopyTo(output);
                copied.Add(target);
            }
            catch (Exception error) when (error is IOException or Java.Lang.Exception or UnauthorizedAccessException)
            {
                Log.Warning($"A shared file could not be read: {error.Message}");
            }
        }
        if (copied.Count == 0)
            return;
        // Shared means "send this": it goes by itself once a PC is connected (CourierPage does the sending).
        Courier.Queue(copied, sendWhenReady: true);
        MainThread.BeginInvokeOnMainThread(ShowCourier);
    }

    /// <summary>Bring the Courier tab to the front, where the shared files are sent and their progress shows.</summary>
    private static void ShowCourier()
    {
        if (Microsoft.Maui.Controls.Shell.Current?.CurrentItem is { Items.Count: > 0 } tabs)
            tabs.CurrentItem = tabs.Items[0];
    }

    private static string NameOf(Context context, global::Android.Net.Uri address)
    {
        using var cursor = context.ContentResolver?.Query(address, [global::Android.Provider.IOpenableColumns.DisplayName], null, null, null);
        if (cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } name)
            return name;
        return address.LastPathSegment ?? "file";
    }
}
