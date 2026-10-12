// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>A program that starts with Windows.</summary>
public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    /// <summary>Shown to the user.</summary>
    public required string Where { get; init; }
    /// <summary>"HKCU" or "HKLM", where its on/off switch lives.</summary>
    public required string Hive { get; init; }
    /// <summary>Subkey of StartupApproved holding the switch.</summary>
    public required string ApprovedKey { get; init; }
    public bool Enabled { get; set; }
    /// <summary>The switch's value name, when it differs from Name (startup-folder files).</summary>
    public string ValueName { get; init; } = "";
    public bool NeedsAdmin => Hive == "HKLM";
}

/// <summary>A running program; copies of one program are combined.</summary>
public sealed class RunningProgram
{
    public required string Name { get; init; }
    public List<int> Pids { get; } = [];
    /// <summary>Bytes, all copies together.</summary>
    public long Memory { get; set; }
    public bool Protected => Booster.ProtectedNames.Contains(Name) || Pids.Contains(Environment.ProcessId);
}

public sealed record PowerPlan(string Guid, string Name, bool Active);

/// <summary>
/// Make the PC start and run lighter: startup apps, memory-hungry programs and the power
/// plan. Every change can be reversed from the same page: startup apps are switched off
/// the way Task Manager does it (the entry itself is kept), and the power plan can be
/// switched back.
/// </summary>
public static partial class Booster
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Ending these would crash or lock Windows, so they are never offered.</summary>
    public static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe",
        "memory compression", "secure system", "sihost.exe", "ctfmon.exe", "audiodg.exe", "conhost.exe",
        "msmpeng.exe", "searchindexer.exe", "spoolsv.exe", "taskhostw.exe", "runtimebroker.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "securityhealthservice.exe",
    };

    private static RegistryKey Base(string hive, RegistryView view = RegistryView.Default) =>
        RegistryKey.OpenBaseKey(hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);

    /// <summary>Task Manager's switch: an even first byte (or no value) means on.</summary>
    private static bool ApprovedState(string hive, string subkey, string name)
    {
        try
        {
            using var root = Base(hive);
            using var key = root.OpenSubKey($@"{Approved}\{subkey}");
            return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static List<StartupItem> StartupItems()
    {
        var items = new List<StartupItem>();
        foreach (var (hive, view, approved, where) in new[]
                 {
                     ("HKCU", RegistryView.Default, "Run", "Registry (you)"),
                     ("HKLM", RegistryView.Registry64, "Run", "Registry (all users)"),
                     ("HKLM", RegistryView.Registry32, "Run32", "Registry (all users, 32-bit)"),
                 })
        {
            try
            {
                using var root = Base(hive, view);
                using var key = root.OpenSubKey(RunKey);
                if (key is null)
                    continue;
                foreach (var name in key.GetValueNames())
                {
                    if (items.Any(item => item.Name == name && item.Hive == hive))
                        continue; // 64- and 32-bit views can show the same key
                    items.Add(new StartupItem
                    {
                        Name = name, Command = key.GetValue(name)?.ToString() ?? "", Where = where, Hive = hive,
                        ApprovedKey = approved, Enabled = ApprovedState(hive, approved, name),
                    });
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var (hive, variable, where) in new[]
                 {
                     ("HKCU", "APPDATA", "Startup folder (you)"),
                     ("HKLM", "ProgramData", "Startup folder (all users)"),
                 })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            var folder = string.IsNullOrEmpty(root) ? null : Path.Combine(root, @"Microsoft\Windows\Start Menu\Programs\Startup");
            if (folder is null || !Directory.Exists(folder))
                continue;
            try
            {
                foreach (var path in Directory.GetFiles(folder))
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;
                    items.Add(new StartupItem
                    {
                        Name = Path.GetFileNameWithoutExtension(path), Command = path, Where = where, Hive = hive,
                        ApprovedKey = "StartupFolder", Enabled = ApprovedState(hive, "StartupFolder", fileName),
                        ValueName = fileName,
                    });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return items.OrderBy(item => item.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Switch a startup app on or off, exactly like Task Manager. Throws
    /// UnauthorizedAccessException for all-users items without administrator rights.
    /// </summary>
    public static void SetStartupEnabled(StartupItem item, bool enabled)
    {
        var name = item.ValueName.Length > 0 ? item.ValueName : item.Name;
        byte[] data;
        if (enabled)
        {
            data = new byte[12];
            data[0] = 2;
        }
        else
        {
            // 3 = off, followed by the Windows time of switching off.
            data = [3, 0, 0, 0, .. BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())];
        }
        try
        {
            using var root = Base(item.Hive);
            using var key = root.CreateSubKey($@"{Approved}\{item.ApprovedKey}", writable: true);
            key.SetValue(name, data, RegistryValueKind.Binary);
        }
        catch (System.Security.SecurityException error)
        {
            throw new UnauthorizedAccessException(error.Message, error);
        }
        item.Enabled = enabled;
    }

    /// <summary>Programs running now, biggest memory users first, copies of one program combined.</summary>
    public static List<RunningProgram> RunningPrograms()
    {
        var programs = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // Windows' own pseudo-processes have no file name; everything else is shown as its .exe.
                var name = process.Id == 0 ? "System Idle Process"
                    : process.ProcessName is "System" or "Registry" or "Memory Compression" or "Secure System"
                        ? process.ProcessName
                        : process.ProcessName + ".exe";
                if (!programs.TryGetValue(name, out var program))
                    programs[name] = program = new RunningProgram { Name = name };
                program.Pids.Add(process.Id);
                try
                {
                    program.Memory += process.WorkingSet64;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return programs.Values.OrderByDescending(program => program.Memory).ToList();
    }

    /// <summary>End every copy of a program. Returns an error message, or null.</summary>
    public static string? EndProgram(RunningProgram program)
    {
        if (program.Protected)
            return string.Format(Tr.T("{0} is part of Windows and can't be ended here."), program.Name);
        string? problem = null;
        foreach (var pid in program.Pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
            {
                problem = error.Message;
            }
        }
        return problem;
    }

    private static (int ExitCode, string Output) RunTool(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? output : (errors.Length > 0 ? errors : output).Trim());
    }

    [GeneratedRegex(@"([0-9a-fA-F-]{36})\s+\((.+?)\)\s*(\*?)")] private static partial Regex PlanLine();

    public static List<PowerPlan> PowerPlans()
    {
        try
        {
            return PlanLine().Matches(RunTool("powercfg", "/list").Output)
                .Select(match => new PowerPlan(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value == "*"))
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>Returns an error message, or null.</summary>
    public static string? SetPowerPlan(PowerPlan plan)
    {
        try
        {
            var (exitCode, output) = RunTool("powercfg", "/setactive", plan.Guid);
            return exitCode == 0 ? null : output.Length > 0 ? output : "powercfg failed";
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return error.Message;
        }
    }

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
    public static (long Total, long Available) MemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);
        return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }
}
