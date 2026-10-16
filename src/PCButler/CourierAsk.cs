// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// A pop-up in the middle of the screen when another PC wants to connect (with the code both
/// PCs show) or wants to send files: answered right there with Allow or Refuse. Closing it
/// leaves the question on the Courier page and the widget, where it can still be answered.
/// It closes by itself once the question is settled elsewhere.
/// </summary>
public sealed class CourierAsk : Window
{
    private static readonly HashSet<string> asked = [];
    private static readonly List<CourierAsk> open = [];
    private readonly Func<bool> stillOpen;

    private CourierAsk(string title, string detail, string code, Action allow, Action refuse, Func<bool> stillOpen)
    {
        this.stillOpen = stillOpen;
        Ui.StyleWindow(this);
        Title = Ui.T("Courier");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 400 };
        body.Children.Add(Ui.Glyph("", 26, "Primary"));
        body.Children.Add(Ui.Text(title, 18, weight: FontWeights.SemiBold, wrap: true).Margin(0, 10, 0, 0));
        body.Children.Add(Ui.Muted(detail, 12.5, wrap: true).Margin(0, 6, 0, 0));
        if (code.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 34, FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Paint("Primary"), FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 12, 0, 0),
            });
        }
        var yes = Ui.Button(Ui.T("Allow"), () => { allow(); Close(); }, accent: true);
        var no = Ui.Button(Ui.T("Refuse"), () => { refuse(); Close(); });
        yes.MinWidth = no.MinWidth = 110;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no.Margin(8, 0, 0, 0));
        body.Children.Add(buttons);
        Content = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(); // the question stays on the Courier page
        };
        Closed += (_, _) => open.Remove(this);
    }

    /// <summary>Called every second: a pop-up for each new question, and the ones answered elsewhere go.</summary>
    public static void Watch(PortalNode? node)
    {
        foreach (var shown in open.Where(shown => !shown.stillOpen()).ToList())
            shown.Close();
        if (node is null)
            return;
        foreach (var pairing in node.Pairings.Where(pairing => pairing is { Incoming: true, Code.Length: > 0 }))
        {
            if (!asked.Add("pair " + pairing.PeerId + pairing.Code))
                continue;
            Pop(new CourierAsk(Ui.T("{0} wants to connect to this PC", pairing.PeerName),
                               Ui.T("Allow it only if you know that PC. The other PC shows this same code, if you want to check."), pairing.Code,
                               () => node.Confirm(pairing), () => node.Refuse(pairing), () => pairing.State == "waiting" && !node.Stopped));
        }
        foreach (var request in node.Requests)
        {
            if (!asked.Add("send " + request.Id))
                continue;
            Pop(new CourierAsk(Ui.T("{0} wants to send {1} file(s)", request.PeerName, request.Files), $"{request.Name}  ·  {Ui.FormatSize(request.Size)}", "",
                               () => node.Accept(request.Id), () => node.Refuse(request.Id), () => !node.Stopped && node.Requests.Any(item => item.Id == request.Id)));
        }
    }

    private static void Pop(CourierAsk ask)
    {
        open.Add(ask);
        ask.Show();
        ask.Activate();
    }
}
