// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>
/// Installing and removing the search service (PCButlerSearch.exe, see Search/SearchService.cs),
/// done by the administrator helper (actions install_search_service and remove_search_service).
///
/// The service runs as LocalSystem, so its program must not stay where the user (and so any
/// program the user runs) can change it: it is copied into %ProgramFiles%\PC Butler Search,
/// the copy's SHA-256 is checked against the one built into this program, and only then is the
/// service made, for the one user whose PC Butler asked (the pipe lets in nobody else).
/// </summary>
public static class SearchServiceSetup
{
    public const string ServiceName = "PCButlerSearch";
    public const string FileName = "PCButlerSearch.exe";

    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PC Butler Search");
    public static string InstalledFile => Path.Combine(InstallFolder, FileName);
    /// <summary>The copy that came with PC Butler, beside its program.</summary>
    public static string BundledFile => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The SHA-256 of PCButlerSearch.exe as built with this program (build.ps1 puts it in), "" in a build without it.</summary>
    public static string ExpectedHash =>
        typeof(SearchServiceSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SearchServiceSha256")?.Value?.Trim().ToUpperInvariant() ?? "";

    /// <summary>This build carries the service, so it can be offered.</summary>
    public static bool Bundled => ExpectedHash.Length == 64 && File.Exists(BundledFile);

    public static bool Installed()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// The helper's side, as administrator: copy, check, make and start the service for the user
    /// with this SID. True when it runs. sid must be the user running the main program.
    /// </summary>
    public static bool Install(string sid)
    {
        var expected = ExpectedHash;
        if (expected.Length != 64 || !File.Exists(BundledFile) || !ValidUser(sid))
        {
            Log.Warning("Search service: not installed (no service in this build, or not a user account)");
            return false;
        }
        try
        {
            StopService();
            Directory.CreateDirectory(InstallFolder);
            var temp = InstalledFile + ".new";
            File.Copy(BundledFile, temp, overwrite: true);
            // Checked after copying, in the folder only administrators can change: what runs is what was checked.
            if (!Hash(temp).Equals(expected, StringComparison.Ordinal))
            {
                File.Delete(temp);
                Log.Warning("Search service: the program's checksum is not the one this PC Butler was built with; not installed");
                return false;
            }
            File.Move(temp, InstalledFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Search service: could not copy the program ({error.Message})");
            return false;
        }
        var command = $"\"{InstalledFile}\" --service --user {sid}";
        AdminHelper.RunSc(Installed() ? "config" : "create", ServiceName, "binPath=", command, "start=", "auto", "obj=", "LocalSystem",
                          "DisplayName=", "PC Butler Search");
        if (!Installed())
        {
            Log.Warning("Search service: Windows did not make the service");
            return false;
        }
        AdminHelper.RunSc("description", ServiceName, "Reads the NTFS file table and change journal for PC Butler's file search. Read-only; serves only the user who installed it.");
        AdminHelper.RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
        AdminHelper.RunSc("start", ServiceName);
        for (var tries = 0; tries < 40; tries++)
        {
            if (AdminHelper.IsRunning(ServiceName))
            {
                Log.Info($"Search service: installed and running for {sid}");
                return true;
            }
            Thread.Sleep(250);
        }
        Log.Warning("Search service: installed but it did not start");
        return false;
    }

    /// <summary>The helper's side, as administrator: stop and delete the service and its folder. True when it is gone.</summary>
    public static bool Remove()
    {
        StopService();
        AdminHelper.RunSc("delete", ServiceName);
        for (var tries = 0; tries < 20 && Installed(); tries++)
            Thread.Sleep(250);
        for (var tries = 0; tries < 20; tries++)
        {
            try
            {
                if (Directory.Exists(InstallFolder))
                    Directory.Delete(InstallFolder, recursive: true);
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250); // the program is still closing
            }
        }
        return !Installed();
    }

    private static void StopService()
    {
        if (!AdminHelper.IsRunning(ServiceName))
            return;
        AdminHelper.RunSc("stop", ServiceName);
        for (var tries = 0; tries < 40 && AdminHelper.IsRunning(ServiceName); tries++)
            Thread.Sleep(250);
    }

    /// <summary>A real user account's SID (S-1-5-21-...), nothing else.</summary>
    private static bool ValidUser(string sid)
    {
        try
        {
            var parsed = new SecurityIdentifier(sid);
            return parsed.IsAccountSid() && parsed.Value == sid;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    /// <summary>
    /// The SID of the account a process runs as (the helper asks about the main program that
    /// started it, so a request cannot name some other user). Null when it cannot be told.
    /// </summary>
    public static string? UserOf(int processId)
    {
        const uint QueryLimited = 0x1000, TokenQuery = 0x0008;
        var process = OpenProcess(QueryLimited, false, processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception error) when (error is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The user running this program, to name in the request.</summary>
    public static string CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "";
    }
}
