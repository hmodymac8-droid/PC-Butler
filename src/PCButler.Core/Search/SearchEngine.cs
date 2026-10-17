// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;

namespace PCButler.Core.Search;

public enum SearchState { Off, Loading, Scanning, Ready }

/// <summary>
/// Keeps the search index for the whole program: loads the saved index at start (ready at
/// once), watches the indexed folders for changes, walks them again in the background when the
/// saved index is old or the settings changed, and saves the index now and then and at exit.
///
/// A walk builds a new index beside the current one, which keeps answering searches; changes
/// seen meanwhile are applied to both, and the new index takes over when it is complete.
/// </summary>
public sealed class SearchEngine : IDisposable
{
    private readonly object applyGate = new();
    private readonly ConcurrentQueue<FileChange> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly List<FolderWatcher> watchers = [];
    private readonly string file;
    private SearchSettings settings = new();
    private ScanRules rules = new(new SearchSettings { Exclude = [] });
    private volatile SearchIndex current = new();
    private List<FileChange>? replay;
    private Thread? pump;
    private CancellationTokenSource? stopping, scanning;
    private volatile SearchState state = SearchState.Off;
    private int scanned, savedGeneration = -1, orderedGeneration = -1, toldGeneration = -1;
    private DateTime lastSave = DateTime.UtcNow, lastOrder = DateTime.MinValue, lastChange = DateTime.UtcNow, scannedAt = DateTime.MinValue;
    private bool rescanWanted;

    public SearchEngine(string? file = null) => this.file = file ?? IndexStore.DefaultFile;

    /// <summary>The index searches read. A walk replaces it with a new one when done.</summary>
    public SearchIndex Index => current;
    public SearchState State => state;
    /// <summary>Files and folders found so far by the walk that is running.</summary>
    public int Scanned => Volatile.Read(ref scanned);
    /// <summary>When the folders were last walked in full (UTC), MinValue if never.</summary>
    public DateTime ScannedAt => scannedAt;
    public SearchSettings Settings => settings;
    /// <summary>The index file's size in bytes, 0 when there is none.</summary>
    public long FileBytes => File.Exists(file) ? new FileInfo(file).Length : 0;

    /// <summary>The index changed (files came, went or were renamed). Raised on a background thread, at most twice a second.</summary>
    public event Action? Changed;
    /// <summary>State or the walk's progress changed. Raised on a background thread.</summary>
    public event Action? StateChanged;

    /// <summary>Start with these settings: load the saved index, watch the folders, walk them when needed.</summary>
    public void Start(SearchSettings wanted)
    {
        Stop();
        settings = wanted.Clone();
        if (!settings.Enabled)
            return;
        rules = new ScanRules(settings);
        stopping = new CancellationTokenSource();
        SetState(SearchState.Loading);
        var token = stopping.Token;
        pump = new Thread(() => Run(token)) { IsBackground = true, Name = "PC Butler search", Priority = ThreadPriority.BelowNormal };
        pump.Start();
    }

    /// <summary>New settings: walks again only when what is indexed changed.</summary>
    public void Apply(SearchSettings wanted)
    {
        if (wanted.Enabled != settings.Enabled || wanted.Fingerprint() != settings.Fingerprint() || state == SearchState.Off)
        {
            Start(wanted);
            return;
        }
        settings = wanted.Clone();
    }

    /// <summary>Walk every indexed folder again now.</summary>
    public void Rescan()
    {
        rescanWanted = true;
        wake.Set();
    }

    public SearchResult Search(string query, SearchSort sort, bool descending, CancellationToken cancel) =>
        Searcher.Run(current, query, sort, descending, new SearchOptions(settings.MatchCase, settings.MatchDiacritics), cancel);

    private void SetState(SearchState value)
    {
        state = value;
        StateChanged?.Invoke();
    }

    private void Run(CancellationToken stop)
    {
        try
        {
            var loaded = IndexStore.Load(file);
            var fingerprint = settings.Fingerprint();
            if (loaded is { } saved && saved.Info.Fingerprint == fingerprint && saved.Index.Variants == settings.ArabicVariants)
            {
                current = saved.Index;
                scannedAt = saved.Info.ScannedUtc;
                savedGeneration = orderedGeneration = current.Generation;
                if (current.Order is null)
                    BuildOrder();
                Log.Info($"Search: loaded {current.Live:N0} entries");
                // Broken entries taken out at load leave their folders missing until they are walked again.
                rescanWanted = current.Repaired > 0
                               || (settings.RescanHours > 0 && DateTime.UtcNow - scannedAt > TimeSpan.FromHours(settings.RescanHours));
                SetState(SearchState.Ready);
            }
            else
                rescanWanted = true;
            Watch();
            Changed?.Invoke();
            while (!stop.IsCancellationRequested)
            {
                if (rescanWanted && scanning is null)
                {
                    rescanWanted = false;
                    StartScan(stop);
                }
                wake.WaitOne(300);
                if (stop.IsCancellationRequested)
                    break;
                ApplyQueued();
                Housekeeping();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Error($"Search stopped: {error}");
        }
    }

    private void Watch()
    {
        foreach (var root in settings.Roots())
        {
            try
            {
                watchers.Add(new FolderWatcher(root, change =>
                {
                    queue.Enqueue(change);
                    lastChange = DateTime.UtcNow;
                }));
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Log.Warning($"Search: {root} cannot be watched for changes ({error.Message})");
            }
        }
    }

    /// <summary>Walk all the folders into a new index, one drive per thread, in the background.</summary>
    private void StartScan(CancellationToken stop)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        scanning = cancel;
        lock (applyGate)
            replay = [];
        Volatile.Write(ref scanned, 0);
        SetState(SearchState.Scanning);
        var token = cancel.Token;
        var scanSettings = settings;
        var scanRules = rules;
        new Thread(() => Scan(scanSettings, scanRules, token)) { IsBackground = true, Name = "PC Butler search walk", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void Scan(SearchSettings scanSettings, ScanRules scanRules, CancellationToken cancel)
    {
        var started = DateTime.UtcNow;
        try
        {
            var groups = scanSettings.Roots().GroupBy(root => Path.GetPathRoot(root) ?? root, StringComparer.OrdinalIgnoreCase).ToList();
            var parts = new SearchIndex[groups.Count];
            var threads = groups.Select((group, number) => new Thread(() =>
            {
                using var quiet = FolderScanSource.Background();
                var part = new SearchIndex(scanSettings.IndexSize, scanSettings.IndexModified, scanSettings.ArabicVariants, 1 << 16);
                foreach (var root in group)
                {
                    if (!Directory.Exists(root))
                        continue;
                    try
                    {
                        FolderScanSource.Scan(part, root, scanRules, cancel, found =>
                        {
                            Interlocked.Add(ref scanned, found);
                            StateChanged?.Invoke();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                parts[number] = part;
            }) { IsBackground = true, Name = "PC Butler search walk " + group.Key, Priority = ThreadPriority.BelowNormal }).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());
            cancel.ThrowIfCancellationRequested();
            var built = parts.Length == 1 && parts[0] is not null
                ? parts[0]
                : new SearchIndex(scanSettings.IndexSize, scanSettings.IndexModified, scanSettings.ArabicVariants, Math.Max(1024, parts.Sum(part => part?.Count ?? 0)));
            if (parts.Length != 1)
                foreach (var part in parts.Where(part => part is not null))
                    built.Append(part);
            built.Gate.EnterReadLock();
            try
            {
                built.BuildOrder(cancel);
            }
            finally
            {
                built.Gate.ExitReadLock();
            }
            Swap(built);
            scannedAt = started;
            Log.Info($"Search: walked {built.Live:N0} entries in {(DateTime.UtcNow - started).TotalSeconds:0} s");
            Save();
            SetState(SearchState.Ready);
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            lock (applyGate)
                replay = null;
        }
        finally
        {
            scanning = null;
        }
    }

    /// <summary>The new index takes over, with the changes seen while it was being built.</summary>
    private void Swap(SearchIndex built)
    {
        lock (applyGate)
        {
            built.Gate.EnterWriteLock();
            try
            {
                foreach (var change in replay ?? [])
                    Apply(built, change);
            }
            finally
            {
                built.Gate.ExitWriteLock();
            }
            replay = null;
            current = built;
            orderedGeneration = -1;
        }
    }

    private void ApplyQueued()
    {
        if (queue.IsEmpty)
            return;
        var batch = new List<FileChange>();
        while (batch.Count < 20000 && queue.TryDequeue(out var change))
            batch.Add(change);
        if (batch.Any(change => change.Kind == ChangeKind.Lost))
            rescanWanted = true;
        lock (applyGate)
        {
            var index = current;
            index.Gate.EnterWriteLock();
            try
            {
                foreach (var change in batch)
                    Apply(index, change);
            }
            finally
            {
                index.Gate.ExitWriteLock();
            }
            replay?.AddRange(batch);
        }
        if (!queue.IsEmpty)
            wake.Set();
    }

    /// <summary>Tell listeners about changes, keep the order fresh, compact, save.</summary>
    private void Housekeeping()
    {
        var index = current;
        var now = DateTime.UtcNow;
        if (index.Generation != toldGeneration)
        {
            toldGeneration = index.Generation;
            Changed?.Invoke();
        }
        if (state != SearchState.Ready)
            return;
        var quiet = now - lastChange > TimeSpan.FromSeconds(3);
        // Rows added or renamed since the order was built are sorted one by one in each search: fine for a few thousand.
        if (index.Unranked > 0 && quiet && index.Generation != orderedGeneration
            && (index.Unranked > 20000 || index.Order is null || now - lastOrder > TimeSpan.FromMinutes(10)))
            BuildOrder();
        if (index.Waste > 0.25 && index.Count > 100000 && quiet)
            Compact();
        if (index.Generation != savedGeneration && now - lastSave > TimeSpan.FromMinutes(15) && quiet)
            Save();
    }

    private void BuildOrder()
    {
        var index = current;
        index.Gate.EnterReadLock();
        try
        {
            index.BuildOrder();
            orderedGeneration = index.Generation;
            lastOrder = DateTime.UtcNow;
        }
        finally
        {
            index.Gate.ExitReadLock();
        }
    }

    private void Compact()
    {
        lock (applyGate)
        {
            var index = current;
            index.Gate.EnterReadLock();
            SearchIndex compact;
            try
            {
                compact = index.Compacted();
            }
            finally
            {
                index.Gate.ExitReadLock();
            }
            current = compact;
            orderedGeneration = compact.Generation;
        }
        Changed?.Invoke();
    }

    /// <summary>Write the index to disk now.</summary>
    public void Save()
    {
        var index = current;
        if (index.Count == 0)
            return;
        try
        {
            var generation = index.Generation;
            IndexStore.Save(index, file, new IndexInfo(settings.Fingerprint(), DateTime.UtcNow, scannedAt));
            savedGeneration = generation;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Search: the index could not be saved ({error.Message})");
        }
    }

    // --- applying one change ---

    private void Apply(SearchIndex index, FileChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Created:
                AddPath(index, change.Path);
                break;
            case ChangeKind.Deleted:
                if (index.Find(change.Path) is var gone and >= 0)
                    index.Remove(gone);
                break;
            case ChangeKind.Changed:
                if (index.Find(change.Path) is var row and >= 0 && !index.IsFolder(row))
                {
                    var info = new FileInfo(change.Path);
                    if (info.Exists)
                        index.Update(row, info.Length, info.LastWriteTimeUtc);
                }
                break;
            case ChangeKind.Renamed when change.OldPath is not null:
                var moved = index.Find(change.OldPath);
                var folder = index.Find(Path.GetDirectoryName(change.Path) ?? "");
                if (moved < 0 || folder < 0 || rules.Excluded(change.Path) || rules.Skip(index.FullPath(folder), Path.GetFileName(change.Path), Attributes(change.Path)))
                {
                    if (moved >= 0)
                        index.Remove(moved);
                    AddPath(index, change.Path);
                    break;
                }
                if (index.Find(change.Path) is var replaced and >= 0 && replaced != moved)
                    index.Remove(replaced);
                index.Rename(moved, Path.GetFileName(change.Path), folder);
                break;
        }
    }

    private static FileAttributes Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>A file or folder that appeared: add it (a folder with what is in it, when it was moved in whole).</summary>
    private void AddPath(SearchIndex index, string path)
    {
        if (rules.Excluded(path))
            return;
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var existing = index.Find(path);
        if (!info.Exists)
        {
            if (existing >= 0)
                index.Remove(existing);
            return;
        }
        var folder = index.Find(Path.GetDirectoryName(path) ?? "");
        if (folder < 0 || rules.Skip(index.FullPath(folder), info.Name, info.Attributes))
            return;
        var directory = info is DirectoryInfo;
        if (existing >= 0)
        {
            if (!directory)
                index.Update(existing, ((FileInfo)info).Length, info.LastWriteTimeUtc);
            return;
        }
        var flags = (byte)((directory ? SearchIndex.Folder : 0) | (info.Attributes.HasFlag(FileAttributes.Hidden) ? SearchIndex.Hidden : 0)
                           | (info.Attributes.HasFlag(FileAttributes.System) ? SearchIndex.SystemFile : 0));
        var row = index.Add(folder, info.Name, flags, directory ? -1 : ((FileInfo)info).Length, SearchIndex.ToSeconds(info.LastWriteTimeUtc));
        if (directory && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            FolderScanSource.ScanInto(index, path, row, rules);
    }

    /// <summary>Stop watching and walking, and save what changed.</summary>
    public void Stop()
    {
        if (stopping is null)
            return;
        stopping.Cancel();
        wake.Set();
        pump?.Join(TimeSpan.FromSeconds(5));
        foreach (var watcher in watchers)
            watcher.Dispose();
        watchers.Clear();
        while (queue.TryDequeue(out _))
        {
        }
        if (current.Generation != savedGeneration && state == SearchState.Ready)
            Save();
        stopping.Dispose();
        stopping = null;
        pump = null;
        SetState(SearchState.Off);
    }

    public void Dispose()
    {
        Stop();
        wake.Dispose();
    }
}
