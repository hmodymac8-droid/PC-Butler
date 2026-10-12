// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;

namespace PCButler.Core;

/// <summary>
/// Interface text in the chosen language. The program is written in English; Tr.T looks
/// each string up in Translations/ar.json when the language is Arabic, and falls back to
/// the English text, so a missing translation is never an error.
///
/// Text with inserted values is written with {0}, {1}...: Tr.T("Moved {0} file(s).", count).
/// </summary>
public static class Tr
{
    private static Dictionary<string, string>? arabic;

    /// <summary>"en" or "ar".</summary>
    public static string Language { get; set; } = "en";

    public static bool IsArabic => Language == "ar";

    private static Dictionary<string, string> Arabic()
    {
        if (arabic is not null)
            return arabic;
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(Tr).Assembly.GetManifestResourceStream("PCButler.Core.ar.json");
            if (stream is not null)
            {
                using var document = JsonDocument.Parse(stream);
                foreach (var entry in document.RootElement.EnumerateObject())
                    loaded[entry.Name] = entry.Value.GetString() ?? entry.Name;
            }
        }
        catch (JsonException)
        {
        }
        return arabic = loaded;
    }

    /// <summary>English text seen with no Arabic translation (useful when adding strings).</summary>
    public static HashSet<string> Missing { get; } = [];

    public static string T(string english, params object[] arguments)
    {
        var text = english;
        if (IsArabic)
        {
            if (Arabic().TryGetValue(english, out var translated))
                text = translated;
            else
                lock (Missing)
                    Missing.Add(english);
        }
        if (arguments.Length == 0)
            return text;
        try
        {
            return string.Format(text, arguments);
        }
        catch (FormatException)
        {
            return text;
        }
    }
}
