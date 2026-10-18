// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// "Move to another drive": pick the drive, then watch the copy. Used from Search, Storage
/// and File Explorer's PC Butler menu, and (to move things back) from History. The work is
/// DriveMover's; this window only asks where and shows how far it got.
/// </summary>
public sealed class DriveMoveWindow : Window
{
    private readonly StackPanel body = new() { Margin = new Thickness(22, 18, 22, 18) };
    private readonly StackPanel drives = new();
    private readonly TextBlock summary = Ui.Muted("", 12.5, wrap: true);
    private readonly ProgressBar bar = new() { Height = 6, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 6) };
    private readonly TextBlock progressText = Ui.Muted("", 12.5, wrap: true);
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? cancel;
    private long copied;
    private long total = -1;

    /// <summary>True when something was moved, so the caller can refresh its lists.</summary>
    public bool Changed { get; private set; }

    private DriveMoveWindow(string heading)
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Move drives");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        body.Children.Add(Ui.Text(Ui.T(Ui.Eyebrow), 10, "Primary", FontWeights.Bold).Margin(0, 0, 0, 4));
        var title = Ui.Text(heading, 20, weight: FontWeights.Bold, wrap: true);
        title.FontFamily = Ui.DisplayFont;
        body.Children.Add(title);
        body.Children.Add(summary.Margin(0, 4, 0, 0));
        body.Children.Add(drives);
        body.Children.Add(bar);
        body.Children.Add(progressText);
        body.Children.Add(buttons);
        Content = body;
        tick.Tick += (_, _) =>
        {
            if (total > 0)
            {
                bar.Value = Math.Min(1, (double)Interlocked.Read(ref copied) / total);
                progressText.Text = Ui.T("Copying… {0} of {1}", Ui.FormatSize(Interlocked.Read(ref copied)), Ui.FormatSize(total));
            }
        };
        // Closing while it copies stops the copy (and puts everything back) instead.
        Closing += (_, e) =>
        {
            if (cancel is null)
                return;
            e.Cancel = true;
            cancel.Cancel();
            progressText.Text = Ui.T("Stopping and putting everything back…");
        };
    }

    private static void ShowOver(Window? owner, DriveMoveWindow window)
    {
        if (owner is { IsVisible: true })
            window.Owner = owner;
        else
            window.Topmost = true; // asked for from File Explorer
        window.ShowDialog();
    }

    /// <summary>Ask where to move these files and folders, and move them. True when something moved.</summary>
    public static bool Move(Window? owner, IEnumerable<string> chosen)
    {
        var paths = chosen.Select(path => path.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var refused = paths.Select(path => (Path: path, Why: DriveMover.Refusal(path))).Where(item => item.Why is not null).ToList();
        var movable = paths.Except(refused.Select(item => item.Path)).ToList();
        var window = new DriveMoveWindow(movable.Count == 1 ? Path.GetFileName(movable[0]) : Ui.T("{0} items", Math.Max(movable.Count, paths.Count)));
        window.Ask(movable, refused);
        ShowOver(owner, window);
        return window.Changed;
    }

    /// <summary>Move back what History logged as moved to another drive. True when something came back.</summary>
    public static bool MoveBack(Window? owner, List<(string RunTime, int Index)> items)
    {
        var window = new DriveMoveWindow(Ui.T("Move back"));
        window.Back(items);
        ShowOver(owner, window);
        return window.Changed;
    }

    private void Ask(List<string> movable, List<(string Path, string? Why)> refused)
    {
        var notes = refused.Select(item => $"{Path.GetFileName(item.Path)}: {Ui.T(item.Why!)}").ToList();
        if (movable.Count == 0)
        {
            summary.Text = string.Join("\n", notes);
            buttons.Children.Add(Ui.Button(Ui.T("Close"), Close, accent: true));
            return;
        }
        summary.Text = Ui.T("Measuring…");
        var text = Ui.T("It is copied to the drive you choose, checked, and only then removed from here. A link is left in its place, "
                        + "so Windows, games and apps still find it where they expect it. You can move it back from History.");
        var linkFiles = true;
        if (movable.Any(File.Exists) && !DriveMover.CanLinkFiles())
        {
            linkFiles = false;
            text += "\n\n" + Ui.T("Windows only lets PC Butler leave a link for a single file when Developer Mode is on (Settings, System, For developers). "
                                  + "Files are moved without a link, so open them from their new place; folders always get one.");
        }
        if (notes.Count > 0)
            text += "\n\n" + Ui.T("Left where they are:") + "\n" + string.Join("\n", notes);
        body.Children.Insert(body.Children.IndexOf(drives), Ui.Muted(text, 12.5, wrap: true).Margin(0, 10, 0, 12));

        // Each drive is a choice; the one the files are already on is left out.
        var roots = movable.Select(path => Path.GetPathRoot(path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var choices = DriveMover.Drives().Where(drive => !(roots.Count == 1 && drive.Root.Equals(roots[0], StringComparison.OrdinalIgnoreCase))).ToList();
        var radios = new List<(RadioButton Button, DriveChoice Drive)>();
        foreach (var drive in choices)
        {
            var name = drive.Label.Length > 0 ? drive.Label : Ui.T("Local disk");
            var radio = new RadioButton
            {
                Content = $"{drive.Root.TrimEnd('\\')}  {name}   ·   " + Ui.T("{0} free of {1}", Ui.FormatSize(drive.Free), Ui.FormatSize(drive.Total)),
                GroupName = "drive", Foreground = Ui.Paint("Text"), Margin = new Thickness(0, 4, 0, 4), IsEnabled = false,
            };
            radios.Add((radio, drive));
            drives.Children.Add(radio);
        }
        if (choices.Count == 0)
            drives.Children.Add(Ui.Muted(Ui.T("There is no other drive to move it to."), 12.5, wrap: true));

        var go = Ui.Button(Ui.T("Move"), () => { }, accent: true);
        go.IsEnabled = false;
        var stop = Ui.Button(Ui.T("Cancel"), Close);
        stop.IsCancel = true;
        buttons.Children.Add(stop);
        buttons.Children.Add(go.Margin(8));

        // Measuring a big game can take a moment: the drives that have room light up when it is done.
        Task.Run(() => movable.Sum(path =>
        {
            try
            {
                return DriveMover.Measure(path).Size;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        })).ContinueWith(measured => Dispatcher.Invoke(() =>
        {
            total = measured.IsCompletedSuccessfully ? measured.Result : 0;
            summary.Text = Ui.T("{0} item(s), {1}", movable.Count, Ui.FormatSize(total));
            foreach (var (radio, drive) in radios)
            {
                radio.IsEnabled = drive.Free > total + (64L << 20);
                if (!radio.IsEnabled)
                    radio.Content += "   ·   " + Ui.T("not enough room");
            }
            if (radios.Where(item => item.Button.IsEnabled).OrderByDescending(item => item.Drive.Free).FirstOrDefault() is { Button: { } best })
            {
                best.IsChecked = true;
                go.IsEnabled = true;
            }
        }));

        go.Click += (_, _) =>
        {
            if (radios.FirstOrDefault(item => item.Button.IsChecked == true) is not { Drive: { } drive })
                return;
            go.Visibility = Visibility.Collapsed;
            foreach (var (radio, _) in radios)
                radio.IsEnabled = false;
            Run(stop, token => DriveMover.MoveAll(movable, drive.Root, linkFiles, done => Interlocked.Exchange(ref copied, done), null, token),
                results => Ui.T("Moved {0} item(s) to {1}. They still open from where they were.", results.Count(result => result.Done), drive.Root.TrimEnd('\\')));
        };
    }

    private void Back(List<(string RunTime, int Index)> items)
    {
        var moves = History.Load().SelectMany(run => run.Moves.Select((move, index) => (run.Time, index, move)))
                           .Where(item => items.Contains((item.Time, item.index)) && item.move is [_, _, DriveMover.Linked]).ToList();
        summary.Text = Ui.T("{0} item(s) go back to where they were, and the links are removed.", moves.Count);
        var stop = Ui.Button(Ui.T("Cancel"), Close);
        stop.IsCancel = true;
        buttons.Children.Add(stop);
        Task.Run(() => moves.Sum(item =>
        {
            try
            {
                return DriveMover.Measure(item.move[1]).Size;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        })).ContinueWith(measured => Dispatcher.Invoke(() =>
        {
            total = measured.IsCompletedSuccessfully ? measured.Result : 0;
            Run(stop, token =>
            {
                var results = new List<DriveMoveResult>();
                long before = 0;
                foreach (var (time, index, move) in moves)
                {
                    if (token.IsCancellationRequested)
                        break;
                    var start = before;
                    try
                    {
                        results.Add(DriveMover.MoveBack(time, index, done => Interlocked.Exchange(ref copied, start + done), token));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        results.Add(new DriveMoveResult(move[0], move[1], false, true, error.Message));
                    }
                    before = Interlocked.Read(ref copied);
                }
                return results;
            }, results => Ui.T("Moved {0} item(s) back.", results.Count(result => result.Done)));
        }));
    }

    /// <summary>Run the work off the window's thread, show its progress, then what came of it.</summary>
    private void Run(Button stop, Func<CancellationToken, List<DriveMoveResult>> work, Func<List<DriveMoveResult>, string> said)
    {
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        bar.Visibility = Visibility.Visible;
        progressText.Text = Ui.T("Starting…");
        stop.Content = Ui.T("Stop");
        tick.Start();
        Task.Run(() => work(token)).ContinueWith(finished => Dispatcher.Invoke(() =>
        {
            tick.Stop();
            cancel = null;
            var results = finished.IsCompletedSuccessfully ? finished.Result : [];
            Changed = results.Any(result => result.Done);
            bar.Value = 1;
            var lines = new List<string> { said(results) };
            lines.AddRange(results.Where(result => !result.Done).Select(result => $"{Path.GetFileName(result.Path)}: {result.Error}"));
            lines.AddRange(results.Where(result => result.Leftover.Length > 0)
                                  .Select(result => Ui.T("The old copy at {0} could not be fully deleted; delete it yourself to free the space.", result.Leftover)));
            if (finished.Exception?.GetBaseException() is { } error)
                lines.Add(error.Message);
            progressText.Text = string.Join("\n\n", lines);
            progressText.Foreground = Ui.Paint("Text");
            buttons.Children.Clear();
            buttons.Children.Add(Ui.Button(Ui.T("Close"), Close, accent: true));
            App.Current.Workspace?.History.Refresh();
        }));
    }
}
