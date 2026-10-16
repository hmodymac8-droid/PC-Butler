// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.Content;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>What the tools share: the files of every folder the user chose, opening one, and a file's row.</summary>
public static class ToolKit
{
    /// <summary>
    /// Every file in all the folders the user gave PC Butler (the Sorter's, and the ones of
    /// Duplicates and Storage), each once.
    /// </summary>
    public static List<Doc> AllFiles(CancellationToken cancel)
    {
        var trees = Folders.For("sorter").Concat(Folders.For("storage")).GroupBy(tree => tree.ToString()).Select(group => group.First());
        var seen = new Dictionary<string, Doc>(StringComparer.Ordinal);
        foreach (var root in trees.Select(Folders.Root).OfType<Doc>())
            foreach (var doc in Folders.Files(root, cancel))
                seen.TryAdd(doc.Path, doc);
        return seen.Values.ToList();
    }

    /// <summary>True when there is at least one folder to look in.</summary>
    public static bool HasFolders => Folders.For("sorter").Count + Folders.For("storage").Count > 0;

    /// <summary>The file in the app Android has for it (a viewer, a player…).</summary>
    public static bool Open(Doc doc)
    {
        try
        {
            var context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
            var type = context.ContentResolver?.GetType(doc.Uri) ?? "*/*";
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(doc.Uri, type);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            App.LeavingOnPurpose();
            context.StartActivity(Intent.CreateChooser(intent, Tr.T("Open with"))!.AddFlags(ActivityFlags.NewTask));
            return true;
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.Exception)
        {
            return false;
        }
    }

    /// <summary>The kind of a file, for the chips: documents, images, videos, audio, apk, archives, or "".</summary>
    public static string Kind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".apk" or ".apks" or ".xapk" => "apk",
        _ when Theme.TileFor(name) == Theme.ImageTile => "images",
        _ when Theme.TileFor(name) == Theme.VideoTile => "videos",
        _ when Theme.TileFor(name) == Theme.AudioTile => "audio",
        _ when Theme.TileFor(name) == Theme.DocTile => "documents",
        _ when Theme.TileFor(name) == Theme.ArchiveTile => "archives",
        _ => "",
    };

    /// <summary>A file's line: its tile, its name, where it is and its size, and maybe something at the end.</summary>
    public static Grid FileRow(Doc file, View? end = null)
    {
        var name = new Label { Text = file.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
        var detail = Theme.Faint($"{Path.GetDirectoryName(file.Path)}  ·  {AppPage.Size(file.Size)}", 12);
        detail.LineBreakMode = LineBreakMode.HeadTruncation;
        var row = Theme.Line3(Theme.Tile(Theme.TileFor(file.Name), 40), Theme.TwoLines(name, detail), end);
        row.Padding = new Thickness(0, 10);
        return row;
    }
}
