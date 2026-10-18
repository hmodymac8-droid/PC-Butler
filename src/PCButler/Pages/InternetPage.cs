// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Internet: this PC's addresses, the speed test (download, upload and response time), and
/// the usage tracker (which apps use the internet right now, and how much went over it today, this week or this month).
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
    private readonly StackPanel history = new() { Visibility = Visibility.Collapsed };
    private readonly Button historyButton;
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

        // --- the tracker: which apps use the internet now; the totals of a period on request ---
        var tracker = new CardPanel("Internet usage", "");
        var trackerBody = new StackPanel { Margin = new Thickness(20, 6, 20, 14) };
        perAppButton = Ui.Button(Ui.T("Start"), TogglePerApp);
        trackerBody.Children.Add(Ui.SettingRow(Ui.Text(Ui.T("Which apps use it"), 13, weight: FontWeights.Bold), perAppNote, perAppButton, first: true));
        trackerBody.Children.Add(Caption("Using the internet now").Margin(0, 12, 0, 6));
        trackerBody.Children.Add(liveList);

        // At the bottom: the history, shown when asked for.
        historyButton = Ui.Button(Ui.T("Show history"), ToggleHistory);
        historyButton.HorizontalAlignment = HorizontalAlignment.Left;
        historyButton.Margin = new Thickness(0, 14, 0, 0);
        trackerBody.Children.Add(historyButton);
        foreach (var choice in new[] { "Today", "This week", "This month" })
            period.Items.Add(new ComboBoxItem { Content = Ui.T(choice), Tag = choice });
        period.SelectedIndex = 0;
        period.SelectionChanged += (_, _) => ShowUsage();
        history.Children.Add(Ui.Row(8, period, Ui.Button(Ui.T("Clear"), ClearUsage)).Margin(0, 12, 0, 0));
        var (cardRow, cards) = StatCard.MakeRow(("Downloaded", ""), ("Uploaded", ""), ("Total", ""));
        usageCards = cards;
        cardRow.Margin = new Thickness(0, 12, 0, 12);
        history.Children.Add(cardRow);
        history.Children.Add(Ui.SettingRow(Ui.T("Track internet usage"),
                                           Ui.T("Counts what this PC downloads and uploads each day, over every network adapter (Wi-Fi, cable, phone). "
                                                + "Only the amounts are counted, on this PC, never what was sent."),
                                           Ui.Switch("", main.Config.TrackInternet, on =>
                                           {
                                               Main.UpdateConfig(config => config.TrackInternet = on);
                                               App.Current.TrackInternet(on);
                                               ShowUsage();
                                           }), first: true));
        trackerBody.Children.Add(history);
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

    private (DateTime First, DateTime Last) Period()
    {
        var today = DateTime.Today;
        return ((period.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "This week" => (today.AddDays(-((7 + (int)today.DayOfWeek - (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek) % 7)), today),
            "This month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today, today),
        };
    }

    private void ToggleHistory()
    {
        var open = history.Visibility != Visibility.Visible;
        history.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        historyButton.Content = Ui.T(open ? "Hide history" : "Show history");
        if (open)
            ShowUsage();
    }

    /// <summary>Once a second: the apps using the internet; every ten seconds, the history's totals while it shows.</summary>
    private void Tick()
    {
        ShowNow();
        if (++ticks % 10 == 0 && history.Visibility == Visibility.Visible)
            ShowUsage();
    }

    /// <summary>Which apps send and receive right now: the helper's counts a second apart, handed to the steady list.</summary>
    private void ShowNow()
    {
        var counting = App.Current.CountingAppTraffic && AdminHelper.Running;
        var net = counting ? NetMeter.Latest() : null;
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
            liveList.Clear(Ui.T(counting ? "Waiting for the helper's first counts…" : "Press Start to see which apps are using the internet."));
        (lastNet, lastNetAt) = (net, at);
        perAppNote.Text = Ui.T(counting
            ? "On: each app's traffic is being counted. Press Stop to stop counting."
            : "Off. Press Start to count which apps use the internet (Windows asks for administrator rights). Press Stop at any time.");
        perAppButton.Content = Ui.T(counting ? "Stop" : "Start");
    }

    /// <summary>The row a process counts in: one per app, so an app made of several programs (SteelSeries GG) is one row.</summary>
    private string NameOf(int pid)
    {
        if (names.TryGetValue(pid, out var known))
            return known;
        // An app that has ended is not remembered under its number: all of them share one row.
        return AppNames.Of(pid) is { } name ? names[pid] = name : Ui.T("Ended apps");
    }

    /// <summary>The history: what went over the network in the chosen period.</summary>
    private void ShowUsage()
    {
        var (first, last) = Period();
        var used = NetUsageLog.Between(first, last);
        var (sent, received) = (used.Sum(day => day.Use.Sent), used.Sum(day => day.Use.Received));
        var note = Main.Config.TrackInternet ? Ui.T("Over every network adapter") : Ui.T("The tracker is off.");
        usageCards[0].Set(Ui.FormatSize(received), "", note);
        usageCards[1].Set(Ui.FormatSize(sent), "", note);
        usageCards[2].Set(Ui.FormatSize(sent + received), "", note);
    }

    /// <summary>Start or stop counting which apps use the internet; the choice is remembered.</summary>
    private async void TogglePerApp()
    {
        perAppButton.IsEnabled = false;
        var on = !(App.Current.CountingAppTraffic && AdminHelper.Running);
        if (!on || await Admin.Ensure("Internet", Ui.T("Windows only tells an administrator how much each app sends and receives over the internet, so counting it "
                                                       + "needs administrator rights. Only the amounts are counted, never what is sent.")))
        {
            Main.UpdateConfig(config => config.CountAppTraffic = on);
            await App.Current.CountAppTraffic(on);
        }
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
