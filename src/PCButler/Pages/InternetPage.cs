// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>Internet speed test: download, upload and response time.</summary>
public sealed class InternetPage : Page
{
    private readonly StatCard[] stats;
    private readonly Button start;
    private readonly TextBlock status = Ui.Text("", 18, weight: FontWeights.SemiBold);
    private readonly ListView results;
    private CancellationTokenSource? running;
    private readonly TextBox localAddress = AddressBox();
    private readonly TextBox publicAddress = AddressBox();

    public InternetPage(MainWindow main) : base(main, "Internet", "How fast is your connection, really?")
    {
        start = AddAction("Start test", Toggle, accent: true);
        stats = AddStats(("Download", ""), ("Upload", ""), ("Response time", ""));
        foreach (var card in stats)
            card.Set("—");

        // This PC's two addresses, each with a button that copies it.
        var addresses = new CardPanel("This PC's addresses", "");
        addresses.Controls.Children.Add(Ui.Button(Ui.T("Refresh"), ShowAddresses));
        var lines = new StackPanel { Margin = new Thickness(20, 12, 20, 14) };
        lines.Children.Add(AddressLine("On your network (local IP)", localAddress));
        lines.Children.Add(AddressLine("On the internet (public IP)", publicAddress).Margin(0, 8, 0, 0));
        lines.Children.Add(Ui.Muted(Ui.T("The public address is what websites see. It is read by asking Cloudflare which address the request came from."), 12, wrap: true)
                             .Margin(0, 8, 0, 0));
        addresses.SetBody(lines);
        addresses.Margin = new Thickness(0, 0, 0, 16);
        Children.Add(addresses.Dock(Dock.Top));
        ShowAddresses();

        var panel = new CardPanel("Speed test","");
        panel.Controls.Children.Add(Ui.Button(Ui.T("Clear history"), ClearHistory));
        var top = new StackPanel { Margin = new Thickness(20, 14, 20, 10) };
        status.Text = Ui.T("Press Start test.");
        top.Children.Add(status);
        top.Children.Add(Ui.Muted(Ui.T("The test takes about 15 seconds and measures against Cloudflare's speed-test servers. It downloads and "
                                       + "uploads random data for a few seconds, so on a fast line it can use a few hundred megabytes. "
                                       + "Pause other downloads for a true result."), 12, wrap: true).Margin(0, 6, 0, 0));
        results = Ui.List(("When", 180, "PlainCell"), ("Download", 140), ("Upload", 140), ("Response time", 140));
        var body = new DockPanel();
        body.Children.Add(top.Dock(Dock.Top));
        body.Children.Add(results);
        panel.SetBody(body);
        Children.Add(panel);
        ShowHistory(SpeedTest.History());
    }

    private static StackPanel AddressLine(string label, TextBox value)
    {
        var caption = Ui.Muted(Ui.T(label));
        caption.Width = 230;
        caption.VerticalAlignment = VerticalAlignment.Center;
        var copy = Ui.Button(Ui.T("Copy"), () =>
        {
            if (System.Net.IPAddress.TryParse(value.Text, out _))
                Admin.Copy(value.Text);
        });
        return Ui.Row(12, caption, value, copy);
    }

    private static TextBox AddressBox() => new()
    {
        IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), MinWidth = 260,
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 16, FontWeight = FontWeights.SemiBold,
        Foreground = Ui.Paint("Text"), FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center,
    };

    private async void ShowAddresses()
    {
        localAddress.Text = NetworkAddresses.Local() is { Length: > 0 } local ? local : Ui.T("Not connected");
        publicAddress.Text = Ui.T("Looking…");
        publicAddress.Text = await NetworkAddresses.Public() is { Length: > 0 } seen ? seen : Ui.T("Could not be read");
    }

    private static string Speed(double megabits) => megabits >= 100 ? $"{megabits:0}" : $"{megabits:0.0}";

    private void ShowHistory(List<SpeedResult> history)
    {
        results.ItemsSource = history.Select(result => new Row
        {
            Name = result.Time, Cells = [$"{Speed(result.Download)} Mbps", $"{Speed(result.Upload)} Mbps", $"{result.Ping} ms"],
        }).ToList();
    }

    private void ClearHistory()
    {
        var count = SpeedTest.History().Count;
        if (count == 0 || !Ui.Ask("Clear history", Ui.T("Clear all {0} speed-test results?", count)))
            return;
        SpeedTest.ClearHistory();
        ShowHistory([]);
    }

    private async void Toggle()
    {
        if (running is not null)
        {
            running.Cancel();
            return;
        }
        running = new CancellationTokenSource();
        start.Content = Ui.T("Stop");
        foreach (var card in stats)
            card.Set("—");
        try
        {
            var result = await Task.Run(() => SpeedTest.Run((stage, speed) => Dispatcher.BeginInvoke(() =>
            {
                status.Text = Ui.T(stage switch
                {
                    "download" => "Measuring download…", "upload" => "Measuring upload…", _ => "Measuring response time…",
                });
                if (stage != "ping" && speed > 0)
                    stats[stage == "download" ? 0 : 1].Set(Speed(speed), "Mbps");
            }), running.Token));
            stats[0].Set(Speed(result.Download), "Mbps", Ui.T("About {0} per second", Ui.FormatSize((long)(result.Download * 125000))));
            stats[1].Set(Speed(result.Upload), "Mbps", Ui.T("About {0} per second", Ui.FormatSize((long)(result.Upload * 125000))));
            stats[2].Set(result.Ping.ToString(), "ms", Ui.T("To the nearest Cloudflare server"));
            status.Text = Ui.T("Done.");
            ShowHistory(SpeedTest.Remember(result));
        }
        catch (OperationCanceledException)
        {
            status.Text = Ui.T("Test stopped.");
        }
        catch (HttpRequestException error)
        {
            status.Text = Ui.T("The test could not reach the internet.");
            Log.Warning($"Speed test failed: {error.Message}");
        }
        finally
        {
            running.Dispose();
            running = null;
            start.Content = Ui.T("Start test");
        }
    }

    public override bool CanClose()
    {
        running?.Cancel();
        return true;
    }
}
