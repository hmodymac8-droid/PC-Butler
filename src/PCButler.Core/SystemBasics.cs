// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;

namespace PCButler.Core;

/// <summary>The PC's physical memory, as Windows reports it.</summary>
public static class MemoryInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual,
                     ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    /// <summary>(total, available) physical memory in bytes.</summary>
    public static (long Total, long Available) Status()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);
        return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }
}

/// <summary>Programs PC Butler never ends or slows down.</summary>
public static class ProtectedProcesses
{
    /// <summary>Ending these would crash or lock Windows, so they are never offered.</summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe",
        "memory compression", "secure system", "sihost.exe", "ctfmon.exe", "audiodg.exe", "conhost.exe",
        "msmpeng.exe", "searchindexer.exe", "spoolsv.exe", "taskhostw.exe", "runtimebroker.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "securityhealthservice.exe",
    };
}
