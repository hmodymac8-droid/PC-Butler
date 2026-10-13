// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>
/// Windows Sandbox: a throwaway copy of Windows for trying files and programs safely. It is
/// an optional Windows feature (Pro, Enterprise and Education), switched on and off through
/// DISM with administrator rights, and it needs a restart to take effect.
/// </summary>
public static class WindowsSandbox
{
    public const string Feature = "Containers-DisposableClientVM";

    public enum State { On, Off, NotAvailable }

    /// <summary>Windows Home has no Sandbox.</summary>
    public static bool EditionSupports
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return key?.GetValue("EditionID") is not string edition || !edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
    }

    /// <summary>Read from Windows' list of optional features (no administrator rights needed).</summary>
    public static State Current()
    {
        if (!EditionSupports)
            return State.NotAvailable;
        try
        {
            using var search = new ManagementObjectSearcher($"SELECT InstallState FROM Win32_OptionalFeature WHERE Name='{Feature}'");
            foreach (var feature in search.Get())
                using (feature)
                    return Convert.ToInt32(feature["InstallState"]) == 1 ? State.On : State.Off; // 1 enabled, 2 disabled, 3 absent
            return State.NotAvailable;
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warning($"Could not read the Windows Sandbox feature: {error.Message}");
            return File.Exists(Program) ? State.On : State.Off;
        }
    }

    private static string Program => Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");

    /// <summary>
    /// Switch the feature on or off. Windows asks for administrator rights. Returns null when
    /// it worked (a restart finishes it), "" when the user said no, or an error message.
    /// </summary>
    public static string? Set(bool on)
    {
        var verb = on ? "/enable-feature /all" : "/disable-feature";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                                         $"/online {verb} /featurename:{Feature} /norestart /quiet")
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var dism = Process.Start(start);
            if (dism is null)
                return "DISM could not be started.";
            dism.WaitForExit();
            // 3010: done, a restart is needed.
            return dism.ExitCode is 0 or 3010 ? null : $"DISM ended with code {dism.ExitCode}.";
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return ""; // the administrator prompt was refused
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return error.Message;
        }
    }

    public static bool Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Program) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Log.Warning($"Windows Sandbox could not be started: {error.Message}");
            return false;
        }
    }
}
