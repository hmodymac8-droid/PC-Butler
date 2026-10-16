// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Apps: the apps on the phone that can be opened, with their icon and size, sorted by name or
/// size. One can be opened, shown in Android's own app info (to stop it or clear its data), or
/// uninstalled: Android always asks first. Android doesn't let an app stop other apps.
/// </summary>
public sealed class AppsPage : AppPage
{
    private sealed record AppItem(string Name, string Package, long Size, bool System, string Category, ImageSource? Icon);

    private readonly Entry query = new() { Placeholder = Tr.T("Search apps…"), TextColor = Theme.Text, PlaceholderColor = Theme.Muted, FontSize = 15 };
    private readonly Label countText = Theme.Faint("", 13);
    private readonly Label listCount = Theme.Faint("", 12.5);
    private readonly VerticalStackLayout rows = new() { Spacing = 0 };
    private List<AppItem> apps = [];
    private bool bySize, userOnly;
    private bool loaded;

    public AppsPage() : base("Apps", tool: true)
    {
        SetSub(countText);
        HeaderRight.Content = Theme.IconButton("i_refresh_cw_w.png", () => _ = Load());
        Body.Children.Add(Theme.Field(query, Theme.Icon("i_search_m.png", 20).Margin(0, 0, 10, 0)));
        query.TextChanged += (_, _) => Show();
        Body.Children.Add(Theme.Chips([Tr.T("Name"), Tr.T("Largest first")], 0, index => { bySize = index == 1; Show(); }).Margin(0, 12, 0, 0));
        Body.Children.Add(Theme.Chips([Tr.T("All apps"), Tr.T("Installed by you")], 0, index => { userOnly = index == 1; Show(); }).Margin(0, 8, 0, 0));
        Body.Children.Add(Theme.Section(Tr.T("Your apps"), listCount));
        Body.Children.Add(Theme.CardOf(rows, 0));
        Body.Children.Add(Theme.Hint(Tr.T("Android doesn't let an app stop other apps: App info has Android's own Force stop. Uninstalling always asks Android first.")));
    }

    protected override void Shown()
    {
        if (!loaded)
            _ = Load();
    }

    private static Context Context => Platform.CurrentActivity ?? global::Android.App.Application.Context;

    private async Task Load()
    {
        loaded = true;
        countText.Text = Tr.T("Looking for apps…");
        apps = await Task.Run(Read);
        Show();
    }

    /// <summary>The apps with an icon on the home screen (other than PC Butler).</summary>
    private static List<AppItem> Read()
    {
        var manager = Context.PackageManager!;
        var launcher = new Intent(Intent.ActionMain);
        launcher.AddCategory(Intent.CategoryLauncher);
#pragma warning disable CA1422 // the flags-number form works on every Android version the app runs on
        var found = manager.QueryIntentActivities(launcher, 0);
#pragma warning restore CA1422
        var list = new List<AppItem>();
        foreach (var package in found.Select(item => item.ActivityInfo?.ApplicationInfo).OfType<ApplicationInfo>().GroupBy(info => info.PackageName).Select(group => group.First()))
        {
            if (package.PackageName is not { } name || name == Context.PackageName)
                continue;
            long size = 0;
            foreach (var file in new[] { package.SourceDir }.Concat(package.SplitSourceDirs ?? []).OfType<string>())
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }
            var system = (package.Flags & ApplicationInfoFlags.System) != 0 && (package.Flags & ApplicationInfoFlags.UpdatedSystemApp) == 0;
            var category = package.Category switch
            {
                ApplicationCategories.Game => "Games",
                ApplicationCategories.Audio => "Music & audio",
                ApplicationCategories.Video => "Video",
                ApplicationCategories.Image => "Photos",
                ApplicationCategories.Social => "Social",
                ApplicationCategories.News => "News",
                ApplicationCategories.Maps => "Maps",
                ApplicationCategories.Productivity => "Productivity",
                _ => system ? "System" : "App",
            };
            list.Add(new AppItem(package.LoadLabel(manager) ?? name, name, size, system, category, IconOf(package, manager)));
        }
        return list;
    }

    /// <summary>The app's own icon, drawn into a small picture.</summary>
    private static ImageSource? IconOf(ApplicationInfo package, PackageManager manager)
    {
        try
        {
            var drawable = package.LoadIcon(manager);
            using var bitmap = Bitmap.CreateBitmap(96, 96, Bitmap.Config.Argb8888!);
            using var canvas = new Canvas(bitmap);
            drawable.SetBounds(0, 0, 96, 96);
            drawable.Draw(canvas);
            using var stream = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            var bytes = stream.ToArray();
            return ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    private void Show()
    {
        countText.Text = Tr.T("{0} apps  ·  {1}", apps.Count, Size(apps.Sum(app => app.Size)));
        var needle = (query.Text ?? "").Trim();
        var shown = apps.Where(app => (!userOnly || !app.System) && (needle.Length == 0 || app.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)));
        var list = (bySize ? shown.OrderByDescending(app => app.Size) : shown.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        listCount.Text = list.Count.ToString();
        rows.Children.Clear();
        if (list.Count == 0)
            rows.Children.Add(Theme.Empty("i_app_window_m.png", Tr.T("No apps found")));
        foreach (var app in list)
        {
            View tile = app.Icon is { } icon
                ? new Image { Source = icon, WidthRequest = 40, HeightRequest = 40, VerticalOptions = LayoutOptions.Center }
                : Theme.Tile(("i_app_window_m.png", Theme.PrimarySoft), 40);
            var name = new Label { Text = app.Name, FontSize = 14.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.TailTruncation };
            var line = Theme.Line3(tile, Theme.TwoLines(name, Theme.Faint($"{Tr.T(app.Category)}  ·  {Size(app.Size)}", 12)),
                                   Theme.Icon(Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png", 16));
            line.Padding = new Thickness(16, 10);
            line.Tapped(() => _ = Manage(app));
            if (rows.Children.Count > 0)
                rows.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
            rows.Children.Add(line);
        }
    }

    private async Task Manage(AppItem app)
    {
        string open = Tr.T("Open app"), info = Tr.T("App info"), uninstall = Tr.T("Uninstall");
        var choices = new List<SheetChoice> { new("i_external_link_w.png", open), new("i_info_w.png", info) };
        if (!app.System)
            choices.Add(new("i_trash_2_r.png", uninstall, Danger: true));
        var choice = await Choose(app.Name, choices, ("i_app_window_m.png", Theme.PrimarySoft));
        try
        {
            App.LeavingOnPurpose();
            if (choice == open && Context.PackageManager!.GetLaunchIntentForPackage(app.Package) is { } launch)
            {
                launch.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(launch);
            }
            else if (choice == info)
            {
                var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings, global::Android.Net.Uri.Parse("package:" + app.Package));
                intent.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(intent);
            }
            else if (choice == uninstall)
            {
                var intent = new Intent(Intent.ActionDelete, global::Android.Net.Uri.Parse("package:" + app.Package));
                intent.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(intent);
                ActivityLog.Record("apps", "Uninstall asked", app.Name);
                loaded = false; // look again when the page shows next
            }
        }
        catch (ActivityNotFoundException)
        {
            await Say(Tr.T("Android didn't open that."));
        }
    }
}
