// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text;

namespace PCButler.Core.Palette;

/// <summary>
/// How well a typed text matches a command's name: the whole name, its start, the start of a
/// word in it, anywhere in it, or its letters in order ("cln" finds Cleaner). Arabic is folded
/// first, so أ إ آ match ا, ة matches ه, ى matches ي, and the short vowel marks are ignored.
/// No allocation per comparison beyond the folded texts, which callers keep.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Lower case, Arabic letters folded, marks dropped, runs of spaces made one.</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            switch (c)
            {
                case 'أ' or 'إ' or 'آ' or 'ٱ':
                    c = 'ا';
                    break;
                case 'ة':
                    c = 'ه';
                    break;
                case 'ى':
                    c = 'ي';
                    break;
                case >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ':
                    continue; // vowel marks and the stretching line
            }
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length == 0 || builder[^1] == ' ')
                    continue;
                c = ' ';
            }
            builder.Append(c);
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>A score for a folded query against a folded text: higher is better, -1 is no match.</summary>
    public static int Score(string query, string text)
    {
        if (query.Length == 0)
            return 0;
        if (query.Length > text.Length)
            return -1;
        if (text == query)
            return 1000;
        if (text.StartsWith(query, StringComparison.Ordinal))
            return 900 - Math.Min(100, text.Length - query.Length);
        var first = text.IndexOf(query, StringComparison.Ordinal);
        var found = first;
        while (found > 0 && !IsWordStart(text, found))
            found = text.IndexOf(query, found + 1, StringComparison.Ordinal);
        if (found > 0)
            return 800 - Math.Min(100, found);
        if (first > 0)
            return 600 - Math.Min(100, first);
        // The letters in order: each at the start of a word, or straight after the last, counts more.
        var score = 300;
        var position = 0;
        var last = -2;
        foreach (var c in query)
        {
            if (c == ' ')
                continue;
            var hit = text.IndexOf(c, position);
            if (hit < 0)
                return -1;
            if (hit == last + 1)
                score += 8;
            else if (IsWordStart(text, hit))
                score += 10;
            else
                score -= Math.Min(10, hit - position);
            last = hit;
            position = hit + 1;
        }
        return Math.Max(1, score);
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || text[index - 1] is ' ' or '-' or '(' or '&' or ':' or '/';
}
