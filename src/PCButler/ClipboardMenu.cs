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
    /// The menu for one item. paste: given in the list the shortcut opens. newTag: how a new
    /// tag is typed there (in the list itself, which would close if a window opened on top);
    /// without it a small window asks.
    /// </summary>
    public static ContextMenu For(ClipEntry entry, Window owner, Action? paste = null, Action? newTag = null, Action<bool>? hold = null)
    {
        var menu = new ContextMenu();
        if (paste is not null)
            menu.Items.Add(Bold(Item("Paste", "\uE77F", paste)));
        menu.Items.Add(Item("Copy", "\uE8C8", () =>
        {
            if (!App.Current.Clips.Copy(entry))
                Ui.Warn("Clipboard", Ui.T("The clipboard is busy. Try again in a moment."));
        }));
        menu.Items.Add(new Separator());

        var picture = entry.Kind == "image" || entry.Files.Count > 0 && WebSearch.IsPicture(entry.Files[0]);
        menu.Items.Add(Item(picture ? "Search Google for this picture" : "Search with Google", picture ? "\uE8B3" : "\uE721", () => SearchGoogle(entry)));
        if (entry.Kind == "text" && LinkIn(entry.Text) is { } link)
            menu.Items.Add(Item("Open link", "\uE71B", () => Shell.Open(link)));
        if (entry.Kind == "files" && entry.Files.Count > 0)
            menu.Items.Add(Item("Show in File Explorer", "\uE838", () => Shell.Reveal(entry.Files[0])));
        menu.Items.Add(new Separator());

        menu.Items.Add(Tags(entry, owner, newTag));
        menu.Items.Add(Item(entry.Pinned ? "Unpin" : "Pin", entry.Pinned ? "\uE77A" : "\uE718", () => ClipboardHistory.SetPinned(entry, !entry.Pinned)));
        menu.Items.Add(Item("Delete", "\uE74D", () => Delete([entry], hold)));
        return menu;
    }

    /// <summary>A menu line with its icon.</summary>
    private static MenuItem Item(string label, string glyph, Action action)
    {
        var item = Ui.Item(label, action);
        item.Icon = Ui.Glyph(glyph, 14, "Muted");
        return item;
    }

    private static MenuItem Bold(MenuItem item)
    {
        item.FontWeight = FontWeights.SemiBold;
        return item;
    }

    /// <summary>The tags submenu: each tag in use, ticked when this item has it, and a new one.</summary>
    private static MenuItem Tags(ClipEntry entry, Window owner, Action? newTag)
    {
        var tags = new MenuItem { Header = Ui.T(entry.Tags.Count > 0 ? "Tags ({0})" : "Tag", entry.Tags.Count), Icon = Ui.Glyph("\uE8EC", 14, "Muted") };
        foreach (var tag in ClipboardHistory.AllTags().Take(15))
        {
            var has = entry.Tags.Any(mine => mine.Equals(tag, StringComparison.CurrentCultureIgnoreCase));
            var item = new MenuItem { Header = "#" + tag, IsCheckable = true, IsChecked = has, StaysOpenOnClick = true };
            item.Click += (_, _) => ClipboardHistory.SetTag(entry, tag, item.IsChecked);
            tags.Items.Add(item);
        }
        if (tags.Items.Count > 0)
            tags.Items.Add(new Separator());
        tags.Items.Add(Item("New tag…", "\uE710", () =>
        {
            if (newTag is not null)
            {
                newTag();
                return;
            }
            var typed = TextDialog.Ask(owner, "Tag", Ui.T("A word to find this item by, like “work” or “address”. Search for #word to see only what has it."));
            if (typed is not null)
                AddTags(entry, typed);
        }));
        if (entry.Tags.Count > 0)
        {
            tags.Items.Add(Item("Remove all tags", "\uE894", () =>
            {
                foreach (var tag in entry.Tags.ToList())
                    ClipboardHistory.SetTag(entry, tag, false);
            }));
        }
        return tags;
    }

    /// <summary>
    /// Delete items. Pinned or tagged ones were kept on purpose, so deleting them is
    /// confirmed first. hold: told while the question is up (the list the shortcut opens
    /// would otherwise close when the question takes the focus).
    /// </summary>
    public static void Delete(IReadOnlyList<ClipEntry> entries, Action<bool>? hold = null)
    {
        var kept = entries.Count(entry => entry.Pinned || entry.Tags.Count > 0);
        if (kept > 0)
        {
            hold?.Invoke(true);
            bool sure;
            try
            {
                sure = Ui.Ask("Clipboard", entries.Count == 1
                    ? Ui.T(entries[0].Pinned ? "This item is pinned. Delete it anyway?" : "This item has tags ({0}). Delete it anyway?",
                           string.Join(", ", entries[0].Tags.Select(tag => "#" + tag)))
                    : Ui.T("{0} of the {1} items are pinned or tagged. Delete them all anyway?", kept, entries.Count), warning: true);
            }
            finally
            {
                hold?.Invoke(false);
            }
            if (!sure)
                return;
        }
        foreach (var entry in entries.ToList())
            ClipboardHistory.Remove(entry);
    }

    /// <summary>Tags as typed: several at once, separated by commas.</summary>
    public static void AddTags(ClipEntry entry, string typed)
    {
        foreach (var tag in typed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            ClipboardHistory.SetTag(entry, tag, true);
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
