// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Text.Json;

namespace PCButler.Core;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Url, long Size, string Page);

/// <summary>Updates come from the project's GitHub releases.</summary>
public static class Updater
{
    public const int CheckHours = 24;

    /// <summary>"v1.10.2" -> [1, 10, 2], so versions compare as numbers, not text.</summary>
    public static int[] ParseVersion(string text) =>
        text.TrimStart('v', 'V').Split('.').Where(part => part.Length > 0 && part.All(char.IsDigit))
            .Select(int.Parse).ToArray();

    public static int CompareVersions(string a, string b)
    {
        int[] left = ParseVersion(a), right = ParseVersion(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            int x = i < left.Length ? left[i] : 0, y = i < right.Length ? right[i] : 0;
            if (x != y)
                return x.CompareTo(y);
        }
        return 0;
    }

    internal static HttpClient Client(int seconds)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCButler");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// The newest release if it is newer than this program, else null. Any network problem
    /// counts as "no update", so being offline never causes an error.
    /// </summary>
    public static UpdateInfo? CheckForUpdate(string? currentVersion = null)
    {
        try
        {
            using var client = Client(10);
            var body = client.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest")
                             .GetAwaiter().GetResult();
            using var release = JsonDocument.Parse(body);
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (CompareVersions(tag, currentVersion ?? AppInfo.Version) <= 0)
                return null;
            foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(tag.TrimStart('v', 'V'), asset.GetProperty("browser_download_url").GetString()!,
                                          asset.GetProperty("size").GetInt64(),
                                          release.RootElement.GetProperty("html_url").GetString()!);
            }
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException
                                          or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Download the release's installer to a temp folder and return its path.</summary>
    public static string Download(UpdateInfo update)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCButlerUpdate")).FullName;
        var target = Path.Combine(folder, $"PCButlerSetup-{update.Version}.exe");
        try
        {
            if (!File.Exists(target) || new FileInfo(target).Length != update.Size)
            {
                using var client = Client(600);
                using var response = client.GetStreamAsync(update.Url).GetAwaiter().GetResult();
                using var file = File.Create(target);
                response.CopyTo(file);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new IOException(error.Message, error);
        }
        if (new FileInfo(target).Length != update.Size)
        {
            File.Delete(target);
            throw new IOException("the download was incomplete");
        }
        return target;
    }

    /// <summary>
    /// Run the downloaded setup without its wizard (Inno Setup's silent mode). It closes this
    /// program, replaces it and starts it again, so the caller should exit right after. See
    /// installer.iss for what "/reopen=1" does.
    /// </summary>
    public static void Apply(string installer, bool reopen = false)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = false };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" })
            start.ArgumentList.Add(argument);
        if (reopen)
            start.ArgumentList.Add("/reopen=1");
        Process.Start(start);
    }
}

/// <summary>
/// The part that keeps working while the window is closed: automatic sorting on a
/// schedule, the clean-up of old files and the daily look for a new version. The
/// program's tray icon listens to its events.
/// </summary>
public sealed class BackgroundService
{
    private readonly AutoResetEvent wake = new(false); // set to interrupt the wait between checks
    private volatile bool sortNow;
    private volatile bool stopping;
    private Thread? thread;
    private string? shownState;
    private DateTime nextUpdateCheck = DateTime.Now.AddMinutes(1); // first look a minute after starting

    /// <summary>The newer release, once one has been found.</summary>
    public UpdateInfo? Update { get; private set; }

    /// <summary>A message for a tray notification (already in English; the listener translates).</summary>
    public event Action<string, object[]>? Notify;
    /// <summary>An automatic run moved files and the settings ask for the report window.</summary>
    public event Action? ReportWanted;
    /// <summary>"automatic sorting is on", "automatic sorting is off" or "paused until {0}".</summary>
    public event Action<string, object[]>? StateChanged;
    /// <summary>A newer version was found (the tray menu gains its Install item).</summary>
    public event Action? UpdateFound;
    /// <summary>History or settings changed behind the window's back.</summary>
    public event Action? DataChanged;
    /// <summary>Asked before updating by itself: is a window of the program open?</summary>
    public Func<bool> WindowIsOpen { get; set; } = () => false;
    /// <summary>Carries out an update: download, then hand over to the installer.</summary>
    public Action<UpdateInfo>? InstallUpdate { get; set; }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "PC Butler background" };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        wake.Set();
    }

    /// <summary>The tray's "Sort now": works even when automatic sorting is off.</summary>
    public void RequestSort()
    {
        sortNow = true;
        wake.Set();
    }

    public void TogglePause()
    {
        var config = ConfigStore.Load();
        config.PausedUntil = config.IsPaused ? "" : ConfigStore.PauseUntilTomorrow();
        ConfigStore.Save(config);
        wake.Set();
        DataChanged?.Invoke();
    }

    /// <summary>Look for a new version right now (used by the tests and the About page).</summary>
    public void CheckUpdatesNow()
    {
        nextUpdateCheck = DateTime.MinValue;
        wake.Set();
    }

    private void Loop()
    {
        var lastRun = DateTime.MinValue;
        while (!stopping)
        {
            var config = ConfigStore.Load(); // reread every tick so changes made in the window apply at once
            try
            {
                CheckUpdates(config);
                // With automatic sorting off the program keeps running, for the tray icon and updates.
                var schedule = config.Schedule;
                var now = DateTime.Now;
                if (schedule.Sort != "interval" && schedule.LastSort.Length == 0
                    || schedule.Clean != "off" && schedule.LastClean.Length == 0)
                {
                    // A schedule just switched on starts counting from now: its first run is the next scheduled time.
                    SaveSchedule(s =>
                    {
                        if (s.LastSort.Length == 0) s.LastSort = now.ToString(ScheduleSettings.Stamp);
                        if (s.LastClean.Length == 0) s.LastClean = now.ToString(ScheduleSettings.Stamp);
                    });
                }
                var due = config.AutoSortOn && !config.IsPaused
                          && (schedule.Sort == "interval"
                              ? (now - lastRun).TotalMinutes >= config.ScanIntervalMinutes
                              : schedule.IsDue(schedule.Sort, schedule.LastSort, now));
                if (sortNow || due)
                {
                    sortNow = false;
                    lastRun = now;
                    if (due && schedule.Sort != "interval")
                        SaveSchedule(s => s.LastSort = now.ToString(ScheduleSettings.Stamp));
                    SortPass(config);
                }
                if (!config.IsPaused && schedule.IsDue(schedule.Clean, schedule.LastClean, now))
                {
                    SaveSchedule(s => s.LastClean = now.ToString(ScheduleSettings.Stamp));
                    CleanPass(config);
                }
            }
            catch (Exception error) // whatever goes wrong, the loop (and the tray icon) keeps going
            {
                Log.Error($"Scan failed: {error.Message}");
            }
            ShowState(config);
            wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Change the schedule's bookkeeping on top of whatever is on disk now.</summary>
    private static void SaveSchedule(Action<ScheduleSettings> change)
    {
        var config = ConfigStore.Load();
        change(config.Schedule);
        ConfigStore.Save(config);
    }

    /// <summary>
    /// Scheduled cleaning: the items ticked on the Cleaner page go to the Recycle Bin (never
    /// deleted for good while nobody is watching) and are listed in History.
    /// </summary>
    private void CleanPass(Config config)
    {
        var keys = config.Cleaner.Categories
                   ?? Cleaner.Categories.Where(category => category.Checked).Select(category => category.Key).ToList();
        var known = keys.Where(key => Cleaner.Categories.Any(category => category.Key == key)).ToList();
        if (known.Count == 0)
            return;
        var found = Cleaner.Scan(known, CancellationToken.None);
        var files = found.SelectMany(result => result.Files).ToList();
        if (files.Count == 0)
            return;
        var (count, _) = Sorter.DeleteFiles(files);
        Cleaner.RemoveEmptyFolders(found.SelectMany(result => result.Folders));
        Log.Info($"Scheduled cleaning: {count} file(s)");
        if (count > 0)
        {
            Notify?.Invoke("Scheduled cleaning sent {0} file(s) to the Recycle Bin. They are listed in History.", [count]);
            DataChanged?.Invoke();
        }
    }

    private void SortPass(Config config)
    {
        var sorted = Sorter.MoveFiles(Sorter.PlanMoves(config), review: true);
        var count = sorted.Count;
        var cleaned = Sorter.CleanupExpired(config);
        if (count > 0)
        {
            Log.Info($"Sorted {count} file(s), {sorted.Deleted} of them deleted as their category asks");
            if (config.AfterRun == "window" && count > sorted.Deleted)
                ReportWanted?.Invoke();
            else if (sorted.Deleted > 0)
                Notify?.Invoke("Moved {0} file(s) and sent {1} to the Recycle Bin. Open History to review or undo.", [count - sorted.Deleted, sorted.Deleted]);
            else
                Notify?.Invoke("Moved {0} file(s). Open History to review or undo.", [count]);
        }
        if (cleaned > 0)
            Notify?.Invoke("Sent {0} old file(s) to the Recycle Bin. You can bring them back from History.", [cleaned]);
        if (count > 0 || cleaned > 0)
            DataChanged?.Invoke();
    }

    private void CheckUpdates(Config config)
    {
        if (File.Exists(AppInfo.UpdateMarker))
        {
            // We have just been updated.
            try
            {
                Notify?.Invoke("Updated to version {0}.", [File.ReadAllText(AppInfo.UpdateMarker).Trim()]);
                File.Delete(AppInfo.UpdateMarker);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (DateTime.Now < nextUpdateCheck)
            return;
        nextUpdateCheck = DateTime.Now.AddHours(Updater.CheckHours);
        var update = Updater.CheckForUpdate();
        if (update is null)
            return;
        Log.Info($"Version {update.Version} is available");
        var firstTime = Update is null || Update.Version != update.Version;
        Update = update;
        UpdateFound?.Invoke(); // reveals the "Install version ..." item
        // Updating closes every window of the program, so the automatic kind waits until none is open.
        if (config.AutoUpdate && AppInfo.IsInstalled)
        {
            if (!WindowIsOpen())
            {
                InstallUpdate?.Invoke(update);
                return;
            }
            nextUpdateCheck = DateTime.Now.AddHours(1); // try again in an hour
        }
        if (firstTime)
            Notify?.Invoke("Version {0} is available. Right-click this icon and choose Install, or open {1}.",
                           [update.Version, AppInfo.Name]);
    }

    private void ShowState(Config config)
    {
        string state;
        object[] arguments = [];
        if (!config.AutoSortOn)
            state = "automatic sorting is off";
        else if (config.IsPaused)
            (state, arguments) = ("paused until {0}", [config.PausedUntil]);
        else
            state = "automatic sorting is on";
        var shown = string.Format(state, arguments);
        if (shown != shownState)
        {
            shownState = shown;
            StateChanged?.Invoke(state, arguments);
        }
    }
}
