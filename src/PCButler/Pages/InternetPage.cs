// SPDX-License-Identifier: GPL-3.0-only
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
    private readonly List<Row> rows = [];
    private CancellationTokenSource? running;

    public InternetPage(MainWindow main) : base(main, "Internet", "How fast is your connection, really?")
    {
        start = AddAction("Start test", Toggle, accent: true);
        stats = AddStats(("Download", ""), ("Upload", ""), ("Response time", ""));
        foreach (var card in stats)
            card.Set("—");

        var panel = new CardPanel("Speed test", "");
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
    }

    private static string Speed(double megabits) => megabits >= 100 ? $"{megabits:0}" : $"{megabits:0.0}";

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
            rows.Insert(0, new Row
            {
                Name = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Cells = [$"{Speed(result.Download)} Mbps", $"{Speed(result.Upload)} Mbps", $"{result.Ping} ms"],
            });
            results.ItemsSource = rows.ToList();
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
