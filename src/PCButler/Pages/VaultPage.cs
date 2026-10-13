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

/// <summary>Password-protected, encrypted storage for private files.</summary>
public sealed class VaultPage : Page
{
    private const int IdleMinutes = 10; // the vault locks itself after this long without use
    private readonly ContentControl screen = new();
    private readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromMinutes(IdleMinutes) };
    private Core.Vault? vault; // null while locked
    private bool jobRunning;
    private readonly List<string> waiting = []; // sent from File Explorer or the drop zone, added once the vault is unlocked
    // The unlocked screen.
    private Button? addButton;
    private CardPanel? panel;
    private TextBox? search;
    private TextBlock? status;
    private ProgressBar? progress;
    private ListView? list;

    public VaultPage(MainWindow main) : base(main, "Vault", "Your private space. Under your control.")
    {
        Core.Vault.ClearOpenCopies(); // copies left behind if the program was closed abruptly
        idleTimer.Tick += (_, _) => LockWhenIdle();
        Children.Add(screen);
        ShowScreen();
    }

    private void ShowScreen()
    {
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

    // --- unlocked: the files ---

    private void BuildUnlocked()
    {
        var layout = new DockPanel();
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        addButton = Ui.Button(Ui.T("Add files…"), Add, accent: true);
        top.Children.Add(addButton);
        top.Children.Add(Ui.Button(Ui.T("Open"), Open).Margin(6));
        top.Children.Add(Ui.Button(Ui.T("Move out…"), MoveOut).Margin(6));
        top.Children.Add(Ui.Button(Ui.T("Delete…"), Delete).Margin(6));
        top.Children.Add(Ui.Button(Ui.T("Lock"), Lock).Dock(Dock.Right));
        top.Children.Add(Ui.Button(Ui.T("Change password…"), ChangePassword).Margin(0, 0, 6, 0).Dock(Dock.Right));
        top.Children.Add(Ui.Button(Ui.T("Recovery key…"), MakeRecoveryKey).Margin(0, 0, 6, 0).Dock(Dock.Right));
        layout.Children.Add(top.Dock(Dock.Top));

        panel = new CardPanel("Vault files", "\uE72E");
        search = Ui.Field(width: 220, hint: Ui.T("Search files…"));
        search.TextChanged += (_, _) => Refresh();
        panel.Controls.Children.Add(search);
        var footer = panel.AddFooter();
        status = Ui.Muted("");
        progress = new ProgressBar { Width = 200, Visibility = Visibility.Collapsed };
        footer.Children.Add(progress.Dock(Dock.Right));
        footer.Children.Add(status);
        list = Ui.List(("File name", 360, "FileCell"), ("Size", 90), ("Date added", 140), ("Source", 220));
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && Ui.RowAt(e.OriginalSource) is not null)
                Open();
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Open();
            else if (e.Key == Key.Delete)
                Delete();
        };
        panel.SetBody(list);
        layout.Children.Add(panel);
        screen.Content = layout;
        Refresh();
    }

    private void Refresh()
    {
        if (vault is null || list is null)
            return;
        var needle = search!.Text.Trim().ToLowerInvariant();
        var items = vault.Items.OrderBy(item => item.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        var rows = items.Where(item => needle.Length == 0 || item.Name.ToLowerInvariant().Contains(needle)).Select(item => new Row
        {
            Name = item.Name, Tag = item,
            Cells = [Ui.FormatSize(item.Size), DateTimeOffset.FromUnixTimeMilliseconds((long)(item.Added * 1000)).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                     Path.GetFileName(item.Folder.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : item.Folder],
        }.WithTile(Path.GetExtension(item.Name))).ToList();
        list.ItemsSource = rows;
        panel!.SetCount(Ui.T("{0} file(s)", items.Count));
        var size = Ui.FormatSize(items.Sum(item => item.Size));
        status!.Text = needle.Length > 0
            ? Ui.T("Showing {0} of {1} file(s), {2}. Locks itself after {3} minutes without use.", rows.Count, items.Count, size, IdleMinutes)
            : Ui.T("{0} file(s), {1}. Locks itself after {2} minutes without use.", items.Count, size, IdleMinutes);
    }

    private List<VaultItem> SelectedItems()
    {
        List<VaultItem> items = list is null ? [] : Ui.Selected(list).Select(row => (VaultItem)row.Tag!).ToList();
        if (items.Count == 0)
            Ui.Info("Vault", Ui.T("Select one or more files first."));
        return items;
    }

    /// <summary>Restart the idle timer that locks the vault.</summary>
    private void Touch()
    {
        idleTimer.Stop();
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
        if (jobRunning)
        {
            Ui.Info("Vault", Ui.T("Wait for the current job to finish first."));
            return;
        }
        var problems = vault.CloseAll();
        vault = null;
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
        addButton!.IsEnabled = false;
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
            result = default;
            ticker.Stop();
            Finish();
            Ui.Warn("Vault", error.Message);
            return;
        }
        ticker.Stop();
        Finish();
        done(result);

        void Finish()
        {
            jobRunning = false;
            progress.Visibility = Visibility.Collapsed;
            addButton.IsEnabled = true;
            Refresh();
            Touch();
        }
    }

    private void Add()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to move into the vault") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || dialog.FileNames.Length == 0)
            return;
        AddPaths(dialog.FileNames.ToList());
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
        var paths = waiting.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        waiting.Clear();
        if (paths.Count > 0)
            AddPaths(paths);
    }

    private void AddPaths(List<string> paths)
    {
        if (!Ui.Ask("Vault", Ui.T("Move {0} file(s) into the vault?\n\nEach file is encrypted, checked, and then removed from where it is now "
                                  + "(not to the Recycle Bin, which would keep a readable copy).", paths.Count)))
            return;
        var unlocked = vault!;
        RunJob("Adding", progress => unlocked.Add(paths, progress),
               result => Ui.ReportResult(Ui.T("Moved {0} file(s) into the vault.", result.Count), result.Errors));
    }

    private void Open()
    {
        var items = SelectedItems();
        Touch();
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
        else if (items.Count > 0)
            status!.Text = Ui.T("Opened. Changes you save are kept in the vault when it locks; the open copy is removed then.");
    }

    private void MoveOut()
    {
        var items = SelectedItems();
        if (items.Count == 0)
            return;
        var choice = MessageBox.Show(Window.GetWindow(this)!, Ui.T("Move {0} file(s) out of the vault, back to the folder each came from?\n\n"
                                                                  + "Yes: back where they came from\nNo: choose a folder", items.Count),
                                     Ui.T("Move out"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
            return;
        string? folder = null;
        if (choice == MessageBoxResult.No)
        {
            folder = Ui.PickFolder("Move the files to");
            if (folder is null)
                return;
        }
        var unlocked = vault!;
        RunJob("Moving out", progress => unlocked.MoveOut(items, folder, progress),
               result => Ui.ReportResult(Ui.T("Moved {0} file(s) out of the vault. They are normal, readable files again.", result.Count), result.Errors));
    }

    private void Delete()
    {
        var items = SelectedItems();
        if (items.Count == 0)
            return;
        var names = string.Join("\n", items.Take(12).Select(item => item.Name)) + (items.Count > 12 ? "\n…" : "");
        if (!Ui.Ask("Delete from vault", Ui.T("Delete {0} file(s) from the vault for good? They can't be brought back.\n\n{1}", items.Count, names), warning: true))
            return;
        var (count, errors) = vault!.Delete(items);
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
