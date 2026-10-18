// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PCButler.Core;

/// <summary>
/// The name a running process is shown under when apps are listed together: its maker's name
/// for a program made by one company in several parts (SteelSeriesGG, SteelSeriesEngine and
/// SteelSeriesSonar are all "SteelSeries"), otherwise the process's own name. Windows' own
/// programs keep their process names, since one maker there would put every part of Windows
/// in one row.
/// </summary>
public static partial class AppNames
{
    private const int QueryLimitedInformation = 0x1000;

    /// <summary>The group name for a running process, or null when it has ended.</summary>
    public static string? Of(int pid)
    {
        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        return MakerOf(ProgramFile(pid)) ?? name;
    }

    /// <summary>The maker's short name from the program's version information, or null for Windows' own programs and unknown files.</summary>
    private static string? MakerOf(string exe)
    {
        if (exe.Length == 0)
            return null;
        try
        {
            var company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
            if (string.IsNullOrEmpty(company) || company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return null;
            var shortName = Suffix().Replace(company, "").Trim(' ', ',', '.');
            return shortName.Length > 0 ? shortName : company;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // "SteelSeries ApS", "Google LLC", "Valve Corp.", "Discord Inc." → the name alone.
    [GeneratedRegex(@"[,\s]+(Inc|Incorporated|LLC|L\.L\.C|Ltd|Limited|Corp|Corporation|Co|Company|GmbH|AG|ApS|A/S|AB|AS|SA|S\.A|SAS|BV|B\.V|NV|Oy|Pty|PLC|KK|S\.r\.l|SRL)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();

    private static string ProgramFile(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return "";
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
