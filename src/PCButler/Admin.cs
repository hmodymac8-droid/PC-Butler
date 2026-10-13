// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using PCButler.Core;

namespace PCButler;

/// <summary>Asking for the administrator helper from a page.</summary>
public static class Admin
{
    /// <summary>
    /// Make sure the helper is running: explain why, start it (Windows shows its permission
    /// prompt) and wait until it answers. False when the user says no at either question.
    /// </summary>
    public static async Task<bool> Ensure(string title, string why)
    {
        if (AdminHelper.Running)
            return true;
        if (!Ui.Ask(title, why + "\n\n" + Ui.T("PC Butler starts a small helper for it; Windows will ask for your permission. Start the helper now?"))
            || !Sensors.StartHelper())
            return false;
        for (var tries = 0; tries < 60 && !AdminHelper.Running; tries++)
            await Task.Delay(250);
        return AdminHelper.Running;
    }

    /// <summary>Copy a text; a clipboard another program is holding is not worth an error.</summary>
    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
