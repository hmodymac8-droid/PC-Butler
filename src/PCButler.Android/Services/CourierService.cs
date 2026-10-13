// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// Keeps Courier connected while the app is in the background or the screen is off, as
/// Android asks: with a notification that says what it is doing, and shows the progress of
/// the file on its way. Tapping it opens the app.
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public sealed class CourierService : Service
{
    private const int NotificationId = 7001;
    private const string Channel = "courier";
    private Timer? timer;
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

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
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
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        timer?.Dispose();
        base.OnDestroy();
    }

    private void Refresh()
    {
        if (Courier.Node is null)
        {
            StopSelf();
            return;
        }
        var notification = Make();
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, notification);
    }

    /// <summary>What Courier is doing, in one line, with the progress of the file being sent or received.</summary>
    private Notification Make()
    {
        var node = Courier.Node;
        var moving = node?.Transfers.LastOrDefault(transfer => transfer.State == "active");
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        var text = moving is not null
            ? Tr.T(moving.Incoming ? "Receiving {0}" : "Sending {0}", moving.Name)
            : ready.Count switch
            {
                0 => node?.IsHost == true ? Tr.T("Waiting for the other PCs to connect.") : Tr.T("Connecting…"),
                1 => Tr.T("Connected to {0}", ready[0].Name),
                _ => Tr.T("{0} PCs connected", ready.Count),
            };
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
                                             PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
        builder.SetContentTitle(Tr.T("Courier")).SetContentText(text).SetSmallIcon(Resource.Mipmap.appicon).SetOngoing(true).SetContentIntent(open)
               .SetOnlyAlertOnce(true);
        if (moving is not null && moving.Size > 0)
            builder.SetProgress(1000, (int)(moving.Done * 1000 / moving.Size), false);
        shown = text;
        return builder.Build()!;
    }
}
