// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;

namespace PCButler.Core.Search;

/// <summary>
/// The searches typed before, newest first, kept in search-history.json in the data folder so
/// they are still there after PC Butler restarts. Used from the window's thread only.
/// </summary>
public static class SearchHistory
{
    public const int MaxEntries = 30;
    private static List<string>? entries;

    private static string File_ => Path.Combine(AppInfo.DataDir, "search-history.json");

    /// <summary>The searches, newest first.</summary>
    public static IReadOnlyList<string> Entries => Loaded();

    /// <summary>The list changed (added to, an entry taken out, or cleared).</summary>
    public static event Action? Changed;

    private static List<string> Loaded()
    {
        if (entries is not null)
            return entries;
        try
        {
            entries = File.Exists(File_) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(File_)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"Search history could not be read ({error.Message})");
        }
        entries ??= [];
        return entries;
    }

    /// <summary>
    /// Remember a search. One typed a little at a time keeps only its final form: a search that
    /// grows or shrinks the newest one (pdf, then pdf report) takes its place.
    /// </summary>
    public static void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return;
        var list = Loaded();
        if (list.Count > 0 && list[0] == query)
            return;
        if (list.Count > 0 && (query.StartsWith(list[0], StringComparison.OrdinalIgnoreCase) || list[0].StartsWith(query, StringComparison.OrdinalIgnoreCase))
            && DateTime.UtcNow - lastAdded < TimeSpan.FromSeconds(30))
            list.RemoveAt(0);
        list.RemoveAll(entry => entry.Equals(query, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, query);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        lastAdded = DateTime.UtcNow;
        Save();
    }

    private static DateTime lastAdded = DateTime.MinValue;

    public static void Remove(string query)
    {
        if (Loaded().Remove(query))
            Save();
    }

    public static void Clear()
    {
        Loaded().Clear();
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var temporary = File_ + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, File_, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Search history could not be saved ({error.Message})");
        }
        Changed?.Invoke();
    }
}
