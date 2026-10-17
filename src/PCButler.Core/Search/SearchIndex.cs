// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>
/// The search index: one row per file or folder, held as plain arrays (no object per file), so
/// millions of names fit in memory and can be searched in a few milliseconds.
///
/// Names live in two shared character pools. The name pool keeps each name as it is written,
/// for showing it; the key pool keeps it folded (lower case, accents and Arabic tashkeel left
/// out), which is what searching reads, as one long block. A name already in its folded form
/// (most are: "setup.exe") is kept only once, in the key pool.
///
/// Key pool layout, so a hit anywhere in the block leads back to its row: the pool starts with
/// '\0', and each entry is two characters holding the row number (from the private-use block,
/// which a search never contains), the folded name, then '\0'.
///
/// Every row knows its parent; folders also chain their children (first child, next sibling), so
/// a path can be looked up and a moved folder takes its contents with it. Paths are worked out
/// from the parent chain; folder paths are cached, file paths never are.
///
/// Readers (searches, the window drawing rows) take Gate for reading, changes take it for writing.
/// </summary>
public sealed class SearchIndex
{
    public const byte Folder = 1, Hidden = 2, SystemFile = 4, Removed = 8, NameInKeys = 16, Root = 32;
    private const byte Kept = Folder | Hidden | SystemFile | Root;
    private const int RowBase = 0xE000, RowSpan = 6400;
    /// <summary>Two private-use characters hold a row number, so this is the most rows an index can hold.</summary>
    public const int MaxRows = RowSpan * RowSpan;
    /// <summary>Date modified is kept to the second, counted from here, in four bytes.</summary>
    private static readonly long Epoch = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>The name and path order worked out by BuildOrder, swapped in as one piece so a search never sees half of it.</summary>
    public sealed class Ordering(int[] nameRank, int[] pathRank, int[] nameOrder)
    {
        /// <summary>Each row's place in name order (-1 = removed or renamed since; rows past the end were added since).</summary>
        public int[] NameRank { get; } = nameRank;
        /// <summary>Each row's place in path order.</summary>
        public int[] PathRank { get; } = pathRank;
        /// <summary>The rows in name order (check Has: some may have been renamed since).</summary>
        public int[] NameOrder { get; } = nameOrder;

        public bool Has(int row) => row < NameRank.Length && NameRank[row] >= 0;
    }

    public ReaderWriterLockSlim Gate { get; } = new(LockRecursionPolicy.SupportsRecursion);
    public bool WithSize { get; }
    public bool WithModified { get; }
    /// <summary>Arabic letter variants are folded together in the keys.</summary>
    public bool Variants { get; }

    private int count, dead;
    private int[] parent, firstChild, nextSibling, nameStart, keyStart;
    private byte[] flags;
    private long[]? size;
    private uint[]? modified;
    private char[] names = new char[1 << 16];
    private int namesUsed;
    private char[] keys = new char[1 << 16];
    private int keysUsed = 1; // keys[0] = '\0'
    private long deadChars;
    private readonly List<int> roots = [];
    private readonly ConcurrentDictionary<int, string> folderPaths = new();
    private int generation;
    private Ordering? order;
    private int unranked; // rows added or renamed since the order was built

    public SearchIndex(bool withSize = true, bool withModified = true, bool variants = true, int capacity = 1024)
    {
        (WithSize, WithModified, Variants) = (withSize, withModified, variants);
        capacity = Math.Max(capacity, 16);
        parent = new int[capacity];
        firstChild = new int[capacity];
        nextSibling = new int[capacity];
        nameStart = new int[capacity];
        keyStart = new int[capacity];
        flags = new byte[capacity];
        size = withSize ? new long[capacity] : null;
        modified = withModified ? new uint[capacity] : null;
    }

    /// <summary>Rows used so far, removed ones included (row numbers run from 0 to Count - 1).</summary>
    public int Count => count;
    /// <summary>Files and folders in the index.</summary>
    public int Live => count - dead;
    /// <summary>Goes up with every change.</summary>
    public int Generation => Volatile.Read(ref generation);
    /// <summary>Rows whose place in the name order is not known yet (added or renamed since BuildOrder).</summary>
    public int Unranked => unranked;
    /// <summary>The current name and path order, or null before BuildOrder.</summary>
    public Ordering? Order => order;
    public IReadOnlyList<int> Roots => roots;
    /// <summary>Share of the rows and pools taken by removed entries, 0 to 1.</summary>
    public double Waste => count == 0 ? 0 : Math.Max((double)dead / count, deadChars / (double)Math.Max(1, namesUsed + keysUsed));

    /// <summary>Memory the index holds, in bytes (arrays at their current capacity).</summary>
    public long Bytes =>
        (long)parent.Length * (4 * 5 + 1 + (size is null ? 0 : 8) + (modified is null ? 0 : 4)) + (names.Length + keys.Length) * 2L
        + (order is null ? 0 : order.NameRank.Length * 12L);

    private void Changed() => Interlocked.Increment(ref generation);

    // --- reading ---

    public bool IsFolder(int row) => (flags[row] & Folder) != 0;
    public bool IsRemoved(int row) => (flags[row] & Removed) != 0;
    public bool IsRoot(int row) => (flags[row] & Root) != 0;
    public byte Flags(int row) => flags[row];
    public int Parent(int row) => parent[row];
    /// <summary>A file's size in bytes; -1 for folders, and when sizes are not kept.</summary>
    public long Size(int row) => size is null || IsFolder(row) ? -1 : size[row];
    public DateTime? Modified(int row) =>
        modified is null || modified[row] == 0 ? null : new DateTime(Epoch + modified[row] * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    /// <summary>Date modified as a number that sorts like the date (0 = unknown).</summary>
    public uint ModifiedSeconds(int row) => modified?[row] ?? 0;

    public static uint ToSeconds(DateTime utc)
    {
        var seconds = (utc.ToUniversalTime().Ticks - Epoch) / TimeSpan.TicksPerSecond;
        return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
    }

    /// <summary>The name as written.</summary>
    public ReadOnlySpan<char> NameSpan(int row)
    {
        var pool = (flags[row] & NameInKeys) != 0 ? keys : names;
        var span = pool.AsSpan(nameStart[row]);
        var end = span.IndexOf('\0');
        return end < 0 ? span : span[..end];
    }

    public string Name(int row) => new(NameSpan(row));

    /// <summary>The name folded for search (lower case, no accents or tashkeel).</summary>
    public ReadOnlySpan<char> Key(int row)
    {
        var span = keys.AsSpan(keyStart[row]);
        var end = span.IndexOf('\0');
        return end < 0 ? span : span[..end];
    }

    /// <summary>The folder a row is in ("" for a root).</summary>
    public string FolderOf(int row) => parent[row] < 0 ? "" : FullPath(parent[row]);

    /// <summary>The deepest a path can go: Windows paths stop at 32,767 characters, two or more per folder.</summary>
    private const int MaxDepth = 16384;

    /// <summary>
    /// Walked up in a loop, not by calling itself: a folder chain thousands deep (a reparse
    /// point that leads back into itself) ran the stack out and closed PC Butler. A chain that
    /// loops back on itself stops after MaxDepth steps, and the path is built once, not joined
    /// again at each level: a loop through millions of rows used to keep the window busy for good.
    /// </summary>
    public string FullPath(int row)
    {
        if (parent[row] < 0)
            return Name(row);
        if (IsFolder(row) && folderPaths.TryGetValue(row, out var known))
            return known;
        var chain = new List<int>();
        var path = "";
        for (var at = row; ; at = parent[at])
        {
            if (at < 0 || chain.Count > MaxDepth)
                break;
            if (parent[at] < 0)
            {
                path = Name(at);
                break;
            }
            if (at != row && IsFolder(at) && folderPaths.TryGetValue(at, out var cached))
            {
                path = cached;
                break;
            }
            chain.Add(at);
        }
        var text = new StringBuilder(path);
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            if (text.Length == 0 || (text[^1] != '\\' && text[^1] != '/'))
                text.Append('\\');
            text.Append(NameSpan(chain[index]));
            if (index > 0 && IsFolder(chain[index]) && text.Length <= 1024) // very long ones are rare, and would fill memory
                folderPaths[chain[index]] = text.ToString();
        }
        var full = text.ToString();
        if (IsFolder(row) && full.Length <= 1024)
            folderPaths[row] = full;
        return full;
    }

    /// <summary>Whether a row is this folder or somewhere inside it.</summary>
    public bool IsWithin(int row, int folder)
    {
        for (var (at, steps) = (row, 0); at >= 0 && steps <= MaxDepth; at = parent[at], steps++)
            if (at == folder)
                return true;
        return false;
    }

    public static string Join(string folder, ReadOnlySpan<char> name) =>
        folder.EndsWith('\\') || folder.EndsWith('/') ? string.Concat(folder, name) : string.Concat(folder, "\\", name);

    /// <summary>The children of a folder.</summary>
    public IEnumerable<int> Children(int row)
    {
        for (var child = firstChild[row]; child >= 0; child = nextSibling[child])
            yield return child;
    }

    /// <summary>The child of a folder with this name (case does not matter), or -1.</summary>
    public int FindChild(int folder, ReadOnlySpan<char> name)
    {
        for (var child = firstChild[folder]; child >= 0; child = nextSibling[child])
            if (NameSpan(child).Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        return -1;
    }

    /// <summary>The row of a full path, or -1 when it is not in the index.</summary>
    public int Find(string path)
    {
        var best = -1;
        var bestLength = 0;
        foreach (var root in roots)
        {
            var rootPath = Name(root).TrimEnd('\\', '/');
            if (path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) && rootPath.Length >= bestLength
                && (path.Length == rootPath.Length || path[rootPath.Length] is '\\' or '/'))
                (best, bestLength) = (root, rootPath.Length);
        }
        if (best < 0)
            return -1;
        var row = best;
        foreach (var part in path[bestLength..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            row = FindChild(row, part);
            if (row < 0)
                return -1;
        }
        return row;
    }

    // --- changing ---

    private void Grow(int needed)
    {
        if (needed <= parent.Length)
            return;
        var capacity = Math.Max(needed, parent.Length * 2);
        Array.Resize(ref parent, capacity);
        Array.Resize(ref firstChild, capacity);
        Array.Resize(ref nextSibling, capacity);
        Array.Resize(ref nameStart, capacity);
        Array.Resize(ref keyStart, capacity);
        Array.Resize(ref flags, capacity);
        if (size is not null)
            Array.Resize(ref size, capacity);
        if (modified is not null)
            Array.Resize(ref modified, capacity);
    }

    private static void Room(ref char[] pool, int used, int more)
    {
        if (used + more > pool.Length)
            Array.Resize(ref pool, Math.Max(used + more, pool.Length * 2));
    }

    /// <summary>Write a row's folded name (and the name itself, when that differs) to the pools.</summary>
    private void WriteName(int row, ReadOnlySpan<char> name)
    {
        Room(ref keys, keysUsed, name.Length + 3);
        keys[keysUsed++] = (char)(RowBase + row / RowSpan);
        keys[keysUsed++] = (char)(RowBase + row % RowSpan);
        keyStart[row] = keysUsed;
        var length = SearchText.Apply(name, keys.AsSpan(keysUsed), Fold.Full, Variants);
        var same = length == name.Length && keys.AsSpan(keysUsed, length).SequenceEqual(name);
        keysUsed += length;
        keys[keysUsed++] = '\0';
        if (same)
        {
            flags[row] |= NameInKeys;
            nameStart[row] = keyStart[row];
            return;
        }
        flags[row] = (byte)(flags[row] & ~NameInKeys);
        Room(ref names, namesUsed, name.Length + 1);
        nameStart[row] = namesUsed;
        name.CopyTo(names.AsSpan(namesUsed));
        namesUsed += name.Length;
        names[namesUsed++] = '\0';
    }

    /// <summary>Blank a row's folded name so the block search never finds it again (its name stays readable).</summary>
    private void ForgetName(int row)
    {
        var key = keys.AsSpan(keyStart[row]);
        var end = key.IndexOf('\0');
        key[..end].Fill('\u0001');
        deadChars += end + 3 + ((flags[row] & NameInKeys) != 0 ? 0 : NameSpan(row).Length + 1);
    }

    private int AddUnlinked(int parentRow, ReadOnlySpan<char> name, byte rowFlags, long bytes, uint seconds)
    {
        if (count >= MaxRows)
            throw new InvalidOperationException("The search index is full.");
        Grow(count + 1);
        var row = count++;
        parent[row] = parentRow;
        firstChild[row] = -1;
        nextSibling[row] = -1;
        flags[row] = (byte)(rowFlags & Kept);
        if (size is not null)
            size[row] = bytes;
        if (modified is not null)
            modified[row] = seconds;
        WriteName(row, name);
        unranked++;
        return row;
    }

    private void Link(int row)
    {
        var folder = parent[row];
        if (folder < 0)
            return;
        nextSibling[row] = firstChild[folder];
        firstChild[folder] = row;
    }

    private void Unlink(int row)
    {
        var folder = parent[row];
        if (folder < 0)
            return;
        if (firstChild[folder] == row)
            firstChild[folder] = nextSibling[row];
        else
        {
            for (var child = firstChild[folder]; child >= 0; child = nextSibling[child])
            {
                if (nextSibling[child] == row)
                {
                    nextSibling[child] = nextSibling[row];
                    break;
                }
            }
        }
        nextSibling[row] = -1;
    }

    /// <summary>A drive or folder at the top of the index; its name is its full path.</summary>
    public int AddRoot(string path)
    {
        var row = AddUnlinked(-1, path, Folder | Root, -1, 0);
        roots.Add(row);
        Changed();
        return row;
    }

    public int Add(int parentRow, ReadOnlySpan<char> name, bool folder, long bytes = -1, DateTime? modifiedUtc = null, bool hidden = false, bool system = false) =>
        Add(parentRow, name, (byte)((folder ? Folder : 0) | (hidden ? Hidden : 0) | (system ? SystemFile : 0)), bytes,
            modifiedUtc is { } when ? ToSeconds(when) : 0);

    public int Add(int parentRow, ReadOnlySpan<char> name, byte rowFlags, long bytes, uint seconds)
    {
        var row = AddUnlinked(parentRow, name, rowFlags, bytes, seconds);
        Link(row);
        Changed();
        return row;
    }

    /// <summary>A file's size and date changed.</summary>
    public void Update(int row, long bytes, DateTime? modifiedUtc)
    {
        if (size is not null)
            size[row] = bytes;
        if (modified is not null)
            modified[row] = modifiedUtc is { } when ? ToSeconds(when) : 0;
        Changed();
    }

    /// <summary>Take a row out, and everything under it.</summary>
    public void Remove(int row)
    {
        if (IsRemoved(row))
            return;
        Unlink(row);
        var stack = new Stack<int>();
        stack.Push(row);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (IsRemoved(next))
                continue;
            for (var child = firstChild[next]; child >= 0; child = nextSibling[child])
                stack.Push(child);
            ForgetName(next);
            flags[next] |= Removed;
            firstChild[next] = -1;
            dead++;
        }
        roots.Remove(row);
        if (IsFolder(row))
            folderPaths.Clear();
        Changed();
    }

    /// <summary>Give a row a new name, and optionally move it into another folder.</summary>
    public void Rename(int row, ReadOnlySpan<char> name, int newParent = -1)
    {
        ForgetName(row);
        WriteName(row, name);
        if (newParent >= 0 && newParent != parent[row] && !IsWithin(newParent, row)) // never into itself: that would loop forever
        {
            Unlink(row);
            parent[row] = newParent;
            Link(row);
        }
        if (order is { } known && row < known.NameRank.Length)
        {
            known.NameRank[row] = -1;
            known.PathRank[row] = -1;
        }
        unranked++;
        if (IsFolder(row))
            folderPaths.Clear();
        Changed();
    }

    // --- order ---

    /// <summary>
    /// Work out the name order (folded name, then the name as written) and the path order (each
    /// folder's contents in name order, walked depth first), so results sort at once. Takes a few
    /// seconds for millions of rows; call it with Gate held for reading.
    /// </summary>
    public void BuildOrder(CancellationToken cancel = default)
    {
        var total = count;
        var live = new int[total - dead];
        var prefixes = new long[live.Length];
        var used = 0;
        for (var row = 0; row < total && used < live.Length; row++)
        {
            if (IsRemoved(row))
                continue;
            live[used] = row;
            var key = Key(row);
            long prefix = 0;
            for (var index = 0; index < 4; index++)
                prefix = (prefix << 16) | (index < key.Length ? key[index] : 0u);
            prefixes[used++] = prefix;
        }
        cancel.ThrowIfCancellationRequested();
        Array.Sort(prefixes, live, 0, used);
        // Rows that share their first four characters are put in order by the whole name.
        var comparer = Comparer<int>.Create(CompareNames);
        for (var start = 0; start < used;)
        {
            var end = start + 1;
            while (end < used && prefixes[end] == prefixes[start])
                end++;
            if (end - start > 1)
                Array.Sort(live, start, end - start, comparer);
            start = end;
            if ((start & 0xFFFF) == 0)
                cancel.ThrowIfCancellationRequested();
        }
        FinishOrder(used == live.Length ? live : live[..used], total);
    }

    private void FinishOrder(int[] live, int total)
    {
        var rankOf = new int[total];
        Array.Fill(rankOf, -1);
        for (var index = 0; index < live.Length; index++)
            rankOf[live[index]] = index;

        // The path order: each folder's children in name order, walked depth first from the roots.
        var childCount = new int[total + 1];
        foreach (var row in live)
            if (parent[row] >= 0)
                childCount[parent[row] + 1]++;
        for (var index = 1; index <= total; index++)
            childCount[index] += childCount[index - 1];
        var children = new int[live.Length];
        var fill = (int[])childCount.Clone();
        foreach (var row in live)
            if (parent[row] >= 0)
                children[fill[parent[row]]++] = row;
        var paths = new int[total];
        Array.Fill(paths, -1);
        var next = 0;
        var stack = new Stack<(int Row, int Child)>();
        foreach (var root in live.Where(row => parent[row] < 0))
        {
            paths[root] = next++;
            stack.Push((root, childCount[root]));
            while (stack.Count > 0)
            {
                var (folder, child) = stack.Pop();
                if (child >= childCount[folder + 1])
                    continue;
                stack.Push((folder, child + 1));
                var row = children[child];
                paths[row] = next++;
                if (IsFolder(row))
                    stack.Push((row, childCount[row]));
            }
        }
        order = new Ordering(rankOf, paths, live);
        unranked = count - total;
    }

    /// <summary>Two rows in name order: folded name first, then the name as written, then row number.</summary>
    public int CompareNames(int a, int b)
    {
        var result = Key(a).SequenceCompareTo(Key(b));
        if (result == 0)
            result = NameSpan(a).SequenceCompareTo(NameSpan(b));
        return result != 0 ? result : a.CompareTo(b);
    }

    /// <summary>Two rows in path order: by the folder they are in, then by name (the same order BuildOrder gives).</summary>
    public int ComparePaths(int a, int b)
    {
        var result = CompareFolders(parent[a], parent[b]);
        return result != 0 ? result : CompareNames(a, b);
    }

    /// <summary>Folders compared one level at a time, each level in name order; a folder comes before the folders inside it.</summary>
    private int CompareFolders(int x, int y)
    {
        if (x == y)
            return 0;
        if (x < 0 || y < 0)
            return x < 0 ? -1 : 1;
        var first = Chain(x);
        var second = Chain(y);
        for (var level = 0; level < Math.Min(first.Count, second.Count); level++)
            if (first[level] != second[level])
                return CompareNames(first[level], second[level]);
        return first.Count.CompareTo(second.Count);
    }

    private List<int> Chain(int folder)
    {
        var chain = new List<int>();
        for (var row = folder; row >= 0 && chain.Count <= count; row = parent[row])
            chain.Add(row);
        chain.Reverse();
        return chain;
    }

    private (int Generation, long[] Bits)? live;

    /// <summary>One bit per row that is not removed; worked out once per change. Callers must not change it.</summary>
    public long[] LiveBits()
    {
        var now = Generation;
        if (live is { } known && known.Generation == now && known.Bits.Length == (count + 63) / 64)
            return known.Bits;
        var bits = new long[(count + 63) / 64];
        for (var row = 0; row < count; row++)
            if ((flags[row] & Removed) == 0)
                bits[row >> 6] |= 1L << (row & 63);
        live = (now, bits);
        return bits;
    }

    // --- the block search ---

    /// <summary>
    /// Every row whose folded name contains needle (already folded with Fold.Full), marked in a
    /// bit set. The key pool is searched as one block, in parallel, with the vectorized IndexOf.
    /// </summary>
    public long[] Contains(string needle, CancellationToken cancel = default)
    {
        var bits = new long[(count + 63) / 64];
        var pool = keys;
        var used = keysUsed;
        if (needle.Length == 0)
            return bits;
        var pieces = Math.Max(1, Math.Min(Environment.ProcessorCount * 4, used / 65536));
        var bounds = new int[pieces + 1];
        bounds[pieces] = used;
        for (var piece = 1; piece < pieces; piece++)
        {
            var at = (int)((long)used * piece / pieces);
            var next = pool.AsSpan(at, used - at).IndexOf('\0');
            bounds[piece] = next < 0 ? used : at + next;
        }
        Parallel.For(0, pieces, new ParallelOptions { CancellationToken = cancel }, piece =>
        {
            int position = bounds[piece], end = bounds[piece + 1];
            while (position < end)
            {
                var found = pool.AsSpan(position, end - position).IndexOf(needle);
                if (found < 0)
                    break;
                var hit = position + found;
                var entry = pool.AsSpan(0, hit).LastIndexOf('\0');
                var row = (pool[entry + 1] - RowBase) * RowSpan + (pool[entry + 2] - RowBase);
                if ((uint)row < (uint)bits.Length * 64 && (flags[row] & Removed) == 0)
                    Interlocked.Or(ref bits[row >> 6], 1L << (row & 63));
                var rest = pool.AsSpan(hit, end - hit).IndexOf('\0');
                if (rest < 0)
                    break;
                position = hit + rest + 1;
            }
        });
        return bits;
    }

    // --- whole index in and out (saving, loading, compacting) ---

    /// <summary>Hands Import the name of row number row, written into a buffer; returns its length.</summary>
    public delegate int NameReader(int row, Span<char> into);

    /// <summary>
    /// The live rows for saving, in name order when it is known, with parents renumbered to match.
    /// Rows[i] is the row of this index that becomes row i (its name is read from here: names are
    /// not copied, millions of strings would cost more memory than the index itself).
    /// </summary>
    public (int[] Rows, int[] Parents, byte[] Flags, long[]? Sizes, uint[]? Modified, bool ByName) Export()
    {
        var byName = order is not null && unranked == 0;
        var rows = new int[Live];
        var used = 0;
        if (byName)
        {
            foreach (var row in order!.NameOrder)
                if (!IsRemoved(row) && used < rows.Length)
                    rows[used++] = row;
        }
        else
        {
            for (var row = 0; row < count && used < rows.Length; row++)
                if (!IsRemoved(row))
                    rows[used++] = row;
        }
        var renumber = new int[count];
        Array.Fill(renumber, -1); // a row whose folder was taken out is not given another folder
        for (var index = 0; index < used; index++)
            renumber[rows[index]] = index;
        var parents = new int[used];
        var rowFlags = new byte[used];
        var sizes = size is null ? null : new long[used];
        var dates = modified is null ? null : new uint[used];
        for (var index = 0; index < used; index++)
        {
            var row = rows[index];
            parents[index] = parent[row] < 0 || parent[row] >= count ? -1 : renumber[parent[row]];
            rowFlags[index] = (byte)(flags[row] & Kept);
            if (sizes is not null)
                sizes[index] = size![row];
            if (dates is not null)
                dates[index] = modified![row];
        }
        return (used == rows.Length ? rows : rows[..used], parents, rowFlags, sizes, dates, byName);
    }

    /// <summary>
    /// Build an index from exported rows (names are asked for in row order). Rows that come in
    /// name order (sortedByName) get their order straight away, without sorting again.
    /// </summary>
    public static SearchIndex Import(int[] parents, byte[] rowFlags, long[]? sizes, uint[]? dates, NameReader names, bool variants, bool sortedByName = false)
    {
        var index = new SearchIndex(sizes is not null, dates is not null, variants, parents.Length);
        var buffer = new char[32768];
        for (var row = 0; row < parents.Length; row++)
        {
            var length = names(row, buffer);
            index.AddUnlinked(parents[row], buffer.AsSpan(0, length), rowFlags[row], sizes?[row] ?? -1, dates?[row] ?? 0);
            if ((rowFlags[row] & Root) != 0)
                index.roots.Add(row);
        }
        // Linked in reverse, so each folder's children come out in their saved order.
        for (var row = parents.Length - 1; row >= 0; row--)
            index.Link(row);
        index.Repair();
        index.Changed();
        if (sortedByName && index.InNameOrder())
            index.FinishOrder(Enumerable.Range(0, index.count).ToArray(), index.count);
        return index;
    }

    /// <summary>Broken entries taken out when this index was loaded; their folders are walked again.</summary>
    public int Repaired { get; private set; }

    /// <summary>
    /// Take out rows that cannot be shown: those whose folders lead back to themselves (an index
    /// saved by an older version could hold such a loop, and drawing one froze the window) and
    /// those with no folder that are not drives or folders at the top.
    /// </summary>
    private void Repair()
    {
        var state = new byte[count]; // 0 not seen, 1 on the walk being made, 2 fine
        var walk = new List<int>();
        var broken = new List<int>();
        for (var start = 0; start < count; start++)
        {
            if (state[start] != 0)
                continue;
            walk.Clear();
            var at = start;
            while (at >= 0 && at < count && state[at] == 0)
            {
                state[at] = 1;
                walk.Add(at);
                at = parent[at];
            }
            if (at >= count || (at >= 0 && state[at] == 1))
                broken.Add(walk[^1]); // the loop is cut where this walk closed it
            foreach (var row in walk)
                state[row] = 2;
        }
        for (var row = 0; row < count; row++)
            if (parent[row] < 0 && !IsRoot(row) && !IsRemoved(row))
                broken.Add(row);
        foreach (var row in broken)
        {
            if (IsRemoved(row))
                continue;
            if (parent[row] >= count)
                parent[row] = -1;
            Remove(row);
            parent[row] = -1;
        }
        Repaired = broken.Count;
        if (broken.Count > 0)
            Log.Warning($"Search: {broken.Count:N0} broken entries were taken out of the index");
    }

    /// <summary>True when the rows already come in name order (a file written by an older version may use other rules).</summary>
    private bool InNameOrder()
    {
        for (var row = 1; row < count; row++)
            if (CompareNames(row - 1, row) > 0)
                return false;
        return true;
    }

    /// <summary>A copy without the removed rows (its row numbers differ), in name order. Call with Gate held for reading.</summary>
    public SearchIndex Compacted()
    {
        var (rows, parents, rowFlags, sizes, dates, byName) = Export();
        var copy = Import(parents, rowFlags, sizes, dates, (row, into) =>
        {
            var name = NameSpan(rows[row]);
            name.CopyTo(into);
            return name.Length;
        }, Variants, byName);
        if (copy.order is null)
            copy.BuildOrder();
        return copy;
    }
    /// <summary>Add another index's rows (a drive scanned on its own) as roots of this one.</summary>
    public void Append(SearchIndex other)
    {
        var renumber = new int[other.count];
        for (var row = 0; row < other.count; row++)
        {
            if (other.IsRemoved(row))
                continue;
            var otherParent = other.parent[row];
            renumber[row] = otherParent < 0
                ? AddRoot(other.Name(row))
                : Add(renumber[otherParent], other.NameSpan(row), other.flags[row], other.size?[row] ?? -1, other.modified?[row] ?? 0);
        }
    }
}
