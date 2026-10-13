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
    private readonly Label count = Theme.Body("", 16);
    private readonly VerticalStackLayout items = new() { Spacing = 6 };
    private Vault? vault;
    private bool busy;

    public VaultPage() : base("Vault", "Private files, encrypted with your password.")
    {
        create.Children.Add(Theme.Title(Tr.T("Create the vault"), 18));
        create.Children.Add(Theme.Faint(Tr.T("Choose a password of at least {0} characters. Without it, nobody (you neither) can open what is inside: write it down somewhere safe.", Vault.MinPassword)));
        create.Children.Add(newPassword);
        create.Children.Add(confirmPassword);
        create.Children.Add(Theme.Accent(Tr.T("Create"), () => _ = Create()));
        locked.Children.Add(Theme.Title(Tr.T("The vault is locked"), 18));
        locked.Children.Add(password);
        password.Completed += (_, _) => _ = Unlock();
        locked.Children.Add(Theme.Accent(Tr.T("Unlock"), () => _ = Unlock()));
        open.Children.Add(count);
        open.Children.Add(new HorizontalStackLayout
        {
            Spacing = 10,
            Children = { Theme.Accent(Tr.T("Add files…"), () => _ = AddFiles()), Theme.Plain(Tr.T("Lock"), Lock) },
        });
        open.Children.Add(Theme.Faint(Tr.T("Added files are moved in: they leave the phone's folders and live only in the vault. Tap a file to open it, take it out or delete it.")));
        open.Children.Add(items);
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Children = { create, locked, open } }));
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
        ShowState();
    }

    private void ShowState()
    {
        create.IsVisible = !Vault.Exists;
        locked.IsVisible = Vault.Exists && vault is null;
        open.IsVisible = vault is not null;
        if (vault is not null)
            ShowItems();
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
        items.Children.Clear();
        foreach (var item in vault.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Children.Add(Theme.Row($"🔒  {item.Name}  ·  {Size(item.Size)}", () => _ = Choose(item)));
        }
    }

    private async Task Choose(VaultItem item)
    {
        if (vault is null)
            return;
        var (openIt, takeOut, delete) = (Tr.T("Open"), Tr.T("Take out to Download"), Tr.T("Delete"));
        var picked = await DisplayActionSheet(item.Name, Tr.T("Cancel"), delete, openIt, takeOut);
        var current = vault;
        if (picked == openIt)
        {
            var copy = await Task.Run(() => current.OpenCopy(item));
            await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, new ReadOnlyFile(copy)));
        }
        else if (picked == takeOut)
        {
            if (!await Access.AskToSaveToDownload())
                return;
            var folder = Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Vault");
            Directory.CreateDirectory(folder);
            var (moved, errors) = await Task.Run(() => current.MoveOut([item], folder));
            await Say(errors.Count == 0 ? Tr.T("{0} is now in Download/PC Butler Vault, no longer encrypted.", item.Name) : string.Join("\n", errors));
        }
        else if (picked == delete && await Ask(Tr.T("Delete {0} from the vault for good? This cannot be undone.", item.Name), Tr.T("Delete")))
        {
            await Task.Run(() => current.Delete([item]));
        }
        ShowItems();
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
