// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Microsoft.Maui.Controls.Shapes;
using PCButler.Core;

namespace PCButler.Android;

/// <summary>
/// The app's colours and the looks of its controls: a calm dark blue-grey, one mint accent,
/// cards without borders, round buttons, chips that scroll sideways and sheets that come up
/// from the bottom. The icons are Lucide's, drawn in three tones (w light, m mint, g grey).
/// </summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb("#0a0f13");
    public static readonly Color Card = Color.FromArgb("#13181d");
    /// <summary>A box inside a card or on the page: a row, an input, a note.</summary>
    public static readonly Color Surface = Color.FromArgb("#181d23");
    public static readonly Color SurfaceRaised = Color.FromArgb("#1d2228");
    public static readonly Color Secondary = Color.FromArgb("#1d2228");
    public static readonly Color Sheet = Color.FromArgb("#161b21");
    public static readonly Color Track = Color.FromArgb("#1d2228");
    public static readonly Color Line = Color.FromArgb("#14ffffff");
    public static readonly Color Text = Color.FromArgb("#eff2f5");
    public static readonly Color Muted = Color.FromArgb("#999fa6");
    public static readonly Color Primary = Color.FromArgb("#47d7a0");
    public static readonly Color PrimaryText = Color.FromArgb("#0b121a");
    /// <summary>Primary at about 16%, behind tiles, pills and tonal buttons.</summary>
    public static readonly Color PrimarySoft = Color.FromArgb("#16322c");
    public static readonly Color AccentBack = PrimarySoft;
    public static readonly Color AccentText = Primary;
    public static readonly Color Amber = Color.FromArgb("#f2af48");
    public static readonly Color AmberSoft = Color.FromArgb("#2d271b");
    public static readonly Color Ok = Color.FromArgb("#4ad496");
    public static readonly Color OkSoft = Color.FromArgb("#142d27");
    public static readonly Color Kicker = Primary;
    /// <summary>Deleting: red text on a dark red.</summary>
    public static readonly Color DangerBack = Color.FromArgb("#2d191c");
    public static readonly Color DangerText = Color.FromArgb("#f4514f");
    public static readonly Color Red = Color.FromArgb("#f4514f");

    /// <summary>The tile behind each kind of file: icon and background.</summary>
    public static readonly (string Icon, Color Back) DocTile = ("i_file_text_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) ImageTile = ("i_image_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) VideoTile = ("i_film_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) AudioTile = ("i_music_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) ArchiveTile = ("i_package_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) FileTile = ("i_file_w.png", SurfaceRaised);
    public static readonly (string Icon, Color Back) FolderTile = ("i_folder_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) LockTile = ("i_lock_m.png", PrimarySoft);
    public static readonly (string Icon, Color Back) BinTile = ("i_trash_2_w.png", SurfaceRaised);
    public static readonly (string Icon, Color Back) MonitorTile = ("i_monitor_w.png", SurfaceRaised);

    public static ResourceDictionary Resources()
    {
        var resources = new ResourceDictionary();
        resources.Add(new Style(typeof(ContentPage)) { Setters = { new() { Property = VisualElement.BackgroundColorProperty, Value = Background } } });
        resources.Add(new Style(typeof(Label)) { Setters = { new() { Property = Label.TextColorProperty, Value = Text } } });
        var disabled = new VisualState { Name = "Disabled" };
        disabled.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.4 });
        var normal = new VisualState { Name = "Normal" };
        normal.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 1.0 });
        var states = new VisualStateGroupList { new VisualStateGroup { Name = "CommonStates", States = { normal, disabled } } };
        resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new() { Property = VisualStateManager.VisualStateGroupsProperty, Value = states },
                new() { Property = Button.BackgroundColorProperty, Value = Secondary },
                new() { Property = Button.TextColorProperty, Value = Text },
                new() { Property = Button.CornerRadiusProperty, Value = 24 },
                new() { Property = Button.PaddingProperty, Value = new Thickness(20, 12) },
                new() { Property = VisualElement.MinimumHeightRequestProperty, Value = 48.0 },
                new() { Property = Button.FontSizeProperty, Value = 15.0 },
                new() { Property = Button.FontAttributesProperty, Value = FontAttributes.Bold },
            },
        });
        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new() { Property = Entry.TextColorProperty, Value = Text },
                new() { Property = Entry.PlaceholderColorProperty, Value = Muted },
                new() { Property = VisualElement.BackgroundColorProperty, Value = Colors.Transparent },
                new() { Property = Entry.FontSizeProperty, Value = 16.0 },
            },
        });
        resources.Add(new Style(typeof(ProgressBar)) { Setters = { new() { Property = ProgressBar.ProgressColorProperty, Value = Primary } } });
        return resources;
    }

    public static RoundRectangle Round(double radius) => new() { CornerRadius = radius };

    /// <summary>A rounded card around some content.</summary>
    public static Border CardOf(View content, double padding = 18) => new()
    {
        Content = content, BackgroundColor = Card, StrokeThickness = 0, Padding = new Thickness(padding),
        StrokeShape = Round(22), Margin = new Thickness(0, 0, 0, 12),
    };

    /// <summary>A box inside a card, or on the page: a note, a row, an input.</summary>
    public static Border BoxOf(View content, double padding = 12, Color? back = null) => new()
    {
        Content = content, BackgroundColor = back ?? Surface, StrokeThickness = 0, Padding = new Thickness(padding), StrokeShape = Round(16),
    };

    /// <summary>An icon of the app's own (one of the i_… images).</summary>
    public static Image Icon(string source, double size = 20) => new()
    {
        Source = source, WidthRequest = size, HeightRequest = size, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
    };

    private static Button Make(string text, Action action, Color back, Color fore, string? icon)
    {
        var button = new Button { Text = text, BackgroundColor = back, TextColor = fore };
        if (icon is not null)
        {
            button.ImageSource = icon;
            button.ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8);
        }
        button.Clicked += (_, _) => action();
        return button;
    }

    /// <summary>The main action: mint, dark text.</summary>
    public static Button Accent(string text, Action action, string? icon = null) => Make(text, action, Primary, PrimaryText, icon);

    public static Button Plain(string text, Action action, string? icon = null) => Make(text, action, Secondary, Text, icon);

    /// <summary>A quieter action in the accent: mint text on a light mint.</summary>
    public static Button Tonal(string text, Action action, string? icon = null) => Make(text, action, PrimarySoft, Primary, icon);

    /// <summary>A button for deleting: red on dark red.</summary>
    public static Button Danger(string text, Action action, string? icon = null) => Make(text, action, DangerBack, DangerText, icon);

    /// <summary>A quiet button whose action can't easily be taken back: no background, red text.</summary>
    public static Button DangerGhost(string text, Action action) => Make(text, action, Colors.Transparent, Red, null);

    /// <summary>A text-only button in the accent, for "Select", "Clear"…</summary>
    public static Button Link(string text, Action action)
    {
        var button = Make(text, action, Colors.Transparent, Primary, null);
        (button.Padding, button.FontSize, button.MinimumHeightRequest, button.VerticalOptions) = (new Thickness(8, 4), 13.5, 36, LayoutOptions.Center);
        return button;
    }

    /// <summary>A round icon button: settings, lock, refresh, copy…</summary>
    public static Border IconButton(string icon, Action action, Color? back = null, double size = 44)
    {
        var button = new Border
        {
            WidthRequest = size, HeightRequest = size, BackgroundColor = back ?? Card, StrokeThickness = 0, StrokeShape = new Ellipse(),
            VerticalOptions = LayoutOptions.Center, Content = Icon(icon, size * 0.45),
        };
        button.Tapped(action);
        return button;
    }

    public static void SetIcon(Border button, string icon)
    {
        if (button.Content is Image image)
            image.Source = icon;
    }

    /// <summary>A line of a list that can be tapped: text from the start, the full width.</summary>
    public static View Row(string text, Action action, bool enabled = true)
    {
        var label = new Label { Text = text, FontSize = 15, TextColor = enabled ? Text : Muted, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var row = BoxOf(label, 14, SurfaceRaised);
        row.Padding = new Thickness(16, 13);
        if (enabled)
            row.Tapped(action);
        return row;
    }

    /// <summary>A rounded square with a file-type (or folder, lock…) icon.</summary>
    public static Border Tile((string Icon, Color Back) tile, double size = 42) => new()
    {
        BackgroundColor = tile.Back, StrokeThickness = 0, WidthRequest = size, HeightRequest = size, StrokeShape = Round(size * 0.3),
        VerticalOptions = LayoutOptions.Center, Content = Icon(tile.Icon, size * 0.48),
    };

    /// <summary>The tile for a file, by what its name says it is.</summary>
    public static (string Icon, Color Back) TileFor(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".heif" or ".bmp" or ".svg" => ImageTile,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".3gp" or ".m4v" => VideoTile,
        ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".aac" or ".opus" or ".amr" => AudioTile,
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" or ".odt" or ".ods" or ".csv" or ".md" or ".epub" => DocTile,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".apk" => ArchiveTile,
        _ => FileTile,
    };

    /// <summary>The tile for one of the Sorter's category folders.</summary>
    public static (string Icon, Color Back) TileForCategory(string folder) => folder.ToLowerInvariant() switch
    {
        var name when name.Contains("pic") || name.Contains("image") || name.Contains("photo") => ImageTile,
        var name when name.Contains("video") || name.Contains("movie") => VideoTile,
        var name when name.Contains("audio") || name.Contains("music") => AudioTile,
        var name when name.Contains("doc") || name.Contains("book") => DocTile,
        var name when name.Contains("archive") || name.Contains("compress") || name.Contains("program") || name.Contains("app") => ArchiveTile,
        _ => FolderTile,
    };

    /// <summary>A small rounded badge with a dot: "Waiting", "Open"…</summary>
    public static Border Pill(string text, Color fore, Color back)
    {
        var dot = new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = fore, VerticalOptions = LayoutOptions.Center };
        return new Border
        {
            BackgroundColor = back, StrokeThickness = 0, Padding = new Thickness(10, 4), StrokeShape = Round(12),
            VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Start,
            Content = new HorizontalStackLayout
            {
                Spacing = 6, Children = { dot, new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = fore, VerticalOptions = LayoutOptions.Center } },
            },
        };
    }

    public static void SetPill(Border pill, string text, Color fore, Color back)
    {
        pill.BackgroundColor = back;
        if (pill.Content is HorizontalStackLayout { Children: [BoxView dot, Label label] })
            (dot.Color, label.Text, label.TextColor) = (fore, text, fore);
    }

    /// <summary>A small heading in capitals over a group: "CONNECT TO A PC".</summary>
    public static Label Caps(string text) => new()
    {
        Text = text.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, FontAttributes = FontAttributes.Bold,
        CharacterSpacing = 1, TextColor = Muted, VerticalOptions = LayoutOptions.Center,
    };

    /// <summary>A section's heading on the page, with maybe something at the end (a count, a link).</summary>
    public static View Section(string text, View? right = null)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Padding = new Thickness(4, 14, 4, 8) };
        grid.Add(Caps(text), 0);
        if (right is not null)
            grid.Add(right, 1);
        return grid;
    }

    /// <summary>A card's header line: a title at the start, and maybe a badge at the end.</summary>
    public static Grid Header(string title, View? right = null, double size = 17)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        grid.Add(Title(title, size).Centered(), 0);
        if (right is not null)
            grid.Add(right, 1);
        return grid;
    }

    /// <summary>A line with something at the start (a tile, an icon), texts in the middle and maybe something at the end.</summary>
    public static Grid Line3(View? left, View middle, View? right = null, double spacing = 12)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = spacing };
        if (left is not null)
            grid.Add(left, 0);
        grid.Add(middle, 1);
        if (right is not null)
            grid.Add(right, 2);
        return grid;
    }

    /// <summary>Two lines of text: a name and a faint detail under it.</summary>
    public static VerticalStackLayout TwoLines(Label top, Label bottom) => new() { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { top, bottom } };

    /// <summary>A thin line between the rows of a card.</summary>
    public static BoxView Divider() => new() { HeightRequest = 1, Color = Line };

    /// <summary>The empty state of a list: an icon in a mint square, a title, maybe a line and an action.</summary>
    public static View Empty(string icon, string text, string? sub = null, View? action = null)
    {
        var square = new Border
        {
            WidthRequest = 64, HeightRequest = 64, BackgroundColor = PrimarySoft, StrokeThickness = 0, StrokeShape = Round(22),
            HorizontalOptions = LayoutOptions.Center, Content = Icon(MintIcon(icon), 28),
        };
        var stack = new VerticalStackLayout
        {
            Spacing = 6, Padding = new Thickness(16, 24), HorizontalOptions = LayoutOptions.Center,
            Children = { square, new Label { Text = text, FontSize = 15, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) } },
        };
        if (sub is not null)
            stack.Children.Add(new Label { Text = sub, FontSize = 13, TextColor = Muted, HorizontalTextAlignment = TextAlignment.Center });
        if (action is not null)
            stack.Children.Add(action.Margin(0, 10, 0, 0));
        return stack;
    }

    /// <summary>The mint icon for an old ic_… name, so older calls still draw in the new style.</summary>
    private static string MintIcon(string icon) => icon switch
    {
        "ic_inbox.png" => "i_inbox_m.png",
        "ic_folder.png" => "i_folder_m.png",
        "ic_copy.png" => "i_copy_m.png",
        "ic_trash.png" => "i_trash_2_m.png",
        "ic_lock.png" => "i_lock_m.png",
        _ => icon,
    };

    /// <summary>A small info icon next to a faint line of help.</summary>
    public static View Hint(string text) => Hint("i_info_g.png", text);

    public static View Hint(string icon, string text)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 6, Padding = new Thickness(4, 6, 4, 0) };
        var source = icon.StartsWith("ic_", StringComparison.Ordinal) ? "i_info_g.png" : icon;
        grid.Add(new Image { Source = source, WidthRequest = 14, HeightRequest = 14, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) }, 0);
        grid.Add(Faint(text, 12), 1);
        return grid;
    }

    /// <summary>One chip: rounded, an outline when off, mint when on.</summary>
    public static Border Chip(string text, string? icon = null)
    {
        var label = new Label { Text = text, FontSize = 13.5, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.NoWrap };
        View content = label;
        if (icon is not null)
            content = new HorizontalStackLayout { Spacing = 6, Children = { Icon(icon, 16), label } };
        return new Border
        {
            StrokeThickness = 1, Stroke = Line, BackgroundColor = Card, Padding = new Thickness(14, 0), HeightRequest = 38, StrokeShape = Round(19),
            Content = content, VerticalOptions = LayoutOptions.Center,
        };
    }

    public static void PaintChip(Border chip, bool on)
    {
        chip.BackgroundColor = on ? Primary : Card;
        chip.StrokeThickness = on ? 0 : 1;
        var label = chip.Content as Label ?? (chip.Content as HorizontalStackLayout)?.Children.OfType<Label>().FirstOrDefault();
        if (label is not null)
            label.TextColor = on ? PrimaryText : Text;
    }

    /// <summary>
    /// One of a few choices, as chips; changed gets the index picked. In one row that scrolls
    /// sideways, or (wrap) in as many rows as they need.
    /// </summary>
    public static View Chips(IReadOnlyList<string> labels, int selected, Action<int> changed, bool wrap = false)
    {
        var chips = new List<Border>();
        void Paint(int index)
        {
            for (var i = 0; i < chips.Count; i++)
                PaintChip(chips[i], i == index);
        }
        Layout row = wrap ? new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap } : new HorizontalStackLayout { Spacing = 8 };
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var chip = Chip(labels[i]);
            if (wrap)
                chip.Margin = new Thickness(0, 0, 8, 8);
            chip.Tapped(() => { Paint(index); changed(index); });
            chips.Add(chip);
            row.Children.Add(chip);
        }
        Paint(selected);
        return wrap ? row : new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = row };
    }

    /// <summary>A thin rounded bar that shows how far something has got.</summary>
    public static ProgressBar Bar(double part, Color? color = null, double height = 6) => new()
    {
        Progress = Math.Clamp(part, 0, 1), ProgressColor = color ?? Primary, BackgroundColor = Track, HeightRequest = height,
    };

    /// <summary>A text box in its own rounded box, maybe with something at its end (a paste or show button).</summary>
    public static Border Field(Entry entry, View? end = null)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 4 };
        entry.VerticalOptions = LayoutOptions.Center;
        grid.Add(entry, 0);
        if (end is not null)
            grid.Add(end, 1);
        return new Border
        {
            Content = grid, BackgroundColor = Card, StrokeThickness = 0, StrokeShape = Round(18), Padding = new Thickness(14, 0, 4, 0), HeightRequest = 56,
        };
    }

    /// <summary>A password box with an eye to show what was typed.</summary>
    public static Border PasswordField(Entry entry)
    {
        entry.IsPassword = true;
        Border eye = null!;
        eye = IconButton("i_eye_g.png", () =>
        {
            entry.IsPassword = !entry.IsPassword;
            SetIcon(eye, entry.IsPassword ? "i_eye_g.png" : "i_eye_off_g.png");
        }, Colors.Transparent);
        return Field(entry, eye);
    }

    public static Label Title(string text, double size = 22) => new() { Text = text, FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = Text };
    public static Label Body(string text, double size = 14) => new() { Text = text, FontSize = size, TextColor = Text, LineBreakMode = LineBreakMode.WordWrap };
    public static Label Faint(string text, double size = 13) => new() { Text = text, FontSize = size, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap };
}

/// <summary>One line of a sheet of choices: its icon, its words, and whether it deletes.</summary>
public sealed record SheetChoice(string Icon, string Label, bool Danger = false);

/// <summary>
/// Sheets that come up from the bottom of the page, over a dimmed page: a few choices with
/// icons, or any content (settings). A tap outside, ✕ or Back closes them.
/// </summary>
public static class Sheets
{
    private static readonly Stack<(Grid Host, View Layer, Action Close)> open = [];

    /// <summary>A sheet is up: Back closes it instead of leaving the page.</summary>
    public static bool CloseTop()
    {
        if (open.Count == 0)
            return false;
        open.Peek().Close();
        return true;
    }

    /// <summary>The content in a sheet over the page; the returned action closes it. closed runs once it is gone.</summary>
    public static Action Open(Grid host, View? title, View content, Action? closed = null)
    {
        var dim = new BoxView { Color = Color.FromArgb("#b30a0f13") };
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(new BoxView
        {
            WidthRequest = 36, HeightRequest = 4, CornerRadius = 2, Color = Color.FromArgb("#66999fa6"), HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 0, 6),
        });
        Action close = () => { };
        if (title is not null)
        {
            var x = Theme.IconButton("i_x_w.png", () => close(), Theme.SurfaceRaised, 36);
            stack.Children.Add(Theme.Line3(null, title, x, 8));
        }
        stack.Children.Add(content);
        var panel = new Border
        {
            BackgroundColor = Theme.Sheet, StrokeThickness = 0, Padding = new Thickness(16, 10, 16, 24), VerticalOptions = LayoutOptions.End,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28, 28, 0, 0) },
            Content = new ScrollView { Content = stack, MaximumHeightRequest = 620 },
        };
        var layer = new Grid { Children = { dim, panel } };
        host.Add(layer);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count));
        var done = false;
        close = async () =>
        {
            if (done)
                return;
            done = true;
            var rest = open.Where(item => item.Layer != layer).Reverse().ToList();
            open.Clear();
            foreach (var item in rest)
                open.Push(item);
            await Task.WhenAll(panel.TranslateTo(0, 500, 180, Easing.CubicIn), dim.FadeTo(0, 180));
            host.Remove(layer);
            stack.Children.Remove(content); // so the same content can go into another sheet later
            closed?.Invoke();
        };
        dim.Tapped(() => close());
        open.Push((host, layer, close));
        panel.TranslationY = 500;
        dim.Opacity = 0;
        _ = panel.TranslateTo(0, 0, 260, Easing.CubicOut);
        _ = dim.FadeTo(1, 200);
        return close;
    }

    public static Label TitleText(string text) => new() { Text = text, FontSize = 16, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };

    /// <summary>A sheet of choices: the one tapped, or null when closed without a choice.</summary>
    public static Task<string?> Choose(Grid host, string title, IEnumerable<SheetChoice> choices, (string Icon, Color Back)? tile = null)
    {
        var answer = new TaskCompletionSource<string?>();
        var list = new VerticalStackLayout { Spacing = 2 };
        Action close = () => { };
        var dangers = false;
        foreach (var choice in choices)
        {
            if (choice.Danger && !dangers && list.Children.Count > 0)
                list.Children.Add(Theme.Divider().Margin(0, 4, 0, 4));
            dangers |= choice.Danger;
            var circle = new Border
            {
                WidthRequest = 36, HeightRequest = 36, StrokeThickness = 0, StrokeShape = new Ellipse(), BackgroundColor = choice.Danger ? Theme.DangerBack : Theme.SurfaceRaised,
                Content = Theme.Icon(choice.Icon, 18), VerticalOptions = LayoutOptions.Center,
            };
            var label = new Label { Text = choice.Label, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = choice.Danger ? Theme.Red : Theme.Text, VerticalOptions = LayoutOptions.Center };
            var row = Theme.Line3(circle, label, null, 14);
            row.Padding = new Thickness(6, 9);
            var picked = choice.Label;
            row.Tapped(() => { answer.TrySetResult(picked); close(); });
            list.Children.Add(row);
        }
        View head = TitleText(title);
        if (tile is { } kind)
            head = Theme.Line3(Theme.Tile(kind, 38), TitleText(title), null, 10);
        close = Open(host, head, list, () => answer.TrySetResult(null));
        return answer.Task;
    }
}

internal static class ThemeViewExtensions
{
    public static T Margin<T>(this T view, double left, double top, double right, double bottom) where T : View
    {
        view.Margin = new Thickness(left, top, right, bottom);
        return view;
    }
}
