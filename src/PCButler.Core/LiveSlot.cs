// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace PCButler.Core;

/// <summary>
/// A small piece of shared memory the helper leaves its latest readings in for the main
/// program: temperatures every few seconds, frame counts every second. Nothing of it is
/// ever written to the disk, unlike the files these readings used to go through.
///
/// The main program makes each slot before it starts the helper: what an administrator's
/// program makes, the ordinary one could not open. Slots are named after the main program's
/// process, so two copies of PC Butler on one PC never read each other's.
/// </summary>
public static class LiveSlot
{
    private const int Size = 64 * 1024;
    // The map is kept with its view: once its last handle is closed (the map collected), Windows forgets the name,
    // and the helper would make a new slot of its own that this program can't open.
    private static readonly ConcurrentDictionary<string, (MemoryMappedFile Map, MemoryMappedViewAccessor View)> Open = new();

    /// <summary>The main program's process id: this process, or in the helper the program that started it.</summary>
    public static int Owner { get; set; } = Environment.ProcessId;

    public static string Name(string what) => $"PCButler-{what}-{Owner}";

    /// <summary>Make the slot now (or open it), so the helper finds it. False when Windows refuses.</summary>
    public static bool Prepare(string name) => Get(name) is not null;

    /// <summary>Replace the slot's text. A reader in the middle of it waits for the next whole one.</summary>
    public static bool Write(string name, string text)
    {
        if (Get(name) is not { } view)
            return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > Size - 8)
            return false;
        lock (view)
        {
            // An odd number while writing, the next even one when done: a reader that sees the number change reads again.
            var turn = view.ReadInt32(0);
            view.Write(0, turn | 1);
            view.Write(4, bytes.Length);
            view.WriteArray(8, bytes, 0, bytes.Length);
            view.Write(0, (turn | 1) + 1);
        }
        return true;
    }

    /// <summary>The slot's text, or null when nothing was written yet or it can't be opened.</summary>
    public static string? Read(string name)
    {
        if (Get(name) is not { } view)
            return null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var turn = view.ReadInt32(0);
            if (turn == 0)
                return null;
            if ((turn & 1) == 1)
            {
                Thread.Yield();
                continue;
            }
            var length = view.ReadInt32(4);
            if (length is < 0 or > Size - 8)
                return null;
            var bytes = new byte[length];
            view.ReadArray(8, bytes, 0, length);
            if (view.ReadInt32(0) == turn)
                return Encoding.UTF8.GetString(bytes);
        }
        return null;
    }

    private static MemoryMappedViewAccessor? Get(string name)
    {
        if (Open.TryGetValue(name, out var slot))
            return slot.View;
        try
        {
            // Kept open for the life of the program: shared memory lasts while someone holds it.
            var map = MemoryMappedFile.CreateOrOpen(name, Size);
            slot = (map, map.CreateViewAccessor());
            if (Open.TryAdd(name, slot))
                return slot.View;
            slot.View.Dispose();
            map.Dispose();
            return Open[name].View;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
