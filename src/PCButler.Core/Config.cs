// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>One destination folder and the file types that go there.</summary>
public sealed class Category
{
    /// <summary>Relative to the sorted folder unless it is an absolute path.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("extensions")] public List<string> Extensions { get; set; } = [];
    /// <summary>null uses the general setting.</summary>
    [JsonPropertyName("min_age_days")] public int? MinAgeDays { get; set; }
    /// <summary>
    /// Files this program put there go to the Recycle Bin that many days after it moved them; 0 = never.
    /// </summary>
    [JsonPropertyName("recycle_after_days")] public int RecycleAfterDays { get; set; }

    public Category Clone() => new()
    {
        Folder = Folder, Extensions = [.. Extensions], MinAgeDays = MinAgeDays, RecycleAfterDays = RecycleAfterDays,
    };
}

/// <summary>What the Duplicates page remembers between runs.</summary>
public sealed class DuplicatesSettings
{
    /// <summary>"sorted" = the sorted folders, "folder" = the one below.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "sorted";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("recursive")] public bool Recursive { get; set; } = true;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "content";
    [JsonPropertyName("extension")] public string Extension { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("min_mb")] public string MinMb { get; set; } = "";
    /// <summary>The folder "Check copies in folder" looks at; "" = the folder above.</summary>
    [JsonPropertyName("check_folder")] public string CheckFolder { get; set; } = "";
    [JsonPropertyName("check_subfolders")] public bool CheckSubfolders { get; set; } = true;
}

/// <summary>When automatic sorting and automatic cleaning run.</summary>
public sealed class ScheduleSettings
{
    /// <summary>"interval" = every scan_interval_minutes, "daily" or "weekly" = at the time below.</summary>
    [JsonPropertyName("sort")] public string Sort { get; set; } = "interval";
    /// <summary>"off", "daily" or "weekly": clean the items ticked on the Cleaner page at the time below.</summary>
    [JsonPropertyName("clean")] public string Clean { get; set; } = "off";
    /// <summary>"HH:mm", for daily and weekly runs.</summary>
    [JsonPropertyName("time")] public string Time { get; set; } = "09:00";
    /// <summary>Day of the week for weekly runs: 0 = Sunday ... 6 = Saturday.</summary>
    [JsonPropertyName("day")] public int Day { get; set; } = 1;
    /// <summary>"yyyy-MM-dd HH:mm" of the last scheduled run of each kind ("" = none yet).</summary>
    [JsonPropertyName("last_sort")] public string LastSort { get; set; } = "";
    [JsonPropertyName("last_clean")] public string LastClean { get; set; } = "";

    public const string Stamp = "yyyy-MM-dd HH:mm";

    /// <summary>The most recent moment a "daily" or "weekly" run was scheduled for, at or before now.</summary>
    public DateTime LastSlot(string mode, DateTime now)
    {
        var time = TimeSpan.TryParseExact(Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : TimeSpan.FromHours(9);
        var slot = now.Date + time;
        if (mode == "weekly")
        {
            slot = slot.AddDays(-(((int)slot.DayOfWeek - Day) % 7 + 7) % 7);
            return slot > now ? slot.AddDays(-7) : slot;
        }
        return slot > now ? slot.AddDays(-1) : slot;
    }

    /// <summary>
    /// True when a scheduled moment has passed since the last run. A PC that was off at
    /// the scheduled time catches up when it is next on.
    /// </summary>
    public bool IsDue(string mode, string lastRun, DateTime now) =>
        mode is "daily" or "weekly"
        && DateTime.TryParseExact(lastRun, Stamp, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.None, out var last)
        && last < LastSlot(mode, now);
}

/// <summary>The clipboard manager: what it keeps, for how many items, and the shortcut that opens its list.</summary>
public sealed class ClipboardSettings
{
    public const int MinLimit = 10, MaxLimit = 10000;

    /// <summary>Off until the user turns it on: everything copied is recorded while it is on.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>How many items are kept; pinned ones never count against it.</summary>
    [JsonPropertyName("limit")] public int Limit { get; set; } = 500;
    /// <summary>The keys that open the list, such as "Ctrl+Alt+V"; "" = none.</summary>
    [JsonPropertyName("shortcut")] public string Shortcut { get; set; } = "Ctrl+Alt+V";
    /// <summary>Keep the history on disk, so it survives a restart.</summary>
    [JsonPropertyName("keep_on_disk")] public bool KeepOnDisk { get; set; } = true;
    /// <summary>Record copied pictures too, not only text and files.</summary>
    [JsonPropertyName("pictures")] public bool Pictures { get; set; } = true;
    /// <summary>After picking an item, paste it into the window that was in front (else only copy it).</summary>
    [JsonPropertyName("paste_on_pick")] public bool PasteOnPick { get; set; } = true;
}

public sealed class CleanerSettings
{
    /// <summary>Keys ticked on the Cleaner page; null = the defaults.</summary>
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
}

/// <summary>Everything in config.json. The file format is the one Downloads Sorter used.</summary>
public sealed class Config
{
    /// <summary>Folders to sort; "" = the Downloads folder, detected automatically.</summary>
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [""];
    [JsonPropertyName("min_age_days")] public int MinAgeDays { get; set; } = 7;
    /// <summary>Sort in the background on a schedule.</summary>
    [JsonPropertyName("auto_sort")] public bool AutoSort { get; set; }
    /// <summary>How often automatic sorting checks; 0 also turns it off.</summary>
    [JsonPropertyName("scan_interval_minutes")] public int ScanIntervalMinutes { get; set; } = 60;
    [JsonPropertyName("categories")] public List<Category> Categories { get; set; } = DefaultCategories();
    /// <summary>Where every other file type goes; "" = leave them.</summary>
    [JsonPropertyName("other_folder")] public string OtherFolder { get; set; } = "Other";
    /// <summary>File names or patterns (*.torrent) that are never moved.</summary>
    [JsonPropertyName("ignore")] public List<string> Ignore { get; set; } = [];
    /// <summary>"yyyy-MM-dd HH:mm"; automatic runs wait until then.</summary>
    [JsonPropertyName("paused_until")] public string PausedUntil { get; set; } = "";
    /// <summary>"window" = report window, "notify" = tray notification only.</summary>
    [JsonPropertyName("after_run")] public string AfterRun { get; set; } = "window";
    [JsonPropertyName("duplicates")] public DuplicatesSettings Duplicates { get; set; } = new();
    [JsonPropertyName("cleaner")] public CleanerSettings Cleaner { get; set; } = new();
    [JsonPropertyName("schedule")] public ScheduleSettings Schedule { get; set; } = new();
    [JsonPropertyName("boost")] public BoostSettings Boost { get; set; } = new();
    [JsonPropertyName("clipboard")] public ClipboardSettings Clipboard { get; set; } = new();
    [JsonPropertyName("overlay")] public OverlaySettings Overlay { get; set; } = new();
    /// <summary>Keyboard shortcuts that work in any program: action -> keys ("Ctrl+Alt+B"). An action without one is not listed.</summary>
    [JsonPropertyName("hotkeys")] public Dictionary<string, string> Hotkeys { get; set; } = [];
    [JsonPropertyName("portal")] public PortalSettings Portal { get; set; } = new();
    /// <summary>Show a tray notification when the processor or the memory stays nearly full.</summary>
    [JsonPropertyName("usage_alerts")] public bool UsageAlerts { get; set; } = true;
    /// <summary>Show the drop zone, the small always-on-top tile that files can be dragged onto.</summary>
    [JsonPropertyName("drop_zone")] public bool DropZone { get; set; } = true;
    /// <summary>Show "Process with PC Butler" when a file or folder is right-clicked in File Explorer.</summary>
    [JsonPropertyName("explorer_menu")] public bool ExplorerMenu { get; set; } = true;
    /// <summary>The version whose What's new list was last shown; a different one means the program was updated.</summary>
    [JsonPropertyName("last_seen_version")] public string LastSeenVersion { get; set; } = "";
    /// <summary>The main window's last size and position, as "WIDTHxHEIGHT+LEFT+TOP".</summary>
    [JsonPropertyName("window_geometry")] public string WindowGeometry { get; set; } = "";
    /// <summary>true = install new versions by itself; false = notify and offer.</summary>
    [JsonPropertyName("auto_update")] public bool AutoUpdate { get; set; }
    /// <summary>"en", "ar", or "" to follow Windows.</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "";

    public static List<Category> DefaultCategories() =>
    [
        new() { Folder = "Documents", Extensions = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv"] },
        new() { Folder = "Pictures", Extensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".heic", ".tif", ".tiff", ".ico"] },
        new() { Folder = "Installers Archive", Extensions = [".exe", ".msi", ".msix", ".appx"], RecycleAfterDays = 90 },
        new() { Folder = "Archives", Extensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso"] },
        new() { Folder = "Videos", Extensions = [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv"] },
        new() { Folder = "Audio", Extensions = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus"] },
    ];

    /// <summary>The folders to sort, with "" resolved to the real Downloads folder.</summary>
    public List<string> WatchedFolders() =>
        Folders.Select(folder => folder.Length > 0 ? folder : ConfigStore.DetectDownloadsFolder()).ToList();

    /// <summary>An interval of 0 turns sorting off; daily and weekly schedules don't use the interval.</summary>
    public bool AutoSortOn => AutoSort && (Schedule.Sort != "interval" || ScanIntervalMinutes > 0);

    public bool IsPaused => string.CompareOrdinal(PausedUntil, DateTime.Now.ToString("yyyy-MM-dd HH:mm")) > 0;

    /// <summary>The interface language: the saved choice, or Arabic when Windows itself is in Arabic.</summary>
    public string EffectiveLanguage()
    {
        if (Language is "en" or "ar")
            return Language;
        const int LangArabic = 0x01; // primary language id, the low 10 bits of the UI language
        try
        {
            return (ConfigStore.GetUserDefaultUILanguage() & 0x3FF) == LangArabic ? "ar" : "en";
        }
        catch (EntryPointNotFoundException)
        {
            return "en";
        }
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Arabic and other non-ASCII text readable
    };

    [DllImport("kernel32.dll")]
    internal static extern ushort GetUserDefaultUILanguage();

    public static Config Load()
    {
        Config config;
        JsonObject? saved = null;
        try
        {
            var text = File.ReadAllText(AppInfo.ConfigFile);
            saved = JsonNode.Parse(text) as JsonObject;
            if (saved is not null && saved.ContainsKey("downloads_folder"))
            {
                // Setting from before several folders were supported.
                var old = saved["downloads_folder"]?.GetValue<string>() ?? "";
                saved.Remove("downloads_folder");
                if (!saved.ContainsKey("folders"))
                    saved["folders"] = new JsonArray(old);
            }
            config = saved?.Deserialize<Config>(Options) ?? new Config();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or InvalidOperationException or FormatException)
        {
            config = new Config();
            saved = null;
        }
        if (saved is null || !saved.ContainsKey("auto_sort"))
        {
            // Settings from before this option existed: automatic sorting was on
            // exactly when the program was set to start at sign-in.
            config.AutoSort = Autostart.IsEnabled();
        }
        config.Folders ??= [];
        if (config.Folders.Count == 0)
            config.Folders = [""];
        config.Categories ??= Config.DefaultCategories();
        config.Ignore ??= [];
        config.Duplicates ??= new DuplicatesSettings();
        config.Cleaner ??= new CleanerSettings();
        config.Schedule ??= new ScheduleSettings();
        config.Clipboard ??= new ClipboardSettings();
        config.Clipboard.Limit = Math.Clamp(config.Clipboard.Limit, ClipboardSettings.MinLimit, ClipboardSettings.MaxLimit);
        return config;
    }

    public static void Save(Config config)
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        File.WriteAllText(AppInfo.ConfigFile, JsonSerializer.Serialize(config, Options));
    }

    /// <summary>Look up a per-user Windows folder, honouring a relocated one.</summary>
    public static string ShellFolder(string valueName, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(valueName) is string value && value.Length > 0)
                return Environment.ExpandEnvironmentVariables(value);
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        return fallback;
    }

    public static string DetectDownloadsFolder() => ShellFolder(
        "{374DE290-123F-4565-9164-39C4925E467B}",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    /// <summary>"Pause for today": the timestamp of the coming midnight.</summary>
    public static string PauseUntilTomorrow() => DateTime.Now.AddDays(1).ToString("yyyy-MM-dd 00:00");
}

/// <summary>The per-user entry that starts the program at sign-in.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PCButler";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
