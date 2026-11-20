// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Numerics;

namespace PCButler.Core.Search;

/// <summary>The defaults a search starts from; case: and diacritics: in the search override them.</summary>
public sealed record SearchOptions(bool MatchCase = false, bool MatchDiacritics = false);

/// <summary>
/// A compiled search: given the index, it marks the matching rows in a bit set (one bit per row).
/// Name terms are found by searching the folded name pool as one block; tests that need each row
/// (extension, wildcards, paths, exact case) run only on the rows the cheaper tests left.
/// </summary>
public abstract class Matcher
{
    /// <summary>The rows that match, marked in a bit set. within, when given, limits which rows need testing (the result may hold others).</summary>
    internal abstract long[] Run(Evaluation run, long[]? within);

    /// <summary>Rough cost, so the cheap tests of an AND run first.</summary>
    internal virtual int Cost => 1;

    /// <summary>The matching rows of an index; call with index.Gate held for reading.</summary>
    public long[] Evaluate(SearchIndex index, CancellationToken cancel = default)
    {
        var run = new Evaluation(index, cancel);
        return Bits.And(Run(run, null), run.Live);
    }
}

/// <summary>What one search run shares between its matchers.</summary>
internal sealed class Evaluation
{
    public SearchIndex Index { get; }
    public CancellationToken Cancel { get; }
    public long[] Live { get; }

    public Evaluation(SearchIndex index, CancellationToken cancel)
    {
        Index = index;
        Cancel = cancel;
        Live = index.LiveBits();
    }

    [ThreadStatic] private static char[]? buffer;

    /// <summary>A row's name folded the given way (the key pool already holds the full fold).</summary>
    public ReadOnlySpan<char> NameIn(int row, Fold fold)
    {
        if (fold == Fold.Full)
            return Index.Key(row);
        var name = Index.NameSpan(row);
        if (fold == Fold.None)
            return name;
        if (buffer is null || buffer.Length < name.Length)
            buffer = new char[Math.Max(512, name.Length)];
        return buffer.AsSpan(0, SearchText.Apply(name, buffer, fold, Index.Variants));
    }

    /// <summary>A root's path folded, without its closing backslash ("c:").</summary>
    public string RootIn(int row, Fold fold) => SearchText.Apply(Index.Name(row).TrimEnd('\\', '/'), fold, Index.Variants);
}

/// <summary>Bit sets: one bit per row.</summary>
internal static class Bits
{
    public static long[] And(long[] a, long[] b)
    {
        var result = new long[Math.Min(a.Length, b.Length)];
        for (var index = 0; index < result.Length; index++)
            result[index] = a[index] & b[index];
        return result;
    }

    public static long[] Or(long[] a, long[] b)
    {
        var result = new long[Math.Max(a.Length, b.Length)];
        for (var index = 0; index < result.Length; index++)
            result[index] = (index < a.Length ? a[index] : 0) | (index < b.Length ? b[index] : 0);
        return result;
    }

    public static long[] AndNot(long[] a, long[] b)
    {
        var result = new long[a.Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = a[index] & ~(index < b.Length ? b[index] : 0);
        return result;
    }

    public static bool Empty(long[] bits)
    {
        foreach (var word in bits)
            if (word != 0)
                return false;
        return true;
    }

    public static int Count(long[] bits)
    {
        var total = 0;
        foreach (var word in bits)
            total += BitOperations.PopCount((ulong)word);
        return total;
    }

    /// <summary>Test each row marked in within (every live row when null), in parallel; the rows that pass.</summary>
    public static long[] ForRows(Evaluation run, long[]? within, Func<int, bool> test)
    {
        var source = within ?? run.Live;
        var live = run.Live;
        var result = new long[live.Length];
        const int Block = 512;
        Parallel.For(0, (live.Length + Block - 1) / Block, new ParallelOptions { CancellationToken = run.Cancel }, block =>
        {
            var end = Math.Min(live.Length, (block + 1) * Block);
            for (var word = block * Block; word < end; word++)
            {
                var bits = (word < source.Length ? source[word] : 0) & live[word];
                long passed = 0;
                while (bits != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(bits);
                    if (test(word * 64 + bit))
                        passed |= 1L << bit;
                    bits &= bits - 1;
                }
                result[word] = passed;
            }
        });
        return result;
    }

    /// <summary>The rows marked, in row order.</summary>
    public static int[] Rows(long[] bits)
    {
        var rows = new int[Count(bits)];
        var next = 0;
        for (var word = 0; word < bits.Length; word++)
        {
            var value = bits[word];
            while (value != 0)
            {
                rows[next++] = word * 64 + BitOperations.TrailingZeroCount(value);
                value &= value - 1;
            }
        }
        return rows;
    }
}

/// <summary>Turns a parsed search into matchers. No Windows APIs: the phone app can use it too.</summary>
public static class QueryCompiler
{
    public static Matcher Compile(string text, SearchOptions? options = null) => Compile(QueryParser.Parse(text), options ?? new SearchOptions());

    public static Matcher Compile(QueryNode node, SearchOptions options) => node switch
    {
        AllNode all => new KindMatcher(all.Modifiers),
        AndNode and => new AndMatcher(and.Parts.Select(part => Compile(part, options)).ToList()),
        OrNode or => new OrMatcher(or.Parts.Select(part => Compile(part, options)).ToList()),
        NotNode not => new NotMatcher(Compile(not.Part, options)),
        ExtNode ext => new ExtMatcher(ext.Extensions.ToList(), ext.Modifiers),
        TermNode term => Term(term, options),
        _ => throw new ArgumentException("Unknown search node"),
    };

    /// <summary>What a term ignores: the defaults, overridden by its own modifiers.</summary>
    public static Fold FoldFor(QueryModifiers modifiers, SearchOptions options)
    {
        var matchCase = modifiers.HasFlag(QueryModifiers.Case) || (options.MatchCase && !modifiers.HasFlag(QueryModifiers.NoCase));
        var matchMarks = modifiers.HasFlag(QueryModifiers.Diacritics) || (options.MatchDiacritics && !modifiers.HasFlag(QueryModifiers.NoDiacritics));
        return (matchCase ? Fold.None : Fold.Case) | (matchMarks ? Fold.None : Fold.Marks);
    }

    private static Matcher Term(TermNode term, SearchOptions options)
    {
        var fold = FoldFor(term.Modifiers, options);
        var text = term.Text;
        var path = !term.Modifiers.HasFlag(QueryModifiers.NoPath) && (term.Modifiers.HasFlag(QueryModifiers.Path) || text.Contains('\\') || text.Contains('/'));
        if (path)
            text = text.Replace('/', '\\');
        var wild = text.Contains('*') || text.Contains('?');
        // The longest plain piece, folded fully, finds the candidates in the name pool.
        var piece = text.Split('*', '?').OrderByDescending(part => part.Length).First();
        if (path)
            return new PathMatcher(text, fold, wild, term.Modifiers);
        if (wild)
            return new WildcardMatcher(text, fold, piece, term.Modifiers);
        return new ContainsMatcher(text, fold, term.Modifiers);
    }
}

/// <summary>Every row, or every file, or every folder.</summary>
internal sealed class KindMatcher(QueryModifiers modifiers) : Matcher
{
    internal override int Cost => 0;

    internal override long[] Run(Evaluation run, long[]? within) => Narrow(run, within ?? run.Live, modifiers);

    /// <summary>Only the files, or only the folders, when the modifiers ask for that.</summary>
    public static long[] Narrow(Evaluation run, long[] rows, QueryModifiers modifiers)
    {
        if (modifiers.HasFlag(QueryModifiers.Files))
            return Bits.ForRows(run, rows, row => !run.Index.IsFolder(row));
        if (modifiers.HasFlag(QueryModifiers.Folders))
            return Bits.ForRows(run, rows, run.Index.IsFolder);
        return rows;
    }
}

internal sealed class AndMatcher(List<Matcher> parts) : Matcher
{
    private readonly List<Matcher> parts = parts.OrderBy(part => part.Cost).ToList();

    internal override int Cost => parts.Count == 0 ? 0 : parts.Min(part => part.Cost);

    internal override long[] Run(Evaluation run, long[]? within)
    {
        var rows = within;
        foreach (var part in parts)
        {
            var found = part.Run(run, rows);
            rows = rows is null ? found : Bits.And(rows, found);
            if (Bits.Empty(rows))
                break;
        }
        return rows ?? run.Live;
    }
}

internal sealed class OrMatcher(List<Matcher> parts) : Matcher
{
    internal override int Cost => parts.Count == 0 ? 0 : parts.Max(part => part.Cost);

    internal override long[] Run(Evaluation run, long[]? within)
    {
        var rows = new long[run.Live.Length];
        foreach (var part in parts)
            rows = Bits.Or(rows, part.Run(run, within));
        return rows;
    }
}

internal sealed class NotMatcher(Matcher part) : Matcher
{
    internal override int Cost => part.Cost + 10;

    internal override long[] Run(Evaluation run, long[]? within) => Bits.AndNot(within ?? run.Live, part.Run(run, within));
}

/// <summary>The name contains the text.</summary>
internal sealed class ContainsMatcher(string text, Fold fold, QueryModifiers modifiers) : Matcher
{
    internal override long[] Run(Evaluation run, long[]? within)
    {
        // Folded at run time: the Arabic letter variants are folded only when the index was built that way.
        var full = SearchText.Needle(text, Fold.Full, run.Index.Variants);
        if (full.Length == 0)
            return KindMatcher.Narrow(run, within ?? run.Live, modifiers);
        var rows = run.Index.Contains(full, run.Cancel);
        if (within is not null)
            rows = Bits.And(rows, within);
        if (fold != Fold.Full)
        {
            // The pool gives every row that matches loosely; the exact test keeps those that match as asked.
            var exact = SearchText.Needle(text, fold, run.Index.Variants);
            rows = Bits.ForRows(run, rows, row => run.NameIn(row, fold).IndexOf(exact, StringComparison.Ordinal) >= 0);
        }
        return KindMatcher.Narrow(run, rows, modifiers);
    }
}

/// <summary>The whole name matches a pattern with * and ?.</summary>
internal sealed class WildcardMatcher(string text, Fold fold, string longestPiece, QueryModifiers modifiers) : Matcher
{
    internal override int Cost => longestPiece.Length > 0 ? 2 : 5;

    internal override long[] Run(Evaluation run, long[]? within)
    {
        var pattern = SearchText.Apply(text, fold, run.Index.Variants);
        var piece = SearchText.Needle(longestPiece, Fold.Full, run.Index.Variants);
        var rows = within;
        if (piece.Length > 0)
        {
            var found = run.Index.Contains(piece, run.Cancel);
            rows = rows is null ? found : Bits.And(rows, found);
        }
        rows = Bits.ForRows(run, rows, row => SearchText.WildcardMatch(run.NameIn(row, fold), pattern));
        return KindMatcher.Narrow(run, rows, modifiers);
    }
}

/// <summary>
/// The full path contains the text (or, with * or ?, matches it whole). A folder's state is
/// worked out once per search and shared by everything in it.
/// </summary>
internal sealed class PathMatcher(string text, Fold fold, bool wild, QueryModifiers modifiers) : Matcher
{
    internal override int Cost => 6;

    internal override long[] Run(Evaluation run, long[]? within)
    {
        var needle = wild ? SearchText.Apply(text, fold, run.Index.Variants) : SearchText.Needle(text, fold, run.Index.Variants);
        if (needle.Length == 0)
            return KindMatcher.Narrow(run, within ?? run.Live, modifiers);
        var index = run.Index;
        long[] rows;
        if (wild)
        {
            var folders = new ConcurrentDictionary<int, string>();
            string PathOf(int folder) => folders.GetOrAdd(folder, row => index.Parent(row) < 0
                ? run.RootIn(row, fold)
                : string.Concat(PathOf(index.Parent(row)), "\\", run.NameIn(row, fold)));
            rows = Bits.ForRows(run, within, row => index.Parent(row) < 0
                ? SearchText.WildcardMatch(run.RootIn(row, fold), needle)
                : SearchText.WildcardMatch(string.Concat(PathOf(index.Parent(row)), "\\", run.NameIn(row, fold)), needle));
        }
        else if (Candidates(run, needle) is { } found)
            rows = found;
        else
        {
            // Per folder: does its path contain the text, and its last few characters (a match can run on into a name).
            var keep = needle.Length - 1;
            var states = new ConcurrentDictionary<int, (bool Contains, string Tail)>();
            string Tail(string text) => text.Length <= keep ? text : text[^keep..];
            (bool Contains, string Tail) State(int folder)
            {
                if (states.TryGetValue(folder, out var known))
                    return known;
                (bool, string) state;
                if (index.Parent(folder) < 0)
                {
                    var root = run.RootIn(folder, fold);
                    state = (root.Contains(needle, StringComparison.Ordinal), Tail(root));
                }
                else
                {
                    var (inside, tail) = State(index.Parent(folder));
                    var joined = string.Concat(tail, "\\", run.NameIn(folder, fold));
                    state = (inside || joined.Contains(needle, StringComparison.Ordinal), Tail(joined));
                }
                return states[folder] = state;
            }
            rows = Bits.ForRows(run, within, row =>
            {
                if (index.Parent(row) < 0)
                    return State(row).Contains;
                var (inside, tail) = State(index.Parent(row));
                return inside || string.Concat(tail, "\\", run.NameIn(row, fold)).Contains(needle, StringComparison.Ordinal);
            });
        }
        return KindMatcher.Narrow(run, rows, modifiers);
    }

    /// <summary>
    /// The quick way, without a path per row: a match of the text ends in some name, and that name
    /// holds the text's last piece (after its last \) at its start, or all of it when there is no \.
    /// The names holding that piece come from the name pool; those whose path contains the text
    /// match, and so does everything inside them. Null when the text has no piece to look for.
    /// </summary>
    private long[]? Candidates(Evaluation run, string needle)
    {
        var index = run.Index;
        var cut = needle.LastIndexOf('\\');
        var trailing = cut == needle.Length - 1;
        var before = trailing ? needle[..cut].LastIndexOf('\\') : cut;
        var piece = trailing ? needle[(before + 1)..cut] : needle[(cut + 1)..];
        var full = SearchText.Needle(piece, Fold.Full, index.Variants);
        if (full.Length == 0)
            return null;
        var rows = new long[run.Live.Length];
        void Mark(int top, bool withTop)
        {
            var pending = new Stack<int>();
            if (withTop)
                rows[top >> 6] |= 1L << (top & 63);
            pending.Push(top);
            while (pending.Count > 0)
            {
                foreach (var child in index.Children(pending.Pop()))
                {
                    if ((rows[child >> 6] & (1L << (child & 63))) != 0)
                        continue; // already marked, with what is under it
                    rows[child >> 6] |= 1L << (child & 63);
                    pending.Push(child);
                }
            }
        }
        foreach (var root in index.Roots)
            if ((run.RootIn(root, fold) + "\\").Contains(needle, StringComparison.Ordinal))
                Mark(root, withTop: true);
        foreach (var row in Bits.Rows(index.Contains(full, run.Cancel)))
        {
            if ((rows[row >> 6] & (1L << (row & 63))) != 0)
                continue;
            var name = run.NameIn(row, fold);
            var fits = cut < 0 ? name.Contains(piece, StringComparison.Ordinal)
                : trailing ? name.EndsWith(piece, StringComparison.Ordinal)
                : name.StartsWith(piece, StringComparison.Ordinal);
            if (!fits)
                continue;
            var path = SearchText.Apply(index.FullPath(row), fold, index.Variants) + (trailing ? "\\" : "");
            if (path.Contains(needle, StringComparison.Ordinal))
                Mark(row, withTop: !trailing);
        }
        return rows;
    }
}

/// <summary>Files with one of the extensions ("" = files without one).</summary>
internal sealed class ExtMatcher(List<string> names, QueryModifiers modifiers) : Matcher
{
    internal override int Cost => 2;

    internal override long[] Run(Evaluation run, long[]? within)
    {
        var index = run.Index;
        var rows = within;
        var extensions = names.Select(name => SearchText.Needle(name, Fold.Full, index.Variants)).ToList();
        // ".jpg" in the name pool narrows it down first, unless one of them is "no extension".
        if (extensions.Count > 0 && extensions.All(ext => ext.Length > 0))
        {
            var found = new long[run.Live.Length];
            foreach (var ext in extensions)
                found = Bits.Or(found, index.Contains("." + ext, run.Cancel));
            rows = rows is null ? found : Bits.And(rows, found);
        }
        rows = Bits.ForRows(run, rows, row =>
        {
            if (index.IsFolder(row))
                return false;
            var key = index.Key(row);
            var dot = key.LastIndexOf('.');
            ReadOnlySpan<char> ext = dot < 0 ? default : key[(dot + 1)..];
            foreach (var wanted in extensions)
                if (ext.SequenceEqual(wanted))
                    return true;
            return false;
        });
        return KindMatcher.Narrow(run, rows, modifiers);
    }
}
