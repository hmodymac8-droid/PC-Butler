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
        // Whether it locks then at all is the user's choice (the Vault page's "Lock when the app is closed").
        window.Stopped += (_, _) =>
        {
            if (leaving)
                awaySince = DateTime.UtcNow;
            else if (ConfigStore.Load().Vault.LockOnClose)
                Pages.VaultPage.Current?.Lock();
            leaving = false;
        };
        window.Activated += (_, _) =>
        {
            if (awaySince is { } since && DateTime.UtcNow - since > TimeSpan.FromMinutes(10) && ConfigStore.Load().Vault.LockOnClose)
                Pages.VaultPage.Current?.Lock();
            (leaving, awaySince) = (false, null);
        };
        return window;
    }

    private static bool leaving;
    private static DateTime? awaySince;

    /// <summary>The last time the user touched the screen or pressed a key: the vault locks after a while without.</summary>
    public static DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    public static void Used() => LastUsed = DateTime.UtcNow;

    /// <summary>The app is about to open one of Android's own screens (a picker, a viewer): that is no reason to lock the vault.</summary>
    public static void LeavingOnPurpose() => leaving = true;
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
        SetTabBarBackgroundColor(this, Theme.Background);
        SetTabBarForegroundColor(this, Theme.Primary);
        SetTabBarUnselectedColor(this, Theme.Muted);
        SetTabBarTitleColor(this, Theme.Text);
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
