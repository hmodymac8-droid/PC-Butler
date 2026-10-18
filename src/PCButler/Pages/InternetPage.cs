// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Internet: this PC's addresses, the speed test (download, upload and response time), and
/// the usage tracker (how much went over the network each day, and which apps used it).
/// </summary>
public sealed class InternetPage : Page
{
    private readonly StatCard[] stats;
    private readonly Button start;
    private readonly TextBlock status = Ui.Text("", 18, weight: FontWeights.SemiBold);
    private readonly ListView results;
    private CancellationTokenSource? running;
    private readonly TextBox localAddress = AddressBox();
    private readonly TextBox publicAddress = AddressBox();

    // The tracker.
    private readonly ComboBox period = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly StatCard[] usageCards;
    private readonly LiveNetList liveList = new();
    private readonly ListView appList;
    private readonly ListView dayList;
    private readonly TextBlock perAppNote = Ui.Muted("", 12, wrap: true);
    private readonly Button perAppButton;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<int, string> names = [];
    private Dictionary<int, (long Sent, long Received)>? lastNet;
    private long lastNetAt;
    private int ticks;

    public InternetPage(MainWindow main) : base(main, "Internet", "How fast is your connection, and what is using it?")
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
        results.MinHeight = 160;
        results.MaxHeight = 280;
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(results);
        panel.SetBody(body);
        panel.Margin = new Thickness(0, 0, 0, 16);
        ShowHistory(SpeedTest.History());

        // --- the tracker: how much went over the network, and which apps used it ---
        var tracker = new CardPanel("Internet usage", "");
        foreach (var choice in new[] { "Today", "Yesterday", "Last 7 days", "Last 30 days", "This month", "Last 90 days" })
            period.Items.Add(new ComboBoxItem { Content = Ui.T(choice), Tag = choice });
        period.SelectedIndex = 0;
        period.SelectionChanged += (_, _) => ShowUsage();
        tracker.Controls.Children.Add(period);
        tracker.Controls.Children.Add(Ui.Button(Ui.T("Clear"), ClearUsage).Margin(6));
        var trackerBody = new StackPanel { Margin = new Thickness(20, 6, 20, 14) };
        trackerBody.Children.Add(Ui.SettingRow(Ui.T("Track internet usage"),
                                               Ui.T("Counts what this PC downloads and uploads each day, over every network adapter (Wi-Fi, cable, phone). "
                                                    + "Only the amounts are counted, on this PC, never what was sent."),
                                               Ui.Switch("", main.Config.TrackInternet, on =>
                                               {
                                                   Main.UpdateConfig(config => config.TrackInternet = on);
                                                   App.Current.TrackInternet(on);
                                                   ShowUsage();
                                               }), first: true));
        var (cardRow, cards) = StatCard.MakeRow(("Downloaded", ""), ("Uploaded", ""), ("Right now", ""));
        usageCards = cards;
        cardRow.Margin = new Thickness(0, 14, 0, 14);
        trackerBody.Children.Add(cardRow);

        perAppButton = Ui.Button(Ui.T("Start the helper"), StartPerApp);
        trackerBody.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Which apps use it"), 13, weight: FontWeights.Bold), perAppNote, perAppButton));
        trackerBody.Children.Add(Caption("Using the internet now").Margin(0, 12, 0, 6));
        trackerBody.Children.Add(liveList);
        appList = Ui.List(("App", 260, "PlainCell"), ("Share", 90), ("Downloaded", 140), ("Uploaded", 140), ("Total", 140));
        appList.MinHeight = 140;
        appList.MaxHeight = 340;
        trackerBody.Children.Add(Caption("By app, in this period").Margin(0, 14, 0, 6));
        trackerBody.Children.Add(appList);
        dayList = Ui.List(("Day", 260, "PlainCell"), ("Downloaded", 140), ("Uploaded", 140), ("Total", 140));
        dayList.MinHeight = 110;
        dayList.MaxHeight = 300;
        trackerBody.Children.Add(Caption("By day").Margin(0, 14, 0, 6));
        trackerBody.Children.Add(dayList);
        tracker.SetBody(trackerBody);
        tracker.MakeFoldable(open: false); // closed until its heading is clicked

        // More than a window shows at once: the page scrolls.
        var all = new StackPanel();
        all.Children.Add(addresses);
        all.Children.Add(panel);
        all.Children.Add(tracker);
        Children.Add(new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        ShowUsage();
        Tick();
        tick.Tick += (_, _) => Tick();
        Loaded += (_, _) => tick.Start();
        Unloaded += (_, _) => tick.Stop();
    }

    private static TextBlock Caption(string text) => Ui.Text(Ui.T(text), 13, weight: FontWeights.Bold);

    private (DateTime First, DateTime Last) Period() => ((period.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "Yesterday" => (DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1)),
        "Last 7 days" => (DateTime.Today.AddDays(-6), DateTime.Today),
        "Last 30 days" => (DateTime.Today.AddDays(-29), DateTime.Today),
        "This month" => (new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today),
        "Last 90 days" => (DateTime.Today.AddDays(-89), DateTime.Today),
        _ => (DateTime.Today, DateTime.Today),
    };

    /// <summary>Once a second: the speed right now and the apps using the internet; every ten seconds, the totals.</summary>
    private void Tick()
    {
        if (App.Current.NetTracker is { } tracker)
        {
            var (down, up) = tracker.Speed;
            usageCards[2].Set("↓ " + NetworkMeter.Megabits(down), "Mbps", Ui.T("↑ {0} Mbps up", NetworkMeter.Megabits(up)));
        }
        else
            usageCards[2].Set("—", "", Ui.T("The tracker is off."));
        ShowNow();
        if (++ticks % 10 == 0)
            ShowUsage();
    }

    /// <summary>Which apps send and receive right now: the helper's counts a second apart, handed to the steady list.</summary>
    private void ShowNow()
    {
        var net = NetMeter.Latest();
        var at = Environment.TickCount64;
        var rows = new Dictionary<string, (long Down, long Up)>(StringComparer.OrdinalIgnoreCase);
        if (net is not null && lastNet is not null && at - lastNetAt > 200)
        {
            var seconds = (at - lastNetAt) / 1000.0;
            foreach (var (pid, (sent, received)) in net)
            {
                // A process seen for the first time has no "before": its whole count so far is not one second's traffic.
                if (!lastNet.TryGetValue(pid, out var before))
                    continue;
                var (sentBefore, receivedBefore) = before;
                var (up, down) = (Math.Max(0, sent - sentBefore), Math.Max(0, received - receivedBefore));
                if (up + down == 0)
                    continue;
                var name = NameOf(pid);
                var (downSoFar, upSoFar) = rows.GetValueOrDefault(name);
                rows[name] = (downSoFar + (long)(down / seconds), upSoFar + (long)(up / seconds)); // bytes in one second
            }
            liveList.Add(rows);
        }
        else if (net is null)
            liveList.Clear(Ui.T(AdminHelper.Running ? "Waiting for the helper's first counts…" : "Start the helper to see which apps are using the internet."));
        (lastNet, lastNetAt) = (net, at);
        var helper = AdminHelper.Running;
        perAppNote.Text = Ui.T(helper
            ? "The helper is counting each app's traffic. It stops when PC Butler closes."
            : "Windows only tells an administrator which app sends what, so apps are counted while the helper runs. The totals need no helper.");
        perAppButton.Visibility = helper ? Visibility.Collapsed : Visibility.Visible;
    }

    private string NameOf(int pid)
    {
        if (names.TryGetValue(pid, out var known))
            return known;
        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            name = Ui.T("Ended app ({0})", pid);
        }
        return names[pid] = name;
    }

    /// <summary>The totals of the chosen period: the cards, each app, and each day.</summary>
    private void ShowUsage()
    {
        var (first, last) = Period();
        var used = NetUsageLog.Between(first, last);
        var (sent, received) = (used.Sum(day => day.Use.Sent), used.Sum(day => day.Use.Received));
        usageCards[0].Set(Ui.FormatSize(received), "", Main.Config.TrackInternet ? Ui.T("Over every network adapter") : Ui.T("The tracker is off."));
        usageCards[1].Set(Ui.FormatSize(sent), "", Ui.T("{0} in all", Ui.FormatSize(sent + received)));
        dayList.ItemsSource = used.Select(day => new Row
        {
            Name = day.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "   " + Ui.T(day.Day.DayOfWeek.ToString()),
            Cells = [Ui.FormatSize(day.Use.Received), Ui.FormatSize(day.Use.Sent), Ui.FormatSize(day.Use.Sent + day.Use.Received)],
        }).ToList();
        // Per app, from the record of what each app uses: only what the helper counted is known.
        var byApp = AppUsageLog.Report(first, last).Where(row => row.Use.NetSeconds > 0 && row.Use.Sent + row.Use.Received > 0)
            .OrderByDescending(row => row.Use.Sent + row.Use.Received).ToList();
        var allApps = Math.Max(1, byApp.Sum(row => row.Use.Sent + row.Use.Received));
        appList.ItemsSource = byApp.Select(row =>
        {
            var share = (double)(row.Use.Sent + row.Use.Received) / allApps;
            return new Row
            {
                Name = row.App,
                Cells = [share < 0.01 ? "<1%" : $"{share * 100:0}%", Ui.FormatSize(row.Use.Received), Ui.FormatSize(row.Use.Sent), Ui.FormatSize(row.Use.Sent + row.Use.Received)],
            };
        }).ToList();
    }

    private async void StartPerApp()
    {
        perAppButton.IsEnabled = false;
        if (await Admin.Ensure("Internet", Ui.T("Windows only tells an administrator how much each app sends and receives over the internet, so counting it "
                                                + "needs administrator rights. Only the amounts are counted, never what is sent.")))
            await Task.Run(() => AdminHelper.Send("net", ["on"], 10));
        perAppButton.IsEnabled = true;
        ShowNow();
    }

    private void ClearUsage()
    {
        if (NetUsageLog.FirstDay is null || !Ui.Ask("Clear", Ui.T("Forget the record of how much went over the internet each day?")))
            return;
        NetUsageLog.Clear();
        ShowUsage();
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
        tick.Stop();
        running?.Cancel();
        return true;
    }
}
