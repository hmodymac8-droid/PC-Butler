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
        return builder.Build();
    }
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
        window.Stopped += (_, _) => Pages.VaultPage.Current?.Lock();
        return window;
    }
}

/// <summary>The program's colours (the same as on Windows), and the looks of the controls that use them.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb("#0d0e11");
    public static readonly Color Card = Color.FromArgb("#141517");
    public static readonly Color Secondary = Color.FromArgb("#1c1e21");
    public static readonly Color Line = Color.FromArgb("#2a2b2e");
    public static readonly Color Text = Color.FromArgb("#e9ebee");
    public static readonly Color Muted = Color.FromArgb("#a7abb1");
    public static readonly Color Primary = Color.FromArgb("#48d7a1");
    public static readonly Color PrimaryText = Color.FromArgb("#041b12");
    public static readonly Color Amber = Color.FromArgb("#e6b55d");
    public static readonly Color Red = Color.FromArgb("#f45a56");

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
                new() { Property = Button.CornerRadiusProperty, Value = 10 },
                new() { Property = Button.PaddingProperty, Value = new Thickness(16, 10) },
                new() { Property = Button.FontSizeProperty, Value = 14.0 },
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

    /// <summary>A rounded card around some content.</summary>
    public static Border CardOf(View content, double padding = 18) => new()
    {
        Content = content, BackgroundColor = Card, Stroke = Line, StrokeThickness = 1, Padding = new Thickness(padding),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }, Margin = new Thickness(0, 0, 0, 14),
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

    /// <summary>A line of a list that can be tapped: text from the left, the full width.</summary>
    public static View Row(string text, Action action, bool enabled = true)
    {
        var label = new Label { Text = text, FontSize = 15, TextColor = enabled ? Text : Muted, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var row = new Border
        {
            Content = label, BackgroundColor = Secondary, StrokeThickness = 0, Padding = new Thickness(16, 13),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        };
        if (enabled)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => action();
            row.GestureRecognizers.Add(tap);
        }
        return row;
    }

    public static Label Title(string text, double size = 22) => new() { Text = text, FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = Text };
    public static Label Body(string text, double size = 14) => new() { Text = text, FontSize = size, TextColor = Text, LineBreakMode = LineBreakMode.WordWrap };
    public static Label Faint(string text, double size = 13) => new() { Text = text, FontSize = size, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap };
}

/// <summary>The tabs at the bottom: one per part of PC Butler that a phone can have.</summary>
public sealed class Shell : Microsoft.Maui.Controls.Shell
{
    public Shell()
    {
        BackgroundColor = Theme.Background;
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
