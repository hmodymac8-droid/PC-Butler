// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.Content;
using Android.OS;

namespace PCButler.Android;

/// <summary>What the app asks Android for: showing notifications, and "All files access" (to save received files and look through the phone).</summary>
public static class Access
{
    public static bool HasAllFiles =>
        Build.VERSION.SdkInt >= BuildVersionCodes.R ? global::Android.OS.Environment.IsExternalStorageManager
            : Permissions.CheckStatusAsync<Permissions.StorageWrite>().GetAwaiter().GetResult() == PermissionStatus.Granted;

    /// <summary>Opens Android's page where "All files access" is switched on for this app.</summary>
    public static async void AskForAllFiles()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
            var intent = new Intent(global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                                    global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        else
        {
            await Permissions.RequestAsync<Permissions.StorageWrite>();
        }
    }

    /// <summary>Android 13 and later ask before an app shows notifications (Courier's says it is running).</summary>
    public static async Task AskForNotifications()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
    }
}
