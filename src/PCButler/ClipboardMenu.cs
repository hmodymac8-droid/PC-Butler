// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The right-click menu of a clipboard item, the same in the list the shortcut opens and on
/// the Clipboard page: paste or copy it, search Google for it (a picture too, through Google
/// Lens), open its link or its file's folder, tag it, pin it, delete it.
/// </summary>
public static class ClipboardMenu
{
    /// <summary>
    /// The menu for one item. paste: given in the list the shortcut opens. hold: told when a
    /// window of PC Butler's opens on top (a dialog), so that list doesn't close meanwhile.
    /// </summary>
    public static ContextMenu For(ClipEntry entry, Window owner, Action? paste = null, Action<bool>? hold = null)
    {
        var menu = new ContextMenu();
        if (paste is not null)
            menu.Items.Add(Bold(Ui.Item("Paste", paste)));
        menu.Items.Add(Ui.Item("Copy", () =>
        {
            if (!App.Current.Clips.Copy(entry))
                Ui.Warn("Clipboard", Ui.T("The clipboard is busy. Try again in a moment."));
        }));
        menu.Items.Add(new Separator());

        menu.Items.Add(Ui.Item(entry.Kind == "image" || entry.Files.Count > 0 && WebSearch.IsPicture(entry.Files[0])
                                   ? "Search Google for this picture" : "Search with Google", () => SearchGoogle(entry)));
        if (entry.Kind == "text" && LinkIn(entry.Text) is { } link)
            menu.Items.Add(Ui.Item("Open link", () => Shell.Open(link)));
        if (entry.Kind == "files" && entry.Files.Count > 0)
            menu.Items.Add(Ui.Item("Show in File Explorer", () => Shell.Reveal(entry.Files[0])));
        menu.Items.Add(new Separator());

        menu.Items.Add(Tags(entry, owner, hold));
        menu.Items.Add(Ui.Item(entry.Pinned ? "Unpin" : "Pin", () => ClipboardHistory.SetPinned(entry, !entry.Pinned)));
        menu.Items.Add(Ui.Item("Delete", () => ClipboardHistory.Remove(entry)));
        return menu;
    }

    private static MenuItem Bold(MenuItem item)
    {
        item.FontWeight = FontWeights.SemiBold;
        return item;
    }

    /// <summary>The tags submenu: each tag in use, ticked when this item has it, and a new one.</summary>
    private static MenuItem Tags(ClipEntry entry, Window owner, Action<bool>? hold)
    {
        var tags = new MenuItem { Header = Ui.T(entry.Tags.Count > 0 ? "Tags ({0})" : "Tag", entry.Tags.Count) };
        foreach (var tag in ClipboardHistory.AllTags().Take(15))
        {
            var has = entry.Tags.Any(mine => mine.Equals(tag, StringComparison.CurrentCultureIgnoreCase));
            var item = new MenuItem { Header = "#" + tag, IsCheckable = true, IsChecked = has, StaysOpenOnClick = true };
            item.Click += (_, _) => ClipboardHistory.SetTag(entry, tag, item.IsChecked);
            tags.Items.Add(item);
        }
        if (tags.Items.Count > 0)
            tags.Items.Add(new Separator());
        tags.Items.Add(Ui.Item("New tag…", () =>
        {
            hold?.Invoke(true);
            try
            {
                var typed = TextDialog.Ask(owner, "Tag", Ui.T("A word to find this item by, like “work” or “address”. Search for #word to see only what has it."));
                if (typed is not null)
                {
                    foreach (var tag in typed.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        ClipboardHistory.SetTag(entry, tag, true);
                }
            }
            finally
            {
                hold?.Invoke(false);
            }
        }));
        if (entry.Tags.Count > 0)
        {
            tags.Items.Add(Ui.Item("Remove all tags", () =>
            {
                foreach (var tag in entry.Tags.ToList())
                    ClipboardHistory.SetTag(entry, tag, false);
            }));
        }
        return tags;
    }

    /// <summary>A text that is just a web address.</summary>
    private static string? LinkIn(string text)
    {
        text = text.Trim();
        return !text.Contains(' ') && !text.Contains('\n') && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString() : null;
    }

    /// <summary>Google for the text, or Google Lens for a picture (copied, or a copied picture file).</summary>
    public static async void SearchGoogle(ClipEntry entry)
    {
        var picture = entry.Kind == "image" ? entry.ImageFile
                    : entry.Kind == "files" && entry.Files.Count > 0 && WebSearch.IsPicture(entry.Files[0]) ? entry.Files[0] : null;
        if (picture is null)
        {
            var text = entry.Kind == "files" ? string.Join(" ", entry.Files.Select(Path.GetFileNameWithoutExtension)) : entry.Text;
            Shell.Open(WebSearch.Google(text));
            return;
        }
        if (!File.Exists(picture))
        {
            Ui.Info("Clipboard", Ui.T("This picture is not there any more."));
            return;
        }
        try
        {
            Shell.Open(await WebSearch.GoogleLens(picture));
        }
        catch (IOException error)
        {
            Log.Warning($"Google Lens: {error.Message}");
            Ui.Warn("Clipboard", Ui.T("Google could not be asked about this picture right now. Check the internet connection and try again."));
        }
    }
}
