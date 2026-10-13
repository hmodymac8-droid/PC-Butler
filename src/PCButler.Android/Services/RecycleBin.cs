// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.Json;

namespace PCButler.Core;

/// <summary>
/// The phone's Recycle Bin: Android has none an app can use for any file, so PC Butler keeps
/// its own, a folder in the app's storage. What Duplicates or Storage delete goes there (a
/// copy is kept, then the file is removed from its folder), and is put back into the same
/// folder of the ones the user chose.
/// </summary>
public static class RecycleBin
{
    private static readonly object Gate = new();
    private static string Folder => Path.Combine(AppInfo.DataDir, "Trash");
    private static string Index => Path.Combine(Folder, "index.json");

    /// <summary>Move a file of a chosen folder into the bin (recoverable), not a permanent delete.</summary>
    public static void Send(PCButler.Android.Doc file)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            var kept = Path.Combine(Folder, $"{DateTime.Now:yyyyMMddHHmmssfff}-{file.Name}");
            try
            {
                using (var input = PCButler.Android.Folders.OpenRead(file))
                using (var output = File.Create(kept))
                    input.CopyTo(output);
                if (!PCButler.Android.Folders.Delete(file.Uri))
                    throw new IOException(Tr.T("the folder's app doesn't let it be removed"));
            }
            catch (Exception error) when (error is IOException or Java.Lang.Exception)
            {
                File.Delete(kept);
                throw error as IOException ?? new IOException(error.Message);
            }
            var index = Read();
            index.Add(new Entry(file.Path, kept, DateTime.Now, file.Tree.ToString(), file.ParentId));
            File.WriteAllText(Index, JsonSerializer.Serialize(index));
        }
    }

    /// <summary>Move a file into the bin (recoverable), not a permanent delete.</summary>
    public static void Send(string path)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            var kept = Path.Combine(Folder, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Path.GetFileName(path)}");
            File.Move(path, kept);
            var index = Read();
            index.Add(new Entry(path, kept, DateTime.Now));
            File.WriteAllText(Index, JsonSerializer.Serialize(index));
        }
    }

    /// <summary>Put the most recently binned file of that name back where it was.</summary>
    public static void Restore(string original)
    {
        lock (Gate)
        {
            var index = Read();
            var entry = index.Where(item => string.Equals(item.Original, original, StringComparison.Ordinal) && File.Exists(item.Kept))
                             .OrderByDescending(item => item.At).FirstOrDefault()
                        ?? throw new IOException("it is no longer in the Recycle Bin");
            if (entry.Tree is { Length: > 0 } tree && entry.Parent is { Length: > 0 } parent)
            {
                // Back into the folder it came from, through the folder the user chose.
                var folder = PCButler.Android.Folders.ById(global::Android.Net.Uri.Parse(tree)!, parent, Path.GetDirectoryName(original) ?? "");
                try
                {
                    PCButler.Android.Folders.CopyInto(folder, Path.GetFileName(original), File.OpenRead(entry.Kept));
                }
                catch (Exception error) when (error is Java.Lang.Exception)
                {
                    throw new IOException(Tr.T("its folder is no longer there, or no longer chosen"));
                }
                File.Delete(entry.Kept);
            }
            else
            {
                if (File.Exists(original))
                    throw new IOException("a file with that name is already there");
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Move(entry.Kept, original);
            }
            index.Remove(entry);
            File.WriteAllText(Index, JsonSerializer.Serialize(index));
        }
    }

    /// <summary>Tree and Parent: where in the folders the user chose it was (empty for older entries, which have a plain path).</summary>
    public sealed record Entry(string Original, string Kept, DateTime At, string? Tree = null, string? Parent = null)
    {
        public long Size => File.Exists(Kept) ? new FileInfo(Kept).Length : 0;
    }

    /// <summary>What is in the bin, newest first.</summary>
    public static List<Entry> Items()
    {
        lock (Gate)
            return Read().Where(entry => File.Exists(entry.Kept)).OrderByDescending(entry => entry.At).ToList();
    }

    /// <summary>Delete everything in the bin for good. Returns how many files went.</summary>
    public static int Empty()
    {
        lock (Gate)
        {
            var gone = 0;
            foreach (var entry in Read())
            {
                try
                {
                    File.Delete(entry.Kept);
                    gone++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
            File.WriteAllText(Index, "[]");
            return gone;
        }
    }

    private static List<Entry> Read()
    {
        try
        {
            return File.Exists(Index) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Index)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }
}
