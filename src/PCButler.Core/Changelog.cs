// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core;

/// <summary>What one version brought.</summary>
public sealed record ChangelogEntry(string Version, List<string> Changes);

/// <summary>
/// The list of what each version brought, read from CHANGELOG.md (CHANGELOG.ar.md in
/// Arabic), which is built into the program. Shown once after an update and from Help &
/// about. A new version needs a new "## x.y.z" section at the top of both files.
/// </summary>
public static class Changelog
{
    /// <summary>Every version, newest first.</summary>
    public static List<ChangelogEntry> Entries(bool arabic)
    {
        var entries = new List<ChangelogEntry>();
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(arabic ? "PCButler.Core.CHANGELOG.ar.md" : "PCButler.Core.CHANGELOG.md");
        if (stream is null)
            return entries;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                entries.Add(new ChangelogEntry(line[3..].Trim(), []));
            else if (line.StartsWith("- ", StringComparison.Ordinal) && entries.Count > 0)
                entries[^1].Changes.Add(line[2..].Trim());
        }
        return entries;
    }

    /// <summary>
    /// True when the program has been updated since the window was last opened, so the
    /// list should be shown. lastSeen is the version saved in the settings ("" before 1.8,
    /// and on a first start, when there is nothing to compare with and nothing to announce).
    /// </summary>
    /// <summary>The most a change's short form runs to: about two or three lines of the window.</summary>
    public const int HeadlineLength = 240;

    /// <summary>
    /// A change in short: its first sentences, as many as fit in about two or three lines
    /// (a first sentence longer than that is cut at a word and ends with "…").
    /// </summary>
    public static string Headline(string change)
    {
        var sentences = System.Text.RegularExpressions.Regex.Matches(change, @".+?[.!?](?=\s|$)|.+$");
        var text = "";
        foreach (System.Text.RegularExpressions.Match sentence in sentences)
        {
            var next = (text + sentence.Value).TrimStart();
            if (text.Length > 0 && next.Length > HeadlineLength)
                break;
            text = next;
        }
        if (text.Length <= HeadlineLength + 40)
            return text;
        var cut = text.LastIndexOf(' ', HeadlineLength);
        return text[..(cut > 0 ? cut : HeadlineLength)].TrimEnd(',', ';', ':') + "…";
    }

    public static bool ShouldShow(string lastSeen, bool firstStart) =>
        !firstStart && lastSeen != AppInfo.Version;
}
