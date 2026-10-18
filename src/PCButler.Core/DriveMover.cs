// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;

namespace PCButler.Core;

/// <summary>A drive something can be moved to: its root (D:\), its name and its free space.</summary>
public sealed record DriveChoice(string Root, string Label, long Free, long Total, string Format);

/// <summary>What one move (or move back) came to.</summary>
public sealed record DriveMoveResult(string Path, string Destination, bool Done, bool Linked, string Error = "", string Leftover = "");

/// <summary>
/// "Move to another drive": a big folder (a game, a project) or file goes to another drive,
/// and a link is left where it was, so Windows and every program still find it at the old
/// place while the space on the first drive is free again.
///
/// Safely, in this order: the original is renamed beside itself first (Windows refuses that
/// while a program has a file in it open, so nothing in use is ever moved); it is copied to
/// "&lt;drive&gt;\PC Butler moved\C\…", every file's size checked; the link is made at the old
/// place; only then is the original deleted. If any step fails, the copy is removed and the
/// original gets its name back. A folder is linked with a junction, which needs no
/// administrator rights; a single file needs a symbolic link, which Windows only allows with
/// Developer Mode on (or as administrator), so without it a file can be moved without a link.
///
/// Every move is logged in History as [old place, new place, "linked"] and can be undone
/// there: the link is removed and the folder or file is copied back the same careful way.
/// </summary>
public static class DriveMover
{
    public const string MovedFolder = "PC Butler moved";
    /// <summary>The History state of a move to another drive that is still in place.</summary>
    public const string Linked = "linked";
    private const string Holding = ".pcbutler-moving";
    private const long Spare = 64L << 20; // left free on the drive it goes to

    /// <summary>The drives a move can go to: ready fixed and removable drives with a file system that can hold big files.</summary>
    public static List<DriveChoice> Drives()
    {
        var drives = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                drives.Add(new DriveChoice(drive.RootDirectory.FullName, drive.VolumeLabel, drive.AvailableFreeSpace, drive.TotalSize, drive.DriveFormat));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return drives;
    }

    public static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Why this can't be moved (in English, for Tr.T), or null when it can.</summary>
    public static string? Refusal(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "It is no longer there.";
        }
        if (!File.Exists(full) && !Directory.Exists(full))
            return "It is no longer there.";
        if (full.Length <= 3 || full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full)?.TrimEnd('\\') == full)
            return "A whole drive, or a network folder, can't be moved this way.";
        if (IsLink(full))
            return "It is already a link to somewhere else (perhaps it was moved before).";
        if (full.Contains($@"\{MovedFolder}\", StringComparison.OrdinalIgnoreCase))
            return "It has already been moved to another drive.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (windows.Length > 0 && (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)))
            return "Windows' own folders are never moved.";
        // These folders themselves (and anything that holds them) stay put; what is inside them may move.
        string[] guarded =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            AppInfo.DataDir, AppContext.BaseDirectory,
        ];
        foreach (var folder in guarded.Where(folder => folder.Length > 0).Select(folder => folder.TrimEnd('\\')))
        {
            if (full.Equals(folder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                return "This is one of Windows' special folders (or holds one), so it stays where it is. What is inside it can be moved.";
        }
        if (full.EndsWith(Holding, StringComparison.OrdinalIgnoreCase))
            return "It is being moved right now.";
        return null;
    }

    /// <summary>The size and number of files of a file or folder; links inside a folder are counted as links (not followed).</summary>
    public static (long Size, int Files, int Links) Measure(string path, CancellationToken cancel = default)
    {
        if (File.Exists(path))
            return (new FileInfo(path).Length, 1, 0);
        long size = 0;
        int files = 0, links = 0;
        var waiting = new Stack<DirectoryInfo>([new DirectoryInfo(path)]);
        while (waiting.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var folder = waiting.Pop();
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    links++;
                else if (entry is DirectoryInfo inner)
                    waiting.Push(inner);
                else if (entry is FileInfo file)
                {
                    size += file.Length;
                    files++;
                }
            }
        }
        return (size, files, links);
    }

    /// <summary>Where it goes on that drive: D:\PC Butler moved\C\Games\Foo for C:\Games\Foo, with a number if that is taken.</summary>
    public static string Destination(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var below = Path.Combine(root, MovedFolder, full[..1].ToUpperInvariant(), full[3..]);
        var candidate = below;
        for (var number = 2; File.Exists(candidate) || Directory.Exists(candidate); number++)
            candidate = Path.Combine(Path.GetDirectoryName(below)!, $"{Path.GetFileNameWithoutExtension(below)} ({number}){Path.GetExtension(below)}");
        return candidate;
    }

    /// <summary>True when Windows lets this program make a symbolic link to a file (Developer Mode, or administrator).</summary>
    public static bool CanLinkFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pcbutler-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "target");
            File.WriteAllText(target, "");
            File.CreateSymbolicLink(Path.Combine(folder, "link"), target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Move a file or folder to the drive at root and leave a link in its place (a file only
    /// gets one when linkFiles is true; without it, it is moved like any other move).
    /// progress(bytes copied) is called as it goes. Nothing is logged here: see MoveAll.
    /// </summary>
    public static DriveMoveResult Move(string path, string root, bool linkFiles, Action<long>? progress, CancellationToken cancel)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (Refusal(path) is { } refused)
            return new DriveMoveResult(path, "", false, false, Tr.T(refused));
        if (string.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase))
            return new DriveMoveResult(path, "", false, false, Tr.T("It is already on that drive."));
        var folder = Directory.Exists(path);
        var (size, _, links) = Measure(path, cancel);
        if (links > 0)
            return new DriveMoveResult(path, "", false, false, Tr.T("It holds links to other places, which can't be copied safely."));
        if (new DriveInfo(root).AvailableFreeSpace < size + Spare)
            return new DriveMoveResult(path, "", false, false, Tr.T("There is not enough free space on {0}.", root.TrimEnd('\\')));
        var destination = Destination(path, root);
        var link = folder || linkFiles;
        var outcome = Relocate(path, destination, progress, cancel, makeLink: link ? () => MakeLink(path, destination, folder) : null);
        return outcome.Error.Length > 0
            ? new DriveMoveResult(path, destination, false, false, outcome.Error)
            : new DriveMoveResult(path, destination, true, link, Leftover: outcome.Leftover);
    }

    /// <summary>Move each one and log what was moved as one History run. done(index, result) reports each.</summary>
    public static List<DriveMoveResult> MoveAll(IReadOnlyList<string> paths, string root, bool linkFiles, Action<long>? progress,
                                                Action<DriveMoveResult>? done, CancellationToken cancel)
    {
        var results = new List<DriveMoveResult>();
        long before = 0;
        try
        {
            foreach (var path in paths)
            {
                if (cancel.IsCancellationRequested)
                    break;
                var start = before;
                DriveMoveResult result;
                try
                {
                    result = Move(path, root, linkFiles, copied => progress?.Invoke(start + copied), cancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result = new DriveMoveResult(path, "", false, false, error.Message);
                }
                if (result.Done)
                    Log.Info($"Moved to another drive: {result.Path} -> {result.Destination}" + (result.Linked ? " (linked)" : ""));
                else
                    Log.Warning($"Not moved to another drive: {path}: {result.Error}");
                results.Add(result);
                done?.Invoke(result);
                try
                {
                    before += result.Done ? Measure(result.Destination).Size : 0;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            // A file moved without a link is an ordinary move ([from, to]); the rest are "linked".
            var moves = results.Where(result => result.Done)
                               .Select(result => result.Linked ? new List<string> { result.Path, result.Destination, Linked } : [result.Path, result.Destination])
                               .ToList();
            if (moves.Count > 0)
                History.AddRun(moves);
        }
        return results;
    }

    /// <summary>
    /// Undo a move to another drive logged in History: remove the link (if it is still
    /// there) and bring the folder or file back the same careful way. The History entry
    /// becomes "undone".
    /// </summary>
    public static DriveMoveResult MoveBack(string runTime, int index, Action<long>? progress, CancellationToken cancel)
    {
        List<string>? move;
        lock (History.Gate)
            move = History.Load().FirstOrDefault(run => run.Time == runTime)?.Moves.ElementAtOrDefault(index);
        if (move is not [var original, var moved, Linked])
            return new DriveMoveResult("", "", false, false, Tr.T("This change can no longer be undone."));
        if (!File.Exists(moved) && !Directory.Exists(moved))
            return new DriveMoveResult(original, moved, false, false, Tr.T("It is no longer at {0}.", moved));
        var folder = Directory.Exists(moved);
        // The link at the old place goes first; anything else there now is left alone.
        if (IsLink(original))
        {
            if (folder)
                Directory.Delete(original); // a junction: only the link goes
            else
                File.Delete(original);
        }
        else if (File.Exists(original) || Directory.Exists(original))
            return new DriveMoveResult(original, moved, false, false, Tr.T("Something new is at {0} now, so nothing was moved back.", original));
        var outcome = Relocate(moved, original, progress, cancel, makeLink: null, needed: () => Measure(moved, cancel).Size);
        if (outcome.Error.Length > 0)
        {
            // Put the link back, so the old place keeps working.
            try
            {
                MakeLink(original, moved, folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"The link at {original} could not be made again: {error.Message}");
            }
            return new DriveMoveResult(original, moved, false, true, outcome.Error);
        }
        RemoveEmptyParents(Path.GetDirectoryName(moved));
        lock (History.Gate)
        {
            var history = History.Load();
            if (history.FirstOrDefault(run => run.Time == runTime)?.Moves.ElementAtOrDefault(index) is [_, _, Linked] logged)
            {
                logged[2] = "undone";
                History.Save(history);
            }
        }
        Log.Info($"Moved back from another drive: {moved} -> {original}");
        return new DriveMoveResult(original, moved, true, false, Leftover: outcome.Leftover);
    }

    /// <summary>
    /// The careful move: rename the source beside itself (refused while in use), copy it,
    /// check every size, make the link, and only then delete the renamed source. On failure
    /// the copy goes and the source gets its name back. Error is "" on success; Leftover
    /// names an old copy that could not be fully deleted.
    /// </summary>
    private static (string Error, string Leftover) Relocate(string source, string destination, Action<long>? progress, CancellationToken cancel,
                                                            Action? makeLink, Func<long>? needed = null)
    {
        var folder = Directory.Exists(source);
        if (needed is not null && Path.GetPathRoot(destination) is { } targetRoot
            && new DriveInfo(targetRoot).AvailableFreeSpace < needed() + Spare)
            return (Tr.T("There is not enough free space on {0}.", targetRoot.TrimEnd('\\')), "");
        var holding = source + Holding;
        for (var number = 2; File.Exists(holding) || Directory.Exists(holding); number++)
            holding = source + Holding + number;
        try
        {
            if (folder)
                Directory.Move(source, holding);
            else
                File.Move(source, holding);
        }
        catch (UnauthorizedAccessException)
        {
            return (Tr.T("Windows didn't allow it: something is using it, or it needs administrator rights. Close the program or game that uses it and try again."), "");
        }
        catch (IOException)
        {
            return (Tr.T("Something is using it. Close the program or game that uses it and try again."), "");
        }

        try
        {
            if (folder)
                CopyFolder(holding, destination, progress, cancel);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyFile(holding, destination, progress is null ? null : copied => progress(copied), cancel);
            }
            makeLink?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            // Undo: the copy goes, the original gets its name back.
            Erase(destination);
            try
            {
                if (folder)
                    Directory.Move(holding, source);
                else
                    File.Move(holding, source);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Could not give {holding} its name back: {back.Message}");
                return (Tr.T("It failed, and the original is safe at {0}. Rename it back to {1}.", holding, Path.GetFileName(source)), "");
            }
            return error is OperationCanceledException ? (Tr.T("Stopped. Nothing was moved."), "") : (error.Message, "");
        }
        return Erase(holding) ? ("", "") : ("", holding);
    }

    private static void MakeLink(string at, string target, bool folder)
    {
        if (!folder)
        {
            File.CreateSymbolicLink(at, target);
            return;
        }
        // A junction (mklink /J) works without administrator rights, across drives on this PC.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", at, target })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("mklink could not start");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(at))
            throw new IOException($"The link could not be made: {error.Trim()}");
    }

    private static void CopyFolder(string source, string destination, Action<long>? progress, CancellationToken cancel)
    {
        long copied = 0;
        var waiting = new Stack<(DirectoryInfo From, string To)>([(new DirectoryInfo(source), destination)]);
        var folders = new List<(DirectoryInfo From, string To)>();
        while (waiting.Count > 0)
        {
            var (from, to) = waiting.Pop();
            Directory.CreateDirectory(to);
            folders.Add((from, to));
            foreach (var entry in from.EnumerateFileSystemInfos())
            {
                cancel.ThrowIfCancellationRequested();
                var target = Path.Combine(to, entry.Name);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException(Tr.T("It holds links to other places, which can't be copied safely."));
                if (entry is DirectoryInfo inner)
                    waiting.Push((inner, target));
                else
                {
                    var start = copied;
                    CopyFile(entry.FullName, target, done => progress?.Invoke(start + done), cancel);
                    copied += ((FileInfo)entry).Length;
                }
            }
        }
        // Folder dates and attributes last, as copying into them changed their dates.
        foreach (var (from, to) in folders)
        {
            Directory.SetCreationTimeUtc(to, from.CreationTimeUtc);
            Directory.SetLastWriteTimeUtc(to, from.LastWriteTimeUtc);
            File.SetAttributes(to, from.Attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Copy one file in large pieces, then check its size and keep its dates and attributes.</summary>
    private static void CopyFile(string source, string destination, Action<long>? progress, CancellationToken cancel)
    {
        var info = new FileInfo(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            output.SetLength(info.Length);
            var buffer = new byte[4 << 20];
            long done = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                progress?.Invoke(done);
            }
            output.SetLength(done);
            output.Flush(flushToDisk: true);
        }
        var copy = new FileInfo(destination);
        if (copy.Length != info.Length)
            throw new IOException(Tr.T("The copy of {0} came out a different size, so nothing was moved.", info.Name));
        copy.CreationTimeUtc = info.CreationTimeUtc;
        copy.LastWriteTimeUtc = info.LastWriteTimeUtc;
        copy.Attributes = info.Attributes;
    }

    /// <summary>Delete a file or folder for good, read-only files too. False if something stayed.</summary>
    private static bool Erase(string path)
    {
        try
        {
            if (File.Exists(path) && !Directory.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            if (!Directory.Exists(path))
                return true;
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;
            }
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {error.Message}");
            return false;
        }
    }

    /// <summary>After a move back, the empty folders left under "PC Butler moved" go, the top one too.</summary>
    private static void RemoveEmptyParents(string? folder)
    {
        while (folder is not null && folder.Contains($@"\{MovedFolder}", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                    return;
                Directory.Delete(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return;
            }
            folder = Path.GetDirectoryName(folder);
        }
    }
}
