// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using PCButler.Android.Pages;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// Keeps Courier connected while the app is in the background or the screen is off, as
/// Android asks: with a notification that says what it is doing. While a file is on its way
/// it shows its progress bar, how long it has been moving and how long is left, with Pause or
/// Resume and Cancel; Disconnect is always there and stops Courier. Tapping it opens the app.
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public sealed class CourierService : Service
{
    private const int NotificationId = 7001;
    private const string Channel = "courier", ReceivedChannel = "courier-received";
    private static int receivedId = 7100;
    private const string PauseAction = "pcbutler.courier.pause", CancelAction = "pcbutler.courier.cancel", DisconnectAction = "pcbutler.courier.disconnect";
    private Timer? timer;
    /// <summary>Lets the Wi-Fi hand over broadcasts, which is how a PC on the same network asks for this phone.</summary>
    private global::Android.Net.Wifi.WifiManager.MulticastLock? nearby;
    private string shown = "";

    private static Context Context => global::Android.App.Application.Context;

    public static void Begin()
    {
        var intent = new Intent(Context, typeof(CourierService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            Context.StartForegroundService(intent);
        else
            Context.StartService(intent);
    }

    public static void End() => Context.StopService(new Intent(Context, typeof(CourierService)));

    /// <summary>
    /// Files from a PC arrived: a notification of its own says so, one that makes a sound and
    /// stays until it is swiped away, so files that came while nobody watched are not missed.
    /// Tapping it opens the app.
    /// </summary>
    public static void Received(PortalTransfer transfer)
    {
        ActivityLog.Record("courier", "Files received", $"{transfer.Name}  ·  {transfer.Peer}");
        try
        {
            var manager = (NotificationManager)Context.GetSystemService(NotificationService)!;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                manager.CreateNotificationChannel(new NotificationChannel(ReceivedChannel, Tr.T("Files received"), NotificationImportance.Default));
            var open = PendingIntent.GetActivity(Context, 0, new Intent(Context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask),
                                                 PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(Context, ReceivedChannel) : new Notification.Builder(Context);
            builder.SetContentTitle(Tr.T(transfer.Files == 1 ? "Received {0}" : "Received {0} ({1} files)", transfer.Name, transfer.Files))
                   .SetContentText(Tr.T("From {0}", transfer.Peer) + "  ·  " + CourierPage.Size(transfer.Size) + "  ·  Download/PC Butler Courier")
                   .SetSmallIcon(Resource.Drawable.notification_icon).SetContentIntent(open).SetAutoCancel(true);
            manager.Notify(Interlocked.Increment(ref receivedId), builder.Build());
        }
        catch (Java.Lang.Exception error)
        {
            Log.Warning($"The notification for a received file could not be shown: {error.Message}");
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == DisconnectAction)
        {
            MainThread.BeginInvokeOnMainThread(() => _ = Courier.Stop());
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action is PauseAction or CancelAction && Courier.Node?.Transfers.FirstOrDefault(transfer => transfer.Id == intent.GetStringExtra("id")) is { } chosen)
        {
            if (intent.Action == PauseAction)
                Courier.TogglePause(chosen);
            else
                chosen.Cancel();
        }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(Channel, Tr.T("Courier"), NotificationImportance.Low));
        }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, Make(), ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, Make());
        timer ??= new Timer(_ => Refresh(), null, 1000, 1000);
        if (nearby is null)
        {
            try
            {
                nearby = ((global::Android.Net.Wifi.WifiManager)GetSystemService(WifiService)!).CreateMulticastLock("pcbutler-courier");
                nearby.SetReferenceCounted(false);
                nearby.Acquire();
            }
            catch (Java.Lang.Exception error)
            {
                Log.Warning($"Courier: PCs on this Wi-Fi may not find the phone directly: {error.Message}");
            }
        }
        ClipBubble.Refresh();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        timer?.Dispose();
        try
        {
            nearby?.Release();
        }
        catch (Java.Lang.Exception)
        {
        }
        ClipBubble.Hide();
        base.OnDestroy();
    }

    private void Refresh()
    {
        if (Courier.Node is null)
        {
            StopSelf();
            return;
        }
        var before = shown;
        var notification = Make();
        if (shown == before)
            return; // the same words and progress: no need to wake the notification shade every second
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, notification);
    }

    /// <summary>A button on the notification: it comes back to this service with its action and the transfer's ID.</summary>
    private Notification.Action Button(string action, string title, string id, int code)
    {
        var intent = new Intent(this, typeof(CourierService)).SetAction(action).PutExtra("id", id);
        var pending = PendingIntent.GetService(this, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
#pragma warning disable CA1422 // the icon-number form is the one that works on every Android version the app runs on
        return new Notification.Action(0, title, pending);
#pragma warning restore CA1422
    }

    /// <summary>What Courier is doing, with the file on its way: its progress, its times and its buttons.</summary>
    private Notification Make()
    {
        var node = Courier.Node;
        var running = node?.Transfers.Where(transfer => transfer.Running).ToList() ?? [];
        var moving = running.LastOrDefault(transfer => transfer.State == "active") ?? running.LastOrDefault();
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        string title, text, more = "";
        var progress = -1;
        if (moving is not null)
        {
            title = Tr.T(moving.Incoming ? "Receiving {0}" : "Sending {0}", moving.Name);
            var direction = Tr.T(moving.Incoming ? "From {0}" : "To {0}", moving.Peer);
            if (moving.State == "waiting")
            {
                text = $"{direction}  ·  " + Tr.T(moving.Incoming ? "Waiting for your answer" : "Waiting for the other PC to accept");
            }
            else
            {
                progress = moving.Size > 0 ? (int)Math.Min(1000, moving.Done * 1000 / moving.Size) : 0;
                var held = Courier.Held(moving);
                text = $"{direction}  ·  {progress / 10}%  ·  {CourierPage.Size(moving.Done)} / {CourierPage.Size(moving.Size)}"
                       + (held.Length > 0 ? $"  ·  {held}" : moving.Speed > 0 ? $"  ·  {CourierPage.Size((long)moving.Speed)}/s" : "");
                more = Courier.Times(moving);
            }
            if (running.Count > 1)
                more += (more.Length > 0 ? "\n" : "") + Tr.T("{0} more on the way", running.Count - 1);
        }
        else
        {
            title = Tr.T("Courier");
            text = ready.Count switch
            {
                0 => Tr.T("Reachable by its ID. No PC is connected yet."),
                1 => Tr.T("Connected to {0}", ready[0].Name),
                _ => Tr.T("{0} PCs connected", ready.Count),
            };
        }
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
                                             PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
        builder.SetContentTitle(title).SetContentText(text).SetSmallIcon(Resource.Drawable.notification_icon).SetOngoing(true).SetContentIntent(open)
               .SetOnlyAlertOnce(true);
        if (more.Length > 0)
            builder.SetStyle(new Notification.BigTextStyle().BigText(text + "\n" + more));
        if (progress >= 0)
            builder.SetProgress(1000, progress, false);
        else if (moving?.State == "waiting")
            builder.SetProgress(0, 0, true);
        // At most three buttons: Pause or Resume while it moves, Cancel while it runs, and Disconnect.
        if (moving is { State: "active" })
            builder.AddAction(Button(PauseAction, Tr.T(moving.Paused ? "Resume" : "Pause"), moving.Id, 1));
        if (moving is not null)
            builder.AddAction(Button(CancelAction, Tr.T("Cancel"), moving.Id, 2));
        builder.AddAction(Button(DisconnectAction, Tr.T("Disconnect"), "", 3));
        shown = $"{title}|{text}|{more}|{progress}|{moving?.Paused}";
        return builder.Build()!;
    }
}
