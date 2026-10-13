// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCButler.Core;

namespace PCButler;

/// <summary>A small fixed-size dialog in the program's look, closed with Escape.</summary>
public abstract class Dialog : Window
{
    protected Dialog(Window owner, string title)
    {
        Title = Ui.T(title);
        Ui.StyleWindow(this);
        Owner = owner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter && e.OriginalSource is not Button)
            {
                Accept();
                e.Handled = true;
            }
        };
    }

    protected abstract void Accept();

    /// <summary>The OK-style button and Cancel, on the right.</summary>
    protected StackPanel Buttons(string accept)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Ui.Button(Ui.T(accept), Accept, accent: true));
        buttons.Children.Add(Ui.Button(Ui.T("Cancel"), Close).Margin(6));
        return buttons;
    }
}

/// <summary>Add or edit one destination folder and its file types.</summary>
public sealed partial class CategoryDialog : Dialog
{
    private readonly TextBox folder;
    private readonly TextBox extensions;
    private readonly TextBox age;
    private readonly TextBox recycle;
    private Category? result;

    private CategoryDialog(Window owner, Category? category) : base(owner, "Category")
    {
        category ??= new Category();
        folder = Ui.Field(category.Folder, 420);
        extensions = Ui.Field(string.Join(" ", category.Extensions), 420);
        age = Ui.Field(category.MinAgeDays?.ToString() ?? "", 80);
        recycle = Ui.Field(category.RecycleAfterDays.ToString(), 80);
        age.HorizontalAlignment = recycle.HorizontalAlignment = HorizontalAlignment.Left;

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(Ui.Text(Ui.T("Destination folder")));
        body.Children.Add(Ui.Row(6, folder, Ui.Button(Ui.T("Browse…"), Browse)).Margin(0, 2));
        body.Children.Add(Ui.Muted(Ui.T("A plain name creates the folder inside the folder being sorted."), 12));
        body.Children.Add(Ui.Text(Ui.T("File extensions (separated by spaces or commas)")).Margin(0, 10));
        body.Children.Add(extensions.Margin(0, 2));
        body.Children.Add(Ui.Text(Ui.T("Move these files after (days)")).Margin(0, 10));
        body.Children.Add(age.Margin(0, 2));
        body.Children.Add(Ui.Muted(Ui.T("Leave blank to use the general setting."), 12));
        body.Children.Add(Ui.Text(Ui.T("Clean up: send to the Recycle Bin after (days in this folder)")).Margin(0, 10));
        body.Children.Add(recycle.Margin(0, 2));
        body.Children.Add(Ui.Muted(Ui.T("0 = never. Only files this program moved here are cleaned up,\nand you can bring them back from the History tab."), 12));
        body.Children.Add(Buttons("OK"));
        Content = body;
        Loaded += (_, _) => folder.Focus();
    }

    /// <summary>The edited category, or null if cancelled.</summary>
    public static Category? Ask(Window owner, Category? category)
    {
        var dialog = new CategoryDialog(owner, category);
        dialog.ShowDialog();
        return dialog.result;
    }

    private void Browse()
    {
        if (Ui.PickFolder("Destination folder", folder.Text) is { } chosen)
            folder.Text = chosen;
    }

    [GeneratedRegex(@"[,\s;]+")]
    private static partial Regex ExtensionSeparators();

    protected override void Accept()
    {
        var name = folder.Text.Trim();
        var types = ExtensionSeparators().Split(extensions.Text).Where(part => part.Trim('.').Length > 0)
                                          .Select(part => part.ToLowerInvariant()).Select(part => part.StartsWith('.') ? part : "." + part)
                                          .Distinct().Order(StringComparer.Ordinal).ToList();
        int? days = null;
        var valid = true;
        if (age.Text.Trim() is { Length: > 0 } ageText)
        {
            if (int.TryParse(ageText, out var parsed) && parsed >= 0)
                days = parsed;
            else
                valid = false;
        }
        var recycleDays = 0;
        if (recycle.Text.Trim() is { Length: > 0 } recycleText && !(int.TryParse(recycleText, out recycleDays) && recycleDays >= 0))
            valid = false;
        if (!valid)
        {
            Ui.Warn("Category", Ui.T("Days must be whole numbers, 0 or more."));
            return;
        }
        if (name.Length == 0 || types.Count == 0)
        {
            Ui.Warn("Category", Ui.T("Enter a folder and at least one extension."));
            return;
        }
        result = new Category { Folder = name, Extensions = types, MinAgeDays = days, RecycleAfterDays = recycleDays };
        Close();
    }
}

/// <summary>
/// A short explanation and one or more password fields. accept gets what was typed and returns
/// an error to show, or null to close the dialog.
/// </summary>
public sealed class PasswordsDialog : Dialog
{
    private readonly PasswordBox[] boxes;
    private readonly TextBlock message = Ui.Text("", brush: "Amber", wrap: true);
    private readonly Func<string[], string?> accept;
    private bool accepted;

    private PasswordsDialog(Window owner, string title, string intro, string[] labels, string acceptText, Func<string[], string?> accept)
        : base(owner, title)
    {
        this.accept = accept;
        var body = new StackPanel { Margin = new Thickness(18), Width = 420 };
        body.Children.Add(Ui.Text(intro, wrap: true).Margin(0, 0, 0, 12));
        boxes = labels.Select(_ => new PasswordBox()).ToArray();
        for (var index = 0; index < labels.Length; index++)
        {
            body.Children.Add(Ui.Muted(Ui.T(labels[index])));
            body.Children.Add(boxes[index].Margin(0, 4, 0, 10));
        }
        body.Children.Add(message);
        body.Children.Add(Buttons(acceptText));
        Content = body;
        Loaded += (_, _) => boxes[0].Focus();
    }

    /// <summary>True when accept succeeded and closed the dialog.</summary>
    public static bool Show(Window owner, string title, string intro, string[] labels, string acceptText, Func<string[], string?> accept)
    {
        var dialog = new PasswordsDialog(owner, title, intro, labels, acceptText, accept);
        dialog.ShowDialog();
        return dialog.accepted;
    }

    protected override void Accept()
    {
        Cursor = Cursors.Wait;
        string? error;
        try
        {
            error = accept(boxes.Select(box => box.Password).ToArray());
        }
        finally
        {
            Cursor = null;
        }
        if (error is not null)
        {
            message.Text = error;
            return;
        }
        accepted = true;
        Close();
    }
}

/// <summary>Change the vault's password (the files stay as they are; only their key is re-wrapped).</summary>
public sealed class ChangePasswordDialog : Dialog
{
    private readonly Core.Vault vault;
    private readonly PasswordBox current = new() { Width = 260 };
    private readonly PasswordBox fresh = new() { Width = 260 };
    private readonly PasswordBox again = new() { Width = 260 };
    private readonly TextBlock message = Ui.Text("", brush: "Amber");

    private ChangePasswordDialog(Window owner, Core.Vault vault) : base(owner, "Change vault password")
    {
        this.vault = vault;
        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var fields = new (string Label, PasswordBox Box)[] { ("Current password", current), ("New password", fresh), ("Type it again", again) };
        for (var index = 0; index < fields.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(Ui.Text(Ui.T(fields[index].Label)).Margin(0, 4, 12, 4).At(index, 0));
            grid.Children.Add(fields[index].Box.Margin(0, 4, 0, 4).At(index, 1));
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(message.At(3, 0, 2));
        grid.Children.Add(Buttons("Change").At(4, 0, 2));
        Content = grid;
        Loaded += (_, _) => current.Focus();
    }

    public static void Show(Window owner, Core.Vault vault) => new ChangePasswordDialog(owner, vault).ShowDialog();

    protected override void Accept()
    {
        if (fresh.Password != again.Password)
        {
            message.Text = Ui.T("The new passwords are not the same.");
            return;
        }
        try
        {
            vault.ChangePassword(current.Password, fresh.Password);
        }
        catch (VaultException error)
        {
            message.Text = error.Message;
            return;
        }
        Close();
        Ui.Info("Vault", Ui.T("The password is changed."));
    }
}
