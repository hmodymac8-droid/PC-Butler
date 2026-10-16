// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Security.Cryptography;
using Android.OS;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Benchmark: the phone measured for real. The processor hashes data for a few seconds, on
/// one core and then on all of them (MB a second); storage writes a 256 MB file in the app's
/// own space and reads it back. Nothing is sped up or changed: it only measures.
/// </summary>
public sealed class BenchmarkPage : AppPage
{
    private const int TestMb = 256;
    private bool storage;
    private CancellationTokenSource? running;
    private readonly Image bigIcon = new() { Source = "i_cpu_m.png", WidthRequest = 48, HeightRequest = 48, HorizontalOptions = LayoutOptions.Center };
    private readonly Label score = new() { FontSize = 44, FontAttributes = FontAttributes.Bold, TextColor = Theme.Primary, FontFamily = "monospace", HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label scoreText = new() { FontSize = 13, TextColor = Theme.Muted, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label second = new() { FontSize = 13.5, TextColor = Theme.Text, HorizontalTextAlignment = TextAlignment.Center };
    private readonly ProgressBar progress = Theme.Bar(0);
    private readonly Button start;
    private readonly VerticalStackLayout device = new() { Spacing = 0 };

    public BenchmarkPage() : base("Benchmark", tool: true)
    {
        SetSub(Theme.Faint(Tr.T("CPU & storage"), 13));
        Body.Children.Add(Theme.Chips([Tr.T("CPU"), Tr.T("Storage")], 0, index =>
        {
            running?.Cancel();
            storage = index == 1;
            ShowLast();
        }));
        progress.IsVisible = false;
        Body.Children.Add(Theme.CardOf(new VerticalStackLayout
        {
            Spacing = 6, Padding = new Thickness(0, 12),
            Children = { bigIcon, score.Margin(0, 10, 0, 0), scoreText, second, progress.Margin(0, 12, 0, 0) },
        }, 20).Margin(0, 12, 0, 12));
        start = Theme.Accent(Tr.T("Run test"), () => _ = Run(), "i_play_k.png");
        Body.Children.Add(start);
        Body.Children.Add(Theme.Section(Tr.T("Device")));
        Body.Children.Add(Theme.CardOf(device, 16));
        Body.Children.Add(Theme.Hint(Tr.T("Measured on this phone. It is hot or busy, or saving power: the numbers come out lower. Nothing is sped up or changed.")));
        ShowDevice();
        ShowLast();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        running?.Cancel();
    }

    private void ShowDevice()
    {
        var memory = new global::Android.App.ActivityManager.MemoryInfo();
        (global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.ActivityService) as global::Android.App.ActivityManager)?.GetMemoryInfo(memory);
        var chip = Build.VERSION.SdkInt >= BuildVersionCodes.S && Build.SocModel is { Length: > 0 } soc && soc != Build.Unknown ? $"{Build.SocManufacturer} {soc}" : Build.Hardware ?? "";
        var stat = new StatFs(global::Android.OS.Environment.DataDirectory!.AbsolutePath);
        (string, string)[] lines =
        [
            (Tr.T("Model"), $"{Build.Manufacturer} {Build.Model}"),
            (Tr.T("Processor"), chip),
            (Tr.T("Cores"), System.Environment.ProcessorCount.ToString()),
            (Tr.T("Memory"), Size(memory.TotalMem)),
            (Tr.T("Storage"), Size(stat.TotalBytes)),
            ("Android", Build.VERSION.Release ?? ""),
        ];
        foreach (var (name, value) in lines)
        {
            if (device.Children.Count > 0)
                device.Children.Add(Theme.Divider());
            var row = Theme.Line3(null, Theme.Faint(name, 13.5), new Label { Text = value, FontSize = 13.5, TextColor = Theme.Text, LineBreakMode = LineBreakMode.TailTruncation, MaximumWidthRequest = 220 });
            row.Padding = new Thickness(0, 10);
            device.Children.Add(row);
        }
    }

    private void ShowLast()
    {
        bigIcon.Source = storage ? "i_hard_drive_m.png" : "i_cpu_m.png";
        var (first, other) = storage ? (Preferences.Default.Get("bench.read", 0.0), Preferences.Default.Get("bench.write", 0.0))
                                     : (Preferences.Default.Get("bench.all", 0.0), Preferences.Default.Get("bench.one", 0.0));
        score.Text = first > 0 ? first.ToString("N0") : "—";
        scoreText.Text = Tr.T(storage ? "Read speed · MB/s" : "All cores · MB/s hashed");
        second.Text = other > 0 ? Tr.T(storage ? "Write {0} MB/s" : "One core {0} MB/s", other.ToString("N0")) : "";
        start.Text = Tr.T(first > 0 ? "Test again" : "Run test");
    }

    private async Task Run()
    {
        if (running is not null)
        {
            running.Cancel();
            return;
        }
        running = new CancellationTokenSource();
        var cancel = running.Token;
        (start.Text, progress.IsVisible, progress.Progress) = (Tr.T("Stop test"), true, 0);
        score.Text = "…";
        second.Text = "";
        var steps = new Progress<double>(part => progress.Progress = part);
        try
        {
            if (storage)
            {
                var (write, read) = await Task.Run(() => Disk(steps, cancel), cancel);
                Preferences.Default.Set("bench.write", write);
                Preferences.Default.Set("bench.read", read);
                ActivityLog.Record("benchmark", "Storage benchmark", $"Read {read:N0} / Write {write:N0} MB/s");
            }
            else
            {
                var (one, all) = await Task.Run(() => Cpu(steps, cancel), cancel);
                Preferences.Default.Set("bench.one", one);
                Preferences.Default.Set("bench.all", all);
                ActivityLog.Record("benchmark", "CPU benchmark", $"{all:N0} MB/s · {one:N0} MB/s one core");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException error)
        {
            await Say(error.Message);
        }
        finally
        {
            running = null;
            progress.IsVisible = false;
            ShowLast();
        }
    }

    /// <summary>Hashing for 3 seconds on one core, then 3 seconds on all: MB a second each time.</summary>
    private static (double One, double All) Cpu(IProgress<double> steps, CancellationToken cancel)
    {
        var data = new byte[1 << 20];
        Random.Shared.NextBytes(data);
        double Measure(int threads, double from)
        {
            long done = 0;
            var clock = Stopwatch.StartNew();
            var ticking = Task.Run(async () =>
            {
                while (clock.Elapsed.TotalSeconds < 3 && !cancel.IsCancellationRequested)
                {
                    steps.Report(from + clock.Elapsed.TotalSeconds / 6);
                    await Task.Delay(100);
                }
            });
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
            {
                using var hash = SHA256.Create();
                while (clock.Elapsed.TotalSeconds < 3 && !cancel.IsCancellationRequested)
                {
                    hash.ComputeHash(data);
                    Interlocked.Increment(ref done);
                }
            });
            ticking.Wait();
            cancel.ThrowIfCancellationRequested();
            return done / clock.Elapsed.TotalSeconds;
        }
        var one = Measure(1, 0);
        var all = Measure(System.Environment.ProcessorCount, 0.5);
        return (one, all);
    }

    /// <summary>256 MB written to the app's own space (to the disk, not just memory), then read back and removed.</summary>
    private static (double Write, double Read) Disk(IProgress<double> steps, CancellationToken cancel)
    {
        var file = System.IO.Path.Combine(FileSystem.CacheDirectory, "benchmark.tmp");
        var chunk = new byte[4 << 20];
        Random.Shared.NextBytes(chunk);
        var chunks = TestMb / 4;
        try
        {
            var clock = Stopwatch.StartNew();
            using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                for (var i = 0; i < chunks; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(chunk);
                    steps.Report(0.5 * (i + 1) / chunks);
                }
                output.Flush(true);
            }
            var write = TestMb / clock.Elapsed.TotalSeconds;
            clock.Restart();
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16))
            {
                var read = 0;
                while (input.Read(chunk) is var count and > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    read += count;
                    steps.Report(0.5 + 0.5 * read / (TestMb * 1048576.0));
                }
            }
            return (write, TestMb / clock.Elapsed.TotalSeconds);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
