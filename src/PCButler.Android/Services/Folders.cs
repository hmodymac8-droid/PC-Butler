// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.App;
using Android.Content;
using Android.Provider;
using PCButler.Core;
using AUri = Android.Net.Uri;

namespace PCButler.Android;

/// <summary>A file or folder in a folder the user gave PC Butler (Android's folder picker).</summary>
/// <param name="Path">How people know it: "DCIM/Camera/a.jpg". Also the key the pages use for it.</param>
public sealed record Doc(AUri Tree, string Id, string ParentId, string Name, string Path, long Size, DateTime Modified, bool IsFolder, int Flags)
{
    public AUri Uri => DocumentsContract.BuildDocumentUriUsingTree(Tree, Id)!;
    public AUri ParentUri => DocumentsContract.BuildDocumentUriUsingTree(Tree, ParentId)!;
    public bool CanDelete => (Flags & 4) != 0;   // FLAG_SUPPORTS_DELETE
    public bool CanMove => (Flags & 256) != 0;   // FLAG_SUPPORTS_MOVE
    public bool CanRename => (Flags & 64) != 0;  // FLAG_SUPPORTS_RENAME
}

/// <summary>
/// The phone's folders, through Android's own folder picker: PC Butler sees only the folders
/// the user chose, and keeps that access across restarts. Each page keeps its own list
/// ("sorter", "duplicates", "storage"). Android doesn't offer the Download folder itself or
/// the whole phone in that picker; any other folder, or one inside Download, can be chosen.
/// </summary>
public static class Folders
{
    private const string ExternalStorage = "com.android.externalstorage.documents";

    private static ContentResolver Resolver => global::Android.App.Application.Context.ContentResolver!;

    /// <summary>The folders chosen for that page, still allowed.</summary>
    public static List<AUri> For(string page)
    {
        var allowed = Resolver.PersistedUriPermissions.Where(grant => grant.IsReadPermission).Select(grant => grant.Uri!.ToString()).ToHashSet();
        return Saved(page).Where(allowed.Contains).Select(text => AUri.Parse(text)!).ToList();
    }

    private static List<string> Saved(string page) =>
        Preferences.Default.Get("folders." + page, "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void Save(string page, List<string> list) => Preferences.Default.Set("folders." + page, string.Join("\n", list.Distinct()));

    /// <summary>Ask the user for a folder and add it to the page's list. False when nothing was chosen.</summary>
    public static async Task<bool> Add(string page)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        var answer = await Results.Start(intent);
        if (answer?.Data is not { } tree)
            return false;
        Resolver.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        var list = Saved(page);
        list.Add(tree.ToString()!);
        Save(page, list);
        return true;
    }

    /// <summary>Take a folder off the page's list; Android forgets the access once no page uses it.</summary>
    public static void Remove(string page, AUri tree)
    {
        var text = tree.ToString()!;
        Save(page, Saved(page).Where(item => item != text).ToList());
        if (new[] { "sorter", "duplicates", "storage" }.Any(other => Saved(other).Contains(text)))
            return;
        try
        {
            Resolver.ReleasePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
        }
    }

    /// <summary>The folder's name as people know it: "DCIM/Camera", or the name its app gives it.</summary>
    public static string Label(AUri tree)
    {
        var id = DocumentsContract.GetTreeDocumentId(tree) ?? "";
        if (tree.Authority == ExternalStorage && id.IndexOf(':') is var colon and >= 0)
        {
            var inside = id[(colon + 1)..];
            return inside.Length > 0 ? inside : Tr.T("Internal storage");
        }
        return Root(tree)?.Name ?? tree.LastPathSegment ?? "?";
    }

    /// <summary>The chosen folder itself, as a Doc.</summary>
    public static Doc? Root(AUri tree)
    {
        var id = DocumentsContract.GetTreeDocumentId(tree)!;
        var uri = DocumentsContract.BuildDocumentUriUsingTree(tree, id)!;
        try
        {
            using var cursor = Resolver.Query(uri, Columns, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return null;
            var doc = Read(cursor, tree, "", "");
            return doc with { Path = LabelOf(tree, doc.Name) };
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException)
        {
            return null; // the folder was removed, or the access withdrawn
        }
    }

    private static string LabelOf(AUri tree, string name)
    {
        var id = DocumentsContract.GetTreeDocumentId(tree) ?? "";
        return tree.Authority == ExternalStorage && id.IndexOf(':') is var colon and >= 0 && id[(colon + 1)..].Length > 0 ? id[(colon + 1)..] : name;
    }

    private static readonly string[] Columns =
    [
        DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize, DocumentsContract.Document.ColumnLastModified, DocumentsContract.Document.ColumnFlags,
    ];

    private static Doc Read(global::Android.Database.ICursor cursor, AUri tree, string parentId, string parentPath)
    {
        var name = cursor.GetString(1) ?? "";
        return new Doc(tree, cursor.GetString(0)!, parentId, name, parentPath.Length == 0 ? name : parentPath + "/" + name,
                       cursor.IsNull(3) ? 0 : cursor.GetLong(3),
                       cursor.IsNull(4) ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(4)).LocalDateTime,
                       cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir, cursor.IsNull(5) ? 0 : cursor.GetInt(5));
    }

    /// <summary>What a folder holds, folders and files. Hidden ones (".name") are left out.</summary>
    public static List<Doc> Children(Doc folder)
    {
        var list = new List<Doc>();
        var uri = DocumentsContract.BuildChildDocumentsUriUsingTree(folder.Tree, folder.Id)!;
        try
        {
            using var cursor = Resolver.Query(uri, Columns, null, null, null);
            while (cursor is not null && cursor.MoveToNext())
            {
                var doc = Read(cursor, folder.Tree, folder.Id, folder.Path);
                if (!doc.Name.StartsWith('.'))
                    list.Add(doc);
            }
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException)
        {
            Log.Warning($"A folder could not be read: {error.Message}");
        }
        return list;
    }

    /// <summary>Every file in the folder, at any depth.</summary>
    public static List<Doc> Files(Doc folder, CancellationToken cancel, Action<Doc>? progress = null)
    {
        var found = new List<Doc>();
        var waiting = new Stack<Doc>([folder]);
        while (waiting.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var current = waiting.Pop();
            progress?.Invoke(current);
            foreach (var child in Children(current))
            {
                if (child.IsFolder)
                    waiting.Push(child);
                else
                    found.Add(child);
            }
        }
        return found;
    }

    public static Stream OpenRead(Doc doc) => Resolver.OpenInputStream(doc.Uri) ?? throw new IOException(Tr.T("it can't be read"));

    /// <summary>A sub-folder by name, made when missing.</summary>
    public static Doc Folder(Doc parent, string name)
    {
        if (Children(parent).FirstOrDefault(child => child.IsFolder && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
            return found;
        var made = DocumentsContract.CreateDocument(Resolver, parent.Uri, DocumentsContract.Document.MimeTypeDir, name)
                   ?? throw new IOException(Tr.T("the folder {0} could not be made", name));
        return new Doc(parent.Tree, DocumentsContract.GetDocumentId(made)!, parent.Id, name, parent.Path + "/" + name, 0, DateTime.Now, true, 0);
    }

    /// <summary>Move a file into another folder of the same chosen folder; a name already there gets " (1)". Returns the file's new id.</summary>
    public static string Move(Doc file, Doc target)
    {
        var taken = Children(target).Select(child => child.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = file;
        if (taken.Contains(file.Name))
        {
            var renamed = DocumentsContract.RenameDocument(Resolver, file.Uri, Unique(file.Name, taken))
                          ?? throw new IOException(Tr.T("a file of that name is already there"));
            source = file with { Id = DocumentsContract.GetDocumentId(renamed)! };
        }
        if (source.CanMove)
        {
            var moved = DocumentsContract.MoveDocument(Resolver, source.Uri, source.ParentUri, target.Uri);
            if (moved is not null)
                return DocumentsContract.GetDocumentId(moved)!;
        }
        // Folders whose app can't move: copy, then delete the original.
        var copy = CopyInto(target, source.Name, OpenRead(source));
        if (!DocumentsContract.DeleteDocument(Resolver, source.Uri))
            throw new IOException(Tr.T("copied, but the original could not be removed"));
        return copy;
    }

    /// <summary>Move a file back into a folder by its id (Undo).</summary>
    public static string MoveBack(AUri tree, string id, string fromParent, string toParent)
    {
        var moved = DocumentsContract.MoveDocument(Resolver, DocumentsContract.BuildDocumentUriUsingTree(tree, id)!,
                                                   DocumentsContract.BuildDocumentUriUsingTree(tree, fromParent)!,
                                                   DocumentsContract.BuildDocumentUriUsingTree(tree, toParent)!)
                    ?? throw new IOException(Tr.T("it is no longer there"));
        return DocumentsContract.GetDocumentId(moved)!;
    }

    /// <summary>A new file in the folder, filled from the stream. Returns its id.</summary>
    public static string CopyInto(Doc folder, string name, Stream content)
    {
        using (content)
        {
            var mime = global::Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant())
                       ?? "application/octet-stream";
            var made = DocumentsContract.CreateDocument(Resolver, folder.Uri, mime, name) ?? throw new IOException(Tr.T("it could not be written"));
            using (var output = Resolver.OpenOutputStream(made) ?? throw new IOException(Tr.T("it could not be written")))
                content.CopyTo(output);
            return DocumentsContract.GetDocumentId(made)!;
        }
    }

    public static bool Delete(AUri uri) => DocumentsContract.DeleteDocument(Resolver, uri);

    public static Doc ById(AUri tree, string id, string path) => new(tree, id, "", System.IO.Path.GetFileName(path), path, 0, DateTime.Now, true, 0);

    private static string Unique(string name, HashSet<string> taken)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(name), suffix = System.IO.Path.GetExtension(name);
        var counter = 1;
        while (taken.Contains($"{stem} ({counter}){suffix}"))
            counter++;
        return $"{stem} ({counter}){suffix}";
    }

    /// <summary>Files picked with Android's own file picker, with their addresses (so the originals can be removed after).</summary>
    public static async Task<List<AUri>> PickFiles()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        var answer = await Results.Start(intent);
        var picked = new List<AUri>();
        if (answer?.ClipData is { } clip)
        {
            for (var index = 0; index < clip.ItemCount; index++)
                if (clip.GetItemAt(index)?.Uri is { } one)
                    picked.Add(one);
        }
        else if (answer?.Data is { } single)
        {
            picked.Add(single);
        }
        return picked;
    }

    /// <summary>A picked file's name.</summary>
    public static string NameOf(AUri address)
    {
        using var cursor = Resolver.Query(address, [IOpenableColumns.DisplayName], null, null, null);
        return cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } name ? name : address.LastPathSegment ?? "file";
    }

    public static Stream OpenRead(AUri address) => Resolver.OpenInputStream(address) ?? throw new IOException(Tr.T("it can't be read"));

    /// <summary>Remove a picked file (Android asks the app that holds it). False when that app doesn't allow it.</summary>
    public static bool TryDelete(AUri address)
    {
        try
        {
            return DocumentsContract.DeleteDocument(Resolver, address);
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException)
        {
            return false;
        }
    }
}

/// <summary>Android's pickers answer the activity; this turns that answer into an awaited result.</summary>
public static class Results
{
    private static readonly Dictionary<int, TaskCompletionSource<Intent?>> Waiting = [];
    private static int next = 7300;

    public static Task<Intent?> Start(Intent intent)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("no activity");
        var code = Interlocked.Increment(ref next);
        var answer = new TaskCompletionSource<Intent?>();
        lock (Waiting)
            Waiting[code] = answer;
        try
        {
            activity.StartActivityForResult(intent, code);
        }
        catch (ActivityNotFoundException)
        {
            lock (Waiting)
                Waiting.Remove(code);
            answer.TrySetResult(null);
        }
        return answer.Task;
    }

    /// <summary>Called by the activity: true when the answer was for one of these.</summary>
    public static bool Done(int code, Result result, Intent? data)
    {
        TaskCompletionSource<Intent?>? answer;
        lock (Waiting)
        {
            if (!Waiting.Remove(code, out answer))
                return false;
        }
        answer.TrySetResult(result == Result.Ok ? data : null);
        return true;
    }
}
