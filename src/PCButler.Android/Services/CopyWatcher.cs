// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Views.Accessibility;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// "Send what I copy": an accessibility service that notices a copy in any app (a Copy button
/// or menu item tapped, a "Copied" message, Android's own copied-text panel) and sends the text
/// to the connected PCs a moment later. Android 10 and later never tell an app in the
/// background that the clipboard changed, so this is the one way to do it by itself. It reads
/// no screen content: only the words of the tapped item and of the message, to tell a copy.
/// The user switches it on in Android's Accessibility settings.
/// </summary>
[Service(Name = "com.hmodymac8droid.pcbutler.CopyWatcher", Exported = true, Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Label = "PC Butler: send what I copy")]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/copy_watcher")]
public sealed class CopyWatcher : AccessibilityService
{
    private static readonly string[] CopyWords = ["copy", "copied", "نسخ", "نُسخ", "تم النسخ"];
    private long lastCheck;

    /// <summary>Android's Accessibility settings have it switched on.</summary>
    public static bool On
    {
        get
        {
            var context = global::Android.App.Application.Context;
            var enabled = global::Android.Provider.Settings.Secure.GetString(context.ContentResolver, global::Android.Provider.Settings.Secure.EnabledAccessibilityServices) ?? "";
            return enabled.Contains(context.PackageName + "/", StringComparison.OrdinalIgnoreCase) && enabled.Contains(nameof(CopyWatcher), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void OpenSettings()
    {
        var context = global::Android.App.Application.Context;
        context.StartActivity(new Intent(global::Android.Provider.Settings.ActionAccessibilitySettings).AddFlags(ActivityFlags.NewTask));
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        // Switched on, it runs in the background from then on: Courier starts with it, as it does with the app.
        var settings = Courier.Settings;
        if (Courier.Node is null && settings.AutoStart && (settings.Used || settings.Role.Length > 0))
            MainThread.BeginInvokeOnMainThread(() => _ = Courier.Start());
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e is null || e.PackageName == PackageName || !LooksLikeCopy(e))
            return;
        var now = Environment.TickCount64;
        if (now - lastCheck < 800)
            return;
        lastCheck = now;
        if (Courier.Node is null)
            return;
        // The app puts the text on the clipboard just after the tap: read it a moment later.
        new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).PostDelayed(() =>
            CopyReader.Read(this, WindowManagerTypes.AccessibilityOverlay, async text =>
            {
                var (sent, _) = await ClipboardSync.SendCopied(text);
                if (sent > 0)
                    ClipBubble.Say(Tr.T("Sent to the PC's clipboard"));
            }), 350);
    }

    private static bool LooksLikeCopy(AccessibilityEvent e)
    {
        var words = string.Join(' ', (e.Text ?? []).Select(item => item?.ToString())) + " " + e.ContentDescription;
        var copyWords = CopyWords.Any(word => words.Contains(word, StringComparison.OrdinalIgnoreCase));
        return e.EventType switch
        {
            EventTypes.ViewClicked => copyWords,
            // A "Copied" toast, or Android 13's copied-text panel.
            EventTypes.NotificationStateChanged => copyWords,
            EventTypes.WindowStateChanged => e.PackageName == "com.android.systemui" && (copyWords || (e.ClassName ?? "").Contains("clipboard", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    public override void OnInterrupt() { }
}
