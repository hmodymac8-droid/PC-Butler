// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The Courier widget: a small window that stays on top while PCs are connected. Drop files
/// on it to send them (it asks which PC when there are several), answer a pairing or a
/// request right there, and watch what is being sent and received, with a progress bar each.
/// </summary>
public sealed class CourierWidget : Window
{
    private readonly TextBlock subtitle = Ui.Muted("", 11.5);
    private readonly Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly StackPanel asks = new();
    private readonly Border dropArea;
    private readonly Rectangle dropEdge;
    private readonly TextBlock dropText = Ui.Muted("", 12.5, wrap: true);
    private readonly StackPanel transfersPanel = new();
    private readonly Border collapseButton;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<PortalTransfer, (ProgressBar Bar, TextBlock Detail, long Done, DateTime At, double Speed)> rows = [];
    private readonly Dictionary<PortalTransfer, Border> pauses = [];
    private string asksShown = "", transfersShown = "";
    private bool collapsed;

    /// <summary>The user closed it: it stays away until the next connection or question.</summary>
    public event Action? Dismissed;

    public CourierWidget()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Courier");
        Width = 330;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;

        // --- the top line: who is connected, and three small buttons ---
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Small("\uE8A7", Ui.T("Open Courier"), () =>
        {
            App.Current.ShowMainWindow();
            App.Current.Workspace?.ShowPage("portal");
        }));
        collapseButton = Small("\uE70D", Ui.T("Make it smaller"), () => SetCollapsed(!collapsed));
        buttons.Children.Add(collapseButton);
        buttons.Children.Add(Small("\uE711", Ui.T("Hide until the next connection"), () =>
        {
            Hide();
            Dismissed?.Invoke();
        }));
        head.Children.Add(buttons.Dock(Dock.Right));
        dot.Fill = Ui.Paint("Primary");
        head.Children.Add(dot);
        head.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Text(Ui.T("Courier"), 14, weight: FontWeights.SemiBold), subtitle },
        });
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };

        // --- where files are dropped ---
        dropEdge = new Rectangle
        {
            Stroke = Ui.Paint("ControlLine"), StrokeThickness = 1.2, StrokeDashArray = [4, 3], RadiusX = 10, RadiusY = 10,
        };
        dropText.TextAlignment = TextAlignment.Center;
        dropText.VerticalAlignment = VerticalAlignment.Center;
        var dropIcon = Ui.Glyph("\uE898", 20, "Primary");
        dropIcon.HorizontalAlignment = HorizontalAlignment.Center;
        var dropGrid = new Grid { MinHeight = 78 };
        dropGrid.Children.Add(dropEdge);
        dropGrid.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8, 12, 8), Children = { dropIcon, dropText.Margin(0, 6, 0, 0) } });
        dropArea = new Border { Child = dropGrid, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        dropArea.MouseLeftButtonUp += (_, _) => PickFiles();
        dropArea.ToolTip = Ui.T("Drop files or folders here, or click to choose them.");

        var layout = new StackPanel();
        layout.Children.Add(head);
        layout.Children.Add(asks);
        layout.Children.Add(dropArea);
        layout.Children.Add(transfersPanel);
        Content = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 12, 14), Margin = new Thickness(6), Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
        };

        DragEnter += (_, e) => Highlight(e, true);
        DragOver += (_, e) => Highlight(e, true);
        DragLeave += (_, e) => Highlight(e, false);
        Drop += (_, e) =>
        {
            Highlight(e, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
                Dispatcher.BeginInvoke(() => SendDropped(dropped)); // after the drag, so Explorer isn't kept waiting
        };
        Loaded += (_, _) =>
        {
            // Bottom corner, above where the drop zone sits.
            var area = SystemParameters.WorkArea;
            Left = Tr.IsArabic ? area.Left + 24 : area.Right - ActualWidth - 24;
            Top = area.Bottom - ActualHeight - 150;
        };
        refresh.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => refresh.IsEnabled = IsVisible;
        Refresh();
    }

    private static Border Small(string glyph, string tip, Action action)
    {
        var button = new Border
        {
            Child = Ui.Glyph(glyph, 11, "Muted"), Padding = new Thickness(7, 5, 7, 5), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0),
        };
        button.MouseEnter += (_, _) => button.Background = Ui.Paint("Secondary");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // not the start of a drag of the window
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private void SetCollapsed(bool on)
    {
        collapsed = on;
        dropArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ((TextBlock)collapseButton.Child).Text = on ? "\uE70E" : "\uE70D";
        collapseButton.ToolTip = Ui.T(on ? "Make it bigger" : "Make it smaller");
    }

    private void Highlight(DragEventArgs e, bool over)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (collapsed && over && files)
            SetCollapsed(false);
        dropEdge.Stroke = Ui.Paint(over && files ? "Primary" : "ControlLine");
        dropArea.Background = over && files ? Ui.Paint("PrimaryTint") : Brushes.Transparent;
    }

    // --- sending ---

    private void SendDropped(string[] dropped) => Choose(target =>
    {
        if (Courier.Node is { } node && Courier.Send(node, target, dropped) is null)
            Ui.Info("Courier", Ui.T("None of those files could be read."));
        Refresh();
    });

    private void PickFiles() => Choose(target =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Ui.T("Files to send to {0}", target.Name) };
        if (dialog.ShowDialog() == true && Courier.Node is { } node)
            Courier.Send(node, target, dialog.FileNames);
        Refresh();
    });

    /// <summary>The PC to send to: the only one connected at once, or a choice from a menu at the drop area.</summary>
    private void Choose(Action<PortalPeer> then)
    {
        var ready = Courier.Targets(Courier.Node).Where(target => target.Ready).ToList();
        if (ready.Count == 0)
        {
            Ui.Info("Courier", Ui.T("No PC is connected right now."));
            return;
        }
        if (ready.Count == 1)
        {
            then(ready[0].Tag);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = dropArea, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = Ui.T("Send to"), IsEnabled = false });
        foreach (var target in ready)
            menu.Items.Add(Ui.Item(target.Name, () => then(target.Tag)));
        menu.IsOpen = true;
    }

    // --- showing ---

    public void Refresh()
    {
        if (Courier.Node is not { } node)
            return;
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        subtitle.Text = ready.Count == 0 ? Ui.T("Connecting…") : ready.Count == 1 ? Ui.T("Connected to {0}", ready[0].Name) : Ui.T("{0} PCs connected", ready.Count);
        dot.Fill = Ui.Paint(ready.Count > 0 ? "Primary" : "Amber");
        dropText.Text = ready.Count == 0 ? Ui.T("No PC is connected yet.")
            : ready.Count == 1 ? Ui.T("Drop files here to send to {0}", ready[0].Name) : Ui.T("Drop files here, then choose the PC");
        ShowAsks(node);
        ShowTransfers(node);
    }

    /// <summary>A pairing to confirm, or files waiting for Accept: answered right here.</summary>
    private void ShowAsks(PortalNode node)
    {
        var pairings = node.Pairings.Where(pairing => pairing is { Incoming: true, Code.Length: > 0 }).ToList();
        var requests = node.Requests;
        var passwords = node.PasswordAsks;
        var key = string.Join("|", pairings.Select(pairing => pairing.PeerId + pairing.Code)) + "#" + string.Join("|", requests.Select(request => request.Id))
                  + "#" + string.Join("|", passwords.Select(item => item.Number + item.Ask));
        if (key == asksShown)
            return;
        asksShown = key;
        asks.Children.Clear();
        foreach (var pairing in pairings)
        {
            var code = new TextBlock
            {
                Text = pairing.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 24, FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 4),
            };
            asks.Children.Add(Ask(Ui.T("{0} wants to connect to this PC", pairing.PeerName),
                                  Ui.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."), code,
                                  Ui.Button(Ui.T("Allow"), () => { node.Confirm(pairing); Refresh(); }, accent: true),
                                  Ui.Button(Ui.T("Refuse"), () => { node.Refuse(pairing); Refresh(); })));
        }
        foreach (var (number, ask) in passwords)
        {
            // Typed on the Courier page, where it is asked for.
            asks.Children.Add(Ask(Ui.T("{0} asks for a password", node.PeerOf(number)?.Name ?? Portal.ShowNumber(number)),
                                  Ui.T(ask == PortalPasswordAsk.Wrong ? "That password was not right. Type it again." : "Type the password set on that PC for new PCs."), null,
                                  Ui.Button(Ui.T("Type the password…"), () => App.Current.ShowPage("portal"), accent: true)));
        }
        foreach (var request in requests)
        {
            asks.Children.Add(Ask(Ui.T("{0} wants to send {1} file(s)", request.PeerName, request.Files),
                                  $"{request.Name}  ·  {Ui.FormatSize(request.Size)}", null,
                                  Ui.Button(Ui.T("Accept"), () => { node.Accept(request.Id); Refresh(); }, accent: true),
                                  Ui.Button(Ui.T("Refuse"), () => { node.Refuse(request.Id); Refresh(); })));
        }
        if (asks.Children.Count > 0 && collapsed)
            SetCollapsed(false);
    }

    private static Border Ask(string title, string detail, UIElement? lead, params Button[] buttons)
    {
        var body = new StackPanel();
        body.Children.Add(Ui.Text(title, 13, weight: FontWeights.SemiBold, wrap: true));
        body.Children.Add(Ui.Muted(detail, 11.5, wrap: true).Margin(0, 2, 0, 0));
        if (lead is not null)
            body.Children.Add(lead);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button.Margin(row.Children.Count == 0 ? 0 : 6));
        body.Children.Add(row);
        return new Border
        {
            Child = body, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 10, 12, 12), CornerRadius = new CornerRadius(10),
            Background = Ui.Paint("Secondary"), BorderBrush = Ui.Paint("Primary"), BorderThickness = new Thickness(1),
        };
    }

    /// <summary>What is moving now, and what has just finished, each with its progress bar.</summary>
    private void ShowTransfers(PortalNode node)
    {
        var now = DateTime.Now;
        var shown = node.Transfers.Where(transfer => transfer.Running || (now - transfer.Started).TotalMinutes < 60 && transfer.State == "done" && rows.ContainsKey(transfer))
                                  .Reverse().Take(4).ToList();
        var key = string.Join("|", shown.Select(transfer => transfer.Id));
        if (key != transfersShown)
        {
            transfersShown = key;
            transfersPanel.Children.Clear();
            foreach (var gone in rows.Keys.Where(transfer => !shown.Contains(transfer)).ToList())
            {
                rows.Remove(gone);
                pauses.Remove(gone);
            }
            foreach (var transfer in shown)
            {
                var bar = new ProgressBar { Height = 5, Maximum = 100, Margin = new Thickness(0, 5, 0, 3) };
                var detail = Ui.Muted("", 11);
                var name = Ui.Text(transfer.Name, 12.5, weight: FontWeights.SemiBold);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var cancel = Small("\uE711", Ui.T("Cancel"), () => { transfer.Cancel(); Refresh(); });
                var pause = Small("", Ui.T("Pause"), () =>
                {
                    if (transfer.Paused)
                        transfer.Resume();
                    else
                        transfer.Pause();
                    Refresh();
                });
                pauses[transfer] = pause;
                var top = new DockPanel();
                top.Children.Add(cancel.Dock(Dock.Right));
                top.Children.Add(pause.Dock(Dock.Right));
                top.Children.Add(Ui.Glyph(transfer.Incoming ? "\uE896" : "\uE898", 12, transfer.Incoming ? "Primary" : "Text").Margin(0, 0, 8, 0).Dock(Dock.Left));
                top.Children.Add(name);
                transfersPanel.Children.Add(new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { top, bar, detail } });
                rows[transfer] = rows.TryGetValue(transfer, out var old) ? (bar, detail, old.Done, old.At, old.Speed) : (bar, detail, transfer.Done, DateTime.UtcNow, 0);
            }
        }
        var clock = DateTime.UtcNow;
        foreach (var transfer in shown)
        {
            var (bar, detail, done, at, speed) = rows[transfer];
            if ((clock - at).TotalSeconds >= 0.9)
                (done, at, speed) = (transfer.Done, clock, Math.Max(0, (transfer.Done - done) / (clock - at).TotalSeconds));
            rows[transfer] = (bar, detail, done, at, speed);
            var percent = transfer.Size > 0 ? transfer.Done * 100.0 / transfer.Size : 100;
            bar.Value = transfer.State == "done" ? 100 : percent;
            bar.IsIndeterminate = transfer.State == "waiting";
            var direction = Ui.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer);
            var held = transfer.Paused || transfer.PausedThere;
            var times = Courier.Times(transfer);
            detail.Text = transfer.State switch
            {
                "done" => $"{direction}  ·  " + Ui.T("Done") + $"  ·  {Ui.FormatSize(transfer.Size)}" + (times.Length > 0 ? $"  ·  {times}" : ""),
                "active" => $"{direction}  ·  {percent:0}%  ·  {Ui.FormatSize(transfer.Done)} / {Ui.FormatSize(transfer.Size)}"
                            + (held ? "  ·  " + Courier.State(transfer) : speed > 0 ? $"  ·  {Ui.FormatSize((long)speed)}/s" : "")
                            + (times.Length > 0 ? "\n" + times : ""),
                _ => $"{direction}  ·  " + Courier.State(transfer),
            };
            if (pauses.TryGetValue(transfer, out var button))
            {
                // Only what is moving can be held, and only this PC's own pause can be let go here.
                button.Visibility = transfer.State == "active" ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)button.Child).Text = transfer.Paused ? "" : "";
                button.ToolTip = Ui.T(transfer.Paused ? "Resume" : "Pause");
            }
        }
    }
}
