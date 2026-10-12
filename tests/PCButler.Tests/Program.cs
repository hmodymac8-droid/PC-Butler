// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
//
// Engine tests. Everything happens in a temporary folder (PCBUTLER_DATA points there),
// except that a few tiny test files pass through the real Recycle Bin and come back.
//
//   dotnet run --project tests/PCButler.Tests [path to a vault made by Downloads Sorter] [its password]
using System.Text;
using PCButler.Core;

var temp = Directory.CreateTempSubdirectory("pcbutler-test-").FullName;
Environment.SetEnvironmentVariable("PCBUTLER_DATA", Path.Combine(temp, "data"));
var failures = 0;

void Check(bool condition, string what)
{
    if (condition)
        return;
    failures++;
    Console.WriteLine("FAIL: " + what);
}

void Section(string name) => Console.WriteLine("-- " + name);

string Make(string folder, string name, string content = "x", int ageDays = 0)
{
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, name);
    File.WriteAllText(path, content);
    if (ageDays > 0)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
    }
    return path;
}

// ---------------------------------------------------------------- settings
Section("settings");
Check(ConfigStore.Load().MinAgeDays == 7 && ConfigStore.Load().Categories.Count == 6, "defaults");
Directory.CreateDirectory(AppInfo.DataDir);
File.WriteAllText(AppInfo.ConfigFile, """{"downloads_folder": "C:\\Old", "min_age_days": 3, "duplicates": {"mode": "size"}}""");
var legacy = ConfigStore.Load();
Check(legacy.Folders.SequenceEqual(["C:\\Old"]) && legacy.MinAgeDays == 3, "old single-folder setting migrates");
Check(legacy.Duplicates.Mode == "size" && legacy.Duplicates.Recursive && legacy.OtherFolder == "Other", "missing keys take defaults");
var downloads = Path.Combine(temp, "Downloads");
var second = Path.Combine(temp, "Second");
var config = new Config { Folders = [downloads, second], Ignore = ["*.torrent"], AutoSort = false };
config.Categories[1].MinAgeDays = 5; // Pictures wait 5 days
ConfigStore.Save(config);
var reloaded = ConfigStore.Load();
Check(reloaded.Folders.Count == 2 && reloaded.Categories[1].MinAgeDays == 5 && reloaded.Categories[0].MinAgeDays is null
      && reloaded.Categories[2].RecycleAfterDays == 90 && !reloaded.AutoSort, "settings round trip");
Check(File.ReadAllText(AppInfo.ConfigFile).Contains("\"min_age_days\": 7") && File.ReadAllText(AppInfo.ConfigFile).Contains("\"scan_interval_minutes\""),
      "file keeps the old key names");
Check(Glob.IsMatch("movie.torrent", "*.torrent") && !Glob.IsMatch("movie.txt", "*.torrent") && Glob.IsMatch("taxes2024.pdf", "taxes*")
      && Glob.IsMatch("a1.txt", "a?.txt") && Glob.IsMatch("b.txt", "[abc].txt") && !Glob.IsMatch("d.txt", "[abc].txt") && !Glob.IsMatch("a.txt", "[z-a].txt"), "name patterns");
Check(new Config { PausedUntil = ConfigStore.PauseUntilTomorrow() }.IsPaused && !new Config().IsPaused, "pause");
Check(new Config { AutoSort = true }.AutoSortOn && !new Config { AutoSort = true, ScanIntervalMinutes = 0 }.AutoSortOn, "interval 0 keeps sorting off");
Check(new Config { Language = "ar" }.EffectiveLanguage() == "ar" && new Config { Language = "en" }.EffectiveLanguage() == "en", "language choice");

// ---------------------------------------------------------------- planning and moving
Section("sorting");
config.MinAgeDays = 7;
foreach (var name in new[] { "a.pdf", "b.jpg", "setup.exe", "z.zip", "m.mp4", "s.mp3", "weird.xyz", "keep.torrent", "link.lnk", "part.crdownload", "a (1).pdf" })
    Make(downloads, name, name.StartsWith('a') ? "A" : name, ageDays: 10);
Make(downloads, "new.pdf");
Make(second, "n.txt", ageDays: 10);
Directory.CreateDirectory(Path.Combine(downloads, "A folder"));
List<string> Names(IEnumerable<PlannedMove> moves) => moves.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
var plan = Sorter.PlanMoves(config);
var expected = new[] { "a (1).pdf", "a.pdf", "b.jpg", "m.mp4", "n.txt", "s.mp3", "setup.exe", "weird.xyz", "z.zip" };
Check(Names(plan).SequenceEqual(expected.OrderBy(n => n, StringComparer.Ordinal)), "plan: " + string.Join(", ", Names(plan)));
string Destination(string name) => Path.GetFileName(plan.First(m => m.Name == name).Folder);
Check(Destination("weird.xyz") == "Other" && Destination("z.zip") == "Archives" && Destination("m.mp4") == "Videos"
      && Destination("s.mp3") == "Audio" && Destination("setup.exe") == "Installers Archive", "destinations");
Check(plan.First(m => m.Name == "n.txt").Folder == Path.Combine(second, "Documents"), "category folders are per sorted folder");
Check(Sorter.PlanMoves(config, everything: true).Any(m => m.Name == "new.pdf"), "everything ignores age");
config.Categories[1].MinAgeDays = 30;
Check(!Sorter.PlanMoves(config).Any(m => m.Name == "b.jpg"), "a category's own age wins");
config.Categories[1].MinAgeDays = null;
config.OtherFolder = "";
Check(!Sorter.PlanMoves(config).Any(m => m.Name == "weird.xyz"), "no catch-all folder leaves other types");
config.OtherFolder = "Other";
Check(Sorter.PlanMoves(config, onlyFolders: [second]).Select(m => m.Name).SequenceEqual(["n.txt"]), "one folder only");
Make(Path.Combine(downloads, "Documents"), "a.pdf", "existing");
var (moved, moveErrors) = Sorter.MoveFiles(plan, review: true);
Check(moved == 9 && moveErrors.Count == 0, $"moved {moved}, errors {moveErrors.Count}");
Check(File.Exists(Path.Combine(downloads, "Documents", "a (1).pdf")) && File.Exists(Path.Combine(downloads, "Documents", "a (2).pdf"))
      && File.ReadAllText(Path.Combine(downloads, "Documents", "a.pdf")) == "existing", "nothing is overwritten");
Check(File.Exists(Path.Combine(downloads, "keep.torrent")) && File.Exists(Path.Combine(downloads, "link.lnk"))
      && File.Exists(Path.Combine(downloads, "part.crdownload")) && File.Exists(Path.Combine(downloads, "new.pdf")), "skipped files stay");

// ---------------------------------------------------------------- history, undo, delete, restore
Section("history");
var pending = History.PendingMoves();
Check(pending.Count == 9, "automatic runs wait for review");
var runTime = pending[0].RunTime;
int IndexOf(string name) => History.Load()[0].Moves.FindIndex(m => Path.GetFileName(m[0]) == name);
var zip = IndexOf("z.zip");
Check(Sorter.UndoMoves([(runTime, zip)]) is (1, { Count: 0 }) && File.Exists(Path.Combine(downloads, "z.zip")), "undo one file");
Check(History.Load()[0].Moves[zip][2] == "undone" && Sorter.UndoMoves([(runTime, zip)]).Count == 0, "undone is recorded and not repeated");
Check(!Sorter.PlanMoves(config).Any(m => m.Name == "z.zip") && Sorter.PlanMoves(config, everything: true).Any(m => m.Name == "z.zip"),
      "a file put back is not moved again automatically");
Check(History.PendingMoves().Count == 8, "pending shrinks");
History.ApprovePending();
Check(History.PendingMoves().Count == 0 && History.LastActiveRun()?.Time == runTime, "approve");

var video = Path.Combine(downloads, "Videos", "m.mp4");
var deleted = Sorter.DeleteFiles([video]);
var videoMove = History.Load()[0].Moves[IndexOf("m.mp4")];
Check(deleted.Count == 1 && !File.Exists(video) && videoMove[2] == "deleted" && History.CanUndo(videoMove)
      && History.CurrentPath(videoMove) is null, "delete goes to the Recycle Bin and is logged");
var broughtBack = Sorter.UndoMoves([(runTime, IndexOf("m.mp4"))]);
Check(broughtBack.Count == 1 && File.Exists(video) && File.ReadAllText(video) == "m.mp4"
      && History.IsActive(History.Load()[0].Moves[IndexOf("m.mp4")]), "restore from the Recycle Bin: " + string.Join("; ", broughtBack.Errors));

var loose = Make(downloads, "pcbutler_test_loose.dat");
Check(Sorter.DeleteFiles([loose]).Count == 1 && History.Load()[^1].Moves[0] is [_, "", "deleted"], "deleting an unsorted file adds a log entry");
var lastRun = History.Load()[^1];
Check(Sorter.UndoMoves([(lastRun.Time, 0)]).Count == 1 && File.Exists(loose) && History.Load()[^1].Moves[0][2] == "restored", "and it can be restored");
Check(Sorter.DeleteFiles([loose], permanent: true).Count == 1 && !File.Exists(loose) && History.Load()[^1].Moves[0][2] == "erased"
      && !History.CanUndo(History.Load()[^1].Moves[0]), "delete for good is logged but can't be undone");

// auto clean-up: nothing yet; backdate the run 91 days and the installer goes, restorably
Check(Sorter.CleanupExpired(config) == 0, "nothing is cleaned up early");
var history = History.Load();
history[0].Time = DateTime.Now.AddDays(-91).ToString(History.TimeFormat);
History.Save(history);
runTime = history[0].Time;
var installer = Path.Combine(downloads, "Installers Archive", "setup.exe");
Check(Sorter.CleanupExpired(config) == 1 && !File.Exists(installer) && File.Exists(Path.Combine(downloads, "Documents", "a (1).pdf")),
      "old installers are cleaned up, other categories are not");
Check(Sorter.UndoMoves([(runTime, IndexOf("setup.exe"))]).Count == 1 && File.Exists(installer), "and can be brought back");
config.Categories[2].RecycleAfterDays = 0;
var undoAll = Sorter.UndoLast();
Check(undoAll.Errors.Count == 0 && File.Exists(Path.Combine(downloads, "b.jpg")) && !Directory.Exists(Path.Combine(downloads, "Pictures"))
      && History.LastActiveRun() is null, $"undo the whole run, removing emptied folders ({string.Join("; ", undoAll.Errors)})");

// ---------------------------------------------------------------- duplicates
Section("duplicates");
string Key(string name) => Duplicates.SimilarNameKey(name);
Check(Key("MC TYSON Make It Rain feat eyden Watson Official Music Video720p.mp4")
      == Key("MC TYSON \" Make It Rain \" feat. eyden & Watson (Official Music Video).mp4"), "similar names: " + Key("MC TYSON Make It Rain feat eyden Watson Official Music Video720p.mp4"));
Check(Key("Lil $o$o - Track.mp3") == Key("Lil oo Track.mp3") && Key("Artist - Title - Artist.mp3") == Key("Artist - Title.mp3")
      && Key("Song [1080p] HD.mkv") == Key("Song.mkv") && Key("a.mp3") != Key("b.mp3"), "similar-name rules");
var dupes = Path.Combine(temp, "Dupes");
Make(dupes, "report.pdf", "SAME"); Make(dupes, "report (1).pdf", "SAME"); Make(Path.Combine(dupes, "sub"), "copy of report.pdf", "SAME");
Make(dupes, "other.txt", "DIFF"); Make(dupes, "empty1.txt", ""); Make(dupes, "empty2.txt", "");
Make(Path.Combine(dupes, "sub"), "other.txt", "another");
var all = Duplicates.Collect([dupes, dupes], recursive: true, CancellationToken.None);
Check(all.Count == 7, $"collect finds each file once ({all.Count})");
Check(Duplicates.Collect([dupes], recursive: false, CancellationToken.None).Count == 5, "top level only");
var byContent = Duplicates.Find(all, "content", CancellationToken.None);
Check(byContent.Count == 1 && byContent[0].Files.Select(f => f.Name).SequenceEqual(["report.pdf", "report (1).pdf", "copy of report.pdf"]),
      "content: one group, shortest name first, empty files ignored");
var byName = Duplicates.Find(all, "name", CancellationToken.None);
Check(byName.Count == 1 && byName[0].Files.All(f => f.Name == "other.txt"), "exact name");
Check(Duplicates.Find(all, "name_size", CancellationToken.None).Count == 0, "name + size");
// "SAME" x3 and "DIFF" are all 4 bytes long.
Check(Duplicates.Find(all, "size", CancellationToken.None).Any(g => g.Files.Count == 4), "size only");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var stopped = false;
    try { Duplicates.Find(all, "content", cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "a scan can be cancelled");
}
config.Folders = [downloads];
Check(Duplicates.SortedFolders(config).Count == 8 && Duplicates.SortedFolders(config)[1] == Path.Combine(downloads, "Documents"), "sorted folders");

// ---------------------------------------------------------------- cleaner
Section("cleaner");
var junk = Path.Combine(temp, "Junk");
Make(junk, "old.tmp", "12345", ageDays: 3); Make(junk, "fresh.tmp", "1"); Make(Path.Combine(junk, "deep", "er"), "old2.log", "123", ageDays: 5);
Make(Path.Combine(junk, "PCButler"), "ours.txt", "1", ageDays: 5);
var originalTemp = Environment.GetEnvironmentVariable("TEMP");
Environment.SetEnvironmentVariable("TEMP", junk);
var scan = Cleaner.Scan(["user_temp"], CancellationToken.None);
Environment.SetEnvironmentVariable("TEMP", originalTemp);
Check(scan.Count == 1 && scan[0].Files.Count == 2 && scan[0].Size == 8 && scan[0].Folders.SequenceEqual([junk]),
      $"only files untouched for a day, never the program's own data ({scan[0].Files.Count} files, {scan[0].Size} bytes)");
Check(Cleaner.Categories.Count == 8 && !Cleaner.ByKey("update_cache").Checked && Cleaner.ByKey("user_temp").Checked, "categories");
foreach (var file in scan[0].Files) File.Delete(file);
Cleaner.RemoveEmptyFolders([junk]);
Check(Directory.Exists(junk) && !Directory.Exists(Path.Combine(junk, "deep")) && Directory.Exists(Path.Combine(junk, "PCButler")), "emptied folders go, the root stays");

// ---------------------------------------------------------------- booster (read-only checks)
Section("booster");
var (total, available) = Booster.MemoryStatus();
Check(total > 0 && available > 0 && available <= total, "memory status");
var programs = Booster.RunningPrograms();
Check(programs.Count > 5 && programs[0].Memory >= programs[^1].Memory && programs.Any(p => p.Pids.Contains(Environment.ProcessId) && p.Protected)
      && programs.Any(p => p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && p.Protected), "running programs");
Check(Booster.EndProgram(programs.First(p => p.Protected)) is { Length: > 0 }, "protected programs are refused");
Check(Booster.PowerPlans().Count(p => p.Active) == 1, "power plans");
Check(Booster.StartupItems() is { } startup && startup.All(i => i.Name.Length > 0 && i.Where.Length > 0), "startup apps can be listed");

// ---------------------------------------------------------------- boost (on two programs started here)
Section("boost");
System.Diagnostics.Process Idle(string tool, string arguments) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
    tool, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
using (var game = Idle("ping", "-n 60 127.0.0.1"))
using (var other = Idle("waitfor", "/t 60 pcbutlertest"))
{
    var mine = new[] { game.Id, other.Id };
    var boost = new BoostSettings { PowerPlan = false, TrimMemory = true };
    var state = BoostMode.Start(boost, game.Id, only: p => mine.Contains(p.Id));
    game.Refresh(); other.Refresh();
    Check(game.PriorityClass == System.Diagnostics.ProcessPriorityClass.High
          && other.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal, "the chosen program is raised, the other lowered");
    Check(state.Processes.Count == 2 && state.Lowered == 1 && state.Trimmed == 1
          && state.Favoured.Equals("ping.exe", StringComparison.OrdinalIgnoreCase),
          $"what was changed is recorded ({state.Processes.Count} priorities, {state.Trimmed} trimmed)");
    Check(File.Exists(BoostMode.StatePath) && BoostMode.Active is not null, "the changes are saved for undoing");
    // As after a crash: the program forgot, the file remembers.
    typeof(BoostMode).GetProperty("Active")!.SetValue(null, null);
    Check(BoostMode.Recover(), "a Boost left on is noticed at startup");
    game.Refresh(); other.Refresh();
    Check(game.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal
          && other.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal && !File.Exists(BoostMode.StatePath), "everything is put back");
    Check(BoostMode.Stop() is null && !BoostMode.Recover(), "stopping twice does nothing");

    // The automatic list. Only the power plan and priorities of ping.exe's siblings could be touched; both are off here.
    var auto = new BoostSettings { PowerPlan = false, Priorities = false, AutoApps = ["ping.exe"] };
    Check("ping.exe".Equals(BoostMode.AutoCheck(auto), StringComparison.OrdinalIgnoreCase) && BoostMode.Active is { Auto: true }, "a listed program switches Boost on");
    Check(BoostMode.AutoCheck(auto) is null, "and it stays on while it runs");
    game.Kill(); other.Kill(); game.WaitForExit(); other.WaitForExit();
    Check(BoostMode.AutoCheck(auto) == "" && BoostMode.Active is null, "and off when it closes");
    Check(BoostMode.AutoCheck(new BoostSettings()) is null, "an empty list does nothing");
}
Check(BoostMode.WindowedPrograms().All(p => p.Pid != Environment.ProcessId && !Booster.ProtectedNames.Contains(p.Name)), "programs to choose from");

// ---------------------------------------------------------------- the administrator helper's requests (no rights needed here)
Section("helper");
var helperDir = Path.Combine(temp, "helper");
Directory.CreateDirectory(helperDir);
var paused = new List<string>();
var asked = new List<string>();
using (var helperStop = new CancellationTokenSource())
{
    // Stands in for the helper: answers requests, "changing" services by writing them down.
    var helper = Task.Run(() =>
    {
        while (!helperStop.IsCancellationRequested)
        {
            AdminHelper.Poll(helperDir, paused, (service, stop) => { asked.Add((stop ? "stop " : "start ") + service); return true; });
            Thread.Sleep(50);
        }
    });
    var done = AdminHelper.Send("stop_services", ["spooler", "SysMain", "WinDefend", "..\\evil"], 10, helperDir);
    Check(done is not null && done.SequenceEqual(["Spooler", "SysMain"]) && paused.Count == 2, "only the listed services can be paused");
    Check(AdminHelper.Send("start_services", ["DiagTrack", "Spooler"], 10, helperDir) is { } resumed && resumed.SequenceEqual(["Spooler"])
          && paused.SequenceEqual(["SysMain"]), "only what was paused is resumed");
    Check(AdminHelper.Send("delete_everything", ["Spooler"], 1, helperDir) is null, "other requests are ignored");
    helperStop.Cancel();
    helper.Wait();
}
Check(asked.SequenceEqual(["stop Spooler", "stop SysMain", "start Spooler"]), "the helper did exactly that");
Check(AdminHelper.PausableServices.Length == 3 && AdminHelper.IsRunning("RpcSs") && !AdminHelper.IsRunning("no-such-service"), "service state can be read");

// ---------------------------------------------------------------- installed programs
Section("programs");
var installed = Programs.Installed();
Check(installed.Count > 3 && installed.All(p => p.Name.Length > 0 && (p.UninstallCommand.Length > 0 || p.Msi)), $"installed programs are listed ({installed.Count})");
Check(Programs.CleanName("Example App 2.1.0 (64-bit)") == "Example App" && Programs.CleanName("Tool v3.2 x64") == "Tool"
      && Programs.CleanName("7-Zip 24.08 (x64)") == "7-Zip" && Programs.CleanName("Microsoft") == "" && Programs.CleanName("App 2") == "",
      "names are cleaned for matching, and general ones refused");
var oldHome = Path.Combine(temp, "Apps", "Gone App");
Directory.CreateDirectory(Path.Combine(oldHome, "data"));
File.WriteAllText(Path.Combine(oldHome, "data", "left.txt"), "12345");
using (var leftKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PCButlerTestGone"))
    leftKey.SetValue("x", 1);
var gone = new InstalledProgram { Name = "PCButlerTestGone 1.2 (64-bit)", Hive = "HKCU", Key = "PCButlerTestGone", Location = oldHome };
var left = Programs.Leftovers(gone, []);
Check(left.Count == 2 && left.Any(l => l.Kind == "folder" && l.Path == oldHome && l.Size == 5)
      && left.Any(l => l.Kind == "registry" && l.Path == @"HKCU\Software\PCButlerTestGone"), $"leftovers are found ({left.Count})");
var neighbour = new InstalledProgram { Name = "Other", Hive = "HKCU", Key = "Other", Location = Path.Combine(oldHome, "data") };
Check(Programs.Leftovers(gone, [neighbour]).All(l => l.Kind != "folder"), "a folder another program lives in is never offered");
Check(Programs.Leftovers(new InstalledProgram { Name = "Windows", Hive = "HKCU", Key = "w", Location = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }, []).Count == 0,
      "shared folders and general names are never offered");
Check(Programs.RemoveLeftovers(left.Where(l => l.Kind == "registry")).Count == 0
      && Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTestGone") is null
      && Directory.GetFiles(Programs.RegistryBackupFolder, "PCButlerTestGone-*.reg").Length == 1, "a registry key is backed up, then removed");
var wingetTable = "   - \r   \\ \rName                 Id                  Version   Available Source\n"
    + "------------------------------------------------------------------------\n"
    + "Mozilla Firefox (x64) Mozilla.Firefox     130.0     131.0.2   winget\n"
    + "7-Zip                7zip.7zip           23.01     24.08     winget\n"
    + "A very long name th… Some.Truncat…       1.0       2.0       winget\n"
    + "2 upgrades available.\n\n"
    + "The following packages have an upgrade available, but require explicit targeting for upgrade:\n"
    + "Name    Id              Version Available Source\n"
    + "-------------------------------------------------\n"
    + "Discord Discord.Discord 1.0.90  1.0.91    winget\n";
var found = Programs.ParseUpdates(wingetTable);
Check(found.Count == 3 && found[0] == new ProgramUpdate("Mozilla Firefox (x64)", "Mozilla.Firefox", "130.0", "131.0.2")
      && found[1].Id == "7zip.7zip" && found[2].Name == "Discord", $"winget's table is read ({found.Count})");
var oneSource = Programs.ParseUpdates(string.Join('\n', "Name      Id                    Version Available",
    "---------------------------------------------", "AviSynth+ AviSynth.AviSynthPlus 3.7.3   3.7.3.4003", "1 upgrades available.", ""));
Check(oneSource.Count == 1 && oneSource[0] == new ProgramUpdate("AviSynth+", "AviSynth.AviSynthPlus", "3.7.3", "3.7.3.4003"), "also without the source column");

var onD = new InstalledProgram { Name = "Somewhere", Hive = "HKCU", Key = "s", UninstallCommand = "\"D:\\Apps\\Some\\unins000.exe\" /S" };
Check(onD.Drive == "D:" && new InstalledProgram { Name = "Msi", Hive = "HKLM", Key = "{1}", UninstallCommand = "MsiExec.exe /X{1}" }.Drive == ""
      && installed.Any(p => p.Drive.Length == 2), "the drive a program is on");
var appHome = Path.Combine(temp, "Apps", "Runner Tool");
Directory.CreateDirectory(appHome);
foreach (var exe in new[] { "unins000.exe", "RunnerTool.exe", "crashhelper.exe" }) File.WriteAllText(Path.Combine(appHome, exe), "");
var runner = new InstalledProgram { Name = "Runner Tool 3.1", Hive = "HKCU", Key = "r", Location = appHome };
Check(Programs.FindLauncher(runner) == Path.Combine(appHome, "RunnerTool.exe")
      && Programs.FindLauncher(new InstalledProgram { Name = "Runner Tool 3.1", Hive = "HKCU", Key = "r", Icon = Path.Combine(appHome, "RunnerTool.exe") + ",0" }) == Path.Combine(appHome, "RunnerTool.exe")
      && Programs.FindLauncher(new InstalledProgram { Name = "Nothing Here 1.0", Hive = "HKCU", Key = "n", Icon = Path.Combine(appHome, "unins000.exe") }) is null,
      "what opens a program is found, never its uninstaller");

// ---------------------------------------------------------------- speed-test history, graphics sensors
Section("speed history, sensors");
SpeedTest.ClearHistory();
SpeedTest.Remember(new SpeedResult(100.5, 20, 12));
var speeds = SpeedTest.Remember(new SpeedResult(250, 40, 9));
Check(speeds.Count == 2 && SpeedTest.History() is [{ Download: 250, Ping: 9, Time.Length: 16 }, { Upload: 20 }], "results are kept, newest first");
SpeedTest.ClearHistory();
Check(SpeedTest.History().Count == 0, "and can be cleared");
var (gpuDegrees, gpuFan) = GpuSensors.Read();
Check(gpuDegrees is null or (> 0 and < 150) && gpuFan is null or (> 0 and < 20000), $"graphics-card sensors read without error ({gpuDegrees?.ToString() ?? "none"} °C)");

// ---------------------------------------------------------------- what's new
Section("changelog");
var changes = Changelog.Entries(arabic: false);
var changesAr = Changelog.Entries(arabic: true);
Check(changes.Count > 5 && changes[0].Version == AppInfo.Version && changes.All(c => c.Changes.Count > 0), $"this version ({AppInfo.Version}) is on top of CHANGELOG.md");
Check(changesAr.Select(c => (c.Version, c.Changes.Count)).SequenceEqual(changes.Select(c => (c.Version, c.Changes.Count))), "CHANGELOG.ar.md lists the same versions and changes");
Check(Changelog.ShouldShow("1.0.0", firstStart: false) && Changelog.ShouldShow("", false) && !Changelog.ShouldShow(AppInfo.Version, false)
      && !Changelog.ShouldShow("", firstStart: true), "shown once after an update, not on a first start");

// ---------------------------------------------------------------- File Explorer's right-click entry
Section("explorer menu");
ExplorerMenu.Remove();
Check(ExplorerMenu.InstalledCommand() is null, "no entry to begin with");
Check(ExplorerMenu.Sync(true, @"C:\Apps\PC Butler\PCButler.exe", "Process with PC Butler") is null
      && ExplorerMenu.InstalledCommand() == "\"C:\\Apps\\PC Butler\\PCButler.exe\" --process \"%1\"", "the entry runs the program with the clicked item");
using (var folderEntry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\PCButler"))
    Check(folderEntry?.GetValue("") as string == "Process with PC Butler" && folderEntry.OpenSubKey("command") is not null, "folders get it too");
Check(ExplorerMenu.Sync(false, "x", "x") is null && ExplorerMenu.InstalledCommand() is null, "switching it off removes it");
var clicked = Path.Combine(temp, "Clicked");
Directory.CreateDirectory(clicked);
foreach (var name in new[] { "report.pdf", "tune.mp3", "strange.xyz123" }) File.WriteAllText(Path.Combine(clicked, name), "x");
var rules = new Config { OtherFolder = "" };
var filePlan = ExplorerMenu.Plan(Path.Combine(clicked, "report.pdf"), rules);
Check(!filePlan.IsFolder && filePlan.Destination == Path.Combine(clicked, "Documents"), "a file is planned into its category beside it");
Check(ExplorerMenu.Plan(Path.Combine(clicked, "strange.xyz123"), rules).Destination == "", "a file no category takes is left alone");
var folderPlan = ExplorerMenu.Plan(clicked, rules);
Check(folderPlan.IsFolder && folderPlan.Moves.Count == 2, $"a folder's loose files are planned ({folderPlan.Moves.Count})");
Check(Sorter.MoveFiles(filePlan.Moves).Count == 1 && File.Exists(Path.Combine(clicked, "Documents", "report.pdf")), "and sorting it moves it there");

// ---------------------------------------------------------------- scrypt and the vault
Section("vault");
// RFC 7914, section 12, second test vector.
var derived = Scrypt.DeriveKey(Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("NaCl"), 1024, 8, 16, 64);
Check(Convert.ToHexString(derived).StartsWith("FDBABE1C9D3472007856E7190D01E9FE7C6AD7CBC8237830E77376634B373162", StringComparison.Ordinal), "scrypt matches the RFC test vector");
Check(!Vault.Exists, "no vault yet");
var tooShort = false;
try { Vault.Create("123"); } catch (VaultException) { tooShort = true; }
Check(tooShort && !Vault.Exists, "short passwords are refused");
var vault = Vault.Create("correct horse");
var secret = Make(Path.Combine(temp, "Secrets"), "diary.txt", "my secret");
var big = Path.Combine(temp, "Secrets", "big.bin");
var bigData = new byte[2 * 1024 * 1024 + 123];
new Random(1).NextBytes(bigData);
File.WriteAllBytes(big, bigData);
var emptyFile = Make(Path.Combine(temp, "Secrets"), "empty.txt", "");
var addedToVault = vault.Add([secret, big, emptyFile, Path.Combine(temp, "missing.txt")]);
Check(addedToVault.Count == 3 && addedToVault.Errors.Count == 1 && !File.Exists(secret) && !File.Exists(big), $"add: {addedToVault.Count} in, {string.Join("; ", addedToVault.Errors)}");
Check(!Directory.GetFiles(Vault.VaultDir).Any(f => File.ReadAllText(f).Contains("my secret") || f.Contains("diary")), "nothing readable in the vault folder");
var wrong = false;
try { Vault.Unlock("wrong horse"); } catch (WrongPasswordException) { wrong = true; }
Check(wrong, "wrong password is refused");
vault = Vault.Unlock("correct horse");
Check(vault.Items.Count == 3 && vault.Items[0].Name == "diary.txt" && vault.Items[1].Size == bigData.Length, "unlock lists the files");
var copy = vault.OpenCopy(vault.Items[0]);
Check(File.ReadAllText(copy) == "my secret", "open decrypts a copy");
File.WriteAllText(copy, "my edited secret");
File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(5));
Check(vault.CloseAll().Count == 0 && !File.Exists(copy) && vault.Items[0].Size == "my edited secret".Length, "locking saves edits and removes the copy");
// The recovery key: sets a new password without the old one.
Check(!Vault.HasRecoveryKey, "no recovery key yet");
var keyFile = Path.Combine(temp, "first.vaultkey");
var askedAgain = false;
try { vault.CreateRecoveryKey(keyFile, "not the password"); } catch (WrongPasswordException) { askedAgain = true; }
Check(askedAgain && !File.Exists(keyFile), "making a recovery key needs the password");
vault.CreateRecoveryKey(keyFile, "correct horse");
Check(Vault.HasRecoveryKey && File.ReadAllText(keyFile).Contains("key: "), "recovery key file written");
var recovered = Vault.ResetPassword(keyFile, "forgot it once");
Check(recovered.Items.Count == 3 && Vault.Unlock("forgot it once").Items.Count == 3, "recovery key sets a new password");
vault = Vault.Unlock("forgot it once");
vault.ChangePassword("forgot it once", "correct horse");
Check(Vault.HasRecoveryKey && Vault.ResetPassword(keyFile, "forgot it twice").Items.Count == 3, "the key survives a password change");
vault = Vault.Unlock("forgot it twice");
var secondKey = Path.Combine(temp, "second.vaultkey");
vault.CreateRecoveryKey(secondKey, "forgot it twice");
var replaced = false;
try { Vault.ResetPassword(keyFile, "should not work"); } catch (VaultException) { replaced = true; }
Check(replaced && Vault.Unlock("forgot it twice").Items.Count == 3, "a newer key retires the old one");
var forged = Path.Combine(temp, "forged.vaultkey");
File.WriteAllText(forged, File.ReadAllText(secondKey).Replace("key: ", "key: AAAA"));
var forgedRefused = false;
try { Vault.ResetPassword(forged, "should not work"); } catch (VaultException) { forgedRefused = true; }
Check(forgedRefused, "a damaged key file is refused");
vault = Vault.ResetPassword(secondKey, "correct horse");
vault.ChangePassword("correct horse", "new password");
vault = Vault.Unlock("new password");
var outFolder = Path.Combine(temp, "Out");
var movedOut = vault.MoveOut(vault.Items.ToList(), outFolder);
Check(movedOut.Count == 3 && File.ReadAllText(Path.Combine(outFolder, "diary.txt")) == "my edited secret"
      && File.ReadAllBytes(Path.Combine(outFolder, "big.bin")).AsSpan().SequenceEqual(bigData)
      && new FileInfo(Path.Combine(outFolder, "empty.txt")).Length == 0 && vault.Items.Count == 0, "move out restores the exact bytes");
// tampering is detected
vault.Add([Make(temp, "tamper.txt", "important")]);
var blob = Directory.GetFiles(Vault.VaultDir, "*.bin").First(f => Path.GetFileName(f) != "index.bin");
var bytes = File.ReadAllBytes(blob);
bytes[^1] ^= 1;
File.WriteAllBytes(blob, bytes);
var refused = vault.MoveOut(vault.Items.ToList(), outFolder);
Check(refused.Count == 0 && refused.Errors.Count == 1 && !File.Exists(Path.Combine(outFolder, "tamper.txt")), "a changed vault file is refused");
Check(vault.Delete(vault.Items.ToList()).Count == 1 && vault.Items.Count == 0, "delete from the vault");

// A vault made by Downloads Sorter (the Python program) must open unchanged.
if (args.Length >= 2)
{
    Environment.SetEnvironmentVariable("PCBUTLER_DATA", args[0]);
    var old = Vault.Unlock(args[1]);
    var target = Path.Combine(temp, "FromPython");
    var result = old.MoveOut(old.Items.ToList(), target);
    Check(result.Count == 2 && File.ReadAllText(Path.Combine(target, "hello.txt")) == "hello from python"
          && new FileInfo(Path.Combine(target, "big.bin")).Length == 3_000_000, "a Downloads Sorter vault opens: " + string.Join("; ", result.Errors));
    Console.WriteLine("   opened a vault made by the Python program");
    Environment.SetEnvironmentVariable("PCBUTLER_DATA", Path.Combine(temp, "data"));
}

// ---------------------------------------------------------------- translations and updates
// privacy items are cleaned whatever their age; program logs only a few folders deep
var profile = Path.Combine(temp, "Profile");
Make(Path.Combine(profile, "Roaming", "Microsoft", "Windows", "Recent"), "report.lnk");
Make(Path.Combine(profile, "Roaming", "Microsoft", "Windows", "Recent", "AutomaticDestinations"), "jump.automaticDestinations-ms");
Make(Path.Combine(profile, "Local", "App"), "today.log"); Make(Path.Combine(profile, "Local", "App"), "settings.json");
Make(Path.Combine(profile, "Local", "App", "logs"), "b.log"); Make(Path.Combine(profile, "Local", "App", "a", "b", "c"), "deep.log");
var (oldRoaming, oldLocal) = (Environment.GetEnvironmentVariable("APPDATA"), Environment.GetEnvironmentVariable("LOCALAPPDATA"));
Environment.SetEnvironmentVariable("APPDATA", Path.Combine(profile, "Roaming"));
Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(profile, "Local"));
var privacy = Cleaner.Scan(["recent_files", "program_logs"], CancellationToken.None);
Environment.SetEnvironmentVariable("APPDATA", oldRoaming);
Environment.SetEnvironmentVariable("LOCALAPPDATA", oldLocal);
Check(privacy[0].Files.Count == 2 && privacy[1].Files.Select(Path.GetFileName).Order().SequenceEqual(["b.log", "today.log"]),
      "privacy: " + string.Join(", ", privacy.SelectMany(p => p.Files).Select(Path.GetFileName)));
Check(Cleaner.ByKey("recent_files") is { Privacy: true, Checked: false } && !Cleaner.ByKey("user_temp").Privacy, "privacy items are off until chosen");

// ---------------------------------------------------------------- schedule
Section("schedule");
var plan9 = new ScheduleSettings { Time = "09:00", Day = 1 };
var morning = new DateTime(2026, 10, 14, 10, 0, 0);
Check(plan9.LastSlot("daily", morning) == new DateTime(2026, 10, 14, 9, 0, 0) && plan9.LastSlot("daily", morning.AddHours(-2)) == new DateTime(2026, 10, 13, 9, 0, 0), "daily slot");
var weekSlot = plan9.LastSlot("weekly", morning);
Check(weekSlot.DayOfWeek == DayOfWeek.Monday && weekSlot.Hour == 9 && weekSlot <= morning && (morning - weekSlot).TotalDays < 7
      && plan9.LastSlot("weekly", weekSlot.AddMinutes(-1)) == weekSlot.AddDays(-7) && plan9.LastSlot("weekly", weekSlot) == weekSlot, "weekly slot");
Check(plan9.IsDue("daily", "2026-10-13 09:30", morning) && !plan9.IsDue("daily", "2026-10-14 09:00", morning) && !plan9.IsDue("daily", "", morning)
      && !plan9.IsDue("off", "2020-01-01 00:00", morning) && !plan9.IsDue("interval", "2020-01-01 00:00", morning)
      && plan9.IsDue("weekly", "2026-10-01 12:00", morning) && !plan9.IsDue("weekly", weekSlot.AddHours(1).ToString(ScheduleSettings.Stamp), morning), "due only once per slot");
Check(new Config { AutoSort = true, ScanIntervalMinutes = 0, Schedule = { Sort = "daily" } }.AutoSortOn && !new Config { AutoSort = true, ScanIntervalMinutes = 0 }.AutoSortOn, "daily and weekly schedules don't need the interval");
ConfigStore.Save(new Config { Schedule = { Sort = "weekly", Clean = "daily", Time = "18:30", Day = 5 } });
Check(ConfigStore.Load().Schedule is { Sort: "weekly", Clean: "daily", Time: "18:30", Day: 5, LastSort: "" }, "schedule round trip");

// ---------------------------------------------------------------- storage
Section("storage");
var disk = Path.Combine(temp, "Disk");
void Sized(string folder, string name, int bytes, int ageDays = 0)
{
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, new byte[bytes]);
    if (ageDays > 0)
    {
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-ageDays));
        File.SetLastAccessTime(path, DateTime.Now.AddDays(-ageDays));
    }
}
Sized(Path.Combine(disk, "Movies"), "film.mkv", 3000, ageDays: 400);
Sized(Path.Combine(disk, "Movies", "Old"), "clip.mp4", 1000);
Sized(Path.Combine(disk, "Docs"), "a.txt", 10);
Sized(disk, "loose.bin", 500);
Directory.CreateDirectory(Path.Combine(disk, "Empty"));
for (var i = 0; i < 45; i++)
    Sized(Path.Combine(disk, "Many"), $"f{i:00}.dat", 100 + i);
var scanResult = Storage.Scan(disk, CancellationToken.None);
var diskRoot = scanResult.Root;
Check(diskRoot.Size == 3000 + 1000 + 10 + 500 + Enumerable.Range(0, 45).Sum(i => 100 + i) && diskRoot.Files == 49, $"totals ({diskRoot.Size} bytes, {diskRoot.Files} files)");
Check(diskRoot.Children.Select(c => c.Name).SequenceEqual(["Many", "Movies", "loose.bin", "Docs"]), "biggest first, empty folders left out: " + string.Join(", ", diskRoot.Children.Select(c => c.Name)));
var movies = diskRoot.Children.First(c => c.Name == "Movies");
Check(movies.IsFolder && movies.Size == 4000 && movies.Files == 2 && movies.Parent == diskRoot && movies.Children[0].Name == "film.mkv" && movies.Children[1] is { Name: "Old", IsFolder: true, Size: 1000 }, "folders add up their contents");
var many = diskRoot.Children.First(c => c.Name == "Many");
Check(many.Children.Count == 41 && many.Children.Count(c => c.Path.Length == 0) == 1 && many.Children.First(c => c.Path.Length == 0).Files == 5 && many.Children.Sum(c => c.Size) == many.Size, "a folder's small files are grouped into one entry");
Check(scanResult.BigFiles.Count == 0 && scanResult.Skipped == 0, "nothing here counts as a big file");
scanResult.BigFiles.AddRange([new BigFile(@"C:\x\old-big.iso", 3L << 30, DateTime.Now.AddMonths(-8)), new BigFile(@"C:\x\new-big.iso", 2L << 30, DateTime.Now.AddDays(-2)), new BigFile(@"C:\x\old-small.zip", 200L << 20, DateTime.Now.AddMonths(-20))]);
Check(Storage.LargeAndOld(scanResult, 1L << 30, 6).Select(f => f.Name).SequenceEqual(["old-big.iso"]) && Storage.LargeAndOld(scanResult, 1L << 30, 0).Count == 2 && Storage.LargeAndOld(scanResult, 100L << 20, 12).Select(f => f.Name).SequenceEqual(["old-small.zip"]), "large and old filter");
using (var stopScan = new CancellationTokenSource())
{
    stopScan.Cancel();
    var stoppedScan = false;
    try { Storage.Scan(disk, stopScan.Token); } catch (OperationCanceledException) { stoppedScan = true; }
    Check(stoppedScan, "a storage scan can be cancelled");
}
var boxes = Storage.Treemap([6, 6, 4, 3, 2, 2, 1], 600, 400);
Check(boxes.Count == 7 && Math.Abs(boxes.Sum(b => b.Width * b.Height) - 600 * 400) < 1 && boxes.All(b => b.X >= -0.01 && b.Y >= -0.01 && b.X + b.Width <= 600.01 && b.Y + b.Height <= 400.01), "treemap fills the area exactly and stays inside it");
Check(Math.Abs(boxes[0].Width * boxes[0].Height / (600.0 * 400) - 6.0 / 24) < 0.001 && boxes.All(b => Math.Max(b.Width / b.Height, b.Height / b.Width) < 4.5), "treemap areas match the sizes and the boxes stay roughly square");
Check(Storage.Treemap([], 100, 100).Count == 0 && Storage.Treemap([5], 100, 50) is [(0, 0, 100, 50)], "treemap edge cases");

Section("translations, updates");
Tr.Language = "ar";
Check(Tr.T("Settings") != "Settings" && Tr.T("Moved {0} file(s).", 3).Contains('3') && !Tr.T("Moved {0} file(s).", 3).Contains("Moved"), "Arabic lookups");
Check(Tr.T("zzz not translated {0}", 1) == "zzz not translated 1" && Tr.Missing.Contains("zzz not translated {0}"), "missing text falls back to English");
Tr.Language = "en";
Check(Tr.T("Moved {0} file(s).", 3) == "Moved 3 file(s).", "English formatting");
Check(Updater.CompareVersions("v1.10.0", "1.9.9") > 0 && Updater.CompareVersions("1.0", "1.0.0") == 0 && Updater.CompareVersions("v1.0.0", "1.0.1") < 0, "version comparison");

try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
