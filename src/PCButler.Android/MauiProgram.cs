// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        Tr.Language = ConfigStore.Load().EffectiveLanguage();
        SharedFiles.ClearOld();
        RecycleBin.EmptyOlderThan(TimeSpan.FromDays(RecycleBin.KeepDays));
        // Entries sit in their own rounded boxes: no Android underline under the text.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent));
        // A box for an ID or a number of days: the phone's keypad of digits only, without the minus, comma and dot of Keyboard.Numeric.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(nameof(Entry.Keyboard), (handler, view) =>
        {
            if (view is DigitsEntry)
                handler.PlatformView.InputType = global::Android.Text.InputTypes.ClassNumber;
        });
        return builder.Build();
    }
}

/// <summary>A text box that takes digits only, with the phone's keypad of digits.</summary>
public sealed class DigitsEntry : Entry
{
    // Numeric, so every other setting of the box treats it as a number; the mapping above then drops the minus, comma and dot.
    public DigitsEntry() => Keyboard = Keyboard.Numeric;
}

/// <summary>The app: one window, the pages in tabs at the bottom.</summary>
public sealed class App : Application
{
    public App()
    {
        UserAppTheme = AppTheme.Dark;
        Resources = Theme.Resources();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new Shell());
        // The vault never stays open behind the user's back: going to the background locks it.
        // Android's own screens that the app opens itself (a picker, the viewer of a file) don't
        // count, unless the user stays away from the app for long.
        window.Stopped += (_, _) =>
        {
            if (leaving)
                awaySince = DateTime.UtcNow;
            else
                Pages.VaultPage.Current?.Lock();
            leaving = false;
        };
        window.Activated += (_, _) =>
        {
            if (awaySince is { } since && DateTime.UtcNow - since > TimeSpan.FromMinutes(10))
                Pages.VaultPage.Current?.Lock();
            (leaving, awaySince) = (false, null);
        };
        return window;
    }

    private static bool leaving;
    private static DateTime? awaySince;

    /// <summary>The app is about to open one of Android's own screens (a picker, a viewer): that is no reason to lock the vault.</summary>
    public static void LeavingOnPurpose() => leaving = true;
}

/// <summary>The program's colours (the same as on Windows), and the looks of the controls that use them.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb("#000000");
    public static readonly Color Card = Color.FromArgb("#131418");
    /// <summary>A box inside a card: a row, a transfer, a note.</summary>
    public static readonly Color Surface = Color.FromArgb("#0b0b0d");
    public static readonly Color SurfaceRaised = Color.FromArgb("#1b1c1f");
    public static readonly Color Secondary = Color.FromArgb("#1e1f22");
    public static readonly Color Track = Color.FromArgb("#26272a");
    public static readonly Color Line = Color.FromArgb("#24252a");
    public static readonly Color Text = Color.FromArgb("#e9ebee");
    public static readonly Color Muted = Color.FromArgb("#9a9ea5");
    public static readonly Color Primary = Color.FromArgb("#48d7a1");
    public static readonly Color PrimaryText = Color.FromArgb("#04241a");
    /// <summary>Primary at about 12%, behind pills and notes in mint.</summary>
    public static readonly Color PrimarySoft = Color.FromArgb("#14322a");
    public static readonly Color AccentBack = Color.FromArgb("#1a3a30");
    public static readonly Color AccentText = Color.FromArgb("#66e0b3");
    public static readonly Color Amber = Color.FromArgb("#e6b55d");
    public static readonly Color AmberSoft = Color.FromArgb("#33291a");
    public static readonly Color Kicker = Color.FromArgb("#4fa88a");
    /// <summary>Deleting: a deep teal with light mint text, as in the redesign.</summary>
    public static readonly Color DangerBack = Color.FromArgb("#2d6151");
    public static readonly Color DangerText = Color.FromArgb("#a4ecd0");
    public static readonly Color Red = Color.FromArgb("#f45a56");

    /// <summary>The coloured tile behind each kind of file: icon and background.</summary>
    public static readonly (string Icon, Color Back) DocTile = ("ic_doc.png", Color.FromArgb("#1d2a38"));
    public static readonly (string Icon, Color Back) ImageTile = ("ic_image.png", Color.FromArgb("#163a2e"));
    public static readonly (string Icon, Color Back) VideoTile = ("ic_film.png", Color.FromArgb("#2e2445"));
    public static readonly (string Icon, Color Back) AudioTile = ("ic_music.png", Color.FromArgb("#3e2a17"));
    public static readonly (string Icon, Color Back) ArchiveTile = ("ic_archive.png", Color.FromArgb("#1f2226"));
    public static readonly (string Icon, Color Back) FileTile = ("ic_file.png", Color.FromArgb("#1f2226"));
    public static readonly (string Icon, Color Back) FolderTile = ("ic_folder.png", Color.FromArgb("#3a3115"));
    public static readonly (string Icon, Color Back) LockTile = ("ic_lock.png", Color.FromArgb("#1a3a30"));
    public static readonly (string Icon, Color Back) BinTile = ("ic_trash.png", Color.FromArgb("#1f2226"));

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
                new() { Property = Button.CornerRadiusProperty, Value = 14 },
                new() { Property = Button.PaddingProperty, Value = new Thickness(18, 12) },
                new() { Property = Button.FontSizeProperty, Value = 14.0 },
                new() { Property = Button.FontAttributesProperty, Value = FontAttributes.Bold },
            },
        });
        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new() { Property = Entry.TextColorProperty, Value = Text },
                new() { Property = Entry.PlaceholderColorProperty, Value = Muted },
                new() { Property = VisualElement.BackgroundColorProperty, Value = Secondary },
            },
        });
        resources.Add(new Style(typeof(ProgressBar)) { Setters = { new() { Property = ProgressBar.ProgressColorProperty, Value = Primary } } });
        return resources;
    }

    private static Microsoft.Maui.Controls.Shapes.RoundRectangle Round(double radius) => new() { CornerRadius = radius };

    /// <summary>A rounded card around some content.</summary>
    public static Border CardOf(View content, double padding = 20) => new()
    {
        Content = content, BackgroundColor = Card, Stroke = Line, StrokeThickness = 1, Padding = new Thickness(padding),
        StrokeShape = Round(22), Margin = new Thickness(0, 0, 0, 14),
    };

    /// <summary>A box inside a card, on the darker surface: a note, a row, a transfer.</summary>
    public static Border BoxOf(View content, double padding = 12, Color? back = null) => new()
    {
        Content = content, BackgroundColor = back ?? Surface, StrokeThickness = 0, Padding = new Thickness(padding), StrokeShape = Round(14),
    };

    public static Button Accent(string text, Action action)
    {
        var button = new Button { Text = text, BackgroundColor = Primary, TextColor = PrimaryText, FontAttributes = FontAttributes.Bold };
        button.Clicked += (_, _) => action();
        return button;
    }

    public static Button Plain(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Clicked += (_, _) => action();
        return button;
    }

    /// <summary>A button for deleting: the deep teal of the redesign, full width.</summary>
    public static Button Danger(string text, Action action)
    {
        var button = new Button { Text = text, BackgroundColor = DangerBack, TextColor = DangerText };
        button.Clicked += (_, _) => action();
        return button;
    }

    /// <summary>A quiet button whose action can't easily be taken back: no background, red text.</summary>
    public static Button DangerGhost(string text, Action action)
    {
        var button = new Button { Text = text, BackgroundColor = Colors.Transparent, TextColor = Red };
        button.Clicked += (_, _) => action();
        return button;
    }

    /// <summary>A line of a list that can be tapped: text from the left, the full width.</summary>
    public static View Row(string text, Action action, bool enabled = true)
    {
        var label = new Label { Text = text, FontSize = 15, TextColor = enabled ? Text : Muted, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var row = BoxOf(label, 14, SurfaceRaised);
        row.Padding = new Thickness(16, 13);
        if (enabled)
            row.Tapped(action);
        return row;
    }

    /// <summary>A rounded square with a file-type (or folder, lock…) icon in its colour.</summary>
    public static Border Tile((string Icon, Color Back) tile, double size = 38) => new()
    {
        BackgroundColor = tile.Back, StrokeThickness = 0, WidthRequest = size, HeightRequest = size, StrokeShape = Round(size * 0.32),
        VerticalOptions = LayoutOptions.Center,
        Content = new Image { Source = tile.Icon, WidthRequest = size * 0.5, HeightRequest = size * 0.5, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
    };

    /// <summary>The tile for a file, by what its name says it is.</summary>
    public static (string Icon, Color Back) TileFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
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

    /// <summary>A small rounded badge: "Waiting", "2×", "Open"…</summary>
    public static Border Pill(string text, Color fore, Color back)
    {
        var pill = new Border
        {
            BackgroundColor = back, StrokeThickness = 0, Padding = new Thickness(10, 4), StrokeShape = Round(12),
            VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End,
            Content = new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = fore },
        };
        return pill;
    }

    public static void SetPill(Border pill, string text, Color fore, Color back)
    {
        pill.BackgroundColor = back;
        if (pill.Content is Label label)
            (label.Text, label.TextColor) = (text, fore);
    }

    /// <summary>A small heading in capitals over a group of choices or a value.</summary>
    public static Label Caps(string text) => new()
    {
        Text = text.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 11.5, FontAttributes = FontAttributes.Bold,
        CharacterSpacing = 1.2, TextColor = Muted,
    };

    /// <summary>A card's header line: a title on the left, and maybe a badge on the right.</summary>
    public static Grid Header(string title, View? right = null, double size = 18)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        grid.Add(Title(title, size).Centered(), 0);
        if (right is not null)
            grid.Add(right, 1);
        return grid;
    }

    /// <summary>A line with something on the left (a tile, an icon), texts in the middle and maybe something on the right.</summary>
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

    /// <summary>The empty state of a list: an icon and one line, centred in a dark box.</summary>
    public static View Empty(string icon, string text)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 8, HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Image { Source = icon, WidthRequest = 30, HeightRequest = 30, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = text, FontSize = 13.5, TextColor = Muted, HorizontalTextAlignment = TextAlignment.Center },
            },
        };
        var box = BoxOf(stack, 0);
        box.Padding = new Thickness(16, 28);
        return box;
    }

    /// <summary>A small icon next to a faint line of help.</summary>
    public static View Hint(string icon, string text)
    {
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8 };
        grid.Add(new Image { Source = icon, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 2, 0, 0) }, 0);
        grid.Add(Faint(text, 12.5), 1);
        return grid;
    }

    /// <summary>One of a few choices, as rounded chips; changed gets the index picked.</summary>
    public static FlexLayout Chips(IReadOnlyList<string> labels, int selected, Action<int> changed)
    {
        var flex = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        var chips = new List<Border>();
        void Paint(int index)
        {
            for (var i = 0; i < chips.Count; i++)
            {
                chips[i].BackgroundColor = i == index ? Primary : Secondary;
                ((Label)chips[i].Content!).TextColor = i == index ? PrimaryText : Text;
            }
        }
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var chip = new Border
            {
                StrokeThickness = 0, Padding = new Thickness(14, 8), Margin = new Thickness(0, 0, 8, 8), StrokeShape = Round(18),
                Content = new Label { Text = labels[i], FontSize = 13.5, FontAttributes = FontAttributes.Bold },
            };
            chip.Tapped(() => { Paint(index); changed(index); });
            chips.Add(chip);
            flex.Children.Add(chip);
        }
        Paint(selected);
        return flex;
    }

    /// <summary>A thin rounded bar that shows how far something has got.</summary>
    public static ProgressBar Bar(double part, Color? color = null, double height = 6) => new()
    {
        Progress = Math.Clamp(part, 0, 1), ProgressColor = color ?? Primary, BackgroundColor = Track, HeightRequest = height,
    };

    public static Label Title(string text, double size = 22) => new() { Text = text, FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = Text };
    public static Label Body(string text, double size = 14) => new() { Text = text, FontSize = size, TextColor = Text, LineBreakMode = LineBreakMode.WordWrap };
    public static Label Faint(string text, double size = 13) => new() { Text = text, FontSize = size, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap };
}

internal static class ThemeExtensions
{
    /// <summary>A tap on the view runs the action.</summary>
    public static T Tapped<T>(this T view, Action action) where T : View
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => action();
        view.GestureRecognizers.Add(tap);
        return view;
    }

    public static T Centered<T>(this T view) where T : View
    {
        view.VerticalOptions = LayoutOptions.Center;
        return view;
    }
}

/// <summary>The tabs at the bottom: one per part of PC Butler that a phone can have.</summary>
public sealed class Shell : Microsoft.Maui.Controls.Shell
{
    public Shell()
    {
        BackgroundColor = Theme.Background;
        // Arabic reads from the right: the whole app is laid out that way, the tabs too.
        FlowDirection = Tr.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetTabBarBackgroundColor(this, Theme.Card);
        SetTabBarForegroundColor(this, Theme.Primary);
        SetTabBarUnselectedColor(this, Theme.Muted);
        SetTabBarTitleColor(this, Theme.Primary);
        SetNavBarIsVisible(this, false);
        var tabs = new TabBar();
        tabs.Items.Add(new ShellContent { Title = Tr.T("Courier"), Content = new Pages.CourierPage(), Icon = "tab_courier.png" });
        tabs.Items.Add(new ShellContent { Title = Tr.T("Sorter"), ContentTemplate = new DataTemplate(typeof(Pages.SorterPage)), Icon = "tab_sorter.png" });
        tabs.Items.Add(new ShellContent { Title = Tr.T("Duplicates"), ContentTemplate = new DataTemplate(typeof(Pages.DuplicatesPage)), Icon = "tab_duplicates.png" });
        tabs.Items.Add(new ShellContent { Title = Tr.T("Storage"), ContentTemplate = new DataTemplate(typeof(Pages.StoragePage)), Icon = "tab_storage.png" });
        tabs.Items.Add(new ShellContent { Title = Tr.T("Vault"), ContentTemplate = new DataTemplate(typeof(Pages.VaultPage)), Icon = "tab_vault.png" });
        Items.Add(tabs);
    }
}
