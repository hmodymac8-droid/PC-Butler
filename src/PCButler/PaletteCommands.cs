// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler;

/// <summary>
/// One thing the command palette can do. Title, group and keywords are English; the palette
/// matches them and their translations both, so English and Arabic words find the same command.
/// </summary>
/// <param name="Id">Kept with the count of runs (palette-use.json), so the most used come first.</param>
/// <param name="Hotkey">The Hotkeys tab action whose shortcut is shown beside it, if any.</param>
/// <param name="Prefix">For a command that starts a kind of search: typed into the box instead of closing it.</param>
public sealed record PaletteCommand(string Id, string Glyph, string Title, string Group, string Keywords, Action Run,
                                    string? Hotkey = null, Func<bool>? Available = null, string? Prefix = null);

/// <summary>The fixed list of commands: every page, the Settings tabs, and things to do at once. Built when the palette first opens.</summary>
public static class PaletteCommands
{
    /// <summary>Words that find a page besides its name.</summary>
    private static readonly Dictionary<string, string> PageWords = new()
    {
        ["home"] = "start overview dashboard",
        ["search"] = "find files folders everything",
        ["preview"] = "sort downloads organize tidy move",
        ["duplicates"] = "copies same identical files",
        ["cleaner"] = "junk temp cache clean free space",
        ["storage"] = "disk space drives big folders",
        ["booster"] = "boost speed fast memory ram processes",
        ["benchmark"] = "fps overlay games performance test",
        ["programs"] = "apps uninstall installed software",
        ["internet"] = "network wifi speed test data usage",
        ["shredder"] = "delete forever wipe erase secure",
        ["clipboard"] = "copy paste history",
        ["portal"] = "send receive transfer files phone share p2p",
        ["vault"] = "lock password private hide encrypt",
        ["history"] = "undo moves log restore",
        ["settings"] = "options preferences",
        ["about"] = "help version changelog what's new",
    };

    public static List<PaletteCommand> All()
    {
        var app = App.Current;
        var list = new List<PaletteCommand>();
        foreach (var (key, glyph, name) in MainWindow.PageList)
            list.Add(new PaletteCommand("page:" + key, glyph, name, "Page", PageWords.GetValueOrDefault(key, ""), () => app.ShowPage(key),
                                        Hotkey: key == "portal" ? "courier" : key == "search" ? "searchpage" : null));
        foreach (var (key, name) in new[] { ("general", "General settings"), ("search", "Search settings"), ("hotkeys", "Hotkeys") })
            list.Add(new PaletteCommand("settings:" + key, "", name, "Settings", "settings shortcuts keys", () =>
            {
                app.ShowMainWindow();
                app.Workspace?.Settings.ShowTab(key);
            }));
        list.AddRange(
        [
            new("files", "", "Search files and folders", "Search", "find file everything", () => { }, Hotkey: "search", Prefix: "file "),
            new("calculator", "", "Calculator", "Search", "math calc sum convert size speed", () => { }, Prefix: "="),
            new("sort", "", "Sort now", "Sorter", "sort downloads organize move", () => app.RunHotkey("sort"), Hotkey: "sort"),
            new("boost", "", "Start or stop Boost", "Booster", "boost speed game fast", () =>
            {
                // Boost favours the program in front: let it come back in front first.
                Task.Delay(250).ContinueWith(_ => app.Dispatcher.BeginInvoke(() => app.RunHotkey("boost")));
            }, Hotkey: "boost"),
            new("overlay", "", "Show or hide the performance overlay", "Benchmark", "fps overlay games", () => app.RunHotkey("overlay"), Hotkey: "overlay"),
            new("dropzone", "", "Show or hide the drop zone", "Sorter", "drop zone drag", () => app.RunHotkey("dropzone"), Hotkey: "dropzone"),
            new("scan", "", "Scan for junk", "Cleaner", "clean junk temp scan free space", () =>
            {
                app.ShowPage("cleaner");
                app.Workspace?.Cleaner.ScanNow();
            }),
            new("resume", "", "Resume yesterday", "Home", "reopen restore session apps", app.ResumeYesterday,
                Available: () => app.ResumeAvailable is not null),
            new("updates", "", "Check for updates", "Help & about", "update new version", () =>
            {
                app.ShowPage("about");
                app.Workspace?.About.CheckUpdates();
            }),
            new("exit", "", "Quit PC Butler", "PC Butler", "exit close quit", app.ExitProgram),
        ]);
        return list;
    }
}
