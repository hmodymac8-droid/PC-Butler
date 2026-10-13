// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.OS;

namespace PCButler.Android;

/// <summary>
/// What the app asks Android for: showing notifications, and on Android 10 and older, saving
/// to Download (later Android lets an app add files there freely). The phone's other folders
/// are reached only through the ones the user picks (see <see cref="Folders"/>).
/// </summary>
public static class Access
{
    /// <summary>Received files and files taken out of the vault can be saved to Download.</summary>
    public static bool CanSaveToDownload =>
        Build.VERSION.SdkInt > BuildVersionCodes.Q
        || Permissions.CheckStatusAsync<Permissions.StorageWrite>().GetAwaiter().GetResult() == PermissionStatus.Granted;

    /// <summary>Android 10 and older ask before an app writes to Download.</summary>
    public static async Task<bool> AskToSaveToDownload()
    {
        if (CanSaveToDownload)
            return true;
        return await Permissions.RequestAsync<Permissions.StorageWrite>() == PermissionStatus.Granted;
    }

    /// <summary>Android 13 and later ask before an app shows notifications (Courier's says it is running).</summary>
    public static async Task AskForNotifications()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
    }
}
