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
