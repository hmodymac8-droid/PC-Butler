// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace PCButler.Core.Search;

/// <summary>What a walk leaves out: excluded folders and name patterns, hidden or system files, other users' profiles.</summary>
public sealed class ScanRules
{
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> patterns = [];
    private readonly bool hidden, system;
    private readonly string usersFolder = "", me = "";

    public ScanRules(SearchSettings settings)
    {
        foreach (var rule in settings.Exclude.Select(rule => rule.Trim()).Where(rule => rule.Length > 0))
        {
            if (rule.Contains('\\') || rule.Contains('/'))
                folders.Add(Path.GetFullPath(rule).TrimEnd('\\', '/'));
            else
                patterns.Add(rule);
        }
        (hidden, system) = (settings.ExcludeHidden, settings.ExcludeSystem);
        if (settings.ExcludeOtherUsers)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0)
                (usersFolder, me) = (Path.GetDirectoryName(profile) ?? "", Path.GetFileName(profile));
        }
    }

    /// <summary>True when an entry found in folder is left out of the index.</summary>
    public bool Skip(string folder, ReadOnlySpan<char> name, FileAttributes attributes)
    {
        if ((hidden && attributes.HasFlag(FileAttributes.Hidden)) || (system && attributes.HasFlag(FileAttributes.System)))
            return true;
        var directory = attributes.HasFlag(FileAttributes.Directory);
        if (directory && usersFolder.Length > 0 && folder.TrimEnd('\\').Equals(usersFolder, StringComparison.OrdinalIgnoreCase)
            && !name.Equals(me, StringComparison.OrdinalIgnoreCase) && !name.Equals("Public", StringComparison.OrdinalIgnoreCase))
            return true;
        if (patterns.Count > 0)
        {
            var text = name.ToString();
            foreach (var pattern in patterns)
                if (Glob.IsMatch(text, pattern) || text.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return directory && folders.Count > 0 && folders.Contains(SearchIndex.Join(folder, name));
    }

    /// <summary>True when a path itself sits in an excluded folder.</summary>
    public bool Excluded(string path) => folders.Any(folder => SearchSettings.Inside(path, folder));
}

/// <summary>
/// Indexing without administrator rights: walk the folders and add what is there. Windows is
/// asked for whole batches of directory entries at once (a 64 KB buffer, as FindFirstFileEx's
/// "large fetch" does); .NET's enumerator hands each entry over without making a string of
/// its name, which matters at millions of files.
/// </summary>
public static class FolderScanSource
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int BackgroundBegin = 0x00010000, BackgroundEnd = 0x00020000;

    /// <summary>
    /// Lower this thread's processor and disk priority while a walk runs, so the PC stays quick
    /// (Windows' background mode). Dispose to end it.
    /// </summary>
    public static IDisposable Background()
    {
        var on = OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), BackgroundBegin);
        return new Ending(() =>
        {
            if (on)
                SetThreadPriority(GetCurrentThread(), BackgroundEnd);
        });
    }

    private sealed class Ending(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, BufferSize = 65536,
        ReturnSpecialDirectories = false, MatchType = MatchType.Win32,
    };

    /// <summary>Walk a drive or folder into the index, as a new root. Returns the root's row.</summary>
    public static int Scan(SearchIndex into, string root, ScanRules rules, CancellationToken cancel = default, Action<int>? progress = null)
    {
        var full = Path.GetFullPath(root);
        if (full.Length > 3)
            full = full.TrimEnd('\\', '/');
        var row = into.AddRoot(full);
        ScanInto(into, full, row, rules, cancel, progress);
        return row;
    }

    /// <summary>Walk a folder whose row is already in the index and add everything under it.</summary>
    public static void ScanInto(SearchIndex into, string folder, int folderRow, ScanRules rules, CancellationToken cancel = default, Action<int>? progress = null)
    {
        var pending = new Stack<(string Path, int Row)>();
        pending.Push((folder, folderRow));
        var added = 0;
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (path, row) = pending.Pop();
            try
            {
                var walk = new FileSystemEnumerable<byte>(path, (ref FileSystemEntry _) => 0, Options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        var attributes = entry.Attributes;
                        if (rules.Skip(path, entry.FileName, attributes))
                            return false;
                        var directory = entry.IsDirectory;
                        var flags = (byte)((directory ? SearchIndex.Folder : 0)
                                           | (attributes.HasFlag(FileAttributes.Hidden) ? SearchIndex.Hidden : 0)
                                           | (attributes.HasFlag(FileAttributes.System) ? SearchIndex.SystemFile : 0));
                        var child = into.Add(row, entry.FileName, flags, directory ? -1 : entry.Length, SearchIndex.ToSeconds(entry.LastWriteTimeUtc.UtcDateTime));
                        added++;
                        // Into folders, but not through links (a junction can loop back, or lead to what is indexed already).
                        if (directory)
                        {
                            var childPath = entry.ToFullPath();
                            if (!attributes.HasFlag(FileAttributes.ReparsePoint) || !IsLink(childPath))
                                pending.Push((childPath, child));
                        }
                        return false;
                    },
                };
                foreach (var _ in walk)
                {
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Gone, or not ours to read: what was found is kept.
            }
            if (progress is not null && added >= 2000)
            {
                progress(added);
                added = 0;
            }
        }
        if (added > 0)
            progress?.Invoke(added);
    }

    /// <summary>A junction or a symbolic link (a cloud folder, also a reparse point, is walked).</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

public enum ChangeKind { Created, Deleted, Renamed, Changed, Lost }

/// <summary>Something that happened under a watched folder. Lost: Windows dropped changes (too many at once), so the folder needs walking again.</summary>
public readonly record struct FileChange(ChangeKind Kind, string Path, string? OldPath = null);

/// <summary>Watches a drive or folder, and everything under it, for files that come, go, are renamed or change.</summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher watcher;

    public string Root { get; }

    public FolderWatcher(string root, Action<FileChange> changed)
    {
        Root = root;
        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        watcher.Created += (_, e) => changed(new FileChange(ChangeKind.Created, e.FullPath));
        watcher.Deleted += (_, e) => changed(new FileChange(ChangeKind.Deleted, e.FullPath));
        watcher.Changed += (_, e) => changed(new FileChange(ChangeKind.Changed, e.FullPath));
        watcher.Renamed += (_, e) => changed(new FileChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));
        watcher.Error += (_, e) =>
        {
            Log.Warning($"Search: changes under {root} were missed ({e.GetException().Message})");
            changed(new FileChange(ChangeKind.Lost, root));
        };
        watcher.EnableRaisingEvents = true;
    }

    public void Dispose() => watcher.Dispose();
}
