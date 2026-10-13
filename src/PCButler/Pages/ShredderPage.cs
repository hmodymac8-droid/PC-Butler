// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>Deep delete: overwrite files with random data, then delete them, so they can't be recovered.</summary>
public sealed class ShredderPage : Page
{
    private readonly StatCard[] stats;
    private readonly ListView list;
    private readonly ProgressBar progress = new() { Width = 220, Height = 6, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = Ui.Muted("");
    private readonly Button shredButton;
    private readonly List<string> chosen = [];
    private CancellationTokenSource? running;

    public ShredderPage(MainWindow main) : base(main, "Shredder", "Gone means gone.")
    {
        shredButton = AddAction("Shred", ShredAll, accent: true);
        stats = AddStats(("Items chosen", ""), ("Files inside", ""), ("Size", ""));

        var hint = Ui.Muted(Ui.T("Each file is overwritten with random data and then deleted. It does not go to the Recycle Bin and cannot be "
                                 + "undone. On an SSD the drive may keep old copies in cells it has not reused yet, out of reach of "
                                 + "ordinary recovery tools."), 11, wrap: true);
        hint.TextAlignment = TextAlignment.Center;
        Children.Add(hint.Margin(40, 10, 40, 0).Dock(Dock.Bottom));

        var panel = new CardPanel("To shred", "");
        panel.Controls.Children.Add(Ui.Button(Ui.T("Add files…"), AddFiles));
        panel.Controls.Children.Add(Ui.Button(Ui.T("Add folder…"), AddFolder).Margin(6));
        panel.Controls.Children.Add(Ui.Button(Ui.T("Remove from list"), RemoveSelected).Margin(6));
        var footer = panel.AddFooter();
        footer.Children.Add(progress.Dock(Dock.Right));
        footer.Children.Add(status);
        list = Ui.List(("Name", 320, "PlainCell"), ("Kind", 90), ("Size", 110), ("Where", 420));
        // Dropping files on the list adds them; nothing is shredded until Shred is pressed and confirmed.
        list.AllowDrop = true;
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                AddPaths(dropped);
        };
        panel.SetBody(list);
        Children.Add(panel);
        ShowChosen();
    }

    /// <summary>Add items from elsewhere (the drop zone, File Explorer's right-click entry).</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var refused = new List<string>();
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            if (Shredder.IsProtected(path))
                refused.Add(path);
            else if ((File.Exists(path) || Directory.Exists(path)) && !chosen.Contains(path, StringComparer.OrdinalIgnoreCase))
                chosen.Add(path);
        }
        ShowChosen();
        if (refused.Count > 0)
            Ui.ReportResult(Ui.T("Some folders are protected and are never shredded."), refused);
    }

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to shred") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AddPaths(dialog.FileNames);
    }

    private void AddFolder()
    {
        if (Ui.PickFolder(Ui.T("Folder to shred")) is { } folder)
            AddPaths([folder]);
    }

    private void RemoveSelected()
    {
        foreach (var row in Ui.Selected(list))
            chosen.Remove((string)row.Tag!);
        ShowChosen();
    }

    private void ShowChosen()
    {
        chosen.RemoveAll(path => !File.Exists(path) && !Directory.Exists(path));
        list.ItemsSource = chosen.Select(path =>
        {
            var folder = Directory.Exists(path);
            var size = Shredder.Measure([path]).Size;
            return new Row
            {
                Name = Path.GetFileName(path.TrimEnd('\\')), Tag = path,
                Cells = [Ui.T(folder ? "Folder" : "File"), Ui.FormatSize(size), Path.GetDirectoryName(path.TrimEnd('\\')) ?? ""],
            };
        }).ToList();
        var (files, total) = Shredder.Measure(chosen);
        stats[0].Set(chosen.Count.ToString());
        stats[1].Set(files.ToString());
        var parts = Ui.FormatSize(total).Split(' ');
        stats[2].Set(parts[0], parts[1]);
        shredButton.IsEnabled = chosen.Count > 0 || running is not null;
    }

    private async void ShredAll()
    {
        if (running is not null)
        {
            running.Cancel();
            return;
        }
        if (chosen.Count == 0)
            return;
        var (files, size) = Shredder.Measure(chosen);
        if (!Ui.Ask("Shredder", Ui.T("Shred {0} item(s): {1} file(s), {2}?\n\nThey are overwritten and deleted. This cannot be undone, "
                                     + "not from History and not from the Recycle Bin.", chosen.Count, files, Ui.FormatSize(size)), warning: true))
            return;
        running = new CancellationTokenSource();
        shredButton.Content = Ui.T("Stop");
        progress.Value = 0;
        progress.Visibility = Visibility.Visible;
        status.Text = Ui.T("Shredding…");
        var paths = chosen.ToList();
        var token = running.Token;
        var result = await Task.Run(() => Shredder.Shred(paths, (done, total) => Dispatcher.BeginInvoke(() =>
            progress.Value = total > 0 ? done * 100.0 / total : 100), token));
        running.Dispose();
        running = null;
        shredButton.Content = Ui.T("Shred");
        progress.Visibility = Visibility.Collapsed;
        status.Text = "";
        ShowChosen();
        Ui.ReportResult(Ui.T("Shredded {0} file(s).", result.Count), result.Errors);
    }

    public override bool CanClose() => running is null;
}
