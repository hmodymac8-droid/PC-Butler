// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// The Vault on the phone: files moved in are encrypted with the password (the same format
/// as on the PC), and can only be seen again by opening them here or taking them out. The
/// vault locks itself when the app goes to the background.
/// </summary>
public sealed class VaultPage : AppPage
{
    private readonly VerticalStackLayout create = new() { Spacing = 10 };
    private readonly VerticalStackLayout locked = new() { Spacing = 10 };
    private readonly VerticalStackLayout open = new() { Spacing = 10 };
    private readonly Entry newPassword = Secret(Tr.T("Password"));
    private readonly Entry confirmPassword = Secret(Tr.T("The same password again"));
    private readonly Entry password = Secret(Tr.T("Password"));
    private readonly Label count = Theme.Faint("", 13.5);
    private readonly Label stateText = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
    private readonly Image stateIcon = new() { WidthRequest = 22, HeightRequest = 22, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Border statePill = Theme.Pill("", Theme.Primary, Theme.PrimarySoft);
    private readonly View stateLine;
    private readonly VerticalStackLayout items = new() { Spacing = 0 };
    private readonly Border itemsCard;
    private VaultItem? expanded;
    private Vault? vault;
    private bool busy;

    public VaultPage() : base("Vault", "Private files, encrypted with your password.")
    {
        // Creating: a lock tile, the password twice.
        create.Children.Add(Theme.Line3(Theme.Tile(Theme.LockTile, 44), Theme.Title(Tr.T("Create the vault"), 18).Centered()));
        create.Children.Add(Theme.Faint(Tr.T("Choose a password of at least {0} characters. Without it, nobody (you neither) can open what is inside: write it down somewhere safe.", Vault.MinPassword)));
        create.Children.Add(Theme.BoxOf(newPassword, 4));
        create.Children.Add(Theme.BoxOf(confirmPassword, 4));
        create.Children.Add(Theme.Accent(Tr.T("Create"), () => _ = Create()));

        // The state of the vault, on top of the card: a lock (open or shut), the files in it, a pill.
        var badge = new Border
        {
            WidthRequest = 46, HeightRequest = 46, BackgroundColor = Theme.AccentBack, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 }, Content = stateIcon,
        };
        stateLine = Theme.Line3(badge, Theme.TwoLines(stateText, count), statePill);

        // Locked: the files are hidden until the password is typed.
        locked.Children.Add(new Border
        {
            WidthRequest = 58, HeightRequest = 58, BackgroundColor = Theme.Surface, StrokeThickness = 0, HorizontalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(), Margin = new Thickness(0, 10, 0, 0),
            Content = new Image { Source = "ic_lock_muted.png", WidthRequest = 28, HeightRequest = 28, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        });
        locked.Children.Add(new Label { Text = Tr.T("Files are hidden"), FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, HorizontalTextAlignment = TextAlignment.Center });
        locked.Children.Add(new Label { Text = Tr.T("Unlock with your password to see your files."), FontSize = 13.5, TextColor = Theme.Muted, HorizontalTextAlignment = TextAlignment.Center });
        locked.Children.Add(Theme.BoxOf(password, 4));
        password.Completed += (_, _) => _ = Unlock();
        locked.Children.Add(Theme.Accent(Tr.T("Unlock"), () => _ = Unlock()));

        // Open: add or lock, and what moving in means.
        var buttons = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        buttons.Add(Theme.Accent(Tr.T("Add files…"), () => _ = AddFiles()), 0);
        buttons.Add(Theme.Plain(Tr.T("Lock"), Lock), 1);
        open.Children.Add(buttons);
        open.Children.Add(Theme.BoxOf(Theme.Faint(Tr.T("Added files are moved in: they leave the phone's folders and live only in the vault. Tap a file to open it, take it out or delete it."), 12.5)));

        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 14, Children = { stateLine, create, locked, open } }));
        itemsCard = Theme.CardOf(new VerticalStackLayout { Spacing = 4, Children = { Theme.Caps(Tr.T("In the vault")), items } }, 16);
        Body.Children.Add(itemsCard);
        Current = this;
    }

    /// <summary>The page, so the app can lock the vault when it goes to the background.</summary>
    public static VaultPage? Current { get; private set; }

    private static Entry Secret(string placeholder) => new() { IsPassword = true, Placeholder = placeholder };

    protected override void Shown() => ShowState();

    /// <summary>Lock: copies opened for viewing are saved back and removed, and the key is forgotten.</summary>
    public void Lock()
    {
        vault?.CloseAll();
        vault = null;
        expanded = null;
        ShowState();
    }

    private void ShowState()
    {
        create.IsVisible = !Vault.Exists;
        locked.IsVisible = Vault.Exists && vault is null;
        open.IsVisible = vault is not null;
        stateLine.IsVisible = Vault.Exists;
        itemsCard.IsVisible = vault is not null && vault.Items.Count > 0;
        stateText.Text = Tr.T(vault is null ? "The vault is locked" : "The vault is open");
        stateIcon.Source = vault is null ? "ic_lock.png" : "ic_lock_open.png";
        if (vault is null)
        {
            count.Text = Tr.T("Private files, encrypted with your password.");
            Theme.SetPill(statePill, Tr.T("Hidden"), Theme.Text, Theme.Secondary);
        }
        else
        {
            Theme.SetPill(statePill, Tr.T("Open"), Theme.Primary, Theme.PrimarySoft);
            ShowItems();
        }
    }

    private async Task Create()
    {
        if ((newPassword.Text ?? "").Length < Vault.MinPassword)
        {
            await Say(Tr.T("The password needs at least {0} characters.", Vault.MinPassword));
            return;
        }
        if (newPassword.Text != confirmPassword.Text)
        {
            await Say(Tr.T("The two passwords are not the same."));
            return;
        }
        var chosen = newPassword.Text!;
        vault = await Task.Run(() => Vault.Create(chosen));
        newPassword.Text = confirmPassword.Text = "";
        ShowState();
    }

    private async Task Unlock()
    {
        if (busy)
            return;
        busy = true;
        var typed = password.Text ?? "";
        try
        {
            vault = await Task.Run(() => Vault.Unlock(typed));
            password.Text = "";
        }
        catch (WrongPasswordException)
        {
            await Say(Tr.T("Wrong password."));
        }
        catch (VaultException error)
        {
            await Say(error.Message);
        }
        finally
        {
            busy = false;
        }
        ShowState();
    }

    private async Task AddFiles()
    {
        if (vault is null)
            return;
        var picked = await Folders.PickFiles();
        if (picked.Count == 0)
            return;
        count.Text = Tr.T("Encrypting {0} file(s)…", picked.Count);
        var opened = vault;
        var (added, errors) = await Task.Run(() => MoveIn(opened, picked));
        if (errors.Count > 0)
            await Say(Tr.T("{0} file(s) added. {1} could not be:\n{2}", added, errors.Count, string.Join("\n", errors.Take(5))));
        ShowItems();
    }

    private void ShowItems()
    {
        if (vault is null)
            return;
        count.Text = vault.Items.Count == 0 ? Tr.T("The vault is open, and empty.")
            : Tr.T("{0} file(s), {1}", vault.Items.Count, Size(vault.Items.Sum(item => item.Size)));
        itemsCard.IsVisible = vault.Items.Count > 0;
        items.Children.Clear();
        foreach (var item in vault.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // A file: its tile, name and size; a tap opens its three actions just under it.
            var name = new Label { Text = item.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
            var chevron = new Image { Source = "ic_chevron.png", WidthRequest = 16, HeightRequest = 16, VerticalOptions = LayoutOptions.Center, Rotation = item == expanded ? 180 : 0 };
            var line = Theme.Line3(Theme.Tile(Theme.TileFor(item.Name), 36), Theme.TwoLines(name, Theme.Faint($"{Size(item.Size)} · " + Tr.T("encrypted"), 12)), chevron);
            line.Padding = new Thickness(0, 10);
            line.Tapped(() =>
            {
                expanded = expanded == item ? null : item;
                ShowItems();
            });
            if (items.Children.Count > 0)
                items.Children.Add(new BoxView { HeightRequest = 1, Color = Theme.Line });
            items.Children.Add(line);
            if (item != expanded)
                continue;
            var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            actions.Add(Small(Theme.Plain(Tr.T("Open"), () => _ = OpenItem(item))), 0);
            actions.Add(Small(Theme.Plain(Tr.T("Take out"), () => _ = TakeOut(item))), 1);
            actions.Add(Small(Theme.DangerGhost(Tr.T("Delete"), () => _ = DeleteItem(item))), 2);
            items.Children.Add(actions);
        }
    }

    private static Button Small(Button button)
    {
        (button.FontSize, button.Padding) = (12.5, new Thickness(6, 8));
        return button;
    }

    private async Task OpenItem(VaultItem item)
    {
        if (vault is not { } current)
            return;
        var copy = await Task.Run(() => current.OpenCopy(item));
        await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, new ReadOnlyFile(copy)));
    }

    private async Task TakeOut(VaultItem item)
    {
        if (vault is not { } current || !await Access.AskToSaveToDownload())
            return;
        var folder = Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Vault");
        Directory.CreateDirectory(folder);
        var (_, errors) = await Task.Run(() => current.MoveOut([item], folder));
        await Say(errors.Count == 0 ? Tr.T("{0} is now in Download/PC Butler Vault, no longer encrypted.", item.Name) : string.Join("\n", errors));
        expanded = null;
        ShowState();
    }

    private async Task DeleteItem(VaultItem item)
    {
        if (vault is not { } current || !await Ask(Tr.T("Delete {0} from the vault for good? This cannot be undone.", item.Name), Tr.T("Delete")))
            return;
        await Task.Run(() => current.Delete([item]));
        expanded = null;
        ShowState();
    }

    /// <summary>
    /// Picked files into the vault: each is copied in (through a private copy, which the vault
    /// encrypts and removes), then the original is removed from its folder. When the app that
    /// holds it doesn't allow that, the user is told to delete it.
    /// </summary>
    private static (int Count, List<string> Errors) MoveIn(Vault vault, List<global::Android.Net.Uri> picked)
    {
        var folder = Path.Combine(FileSystem.CacheDirectory, "to-vault");
        Directory.CreateDirectory(folder);
        var added = 0;
        var errors = new List<string>();
        foreach (var address in picked)
        {
            string name = "?", copy = "";
            try
            {
                name = Portal.SafeName(Folders.NameOf(address));
                copy = Sorter.UniqueDestination(folder, name);
                using (var input = Folders.OpenRead(address))
                using (var output = File.Create(copy))
                    input.CopyTo(output);
                var (count, problems) = vault.Add([copy]);
                errors.AddRange(problems);
                if (count == 0)
                    continue;
                added++;
                if (!Folders.TryDelete(address))
                    errors.Add(Tr.T("{0}: locked in the vault, but the original could not be removed. Delete it yourself.", name));
            }
            catch (Exception error) when (error is IOException or Java.Lang.Exception or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {error.Message}");
            }
            finally
            {
                if (copy.Length > 0 && File.Exists(copy))
                    File.Delete(copy);
            }
        }
        return (added, errors);
    }
}
