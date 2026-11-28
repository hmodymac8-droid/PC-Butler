// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace PCButler.Core.Search;

/// <summary>
/// The optional search service, PCButlerSearch.exe (src/PCButler.SearchService, in Rust). It
/// runs as LocalSystem, reads the NTFS file table and change journal, and answers only the user
/// who installed it, over the pipe \\.\pipe\PCButlerSearch. Its protocol is described in that
/// project's protocol.rs; the two sides change together, with ProtocolVersion.
///
/// Everything here is optional: when the service is not installed, not running, of another
/// protocol version, or the drive is not NTFS, search walks the folders as before.
/// </summary>
public static class SearchService
{
    public const string PipeName = "PCButlerSearch";
    public const string ServiceName = "PCButlerSearch";
    public const ushort ProtocolVersion = 1;
    internal const uint Magic = 0x53424350; // "PCBS"

    // Frame types.
    internal const byte Hello = 1, Subscribe = 2;
    internal const byte HelloOk = 0x81, Error = 0x82, Entries = 0x83, End = 0x84, Changes = 0x85, Following = 0x86, Rescan = 0x87, NoJournal = 0x88;
    internal const byte ModeEntries = 1, ModeFollow = 2;
    internal const byte Touch = 1, Renamed = 2;

    /// <summary>True when the service is installed (Windows knows it). Needs no special rights.</summary>
    public static bool Installed()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when the service is installed, running and speaks this protocol.</summary>
    public static bool Available()
    {
        if (!OperatingSystem.IsWindows() || !Installed())
            return false;
        using var connection = ServiceConnection.Open(1500);
        return connection is not null;
    }

    /// <summary>A whole fixed NTFS drive ("C:\"): what the service can read. Folders and other drives are walked.</summary>
    public static bool Serves(string root)
    {
        if (root.Length != 3 || root[1] != ':' || root[2] != '\\' || !char.IsAsciiLetter(root[0]))
            return false;
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A FILETIME as the index's seconds (0 = unknown).</summary>
    internal static uint Seconds(long fileTime) =>
        fileTime <= 0 || fileTime > 2650467743999999999 ? 0 : SearchIndex.ToSeconds(DateTime.FromFileTimeUtc(fileTime));
}

/// <summary>Where a drive's change journal was read up to: saved with the index, so the next start catches up from there.</summary>
public readonly record struct JournalPosition(ulong Journal, long Usn)
{
    public override string ToString() => $"{Journal}:{Usn}";

    public static JournalPosition? Parse(string? text)
    {
        var parts = (text ?? "").Split(':');
        return parts.Length == 2 && ulong.TryParse(parts[0], out var journal) && long.TryParse(parts[1], out var usn) && journal != 0
            ? new JournalPosition(journal, usn) : null;
    }
}

/// <summary>The journal positions of the drives the service follows, in journal.json beside the index file.</summary>
public static class JournalStore
{
    private static string FileFor(string indexFile) => Path.Combine(Path.GetDirectoryName(indexFile) ?? "", "journal.json");

    public static Dictionary<string, JournalPosition> Load(string indexFile)
    {
        var positions = new Dictionary<string, JournalPosition>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = FileFor(indexFile);
            if (!File.Exists(file))
                return positions;
            foreach (var (root, text) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [])
                if (JournalPosition.Parse(text) is { } position)
                    positions[root] = position;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return positions;
    }

    public static void Save(string indexFile, IReadOnlyDictionary<string, JournalPosition> positions)
    {
        var file = FileFor(indexFile);
        if (positions.Count == 0)
        {
            File.Delete(file);
            return;
        }
        var temp = file + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(positions.ToDictionary(pair => pair.Key, pair => pair.Value.ToString())));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>One connection to the service, after the handshake.</summary>
internal sealed class ServiceConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private byte[] buffer = new byte[1 << 16];

    private ServiceConnection(NamedPipeClientStream pipe) => this.pipe = pipe;

    /// <summary>Connect and agree on the protocol; null when the service is not there or speaks another version.</summary>
    public static ServiceConnection? Open(int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", SearchService.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeoutMs);
            var connection = new ServiceConnection(pipe);
            Span<byte> hello = stackalloc byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(hello, SearchService.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(hello[4..], SearchService.ProtocolVersion);
            connection.Send(SearchService.Hello, hello);
            var type = connection.Read(out var body);
            if (type == SearchService.HelloOk && body.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(body) == SearchService.Magic
                && BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) == SearchService.ProtocolVersion)
                return connection;
            if (type == SearchService.Error)
                Log.Warning($"Search service: {ErrorText(body)}");
            connection.Dispose();
            return null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            pipe.Dispose();
            return null;
        }
    }

    /// <summary>Ask for one drive: its entries (mode 1) or its changes from a position (mode 2).</summary>
    public void Subscribe(char letter, byte mode, JournalPosition? from)
    {
        Span<byte> request = stackalloc byte[19];
        BinaryPrimitives.WriteUInt16LittleEndian(request, char.ToUpperInvariant(letter));
        request[2] = mode;
        BinaryPrimitives.WriteUInt64LittleEndian(request[3..], from?.Journal ?? 0);
        BinaryPrimitives.WriteInt64LittleEndian(request[11..], from?.Usn ?? 0);
        Send(SearchService.Subscribe, request);
    }

    private void Send(byte type, ReadOnlySpan<byte> fields)
    {
        var frame = new byte[5 + fields.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + fields.Length));
        frame[4] = type;
        fields.CopyTo(frame.AsSpan(5));
        pipe.Write(frame);
        pipe.Flush();
    }

    /// <summary>The next frame: its type and fields (valid until the next Read).</summary>
    public byte Read(out ReadOnlySpan<byte> body)
    {
        Span<byte> head = stackalloc byte[4];
        pipe.ReadExactly(head);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (length < 1 || length > 64 << 20)
            throw new IOException("Search service: a frame of the wrong size");
        if (buffer.Length < length)
            buffer = new byte[Math.Max(length, buffer.Length * 2)];
        pipe.ReadExactly(buffer.AsSpan(0, length));
        body = buffer.AsSpan(1, length - 1);
        return buffer[0];
    }

    public static string ErrorText(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            return "error";
        var reader = new FrameReader(body);
        var code = reader.UInt16();
        return $"{reader.Text()} ({code})";
    }

    public void Dispose() => pipe.Dispose();
}

/// <summary>Reading a frame's fields in order.</summary>
internal ref struct FrameReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> bytes = bytes;
    private int at;

    public bool More => at < bytes.Length;
    public byte Byte() => bytes[at++];

    public ushort UInt16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        at += 2;
        return value;
    }

    public uint UInt32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return value;
    }

    public ulong UInt64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
        at += 8;
        return value;
    }

    public long Int64()
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
        at += 8;
        return value;
    }

    /// <summary>A name or path, without making a string.</summary>
    public ReadOnlySpan<char> Chars()
    {
        var count = UInt16();
        var text = MemoryMarshal.Cast<byte, char>(bytes.Slice(at, count * 2));
        at += count * 2;
        return text;
    }

    public string Text() => new(Chars());
}

/// <summary>
/// Indexing a whole NTFS drive through the service: its file table is read in seconds, where a
/// folder walk takes minutes. Folders come before what is in them, and the names in one folder
/// together, so each entry is added at once; the same rules as a walk leave things out.
/// </summary>
public static class MftServiceSource
{
    private const uint DirectoryAttribute = 0x10;
    private const ulong TopRecord = 5;

    /// <summary>Read the drive into the index as a new root. Throws IOException when the service fails; nothing is half-added then that matters, the caller drops the index.</summary>
    public static int Scan(SearchIndex into, string root, ScanRules rules, CancellationToken cancel = default, Action<int>? progress = null)
    {
        using var connection = ServiceConnection.Open(3000) ?? throw new IOException("the search service is not reachable");
        connection.Subscribe(root[0], SearchService.ModeEntries, null);
        var rootRow = into.AddRoot(root);
        // Folders found but not yet entered: their row and full path.
        var folders = new Dictionary<ulong, (int Row, string Path)> { [TopRecord] = (rootRow, root) };
        var current = ulong.MaxValue;
        var (currentRow, currentPath, known) = (-1, "", false);
        var added = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var type = connection.Read(out var body);
            if (type == SearchService.End)
                break;
            if (type == SearchService.Error)
                throw new IOException(ServiceConnection.ErrorText(body));
            if (type != SearchService.Entries)
                throw new IOException($"Search service: unexpected frame {type}");
            var reader = new FrameReader(body);
            var count = reader.UInt32();
            for (var number = 0u; number < count; number++)
            {
                var record = reader.UInt64();
                var parent = reader.UInt64();
                var attributes = reader.UInt32();
                var size = reader.Int64();
                var modified = reader.Int64();
                var name = reader.Chars();
                if (parent != current)
                {
                    current = parent;
                    known = folders.Remove(parent, out var folder);
                    (currentRow, currentPath) = known ? folder : (-1, "");
                }
                if (!known || rules.Skip(currentPath, name, (FileAttributes)attributes))
                    continue;
                var directory = (attributes & DirectoryAttribute) != 0;
                var flags = (byte)((directory ? SearchIndex.Folder : 0)
                                   | ((attributes & (uint)FileAttributes.Hidden) != 0 ? SearchIndex.Hidden : 0)
                                   | ((attributes & (uint)FileAttributes.System) != 0 ? SearchIndex.SystemFile : 0));
                var row = into.Add(currentRow, name, flags, directory ? -1 : size, SearchService.Seconds(modified));
                if (directory)
                    folders[record] = (row, SearchIndex.Join(currentPath, name));
                if (++added >= 2000)
                {
                    progress?.Invoke(added);
                    added = 0;
                }
            }
        }
        if (added > 0)
            progress?.Invoke(added);
        return rootRow;
    }
}

/// <summary>
/// Follows one drive's change journal through the service, from a saved position (catching up
/// with what changed while PC Butler was closed) or from now, and hands each change to the
/// search engine as a FileChange: a path to look at again, or a rename. After each batch comes
/// a ChangeKind.Journal mark with the position, which the engine saves with the index.
/// </summary>
public sealed class ServiceFollower : IDisposable
{
    private readonly Action<FileChange> changed;
    private readonly Action<string, bool> failed;
    private readonly JournalPosition? from;
    private ServiceConnection? connection;
    private volatile bool disposed;

    public string Root { get; }

    /// <param name="failed">The service could not follow the drive (root, and whether it had started following): the engine watches the folders itself instead.</param>
    public ServiceFollower(string root, JournalPosition? from, Action<FileChange> changed, Action<string, bool> failed)
    {
        Root = root;
        this.from = from;
        this.changed = changed;
        this.failed = failed;
        new Thread(Run) { IsBackground = true, Name = "PC Butler search journal " + root, Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>The mark that carries a drive's journal position through the change queue.</summary>
    public static FileChange Mark(string root, JournalPosition position) => new(ChangeKind.Journal, root, position.ToString());

    private void Run()
    {
        var following = false;
        try
        {
            connection = ServiceConnection.Open(3000) ?? throw new IOException("the search service is not reachable");
            if (disposed)
                return;
            connection.Subscribe(Root[0], SearchService.ModeFollow, from);
            while (!disposed)
            {
                var type = connection.Read(out var body);
                var reader = new FrameReader(body);
                switch (type)
                {
                    case SearchService.Following:
                        following = true;
                        changed(Mark(Root, new JournalPosition(reader.UInt64(), reader.Int64())));
                        break;
                    case SearchService.Changes:
                        var count = reader.UInt32();
                        for (var number = 0u; number < count; number++)
                        {
                            var kind = reader.Byte();
                            var path = reader.Text();
                            if (kind == SearchService.Renamed)
                                changed(new FileChange(ChangeKind.Renamed, path, reader.Text()));
                            else
                                changed(new FileChange(ChangeKind.Created, path));
                        }
                        changed(Mark(Root, new JournalPosition(reader.UInt64(), reader.Int64())));
                        break;
                    case SearchService.Rescan:
                        Log.Info($"Search service: changes on {Root} were missed; indexing it again");
                        changed(new FileChange(ChangeKind.Lost, Root));
                        break;
                    case SearchService.NoJournal:
                        Log.Info($"Search service: {Root} keeps no change journal; watching its folders instead");
                        failed(Root, following);
                        return;
                    case SearchService.Error:
                        throw new IOException(ServiceConnection.ErrorText(body));
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            if (disposed)
                return;
            Log.Warning($"Search service: following {Root} stopped ({error.Message}); watching its folders instead");
            failed(Root, following);
        }
    }

    public void Dispose()
    {
        disposed = true;
        connection?.Dispose();
    }
}
