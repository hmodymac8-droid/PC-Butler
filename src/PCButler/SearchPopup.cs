// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCButler.Core.Search;

namespace PCButler;

/// <summary>
/// The search box the shortcut (Win + F unless changed) opens over any program: type, then Enter
/// opens the first result (or the chosen one), Ctrl+Enter opens its folder, the arrows choose,
/// Esc closes. It closes by itself when another program comes to the front.
/// </summary>
public sealed class SearchPopup : Window
{
    private readonly TextBox box;
    private readonly SearchResults results = new(compact: true);
    private readonly TextBlock status = Ui.Muted("", 11.5);
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private CancellationTokenSource? running;
    private bool closing;

    public SearchPopup()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Search");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 780;
        Height = 540;

        box = Ui.Field(hint: Ui.T("Search files and folders…"));
        box.FontSize = 16 * Ui.TextScale;
        box.TextChanged += (_, _) =>
        {
            typing.Stop();
            typing.Start();
        };
        typing.Tick += (_, _) =>
        {
            typing.Stop();
            Run();
        };
        results.Opened += () => SearchHistory.Add(box.Text);
        results.Opened += CloseOnce;
        results.SortChanged += Run;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Ui.Glyph("", 17, "Primary").Margin(0, 0, 10, 0).Dock(Dock.Left));
        head.Children.Add(box);
        var everything = Ui.Text(Ui.T("All results on the Search page") + " →", 11.5, "Primary");
        everything.Cursor = Cursors.Hand;
        everything.MouseLeftButtonUp += (_, _) => OpenPage();
        var foot = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        foot.Children.Add(everything.Dock(Dock.Right));
        foot.Children.Add(Ui.Muted(Ui.T("Enter: open  ·  Ctrl+Enter: open folder  ·  ↑ ↓: choose  ·  Esc: close"), 11.5).Margin(0, 0, 12, 0));
        var layout = new DockPanel();
        layout.Children.Add(head.Dock(Dock.Top));
        layout.Children.Add(foot.Dock(Dock.Bottom));
        layout.Children.Add(status.Margin(2, 6, 2, 0).Dock(Dock.Bottom));
        layout.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = results });
        Content = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = layout,
        };

        PreviewKeyDown += OnKey;
        // Another program in front closes it; a question of PC Butler's own (rename, delete) does not.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && !OursInFront())
                CloseOnce();
        }, DispatcherPriority.ApplicationIdle);
        Loaded += (_, _) =>
        {
            Place();
            box.Focus();
        };
        Closed += (_, _) => running?.Cancel();
        Run();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                break;
            case Key.Enter when control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                OpenPage();
                break;
            case Key.Enter when box.IsKeyboardFocused:
                if (results.SelectedIndex < 0)
                    results.ChooseFirst();
                if (control)
                    results.OpenFolderChosen();
                else
                    results.OpenChosen();
                break;
            case Key.Down or Key.Up when box.IsKeyboardFocused:
                results.Step(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.PageDown or Key.PageUp when box.IsKeyboardFocused:
                results.Step(e.Key == Key.PageDown ? 12 : -12);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async void Run()
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        var (query, sort, descending) = (box.Text, results.Sort, results.Descending);
        var engine = App.Current.Search;
        try
        {
            var result = await Task.Run(() => engine.Search(query, sort, descending, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || closing)
                return;
            results.Show(result);
            results.ChooseFirst();
            status.Text = engine.State == SearchState.Scanning && engine.Index.Live == 0
                ? Ui.T("Indexing your drives: {0:N0} found so far…", engine.Scanned)
                : engine.State == SearchState.Off ? Ui.T("Search is switched off in Settings.")
                : Ui.T("{0:N0} results", result.Rows.Length);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenPage()
    {
        var text = box.Text;
        CloseOnce();
        App.Current.ShowPage("search");
        App.Current.Workspace?.SearchPage.SearchFor(text);
    }

    private void CloseOnce()
    {
        if (closing)
            return;
        closing = true;
        Close();
    }

    /// <summary>Across the screen the mouse is on, a fifth of the way down, like a launcher.</summary>
    private void Place()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
        var area = SystemParameters.WorkArea;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var (left, top, width, height) = (screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY, screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
        if (width <= 0 || height <= 0 || !new Rect(left, top, width, height).Contains(point))
            (left, top, width, height) = (area.Left, area.Top, area.Width, area.Height);
        Left = left + Math.Max(0, (width - ActualWidth) / 2);
        Top = top + Math.Max(0, Math.Min(height / 5, height - ActualHeight));
    }

    /// <summary>
    /// Bring the box in front of the program the user is in. A key caught by the keyboard hook
    /// does not give PC Butler the right to take the focus (a registered shortcut does), so it
    /// joins the input of the window in front for a moment, which Windows allows.
    /// </summary>
    public static void BringToFront(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var front = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(front, out _);
        var ours = GetCurrentThreadId();
        var joined = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            SetForegroundWindow(handle);
            BringWindowToTop(handle);
        }
        finally
        {
            if (joined)
                AttachThreadInput(ours, theirs, false);
        }
        window.Activate();
    }

    private static bool OursInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == (uint)Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
