// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Duplicates on the phone: the same photo saved twice, the same download from two chats.
/// In each group one copy is kept (the oldest, to start with; a tap keeps another) and the
/// others are deleted: into PC Butler's Recycle Bin (on the Storage page), from where they can
/// be put back. Tapping the kept copy keeps the whole group.
/// </summary>
public sealed class DuplicatesPage : AppPage
{
    private static readonly string[] Hints =
        ["Identical bytes — safest", "Same file name anywhere", "Same name and same size", "Good for songs & videos", "Fast, may have false matches"];
    private int mode;
    private readonly Label status = Theme.Faint("", 13.5);
    private readonly Label modeHint = Theme.Faint("", 12);
    private readonly Label groupsTitle = Theme.Caps("");
    private readonly View resultsHead;
    private readonly Button clear;
    private readonly VerticalStackLayout groups = new() { Spacing = 0 };
    private readonly Button remove;
    private readonly Label freesText = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
    private readonly Label freesDetail = Theme.Faint("", 12);
    private readonly Border bar;
    // Each group: its files, and the one kept (-1: the whole group is kept).
    private readonly List<Group> found = [];
    private readonly Dictionary<string, Doc> docs = new(StringComparer.Ordinal);
    private CancellationTokenSource? scanning;

    public DuplicatesPage() : base("Duplicates")
    {
        Body.Children.Add(Theme.Section(Tr.T("Match by")));
        Body.Children.Add(Theme.Chips(Duplicates.Modes.Select(item => Tr.T(item.Label)).ToList(), 0, index =>
        {
            mode = index;
            modeHint.Text = Tr.T(Hints[index]);
        }));
        modeHint.Text = Tr.T(Hints[0]);
        Body.Children.Add(Theme.Line3(Theme.Icon("i_info_g.png", 14), modeHint, null, 6).Margin(4, 8, 4, 0));
        Body.Children.Add(Theme.Section(Tr.T("Folders")));
        Body.Children.Add(FoldersChips("duplicates", () => { }));
        var find = Theme.Accent(Tr.T("Find duplicates"), () => _ = Scan(), "i_search_k.png");
        find.Margin = new Thickness(0, 16, 0, 0);
        Body.Children.Add(find);
        Body.Children.Add(status.Margin(4, 12, 4, 0));
        // What was found: a heading with Clear, the groups, and a bar at the bottom that deletes.
        clear = Theme.Link(Tr.T("Clear"), KeepAll);
        var heading = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Padding = new Thickness(4, 14, 4, 4), IsVisible = false };
        heading.Add(groupsTitle, 0);
        heading.Add(clear, 1);
        resultsHead = heading;
        Body.Children.Add(resultsHead);
        Body.Children.Add(groups);
        Body.Children.Add(new BoxView { HeightRequest = 90, Color = Colors.Transparent }); // room for the bar
        remove = Theme.Danger(Tr.T("Delete extras"), () => _ = DeleteTicked(), "i_trash_2_r.png");
        var line = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        line.Add(Theme.TwoLines(freesText, freesDetail), 0);
        line.Add(remove, 1);
        bar = Theme.BoxOf(line, 12, Theme.SurfaceRaised);
        (bar.Padding, bar.StrokeShape, bar.IsVisible) = (new Thickness(16, 10, 10, 10), Theme.Round(24), false);
        bar.Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.5f, Radius = 18, Offset = new Point(0, 6) };
        Float(bar);
    }

    private async Task Scan()
    {
        scanning?.Cancel();
        scanning = new CancellationTokenSource();
        var cancel = scanning.Token;
        var trees = Folders.For("duplicates");
        if (trees.Count == 0)
        {
            await Say(Tr.T("Choose a folder to look in first."));
            return;
        }
        var key = Duplicates.Modes[mode].Key;
        groups.Children.Clear();
        found.Clear();
        resultsHead.IsVisible = bar.IsVisible = false;
        status.IsVisible = true;
        status.Text = Tr.T("Looking for files…");
        List<DuplicateGroup> result;
        try
        {
            result = await Task.Run(() =>
            {
                // Folders inside each other are walked once; each file is known by its path.
                docs.Clear();
                foreach (var root in trees.Select(Folders.Root).OfType<Doc>())
                    foreach (var doc in Folders.Files(root, cancel))
                        docs.TryAdd(doc.Path, doc);
                var files = docs.Values.Select(doc => new DuplicateEntry(doc.Path, doc.Size, doc.Modified)).ToList();
                Duplicates.OpenFile = Open;
                MainThread.BeginInvokeOnMainThread(() => status.Text = Tr.T("Comparing {0} file(s)…", files.Count));
                return Duplicates.Find(files, key, cancel);
            }, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (result.Count == 0)
        {
            status.IsVisible = false;
            groups.Children.Add(Theme.CardOf(Theme.Empty("i_circle_check_m.png", Tr.T("No duplicates found."))));
        }
        resultsHead.IsVisible = result.Count > 0;
        groupsTitle.Text = Tr.T("{0} group(s)", result.Count).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        foreach (var group in result.Take(150))
        {
            // The oldest copy is taken for the original, and kept; the others are deleted.
            var files = group.Files.ToList();
            var index = found.Count;
            var card = new VerticalStackLayout { Spacing = 0 };
            var name = new Label { Text = files[0].Name, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
            var head = Theme.Line3(Theme.Tile(Theme.TileFor(files[0].Name)),
                                   Theme.TwoLines(name, Theme.Faint(Tr.T("{0} copies", files.Count) + "  ·  " + Size(files[0].Size), 12)), null);
            head.Padding = new Thickness(0, 0, 0, 10);
            card.Children.Add(head);
            var marks = new List<(Border Mark, Label Path, Label Word)>();
            foreach (var file in files)
            {
                var position = marks.Count;
                var mark = new Border { WidthRequest = 22, HeightRequest = 22, StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(), StrokeThickness = 2, VerticalOptions = LayoutOptions.Center };
                var path = new Label { Text = file.Path, FontSize = 13, LineBreakMode = LineBreakMode.MiddleTruncation };
                var word = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
                var row = Theme.Line3(mark, Theme.TwoLines(path, Theme.Faint(file.Modified.ToString("yyyy-MM-dd HH:mm"), 11.5)), word, 10);
                row.Padding = new Thickness(0, 10);
                row.Tapped(() => Keep(index, position));
                card.Children.Add(Theme.Divider());
                card.Children.Add(row);
                marks.Add((mark, path, word));
            }
            var entry = new Group(files, files.IndexOf(files.MinBy(file => file.Modified)!));
            entry.Paint = () =>
            {
                var kept = entry.Kept;
                for (var i = 0; i < marks.Count; i++)
                {
                    var (mark, path, word) = marks[i];
                    var keep = kept < 0 || i == kept;
                    mark.Stroke = keep ? Theme.Primary : Theme.Red;
                    mark.BackgroundColor = i == kept ? Theme.Primary : Colors.Transparent;
                    mark.Content = i == kept ? Theme.Icon("i_check_k.png", 13) : null;
                    (word.Text, word.TextColor) = keep ? (Tr.T("Keep"), Theme.Primary) : (Tr.T("Delete"), Theme.Red);
                    path.TextColor = keep ? Theme.Text : Theme.Muted;
                }
            };
            found.Add(entry);
            entry.Paint();
            groups.Children.Add(Theme.CardOf(card, 16));
        }
        if (result.Count > 150)
            groups.Children.Add(Theme.Faint(Tr.T("The first 150 groups are shown. Delete these, then look again for the rest.")));
        Count();
    }

    /// <summary>A tap on a copy keeps it (and deletes the others); a tap on the kept one keeps them all.</summary>
    private void Keep(int group, int file)
    {
        var item = found[group];
        item.Kept = item.Kept == file ? -1 : file;
        item.Paint();
        Count();
    }

    private void KeepAll()
    {
        foreach (var group in found)
        {
            group.Kept = -1;
            group.Paint();
        }
        Count();
    }

    private List<DuplicateEntry> Extras() => found.Where(group => group.Kept >= 0).SelectMany(group => group.Files.Where((_, i) => i != group.Kept)).ToList();

    private void Count()
    {
        var extras = Extras();
        bar.IsVisible = found.Count > 0;
        remove.IsEnabled = extras.Count > 0;
        freesText.Text = Tr.T("Frees {0}", Size(extras.Sum(file => file.Size)));
        freesDetail.Text = Tr.T("{0} extra copies", extras.Count);
        clear.IsVisible = extras.Count > 0;
    }

    private async Task DeleteTicked()
    {
        var chosen = Extras().Select(item => docs[item.Path]).ToList();
        if (chosen.Count == 0 || !await Ask(Tr.T("Move {0} file(s) to PC Butler's Recycle Bin? They can be put back from there, on the Storage page.", chosen.Count) + "\n\n" + SpaceNote, Tr.T("Delete")))
            return;
        var (count, errors) = await Task.Run(() => Bin(chosen));
        await Say(errors.Count == 0 ? Tr.T("{0} file(s) deleted.", count) : Tr.T("{0} file(s) deleted. {1} could not be:\n{2}", count, errors.Count, string.Join("\n", errors.Take(5))));
        await Scan();
    }

    /// <summary>A file of the chosen folders, opened to compare contents.</summary>
    private Stream Open(string path)
    {
        try
        {
            return Folders.OpenRead(docs[path]);
        }
        catch (Java.Lang.Exception error)
        {
            throw new IOException(error.Message);
        }
    }

    private sealed class Group(List<DuplicateEntry> files, int kept)
    {
        public List<DuplicateEntry> Files { get; } = files;
        public int Kept { get; set; } = kept;
        public Action Paint { get; set; } = () => { };
    }

    /// <summary>The bin keeps a copy, so the space comes back only once it is emptied.</summary>
    public static string SpaceNote => Tr.T("The space is freed when the Recycle Bin is emptied; it empties itself after {0} days.", RecycleBin.KeepDays);

    /// <summary>Files into PC Butler's Recycle Bin, one by one.</summary>
    public static (int Count, List<string> Errors) Bin(IEnumerable<Doc> files)
    {
        var count = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                RecycleBin.Send(file);
                count++;
            }
            catch (IOException error)
            {
                errors.Add($"{file.Name}: {error.Message}");
            }
        }
        return (count, errors);
    }
}
