// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core;

/// <summary>One kind of junk the Cleaner page can remove.</summary>
public sealed record CleanerCategory(string Key, string Label, string Description, Func<List<string>> Folders,
                                     bool Checked = true);

/// <summary>What a scan found for one category.</summary>
public sealed class CleanerFound(CleanerCategory category)
{
    public CleanerCategory Category { get; } = category;
    public List<string> Files { get; } = [];
    public long Size { get; set; }
    public List<string> Folders { get; } = [];
}

/// <summary>
/// Find junk files that are safe to remove: temporary files, browser caches, crash
/// reports, Windows Update downloads and the like. Deleting goes through
/// Sorter.DeleteFiles, so every removed file is logged in History.
/// </summary>
public static class Cleaner
{
    /// <summary>Files changed more recently than this are left alone: a program may still be using them.</summary>
    public const int MinAgeHours = 24;
    /// <summary>Never descend into these, wherever they turn up (this program's own data).</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "pcbutler", "downloadssorter" };

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static List<string> Existing(params string?[] folders) => folders.OfType<string>().ToList();

    private static string? Under(string? root, string relative) => root is null ? null : Path.Combine(root, relative);

    /// <summary>Cache folders of Chromium browsers (every profile) and Firefox.</summary>
    private static List<string> BrowserCaches()
    {
        var folders = new List<string>();
        if (Env("LOCALAPPDATA") is not { } local)
            return folders;
        foreach (var userData in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data",
                     @"BraveSoftware\Brave-Browser\User Data", @"Vivaldi\User Data", @"Opera Software\Opera Stable" })
        {
            var root = Path.Combine(local, userData);
            if (!Directory.Exists(root))
                continue;
            var profiles = new List<string> { root };
            try
            {
                profiles.AddRange(Directory.GetDirectories(root));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            foreach (var profile in profiles)
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                    if (Directory.Exists(Path.Combine(profile, cache)))
                        folders.Add(Path.Combine(profile, cache));
        }
        var firefox = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
        {
            try
            {
                folders.AddRange(Directory.GetDirectories(firefox).Select(p => Path.Combine(p, "cache2")).Where(Directory.Exists));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return folders;
    }

    public static readonly IReadOnlyList<CleanerCategory> Categories =
    [
        new("user_temp", "Your temporary files", "Left behind by installers and programs (%TEMP%)",
            () => Existing(Env("TEMP"))),
        new("windows_temp", "Windows temporary files", @"C:\Windows\Temp (some files need administrator rights)",
            () => Existing(Under(Env("SystemRoot"), "Temp"))),
        new("browser_cache", "Browser caches", "Chrome, Edge, Brave, Vivaldi, Opera and Firefox. "
            + "Close the browsers first; sign-ins and history are kept", BrowserCaches),
        new("crash_reports", "Crash reports and dumps", "Windows Error Reporting files and program crash dumps",
            () => Existing(Under(Env("LOCALAPPDATA"), "CrashDumps"), Under(Env("LOCALAPPDATA"), @"Microsoft\Windows\WER"),
                           Under(Env("ProgramData"), @"Microsoft\Windows\WER"))),
        new("thumbnails", "Thumbnail cache", "Explorer rebuilds it as needed (files in use are skipped)",
            () => Existing(Under(Env("LOCALAPPDATA"), @"Microsoft\Windows\Explorer"))),
        new("update_cache", "Windows Update downloads", "Already-installed update files (needs administrator rights)",
            () => Existing(Under(Env("SystemRoot"), @"SoftwareDistribution\Download")), Checked: false),
    ];

    public static CleanerCategory ByKey(string key) => Categories.First(category => category.Key == key);

    private static bool Wanted(CleanerCategory category, string path)
    {
        if (category.Key != "thumbnails")
            return true;
        var name = Path.GetFileName(path);
        return name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Collect the junk files of the chosen categories. Nothing is deleted here.</summary>
    public static List<CleanerFound> Scan(IEnumerable<string> keys, CancellationToken cancel,
                                          Action<string, object[]>? progress = null)
    {
        var cutoff = DateTime.UtcNow.AddHours(-MinAgeHours);
        var results = new List<CleanerFound>();
        var seen = new HashSet<string>();
        foreach (var key in keys)
        {
            var category = ByKey(key);
            var found = new CleanerFound(category);
            foreach (var root in category.Folders())
            {
                if (!Directory.Exists(root))
                    continue;
                found.Folders.Add(root);
                progress?.Invoke("Scanning: {0}…", [category.Label]);
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    var folder = pending.Pop();
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(folder);
                        foreach (var sub in Directory.GetDirectories(folder))
                            if (!SkipDirs.Contains(Path.GetFileName(sub)) && !IsLink(sub))
                                pending.Push(sub);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    foreach (var path in files)
                    {
                        var keyPath = path.ToLowerInvariant();
                        if (seen.Contains(keyPath) || !Wanted(category, path))
                            continue;
                        try
                        {
                            var info = new FileInfo(path);
                            if (info.LastWriteTimeUtc > cutoff)
                                continue;
                            seen.Add(keyPath);
                            found.Files.Add(path);
                            found.Size += info.Length;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
            results.Add(found);
        }
        return results;
    }

    /// <summary>Remove folders the clean-up emptied, below (never including) each root.</summary>
    public static void RemoveEmptyFolders(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
                RemoveEmptyBelow(root, isRoot: true);
        }
    }

    private static void RemoveEmptyBelow(string folder, bool isRoot)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(folder))
                if (!IsLink(sub))
                    RemoveEmptyBelow(sub, isRoot: false);
            if (!isRoot)
                Directory.Delete(folder); // only succeeds when it is empty
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
