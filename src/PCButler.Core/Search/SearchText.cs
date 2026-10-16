// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>What a comparison of names ignores.</summary>
[Flags]
public enum Fold
{
    /// <summary>The name exactly as written.</summary>
    None = 0,
    /// <summary>Upper and lower case are the same.</summary>
    Case = 1,
    /// <summary>
    /// Accents and other marks are ignored: é is e, and in Arabic the short vowels (tashkeel) and the
    /// tatweel are left out, so محمد also finds مُحَمَّد. With the Arabic letter variants on, أ إ آ ٱ are ا,
    /// ى is ي and ة is ه.
    /// </summary>
    Marks = 2,
    Full = Case | Marks,
}

/// <summary>
/// Folding names for search, one character at a time through a table, so a few million names
/// fold in a moment. Plain .NET only: the phone app can use it too.
/// </summary>
public static class SearchText
{
    private static readonly char[]?[] Tables = new char[]?[8];
    private static readonly object Gate = new();

    /// <summary>
    /// The table for a fold: each character to what it folds to, '\0' for one that is left out.
    /// variants adds the Arabic letter variants (only with Marks).
    /// </summary>
    public static char[] Table(Fold fold, bool variants)
    {
        var slot = (int)fold | (variants && fold.HasFlag(Fold.Marks) ? 4 : 0);
        if (Tables[slot] is { } ready)
            return ready;
        lock (Gate)
        {
            return Tables[slot] ??= Build(fold, variants && fold.HasFlag(Fold.Marks));
        }
    }

    private static char[] Build(Fold fold, bool variants)
    {
        var table = new char[65536];
        for (var code = 0; code < table.Length; code++)
        {
            var c = (char)code;
            if (char.IsSurrogate(c))
            {
                table[code] = c;
                continue;
            }
            if (fold.HasFlag(Fold.Marks))
            {
                if (c == 'ـ' || IsMark(c))
                {
                    table[code] = '\0'; // tatweel, tashkeel, combining accents
                    continue;
                }
                c = Base(c);
                if (variants)
                {
                    c = c switch
                    {
                        'ى' => 'ي', // ى -> ي
                        'ة' => 'ه', // ة -> ه
                        'ٱ' => 'ا', // ٱ -> ا (أ إ آ are taken apart into ا and a mark above)
                        _ => c,
                    };
                }
            }
            if (fold.HasFlag(Fold.Case))
                c = char.ToLowerInvariant(c);
            table[code] = c;
        }
        return table;
    }

    private static bool IsMark(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>A letter with its accents taken off (é to e, أ to ا); letters made of other letters (Korean syllables) stay as they are.</summary>
    private static char Base(char c)
    {
        if (c < 'À')
            return c;
        string parts;
        try
        {
            var single = c.ToString();
            if (single.IsNormalized(NormalizationForm.FormD))
                return c;
            parts = single.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return c;
        }
        if (parts.Length < 2 || IsMark(parts[0]))
            return c;
        for (var index = 1; index < parts.Length; index++)
            if (!IsMark(parts[index]))
                return c;
        return parts[0];
    }

    /// <summary>Fold text into a buffer at least as long as it. Returns how many characters were written.</summary>
    public static int Apply(ReadOnlySpan<char> text, Span<char> into, Fold fold, bool variants)
    {
        if (fold == Fold.None)
        {
            text.CopyTo(into);
            return text.Length;
        }
        var table = Table(fold, variants);
        var length = 0;
        foreach (var c in text)
        {
            var folded = table[c];
            if (folded != '\0')
                into[length++] = folded;
        }
        return length;
    }

    public static string Apply(string text, Fold fold, bool variants)
    {
        if (text.Length <= 256)
        {
            Span<char> buffer = stackalloc char[text.Length];
            return new string(buffer[..Apply(text, buffer, fold, variants)]);
        }
        var big = new char[text.Length];
        return new string(big, 0, Apply(text, big, fold, variants));
    }

    /// <summary>
    /// Text from a search box made safe for the folded name pool: the characters the pool uses
    /// for its own bookkeeping (nul, U+0001 and the private-use block) never match a name.
    /// </summary>
    public static string Needle(string text, Fold fold, bool variants)
    {
        var folded = Apply(text, fold, variants);
        if (!folded.AsSpan().ContainsAny(Reserved))
            return folded;
        var clean = new StringBuilder(folded.Length);
        foreach (var c in folded)
            if (!Reserved.Contains(c))
                clean.Append(c);
        return clean.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> Reserved =
        System.Buffers.SearchValues.Create("\0\u0001" + string.Concat(Enumerable.Range(0xE000, 0xF900 - 0xE000).Select(code => (char)code)));

    /// <summary>
    /// True when text matches a pattern with * (any run of characters) and ? (exactly one), the
    /// whole text. Both must already be folded the same way.
    /// </summary>
    public static bool WildcardMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0, starAt = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starAt = p++;
                resume = t;
            }
            else if (starAt >= 0)
            {
                p = starAt + 1;
                t = ++resume;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
