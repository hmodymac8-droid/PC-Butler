// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;
using PCButler.Core;
using AUri = Android.Net.Uri;

namespace PCButler.Android;

/// <summary>A file due to go into a category folder, inside the folder the user chose.</summary>
public sealed record PhoneMove(Doc File, Doc Root, string Folder, double Age);

/// <summary>
/// The Sorter on the phone: the same rules as on the PC (categories, the age a file must
/// reach, names to leave alone), applied to the loose files of each folder the user chose.
/// Each run is logged so the last one can be undone.
/// </summary>
public static class PhoneSorter
{
    public sealed record Moved(string Tree, string Id, string From, string To, string Name);

    public sealed record Run(string Time, List<Moved> Moves, bool Undone = false);

    private static readonly object Gate = new();
    private static string LogFile => Path.Combine(PCButler.Core.AppInfo.DataDir, "phone-sorts.json");

    public static List<PhoneMove> Plan(Config config, IEnumerable<AUri> trees)
    {
        var moves = new List<PhoneMove>();
        foreach (var tree in trees)
        {
            if (Folders.Root(tree) is not { } root)
                continue;
            foreach (var file in Folders.Children(root).Where(child => !child.IsFolder).OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (Sorter.IsSkipped(file.Name) || Sorter.IsIgnored(file.Name, config))
                    continue;
                var extension = Path.GetExtension(file.Name);
                var category = config.Categories.FirstOrDefault(item => item.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
                var folder = category?.Folder ?? config.OtherFolder;
                if (folder.Length == 0 || Path.IsPathRooted(folder))
                    continue;
                var age = (DateTime.Now - file.Modified).TotalDays;
                if (age < (category?.MinAgeDays ?? config.MinAgeDays))
                    continue;
                moves.Add(new PhoneMove(file, root, folder, age));
            }
        }
        return moves;
    }

    public static (int Count, List<string> Errors) Move(IReadOnlyList<PhoneMove> moves)
    {
        var done = new List<Moved>();
        var errors = new List<string>();
        var made = new Dictionary<string, Doc>();
        foreach (var move in moves)
        {
            try
            {
                var key = move.Root.Tree + "|" + move.Folder;
                if (!made.TryGetValue(key, out var target))
                    made[key] = target = Folders.Folder(move.Root, move.Folder);
                var id = Folders.Move(move.File, target);
                done.Add(new Moved(move.Root.Tree.ToString()!, id, target.Id, move.File.ParentId, move.File.Name));
            }
            catch (Exception error) when (error is IOException or Java.Lang.Exception)
            {
                errors.Add($"{move.File.Name}: {error.Message}");
            }
        }
        if (done.Count > 0)
        {
            lock (Gate)
            {
                var log = Load();
                log.Add(new Run(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), done));
                Save(log.TakeLast(30).ToList());
            }
        }
        return (done.Count, errors);
    }

    public static Run? Last() => Load().LastOrDefault(run => !run.Undone);

    /// <summary>Put the files of the last run back where they were.</summary>
    public static (int Count, List<string> Errors) UndoLast()
    {
        lock (Gate)
        {
            var log = Load();
            var index = log.FindLastIndex(run => !run.Undone);
            if (index < 0)
                return (0, []);
            var count = 0;
            var errors = new List<string>();
            foreach (var moved in log[index].Moves)
            {
                try
                {
                    Folders.MoveBack(AUri.Parse(moved.Tree)!, moved.Id, moved.From, moved.To);
                    count++;
                }
                catch (Exception error) when (error is IOException or Java.Lang.Exception)
                {
                    errors.Add($"{moved.Name}: {error.Message}");
                }
            }
            log[index] = log[index] with { Undone = true };
            Save(log);
            return (count, errors);
        }
    }

    private static List<Run> Load()
    {
        try
        {
            return File.Exists(LogFile) ? JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(LogFile)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }

    private static void Save(List<Run> log)
    {
        Directory.CreateDirectory(PCButler.Core.AppInfo.DataDir);
        File.WriteAllText(LogFile, JsonSerializer.Serialize(log));
    }
}
