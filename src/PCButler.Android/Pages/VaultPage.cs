// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// The Vault on the phone: files and whole folders moved in are encrypted with the password
/// (the same format as on the PC), and kept in folders of the vault's own, which can be made,
/// renamed and moved like in a file manager. The vault locks itself after a while without use,
/// and when the app leaves the screen (both can be changed).
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
    // The files: where in the vault, what is there, and the bar for what is selected.
    private readonly FlexLayout crumbs = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
    private readonly VerticalStackLayout items = new() { Spacing = 0 };
    private readonly Border itemsCard;
    private readonly Button selectButton;
    private readonly Label selectedText = new() { FontSize = 13.5, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly View selectionBar;
    private readonly Label lockText = Theme.Faint("", 12.5);
    private readonly Border settingsCard;
    private string here = "";
    private bool selecting;
    private readonly HashSet<string> selectedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<VaultItem> selectedFiles = [];
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

        // Open: add files or a whole folder, make a folder, lock.
        var adds = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        adds.Add(Theme.Accent(Tr.T("Add files…"), () => _ = AddFiles()), 0);
        adds.Add(Theme.Plain(Tr.T("Add folder…"), () => _ = AddFolder()), 1);
        var more = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        more.Add(Theme.Plain(Tr.T("New folder…"), () => _ = NewFolder()), 0);
        more.Add(Theme.Plain(Tr.T("Lock"), Lock), 1);
        open.Children.Add(adds);
        open.Children.Add(more);
        open.Children.Add(Theme.BoxOf(Theme.Faint(Tr.T("What you add is moved in: it leaves the phone's folders and lives only in the vault. Tap a folder to open it, a file for what can be done with it."), 12.5)));

        Body.Children.Add(Theme.CardOf(new VerticalStackLayout { Spacing = 14, Children = { stateLine, create, locked, open } }));

        // The files: a path to tap back along, "Select", the folders and files here.
        selectButton = Theme.Plain(Tr.T("Select"), () => Select(!selecting));
        (selectButton.Padding, selectButton.FontSize, selectButton.BackgroundColor, selectButton.TextColor) = (new Thickness(12, 6), 13, Colors.Transparent, Theme.Primary);
        var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        head.Add(crumbs, 0);
        head.Add(selectButton, 1);
        var bar = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 6 };
        bar.Add(selectedText, 0);
        bar.Add(Small(Theme.Plain(Tr.T("Move…"), () => _ = MoveSelected())), 1);
        bar.Add(Small(Theme.Plain(Tr.T("Take out"), () => _ = TakeOutSelected())), 2);
        bar.Add(Small(Theme.DangerGhost(Tr.T("Delete"), () => _ = DeleteSelected())), 3);
        selectionBar = Theme.BoxOf(bar, 8, Theme.SurfaceRaised);
        selectionBar.IsVisible = false;
        itemsCard = Theme.CardOf(new VerticalStackLayout { Spacing = 8, Children = { head, selectionBar, items } }, 16);
        Body.Children.Add(itemsCard);

        // When it locks.
        var settings = ConfigStore.Load().Vault;
        var choices = VaultSettings.Choices;
        var labels = choices.Select(minutes => minutes == 0 ? Tr.T("Never") : minutes == 60 ? Tr.T("1 hour") : Tr.T("{0} min", minutes)).ToList();
        var onClose = new Switch { IsToggled = settings.LockOnClose, OnColor = Theme.Primary, ThumbColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
        onClose.Toggled += (_, e) => SaveSettings(saved => saved.LockOnClose = e.Value);
        var closeLine = Theme.Line3(null, Theme.TwoLines(Theme.Body(Tr.T("Lock when the app is closed"), 14),
                                                         Theme.Faint(Tr.T("Also when you switch to another app or the screen turns off."), 12)), onClose, 8);
        settingsCard = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Theme.Header(Tr.T("Locking")),
                Theme.Caps(Tr.T("Lock after this long without use")),
                Theme.Chips(labels, Math.Max(0, Array.IndexOf(choices, settings.LockMinutes)), index => SaveSettings(saved => saved.LockMinutes = choices[index])),
                closeLine, lockText,
            },
        });
        Body.Children.Add(settingsCard);
        Current = this;
        // Without use for the chosen time: locked, wherever the app is.
        Dispatcher.StartTimer(TimeSpan.FromSeconds(15), () =>
        {
            var minutes = ConfigStore.Load().Vault.LockMinutes;
            if (vault is not null && !busy && minutes > 0 && DateTime.UtcNow - App.LastUsed > TimeSpan.FromMinutes(minutes))
                Lock();
            return true;
        });
    }

    /// <summary>The page, so the app can lock the vault when it goes to the background.</summary>
    public static VaultPage? Current { get; private set; }

    private static Entry Secret(string placeholder) => new() { IsPassword = true, Placeholder = placeholder };

    protected override void Shown() => ShowState();

    private void SaveSettings(Action<VaultSettings> change)
    {
        var config = ConfigStore.Load();
        change(config.Vault);
        ConfigStore.Save(config);
        ShowLockText();
    }

    private void ShowLockText()
    {
        var settings = ConfigStore.Load().Vault;
        lockText.Text = settings.LockMinutes == 0
            ? Tr.T(settings.LockOnClose ? "It locks when you lock it, or close the app." : "It locks only when you lock it.")
            : Tr.T(settings.LockOnClose ? "It locks after {0} minutes without use, or when you close the app." : "It locks after {0} minutes without use.", settings.LockMinutes);
    }

    /// <summary>Lock: copies opened for viewing are saved back and removed, and the key is forgotten.</summary>
    public void Lock()
    {
        vault?.CloseAll();
        vault = null;
        (here, selecting) = ("", false);
        selectedFiles.Clear();
        selectedFolders.Clear();
        ShowState();
    }

    private void ShowState()
    {
        create.IsVisible = !Vault.Exists;
        locked.IsVisible = Vault.Exists && vault is null;
        open.IsVisible = vault is not null;
        stateLine.IsVisible = Vault.Exists;
        itemsCard.IsVisible = vault is not null;
        settingsCard.IsVisible = Vault.Exists;
        stateText.Text = Tr.T(vault is null ? "The vault is locked" : "The vault is open");
        stateIcon.Source = vault is null ? "ic_lock.png" : "ic_lock_open.png";
        ShowLockText();
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
        try
        {
            vault = await Task.Run(() => Vault.Create(chosen));
        }
        catch (VaultException error)
        {
            await Say(error.Message);
            return;
        }
        newPassword.Text = confirmPassword.Text = "";
        App.Used();
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
            App.Used();
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

    // --- what is shown: the path, the folders, the files ---

    private void ShowItems()
    {
        if (vault is null)
            return;
        if (here.Length > 0 && !vault.Folders().Contains(here, StringComparer.OrdinalIgnoreCase))
            here = ""; // the folder was moved or deleted
        count.Text = vault.Items.Count == 0 && vault.Folders().Count == 0 ? Tr.T("The vault is open, and empty.")
            : Tr.T("{0} file(s), {1}", vault.Items.Count, Size(vault.Items.Sum(item => item.Size)));
        ShowCrumbs();
        selectButton.Text = Tr.T(selecting ? "Done" : "Select");
        ShowSelection();
        items.Children.Clear();
        var folders = vault.FoldersIn(here);
        var files = vault.FilesIn(here);
        if (folders.Count == 0 && files.Count == 0)
            items.Children.Add(Theme.Empty("ic_folder.png", Tr.T(here.Length == 0 ? "Nothing in the vault yet. Add files or a folder." : "This folder is empty.")));
        foreach (var folder in folders)
        {
            var inside = vault.FilesUnder(folder).Count;
            var detail = inside == 0 ? Tr.T("Folder") : Tr.T("{0} file(s)", inside);
            AddRow(Theme.FolderTile, Vault.DirName(folder), detail, selectedFolders.Contains(folder),
                   ticked => Tick(selectedFolders, folder, ticked), () => { here = folder; ShowItems(); }, () => _ = FolderActions(folder));
        }
        foreach (var file in files)
            AddRow(Theme.TileFor(file.Name), file.Name, $"{Size(file.Size)} · " + Tr.T("encrypted"), selectedFiles.Contains(file),
                   ticked => Tick(selectedFiles, file, ticked), () => _ = FileActions(file), () => _ = FileActions(file));
    }

    private void Tick<T>(HashSet<T> set, T item, bool ticked)
    {
        if (ticked)
            set.Add(item);
        else
            set.Remove(item);
        ShowSelection();
    }

    /// <summary>One line: a tile, the name and a detail, then "⋯"; while selecting, a tick box in front instead.</summary>
    private void AddRow((string Icon, Color Back) tile, string name, string detail, bool ticked, Action<bool> tickChanged, Action tapped, Action more)
    {
        if (items.Children.Count > 0)
            items.Children.Add(new BoxView { HeightRequest = 1, Color = Theme.Line });
        var title = new Label { Text = name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
        var texts = Theme.TwoLines(title, Theme.Faint(detail, 12));
        View? left;
        if (selecting)
        {
            var box = new CheckBox { IsChecked = ticked, Color = Theme.Primary, VerticalOptions = LayoutOptions.Center };
            box.CheckedChanged += (_, e) => tickChanged(e.Value);
            left = new HorizontalStackLayout { Spacing = 4, Children = { box, Theme.Tile(tile, 36) } };
            texts.Tapped(() => box.IsChecked = !box.IsChecked);
        }
        else
        {
            left = Theme.Tile(tile, 36);
            texts.Tapped(() => { App.Used(); tapped(); });
        }
        var dots = Theme.Plain("⋯", () => { App.Used(); more(); });
        (dots.Padding, dots.BackgroundColor, dots.TextColor, dots.FontSize, dots.VerticalOptions) = (new Thickness(12, 4), Colors.Transparent, Theme.Muted, 18, LayoutOptions.Center);
        dots.IsVisible = !selecting;
        var line = Theme.Line3(left, texts, dots);
        line.Padding = new Thickness(0, 8);
        items.Children.Add(line);
    }

    /// <summary>"Vault › Photos › 2024": each part opens that folder.</summary>
    private void ShowCrumbs()
    {
        crumbs.Children.Clear();
        var parts = here.Length == 0 ? [] : here.Split('/');
        void Part(string text, string dir, bool last)
        {
            var label = new Label
            {
                Text = text, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = last ? Theme.Text : Theme.Primary,
                VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 4),
            };
            if (!last)
                label.Tapped(() => { here = dir; ShowItems(); });
            crumbs.Children.Add(label);
            if (!last)
                crumbs.Children.Add(new Label { Text = Tr.IsArabic ? "  ‹  " : "  ›  ", FontSize = 15, TextColor = Theme.Muted, VerticalOptions = LayoutOptions.Center });
        }
        Part(Tr.T("Vault"), "", parts.Length == 0);
        for (var index = 0; index < parts.Length; index++)
            Part(parts[index], string.Join('/', parts[..(index + 1)]), index == parts.Length - 1);
    }

    private void Select(bool on)
    {
        selecting = on;
        selectedFiles.Clear();
        selectedFolders.Clear();
        ShowItems();
    }

    private void ShowSelection()
    {
        selectionBar.IsVisible = selecting;
        var total = selectedFiles.Count + selectedFolders.Count;
        selectedText.Text = total == 0 ? Tr.T("Tick what to move, take out or delete.") : Tr.T("{0} selected", total);
    }

    private static Button Small(Button button)
    {
        (button.FontSize, button.Padding) = (12.5, new Thickness(10, 8));
        return button;
    }

    // --- one file, one folder ---

    private async Task FileActions(VaultItem item)
    {
        string open = Tr.T("Open"), rename = Tr.T("Rename…"), move = Tr.T("Move to…"), takeOut = Tr.T("Take out"), delete = Tr.T("Delete");
        var choice = await DisplayActionSheet(item.Name, Tr.T("Cancel"), delete, open, rename, move, takeOut);
        if (choice == open)
            await OpenItem(item);
        else if (choice == rename)
            await Rename(item.Name, name => vault!.Rename(item, name));
        else if (choice == move)
            await MoveTo([item], []);
        else if (choice == takeOut)
            await TakeOut([item], []);
        else if (choice == delete)
            await DeleteItems([item], []);
    }

    private async Task FolderActions(string folder)
    {
        string open = Tr.T("Open"), rename = Tr.T("Rename…"), move = Tr.T("Move to…"), takeOut = Tr.T("Take out"), delete = Tr.T("Delete");
        var choice = await DisplayActionSheet(Vault.DirName(folder), Tr.T("Cancel"), delete, open, rename, move, takeOut);
        if (choice == open)
        {
            here = folder;
            ShowItems();
        }
        else if (choice == rename)
            await Rename(Vault.DirName(folder), name => vault!.RenameFolder(folder, name));
        else if (choice == move)
            await MoveTo([], [folder]);
        else if (choice == takeOut)
            await TakeOut([], [folder]);
        else if (choice == delete)
            await DeleteItems([], [folder]);
    }

    private async Task Rename(string old, Action<string> rename)
    {
        var name = await DisplayPromptAsync(Title, Tr.T("New name"), Tr.T("Rename"), Tr.T("Cancel"), initialValue: old);
        if (string.IsNullOrWhiteSpace(name) || name == old || vault is null)
            return;
        try
        {
            rename(name);
        }
        catch (VaultException error)
        {
            await Say(error.Message);
        }
        ShowItems();
    }

    private async Task NewFolder()
    {
        if (vault is null)
            return;
        App.Used();
        var name = await DisplayPromptAsync(Title, Tr.T("The new folder's name"), Tr.T("Create"), Tr.T("Cancel"));
        if (string.IsNullOrWhiteSpace(name) || vault is null)
            return;
        try
        {
            vault.CreateFolder(here, name);
        }
        catch (VaultException error)
        {
            await Say(error.Message);
        }
        ShowItems();
    }

    // --- several at once: what is ticked ---

    private (List<VaultItem> Files, List<string> Folders) Selected() => (selectedFiles.ToList(), selectedFolders.ToList());

    private async Task MoveSelected()
    {
        var (files, folders) = Selected();
        if (files.Count + folders.Count > 0 && await MoveTo(files, folders))
            Select(false);
    }

    private async Task TakeOutSelected()
    {
        var (files, folders) = Selected();
        if (files.Count + folders.Count > 0 && await TakeOut(files, folders))
            Select(false);
    }

    private async Task DeleteSelected()
    {
        var (files, folders) = Selected();
        if (files.Count + folders.Count > 0 && await DeleteItems(files, folders))
            Select(false);
    }

    /// <summary>Into another of the vault's folders, picked from a list. True when moved.</summary>
    private async Task<bool> MoveTo(List<VaultItem> files, List<string> folders)
    {
        if (vault is null)
            return false;
        // Not into a folder being moved, or into one inside it.
        var targets = vault.Folders().Where(target => !folders.Any(folder => Vault.IsInside(target, folder))).ToList();
        var top = Tr.T("Vault (the top)");
        var choice = await DisplayActionSheet(Tr.T("Move to"), Tr.T("Cancel"), null, [top, .. targets.Select(target => target.Replace("/", " › "))]);
        if (choice is null || choice == Tr.T("Cancel"))
            return false;
        var into = choice == top ? "" : targets.First(target => target.Replace("/", " › ") == choice);
        try
        {
            vault.Move(files, into);
            foreach (var folder in folders)
                vault.MoveFolder(folder, into);
        }
        catch (VaultException error)
        {
            await Say(error.Message);
        }
        ShowItems();
        return true;
    }

    /// <summary>Out to Download/PC Butler Vault, no longer encrypted: files as they are, folders with what is in them. True when done.</summary>
    private async Task<bool> TakeOut(List<VaultItem> files, List<string> folders)
    {
        if (vault is not { } current || !await Access.AskToSaveToDownload())
            return false;
        var target = Path.Combine(ConfigStore.DetectDownloadsFolder(), "PC Butler Vault");
        Directory.CreateDirectory(target);
        busy = true;
        count.Text = Tr.T("Decrypting…");
        List<string> errors;
        try
        {
            errors = await Task.Run(() =>
            {
                var problems = current.MoveOut(files, target).Errors;
                foreach (var folder in folders)
                    problems.AddRange(current.MoveOutFolder(folder, target).Errors);
                return problems;
            });
        }
        finally
        {
            busy = false;
            App.Used();
        }
        var what = files.Count + folders.Count == 1 ? (files.FirstOrDefault()?.Name ?? Vault.DirName(folders[0])) : Tr.T("{0} item(s)", files.Count + folders.Count);
        await Say(errors.Count == 0 ? Tr.T("{0} is now in Download/PC Butler Vault, no longer encrypted.", what) : string.Join("\n", errors.Take(8)));
        ShowState();
        return true;
    }

    private async Task<bool> DeleteItems(List<VaultItem> files, List<string> folders)
    {
        if (vault is not { } current)
            return false;
        var inside = folders.Sum(folder => current.FilesUnder(folder).Count);
        var question = files.Count == 1 && folders.Count == 0 ? Tr.T("Delete {0} from the vault for good? This cannot be undone.", files[0].Name)
            : folders.Count == 0 ? Tr.T("Delete {0} file(s) from the vault for good? This cannot be undone.", files.Count)
            : Tr.T("Delete {0} folder(s) and {1} file(s) from the vault for good, with everything in the folders ({2} file(s))? This cannot be undone.",
                   folders.Count, files.Count, inside);
        if (!await Ask(question, Tr.T("Delete")))
            return false;
        var errors = await Task.Run(() =>
        {
            var problems = current.Delete(files).Errors;
            foreach (var folder in folders)
                problems.AddRange(current.DeleteFolder(folder).Errors);
            return problems;
        });
        if (errors.Count > 0)
            await Say(string.Join("\n", errors.Take(8)));
        ShowState();
        return true;
    }

    private async Task OpenItem(VaultItem item)
    {
        if (vault is not { } current)
            return;
        string copy;
        try
        {
            copy = await Task.Run(() => current.OpenCopy(item));
        }
        catch (Exception error) when (error is IOException or VaultException or System.Security.Cryptography.CryptographicException)
        {
            await Say(error.Message);
            return;
        }
        App.LeavingOnPurpose();
        await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, new ReadOnlyFile(copy)));
    }

    // --- moving in ---

    private async Task AddFiles()
    {
        if (vault is null)
            return;
        var picked = await Folders.PickFiles();
        if (picked.Count == 0 || vault is not { } opened)
            return;
        var dir = here;
        busy = true;
        count.Text = Tr.T("Encrypting {0} file(s)…", picked.Count);
        (int Count, List<string> Errors) result;
        try
        {
            result = await Task.Run(() => MoveIn(opened, picked, dir));
        }
        finally
        {
            busy = false;
            App.Used();
        }
        if (result.Errors.Count > 0)
            await Say(Tr.T("{0} file(s) added. {1} could not be:\n{2}", result.Count, result.Errors.Count, string.Join("\n", result.Errors.Take(5))));
        ShowItems();
    }

    /// <summary>A whole folder of the phone, with the folders in it, into the vault folder shown.</summary>
    private async Task AddFolder()
    {
        if (vault is null)
            return;
        var tree = await Folders.PickTree();
        if (tree is null || vault is not { } opened || Folders.Root(tree) is not { } root)
            return;
        if (!await Ask(Tr.T("Move the folder {0} into the vault, with everything in it? It leaves the phone's folders and lives only in the vault.", root.Name), Tr.T("Move in")))
            return;
        var dir = here;
        busy = true;
        count.Text = Tr.T("Encrypting the folder {0}…", root.Name);
        (int Count, List<string> Errors) result;
        try
        {
            result = await Task.Run(() => MoveFolderIn(opened, root, dir));
        }
        finally
        {
            busy = false;
            App.Used();
        }
        await Say(result.Errors.Count == 0 ? Tr.T("{0} file(s) moved into the vault.", result.Count)
                  : Tr.T("{0} file(s) added. {1} could not be:\n{2}", result.Count, result.Errors.Count, string.Join("\n", result.Errors.Take(5))));
        ShowItems();
    }

    /// <summary>
    /// The folder copied to a private place with its shape, moved into the vault from there, and
    /// then each file that went in removed from the phone's folder, and the folders left empty.
    /// </summary>
    private static (int Count, List<string> Errors) MoveFolderIn(Vault vault, Doc root, string dir)
    {
        var stage = Path.Combine(FileSystem.CacheDirectory, "to-vault", Guid.NewGuid().ToString("N"));
        var top = Path.Combine(stage, Portal.SafeName(root.Name));
        var copies = new List<(string Copy, Doc File)>();
        var errors = new List<string>();
        try
        {
            Directory.CreateDirectory(top);
            var everything = new List<Doc>();
            var waiting = new Stack<Doc>([root]);
            while (waiting.Count > 0)
            {
                var folder = waiting.Pop();
                everything.Add(folder);
                foreach (var child in Folders.Children(folder))
                {
                    var place = Path.Combine(top, Path.Combine(child.Path[(root.Path.Length + 1)..].Split('/').Select(Portal.SafeName).ToArray()));
                    if (child.IsFolder)
                    {
                        Directory.CreateDirectory(place);
                        waiting.Push(child);
                        continue;
                    }
                    try
                    {
                        using (var input = Folders.OpenRead(child))
                        using (var output = File.Create(place))
                            input.CopyTo(output);
                        copies.Add((place, child));
                    }
                    catch (Exception error) when (error is IOException or Java.Lang.Exception or UnauthorizedAccessException)
                    {
                        errors.Add($"{child.Name}: {error.Message}");
                    }
                }
            }
            var (count, problems) = vault.AddFolders([top], dir);
            errors.AddRange(problems);
            // What went in is gone from the private copy: take it off the phone's folder too.
            foreach (var (copy, file) in copies.Where(pair => !File.Exists(pair.Copy)))
                if (!Folders.TryDelete(file.Uri))
                    errors.Add(Tr.T("{0}: locked in the vault, but the original could not be removed. Delete it yourself.", file.Name));
            foreach (var folder in everything.OrderByDescending(folder => folder.Path.Length))
            {
                try
                {
                    if (Folders.IsEmpty(folder))
                        Folders.TryDelete(folder.Uri);
                }
                catch (Exception error) when (error is IOException or Java.Lang.Exception)
                {
                    Log.Warning($"Vault: an emptied folder could not be removed: {error.Message}");
                }
            }
            return (count, errors);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Vault: the private copy could not be removed: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Picked files into the vault: each is copied in (through a private copy, which the vault
    /// encrypts and removes), then the original is removed from its folder. When the app that
    /// holds it doesn't allow that, the user is told to delete it.
    /// </summary>
    private static (int Count, List<string> Errors) MoveIn(Vault vault, List<global::Android.Net.Uri> picked, string dir)
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
                // Under its own name, not the " (1)" a second copy in the private folder may have got.
                var (count, problems) = vault.Add([(copy, dir)]);
                if (count == 1 && vault.Items[^1].Name != name)
                    vault.Rename(vault.Items[^1], name);
                errors.AddRange(problems);
                if (count == 0)
                    continue;
                added++;
                if (!Folders.TryDelete(address))
                    errors.Add(Tr.T("{0}: locked in the vault, but the original could not be removed. Delete it yourself.", name));
            }
            catch (Exception error) when (error is IOException or Java.Lang.Exception or UnauthorizedAccessException or VaultException)
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
