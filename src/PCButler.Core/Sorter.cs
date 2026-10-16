// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCButler.Core;

/// <summary>A file that is due to move, and where it will go.</summary>
public sealed record PlannedMove(string Source, string Folder, double Age, long Size, string Root)
{
    public string Name => Path.GetFileName(Source);
}

/// <summary>
/// One batch of changes in the history log. A move is [source, destination], plus a
/// third item once it changes state:
///   "undone"   - moved back to source
///   "deleted"  - the file (at destination, or at source if destination is "") was sent to the Recycle Bin
///   "erased"   - the same, but deleted for good (only on request), so no undo
///   "restored" - a deleted file that was never moved was brought back to source
/// Nothing is ever removed from the log.
/// </summary>
public sealed class Run
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("moves")] public List<List<string>> Moves { get; set; } = [];
    /// <summary>True while an automatic run waits for the user's Approve/Undo in the report window.</summary>
    [JsonPropertyName("review")] public bool Review { get; set; }
}

/// <summary>A move identified by its run and its place in that run.</summary>
public sealed record MoveRef(string RunTime, int Index, List<string> Move);

public static class History
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    /// <summary>
    /// Held for each whole load-change-save, so the window and the background sorter never
    /// overwrite each other's changes. Re-entrant, so the steps inside can take it again.
    /// </summary>
    internal static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<Run> Load()
    {
        lock (Gate)
        {
            try
            {
                return JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(AppInfo.HistoryFile), Options) ?? [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    public static void Save(List<Run> history)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(AppInfo.HistoryFile, JsonSerializer.Serialize(history, Options));
        }
    }

    public static void AddRun(List<List<string>> moves, bool review = false)
    {
        lock (History.Gate)
        {
            var history = Load();
            history.Add(new Run { Time = DateTime.Now.ToString(TimeFormat), Moves = moves, Review = review });
            Save(history);
        }
    }

    /// <summary>True while the file is still where the program moved it.</summary>
    /// <summary>Forget every entry. The files stay where they are; what was logged can no longer be undone from here.</summary>
    public static void Clear() => Save([]);

    public static bool IsActive(List<string> move) => move.Count == 2;

    public static bool CanUndo(List<string> move) => IsActive(move) || move[2] == "deleted";

    /// <summary>Where the move's file is now, or null if it is in the Recycle Bin or gone.</summary>
    public static string? CurrentPath(List<string> move) =>
        IsActive(move) ? move[1] : move[2] is "deleted" or "erased" ? null : move[0];

    /// <summary>The most recent run that still has moves which can be undone.</summary>
    public static Run? LastActiveRun() => Enumerable.Reverse(Load()).FirstOrDefault(run => run.Moves.Any(IsActive));

    /// <summary>Moves from automatic runs the user has not approved yet.</summary>
    public static List<MoveRef> PendingMoves() =>
        Load().Where(run => run.Review)
              .SelectMany(run => run.Moves.Select((move, index) => new MoveRef(run.Time, index, move)))
              .Where(item => IsActive(item.Move)).ToList();

    public static void ApprovePending()
    {
        lock (History.Gate)
        {
            var history = Load();
            foreach (var run in history)
                run.Review = false;
            Save(history);
        }
    }
}

public static class Sorter
{
    /// <summary>Partial downloads, shortcuts and system files that must never be moved.</summary>
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".lnk", ".url" };
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db" };

    /// <summary>A path in the form used to compare paths: lower case, backslashes.</summary>
    public static string Norm(string path) => path.Replace('/', '\\').ToLowerInvariant();

    public static bool IsSkipped(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') || SkipExtensions.Contains(Path.GetExtension(name)) || SkipNames.Contains(name);
    }

    public static bool IsIgnored(string name, Config config) =>
        config.Ignore.Any(pattern => Glob.IsMatch(name.ToLowerInvariant(), pattern.ToLowerInvariant()));

    /// <summary>
    /// Days since the file arrived in the folder. The newer of created/modified is used:
    /// files extracted from archives keep an old modified time, and copied files keep one
    /// too, so modified time alone would make brand-new files look ancient.
    /// </summary>
    public static double AgeInDays(FileInfo file)
    {
        var newest = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
        return (DateTime.UtcNow - newest).TotalDays;
    }

    /// <summary>A path in folder that doesn't exist yet: name.ext, name (1).ext, ...</summary>
    public static string UniqueDestination(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), suffix = Path.GetExtension(name);
        for (var counter = 1; File.Exists(candidate) || Directory.Exists(candidate); counter++)
            candidate = Path.Combine(folder, $"{stem} ({counter}){suffix}");
        return candidate;
    }

    /// <summary>
    /// List the files that are due to move, without touching anything. everything=true is
    /// the manual "sort all now": it ignores file age and also takes files the user had put
    /// back with Undo.
    /// </summary>
    public static List<PlannedMove> PlanMoves(Config config, bool everything = false, IReadOnlyList<string>? onlyFolders = null)
    {
        // A file the user put back with Undo stays put, or the next run would just move it again.
        var undone = everything
            ? []
            : History.Load().SelectMany(run => run.Moves).Where(move => move.Count == 3 && move[2] == "undone")
                     .Select(move => Norm(move[0])).ToHashSet();
        var moves = new List<PlannedMove>();
        var roots = onlyFolders?.Select(folder => folder.Length > 0 ? folder : ConfigStore.DetectDownloadsFolder())
                    ?? config.WatchedFolders();
        foreach (var root in roots)
        {
            var rules = new Dictionary<string, (string Folder, int? MinAge)>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in config.Categories)
                foreach (var extension in category.Extensions)
                    rules[extension] = (Path.Combine(root, category.Folder), category.MinAgeDays);
            (string Folder, int? MinAge)? other =
                config.OtherFolder.Length > 0 ? (Path.Combine(root, config.OtherFolder), null) : null;
            List<string> items;
            try
            {
                items = Directory.EnumerateFiles(root).OrderBy(path => Path.GetFileName(path).ToLowerInvariant(),
                                                               StringComparer.Ordinal).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue; // folder missing or unreadable
            }
            foreach (var item in items)
            {
                try
                {
                    var name = Path.GetFileName(item);
                    if (IsSkipped(item) || IsIgnored(name, config) || undone.Contains(Norm(item)))
                        continue;
                    var rule = rules.TryGetValue(Path.GetExtension(name), out var found) ? found : other;
                    if (rule is null)
                        continue;
                    var info = new FileInfo(item);
                    var age = AgeInDays(info);
                    var minAge = everything ? 0 : rule.Value.MinAge ?? config.MinAgeDays;
                    if (age < minAge)
                        continue;
                    moves.Add(new PlannedMove(item, rule.Value.Folder, age, info.Length, root));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Vanished or unreadable mid-scan.
                }
            }
        }
        return moves;
    }

    /// <summary>
    /// Plan what to do with files and folders dropped on the drop zone: a dropped file goes
    /// into its category's folder under "root" (the first sorted folder, usually Downloads),
    /// a dropped folder has its own loose files sorted inside it. Age is not looked at.
    /// </summary>
    public static List<PlannedMove> PlanDropped(Config config, IEnumerable<string> dropped, string root)
    {
        var moves = new List<PlannedMove>();
        foreach (var path in dropped)
        {
            if (Directory.Exists(path))
            {
                moves.AddRange(PlanMoves(config, everything: true, onlyFolders: [path]));
                continue;
            }
            if (root.Length == 0 || !File.Exists(path) || IsSkipped(path) || IsIgnored(Path.GetFileName(path), config))
                continue;
            var category = config.Categories.FirstOrDefault(item => item.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
            var folder = category?.Folder ?? config.OtherFolder;
            if (folder.Length == 0)
                continue;
            var info = new FileInfo(path);
            var target = Path.Combine(root, folder);
            // Already where it belongs: nothing to do.
            if (!Norm(info.DirectoryName ?? "").Equals(Norm(target), StringComparison.Ordinal))
                moves.Add(new PlannedMove(path, target, AgeInDays(info), info.Length, root));
        }
        return moves;
    }

    /// <summary>
    /// Carry out planned moves and log them. review=true marks the run as waiting for the
    /// user's Approve/Undo in the report window.
    /// </summary>
    public static (int Count, List<string> Errors) MoveFiles(IEnumerable<PlannedMove> moves, bool review = false)
    {
        var done = new List<List<string>>();
        var errors = new List<string>();
        foreach (var move in moves)
        {
            try
            {
                Directory.CreateDirectory(move.Folder);
                var destination = UniqueDestination(move.Folder, move.Name);
                File.Move(move.Source, destination);
                done.Add([move.Source, destination]);
                Log.Info($"Moved {move.Name} -> {destination}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // File in use, permission denied, vanished: it stays for the next pass.
                errors.Add($"{move.Name}: {error.Message}");
                Log.Warning($"Skipped {move.Name}: {error.Message}");
            }
        }
        if (done.Count > 0)
            History.AddRun(done, review);
        return (done.Count, errors);
    }

    /// <summary>
    /// Reverse chosen changes. A moved file goes back where it came from; a deleted file
    /// comes back out of the Recycle Bin to where it was when it was deleted.
    /// </summary>
    public static (int Count, List<string> Errors) UndoMoves(IEnumerable<(string RunTime, int Index)> selection)
    {
        lock (History.Gate)
        {
            var history = History.Load();
            var runs = new Dictionary<string, Run>();
            foreach (var run in history)
                runs[run.Time] = run;
            var restored = 0;
            var errors = new List<string>();
            var folders = new HashSet<string>();
            foreach (var (runTime, index) in selection)
            {
                if (!runs.TryGetValue(runTime, out var run) || index < 0 || index >= run.Moves.Count)
                    continue;
                var move = run.Moves[index];
                if (!History.CanUndo(move))
                    continue;
                string source = move[0], destination = move[1];
                try
                {
                    if (History.IsActive(move))
                    {
                        var parent = Path.GetDirectoryName(source)!;
                        Directory.CreateDirectory(parent);
                        // Record where it actually landed in case the old name was taken.
                        move[0] = UniqueDestination(parent, Path.GetFileName(source));
                        File.Move(destination, move[0]);
                        move.Add("undone");
                        folders.Add(Path.GetDirectoryName(destination)!);
                    }
                    else if (destination.Length > 0)
                    {
                        // Deleted after being moved: back to where it was moved to.
                        RecycleBin.Restore(destination);
                        move.RemoveAt(2);
                    }
                    else
                    {
                        // Deleted where it lay.
                        RecycleBin.Restore(source);
                        move[2] = "restored";
                    }
                    restored++;
                    Log.Info($"Restored {Path.GetFileName(source)}");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(source)}: {error.Message}");
                }
            }
            foreach (var folder in folders)
            {
                try
                {
                    Directory.Delete(folder); // only succeeds if undoing left it empty
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
            History.Save(history);
            return (restored, errors);
        }
    }

    /// <summary>Undo every remaining move of one run: the given one, or the latest.</summary>
    public static (int Count, List<string> Errors) UndoLast(string? runTime = null)
    {
        var run = runTime is null ? History.LastActiveRun() : History.Load().FirstOrDefault(r => r.Time == runTime);
        if (run is null)
            return (0, []);
        return UndoMoves(run.Moves.Select((move, index) => (move, index)).Where(item => History.IsActive(item.move))
                            .Select(item => (run.Time, item.index)).ToList());
    }

    /// <summary>
    /// Recycle files and log each one so it can be brought back from History.
    /// permanent=true deletes them for good instead; they are still logged, but nothing
    /// can bring them back. progress(done, total) is called as it goes, and cancelling
    /// stops it (what was deleted is still logged).
    /// </summary>
    public static (int Count, List<string> Errors) DeleteFiles(
        IReadOnlyList<string> paths, bool permanent = false, Action<int, int>? progress = null,
        CancellationToken cancel = default)
    {
        lock (History.Gate)
        {
            var history = History.Load();
            var active = new Dictionary<string, List<string>>();
            foreach (var move in history.SelectMany(run => run.Moves).Where(History.IsActive))
                active[Norm(move[1])] = move;
            var unlogged = new List<List<string>>();
            var errors = new List<string>();
            var count = 0;
            for (var done = 0; done < paths.Count; done++)
            {
                if (cancel.IsCancellationRequested)
                    break;
                progress?.Invoke(done, paths.Count);
                var path = paths[done];
                try
                {
                    if (permanent)
                        File.Delete(path);
                    else
                        RecycleBin.Send(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(path)}: {error.Message}");
                    continue;
                }
                count++;
                var status = permanent ? "erased" : "deleted";
                if (active.TryGetValue(Norm(path), out var logged))
                    logged.Add(status);
                else
                    unlogged.Add([path, "", status]);
            }
            // One line for the lot: each file is in the history already, and a line per file once grew the log to megabytes.
            if (count > 0)
                Log.Info($"Deleted {count} file(s) ({(permanent ? "permanently" : "Recycle Bin")}), {errors.Count} could not be");
            if (unlogged.Count > 0)
                history.Add(new Run { Time = DateTime.Now.ToString(History.TimeFormat), Moves = unlogged, Review = false });
            History.Save(history);
            return (count, errors);
        }
    }

    /// <summary>
    /// Recycle files this program sorted into a category with "recycle_after_days", once
    /// that many days have passed since it moved them. Files the program did not put there
    /// are never touched.
    /// </summary>
    public static int CleanupExpired(Config config)
    {
        lock (History.Gate)
        {
            var limits = new Dictionary<string, int>();
            foreach (var root in config.WatchedFolders())
                foreach (var category in config.Categories.Where(c => c.RecycleAfterDays > 0))
                    limits[Norm(Path.Combine(root, category.Folder))] = category.RecycleAfterDays;
            if (limits.Count == 0)
                return 0;
            var history = History.Load();
            var count = 0;
            foreach (var run in history)
            {
                if (!DateTime.TryParseExact(run.Time, History.TimeFormat, CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var movedAt))
                    continue;
                foreach (var move in run.Moves)
                {
                    if (!History.IsActive(move))
                        continue;
                    var destination = move[1];
                    if (!limits.TryGetValue(Norm(Path.GetDirectoryName(destination) ?? ""), out var days)
                        || (DateTime.Now - movedAt).TotalDays < days || !File.Exists(destination))
                        continue;
                    try
                    {
                        RecycleBin.Send(destination);
                    }
                    catch (IOException error)
                    {
                        Log.Warning($"Could not clean up {Path.GetFileName(destination)}: {error.Message}");
                        continue;
                    }
                    move.Add("deleted");
                    count++;
                    Log.Info($"Cleaned up (Recycle Bin) {destination} after {days} days");
                }
            }
            if (count > 0)
                History.Save(history);
            return count;
        }
    }
}

/// <summary>Shell-style name patterns: * ? and [abc], as in "*.torrent".</summary>
public static class Glob
{
    public static bool IsMatch(string name, string pattern)
    {
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
                regex.Append(".*");
            else if (c == '?')
                regex.Append('.');
            else if (c == '[' && pattern.IndexOf(']', i + 1) is var close && close > i + 1)
            {
                var set = pattern[(i + 1)..close];
                regex.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set).Replace(@"\-", "-")).Append(']');
                i = close;
            }
            else
                regex.Append(Regex.Escape(c.ToString()));
        }
        regex.Append('$');
        try
        {
            return Regex.IsMatch(name, regex.ToString(), RegexOptions.Singleline);
        }
        catch (ArgumentException)
        {
            return false; // a broken pattern such as "[z-a]" matches nothing, as in Python
        }
    }
}
