// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.Content;
using Android.Graphics.Drawables;
using AColor = Android.Graphics.Color;
using Format = Android.Graphics.Format;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using PCButler.Core;
using View = Android.Views.View;

namespace PCButler.Android;

/// <summary>
/// The floating button: PC Butler's logo over every app while Courier runs. Tapping it sends
/// what is copied to the connected PCs; it can be dragged anywhere and stays at the side it is
/// left near. Showing over other apps needs Android's "Display over other apps" permission.
/// </summary>
public static class ClipBubble
{
    private const string OnKey = "bubble.on", XKey = "bubble.x", YKey = "bubble.y";
    private static View? bubble;
    private static WindowManagerLayoutParams? place;

    private static Context Context => global::Android.App.Application.Context;
    private static IWindowManager Windows => Context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;

    /// <summary>PC Butler is in front again, perhaps back from Android's settings.</summary>
    public static event Action? Back;

    public static void CameBack()
    {
        Refresh();
        Back?.Invoke();
    }

    /// <summary>The user switched the floating button on.</summary>
    public static bool On => Preferences.Default.Get(OnKey, false);

    /// <summary>Android lets the app show over other apps.</summary>
    public static bool Allowed => Build.VERSION.SdkInt < BuildVersionCodes.M || global::Android.Provider.Settings.CanDrawOverlays(Context);

    public static void SetOn(bool on)
    {
        Preferences.Default.Set(OnKey, on);
        Refresh();
    }

    /// <summary>Android's page where the user lets PC Butler show over other apps.</summary>
    public static void AskAllowed()
    {
        var intent = new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission, global::Android.Net.Uri.Parse("package:" + Context.PackageName));
        Context.StartActivity(intent.AddFlags(ActivityFlags.NewTask));
    }

    /// <summary>Shown while it is switched on, allowed, and Courier runs; gone otherwise.</summary>
    public static void Refresh() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (On && Allowed && Courier.Node is not null)
            Show();
        else
            Hide();
    });

    private static int Dp(float value) => (int)(value * Context.Resources!.DisplayMetrics!.Density);

    private static WindowManagerTypes Overlay => Build.VERSION.SdkInt >= BuildVersionCodes.O ? WindowManagerTypes.ApplicationOverlay
#pragma warning disable CA1422 // the only overlay type before Android 8
                                                                         : WindowManagerTypes.Phone;
#pragma warning restore CA1422

    private static void Show()
    {
        if (bubble is not null)
            return;
        var size = Dp(52);
        var logo = new ImageView(Context);
        logo.SetImageResource(Resource.Mipmap.appicon);
        logo.SetScaleType(ImageView.ScaleType.CenterCrop);
        var round = new GradientDrawable();
        round.SetShape(ShapeType.Oval);
        round.SetColor(AColor.ParseColor("#0D0E11"));
        round.SetStroke(Dp(2), AColor.ParseColor("#3A7BFF"));
        logo.Background = round;
        logo.ClipToOutline = true;
        logo.Elevation = Dp(6);
        logo.ContentDescription = Tr.T("Send what's copied to PC");
        place = new WindowManagerLayoutParams(size, size, Overlay, WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = Preferences.Default.Get(XKey, 0),
            Y = Preferences.Default.Get(YKey, Dp(180)),
        };
        logo.SetOnTouchListener(new Dragger());
        try
        {
            Windows.AddView(logo, place);
            bubble = logo;
        }
        catch (Java.Lang.Exception error)
        {
            Log.Warning($"The floating button could not be shown: {error.Message}");
        }
    }

    public static void Hide()
    {
        if (bubble is null)
            return;
        try
        {
            Windows.RemoveView(bubble);
        }
        catch (Java.Lang.Exception)
        {
        }
        bubble = null;
    }

    /// <summary>Tapped: what is copied goes to the PCs, and a short message says how it went.</summary>
    private static void Tapped()
    {
        bubble?.Animate()?.ScaleX(0.85f).ScaleY(0.85f).SetDuration(90).WithEndAction(new Java.Lang.Runnable(() => bubble?.Animate()?.ScaleX(1).ScaleY(1).SetDuration(90)));
        CopyReader.Read(Context, Overlay, async text =>
        {
            var (sent, problem) = await ClipboardSync.SendCopied(text, again: true);
            Say(sent > 0 ? Tr.T("On the PC's clipboard now: press Ctrl+V there to paste it.")
                         : problem ?? Tr.T("Nothing is copied right now."));
        });
    }

    public static void Say(string message) => MainThread.BeginInvokeOnMainThread(() => Toast.MakeText(Context, message, ToastLength.Short)?.Show());

    /// <summary>Moves the button with the finger; a touch that hardly moved is a tap.</summary>
    private sealed class Dragger : Java.Lang.Object, View.IOnTouchListener
    {
        private float startX, startY;
        private int fromX, fromY;
        private bool moved;

        public bool OnTouch(View? view, MotionEvent? e)
        {
            if (view is null || e is null || place is null)
                return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    (startX, startY, fromX, fromY, moved) = (e.RawX, e.RawY, place.X, place.Y, false);
                    return true;
                case MotionEventActions.Move:
                    var (dx, dy) = (e.RawX - startX, e.RawY - startY);
                    if (!moved && Math.Abs(dx) + Math.Abs(dy) < Dp(8))
                        return true;
                    moved = true;
                    (place.X, place.Y) = (fromX + (int)dx, Math.Max(0, fromY + (int)dy));
                    Windows.UpdateViewLayout(view, place);
                    return true;
                case MotionEventActions.Up:
                    if (!moved)
                    {
                        Tapped();
                        return true;
                    }
                    // Left near a side: it settles against that side.
                    var width = Context.Resources!.DisplayMetrics!.WidthPixels;
                    place.X = place.X + view.Width / 2 < width / 2 ? 0 : width - view.Width;
                    Windows.UpdateViewLayout(view, place);
                    Preferences.Default.Set(XKey, place.X);
                    Preferences.Default.Set(YKey, place.Y);
                    return true;
            }
            return false;
        }
    }
}

/// <summary>
/// Reads the clipboard while PC Butler is not on the screen. Android 10 and later let only the
/// app in focus read it, so for a moment a tiny invisible window of PC Butler's takes the focus,
/// reads, and goes away. The keyboard of the app in front may close for that moment.
/// </summary>
public static class CopyReader
{
    private static bool busy;

    /// <param name="type">The window type the context may add: an overlay, or the accessibility service's own.</param>
    public static void Read(Context context, WindowManagerTypes type, Func<string?, Task> done) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (busy)
            return;
        busy = true;
        var windows = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        var probe = new FocusProbe(context);
        var place = new WindowManagerLayoutParams(1, 1, type, WindowManagerFlags.NotTouchModal | WindowManagerFlags.AltFocusableIm, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
        };
        var finished = false;
        void Finish(string? text)
        {
            if (finished)
                return;
            finished = true;
            try
            {
                windows.RemoveView(probe);
            }
            catch (Java.Lang.Exception)
            {
            }
            busy = false;
            _ = done(text);
        }
        probe.Focused = () => Finish(Current(context));
        try
        {
            windows.AddView(probe, place);
        }
        catch (Java.Lang.Exception error)
        {
            Log.Warning($"The clipboard could not be read from the background: {error.Message}");
            busy = false;
            return;
        }
        // No focus within a second (a locked screen, a system dialog): give up quietly.
        probe.PostDelayed(() => Finish(null), 1000);
    });

    /// <summary>The copied text, when this app may read it now.</summary>
    public static string? Current(Context context)
    {
        try
        {
            var clipboard = (ClipboardManager)context.GetSystemService(Context.ClipboardService)!;
            var clip = clipboard.PrimaryClip;
            return clip is { ItemCount: > 0 } ? clip.GetItemAt(0)?.CoerceToText(context) : null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    private sealed class FocusProbe : View
    {
        public Action? Focused;

        public FocusProbe(Context context) : base(context) { }

        private FocusProbe(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

        public override void OnWindowFocusChanged(bool hasWindowFocus)
        {
            base.OnWindowFocusChanged(hasWindowFocus);
            if (hasWindowFocus)
                Focused?.Invoke();
        }
    }
}
