// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>Words the user gave it ("work", "address"), to find it again: searching for one finds it, and "#work" finds only items tagged so.</summary>
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    /// <summary>
    /// A locked item: its content (text, files or picture), encrypted with a password of its
    /// own. Nothing else of the content is kept, on disk or in the list, until it is unlocked.
    /// </summary>
    [JsonPropertyName("sealed")] public string Sealed { get; set; } = "";

    [JsonIgnore] public string ImageFile => Path.Combine(ClipboardHistory.ImageFolder, Id + ".png");
    /// <summary>True for an item with a password.</summary>
    [JsonIgnore] public bool Locked => Sealed.Length > 0;
    /// <summary>A locked item unlocked for now: its content is in memory (never written out) until it is locked again or PC Butler closes.</summary>
    [JsonIgnore] public bool Open { get; internal set; }
    /// <summary>True when the content can't be seen or used without the password.</summary>
    [JsonIgnore] public bool Hidden => Locked && !Open;
    /// <summary>An unlocked picture of a locked item: its PNG, in memory only.</summary>
    [JsonIgnore] public byte[]? Picture { get; internal set; }

    /// <summary>One line for lists.</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Hidden)
                return Tr.T("Locked");
            if (Kind == "files")
                return Files.Count == 1 ? Files[0] : $"{Path.GetFileName(Files[0])} (+{Files.Count - 1})";
            if (Kind == "image")
                return $"{Width} × {Height}";
            var line = string.Join(" ", Text[..Math.Min(Text.Length, 2000)].Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > 300 ? line[..300] + "…" : line;
        }
    }

    public bool SameContent(ClipEntry other) => !Hidden && !other.Hidden && Kind == other.Kind && Kind switch
    {
        "files" => Files.SequenceEqual(other.Files, StringComparer.OrdinalIgnoreCase),
        "image" => Hash == other.Hash,
        _ => Text == other.Text,
    };

    public bool Matches(string search)
    {
        if (search.Length == 0)
            return true;
        // "#work": only what is tagged "work".
        if (search.StartsWith('#'))
            return Tags.Any(tag => tag.Equals(search[1..].Trim(), StringComparison.CurrentCultureIgnoreCase));
        // A locked item gives nothing away to a search but its tags.
        if (Hidden)
            return Tags.Any(tag => tag.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        return Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)
               || Files.Any(file => file.Contains(search, StringComparison.CurrentCultureIgnoreCase))
               || Tags.Any(tag => tag.Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>A tag as typed, made tidy: one line, no "#", not too long. "" when nothing is left.</summary>
    public static string CleanTag(string tag)
    {
        tag = string.Join(" ", tag.Replace("#", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tag.Length > 30 ? tag[..30] : tag;
    }
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
            // Kept aside, not written over: the next save would otherwise lose everything that was in it.
            try
            {
                if (error is JsonException)
                    File.Move(File_, Path.Combine(Folder, "history.broken.json"), overwrite: true);
            }
            catch (Exception moving) when (moving is IOException or UnauthorizedAccessException)
            {
            }
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
            // What a locked item holds while it is unlocked stays in memory: only its sealed copy is written.
            var written = Entries.Select(entry => entry.Locked
                ? new ClipEntry
                {
                    Id = entry.Id, Kind = entry.Kind, Width = entry.Width, Height = entry.Height, Time = entry.Time, Pinned = entry.Pinned, Tags = entry.Tags,
                    Sealed = entry.Sealed,
                }
                : entry).ToList();
            File.WriteAllText(temporary, JsonSerializer.Serialize(written, Options));
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

    /// <summary>Give an item a tag, or take it away.</summary>
    public static void SetTag(ClipEntry entry, string tag, bool on)
    {
        tag = ClipEntry.CleanTag(tag);
        if (tag.Length == 0)
            return;
        entry.Tags.RemoveAll(old => old.Equals(tag, StringComparison.CurrentCultureIgnoreCase));
        if (on)
            entry.Tags.Add(tag);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Every tag in use, most used first: offered when tagging another item.</summary>
    public static List<string> AllTags() =>
        Entries.SelectMany(entry => entry.Tags).GroupBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
               .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
               .Select(group => group.First()).ToList();

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

    // --- items with a password ---

    private const int Rounds = 210_000;

    /// <summary>What a locked item holds, as it is sealed.</summary>
    private sealed record Content(
        [property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("files")] List<string> Files,
        [property: JsonPropertyName("picture")] string Picture);

    /// <summary>
    /// Put a password on an item. Its content is encrypted (AES-256-GCM, with a key stretched
    /// from the password) and taken out of the list and off the disk; a picture's file is
    /// removed. False when it can't be done (the picture's file is gone, or the password is empty).
    /// </summary>
    public static bool Lock(ClipEntry entry, string password)
    {
        if (password.Length == 0 || entry.Locked)
            return false;
        byte[] picture = [];
        if (entry.Kind == "image")
        {
            try
            {
                picture = File.ReadAllBytes(entry.ImageFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        var content = JsonSerializer.SerializeToUtf8Bytes(new Content(entry.Text, entry.Files, Convert.ToBase64String(picture)));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var sealedBytes = new byte[content.Length];
        using (var aes = new AesGcm(Key(password, salt, Rounds), tag.Length))
            aes.Encrypt(nonce, content, sealedBytes, tag);
        CryptographicOperations.ZeroMemory(content);
        entry.Sealed = string.Join('$', "aesgcm", Rounds, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(sealedBytes),
                                   Convert.ToBase64String(tag));
        (entry.Text, entry.Files, entry.Hash, entry.Picture, entry.Open) = ("", [], "", null, false);
        if (entry.Kind == "image")
            TryDelete(entry.ImageFile);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Unlock a locked item with its password, until Relock (or until PC Butler closes): it can
    /// then be pasted again and again without the password. False for a wrong password.
    /// </summary>
    public static bool Unlock(ClipEntry entry, string password)
    {
        if (!entry.Locked)
            return true;
        if (Unseal(entry.Sealed, password) is not { } content)
            return false;
        (entry.Text, entry.Files, entry.Open) = (content.Text, content.Files, true);
        entry.Picture = content.Picture.Length > 0 ? Convert.FromBase64String(content.Picture) : null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Lock an unlocked item again: its content leaves memory, and the password is asked again.</summary>
    public static void Relock(ClipEntry entry)
    {
        if (!entry.Locked)
            return;
        (entry.Text, entry.Files, entry.Picture, entry.Open) = ("", [], null, false);
        Changed?.Invoke();
    }

    /// <summary>Lock every unlocked item again (when PC Butler is locked away, say).</summary>
    public static void RelockAll()
    {
        foreach (var entry in Entries.Where(entry => entry.Open))
            (entry.Text, entry.Files, entry.Picture, entry.Open) = ("", [], null, false);
        Changed?.Invoke();
    }

    /// <summary>Take the password off for good: the item is an ordinary one again. False for a wrong password.</summary>
    public static bool RemoveLock(ClipEntry entry, string password)
    {
        if (!Unlock(entry, password))
            return false;
        if (entry.Kind == "image" && entry.Picture is { } picture)
        {
            try
            {
                Directory.CreateDirectory(ImageFolder);
                File.WriteAllBytes(entry.ImageFile, picture);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Relock(entry);
                return false;
            }
        }
        (entry.Sealed, entry.Picture, entry.Open) = ("", null, false);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The content of a locked item, for one paste, without unlocking it. Null for a wrong password.</summary>
    public static ClipEntry? Peek(ClipEntry entry, string password)
    {
        if (Unseal(entry.Sealed, password) is not { } content)
            return null;
        return new ClipEntry
        {
            Id = entry.Id, Kind = entry.Kind, Text = content.Text, Files = content.Files, Width = entry.Width, Height = entry.Height,
            Picture = content.Picture.Length > 0 ? Convert.FromBase64String(content.Picture) : null,
        };
    }

    private static byte[] Key(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    private static Content? Unseal(string sealedText, string password)
    {
        var parts = sealedText.Split('$');
        if (parts is not ["aesgcm", var roundsText, var salt, var nonce, var data, var tag] || !int.TryParse(roundsText, out var rounds) || rounds is < 10_000 or > 10_000_000)
            return null;
        try
        {
            var sealedBytes = Convert.FromBase64String(data);
            var plain = new byte[sealedBytes.Length];
            using (var aes = new AesGcm(Key(password, Convert.FromBase64String(salt), rounds), 16))
                aes.Decrypt(Convert.FromBase64String(nonce), sealedBytes, Convert.FromBase64String(tag), plain);
            var content = JsonSerializer.Deserialize<Content>(plain);
            CryptographicOperations.ZeroMemory(plain);
            return content;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return null; // a wrong password fails the check that GCM makes
        }
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
