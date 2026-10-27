// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>A bit of text kept on the Clipboard page.</summary>
public sealed class Snippet
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTime Saved { get; set; }
    /// <summary>The PC it came from, or "" for text saved on this phone.</summary>
    public string From { get; set; } = "";
}

/// <summary>
/// Clipboard sharing with the PCs connected through Courier. Text a PC copies lands on this
/// phone's clipboard (and in the saved snippets); what this phone copies goes to the PCs when
/// the user sends it, or by itself when PC Butler comes back to the screen (Android lets an
/// app read the clipboard only while it is in front, so nothing is read in the background).
/// </summary>
public static class ClipboardSync
{
    private static string StoreFile => Path.Combine(PCButler.Core.AppInfo.DataDir, "snippets.json");
    private static readonly object gate = new();
    private static List<Snippet>? snippets;
    private static string last = ""; // the last text sent or received: the same text never goes back and forth
    private static int receivedId = 7300;
    private const string ReceivedChannel = "clipboard-received";

    /// <summary>The snippets changed (one arrived from a PC, or the page changed them).</summary>
    public static event Action? Changed;

    public static bool On => Courier.Settings.ShareClipboard;

    public static void SetOn(bool on) => Courier.Update(settings => settings.ShareClipboard = on);

    public static List<Snippet> Snippets
    {
        get
        {
            lock (gate)
                return snippets ??= Load();
        }
    }

    private static List<Snippet> Load()
    {
        try
        {
            return File.Exists(StoreFile) ? JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(StoreFile)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }

    public static void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(PCButler.Core.AppInfo.DataDir);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(Snippets));
        }
        Changed?.Invoke();
    }

    /// <summary>Keep a text, the same text once, on top.</summary>
    public static void Add(string text, string from = "")
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        lock (gate)
        {
            var list = Snippets;
            var pinned = list.Any(item => item.Text == text && item.Pinned);
            list.RemoveAll(item => item.Text == text);
            list.Insert(0, new Snippet { Id = DateTime.UtcNow.Ticks, Text = text, Saved = DateTime.Now, From = from, Pinned = pinned });
        }
        Save();
    }

    /// <summary>Text from a PC: on this phone's clipboard, kept, and a notification says so. False when sharing is off.</summary>
    public static bool Received(PortalPeer peer, string text)
    {
        if (!On)
            return false;
        last = text;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Clipboard.Default.SetTextAsync(text);
            }
            catch (Exception error)
            {
                Log.Warning($"Text from {peer.Name} could not be put on the clipboard: {error.Message}");
            }
        });
        Add(text, peer.Name);
        ActivityLog.Record("clipboard", "Text received from a PC", peer.Name);
        Notify(peer.Name, text);
        return true;
    }

    /// <summary>Send text to every connected PC. Returns how many took it, and the first reason one did not.</summary>
    public static async Task<(int Sent, string? Problem)> SendToAll(string text)
    {
        if (Courier.Node is not { } node)
            return (0, Tr.T("Connect to a PC on the Courier page first."));
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        if (ready.Count == 0)
            return (0, Tr.T("Connect to a PC on the Courier page first."));
        last = text;
        var answers = await Task.WhenAll(ready.Select(target => node.SendClipboard(target.Tag, text)));
        var problem = answers.FirstOrDefault(answer => answer is not null);
        if (problem == PortalNode.OldVersion)
            problem = Tr.T("The PC has an older PC Butler: update it to share the clipboard.");
        var sent = answers.Count(answer => answer is null);
        if (sent > 0)
            ActivityLog.Record("clipboard", "Text sent to a PC", string.Join(", ", ready.Where((_, index) => answers[index] is null).Select(target => target.Name)));
        return (sent, problem);
    }

    /// <summary>
    /// PC Butler is in front again: what was copied meanwhile in another app goes to the
    /// connected PCs, when sharing is on and it is new.
    /// </summary>
    public static async Task CheckCopied()
    {
        if (!On || Courier.Node is not { } node || !Courier.Targets(node).Any(target => target.Ready))
            return;
        string? text;
        try
        {
            text = await Clipboard.Default.GetTextAsync();
        }
        catch (Exception)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || text == last || text.Length > PortalNode.MaxClipboard)
            return;
        var (sent, _) = await SendToAll(text);
        if (sent > 0)
            Add(text);
    }

    /// <summary>
    /// Text read from the clipboard outside the app (the floating button, or a copy noticed in
    /// another app) goes to the connected PCs. Unless <paramref name="again"/>, text already sent
    /// or received is not sent once more.
    /// </summary>
    public static async Task<(int Sent, string? Problem)> SendCopied(string? text, bool again = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, null);
        if (text.Length > PortalNode.MaxClipboard || !again && text == last)
            return (0, null);
        var result = await SendToAll(text);
        if (result.Sent > 0)
            Add(text);
        return result;
    }

    private static void Notify(string peer, string text)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                manager.CreateNotificationChannel(new NotificationChannel(ReceivedChannel, Tr.T("Text from a PC"), NotificationImportance.Low));
            var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask),
                                                 PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(context, ReceivedChannel) : new Notification.Builder(context);
            builder.SetContentTitle(Tr.T("Copied from {0}", peer)).SetContentText(text.ReplaceLineEndings(" "))
                   .SetSmallIcon(Resource.Drawable.notification_icon).SetContentIntent(open).SetAutoCancel(true).SetTimeoutAfter(60_000);
            manager.Notify(Interlocked.Increment(ref receivedId), builder.Build());
        }
        catch (Java.Lang.Exception error)
        {
            Log.Warning($"The notification for received text could not be shown: {error.Message}");
        }
    }
}
