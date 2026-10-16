// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace PCButler.Android;

/// <summary>
/// The app's one screen. Files shared to PC Butler from any other app ("Share" -> PC Butler)
/// arrive here too, and go to Courier to be sent to the PC.
/// </summary>
[Activity(Name = "com.hmodymac8droid.pcbutler.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask,
          ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
                                 | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*", Label = "Send with Courier")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SharedFiles.Take(this, Intent);
        if (Intent?.GetBooleanExtra("selftest", false) == true)
            _ = Task.Run(SelfTest.Run);
    }

    /// <summary>Android's folder and file pickers answer here.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!Results.Done(requestCode, resultCode, data))
            base.OnActivityResult(requestCode, resultCode, data);
    }

    /// <summary>Any touch counts as use, so the vault doesn't lock under the user's finger.</summary>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        App.Used();
        return base.DispatchTouchEvent(e);
    }

    /// <summary>A real keyboard's keys go first to the ID pad, when it is on the screen.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        App.Used();
        if (e is not null && Pages.KeyInput.Handler?.Invoke(e) == true)
            return true;
        return base.DispatchKeyEvent(e);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        SharedFiles.Take(this, intent);
    }
}
