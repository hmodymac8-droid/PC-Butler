// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>What "Process with PC Butler" can do with the file or folder that was right-clicked.</summary>
public sealed record ProcessPlan(string Path, bool IsFolder, List<PlannedMove> Moves)
{
    /// <summary>For a file: the folder it would be sorted into, or "" when no category takes it.</summary>
    public string Destination => !IsFolder && Moves.Count == 1 ? Moves[0].Folder : "";
}

/// <summary>
/// The "Process with PC Butler" entry in File Explorer's right-click menu, for files and
/// for folders. It is written under HKEY_CURRENT_USER, so it needs no administrator
/// rights and only affects this user. On Windows 11 such entries appear under "Show more
/// options"; only programs installed from a package can add to the short menu.
/// </summary>
public static class ExplorerMenu
{
    private const string Verb = "PCButler";
    /// <summary>All files, and folders (right-clicking a folder itself).</summary>
    private static readonly string[] Classes = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];

    public static string CommandFor(string program) => $"\"{program}\" --process \"%1\"";

    /// <summary>The command the entry runs now, or null when the entry isn't there.</summary>
    public static string? InstalledCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes[0]}\{Verb}\command");
            return key?.GetValue("") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Add the entry (or point it at this copy of the program, in this language).</summary>
    public static void Install(string program, string label)
    {
        foreach (var shell in Classes)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{shell}\{Verb}");
            key.SetValue("", label);
            key.SetValue("Icon", $"\"{program}\",0");
            using var command = key.CreateSubKey("command");
            command.SetValue("", CommandFor(program));
        }
    }

    public static void Remove()
    {
        foreach (var shell in Classes)
        {
            using var key = Registry.CurrentUser.OpenSubKey(shell, writable: true);
            key?.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Bring the menu in line with the setting. Returns an error message, or null. Called
    /// at startup, so a moved program or a changed language is picked up.
    /// </summary>
    public static string? Sync(bool wanted, string program, string label)
    {
        try
        {
            if (!wanted)
                Remove();
            else
                Install(program, label);
            return null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    // --- Windows 11: the short menu or the classic one ---

    /// <summary>
    /// Windows 11 looks this key up to build its short right-click menu; an empty entry for
    /// the current user makes File Explorer fall back to the classic full menu. Deleting
    /// the entry brings the short menu back. It is a per-user setting, needs no
    /// administrator rights, and File Explorer reads it when it starts.
    /// </summary>
    private const string ShortMenuClass = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    /// <summary>Windows 10 only has the classic menu, so there is nothing to choose there.</summary>
    public static bool HasShortMenu => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>True when the classic full menu is switched on for this user.</summary>
    public static bool ClassicMenu
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{ShortMenuClass}\InprocServer32");
                return key is not null && (key.GetValue("") as string ?? "").Length == 0;
            }
            catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Switch between the classic menu and Windows 11's short one. Returns an error message, or null.</summary>
    public static string? SetClassicMenu(bool classic)
    {
        try
        {
            if (classic)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{ShortMenuClass}\InprocServer32");
                key.SetValue("", "");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(ShortMenuClass, throwOnMissingSubKey: false);
            }
            return null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// Close File Explorer's desktop process so Windows starts it again and it reads the
    /// setting. Open folder windows close; nothing else is affected.
    /// </summary>
    public static void RestartExplorer()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var explorer in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                try
                {
                    if (explorer.SessionId == session)
                        explorer.Kill();
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        // Windows restarts the desktop by itself; if it hasn't after a moment, start it.
        Thread.Sleep(2500);
        if (System.Diagnostics.Process.GetProcessesByName("explorer").All(process => process.SessionId != session))
            System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
    }

    /// <summary>
    /// What sorting would do with the right-clicked item, using the same categories as the
    /// Sorter page: a file goes into its category's folder beside it, a folder has its
    /// loose files sorted into category folders inside it. Age is not looked at, because
    /// the user asked for this item now.
    /// </summary>
    public static ProcessPlan Plan(string path, Config config)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
            return new ProcessPlan(path, true, Sorter.PlanMoves(config, everything: true, onlyFolders: [path]));
        var folder = Path.GetDirectoryName(path) ?? "";
        var moves = folder.Length == 0 ? [] : Sorter.PlanMoves(config, everything: true, onlyFolders: [folder])
            .Where(move => Sorter.Norm(move.Source) == Sorter.Norm(path)).ToList();
        return new ProcessPlan(path, false, moves);
    }
}
