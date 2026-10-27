// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;

namespace PCButler.Core.Palette;

/// <summary>
/// How often each command palette command was run, kept in palette-use.json in the data folder,
/// so the ones used most come first. A few bytes per command. Used from the window's thread only.
/// </summary>
public static class PaletteUsage
{
    private static Dictionary<string, int>? counts;

    private static string File_ => Path.Combine(AppInfo.DataDir, "palette-use.json");

    private static Dictionary<string, int> Loaded()
    {
        if (counts is not null)
            return counts;
        try
        {
            counts = File.Exists(File_) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(File_)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"Command palette use could not be read ({error.Message})");
        }
        counts ??= [];
        return counts;
    }

    /// <summary>How many times the command was run.</summary>
    public static int Count(string id) => Loaded().GetValueOrDefault(id);

    /// <summary>Count a run of the command, and save.</summary>
    public static void Add(string id)
    {
        var all = Loaded();
        all[id] = all.GetValueOrDefault(id) + 1;
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(File_, JsonSerializer.Serialize(all));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Command palette use could not be saved ({error.Message})");
        }
    }
}
