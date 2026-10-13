// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

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

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        SharedFiles.Take(this, intent);
    }
}
