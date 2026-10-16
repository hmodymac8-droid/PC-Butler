// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// What PC Butler did on the phone, newest first, for the History tool: sorting, deleting,
/// cleaning, a benchmark, text sent to a PC… Only PC Butler's own actions, kept in the app's
/// storage (the last 300).
/// </summary>
public static class ActivityLog
{
    /// <summary>One thing done. Kind: sorter, duplicates, storage, cleaner, courier, apps, benchmark, clipboard.</summary>
    public sealed record Entry(string Kind, string Text, string Detail, DateTime Time);

    private const int Most = 300;
    private static readonly object Gate = new();
    private static string LogFile => Path.Combine(PCButler.Core.AppInfo.DataDir, "activity.json");

    /// <summary>The kinds that are about files; the others are tools.</summary>
    public static readonly string[] FileKinds = ["sorter", "duplicates", "storage", "cleaner", "courier"];

    /// <summary>Text: in English, as a key for Tr.T, so it shows in the language of the moment.</summary>
    public static void Record(string kind, string text, string detail = "")
    {
        lock (Gate)
        {
            var list = Read();
            list.Insert(0, new Entry(kind, text, detail, DateTime.Now));
            if (list.Count > Most)
                list.RemoveRange(Most, list.Count - Most);
            Write(list);
        }
    }

    public static List<Entry> Items()
    {
        lock (Gate)
            return Read();
    }

    public static void Clear()
    {
        lock (Gate)
            Write([]);
    }

    private static List<Entry> Read()
    {
        try
        {
            return File.Exists(LogFile) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(LogFile)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }

    private static void Write(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(PCButler.Core.AppInfo.DataDir);
            File.WriteAllText(LogFile, JsonSerializer.Serialize(list));
        }
        catch (IOException error)
        {
            Log.Warning($"Activity log: {error.Message}");
        }
    }
}
