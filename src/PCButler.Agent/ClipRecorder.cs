// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using PCButler.Core;
using static PCButler.Agent.Native;

namespace PCButler.Agent;

/// <summary>
/// Clipboard history's recording: each copy (text, files, and pictures when that setting is on)
/// is added to the list, read straight from Windows' clipboard.
/// </summary>
internal sealed class ClipRecorder : IDisposable
{
    private const uint CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIB = 8, CF_DIBV5 = 17;
    private readonly MessageLoop loop;
    private ClipboardSettings settings = new();
    private bool listening;

    public ClipRecorder(MessageLoop loop)
    {
        this.loop = loop;
        loop.Listen((message, _, _) =>
        {
            if (message != WM_CLIPBOARDUPDATE)
                return false;
            // The program that copied may still hold the clipboard for a moment.
            loop.After(TimeSpan.FromMilliseconds(60), Record);
            return true;
        });
    }

    public void Apply(ClipboardSettings wanted)
    {
        settings = wanted;
        ClipboardHistory.Apply(wanted);
        if (wanted.Enabled && !listening)
            listening = AddClipboardFormatListener(loop.Handle);
        else if (!wanted.Enabled && listening)
        {
            RemoveClipboardFormatListener(loop.Handle);
            listening = false;
        }
    }

    private void Record()
    {
        if (!settings.Enabled || Excluded())
            return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!OpenClipboard(loop.Handle))
            {
                Thread.Sleep(50); // another program has the clipboard open
                continue;
            }
            ClipEntry? entry;
            try
            {
                entry = Read();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
                Log.Warning($"A copy could not be read: {error.Message}");
                entry = null;
            }
            finally
            {
                CloseClipboard();
            }
            if (entry is not null)
                ClipboardHistory.Add(entry, settings.Limit);
            return;
        }
    }

    /// <summary>Password managers mark what they copy as not for clipboard history; that is respected.</summary>
    private static bool Excluded()
    {
        if (IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"))
            || IsClipboardFormatAvailable(RegisterClipboardFormat("Clipboard Viewer Ignore")))
            return true;
        var flag = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        if (!IsClipboardFormatAvailable(flag) || !OpenClipboard(IntPtr.Zero))
            return false;
        try
        {
            var bytes = Bytes(GetClipboardData(flag));
            return bytes is { Length: >= 4 } && BitConverter.ToInt32(bytes, 0) == 0;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private ClipEntry? Read()
    {
        if (IsClipboardFormatAvailable(CF_HDROP))
        {
            var drop = GetClipboardData(CF_HDROP);
            var files = new List<string>();
            var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(drop, index, null, 0);
                var name = new char[length + 1];
                DragQueryFile(drop, index, name, length + 1);
                files.Add(new string(name, 0, (int)length));
            }
            return files.Count > 0 ? new ClipEntry { Kind = "files", Files = files } : null;
        }
        if (IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            var memory = GetClipboardData(CF_UNICODETEXT);
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
                return null;
            string text;
            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? "";
            }
            finally
            {
                GlobalUnlock(memory);
            }
            return string.IsNullOrWhiteSpace(text) || text.Length > ClipboardHistory.MaxText ? null : new ClipEntry { Kind = "text", Text = text };
        }
        if (settings.Pictures && (IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_DIB)))
        {
            var dib = Bytes(GetClipboardData(IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : CF_DIB));
            if (dib is null || Picture.FromDib(dib) is not { } picture)
                return null;
            var entry = new ClipEntry { Kind = "image", Width = picture.Width, Height = picture.Height };
            entry.Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture.Bgra));
            if (ClipboardHistory.Entries.Any(known => known.Kind == "image" && known.Hash == entry.Hash))
                return entry; // already kept: it just moves to the top
            Directory.CreateDirectory(ClipboardHistory.ImageFolder);
            File.WriteAllBytes(entry.ImageFile, picture.Png());
            return entry;
        }
        return null;
    }

    private static byte[]? Bytes(IntPtr memory)
    {
        if (memory == IntPtr.Zero)
            return null;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            var bytes = new byte[(int)GlobalSize(memory)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    public void Dispose()
    {
        if (listening)
            RemoveClipboardFormatListener(loop.Handle);
        listening = false;
    }
}

/// <summary>A picture from the clipboard: its pixels (blue, green, red, alpha; top row first), and as a PNG file.</summary>
internal sealed record Picture(int Width, int Height, byte[] Bgra)
{
    /// <summary>A device-independent bitmap (CF_DIB or CF_DIBV5) of 24 or 32 bits per pixel; null for any other kind.</summary>
    public static Picture? FromDib(byte[] dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var colours = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        var topDown = height < 0;
        height = Math.Abs(height);
        if (width <= 0 || height <= 0 || width > 20000 || height > 20000 || bits is not (24 or 32) || compression is not (0 or 3))
            return null;
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + colours * 4;
        var stride = ((width * bits + 31) / 32) * 4;
        if (offset + (long)stride * height > dib.Length)
            return null;
        var pixels = new byte[width * height * 4];
        var anyAlpha = false;
        for (var row = 0; row < height; row++)
        {
            var source = offset + (topDown ? row : height - 1 - row) * stride;
            for (var x = 0; x < width; x++)
            {
                var from = source + x * (bits / 8);
                var to = (row * width + x) * 4;
                pixels[to] = dib[from];
                pixels[to + 1] = dib[from + 1];
                pixels[to + 2] = dib[from + 2];
                pixels[to + 3] = bits == 32 ? dib[from + 3] : (byte)255;
                anyAlpha |= bits == 32 && dib[from + 3] != 0;
            }
        }
        // Most programs leave the fourth byte at zero: that means opaque, not invisible.
        if (bits == 32 && !anyAlpha)
            for (var index = 3; index < pixels.Length; index += 4)
                pixels[index] = 255;
        return new Picture(width, height, pixels);
    }

    /// <summary>The picture as a PNG file's bytes.</summary>
    public byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // red, green, blue, alpha
        Chunk(output, "IHDR", header);
        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var line = new byte[Width * 4 + 1];
                for (var row = 0; row < Height; row++)
                {
                    line[0] = 0; // no filter
                    for (var x = 0; x < Width; x++)
                    {
                        var from = (row * Width + x) * 4;
                        line[1 + x * 4] = Bgra[from + 2];
                        line[2 + x * 4] = Bgra[from + 1];
                        line[3 + x * 4] = Bgra[from];
                        line[4 + x * 4] = Bgra[from + 3];
                    }
                    zlib.Write(line);
                }
            }
            Chunk(output, "IDAT", packed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        output.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
