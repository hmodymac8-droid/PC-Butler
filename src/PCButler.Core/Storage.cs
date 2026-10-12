// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core;

/// <summary>A folder or file in a storage scan, with the space it takes.</summary>
public sealed class StorageNode
{
    public required string Name { get; init; }
    /// <summary>Full path; empty for the "other files" entry that stands for a folder's small files.</summary>
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    /// <summary>Files inside, at any depth (1 for a file).</summary>
    public long Files { get; set; }
    public StorageNode? Parent { get; set; }
    /// <summary>Biggest first.</summary>
    public List<StorageNode> Children { get; } = [];
}

/// <summary>A file big enough to be worth listing on its own.</summary>
public sealed record BigFile(string Path, long Size, DateTime LastUsed)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed class StorageResult
{
    public required StorageNode Root { get; init; }
    /// <summary>Every file of at least <see cref="Storage.BigFileMinimum"/> bytes, biggest first.</summary>
    public List<BigFile> BigFiles { get; } = [];
    /// <summary>Folders that could not be read (no permission).</summary>
    public int Skipped { get; set; }
}

/// <summary>
/// Work out what takes up the space in a folder or on a drive. Nothing is changed; a scan
/// only reads names and sizes, so it can run on a background thread.
/// </summary>
public static class Storage
{
    public const long BigFileMinimum = 50L * 1024 * 1024;
    /// <summary>A folder's files beyond this many (by size) are shown together as one entry.</summary>
    private const int FilesKeptPerFolder = 40;

    public static StorageResult Scan(string root, CancellationToken cancel, Action<string>? progress = null)
    {
        var top = new DirectoryInfo(root);
        var node = new StorageNode { Name = top.Name.Length > 0 ? top.Name : root, Path = top.FullName, IsFolder = true };
        var result = new StorageResult { Root = node };
        var folders = 0;
        Fill(node, top, result, cancel, progress, ref folders);
        result.BigFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
        return result;
    }

    private static void Fill(StorageNode node, DirectoryInfo folder, StorageResult result, CancellationToken cancel,
                             Action<string>? progress, ref int folders)
    {
        cancel.ThrowIfCancellationRequested();
        if (++folders % 200 == 0)
            progress?.Invoke(folder.FullName);
        var files = new List<StorageNode>();
        try
        {
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo sub)
                {
                    // Links and junctions point elsewhere; following them would count space twice or loop.
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    var child = new StorageNode { Name = sub.Name, Path = sub.FullName, IsFolder = true, Parent = node };
                    Fill(child, sub, result, cancel, progress, ref folders);
                    if (child.Size > 0 || child.Files > 0)
                        node.Children.Add(child);
                    node.Size += child.Size;
                    node.Files += child.Files;
                }
                else if (entry is FileInfo file)
                {
                    long size;
                    DateTime used;
                    try
                    {
                        size = file.Length;
                        // "Last used": opened or changed, whichever is later (Windows does not always record opening).
                        used = file.LastAccessTime > file.LastWriteTime ? file.LastAccessTime : file.LastWriteTime;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    node.Size += size;
                    node.Files++;
                    files.Add(new StorageNode { Name = file.Name, Path = file.FullName, Size = size, Files = 1, Parent = node });
                    if (size >= BigFileMinimum)
                        result.BigFiles.Add(new BigFile(file.FullName, size, used));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            result.Skipped++;
        }
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Children.AddRange(files.Take(FilesKeptPerFolder));
        if (files.Count > FilesKeptPerFolder)
        {
            var rest = files.Skip(FilesKeptPerFolder).ToList();
            node.Children.Add(new StorageNode
            {
                Name = "", Path = "", Size = rest.Sum(file => file.Size), Files = rest.Count, Parent = node,
            });
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>The big files of at least minimumSize that have not been used for the given number of months.</summary>
    public static List<BigFile> LargeAndOld(StorageResult result, long minimumSize, int unusedMonths) =>
        result.BigFiles.Where(file => file.Size >= minimumSize
                                      && (unusedMonths <= 0 || file.LastUsed < DateTime.Now.AddMonths(-unusedMonths)))
              .ToList();

    /// <summary>
    /// Squarified treemap: place each size as a rectangle inside the area, keeping the
    /// rectangles as close to square as possible. sizes must be biggest first and positive.
    /// Returns (x, y, width, height) per size.
    /// </summary>
    public static List<(double X, double Y, double Width, double Height)> Treemap(
        IReadOnlyList<double> sizes, double width, double height)
    {
        var rectangles = new List<(double, double, double, double)>(sizes.Count);
        var total = sizes.Sum();
        if (total <= 0 || width <= 0 || height <= 0)
            return rectangles;
        var scale = width * height / total;
        double x = 0, y = 0, w = width, h = height;
        var start = 0;
        while (start < sizes.Count)
        {
            // Grow the row while its worst aspect ratio keeps improving.
            var side = Math.Min(w, h);
            var end = start;
            double sum = 0, worst = double.MaxValue;
            while (end < sizes.Count)
            {
                var area = sizes[end] * scale;
                var next = sum + area;
                double largest = sizes[start] * scale, smallest = area;
                var ratio = Math.Max(side * side * largest / (next * next), next * next / (side * side * smallest));
                if (end > start && ratio > worst)
                    break;
                (sum, worst) = (next, ratio);
                end++;
            }
            // Lay the row out along the shorter side, then continue in what is left.
            var thickness = sum / side;
            double offset = 0;
            for (var index = start; index < end; index++)
            {
                var length = sizes[index] * scale / thickness;
                rectangles.Add(w >= h ? (x, y + offset, thickness, length) : (x + offset, y, length, thickness));
                offset += length;
            }
            if (w >= h)
                (x, w) = (x + thickness, w - thickness);
            else
                (y, h) = (y + thickness, h - thickness);
            start = end;
        }
        return rectangles;
    }
}
