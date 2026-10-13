// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// What the items of File Explorer's "PC Butler" submenu do with the selected files and
/// folders. Sorting is logged in History and can be undone there; shredding
/// is asked about first, because nothing brings it back.
/// </summary>
public static class MenuActions
{
    public static void Run(string action, List<string> selected)
    {
        var paths = selected.Select(Path.GetFullPath).Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (paths.Count == 0)
        {
            Ui.Info(AppInfo.Name, Ui.T("It is no longer there."));
            return;
        }
        switch (action)
        {
            case "sort":
                Sort(paths);
                break;
            case "vault":
                Vault(paths);
                break;
            case "shred":
                Shred(paths);
                break;
        }
    }

    /// <summary>Files go into their category's folder beside them; folders have their loose files sorted inside.</summary>
    private static void Sort(List<string> paths)
    {
        var config = ConfigStore.Load();
        var moves = paths.SelectMany(path => ExplorerMenu.Plan(path, config).Moves)
                         .DistinctBy(move => Sorter.Norm(move.Source)).ToList();
        if (moves.Count == 0)
        {
            Ui.Info(AppInfo.Name, Ui.T("Nothing to sort: no category takes these files."));
            return;
        }
        var (count, errors) = Sorter.MoveFiles(moves);
        Ui.ReportResult(Ui.T("Sorted {0} file(s).", count), errors);
    }

    /// <summary>The main window holds the vault and asks for its password, so the files are handed over to it.</summary>
    private static void Vault(List<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            Ui.Info(AppInfo.Name, Ui.T("Only files can be locked in the Vault, not folders."));
            return;
        }
        VaultInbox.Add(files);
        Shell.Open(Environment.ProcessPath!);
    }

    private static void Shred(List<string> paths)
    {
        var refused = paths.Where(Shredder.IsProtected).ToList();
        var chosen = paths.Except(refused).ToList();
        if (chosen.Count == 0)
        {
            Ui.ReportResult(Ui.T("Some folders are protected and are never shredded."), refused);
            return;
        }
        var (files, size) = Shredder.Measure(chosen);
        if (!Ui.Ask("Shredder", Ui.T("Shred {0} item(s): {1} file(s), {2}?\n\nThey are overwritten and deleted. This cannot be undone, "
                                     + "not from History and not from the Recycle Bin.", chosen.Count, files, Ui.FormatSize(size)), warning: true))
            return;
        var (count, errors) = Shredder.Shred(chosen);
        errors.AddRange(refused.Select(path => $"{path}: {Ui.T("this folder is protected and is never shredded")}"));
        Ui.ReportResult(Ui.T("Shredded {0} file(s).", count), errors);
    }
}
