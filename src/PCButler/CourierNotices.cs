// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// A notification in the corner of the screen for each file being sent or received: its
/// progress bar, how long it has been moving and how long is left, and Pause and Cancel.
/// It shows while the Courier widget is not on screen (the widget shows the same), stays
/// until the transfer ends, then says how it ended for a few seconds. The cross closes it
/// for that transfer only.
/// </summary>
public sealed class CourierNotices : Window
{
    private const int MaxShown = 3;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    private sealed class Card
    {
        public required Border Root { get; init; }
        public required TextBlock Title { get; init; }
        public required ProgressBar Bar { get; init; }
        public required TextBlock Detail { get; init; }
        public required TextBlock Times { get; init; }
        public required Button Pause { get; init; }
        public required Button Cancel { get; init; }
        public DateTime? EndedAt { get; set; }
    }

    private readonly StackPanel stack = new();
    private readonly Dictionary<PortalTransfer, Card> cards = [];
    private readonly HashSet<string> closed = [];

    public CourierNotices()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Courier");
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Content = stack;
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Bottom corner, just above the taskbar, like the notifications of Windows.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = Tr.IsArabic ? area.Left + 12 : area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    /// <summary>Called every second: brings the cards in line with the transfers, and shows or hides the window.</summary>
    public void Update(PortalNode? node, bool allowed)
    {
        var now = DateTime.UtcNow;
        var transfers = node?.Transfers.ToList() ?? [];
        var wanted = allowed
            ? transfers.Where(transfer => !closed.Contains(transfer.Id)
                                          && (transfer.Running || cards.TryGetValue(transfer, out var card) && (card.EndedAt is null || now - card.EndedAt < Linger)))
                       .Reverse().Take(MaxShown).ToList()
            : [];
        foreach (var gone in cards.Keys.Where(transfer => !wanted.Contains(transfer)).ToList())
        {
            stack.Children.Remove(cards[gone].Root);
            cards.Remove(gone);
        }
        foreach (var transfer in wanted)
        {
            if (!cards.TryGetValue(transfer, out var card))
            {
                card = Make(transfer);
                cards[transfer] = card;
                stack.Children.Insert(0, card.Root);
            }
            Show(transfer, card, now);
        }
        if (cards.Count == 0)
        {
            Hide();
            return;
        }
        if (!IsVisible)
            Show();
        Place();
    }

    private Card Make(PortalTransfer transfer)
    {
        var title = Ui.Text("", 13, weight: FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var close = new Border
        {
            Child = Ui.Glyph("", 10, "Muted"), Padding = new Thickness(7, 5, 7, 5), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Ui.T("Close"), VerticalAlignment = VerticalAlignment.Top,
        };
        close.MouseEnter += (_, _) => close.Background = Ui.Paint("Secondary");
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            closed.Add(transfer.Id);
            Update(Courier.Node, true);
        };
        var head = new DockPanel();
        head.Children.Add(close.Dock(Dock.Right));
        head.Children.Add(Ui.Glyph(transfer.Incoming ? "" : "", 14, "Primary").Margin(0, 0, 10, 0).Dock(Dock.Left));
        head.Children.Add(title);

        var bar = new ProgressBar { Height = 6, Maximum = 100, Margin = new Thickness(0, 10, 0, 6) };
        var detail = Ui.Muted("", 11.5);
        var times = Ui.Muted("", 11.5);
        var pause = Ui.Button(Ui.T("Pause"), () =>
        {
            if (transfer.Paused)
                transfer.Resume();
            else
                transfer.Pause();
            Update(Courier.Node, true);
        });
        var cancel = Ui.Button(Ui.T("Cancel"), () =>
        {
            transfer.Cancel();
            Update(Courier.Node, true);
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(pause);
        buttons.Children.Add(cancel.Margin(6, 0, 0, 0));

        var body = new StackPanel { Children = { head, bar, detail, times, buttons } };
        var root = new Border
        {
            Background = Ui.Paint("Card"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 10, 14), Margin = new Thickness(6, 6, 6, 0), Child = body, Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
        };
        // A click on the card itself (not a button) opens the Courier page, where everything about it is.
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) // a button or the cross took the click
                return;
            App.Current.ShowPage("portal");
        };
        return new Card { Root = root, Title = title, Bar = bar, Detail = detail, Times = times, Pause = pause, Cancel = cancel };
    }

    private static void Show(PortalTransfer transfer, Card card, DateTime now)
    {
        if (!transfer.Running)
            card.EndedAt ??= now;
        card.Title.Text = Ui.T(transfer.Incoming ? "Receiving {0}" : "Sending {0}", transfer.Name);
        var percent = transfer.Size > 0 ? Math.Min(100, transfer.Done * 100.0 / transfer.Size) : 100;
        card.Bar.IsIndeterminate = transfer.State == "waiting";
        card.Bar.Value = transfer.State == "done" ? 100 : percent;
        var direction = Ui.T(transfer.Incoming ? "From {0}" : "To {0}", transfer.Peer);
        card.Detail.Text = transfer.State == "active"
            ? $"{direction}  ·  {percent:0}%  ·  {Ui.FormatSize(transfer.Done)} / {Ui.FormatSize(transfer.Size)}"
              + (transfer.Paused || transfer.PausedThere ? "  ·  " + Courier.State(transfer) : transfer.Speed > 0 ? $"  ·  {Ui.FormatSize((long)transfer.Speed)}/s" : "")
            : $"{direction}  ·  " + Courier.State(transfer) + (transfer.Note.Length > 0 && transfer.State != "done" ? $" ({transfer.Note})" : "");
        var times = transfer.State is "active" or "done" ? Courier.Times(transfer) : "";
        card.Times.Text = times;
        card.Times.Visibility = times.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Only what is moving can be held, and only this PC's own pause can be let go here.
        card.Pause.Visibility = transfer.State == "active" ? Visibility.Visible : Visibility.Collapsed;
        card.Pause.Content = Ui.T(transfer.Paused ? "Resume" : "Pause");
        card.Cancel.Visibility = transfer.Running ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)card.Pause.Parent).Visibility = transfer.Running ? Visibility.Visible : Visibility.Collapsed;
    }
}
