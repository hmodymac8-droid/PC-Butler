// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>Password-protected, encrypted storage for private files, kept in folders of its own.</summary>
public sealed class VaultPage : Page
{
    private const string DragFormat = "PCButler.VaultRows"; // rows dragged inside the list
    private readonly ContentControl screen = new();
    private readonly DispatcherTimer idleTimer = new();
    private Core.Vault? vault; // null while locked
    private bool jobRunning;
    private bool lockWhenDone; // the window was closed during a job: lock once it ends
    private DateTime lastTouch;
    private readonly List<string> waiting = []; // sent from File Explorer or the drop zone, added once the vault is unlocked
    private string current = ""; // the vault folder shown, "" for the top
    // The unlocked screen.
    private Panel? toolbar;
    private CardPanel? panel;
    private TextBox? search;
    private TextBlock? status;
    private ProgressBar? progress;
    private ListView? list;
    private GridViewColumn? folderColumn;
    private Button? upButton;
    private WrapPanel? pathBar;
    // Dragging rows onto a folder.
    private Point dragStart;
    private Row? pressed;
    private bool keepSelection;
    private List<object>? dragging;

    /// <summary>A folder row's tag; file rows carry their VaultItem.</summary>
    private sealed record FolderTag(string Dir);

    public VaultPage(MainWindow main) : base(main, "Vault", "Your private space. Under your control.")
    {
        Core.Vault.ClearOpenCopies(); // copies left behind if the program was closed abruptly
        idleTimer.Tick += (_, _) => LockWhenIdle();
        AddAction("Lock", Lock);
        AddAction("Change password…", ChangePassword);
        AddAction("Recovery key…", MakeRecoveryKey);
        // Any use of the page keeps the vault open a while longer.
        PreviewMouseDown += (_, _) => Touch();
        PreviewKeyDown += (_, _) => Touch();
        PreviewMouseWheel += (_, _) => Touch();
        PreviewMouseMove += (_, _) =>
        {
            if ((DateTime.UtcNow - lastTouch).TotalSeconds > 5)
                Touch();
        };
        Children.Add(screen);
        ShowScreen();
    }

    private VaultSettings Settings => Main.Config.Vault;

    private void ShowScreen()
    {
        Actions.Visibility = vault is null ? Visibility.Collapsed : Visibility.Visible;
        if (vault is not null)
            BuildUnlocked();
        else
            BuildPassword(create: !Core.Vault.Exists);
    }

    // --- locked: the password form ---

    private void BuildPassword(bool create)
    {
        // A centred column: the lock mark, a heading, and the password form.
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Width = 440 };
        box.Children.Add(new Border
        {
            Background = new SolidColorBrush(Ui.Mix("#42d8a5", "#080a0e", 0.10)), Padding = new Thickness(26, 22, 26, 22),
            HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.Glyph("\uE72E", 34, "Primary"),
        });
        var (heading, intro) = create
            ? ("Create your Vault.", "Files you add are encrypted with your password and removed from where they were, "
                                     + "so nobody can open them unless they know the password.")
            : ("Your Vault is locked.", "Enter your password to see and open your private files.");
        var title = Ui.Text(Ui.T(heading), 17, weight: FontWeights.Bold).Margin(0, 16, 0, 4);
        title.FontFamily = Ui.DisplayFont;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        box.Children.Add(title);
        var introText = Ui.Muted(Ui.T(intro), wrap: true);
        introText.TextAlignment = TextAlignment.Center;
        box.Children.Add(introText);

        var form = new StackPanel { Margin = new Thickness(20, 18, 20, 0) };
        var password = new PasswordBox();
        var confirm = new PasswordBox();
        form.Children.Add(Ui.Muted(Ui.T("Password")));
        form.Children.Add(password.Margin(0, 4, 0, 10));
        if (create)
        {
            form.Children.Add(Ui.Muted(Ui.T("Type it again")));
            form.Children.Add(confirm.Margin(0, 4, 0, 10));
        }
        var message = Ui.Text("", brush: "Amber", wrap: true);
        form.Children.Add(message);
        var submitButton = Ui.Button(Ui.T(create ? "Create vault" : "Unlock"), () => { }, accent: true).Margin(0, 8);
        form.Children.Add(submitButton);
        if (!create && Core.Vault.HasRecoveryKey)
        {
            // A link-like button: quiet, but reachable by keyboard and screen readers.
            var forgot = Ui.Button(Ui.T("Forgot your password?"), ResetWithRecoveryKey).Margin(0, 10);
            forgot.Background = forgot.BorderBrush = Brushes.Transparent;
            forgot.Foreground = Ui.Paint("Primary");
            forgot.HorizontalAlignment = HorizontalAlignment.Center;
            form.Children.Add(forgot);
        }
        box.Children.Add(form);
        if (create)
        {
            var note = Ui.Muted(Ui.T("Write the password down somewhere safe. If it is forgotten, the files in the vault can't be recovered by anyone."), 11, wrap: true);
            note.TextAlignment = TextAlignment.Center;
            box.Children.Add(note.Margin(0, 16));
        }

        var submitting = false;
        async void Submit()
        {
            if (submitting)
                return; // a second Enter while the first is still working
            if (create && password.Password != confirm.Password)
            {
                message.Text = Ui.T("The two passwords are not the same.");
                return;
            }
            var typed = password.Password;
            message.Text = Ui.T(create ? "Creating the vault…" : "Unlocking…");
            submitting = true;
            submitButton.IsEnabled = password.IsEnabled = confirm.IsEnabled = false;
            Cursor = Cursors.Wait;
            try
            {
                // Stretching the password takes about half a second on purpose; the window stays responsive meanwhile.
                vault = await Task.Run(() => create ? Core.Vault.Create(typed) : Core.Vault.Unlock(typed));
            }
            catch (VaultException error)
            {
                message.Text = error.Message;
                password.Password = "";
                password.Focus();
                return;
            }
            finally
            {
                Cursor = null;
                submitting = false;
                submitButton.IsEnabled = password.IsEnabled = confirm.IsEnabled = true;
            }
            Touch();
            ShowScreen();
            AddWaiting();
        }

        submitButton.Click += (_, _) => Submit();
        foreach (var field in new[] { password, confirm })
            field.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    Submit();
            };
        screen.Content = box;
        password.Loaded += (_, _) => password.Focus();
    }

    // --- unlocked: the files and folders ---

    private void BuildUnlocked()
    {
        var layout = new DockPanel();
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Tool(string text, Action action, bool accent = false, double gap = 0) =>
            tools.Children.Add(Ui.Button(Ui.T(text), action, accent).Margin(gap, 0, 6, 6));
        Tool("Add files…", Add, accent: true);
        Tool("Add folder…", AddFolder);
        Tool("New folder…", NewFolder);
        Tool("Open", Open, gap: 18);
        Tool("Rename…", Rename);
        Tool("Move to…", MoveTo);
        Tool("Move out…", MoveOut);
        Tool("Delete…", Delete);
        toolbar = tools;
        layout.Children.Add(tools.Dock(Dock.Top));

        panel = new CardPanel("Vault files", "\uE72E");
        search = Ui.Field(width: 220, hint: Ui.T("Search the whole vault…"));
        search.TextChanged += (_, _) => Refresh();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                search.Text = "";
        };
        panel.Controls.Children.Add(search);
        var footer = panel.AddFooter();
        status = Ui.Muted("");
        progress = new ProgressBar { Width = 200, Visibility = Visibility.Collapsed };
        footer.Children.Add(LockChoices().Dock(Dock.Right));
        footer.Children.Add(progress.Margin(0, 0, 14, 0).Dock(Dock.Right));
        footer.Children.Add(status);

        // Where we are: Up, then "Vault › Photos › 2024", each part a way back there (and a place to drop on).
        var where = new DockPanel { Margin = new Thickness(14, 8, 20, 8) };
        upButton = new Button { Content = Ui.Glyph("\uE74A", 14, "Text"), ToolTip = Ui.T("Up one folder (Backspace)"), Padding = new Thickness(8, 4, 8, 4) };
        upButton.Click += (_, _) => Up();
        where.Children.Add(upButton);
        pathBar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        where.Children.Add(pathBar);
        var body = new DockPanel();
        body.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = where }.Dock(Dock.Top));

        var made = Ui.List(("Name", 360, "FileCell"), ("Size", 90), ("Date added", 140), ("Source", 220), ("Vault folder", 200));
        var view = (GridView)made.View;
        view.Columns[0].CellTemplate = NameCell;
        folderColumn = view.Columns[4];
        folderColumn.Width = 0;
        list = made;
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || Ui.RowAt(e.OriginalSource) is not { } row)
                return;
            if (row.Tag is FolderTag folder)
                Navigate(folder.Dir);
            else
                Open();
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Activate();
            else if (e.Key == Key.Delete)
                Delete();
            else if (e.Key == Key.F2)
                Rename();
            else if (e.Key == Key.Back || e.Key == Key.System && e.SystemKey == Key.Up)
                Up();
            else if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                NewFolder();
            else
                return;
            e.Handled = true;
        };
        list.ContextMenu = new ContextMenu();
        list.ContextMenuOpening += (_, e) => FillMenu(e);
        AttachDragging(list);
        body.Children.Add(list);
        panel.SetBody(body);
        layout.Children.Add(panel);
        screen.Content = layout;
        Refresh();
    }

    /// <summary>The file cell with room for a folder glyph: a tile of size 15 draws its text in the icon font.</summary>
    private static readonly DataTemplate NameCell = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:sys="clr-namespace:System;assembly=mscorlib">
            <StackPanel Orientation="Horizontal" Margin="16,0,0,0">
                <Border Width="30" Height="30" CornerRadius="6" Background="{Binding TileBack}" Margin="0,0,10,0">
                    <TextBlock x:Name="Tile" Text="{Binding TileText}" Foreground="{Binding TileFore}"
                               FontFamily="Segoe UI" FontWeight="Bold" FontSize="{Binding TileFontSize}" HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <TextBlock Text="{Binding Name}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
            </StackPanel>
            <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding TileFontSize}">
                    <DataTrigger.Value><sys:Double>15</sys:Double></DataTrigger.Value>
                    <Setter TargetName="Tile" Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
                    <Setter TargetName="Tile" Property="FontWeight" Value="Normal" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
        """);

    /// <summary>"Lock after: [10 minutes]" and "Lock when PC Butler is closed".</summary>
    private StackPanel LockChoices()
    {
        var choices = VaultSettings.Choices.ToList();
        if (!choices.Contains(Settings.LockMinutes))
            choices.Insert(choices.FindIndex(choice => choice == 0 || choice > Settings.LockMinutes) is var at and >= 0 ? at : choices.Count, Settings.LockMinutes);
        var minutes = new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center };
        foreach (var choice in choices)
            minutes.Items.Add(choice == 0 ? Ui.T("Never") : choice == 1 ? Ui.T("1 minute") : Ui.T("{0} minutes", choice));
        minutes.SelectedIndex = choices.IndexOf(Settings.LockMinutes);
        minutes.SelectionChanged += (_, _) =>
        {
            if (minutes.SelectedIndex < 0)
                return;
            var chosen = choices[minutes.SelectedIndex];
            Main.UpdateConfig(config => config.Vault.LockMinutes = chosen);
            Touch();
            Refresh();
        };
        var onClose = Ui.Switch(Ui.T("Lock when PC Butler is closed"), Settings.LockOnClose, on =>
        {
            Main.UpdateConfig(config => config.Vault.LockOnClose = on);
            Refresh();
        });
        onClose.ToolTip = Ui.T("Closing the window to the tray counts too.");
        return Ui.Row(8, LockLabel(), minutes, onClose.Margin(14));
    }

    private static TextBlock LockLabel()
    {
        var label = Ui.Muted(Ui.T("Lock after:"));
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private bool Searching => search is { Text.Length: > 0 } && search.Text.Trim().Length > 0;

    private void Refresh()
    {
        if (vault is null || list is null)
            return;
        try
        {
            Fill();
        }
        catch (InvalidOperationException)
        {
            // The list changed under a running job; it is shown again when the job ends.
        }
    }

    private void Fill()
    {
        var unlocked = vault!;
        var folders = unlocked.Folders();
        while (current.Length > 0 && !folders.Contains(current, StringComparer.OrdinalIgnoreCase))
            current = Core.Vault.ParentDir(current); // it was moved, renamed or deleted
        var needle = search!.Text.Trim();
        var searching = needle.Length > 0;
        List<Row> rows;
        if (searching)
            rows = unlocked.Items.Where(item => item.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                           .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Select(FileRow).ToList();
        else
        {
            int Inside(string dir) => folders.Count(folder => Core.Vault.ParentDir(folder).Equals(dir, StringComparison.OrdinalIgnoreCase))
                                      + unlocked.Items.Count(item => item.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase));
            rows = folders.Where(folder => Core.Vault.ParentDir(folder).Equals(current, StringComparison.OrdinalIgnoreCase))
                          .Select(folder => FolderRow(folder, Inside(folder)))
                          .Concat(unlocked.FilesIn(current).Select(FileRow)).ToList();
        }
        var kept = list!.SelectedItems.Cast<Row>().Select(row => RowKey(row.Tag)).ToHashSet();
        list.ItemsSource = rows;
        foreach (var row in rows.Where(row => kept.Contains(RowKey(row.Tag))))
            list.SelectedItems.Add(row);
        if (folderColumn is not null && folderColumn.Width > 0 != searching)
        {
            folderColumn.Width = searching ? 200 : 0;
            FitColumns();
        }
        ShowPath(searching);

        panel!.Title = searching ? Ui.T("Search results") : current.Length == 0 ? Ui.T("Vault files") : Core.Vault.DirName(current);
        var total = unlocked.Items.Count;
        panel.SetCount(Ui.T("{0} file(s)", total));
        var size = Ui.FormatSize(unlocked.Items.Sum(item => item.Size));
        var lockNote = Settings.LockMinutes > 0 ? Ui.T("Locks itself after {0} minutes without use.", Settings.LockMinutes)
                       : Settings.LockOnClose ? Ui.T("Locks when you lock it or close PC Butler.") : Ui.T("Locks only when you lock it.");
        status!.Text = (searching ? Ui.T("Found {0} of {1} file(s).", rows.Count, total) : Ui.T("{0} file(s), {1}.", total, size)) + " " + lockNote;
    }

    private Row FileRow(VaultItem item) => new Row
    {
        Name = item.Name, Tag = item, SortGroup = 1,
        Cells = [Ui.FormatSize(item.Size), DateTimeOffset.FromUnixTimeMilliseconds((long)(item.Added * 1000)).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                 Path.GetFileName(item.Folder.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : item.Folder,
                 item.Dir.Length == 0 ? Ui.T("Vault") : item.Dir.Replace("/", " › ")],
    }.WithTile(Path.GetExtension(item.Name));

    private static Row FolderRow(string dir, int inside)
    {
        var row = new Row
        {
            Name = Core.Vault.DirName(dir), Tag = new FolderTag(dir), SortGroup = 0,
            Cells = [inside > 0 ? Ui.T("{0} item(s)", inside) : Ui.T("Folder"), "", "", ""],
        }.WithTile("", "vault folder");
        row.TileText = "\uE8B7";
        row.TileFontSize = 15;
        return row;
    }

    private static string RowKey(object? tag) => tag switch
    {
        VaultItem item => "f:" + item.Id,
        FolderTag folder => "d:" + folder.Dir.ToLowerInvariant(),
        _ => "",
    };

    /// <summary>Select one row (a file by its item, a folder by its path) and give it the keyboard.</summary>
    private void Select(object tag)
    {
        if (list?.ItemsSource is not IEnumerable<Row> rows || rows.FirstOrDefault(row => RowKey(row.Tag) == RowKey(tag)) is not { } found)
            return;
        list.SelectedItems.Clear();
        list.SelectedItem = found;
        list.ScrollIntoView(found);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(found) as ListViewItem)?.Focus();
    }

    /// <summary>The name column takes what the other columns leave, as the list does when resized.</summary>
    private void FitColumns()
    {
        if (list?.View is not GridView view || list.ActualWidth <= 0)
            return;
        var others = view.Columns.Skip(1).Sum(column => column.Width);
        view.Columns[0].Width = Math.Max(160, list.ActualWidth - others - 30);
    }

    /// <summary>The path line: "Vault › Photos › 2024", each part clickable and a drop target.</summary>
    private void ShowPath(bool searching)
    {
        pathBar!.Children.Clear();
        upButton!.IsEnabled = !searching && current.Length > 0;
        if (searching)
        {
            pathBar.Children.Add(Ui.Muted(Ui.T("Files from the whole vault. Clear the search to go back to the folder.")));
            return;
        }
        var parts = current.Length == 0 ? Array.Empty<string>() : current.Split('/');
        for (var index = -1; index < parts.Length; index++)
        {
            var dir = string.Join('/', parts.Take(index + 1));
            var last = index == parts.Length - 1;
            if (index >= 0)
                pathBar.Children.Add(Ui.Muted(Tr.IsArabic ? "‹" : "›").Margin(2, 0, 2, 0));
            var crumb = new Button
            {
                Content = index < 0 ? Ui.T("Vault") : parts[index], Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Foreground = Ui.Paint(last ? "Text" : "Primary"), Padding = new Thickness(6, 2, 6, 2), FontWeight = last ? FontWeights.Bold : FontWeights.Normal,
                AllowDrop = true,
            };
            crumb.Click += (_, _) => Navigate(dir);
            crumb.DragOver += (_, e) => DragOverFolder(e, dir);
            crumb.Drop += (_, e) => DropOnFolder(e, dir);
            pathBar.Children.Add(crumb);
        }
    }

    private void Navigate(string dir)
    {
        current = dir;
        if (search is { Text.Length: > 0 })
            search.Text = ""; // refreshes
        else
            Refresh();
        Touch();
        if (list?.ItemsSource is IEnumerable<Row> rows && rows.FirstOrDefault() is { } first)
            Select(first.Tag!);
        else
            list?.Focus();
    }

    private void Up()
    {
        if (Searching || current.Length == 0)
            return;
        var from = current;
        Navigate(Core.Vault.ParentDir(current));
        Select(new FolderTag(from));
    }

    /// <summary>Enter: into a single selected folder, otherwise open the selected files.</summary>
    private void Activate()
    {
        List<Row> rows = list is null ? [] : Ui.Selected(list);
        if (rows.Count == 1 && rows[0].Tag is FolderTag folder)
            Navigate(folder.Dir);
        else
            Open();
    }

    private void FillMenu(ContextMenuEventArgs e)
    {
        var menu = list!.ContextMenu!;
        menu.Items.Clear();
        var rows = Ui.Selected(list);
        if (rows.Count == 0)
        {
            menu.Items.Add(Ui.Item("New folder…", NewFolder));
            menu.Items.Add(Ui.Item("Add files…", Add));
            menu.Items.Add(Ui.Item("Add folder…", AddFolder));
            return;
        }
        if (Searching && rows.Count == 1 && rows[0].Tag is VaultItem found)
            menu.Items.Add(Ui.Item("Show in its folder", () =>
            {
                Navigate(found.Dir);
                Select(found);
            }));
        menu.Items.Add(Ui.Item("Open", Activate));
        menu.Items.Add(Ui.Item("Rename…", Rename));
        menu.Items.Add(Ui.Item("Move to…", MoveTo));
        menu.Items.Add(Ui.Item("Move out…", MoveOut));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Delete…", Delete));
    }

    /// <summary>The selected files and folders, or a note asking to select some.</summary>
    private (List<VaultItem> Files, List<string> Folders) Selection()
    {
        List<Row> rows = list is null ? [] : Ui.Selected(list);
        if (rows.Count == 0)
            Ui.Info("Vault", Ui.T("Select one or more files or folders first."));
        return (rows.Select(row => row.Tag).OfType<VaultItem>().ToList(), rows.Select(row => row.Tag).OfType<FolderTag>().Select(folder => folder.Dir).ToList());
    }

    private bool Busy()
    {
        if (!jobRunning)
            return false;
        Ui.Info("Vault", Ui.T("Wait for the current job to finish first."));
        return true;
    }

    /// <summary>Run a change to the vault's list, showing what went wrong instead of losing it.</summary>
    private bool Change(Action change)
    {
        Touch();
        try
        {
            change();
            return true;
        }
        catch (Exception error) when (error is VaultException or IOException or UnauthorizedAccessException)
        {
            Ui.Warn("Vault", error.Message);
            return false;
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>"Vault › Photos › 2024", for messages.</summary>
    private static string VaultPath(string dir) => string.Join(" › ", new[] { Ui.T("Vault") }.Concat(dir.Length == 0 ? Array.Empty<string>() : dir.Split('/')));

    // --- dragging rows onto folders, and files in from File Explorer ---

    private void AttachDragging(ListView view)
    {
        view.AllowDrop = true;
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(view);
            pressed = Ui.RowAt(e.OriginalSource);
            keepSelection = false;
            // A plain click on one of several selected rows would select only it; wait for the release, so they can be dragged together.
            if (pressed is not null && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && view.SelectedItems.Count > 1 && view.SelectedItems.Contains(pressed))
            {
                keepSelection = true;
                e.Handled = true;
            }
        };
        view.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (keepSelection && pressed is not null)
                view.SelectedItem = pressed;
            keepSelection = false;
            pressed = null;
        };
        view.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed || jobRunning)
                return;
            var moved = e.GetPosition(view) - dragStart;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            var rows = Ui.Selected(view);
            if (!rows.Contains(start))
                rows = [start];
            pressed = null;
            keepSelection = false;
            dragging = rows.Select(row => row.Tag!).ToList();
            try
            {
                DragDrop.DoDragDrop(view, new DataObject(DragFormat, "rows"), DragDropEffects.Move);
            }
            finally
            {
                dragging = null;
            }
        };
        view.DragOver += (_, e) => DragOverFolder(e, FolderUnder(e));
        view.Drop += (_, e) => DropOnFolder(e, FolderUnder(e));
    }

    /// <summary>The folder a drop on the list goes into: the folder row under the mouse, else (for files from outside) the folder shown.</summary>
    private string? FolderUnder(DragEventArgs e) =>
        Ui.RowAt(e.OriginalSource)?.Tag is FolderTag folder ? folder.Dir : e.Data.GetDataPresent(DragFormat) ? null : current;

    /// <summary>True when the dragged rows can go into dir: something moves, and no folder goes into itself.</summary>
    private bool CanMoveInto(string dir) =>
        dragging is { Count: > 0 } items
        && !items.OfType<FolderTag>().Any(folder => Core.Vault.IsInside(dir, folder.Dir))
        && items.Any(tag => tag is VaultItem item ? !item.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)
                            : tag is FolderTag folder && !Core.Vault.ParentDir(folder.Dir).Equals(dir, StringComparison.OrdinalIgnoreCase));

    private void DragOverFolder(DragEventArgs e, string? dir)
    {
        e.Effects = DragDropEffects.None;
        if (vault is not null && !jobRunning && dir is not null)
        {
            if (e.Data.GetDataPresent(DragFormat))
                e.Effects = CanMoveInto(dir) ? DragDropEffects.Move : DragDropEffects.None;
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void DropOnFolder(DragEventArgs e, string? dir)
    {
        e.Handled = true;
        if (vault is null || jobRunning || dir is null)
            return;
        // Acted on after the drop is over, so a question doesn't hold up File Explorer.
        if (e.Data.GetDataPresent(DragFormat))
        {
            if (CanMoveInto(dir) && dragging is { } items)
            {
                var moving = items.ToList();
                Dispatcher.InvokeAsync(() => MoveInto(moving, dir));
            }
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
            Dispatcher.InvokeAsync(() => AddPaths(dropped.ToList(), dir));
    }

    // --- locking ---

    /// <summary>Restart the idle timer that locks the vault.</summary>
    private void Touch()
    {
        lastTouch = DateTime.UtcNow;
        idleTimer.Stop();
        if (vault is null || Settings.LockMinutes <= 0)
            return;
        idleTimer.Interval = TimeSpan.FromMinutes(Settings.LockMinutes);
        idleTimer.Start();
    }

    private void LockWhenIdle()
    {
        if (jobRunning)
        {
            Touch(); // never in the middle of adding or moving files
            return;
        }
        Lock();
    }

    private void Lock()
    {
        if (vault is null)
            return;
        if (Busy())
            return;
        var problems = vault.CloseAll();
        vault = null;
        current = "";
        lockWhenDone = false;
        idleTimer.Stop();
        ShowScreen();
        if (problems.Count > 0)
            Ui.Warn("Vault", Ui.T("Locked, but:\n\n{0}", string.Join("\n", problems.Take(8))));
    }

    public override bool CanClose()
    {
        if (!jobRunning)
            return true;
        Ui.Info("Vault", Ui.T("Wait for the current job to finish first."));
        return false;
    }

    public override void Closing()
    {
        vault?.CloseAll(); // save edits to opened vault files and remove the readable copies
        idleTimer.Stop();
    }

    /// <summary>The window is kept when closed to the tray; the vault locks with it when that is chosen.</summary>
    public override void Hidden()
    {
        if (vault is null || !Settings.LockOnClose)
            return;
        if (jobRunning)
            lockWhenDone = true;
        else
            Lock();
    }

    // --- jobs ---

    /// <summary>Run work off the window's thread with a progress bar, then done(result) back on it.</summary>
    private async void RunJob(string label, Func<Action<int, int, string, long, long>, (int Count, List<string> Errors)> work,
                              Action<(int Count, List<string> Errors)> done)
    {
        Touch();
        var text = Ui.T(label);
        var fraction = 0.0;
        void Progress(int number, int count, string name, long doneBytes, long size)
        {
            text = Ui.T(label + " {0} of {1}: {2}", number + 1, count, name);
            fraction = (number + (size > 0 ? (double)doneBytes / size : 1)) / count;
        }

        jobRunning = true;
        toolbar!.IsEnabled = false;
        progress!.Visibility = Visibility.Visible;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        ticker.Tick += (_, _) =>
        {
            status!.Text = text;
            progress.Value = fraction * 100;
        };
        ticker.Start();
        (int, List<string>) result;
        try
        {
            result = await Task.Run(() => work(Progress));
        }
        catch (Exception error) // shown to the user, not lost with the thread
        {
            ticker.Stop();
            Finish();
            Ui.Warn("Vault", error.Message);
            AfterJob();
            return;
        }
        ticker.Stop();
        Finish();
        done(result);
        AfterJob();

        void Finish()
        {
            jobRunning = false;
            progress.Visibility = Visibility.Collapsed;
            toolbar.IsEnabled = true;
            Refresh();
            Touch();
        }

        void AfterJob()
        {
            if (lockWhenDone)
                Lock();
            else
                AddWaiting();
        }
    }

    private void Add()
    {
        if (Busy())
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to move into the vault") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || dialog.FileNames.Length == 0)
            return;
        AddPaths(dialog.FileNames.ToList(), current);
    }

    private void AddFolder()
    {
        if (Busy())
            return;
        if (Ui.PickFolder("Folder to move into the vault") is { } folder)
            AddPaths([folder], current);
    }

    /// <summary>Files handed over from outside the page. They go in now if the vault is unlocked, otherwise right after the password.</summary>
    public void AddFromOutside(List<string> paths)
    {
        waiting.AddRange(paths);
        AddWaiting();
    }

    private void AddWaiting()
    {
        if (vault is null || jobRunning || waiting.Count == 0)
            return;
        var paths = waiting.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        waiting.Clear();
        if (paths.Count > 0)
            AddPaths(paths, current);
    }

    /// <summary>Move files and whole folders into the vault folder dir.</summary>
    private void AddPaths(List<string> paths, string dir)
    {
        if (vault is null || Busy())
            return;
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count + folders.Count == 0)
            return;
        var question = folders.Count == 0
            ? Ui.T("Move {0} file(s) into the vault?\n\nEach file is encrypted, checked, and then removed from where it is now "
                   + "(not to the Recycle Bin, which would keep a readable copy).", files.Count)
            : Ui.T("Move {0} folder(s) and {1} file(s) into the vault, with everything in the folders?\n\nEach file is encrypted, checked, and then removed "
                   + "from where it is now (not to the Recycle Bin, which would keep a readable copy).", folders.Count, files.Count);
        if (dir.Length > 0)
            question += "\n\n" + Ui.T("They go into: {0}", VaultPath(dir));
        if (!Ui.Ask("Vault", question))
            return;
        var unlocked = vault;
        RunJob("Adding", progress =>
        {
            var (count, errors) = files.Count > 0 ? unlocked.Add(files, progress, default, dir) : (0, new List<string>());
            if (folders.Count > 0)
            {
                var (more, problems) = unlocked.AddFolders(folders, dir, progress);
                count += more;
                errors.AddRange(problems);
            }
            return (count, errors);
        }, result => Ui.ReportResult(Ui.T("Moved {0} file(s) into the vault.", result.Count), result.Errors));
    }

    // --- what the toolbar does ---

    private void Open()
    {
        var (items, folders) = Selection();
        Touch();
        if (items.Count == 0)
        {
            if (folders.Count > 0)
                Navigate(folders[0]);
            return;
        }
        var errors = new List<string>();
        foreach (var item in items.Take(10))
        {
            try
            {
                if (Shell.Open(vault!.OpenCopy(item)) is { } error)
                    errors.Add($"{item.Name}: {error}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException
                                              or System.Security.Cryptography.CryptographicException)
            {
                errors.Add($"{item.Name}: {error.Message}");
            }
        }
        if (errors.Count > 0)
            Ui.Warn("Vault", Ui.T("Could not open:\n{0}", string.Join("\n", errors)));
        else
            status!.Text = Ui.T("Opened. Changes you save are kept in the vault when it locks; the open copy is removed then.");
    }

    private void NewFolder()
    {
        if (vault is null || Busy())
            return;
        var name = TextDialog.Ask(Window.GetWindow(this)!, "New folder", Ui.T("Name of the new folder:"), Ui.T("New folder"));
        if (string.IsNullOrWhiteSpace(name))
            return;
        var made = "";
        if (search is { Text.Length: > 0 })
            search.Text = ""; // show the folder it is made in
        if (Change(() => made = vault.CreateFolder(current, name)))
            Select(new FolderTag(made));
    }

    private void Rename()
    {
        if (vault is null || list is null || Busy())
            return;
        var rows = Ui.Selected(list);
        if (rows.Count != 1)
        {
            Ui.Info("Vault", Ui.T("Select one file or folder to rename."));
            return;
        }
        var owner = Window.GetWindow(this)!;
        if (rows[0].Tag is FolderTag folder)
        {
            var name = TextDialog.Ask(owner, "Rename", Ui.T("New name for the folder:"), Core.Vault.DirName(folder.Dir));
            var renamed = folder.Dir;
            if (!string.IsNullOrWhiteSpace(name) && Change(() => renamed = vault.RenameFolder(folder.Dir, name)))
                Select(new FolderTag(renamed));
        }
        else if (rows[0].Tag is VaultItem item)
        {
            var name = TextDialog.Ask(owner, "Rename", Ui.T("New name for the file:"), item.Name);
            if (!string.IsNullOrWhiteSpace(name) && Change(() => vault.Rename(item, name)))
                Select(item);
        }
    }

    private void MoveTo()
    {
        if (vault is null || Busy())
            return;
        var (files, folders) = Selection();
        if (files.Count + folders.Count == 0)
            return;
        var start = files.Count > 0 && folders.Count == 0 && files.All(item => item.Dir.Equals(files[0].Dir, StringComparison.OrdinalIgnoreCase)) ? files[0].Dir : current;
        if (VaultFolderDialog.Ask(Window.GetWindow(this)!, vault.Folders(), folders, start) is not { } dir)
            return;
        MoveInto([.. files, .. folders.Select(folder => new FolderTag(folder))], dir);
    }

    /// <summary>Move files and folders (by their row tags) into the vault folder dir.</summary>
    private void MoveInto(List<object> tags, string dir)
    {
        if (vault is null || Busy())
            return;
        var moved = 0;
        var errors = new List<string>();
        var files = tags.OfType<VaultItem>().Where(item => !item.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList();
        try
        {
            if (files.Count > 0)
            {
                vault.Move(files, dir);
                moved += files.Count;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException)
        {
            errors.Add(error.Message);
        }
        foreach (var folder in tags.OfType<FolderTag>())
        {
            try
            {
                if (!vault.MoveFolder(folder.Dir, dir).Equals(folder.Dir, StringComparison.OrdinalIgnoreCase))
                    moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException)
            {
                errors.Add($"{Core.Vault.DirName(folder.Dir)}: {error.Message}");
            }
        }
        Refresh();
        Touch();
        var message = Ui.T("Moved {0} item(s) to {1}.", moved, VaultPath(dir));
        if (errors.Count > 0)
            Ui.ReportResult(message, errors);
        else
            status!.Text = message;
    }

    private void MoveOut()
    {
        if (vault is null || Busy())
            return;
        var (items, folders) = Selection();
        if (items.Count + folders.Count == 0)
            return;
        var unlocked = vault;
        if (folders.Count > 0)
        {
            // Folders go out whole, into a folder chosen for them (with any files picked alongside).
            var target = Ui.PickFolder("Move them out of the vault, into");
            if (target is null)
                return;
            RunJob("Moving out", progress =>
            {
                var (count, errors) = items.Count > 0 ? unlocked.MoveOut(items, target, progress) : (0, new List<string>());
                foreach (var folder in folders)
                {
                    var (more, problems) = unlocked.MoveOutFolder(folder, target, progress);
                    count += more;
                    errors.AddRange(problems);
                }
                return (count, errors);
            }, result => Ui.ReportResult(Ui.T("Moved {0} file(s) out of the vault. They are normal, readable files again.", result.Count), result.Errors));
            return;
        }
        var choice = MessageBox.Show(Window.GetWindow(this)!, Ui.T("Move {0} file(s) out of the vault, back to the folder each came from?\n\n"
                                                                  + "Yes: back where they came from\nNo: choose a folder", items.Count),
                                     Ui.T("Move out"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
            return;
        string? destination = null;
        if (choice == MessageBoxResult.No)
        {
            destination = Ui.PickFolder("Move the files to");
            if (destination is null)
                return;
        }
        RunJob("Moving out", progress => unlocked.MoveOut(items, destination, progress),
               result => Ui.ReportResult(Ui.T("Moved {0} file(s) out of the vault. They are normal, readable files again.", result.Count), result.Errors));
    }

    private void Delete()
    {
        if (vault is null || Busy())
            return;
        var (items, folders) = Selection();
        if (items.Count + folders.Count == 0)
            return;
        var shown = folders.Select(folder => Ui.T("{0} (folder)", Core.Vault.DirName(folder))).Concat(items.Select(item => item.Name)).ToList();
        var names = string.Join("\n", shown.Take(12)) + (shown.Count > 12 ? "\n…" : "");
        var question = folders.Count == 0
            ? Ui.T("Delete {0} file(s) from the vault for good? They can't be brought back.\n\n{1}", items.Count, names)
            : Ui.T("Delete {0} folder(s) and {1} file(s) from the vault for good? Everything inside the folders is deleted too, {2} file(s) in all. "
                   + "They can't be brought back.\n\n{3}", folders.Count, items.Count,
                   items.Count + folders.Sum(folder => vault.FilesUnder(folder).Count), names);
        if (!Ui.Ask("Delete from vault", question, warning: true))
            return;
        var (count, errors) = vault.Delete(items);
        foreach (var folder in folders)
        {
            var (more, problems) = vault.DeleteFolder(folder);
            count += more;
            errors.AddRange(problems);
        }
        Refresh();
        Touch();
        Ui.ReportResult(Ui.T("Deleted {0} file(s) from the vault.", count), errors);
    }

    private void ChangePassword()
    {
        Touch();
        if (vault is not null)
            ChangePasswordDialog.Show(Window.GetWindow(this)!, vault);
    }

    /// <summary>Save a recovery key file: with it, a forgotten password can be replaced.</summary>
    private void MakeRecoveryKey()
    {
        Touch();
        if (vault is null)
            return;
        var owner = Window.GetWindow(this)!;
        var intro = Ui.T("A recovery key is a file that lets you set a new password if you forget this one. "
                         + "Keep it somewhere safe away from this PC, such as a USB stick: anyone who has it and this PC can open the vault.");
        if (Core.Vault.HasRecoveryKey)
            intro += "\n\n" + Ui.T("This vault already has a recovery key. Making a new one makes the old file stop working.");
        string? password = null;
        if (!PasswordsDialog.Show(owner, "Recovery key", intro, ["Current password"], "Continue", typed =>
            {
                try
                {
                    Core.Vault.Unlock(typed[0]);
                }
                catch (VaultException error)
                {
                    return error.Message;
                }
                password = typed[0];
                return null;
            }))
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Ui.T("Save the recovery key"), FileName = Core.Vault.RecoveryFileName,
            Filter = Ui.T("Recovery key") + " (*.vaultkey)|*.vaultkey", DefaultExt = ".vaultkey",
        };
        if (dialog.ShowDialog(owner) != true)
            return;
        try
        {
            vault.CreateRecoveryKey(dialog.FileName, password!);
        }
        catch (Exception error) when (error is VaultException or IOException or UnauthorizedAccessException)
        {
            Ui.Warn("Recovery key", error.Message);
            return;
        }
        Ui.Info("Recovery key", Ui.T("The recovery key is saved:\n{0}\n\nIf you forget the password, put this file in the program's folder:\n{1}\n"
                                     + "then click \"Forgot your password?\" on the Vault page.",
                                     dialog.FileName, AppContext.BaseDirectory.TrimEnd('\\')));
    }

    /// <summary>Set a new password with the recovery key file beside the program (or one picked by hand).</summary>
    private void ResetWithRecoveryKey()
    {
        var owner = Window.GetWindow(this)!;
        var file = Core.Vault.RecoveryFileBesideProgram;
        if (!File.Exists(file))
        {
            if (!Ui.Ask("Recovery key", Ui.T("Put your recovery key file ({0}) in the program's folder:\n{1}\n\nOr choose the file now?",
                                             Core.Vault.RecoveryFileName, AppContext.BaseDirectory.TrimEnd('\\'))))
                return;
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = Ui.T("Recovery key"), Filter = Ui.T("Recovery key") + " (*.vaultkey)|*.vaultkey|*.*|*.*",
            };
            if (pick.ShowDialog(owner) != true)
                return;
            file = pick.FileName;
        }
        if (!PasswordsDialog.Show(owner, "Recovery key", Ui.T("The recovery key was found. Choose a new password for the vault."),
                                  ["New password", "Type it again"], "Change", typed =>
            {
                if (typed[0] != typed[1])
                    return Ui.T("The new passwords are not the same.");
                try
                {
                    vault = Core.Vault.ResetPassword(file, typed[0]);
                }
                catch (VaultException error)
                {
                    return error.Message;
                }
                return null;
            }))
            return;
        Touch();
        ShowScreen();
        var beside = string.Equals(Path.GetFullPath(file), Path.GetFullPath(Core.Vault.RecoveryFileBesideProgram), StringComparison.OrdinalIgnoreCase);
        Ui.Info("Recovery key", Ui.T("The password is changed and the vault is open.")
                                + (beside ? "\n\n" + Ui.T("Take the recovery key file out of the program's folder again and keep it safe: anyone who has it can do the same.") : ""));
    }
}

/// <summary>Pick one of the vault's folders, the top shown as "Vault", as a tree.</summary>
internal sealed class VaultFolderDialog : Dialog
{
    private readonly ListBox box = new() { Height = 320 };
    private string? answer;

    private VaultFolderDialog(Window owner, List<string> folders, List<string> moving, string start) : base(owner, "Move to")
    {
        var body = new StackPanel { Margin = new Thickness(18), Width = 380 };
        body.Children.Add(Ui.Text(Ui.T("Choose the vault folder to move them into."), wrap: true).Margin(0, 0, 0, 10));
        // Each folder under the one it is in, whatever the sort order of the full paths.
        var children = folders.GroupBy(Core.Vault.ParentDir, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(group => group.Key, group => group.OrderBy(Core.Vault.DirName, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        void AddFolder(string dir, int depth)
        {
            var item = new ListBoxItem
            {
                Tag = dir,
                Content = Ui.Row(8, Ui.Glyph(dir.Length == 0 ? "\uE72E" : "\uE8B7", 14, "Primary"), Ui.Text(dir.Length == 0 ? Ui.T("Vault") : Core.Vault.DirName(dir))).Margin(depth * 18),
                // A folder can't go into itself or a folder inside it.
                IsEnabled = !moving.Any(folder => Core.Vault.IsInside(dir, folder)),
            };
            box.Items.Add(item);
            if (dir.Equals(start, StringComparison.OrdinalIgnoreCase))
                box.SelectedItem = item;
            foreach (var child in children.GetValueOrDefault(dir) ?? [])
                AddFolder(child, depth + 1);
        }
        AddFolder("", 0);
        box.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
                Accept();
        };
        body.Children.Add(box);
        body.Children.Add(Buttons("Move"));
        Content = body;
        Loaded += (_, _) =>
        {
            box.Focus();
            if (box.SelectedItem is ListBoxItem selected)
            {
                box.ScrollIntoView(selected);
                selected.Focus();
            }
        };
    }

    /// <summary>The folder chosen ("" for the top), or null when cancelled.</summary>
    public static string? Ask(Window owner, List<string> folders, List<string> moving, string start)
    {
        var dialog = new VaultFolderDialog(owner, folders, moving, start);
        dialog.ShowDialog();
        return dialog.answer;
    }

    protected override void Accept()
    {
        if (box.SelectedItem is not ListBoxItem { Tag: string dir, IsEnabled: true })
            return;
        answer = dir;
        Close();
    }
}
