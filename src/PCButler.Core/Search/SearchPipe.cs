// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core.Search;

public enum ChangeKind { Created, Deleted, Renamed, Changed, Lost }

/// <summary>Something that happened under an indexed folder. Lost: changes were missed (too many at once), so the folder needs walking again.</summary>
public readonly record struct FileChange(ChangeKind Kind, string Path, string? OldPath = null);

/// <summary>One name the Search service found in a volume's file table. Flags: 1 folder, 2 hidden, 4 system (as in SearchIndex).</summary>
public readonly record struct ServiceEntry(long Record, long Parent, byte Flags, long Size, uint Modified, string Name);

/// <summary>
/// The conversation between PC Butler and its Search service over a named pipe. The service's
/// side is in its own program (PCButlerSearch.exe), which compiles this same file; nothing here
/// may need more than plain .NET.
///
/// Requests are a command word and its arguments; answers start with a status byte.
///   hello                       -> int protocol version
///   volumes                     -> int count, then (string drive "C:\", string file system) each
///   scan drive                  -> status (1 ok, 0 failed + string why); then ulong journal id, long next USN,
///                                  int count, and the entries, each folder before what is in it
///   changes drive journal next  -> status (1 ok, 2 walk again: the journal was made again or ran past next,
///                                  0 failed + string why); then long next USN, int count, and the changes
/// </summary>
public static class SearchPipe
{
    public const string PipeName = "PCButlerSearch";
    public const string ServiceName = "PCButlerSearch";
    public const int Version = 1;
    public const byte Failed = 0, Ok = 1, WalkAgain = 2;

    public static void Write(BinaryWriter writer, in ServiceEntry entry)
    {
        writer.Write(entry.Record);
        writer.Write(entry.Parent);
        writer.Write(entry.Flags);
        writer.Write(entry.Size);
        writer.Write(entry.Modified);
        writer.Write(entry.Name);
    }

    public static ServiceEntry ReadEntry(BinaryReader reader) =>
        new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte(), reader.ReadInt64(), reader.ReadUInt32(), reader.ReadString());

    public static void Write(BinaryWriter writer, in FileChange change)
    {
        writer.Write((byte)change.Kind);
        writer.Write(change.Path);
        writer.Write(change.OldPath ?? "");
    }

    public static FileChange ReadChange(BinaryReader reader)
    {
        var kind = (ChangeKind)reader.ReadByte();
        var path = reader.ReadString();
        var old = reader.ReadString();
        return new FileChange(kind, path, old.Length > 0 ? old : null);
    }

    /// <summary>Seconds since 1980 (the index's date unit) from a FILETIME.</summary>
    public static uint Seconds(long fileTime)
    {
        const long From1601To1980 = 119600064000000000; // FILETIME of 1980-01-01 UTC
        var seconds = (fileTime - From1601To1980) / 10000000;
        return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
    }
}
