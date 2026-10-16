// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>Where a volume's change journal stands: its id (a new id means it was made again) and its range of change numbers (USNs).</summary>
public readonly record struct UsnJournalInfo(ulong Id, long First, long Next, long Lowest);

/// <summary>One change journal record. File and parent ids are 64-bit on NTFS, 128-bit on ReFS.</summary>
public readonly record struct UsnRecord(long Usn, UInt128 File, UInt128 Parent, uint Reason, uint Attributes, string Name)
{
    public bool IsFolder => (Attributes & 0x10) != 0;
}

/// <summary>
/// Reading the NTFS/ReFS change journal from raw bytes (USN_JOURNAL_DATA and USN_RECORD_V2/V3),
/// and turning its records into file changes by path. Only bytes go in: the Search service reads
/// the journal, the tests feed records written by hand.
/// </summary>
public static class UsnParser
{
    public const uint DataOverwrite = 0x1, DataExtend = 0x2, DataTruncation = 0x4, Create = 0x100, Delete = 0x200,
        RenameOld = 0x1000, RenameNew = 0x2000, BasicInfo = 0x8000, Close = 0x80000000;

    /// <summary>The answer of FSCTL_QUERY_USN_JOURNAL.</summary>
    public static UsnJournalInfo? Journal(ReadOnlySpan<byte> data) =>
        data.Length < 32 ? null : new UsnJournalInfo(BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data[8..]),
                                                      BinaryPrimitives.ReadInt64LittleEndian(data[16..]), BinaryPrimitives.ReadInt64LittleEndian(data[24..]));

    /// <summary>The answer of FSCTL_READ_USN_JOURNAL: the USN to read from next, and the records.</summary>
    public static (long Next, List<UsnRecord> Records) Read(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        if (buffer.Length < 8)
            return (0, records);
        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        for (var at = 8; at + 8 <= buffer.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[at..]);
            if (length < 60 || at + length > buffer.Length)
                break;
            if (Record(buffer.Slice(at, length)) is { } record)
                records.Add(record);
            at += (length + 7) & ~7;
        }
        return (next, records);
    }

    /// <summary>One USN_RECORD_V2 or V3, or null for another version.</summary>
    public static UsnRecord? Record(ReadOnlySpan<byte> bytes)
    {
        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int at;
        UInt128 file, parent;
        if (major == 2)
        {
            file = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            parent = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
            at = 24;
        }
        else if (major == 3 && bytes.Length >= 76)
        {
            file = BinaryPrimitives.ReadUInt128LittleEndian(bytes[8..]);
            parent = BinaryPrimitives.ReadUInt128LittleEndian(bytes[24..]);
            at = 40;
        }
        else
            return null;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 16)..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 28)..]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 32)..]), nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 34)..]);
        if (nameOffset + nameLength > bytes.Length)
            return null;
        return new UsnRecord(usn, file, parent, reason, attributes, Encoding.Unicode.GetString(bytes.Slice(nameOffset, nameLength)));
    }

    /// <summary>
    /// Turn journal records into changes by path. folderPath gives a folder's full path from its id
    /// (null when it is gone). A rename comes as two records, the old name then the new one; renamed
    /// remembers old names between calls, as a rename can be split across two reads.
    /// </summary>
    public static List<FileChange> Changes(IEnumerable<UsnRecord> records, Func<UInt128, string?> folderPath, Dictionary<UInt128, string> renamed)
    {
        var changes = new List<FileChange>();
        string? PathOf(UsnRecord record) => folderPath(record.Parent) is { } folder
            ? (folder.EndsWith('\\') ? folder + record.Name : folder + "\\" + record.Name)
            : null;
        foreach (var record in records)
        {
            var reason = record.Reason;
            if ((reason & RenameOld) != 0)
            {
                if (PathOf(record) is { } old)
                    renamed[record.File] = old;
                continue;
            }
            if ((reason & RenameNew) != 0 && (reason & Close) == 0)
            {
                if (PathOf(record) is not { } now)
                    continue;
                changes.Add(renamed.Remove(record.File, out var before) ? new FileChange(ChangeKind.Renamed, now, before) : new FileChange(ChangeKind.Created, now));
                continue;
            }
            if ((reason & Close) == 0 || PathOf(record) is not { } path)
                continue;
            if ((reason & Delete) != 0)
            {
                if ((reason & Create) == 0)
                    changes.Add(new FileChange(ChangeKind.Deleted, path));
            }
            else if ((reason & Create) != 0)
                changes.Add(new FileChange(ChangeKind.Created, path));
            else if ((reason & RenameNew) != 0 && renamed.Remove(record.File, out var before))
                changes.Add(new FileChange(ChangeKind.Renamed, path, before));
            else if ((reason & (DataOverwrite | DataExtend | DataTruncation | BasicInfo)) != 0 && !record.IsFolder)
                changes.Add(new FileChange(ChangeKind.Changed, path));
        }
        return changes;
    }
}
