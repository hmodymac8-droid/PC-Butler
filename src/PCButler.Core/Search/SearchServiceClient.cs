// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO.Pipes;
using System.Text;

namespace PCButler.Core.Search;

/// <summary>
/// Talking to the Search service (see SearchPipe for the conversation). Each request opens its
/// own connection; the pipe only lets in the user the service serves.
/// </summary>
public static class SearchServiceClient
{
    /// <summary>True when the service answers, with the protocol this program speaks.</summary>
    public static bool Available()
    {
        try
        {
            return Ask("hello", [], reader => reader.ReadInt32(), 400) == SearchPipe.Version;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The volumes the service can read, as ("C:\", "NTFS").</summary>
    public static List<(string Drive, string FileSystem)> Volumes() =>
        Ask("volumes", [], reader =>
        {
            var list = new List<(string, string)>();
            for (var count = reader.ReadInt32(); count > 0; count--)
                list.Add((reader.ReadString(), reader.ReadString()));
            return list;
        });

    /// <summary>Every name on a volume, folders before what is in them, and where its journal stood when the reading began.</summary>
    public static (ulong Journal, long Next, List<ServiceEntry> Entries) Scan(string drive, CancellationToken cancel = default) =>
        Ask("scan", [drive], reader =>
        {
            Expect(reader);
            var journal = reader.ReadUInt64();
            var next = reader.ReadInt64();
            var count = reader.ReadInt32();
            var entries = new List<ServiceEntry>(count);
            for (var index = 0; index < count; index++)
            {
                if ((index & 0xFFFF) == 0)
                    cancel.ThrowIfCancellationRequested();
                entries.Add(SearchPipe.ReadEntry(reader));
            }
            return (journal, next, entries);
        }, 10000);

    /// <summary>What changed on a volume since next. Status WalkAgain: the journal no longer reaches back that far, or was made again.</summary>
    public static (byte Status, long Next, List<FileChange> Changes) Changes(string drive, ulong journal, long next) =>
        Ask("changes", [drive, journal.ToString(), next.ToString()], reader =>
        {
            var status = reader.ReadByte();
            if (status == SearchPipe.Failed)
                throw new IOException(reader.ReadString());
            var after = reader.ReadInt64();
            var changes = new List<FileChange>();
            for (var count = reader.ReadInt32(); count > 0; count--)
                changes.Add(SearchPipe.ReadChange(reader));
            return (status, after, changes);
        });

    private static void Expect(BinaryReader reader)
    {
        if (reader.ReadByte() != SearchPipe.Ok)
            throw new IOException(reader.ReadString());
    }

    private static T Ask<T>(string command, string[] arguments, Func<BinaryReader, T> read, int connectMs = 2000)
    {
        using var pipe = new NamedPipeClientStream(".", SearchPipe.PipeName, PipeDirection.InOut);
        pipe.Connect(connectMs);
        using var buffered = new BufferedStream(pipe, 1 << 16);
        using var writer = new BinaryWriter(buffered, Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(buffered, Encoding.UTF8, leaveOpen: true);
        writer.Write(command);
        writer.Write(arguments.Length);
        foreach (var argument in arguments)
            writer.Write(argument);
        writer.Flush();
        return read(reader);
    }

    /// <summary>
    /// Add a volume's names to an index, under a new root for the drive. Entries come folders
    /// first, so each one's folder is already in; what the rules leave out is skipped, with
    /// everything under it.
    /// </summary>
    public static void Build(SearchIndex into, string drive, IReadOnlyList<ServiceEntry> entries, ScanRules rules, CancellationToken cancel = default)
    {
        var folders = new Dictionary<long, int> { [MftParser.RootRecord] = into.AddRoot(drive) };
        for (var at = 0; at < entries.Count; at++)
        {
            if ((at & 0xFFFF) == 0)
                cancel.ThrowIfCancellationRequested();
            var entry = entries[at];
            if (!folders.TryGetValue(entry.Parent, out var parent))
                continue;
            var attributes = ((entry.Flags & SearchIndex.Folder) != 0 ? FileAttributes.Directory : 0)
                             | ((entry.Flags & SearchIndex.Hidden) != 0 ? FileAttributes.Hidden : 0)
                             | ((entry.Flags & SearchIndex.SystemFile) != 0 ? FileAttributes.System : 0);
            if (rules.Skip(into.FullPath(parent), entry.Name, attributes))
                continue;
            var row = into.Add(parent, entry.Name, entry.Flags, entry.Size, entry.Modified);
            if ((entry.Flags & SearchIndex.Folder) != 0)
                folders[entry.Record] = row;
        }
    }
}
