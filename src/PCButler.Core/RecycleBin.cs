// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Text;

namespace PCButler.Core;

/// <summary>Deleting always goes through the Recycle Bin, so it can be brought back.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT operation);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    /// <summary>Send a file to the Recycle Bin (recoverable), not a permanent delete.</summary>
    public static void Send(string path)
    {
        // The list of names must end with two nulls.
        var source = Marshal.StringToHGlobalUni(path + "\0");
        try
        {
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = source,
                fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0 || File.Exists(path))
                throw new IOException($"Windows could not move it to the Recycle Bin (code {result})");
        }
        finally
        {
            Marshal.FreeHGlobal(source);
        }
    }

    /// <summary>
    /// Bring back the most recently recycled file that used to be at <paramref name="original"/>.
    /// The bin keeps each file as a pair: $R&lt;id&gt; holds the content and $I&lt;id&gt; records
    /// the original path and deletion time.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint size);

    /// <summary>
    /// The path with its folders spelled out in full ("C:\Users\Administrator", not
    /// "C:\Users\ADMINI~1"), as the Recycle Bin records it. The file itself may be gone, so
    /// only its folder is looked up.
    /// </summary>
    private static string LongPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(full);
            if (folder is null || !full.Contains('~'))
                return full;
            var buffer = new StringBuilder(1024);
            var length = GetLongPathName(folder, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? Path.Combine(buffer.ToString(), Path.GetFileName(full)) : full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static void Restore(string original)
    {
        if (File.Exists(original))
            throw new IOException("a file with that name is already there");
        (long DeletedAt, string Record)? newest = null;
        var wanted = Sorter.Norm(LongPath(original));
        string[] userBins;
        try
        {
            userBins = Directory.GetDirectories(Path.Combine(Path.GetPathRoot(Path.GetFullPath(original))!, "$Recycle.Bin"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            userBins = [];
        }
        foreach (var userBin in userBins)
        {
            string[] records;
            try
            {
                records = Directory.GetFiles(userBin, "$I*");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue; // another user's bin
            }
            foreach (var record in records)
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(record);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (data.Length < 28)
                    continue;
                var deletedAt = BitConverter.ToInt64(data, 16);
                string name;
                if (BitConverter.ToInt64(data, 0) == 2)
                {
                    // Windows 10+: length-prefixed path.
                    var length = Math.Min(BitConverter.ToInt32(data, 24) * 2, data.Length - 28);
                    name = Encoding.Unicode.GetString(data, 28, Math.Max(length, 0));
                }
                else
                    name = Encoding.Unicode.GetString(data, 24, data.Length - 24);
                name = name.Split('\0')[0];
                if (Sorter.Norm(LongPath(name)) == wanted && (newest is null || deletedAt > newest.Value.DeletedAt))
                    newest = (deletedAt, record);
            }
        }
        if (newest is null)
            throw new IOException("it is no longer in the Recycle Bin");
        var info = newest.Value.Record;
        var content = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Move(content, original);
        try
        {
            File.Delete(info);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
