// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;

namespace PCButler.Core.Search;

public enum SearchSort { Name, Path, Size, Modified }

/// <summary>
/// The rows that matched a search, in the order asked for. Index is the index they were found
/// in: the window reads names and paths from it, even if a newer index has replaced it since.
/// </summary>
public sealed record SearchResult(SearchIndex Index, int[] Rows, int Generation, TimeSpan Took, string Query)
{
    public static SearchResult Empty(SearchIndex index) => new(index, [], index.Generation, TimeSpan.Zero, "");
}

/// <summary>Runs a search over an index and puts the results in order.</summary>
public static class Searcher
{
    public static SearchResult Run(SearchIndex index, string query, SearchSort sort = SearchSort.Name, bool descending = false,
                                   SearchOptions? options = null, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var matcher = QueryCompiler.Compile(query, options);
        index.Gate.EnterReadLock();
        try
        {
            var bits = matcher.Evaluate(index, cancel);
            cancel.ThrowIfCancellationRequested();
            var rows = Sort(index, bits, sort, cancel);
            if (descending)
                Array.Reverse(rows);
            return new SearchResult(index, rows, index.Generation, watch.Elapsed, query);
        }
        finally
        {
            index.Gate.ExitReadLock();
        }
    }

    /// <summary>
    /// The marked rows in order. Name and path use the order the index worked out beforehand, so a
    /// million results sort in milliseconds; only rows added or renamed since are compared one by one.
    /// </summary>
    public static int[] Sort(SearchIndex index, long[] bits, SearchSort sort, CancellationToken cancel = default)
    {
        var order = index.Order;
        switch (sort)
        {
            case SearchSort.Size:
            case SearchSort.Modified:
            {
                var rows = Bits.Rows(bits);
                var keys = new long[rows.Length];
                for (var at = 0; at < rows.Length; at++)
                    keys[at] = sort == SearchSort.Size ? index.Size(rows[at]) : index.ModifiedSeconds(rows[at]);
                Array.Sort(keys, rows);
                return rows;
            }
            case SearchSort.Name when order is not null:
            {
                var total = Bits.Count(bits);
                List<int> ranked, others = [];
                if (total > index.Count / 16)
                {
                    // Many results: walking the whole name order and keeping the marked rows is quicker than sorting them.
                    ranked = new List<int>(total);
                    foreach (var row in order.NameOrder)
                        if ((bits[row >> 6] & (1L << (row & 63))) != 0 && order.Has(row))
                            ranked.Add(row);
                    foreach (var row in Bits.Rows(bits))
                        if (!order.Has(row))
                            others.Add(row);
                }
                else
                {
                    ranked = new List<int>(total);
                    foreach (var row in Bits.Rows(bits))
                        (order.Has(row) ? ranked : others).Add(row);
                    var rows = ranked.ToArray();
                    var keys = rows.Select(row => order.NameRank[row]).ToArray();
                    Array.Sort(keys, rows);
                    ranked = [.. rows];
                }
                cancel.ThrowIfCancellationRequested();
                return Merge(ranked, others, index.CompareNames);
            }
            case SearchSort.Path when order is not null:
            {
                var ranked = new List<int>();
                var others = new List<int>();
                var keys = new List<long>();
                foreach (var row in Bits.Rows(bits))
                {
                    var folder = index.Parent(row);
                    if (order.Has(row) && (folder < 0 || order.Has(folder)))
                    {
                        ranked.Add(row);
                        keys.Add(((long)(folder < 0 ? 0 : order.PathRank[folder] + 1) << 32) | (uint)order.NameRank[row]);
                    }
                    else
                        others.Add(row);
                }
                var rows = ranked.ToArray();
                Array.Sort(keys.ToArray(), rows);
                cancel.ThrowIfCancellationRequested();
                return Merge([.. rows], others, index.ComparePaths);
            }
            default:
            {
                // No order worked out yet: compare them one by one.
                var rows = Bits.Rows(bits);
                Array.Sort(rows, Comparer<int>.Create(sort == SearchSort.Path ? index.ComparePaths : index.CompareNames));
                return rows;
            }
        }
    }

    /// <summary>Put the few rows that are not in the prepared order in their places among the rest.</summary>
    private static int[] Merge(List<int> sorted, List<int> others, Comparison<int> compare)
    {
        if (others.Count == 0)
            return [.. sorted];
        others.Sort(compare);
        var merged = new int[sorted.Count + others.Count];
        int a = 0, b = 0, next = 0;
        while (a < sorted.Count && b < others.Count)
            merged[next++] = compare(sorted[a], others[b]) <= 0 ? sorted[a++] : others[b++];
        while (a < sorted.Count)
            merged[next++] = sorted[a++];
        while (b < others.Count)
            merged[next++] = others[b++];
        return merged;
    }
}
