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
    private readonly VerticalStackLayout create = new() { Spacing = 12, Padding = new Thickness(0, 24, 0, 0) };
    private readonly VerticalStackLayout locked = new() { Spacing = 12, Padding = new Thickness(0, 24, 0, 0) };
    private readonly VerticalStackLayout open = new() { Spacing = 0 };
    private readonly Entry newPassword = Secret(Tr.T("Password"));
    private readonly Entry confirmPassword = Secret(Tr.T("The same password again"));
    private readonly Entry password = Secret(Tr.T("Password"));
    private readonly Label count = Theme.Faint("", 12.5);
    private readonly Border statePill = Theme.Pill("", Theme.Primary, Theme.PrimarySoft);
    private readonly Border settingsButton, lockButton;
    // The files: where in the vault, what is there, and the bar for what is selected.
    private readonly FlexLayout crumbs = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
    private readonly VerticalStackLayout items = new() { Spacing = 0 };
    private readonly Button selectButton;
    private readonly Label selectedText = new() { FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
    private readonly Border selectionBar;
    private readonly Label lockText = Theme.Faint("", 12.5);
    private readonly Label autoText = Theme.Faint("", 12.5);
    private readonly Label autoValue = Theme.Faint("", 13);
    private readonly VerticalStackLayout lockSheet;
    private string here = "";
    private bool selecting;
    private readonly HashSet<string> selectedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<VaultItem> selectedFiles = [];
    private Vault? vault;
    private bool busy;

    /// <summary>A big rounded square with an icon, over the create and unlock screens.</summary>
    private static Border Hero(string icon) => new()
    {
        WidthRequest = 96, HeightRequest = 96, BackgroundColor = Theme.PrimarySoft, StrokeThickness = 0, StrokeShape = Theme.Round(32),
        HorizontalOptions = LayoutOptions.Center, Content = Theme.Icon(icon, 44), Margin = new Thickness(0, 0, 0, 6),
    };

    private static Label Centered(Label label)
    {
        label.HorizontalTextAlignment = TextAlignment.Center;
        return label;
    }

    public VaultPage() : base("Vault")
    {
        SetSub(new HorizontalStackLayout { Spacing = 8, Children = { statePill, count.Centered() } });
        settingsButton = Theme.IconButton("i_settings_2_w.png", OpenLockSheet);
        lockButton = Theme.IconButton("i_lock_w.png", Lock);

        // Creating: a shield, the password twice, and a warning that nothing can get it back.
        create.Children.Add(Hero("i_shield_check_m.png"));
        create.Children.Add(Centered(Theme.Title(Tr.T("Create the vault"), 20)));
        create.Children.Add(Centered(Theme.Faint(Tr.T("Choose a password of at least {0} characters. Without it, nobody (you neither) can open what is inside: write it down somewhere safe.", Vault.MinPassword))));
        create.Children.Add(Theme.PasswordField(newPassword).Margin(0, 8, 0, 0));
        create.Children.Add(Theme.PasswordField(confirmPassword));
        create.Children.Add(Theme.Hint(Tr.T("There's no recovery — if you forget it, files can't be opened.")));
        create.Children.Add(Theme.Accent(Tr.T("Create"), () => _ = Create(), "i_shield_check_k.png").Margin(0, 6, 0, 0));

        // Locked: the files are hidden until the password is typed.
        locked.Children.Add(Hero("i_lock_m.png"));
        locked.Children.Add(Centered(Theme.Title(Tr.T("Files are hidden"), 20)));
        locked.Children.Add(Centered(Theme.Faint(Tr.T("Unlock with your password to see your files."))));
        locked.Children.Add(Theme.PasswordField(password).Margin(0, 8, 0, 0));
        password.Completed += (_, _) => _ = Unlock();
        locked.Children.Add(Theme.Accent(Tr.T("Unlock"), () => _ = Unlock(), "i_lock_open_k.png"));
        locked.Children.Add(Centered(autoText));

        // Open: three tiles to add files, a folder, or make a folder.
        var tiles = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        tiles.Add(ActionTile("i_file_plus_m.png", Tr.T("Add files"), () => _ = AddFiles()), 0);
        tiles.Add(ActionTile("i_folder_input_m.png", Tr.T("Add folder"), () => _ = AddFolder()), 1);
        tiles.Add(ActionTile("i_folder_plus_m.png", Tr.T("New folder"), () => _ = NewFolder()), 2);
        open.Children.Add(tiles);
        open.Children.Add(Theme.Hint(Tr.T("Added files are moved in and encrypted — originals are removed.")));

        // The files: a path to tap back along, "Select", the folders and files here.
        selectButton = Theme.Link(Tr.T("Select"), () => Select(!selecting));
        var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8, Padding = new Thickness(4, 14, 0, 6) };
        head.Add(crumbs, 0);
        head.Add(selectButton, 1);
        open.Children.Add(head);
        open.Children.Add(Theme.CardOf(items, 0));
        var auto = Theme.Line3(Theme.Tile(("i_clock_m.png", Theme.PrimarySoft), 38), new Label { Text = Tr.T("Auto-lock"), FontSize = 14.5, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center },
                               new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center, Children = { autoValue.Centered(), Theme.Icon(Tr.IsArabic ? "i_chevron_left_g.png" : "i_chevron_right_g.png", 16) } });
        var autoCard = Theme.CardOf(auto, 14);
        autoCard.Tapped(OpenLockSheet);
        open.Children.Add(autoCard);
        open.Children.Add(new BoxView { HeightRequest = 80, Color = Colors.Transparent }); // room for the bar

        // While selecting: a bar at the bottom with what can be done to all of them.
        var bar = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 6 };
        bar.Add(Theme.IconButton("i_x_w.png", () => Select(false), Theme.Card, 40), 0);
        bar.Add(selectedText.Margin(4, 0, 0, 0), 1);
        bar.Add(Theme.IconButton("i_move_right_w.png", () => _ = MoveSelected(), Theme.Card, 40), 2);
        bar.Add(Theme.IconButton("i_log_out_w.png", () => _ = TakeOutSelected(), Theme.Card, 40), 3);
        bar.Add(Theme.IconButton("i_trash_2_r.png", () => _ = DeleteSelected(), Theme.DangerBack, 40), 4);
        selectionBar = Theme.BoxOf(bar, 8, Theme.SurfaceRaised);
        (selectionBar.StrokeShape, selectionBar.IsVisible) = (Theme.Round(26), false);
        selectionBar.Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.5f, Radius = 18, Offset = new Point(0, 6) };

        Body.Children.Add(create);
        Body.Children.Add(locked);
        Body.Children.Add(open);
        Float(selectionBar);

        // When it locks: in a sheet.
        var settings = ConfigStore.Load().Vault;
        var choices = VaultSettings.Choices;
        var labels = choices.Select(minutes => minutes == 0 ? Tr.T("Never") : minutes == 60 ? Tr.T("1 hour") : Tr.T("{0} min", minutes)).ToList();
        var onClose = new Switch { IsToggled = settings.LockOnClose, OnColor = Theme.Primary, ThumbColor = Theme.Text, VerticalOptions = LayoutOptions.Center };
        onClose.Toggled += (_, e) => SaveSettings(saved => saved.LockOnClose = e.Value);
        var closeLine = Theme.Line3(null, Theme.TwoLines(Theme.Body(Tr.T("Lock when the app is closed"), 14),
                                                         Theme.Faint(Tr.T("Also when you switch to another app or the screen turns off."), 12)), onClose, 8);
        lockSheet = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Theme.Caps(Tr.T("Lock after this long without use")),
                Theme.Chips(labels, Math.Max(0, Array.IndexOf(choices, settings.LockMinutes)), index => SaveSettings(saved => saved.LockMinutes = choices[index]), wrap: true),
                Theme.BoxOf(closeLine, 14, Theme.SurfaceRaised), lockText,
            },
        };
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

    private static View ActionTile(string icon, string text, Action action)
    {
        var tile = Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Theme.Tile((icon, Theme.PrimarySoft), 40),
                new Label { Text = text, FontSize = 13, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap },
            },
        }, 14);
        tile.Tapped(() => { App.Used(); action(); });
        return tile;
    }

    private void OpenLockSheet()
    {
        ShowLockText();
        OpenSheet(Tr.T("Locking"), lockSheet);
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
        autoValue.Text = settings.LockMinutes == 0 ? Tr.T("Never") : settings.LockMinutes == 60 ? Tr.T("1 hour") : Tr.T("{0} min", settings.LockMinutes);
        autoText.Text = settings.LockMinutes == 0 ? "" : Tr.T("Auto-locks after {0}", autoValue.Text);
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
        HeaderRight.Content = vault is null ? Vault.Exists ? settingsButton : null : lockButton;
        ShowLockText();
        if (!Vault.Exists)
        {
            SetSub(null);
        }
        else
        {
            SetSub(statePill.Parent as View ?? statePill);
            if (vault is null)
            {
                count.Text = "";
                Theme.SetPill(statePill, Tr.T("Locked"), Theme.Muted, Theme.SurfaceRaised);
            }
            else
            {
                Theme.SetPill(statePill, Tr.T("Open"), Theme.Primary, Theme.PrimarySoft);
                ShowItems();
            }
        }
        if (vault is null)
            selectionBar.IsVisible = false;
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
        count.Text = Tr.T("{0} file(s), {1}", vault.Items.Count, Size(vault.Items.Sum(item => item.Size)));
        ShowCrumbs();
        selectButton.Text = Tr.T(selecting ? "Done" : "Select");
        ShowSelection();
        items.Children.Clear();
        var folders = vault.FoldersIn(here);
        var files = vault.FilesIn(here);
        if (folders.Count == 0 && files.Count == 0)
            items.Children.Add(Theme.Empty("i_folder_open_m.png", Tr.T(here.Length == 0 ? "Nothing in the vault yet. Add files or a folder." : "This folder is empty.")));
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
            items.Children.Add(Theme.Divider().Margin(16, 0, 16, 0));
        var title = new Label { Text = name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text, LineBreakMode = LineBreakMode.MiddleTruncation };
        var texts = Theme.TwoLines(title, Theme.Faint(detail, 12));
        View? left;
        Action tappedRow;
        if (selecting)
        {
            // A round tick in front of the tile: mint with a check when chosen.
            var on = ticked;
            var mark = new Border { WidthRequest = 22, HeightRequest = 22, StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(), StrokeThickness = 2, VerticalOptions = LayoutOptions.Center };
            void Paint()
            {
                mark.Stroke = on ? Theme.Primary : Theme.Muted;
                mark.BackgroundColor = on ? Theme.Primary : Colors.Transparent;
                mark.Content = on ? Theme.Icon("i_check_k.png", 13) : null;
            }
            Paint();
            left = new HorizontalStackLayout { Spacing = 10, Children = { mark, Theme.Tile(tile, 40) } };
            tappedRow = () =>
            {
                on = !on;
                Paint();
                tickChanged(on);
            };
        }
        else
        {
            left = Theme.Tile(tile, 40);
            tappedRow = () => { App.Used(); tapped(); };
        }
        var dots = Theme.IconButton("i_ellipsis_vertical_g.png", () => { App.Used(); more(); }, Colors.Transparent, 40);
        dots.IsVisible = !selecting;
        var line = Theme.Line3(left, texts, dots);
        line.Padding = new Thickness(16, 10, 8, 10);
        texts.Tapped(tappedRow);
        left.Tapped(tappedRow);
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
        selectionBar.IsVisible = selecting && vault is not null;
        var total = selectedFiles.Count + selectedFolders.Count;
        selectedText.Text = Tr.T("{0} selected", total);
    }

    // --- one file, one folder ---

    private async Task FileActions(VaultItem item)
    {
        string open = Tr.T("Open"), rename = Tr.T("Rename…"), move = Tr.T("Move to…"), takeOut = Tr.T("Take out"), delete = Tr.T("Delete");
        var choice = await Choose(item.Name, Actions(open, rename, move, takeOut, delete), Theme.TileFor(item.Name));
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

    private static SheetChoice[] Actions(string open, string rename, string move, string takeOut, string delete) =>
    [
        new("i_external_link_w.png", open), new("i_pencil_w.png", rename), new("i_move_right_w.png", move),
        new("i_log_out_w.png", takeOut), new("i_trash_2_r.png", delete, Danger: true),
    ];

    private async Task FolderActions(string folder)
    {
        string open = Tr.T("Open"), rename = Tr.T("Rename…"), move = Tr.T("Move to…"), takeOut = Tr.T("Take out"), delete = Tr.T("Delete");
        var choice = await Choose(Vault.DirName(folder), Actions(open, rename, move, takeOut, delete), Theme.FolderTile);
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
        var choice = await Choose(Tr.T("Move to"), [new SheetChoice("i_lock_w.png", top), .. targets.Select(target => new SheetChoice("i_folder_w.png", target.Replace("/", " › ")))]);
        if (choice is null)
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
