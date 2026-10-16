// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>An NTFS volume's layout, from its boot sector.</summary>
public readonly record struct NtfsLayout(int BytesPerSector, long ClusterSize, long MftCluster, int RecordSize);

/// <summary>One name of a file: the folder it is in (record number) and the name there. A file with hard links has several.</summary>
public readonly record struct MftName(long Parent, string Name);

/// <summary>What one file record says, after the parts kept in extension records are joined in by the caller.</summary>
public sealed class MftRecord
{
    public long Number { get; set; }
    /// <summary>0 for a base record; otherwise the base record this one extends.</summary>
    public long BaseRecord { get; set; }
    public bool InUse { get; set; }
    public bool Directory { get; set; }
    /// <summary>FILE_ATTRIBUTE_* from $STANDARD_INFORMATION, when this record holds it.</summary>
    public uint? Attributes { get; set; }
    /// <summary>Date modified as a FILETIME, when this record holds $STANDARD_INFORMATION.</summary>
    public long? Modified { get; set; }
    /// <summary>The unnamed $DATA's size, when this record holds its start.</summary>
    public long? Size { get; set; }
    /// <summary>Its names, without the short 8.3 (DOS) ones.</summary>
    public List<MftName> Names { get; } = [];

    public void Clear()
    {
        (Number, BaseRecord, InUse, Directory, Attributes, Modified, Size) = (0, 0, false, false, null, null, null);
        Names.Clear();
    }
}

/// <summary>
/// Reading NTFS's master file table from raw bytes. Only bytes go in, so this runs anywhere
/// (the tests feed it records written by hand); the Search service is what reads the volume.
/// Layouts follow the NTFS on-disk format: boot sector, FILE records with their update
/// sequence array, attributes, and data runs.
/// </summary>
public static class MftParser
{
    private const uint FileSignature = 0x454C4946; // "FILE"
    private const uint StandardInformation = 0x10, FileName = 0x30, Data = 0x80, End = 0xFFFFFFFF;
    /// <summary>A file reference is a 48-bit record number and a 16-bit sequence number.</summary>
    public const long RecordMask = 0xFFFFFFFFFFFF;
    /// <summary>The record of the volume's root folder.</summary>
    public const long RootRecord = 5;

    /// <summary>The layout from the boot sector, or null when it is not NTFS.</summary>
    public static NtfsLayout? Layout(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 512 || !boot.Slice(3, 8).SequenceEqual("NTFS    "u8))
            return null;
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[0x0B..]);
        var raw = boot[0x0D];
        long sectorsPerCluster = raw <= 0x80 ? raw : 1L << (256 - raw);
        var cluster = bytesPerSector * sectorsPerCluster;
        var mft = BinaryPrimitives.ReadInt64LittleEndian(boot[0x30..]);
        var perRecord = (sbyte)boot[0x40];
        var recordSize = perRecord > 0 ? (int)(perRecord * cluster) : 1 << -perRecord;
        if (bytesPerSector is < 256 or > 4096 || cluster <= 0 || recordSize is < 256 or > 65536 || mft <= 0)
            return null;
        return new NtfsLayout(bytesPerSector, cluster, mft, recordSize);
    }

    /// <summary>
    /// Put back the last two bytes of each sector of a record from its update sequence array.
    /// False when the record is torn (a sector does not carry the sequence number), or not a FILE record.
    /// </summary>
    public static bool Fixup(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
            return false;
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]), count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (count == 0 || offset + count * 2 > record.Length)
            return false;
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[offset..]);
        for (var sector = 1; sector < count && sector * bytesPerSector <= record.Length; sector++)
        {
            var tail = record.Slice(sector * bytesPerSector - 2, 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail) != sequence)
                return false;
            record.Slice(offset + sector * 2, 2).CopyTo(tail);
        }
        return true;
    }

    /// <summary>A data run list as (first cluster, cluster count); the first cluster is -1 for a sparse run.</summary>
    public static List<(long Cluster, long Count)> Runs(ReadOnlySpan<byte> list)
    {
        var runs = new List<(long, long)>();
        long cluster = 0;
        var at = 0;
        while (at < list.Length && list[at] != 0)
        {
            int lengthBytes = list[at] & 0xF, offsetBytes = list[at] >> 4;
            at++;
            if (at + lengthBytes + offsetBytes > list.Length)
                break;
            long count = 0;
            for (var index = 0; index < lengthBytes; index++)
                count |= (long)list[at + index] << (8 * index);
            at += lengthBytes;
            if (offsetBytes == 0)
            {
                runs.Add((-1, count));
                continue;
            }
            long delta = 0;
            for (var index = 0; index < offsetBytes; index++)
                delta |= (long)list[at + index] << (8 * index);
            if ((list[at + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
                delta |= -1L << (8 * offsetBytes);
            at += offsetBytes;
            cluster += delta;
            runs.Add((cluster, count));
        }
        return runs;
    }

    /// <summary>Where $MFT itself lies, from its own record (number 0, fixed up): its size in bytes and its runs.</summary>
    public static (long Bytes, List<(long Cluster, long Count)> Runs)? MftExtent(ReadOnlySpan<byte> record)
    {
        if (record.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
            return null;
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        for (int at = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]); at + 16 <= used;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(at + 4)..]);
            if (type == End || length <= 0 || at + length > used)
                break;
            var attribute = record.Slice(at, length);
            if (type == Data && attribute[8] != 0 && attribute[9] == 0 && attribute.Length >= 0x40)
            {
                var bytes = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
                var runs = Runs(attribute[BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..])..]);
                return (bytes, runs);
            }
            at += length;
        }
        return null;
    }

    /// <summary>
    /// Read one record (already fixed up) into into. False when it is not a FILE record. Names in
    /// the DOS (8.3) namespace are left out: they are the short names Windows makes up.
    /// </summary>
    public static bool Parse(ReadOnlySpan<byte> record, long number, MftRecord into)
    {
        into.Clear();
        into.Number = number;
        if (record.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
            return false;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        into.InUse = (flags & 1) != 0;
        into.Directory = (flags & 2) != 0;
        into.BaseRecord = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]) & RecordMask;
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        for (int at = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]); at + 16 <= used;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            if (type == End)
                break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(at + 4)..]);
            if (length <= 0 || at + length > used)
                break;
            var attribute = record.Slice(at, length);
            var resident = attribute[8] == 0;
            var unnamed = attribute[9] == 0;
            if (resident && attribute.Length >= 0x18)
            {
                var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
                if (valueOffset + valueLength <= attribute.Length)
                {
                    var value = attribute.Slice(valueOffset, valueLength);
                    if (type == StandardInformation && value.Length >= 36)
                    {
                        into.Modified = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                        into.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                    }
                    else if (type == FileName && value.Length >= 0x42)
                    {
                        int nameLength = value[0x40];
                        var space = value[0x41];
                        if (space != 2 && 0x42 + nameLength * 2 <= value.Length)
                        {
                            var name = Encoding.Unicode.GetString(value.Slice(0x42, nameLength * 2));
                            var parent = BinaryPrimitives.ReadInt64LittleEndian(value) & RecordMask;
                            if (!into.Names.Contains(new MftName(parent, name)))
                                into.Names.Add(new MftName(parent, name));
                        }
                    }
                    else if (type == Data && unnamed)
                        into.Size = valueLength;
                }
            }
            else if (!resident && type == Data && unnamed && attribute.Length >= 0x38
                     && BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) == 0)
                into.Size = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
            at += length;
        }
        return true;
    }

    /// <summary>NTFS's own files (the first 24 records and what is under $Extend) are not shown.</summary>
    public static bool IsMetadata(long record) => record < 24 && record != RootRecord;
}
