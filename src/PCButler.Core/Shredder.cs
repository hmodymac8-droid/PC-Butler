// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Security.Cryptography;

namespace PCButler.Core;

/// <summary>
/// Deep delete: a file's contents are overwritten with random data before it is deleted,
/// so an undelete tool finds nothing to bring back. It does not go to the Recycle Bin and
/// cannot be undone.
///
/// What this can and cannot promise: on a hard disk the old contents are gone. On an SSD
/// the drive itself decides where data is written, so a copy of the old contents can
/// survive in cells the drive has not reused yet; ordinary recovery software can't read
/// those, a forensic lab possibly can. Copies made elsewhere (backups, cloud sync,
/// shadow copies) are not touched.
/// </summary>
public static class Shredder
{
    private const int Chunk = 1024 * 1024;

    /// <summary>Folders that are never shredded, whatever is asked: Windows, the programs, the user's whole profile, a whole drive.</summary>
    public static bool IsProtected(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length <= 2 || Path.GetPathRoot(full)?.TrimEnd('\\') == full)
            return true;
        string[] guarded =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            AppInfo.DataDir, AppContext.BaseDirectory,
        ];
        // The folder itself, or anything that contains it. Inside Windows and the program folders nothing at all.
        return guarded.Where(folder => folder.Length > 0).Select(folder => folder.TrimEnd('\\')).Any(folder =>
                   full.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || folder.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
               || guarded.Take(3).Where(folder => folder.Length > 0).Any(folder =>
                   full.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Total size and number of files in what was chosen, for the confirmation.</summary>
    public static (int Files, long Size) Measure(IEnumerable<string> paths)
    {
        var files = 0;
        long size = 0;
        foreach (var file in paths.SelectMany(FilesIn))
        {
            try
            {
                size += new FileInfo(file).Length;
                files++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (files, size);
    }

    private static IEnumerable<string> FilesIn(string path)
    {
        if (File.Exists(path))
            return [path];
        if (!Directory.Exists(path))
            return [];
        try
        {
            // Links to other folders are removed as links; what they point at is not followed.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(path, "*", options).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Shred files and whole folders. progress(bytes done, bytes in all). Returns how many
    /// files were shredded and what could not be. Each item is logged in History as erased.
    /// </summary>
    public static (int Count, List<string> Errors) Shred(IReadOnlyList<string> paths, Action<long, long>? progress = null,
                                                         CancellationToken cancel = default)
    {
        var errors = new List<string>();
        var logged = new List<List<string>>();
        var count = 0;
        long done = 0;
        var total = Measure(paths).Size;
        var buffer = new byte[Chunk];
        foreach (var path in paths)
        {
            if (cancel.IsCancellationRequested)
                break;
            if (IsProtected(path))
            {
                errors.Add($"{path}: {Tr.T("this folder is protected and is never shredded")}");
                continue;
            }
            var failed = false;
            foreach (var file in FilesIn(path))
            {
                if (cancel.IsCancellationRequested)
                    break;
                try
                {
                    ShredFile(file, buffer, written =>
                    {
                        done += written;
                        progress?.Invoke(done, total);
                    }, cancel);
                    count++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                    errors.Add($"{Path.GetFileName(file)}: {error.Message}");
                }
            }
            if (cancel.IsCancellationRequested)
                break;
            try
            {
                if (Directory.Exists(path) && !failed)
                    Directory.Delete(path, recursive: true); // only empty folders and links are left by now
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {error.Message}");
            }
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                logged.Add([path, "", "erased"]);
                Log.Info($"Shredded {path}");
            }
        }
        if (logged.Count > 0)
            History.AddRun(logged);
        return (count, errors);
    }

    private static void ShredFile(string file, byte[] buffer, Action<int> written, CancellationToken cancel)
    {
        var attributes = File.GetAttributes(file);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var left = stream.Length;
            while (left > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var size = (int)Math.Min(left, buffer.Length);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, size));
                stream.Write(buffer, 0, size);
                left -= size;
                written(size);
            }
            stream.Flush(flushToDisk: true);
        }
        // The name and the size tell something too: both are wiped from the folder's entry before it goes.
        var blank = Path.Combine(Path.GetDirectoryName(file)!, Guid.NewGuid().ToString("N"));
        File.Move(file, blank);
        using (var stream = new FileStream(blank, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(0);
        File.Delete(blank);
    }
}

/// <summary>
/// Files waiting to go into the vault, handed over by File Explorer's right-click entry
/// or the drop zone. Only the main window adds to the vault (it holds the unlocked vault
/// and asks for the password), so other places leave the paths here and open the window.
/// </summary>
public static class VaultInbox
{
    private static string InboxFile => Path.Combine(AppInfo.DataDir, "vault-inbox.txt");

    public static void Add(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        File.AppendAllLines(InboxFile, paths.Select(Path.GetFullPath));
    }

    /// <summary>The waiting files that still exist; the list is emptied.</summary>
    public static List<string> Take()
    {
        try
        {
            if (!File.Exists(InboxFile))
                return [];
            var paths = File.ReadAllLines(InboxFile).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            File.Delete(InboxFile);
            return paths;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
