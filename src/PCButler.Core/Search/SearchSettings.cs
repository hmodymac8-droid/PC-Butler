// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json.Serialization;

namespace PCButler.Core.Search;

/// <summary>File search: what is indexed and how names are matched. Kept in config.json under "search".</summary>
public sealed class SearchSettings
{
    /// <summary>Keep the index (and the search box shortcut) running.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Drives to index, as "C:\"; null = every fixed drive.</summary>
    [JsonPropertyName("drives")] public List<string>? Drives { get; set; }
    /// <summary>More folders to index: on a USB drive, a network share...</summary>
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];
    /// <summary>Left out: full folder paths, or name patterns such as node_modules or *.tmp.</summary>
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = DefaultExclusions();
    /// <summary>Leave out the profile folders of the PC's other users.</summary>
    [JsonPropertyName("exclude_other_users")] public bool ExcludeOtherUsers { get; set; } = true;
    [JsonPropertyName("exclude_hidden")] public bool ExcludeHidden { get; set; }
    [JsonPropertyName("exclude_system")] public bool ExcludeSystem { get; set; }
    /// <summary>Keep each file's size (8 bytes per file).</summary>
    [JsonPropertyName("index_size")] public bool IndexSize { get; set; } = true;
    /// <summary>Keep each file's date modified (4 bytes per file).</summary>
    [JsonPropertyName("index_modified")] public bool IndexModified { get; set; } = true;
    /// <summary>Accents and Arabic tashkeel must match (off: محمد also finds مُحَمَّد).</summary>
    [JsonPropertyName("match_diacritics")] public bool MatchDiacritics { get; set; }
    /// <summary>Treat أ إ آ ا, ى ي and ة ه as the same letter.</summary>
    [JsonPropertyName("arabic_variants")] public bool ArabicVariants { get; set; } = true;
    [JsonPropertyName("match_case")] public bool MatchCase { get; set; }
    /// <summary>Hours after which the folders are walked again at start, to catch what changed while PC Butler was closed; 0 = never.</summary>
    [JsonPropertyName("rescan_hours")] public int RescanHours { get; set; } = 24;

    public static List<string> DefaultExclusions()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 ? [Path.Combine(windows, "WinSxS"), "$Recycle.Bin", "System Volume Information"] : ["$Recycle.Bin", "System Volume Information"];
    }

    /// <summary>The fixed drives of this PC.</summary>
    public static List<string> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Select(drive => drive.RootDirectory.FullName).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The folders to walk: the chosen drives and folders, without any that sit inside another.</summary>
    public List<string> Roots()
    {
        var wanted = (Drives ?? FixedDrives()).Concat(Folders)
            .Where(path => path.Length > 0)
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
        var roots = new List<string>();
        foreach (var path in wanted)
            if (!roots.Any(root => Inside(path, root)))
                roots.Add(path);
        return roots;
    }

    public static bool Inside(string path, string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        return path.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || path.Equals(folder, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the index depends on: a saved index made with other settings is walked again.</summary>
    public string Fingerprint() =>
        string.Join("|", Roots().Select(root => root.ToLowerInvariant())) + "#" + string.Join("|", Exclude.Select(rule => rule.ToLowerInvariant()))
        + $"#{ExcludeOtherUsers}{ExcludeHidden}{ExcludeSystem}{IndexSize}{IndexModified}{ArabicVariants}";

    public SearchSettings Clone() => new()
    {
        Enabled = Enabled, Drives = Drives is null ? null : [.. Drives], Folders = [.. Folders], Exclude = [.. Exclude],
        ExcludeOtherUsers = ExcludeOtherUsers, ExcludeHidden = ExcludeHidden, ExcludeSystem = ExcludeSystem,
        IndexSize = IndexSize, IndexModified = IndexModified, MatchDiacritics = MatchDiacritics, ArabicVariants = ArabicVariants,
        MatchCase = MatchCase, RescanHours = RescanHours,
    };
}
