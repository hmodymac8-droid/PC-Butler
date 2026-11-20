// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCButler.Core;

/// <summary>A file considered by a duplicates scan.</summary>
public sealed record DuplicateEntry(string Path, long Size, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Files that match each other. The first one is the one to keep.</summary>
public sealed record DuplicateGroup(int Number, List<DuplicateEntry> Files);

/// <summary>
/// Find duplicate files: by content, by name, by name and size, by a similar name
/// (messy song/video titles), or by size. Nothing here touches the window, so a scan
/// can run on a background thread.
/// </summary>
public static partial class Duplicates
{
    /// <summary>Mode key -> the name shown to the user, in display order.</summary>
    public static readonly (string Key, string Label)[] Modes =
    [
        ("content", "Content (identical bytes)"),
        ("name", "Exact file name"),
        ("name_size", "Name + size"),
        ("name_similar", "Similar name (songs, videos)"),
        ("size", "Size only"),
    ];

    private const int ChunkSize = 1024 * 1024;
    /// <summary>
    /// In "similar name" mode a key that starts with another key at least this long joins
    /// that group, so "Title" and "Title Channel Handle" end up together.
    /// </summary>
    private const int MinPrefix = 10;

    /// <summary>Unicode-normalised, case-folded text for comparing Arabic and Latin names.</summary>
    public static string NormText(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    // Resolution tags, even when glued to a word ("Video720p").
    [GeneratedRegex(@"\d{3,4}p", RegexOptions.IgnoreCase)] private static partial Regex Resolution();
    [GeneratedRegex(@"(?<![a-z0-9])(?:4k|8k|uhd|hd|h264|h265|x264|x265|hevc|av1|vp9)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Quality();
    [GeneratedRegex(@"\b(?:feat\.?|ft\.?|featuring)\b", RegexOptions.IgnoreCase)] private static partial Regex Feat();
    [GeneratedRegex(@"\b(?:prod\.?|produced\s+by)\b", RegexOptions.IgnoreCase)] private static partial Regex Prod();
    [GeneratedRegex(@"[\(\[]\s*(?:prod\.?|produced\s+by)\s*([^)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex ProdParen();
    // Common video/music title clutter, longer phrases first.
    [GeneratedRegex(@"\b(?:official\s+music\s+video|official\s+video|music\s+video|official\s+audio|lyric\s+video|"
                    + @"full\s+video|hd\s+video|official|lyrics?|audio|video|omv|mv|hd|uhd|youtube)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JunkPhrases();
    [GeneratedRegex("[\"'`´‘’“”«»]")] private static partial Regex Quotes();
    [GeneratedRegex(@"\s*[&+]\s*|,\s*")] private static partial Regex Joiners();
    [GeneratedRegex(@"[^\w\s]|_")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>
    /// Canonical key for messy music/video file names. These all give the same key:
    ///   MC TYSON Make It Rain feat eyden Watson Official Music Video720p.mp4
    ///   MC TYSON " Make It Rain " feat. eyden &amp; Watson (Official Music Video).mp4
    /// Spaces and punctuation don't count, so "Lil $o$o" matches "Lil oo" (a downloader
    /// that deletes symbols). Trailing words that repeat the leading words are dropped,
    /// so "Artist - Title - Artist" matches "Artist - Title".
    /// </summary>
    public static string SimilarNameKey(string name)
    {
        var s = NormText(Path.GetFileNameWithoutExtension(name));
        s = Quotes().Replace(s, " ");
        s = Resolution().Replace(s, " ");
        s = Quality().Replace(s, " ");
        s = Feat().Replace(s, " feat ");
        s = ProdParen().Replace(s, " prod $1 "); // keep producer credits that only appear in (Prod. …)
        s = Prod().Replace(s, " prod ");
        s = Joiners().Replace(s, " ");
        // Text in brackets stays: it is often the real title or a translation.
        // The rules above already removed metadata like (Official MV) or [1080p].
        s = JunkPhrases().Replace(s, " ");
        s = Punctuation().Replace(s, " "); // underscore is a separator in file names, not a letter
        var tokens = Spaces().Replace(s, " ").Trim().Split(' ').ToList();
        for (var k = tokens.Count / 2; k > 0; k--)
        {
            if (tokens.Take(k).SequenceEqual(tokens.Skip(tokens.Count - k)))
            {
                tokens.RemoveRange(tokens.Count - k, k);
                break;
            }
        }
        return string.Concat(tokens);
    }

    /// <summary>How a file is read to compare contents: by its path, or on the phone, through the folder the user chose.</summary>
    public static Func<string, Stream> OpenFile { get; set; } = File.OpenRead;

    private static string FileHash(string path, CancellationToken cancel)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = OpenFile(path);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>The folders this program sorts, and their category folders.</summary>
    public static List<string> SortedFolders(Config config)
    {
        var folders = new List<string>();
        foreach (var root in config.WatchedFolders())
        {
            folders.Add(root);
            folders.AddRange(config.Categories.Select(category => Path.Combine(root, category.Folder)));
            if (config.OtherFolder.Length > 0)
                folders.Add(Path.Combine(root, config.OtherFolder));
        }
        return folders;
    }

    /// <summary>Every file in the folders, each once even when folders overlap.</summary>
    public static List<DuplicateEntry> Collect(IEnumerable<string> folders, bool recursive, CancellationToken cancel)
    {
        var found = new Dictionary<string, DuplicateEntry>();
        var order = new List<string>();
        foreach (var folder in folders)
        {
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var top = pending.Pop();
                string[] files;
                try
                {
                    files = Directory.GetFiles(top);
                    if (recursive)
                        foreach (var sub in Directory.GetDirectories(top))
                            pending.Push(sub);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var path in files)
                {
                    var key = Sorter.Norm(path);
                    if (Sorter.IsSkipped(path) || found.ContainsKey(key))
                        continue;
                    try
                    {
                        var info = new FileInfo(path);
                        found[key] = new DuplicateEntry(path, info.Length, info.LastWriteTime);
                        order.Add(key);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        return order.Select(key => found[key]).ToList();
    }

    /// <summary>
    /// Group the files by mode. Within each group the file to keep comes first: the
    /// shortest name, so "report.pdf" rather than "report (1).pdf".
    /// </summary>
    public static List<DuplicateGroup> Find(List<DuplicateEntry> files, string mode, CancellationToken cancel,
                                            Action<string, object[]>? progress = null)
    {
        var buckets = new Dictionary<string, List<DuplicateEntry>>(StringComparer.Ordinal);
        void Put(string key, DuplicateEntry entry)
        {
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add(entry);
        }

        if (mode is "content" or "size")
            files = files.Where(entry => entry.Size > 0).ToList(); // empty files all look alike
        switch (mode)
        {
            case "content":
                // A unique size can't have a twin, so only same-size files are hashed.
                var candidates = files.GroupBy(entry => entry.Size).Where(same => same.Count() > 1)
                                      .SelectMany(same => same).ToList();
                for (var done = 1; done <= candidates.Count; done++)
                {
                    var entry = candidates[done - 1];
                    progress?.Invoke("Comparing contents… {0} of {1} ({2}%)", [done, candidates.Count, 100 * done / candidates.Count]);
                    try
                    {
                        Put($"{entry.Size}:{FileHash(entry.Path, cancel)}", entry);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                break;
            case "name":
                foreach (var entry in files)
                    Put(NormText(entry.Name), entry);
                break;
            case "name_size":
                foreach (var entry in files)
                    Put($"{NormText(entry.Name)}\0{entry.Size}", entry);
                break;
            case "size":
                foreach (var entry in files)
                    Put(entry.Size.ToString(), entry);
                break;
            case "name_similar":
                foreach (var entry in files)
                {
                    var key = SimilarNameKey(entry.Name);
                    if (key.Length > 0)
                        Put(key, entry);
                }
                // Cleaned-up copies often lose or gain a tail (channel names, translated
                // subtitles), so a key that extends a shorter one joins its group.
                string? root = null;
                foreach (var key in buckets.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                {
                    if (root is not null && root.Length >= MinPrefix && key.StartsWith(root, StringComparison.Ordinal))
                    {
                        buckets[root].AddRange(buckets[key]);
                        buckets.Remove(key);
                    }
                    else
                        root = key;
                }
                break;
            default:
                throw new ArgumentException($"unknown mode '{mode}'");
        }

        var groups = buckets.Values.Where(same => same.Count > 1)
            .Select(same => same.OrderBy(e => e.Name.Length).ThenBy(e => NormText(e.Path), StringComparer.Ordinal).ToList())
            .ToList();
        IEnumerable<List<DuplicateEntry>> sorted = mode == "size"
            ? groups.OrderByDescending(g => g[0].Size).ThenByDescending(g => g.Count)
                    .ThenBy(g => NormText(g[0].Name), StringComparer.Ordinal)
            : groups.OrderByDescending(g => g.Count).ThenBy(g => NormText(g[0].Name), StringComparer.Ordinal);
        return sorted.Select((group, index) => new DuplicateGroup(index + 1, group)).ToList();
    }
}

/// <summary>Opening files and showing them in File Explorer.</summary>
public static class Shell
{
    /// <summary>Show a file (or folder) selected in File Explorer. null = done, else what went wrong.</summary>
    public static string? Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            return error.Message;
        }
    }

    /// <summary>Open a file, folder or web address with its default program. null = done, else what went wrong.</summary>
    public static string? Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            return error.Message;
        }
    }
}
