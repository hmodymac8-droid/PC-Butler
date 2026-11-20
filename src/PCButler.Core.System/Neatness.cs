// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCButler.Core;

/// <summary>One thing that cost points in the neatness score, as text for the user.</summary>
public sealed record NeatnessNote(string Text, object[] Arguments, int Lost);

/// <summary>
/// A 0-100 score for how tidy the PC is: files waiting to be sorted, junk that can be
/// cleaned, a crowded desktop, a nearly full disk and many startup apps each cost points.
/// Clearing them raises the score.
/// </summary>
public static class Neatness
{
    private static string FormatSize(long size) =>
        size >= 1L << 30 ? $"{size / (double)(1L << 30):0.0} GB" : $"{size / (double)(1L << 20):0} MB";

    public static (int Score, List<NeatnessNote> Notes) Compute(Config config, CancellationToken cancel = default)
    {
        var notes = new List<NeatnessNote>();
        void Lose(int points, string text, params object[] arguments)
        {
            if (points > 0)
                notes.Add(new NeatnessNote(text, arguments, points));
        }

        // Up to 25: files old enough to sort that are still waiting.
        var waiting = Sorter.PlanMoves(config).Count;
        Lose(Math.Min(25, (waiting + 1) / 2), "{0} file(s) are waiting to be sorted", waiting);

        // Up to 25: junk the Cleaner would remove from your temporary files (1 point per 200 MB).
        long junk = 0;
        try
        {
            junk = Cleaner.Scan(["user_temp"], cancel).Sum(found => found.Size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        Lose((int)Math.Min(25, junk / (200L << 20)), "{0} of temporary files can be cleaned", FormatSize(junk));

        // Up to 15: more than ten loose files on the desktop.
        var desktop = 0;
        try
        {
            var folder = ConfigStore.ShellFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            desktop = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Count(file => !Sorter.IsSkipped(file)) : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        Lose(Math.Min(15, Math.Max(0, desktop - 10) / 3), "{0} files are loose on the desktop", desktop);

        // Up to 20: less than a fifth of the Windows drive is free.
        var system = UsageMonitor.Drives().FirstOrDefault(drive =>
            Environment.SystemDirectory.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase));
        if (system is { Total: > 0 })
        {
            var free = (int)(system.Free * 100 / system.Total);
            Lose(Math.Min(20, Math.Max(0, 20 - free)), "Only {0}% of drive {1} is free", free, system.Name);
        }

        // Up to 15: more than eight apps start with Windows.
        var startup = Booster.StartupItems().Count(item => item.Enabled);
        Lose(Math.Min(15, Math.Max(0, startup - 8) * 3 / 2), "{0} apps start with Windows", startup);

        return (Math.Max(0, 100 - notes.Sum(note => note.Lost)), notes.OrderByDescending(note => note.Lost).ToList());
    }
}
