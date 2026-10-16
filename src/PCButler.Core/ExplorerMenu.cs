// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>What "Process with PC Butler" can do with the file or folder that was right-clicked.</summary>
public sealed record ProcessPlan(string Path, bool IsFolder, List<PlannedMove> Moves)
{
    /// <summary>For a file: the folder it would be sorted into, or "" when no category takes it.</summary>
    public string Destination => !IsFolder && Moves.Count == 1 ? Moves[0].Folder : "";
}

/// <summary>One item of the "PC Butler" submenu: what it does, its English label and its icon. Divided: a line above it starts a group of its own.</summary>
public sealed record MenuAction(string Key, string Label, string Icon, bool ForFolders, bool Divided = false);

/// <summary>
/// The "PC Butler" submenu in File Explorer's right-click menu, for files and folders, in
/// three groups with a line between them: sort and share with Courier; lock in the Vault
/// (files only); shred, last and on its own, because it cannot be undone. Each item runs the program with
/// "--act &lt;key&gt; &lt;path&gt;"; when several items are selected File Explorer starts it once per item,
/// and the copies hand their paths to the first one (see App.RunMenuAction). It is written under
/// HKEY_CURRENT_USER, so it needs no administrator rights and only affects this user. On
/// Windows 11 such entries appear under "Show more options" unless the classic menu is on.
/// </summary>
public static class ExplorerMenu
{
    private const string Verb = "PCButler";

    /// <summary>
    /// Where Windows reads the per-user menu. A test copy (PCBUTLER_DATA set) writes somewhere
    /// Windows never looks, so running the tests can't change the real menu.
    /// </summary>
    private static string ClassesRoot =>
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 } ? @"Software\PCButlerTest\Classes" : @"Software\Classes";

    /// <summary>All files, and folders (right-clicking a folder itself).</summary>
    private static string[] Classes => [$@"{ClassesRoot}\*\shell", $@"{ClassesRoot}\Directory\shell"];

    /// <summary>The submenu, in order. Icons are imageres.dll pictures, so they look like Windows' own.</summary>
    public static readonly MenuAction[] Actions =
    [
        new("sort", "Sort into its category folder", "imageres.dll,-116", ForFolders: true),
        new("courier", "Share with Courier", "imageres.dll,-171", ForFolders: true),
        new("vault", "Lock in the Vault", "imageres.dll,-59", ForFolders: false, Divided: true),
        new("shred", "Shred (deep delete, cannot be undone)", "imageres.dll,-5305", ForFolders: true, Divided: true),
    ];

    /// <summary>File Explorer's flag for a line above a submenu item (ECF_SEPARATORBEFORE).</summary>
    public const int SeparatorBefore = 0x20;

    public static string CommandFor(string program, string action) => $"\"{program}\" --act {action} \"%1\"";

    /// <summary>The command a submenu item runs now, or null when the menu isn't there.</summary>
    public static string? InstalledCommand(string action = "sort")
    {
        try
        {
            var index = Array.FindIndex(Actions, item => item.Key == action);
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes[0]}\{Verb}\shell\{index + 1}{action}\command");
            return key?.GetValue("") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public const string LogoFile = "logo.ico";

    /// <summary>Write the submenu (or point it at this copy of the program, in this language).</summary>
    public static void Install(string program, Func<string, string> translate)
    {
        foreach (var shell in Classes)
        {
            var folders = shell.Contains(@"\Directory\", StringComparison.Ordinal);
            // Start clean: earlier versions had a single entry with a command of its own.
            using (var parent = Registry.CurrentUser.CreateSubKey(shell))
                parent.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            using var key = Registry.CurrentUser.CreateSubKey($@"{shell}\{Verb}");
            key.SetValue("MUIVerb", AppInfo.Name);
            // The logo on a transparent background when it ships beside the program; else the program's own icon.
            var logo = Path.Combine(Path.GetDirectoryName(program) ?? "", LogoFile);
            key.SetValue("Icon", File.Exists(logo) ? $"\"{logo}\"" : $"\"{program}\",0");
            key.SetValue("SubCommands", ""); // the items are in the "shell" key below
            for (var index = 0; index < Actions.Length; index++)
            {
                var action = Actions[index];
                if (folders && !action.ForFolders)
                    continue;
                // The number keeps File Explorer's alphabetical order the same as ours.
                using var item = key.CreateSubKey($@"shell\{index + 1}{action.Key}");
                item.SetValue("MUIVerb", translate(action.Label));
                item.SetValue("Icon", action.Icon);
                // Offered for any number of selected items (by default File Explorer hides it above 15).
                item.SetValue("MultiSelectModel", "Player");
                if (action.Divided)
                    item.SetValue("CommandFlags", SeparatorBefore, RegistryValueKind.DWord);
                using var command = item.CreateSubKey("command");
                command.SetValue("", CommandFor(program, action.Key));
            }
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
    public static string? Sync(bool wanted, string program, Func<string, string> translate)
    {
        try
        {
            if (!wanted)
                Remove();
            else
                Install(program, translate);
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
    private static string ShortMenuClass => $@"{ClassesRoot}\CLSID\{{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}}";

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
