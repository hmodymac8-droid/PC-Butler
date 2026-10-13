// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The small window behind the single "Process with PC Butler" entry of versions 1.8 to 1.9.0: what to do with the
/// file or folder that was right-clicked. Everything done here is logged in History and
/// can be undone there.
/// </summary>
public sealed class ProcessWindow : Window
{
    private readonly string path;

    public ProcessWindow(string item)
    {
        var config = ConfigStore.Load();
        var plan = ExplorerMenu.Plan(item, config);
        path = plan.Path;
        Ui.StyleWindow(this);
        Title = AppInfo.Name;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true; // it was asked for from another program's window

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4));
        var name = Ui.Text(Path.GetFileName(path.TrimEnd('\\')), 20, weight: FontWeights.Bold, wrap: true);
        name.FontFamily = Ui.DisplayFont;
        name.FlowDirection = FlowDirection.LeftToRight;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(name);
        var where = Ui.Muted(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "", 12, wrap: true);
        where.FlowDirection = FlowDirection.LeftToRight;
        where.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(where.Margin(0, 2, 0, 14));

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            body.Children.Add(Ui.Text(Ui.T("It is no longer there."), wrap: true));
        }
        else if (plan.IsFolder)
        {
            Add(body, plan.Moves.Count > 0 ? Ui.T("Sort its {0} loose file(s) into category folders", plan.Moves.Count)
                                           : Ui.T("Nothing to sort: no loose files here match a category"),
                plan.Moves.Count > 0, () => Sort(plan), accent: true);
        }
        else
        {
            Add(body, plan.Destination.Length > 0 ? Ui.T("Sort it into “{0}”", Path.GetFileName(plan.Destination))
                                                  : Ui.T("Nothing to sort: no category takes this kind of file"),
                plan.Destination.Length > 0, () => Sort(plan), accent: true);
            Add(body, Ui.T("Lock it in the Vault"), true, () =>
            {
                // The main window holds the vault and asks for its password, so the file is handed over to it.
                VaultInbox.Add([path]);
                Shell.Open(Environment.ProcessPath!);
                Close();
            });
            Add(body, Ui.T("Move it to the Recycle Bin"), true, Recycle);
        }
        if (File.Exists(path) || Directory.Exists(path))
            Add(body, Ui.T("Shred it (deep delete, cannot be undone)"), !Shredder.IsProtected(path), Shred);
        Add(body, Ui.T($"Open {AppInfo.Name}"), true, () =>
        {
            Shell.Open(Environment.ProcessPath!);
            Close();
        });
        var cancel = Ui.Button(Ui.T("Cancel"), Close);
        cancel.IsCancel = true;
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(cancel.Margin(0, 12, 0, 0));
        body.Children.Add(Ui.Muted(Ui.T("What you do here is logged in History and can be undone there."), 11, wrap: true).Margin(0, 10, 0, 0));
        Content = body;
    }

    private static void Add(StackPanel body, string text, bool enabled, Action action, bool accent = false)
    {
        var button = Ui.Button(text, action, accent && enabled);
        button.IsEnabled = enabled;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        body.Children.Add(button.Margin(0, 0, 0, 6));
    }

    private void Sort(ProcessPlan plan)
    {
        var (count, errors) = Sorter.MoveFiles(plan.Moves);
        Hide();
        Ui.ReportResult(Ui.T("Sorted {0} file(s).", count), errors);
        Close();
    }

    private void Shred()
    {
        var (files, size) = Shredder.Measure([path]);
        if (!Ui.Ask("Shredder", Ui.T("Shred {0} item(s): {1} file(s), {2}?\n\nThey are overwritten and deleted. This cannot be undone, "
                                     + "not from History and not from the Recycle Bin.", 1, files, Ui.FormatSize(size)), warning: true))
            return;
        Hide();
        var (count, errors) = Shredder.Shred([path]);
        Ui.ReportResult(Ui.T("Shredded {0} file(s).", count), errors);
        Close();
    }

    private void Recycle()
    {
        var (count, errors) = Sorter.DeleteFiles([path]);
        Hide();
        if (count == 0 || errors.Count > 0)
            Ui.ReportResult(Ui.T("It could not be moved to the Recycle Bin."), errors);
        Close();
    }
}
