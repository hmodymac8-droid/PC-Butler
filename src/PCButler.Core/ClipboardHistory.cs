// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>One thing that was copied: text, a list of files, or a picture (kept as a PNG beside the history).</summary>
public sealed class ClipEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>"text", "files" or "image".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    /// <summary>For a picture: a fingerprint of its pixels, so copying it again doesn't add it twice.</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("time")] public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>Pinned items stay at the top and are never removed by the limit or by Clear.</summary>
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    [JsonIgnore] public string ImageFile => Path.Combine(ClipboardHistory.ImageFolder, Id + ".png");

    /// <summary>One line for lists.</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Kind == "files")
                return Files.Count == 1 ? Files[0] : $"{Path.GetFileName(Files[0])} (+{Files.Count - 1})";
            if (Kind == "image")
                return $"{Width} × {Height}";
            var line = string.Join(" ", Text[..Math.Min(Text.Length, 2000)].Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > 300 ? line[..300] + "…" : line;
        }
    }

    public bool SameContent(ClipEntry other) => Kind == other.Kind && Kind switch
    {
        "files" => Files.SequenceEqual(other.Files, StringComparer.OrdinalIgnoreCase),
        "image" => Hash == other.Hash,
        _ => Text == other.Text,
    };

    public bool Matches(string search) =>
        search.Length == 0
        || Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Files.Any(file => file.Contains(search, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>
/// The clipboard history, newest first. Lives in memory and, when the setting says so, in
/// clipboard\history.json under the data folder. Used from the window's thread only.
/// </summary>
public static class ClipboardHistory
{
    /// <summary>Texts longer than this are not recorded (a whole book copied by accident).</summary>
    public const int MaxText = 1_000_000;

    public static string Folder => Path.Combine(AppInfo.DataDir, "clipboard");
    public static string ImageFolder => Path.Combine(Folder, "images");
    private static string File_ => Path.Combine(Folder, "history.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static List<ClipEntry>? entries;
    public static bool KeepOnDisk { get; set; } = true;

    /// <summary>Raised after every change, for the page and the picker.</summary>
    public static event Action? Changed;

    public static List<ClipEntry> Entries => entries ??= Load();

    /// <summary>Pinned first, then newest first.</summary>
    public static List<ClipEntry> Ordered(string search = "") =>
        Entries.Where(entry => entry.Matches(search)).OrderByDescending(entry => entry.Pinned).ToList();

    private static List<ClipEntry> Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(File_), Options) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"The clipboard history could not be read: {error.Message}");
        }
        return [];
    }

    public static void Save()
    {
        try
        {
            if (!KeepOnDisk)
            {
                if (File.Exists(File_))
                    File.Delete(File_);
                return;
            }
            Directory.CreateDirectory(Folder);
            var temporary = File_ + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Entries, Options));
            File.Move(temporary, File_, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The clipboard history could not be saved: {error.Message}");
        }
    }

    /// <summary>
    /// Record a copy. The same content copied again moves to the top instead of being added
    /// twice. Then the oldest unpinned items beyond the limit are dropped.
    /// </summary>
    public static void Add(ClipEntry entry, int limit)
    {
        var existing = Entries.FirstOrDefault(known => known.SameContent(entry));
        if (existing is not null)
        {
            Entries.Remove(existing);
            existing.Time = entry.Time;
            if (entry.Kind == "image" && File.Exists(entry.ImageFile) && entry.Id != existing.Id)
                TryDelete(entry.ImageFile);
            entry = existing;
        }
        Entries.Insert(0, entry);
        Trim(limit);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Drop the oldest unpinned items beyond the limit.</summary>
    public static void Trim(int limit)
    {
        var unpinned = Entries.Where(entry => !entry.Pinned).Skip(Math.Max(limit, 1)).ToList();
        foreach (var entry in unpinned)
            Forget(entry);
    }

    public static void Remove(ClipEntry entry)
    {
        Forget(entry);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Remove everything except pinned items (or everything, when keepPinned is false).</summary>
    public static void Clear(bool keepPinned = true)
    {
        foreach (var entry in Entries.Where(entry => !keepPinned || !entry.Pinned).ToList())
            Forget(entry);
        Save();
        Changed?.Invoke();
    }

    public static void SetPinned(ClipEntry entry, bool pinned)
    {
        entry.Pinned = pinned;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Move an item to the top, as when it is pasted again.</summary>
    public static void Touch(ClipEntry entry)
    {
        Entries.Remove(entry);
        entry.Time = DateTime.Now;
        Entries.Insert(0, entry);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Settings changed: apply the limit and where the history lives.</summary>
    public static void Apply(ClipboardSettings settings)
    {
        KeepOnDisk = settings.KeepOnDisk;
        Trim(settings.Limit);
        Save();
        Changed?.Invoke();
    }

    private static void Forget(ClipEntry entry)
    {
        Entries.Remove(entry);
        if (entry.Kind == "image")
            TryDelete(entry.ImageFile);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
