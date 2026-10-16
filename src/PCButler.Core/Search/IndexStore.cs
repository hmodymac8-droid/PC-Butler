// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>What the index file says about itself besides the rows.</summary>
public sealed record IndexInfo(string Fingerprint, DateTime SavedUtc, DateTime ScannedUtc);

/// <summary>
/// Saving and loading the index, so search is ready the moment PC Butler starts instead of after
/// a scan. One binary file, versioned: a header, then each column of the index as one block
/// (parents, flags, sizes, dates), then the names in UTF-8, each ended by a nul byte. Rows are
/// saved in name order, so loading needs no sorting.
///
/// The file is large (tens of bytes per file on disk), so it lives in the local, not the roaming,
/// application data folder.
/// </summary>
public static class IndexStore
{
    private static readonly byte[] Magic = "PCBSIDX\0"u8.ToArray();
    public const int FormatVersion = 1;

    /// <summary>%LOCALAPPDATA%\PCButler\search, or "search" in the data folder when PCBUTLER_DATA points elsewhere (tests, trial runs).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 }
            ? Path.Combine(AppInfo.DataDir, "search")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCButler", "search");

    public static string DefaultFile => Path.Combine(Folder, "index.bin");

    /// <summary>
    /// Write the index (the live rows only). Takes the index's read lock only while it copies, not
    /// while it writes: a change waiting for the lock holds up everyone reading after it, the
    /// window too.
    /// </summary>
    public static void Save(SearchIndex index, string file, IndexInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".new";
        int[] parents;
        byte[] flags;
        long[]? sizes;
        uint[]? dates;
        bool byName;
        var names = new MemoryStream();
        index.Gate.EnterReadLock();
        try
        {
            (var rows, parents, flags, sizes, dates, byName) = index.Export();
            names.Capacity = rows.Length * 24;
            var buffer = new byte[Encoding.UTF8.GetMaxByteCount(32768) + 1];
            foreach (var row in rows)
            {
                var length = Encoding.UTF8.GetBytes(index.NameSpan(row), buffer);
                buffer[length] = 0;
                names.Write(buffer, 0, length + 1);
            }
        }
        finally
        {
            index.Gate.ExitReadLock();
        }
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write((sizes is null ? 0 : 1) | (dates is null ? 0 : 2) | (index.Variants ? 4 : 0) | (byName ? 8 : 0));
            writer.Write(info.Fingerprint);
            writer.Write(info.SavedUtc.Ticks);
            writer.Write(info.ScannedUtc.Ticks);
            writer.Write(parents.Length);
            writer.Flush();
            stream.Write(MemoryMarshal.AsBytes(parents.AsSpan()));
            stream.Write(flags);
            if (sizes is not null)
                stream.Write(MemoryMarshal.AsBytes(sizes.AsSpan()));
            if (dates is not null)
                stream.Write(MemoryMarshal.AsBytes(dates.AsSpan()));
            names.Position = 0;
            names.CopyTo(stream);
        }
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Read an index file; null when there is none, or it is from another version or damaged.</summary>
    public static (SearchIndex Index, IndexInfo Info)? Load(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
                return null;
            var options = reader.ReadInt32();
            var info = new IndexInfo(reader.ReadString(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc), new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
            var count = reader.ReadInt32();
            if (count < 0 || count > SearchIndex.MaxRows)
                return null;
            var parents = new int[count];
            stream.ReadExactly(MemoryMarshal.AsBytes(parents.AsSpan()));
            var flags = new byte[count];
            stream.ReadExactly(flags);
            long[]? sizes = null;
            uint[]? dates = null;
            if ((options & 1) != 0)
            {
                sizes = new long[count];
                stream.ReadExactly(MemoryMarshal.AsBytes(sizes.AsSpan()));
            }
            if ((options & 2) != 0)
            {
                dates = new uint[count];
                stream.ReadExactly(MemoryMarshal.AsBytes(dates.AsSpan()));
            }
            var names = new byte[stream.Length - stream.Position];
            stream.ReadExactly(names);
            foreach (var parent in parents)
                if (parent < -1 || parent >= count)
                    return null;
            var position = 0;
            var index = SearchIndex.Import(parents, flags, sizes, dates, (_, into) =>
            {
                var end = names.AsSpan(position).IndexOf((byte)0);
                if (end < 0)
                    throw new InvalidDataException("The names are cut short.");
                var length = Encoding.UTF8.GetChars(names.AsSpan(position, end), into);
                position += end + 1;
                return length;
            }, (options & 4) != 0, (options & 8) != 0);
            return (index, info);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                          or EndOfStreamException or ArgumentException or OverflowException or OutOfMemoryException)
        {
            if (error is not FileNotFoundException and not DirectoryNotFoundException)
                Log.Warning($"Search index not loaded: {error.Message}");
            return null;
        }
    }
}
