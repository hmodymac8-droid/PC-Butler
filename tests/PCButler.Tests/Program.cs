// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
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

var verbose = Environment.GetEnvironmentVariable("PCBUTLER_TESTS_VERBOSE") == "1";

void Check(bool condition, string what)
{
    if (condition && verbose)
        Console.WriteLine("ok: " + what);
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
var git = Programs.EssentialApps.First(app => app.Id == "Git.Git");
Check(Programs.IsInstalled(git, [new InstalledProgram { Name = "Git", Hive = "HKCU", Key = "x" }]) && !Programs.IsInstalled(git, [new InstalledProgram { Name = "GitHub Desktop", Hive = "HKCU", Key = "x" }])
      && Programs.IsInstalled(Programs.EssentialApps.First(app => app.Id == "Microsoft.DotNet.DesktopRuntime.8"),
                              [new InstalledProgram { Name = "Microsoft Windows Desktop Runtime - 8.0.11 (x64)", Hive = "HKCU", Key = "x" }]), "app lists match whole names");
var echo = Programs.RecommendedApps.First(app => app.Id == "mPhpMaster/echo-sub");
Check(Programs.IsInstalled(echo, [new InstalledProgram { Name = "EchoSub", Hive = "HKCU", Key = "x" }])
      && !Programs.IsInstalled(echo, [new InstalledProgram { Name = "EchoSub Lens", Hive = "HKCU", Key = "x" }]), "an exact app name is not taken from a longer one");
// A GitHub app's setup comes down whole and matches its published checksum (it is not run).
var lensSetup = Programs.DownloadRelease(Programs.RecommendedApps.First(app => app.Id == "mPhpMaster/echo-sub-lens"));
Check(File.Exists(lensSetup) && new FileInfo(lensSetup).Length > 1_000_000, "a GitHub app's setup is downloaded and checked");
File.Delete(lensSetup);
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
// PCBUTLER_DATA is set, so this writes under Software\PCButlerTest, where Windows never looks.
ExplorerMenu.Remove();
Check(ExplorerMenu.InstalledCommand() is null, "no menu to begin with");
Check(ExplorerMenu.Sync(true, @"C:\Apps\PC Butler\PCButler.exe", english => english) is null
      && ExplorerMenu.InstalledCommand("sort") == ExplorerMenu.CommandFor(@"C:\Apps\PC Butler\PCButler.exe", "sort")
      && ExplorerMenu.InstalledCommand("shred") == ExplorerMenu.CommandFor(@"C:\Apps\PC Butler\PCButler.exe", "shred")
      && ExplorerMenu.CommandFor("P.exe", "sort") == "\"P.exe\" --act sort \"%1\"", "each submenu item runs the program with its action");
using (var fileMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTest\Classes\*\shell\PCButler"))
    Check(fileMenu?.GetValue("MUIVerb") as string == "PC Butler" && fileMenu.GetValue("SubCommands") as string == ""
          && fileMenu.OpenSubKey("command") is null && fileMenu.OpenSubKey(@"shell")!.GetSubKeyNames().SequenceEqual(["1sort", "2vault", "3shred"]),
          "files get the submenu, in order");
using (var folderMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTest\Classes\Directory\shell\PCButler\shell"))
    Check(folderMenu is not null && !folderMenu.GetSubKeyNames().Contains("2vault") && folderMenu.GetSubKeyNames().SequenceEqual(["1sort", "3shred"]),
          "folders get it too, without the Vault");
using (var real = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\PCButler\shellsort\command"))
    Check(real?.GetValue("") as string != ExplorerMenu.CommandFor(@"C:\Apps\PC Butler\PCButler.exe", "sort"), "the real menu is left alone");
Check(ExplorerMenu.Sync(false, "x", english => english) is null && ExplorerMenu.InstalledCommand() is null, "switching it off removes it");
// Several selected items: every copy hands its path to the first, which acts once.
var batches = new List<string>?[6];
var copies = Enumerable.Range(0, batches.Length).Select(number => new Thread(() => batches[number] = MenuBatch.Gather("test", $@"C:\item{number}.txt"))).ToList();
copies.ForEach(copy => copy.Start());
copies.ForEach(copy => copy.Join());
var gathered = batches.Where(batch => batch is not null).ToList();
Check(gathered.Count == 1 && gathered[0]!.Count == batches.Length, $"several selected items make one batch ({string.Join(", ", gathered.Select(batch => batch!.Count))})");
var hadClassic = ExplorerMenu.ClassicMenu;
Check(ExplorerMenu.SetClassicMenu(true) is null && ExplorerMenu.ClassicMenu && ExplorerMenu.SetClassicMenu(false) is null && !ExplorerMenu.ClassicMenu,
      "the classic right-click menu can be switched on and off");
ExplorerMenu.SetClassicMenu(hadClassic);
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

// ---------------------------------------------------------------- shredder, drop zone, vault inbox
Section("shredder");
var doomed = Path.Combine(temp, "Doomed");
Directory.CreateDirectory(Path.Combine(doomed, "inner"));
File.WriteAllBytes(Path.Combine(doomed, "secret.bin"), new byte[3_000_000]);
File.WriteAllText(Path.Combine(doomed, "inner", "note.txt"), "private");
File.SetAttributes(Path.Combine(doomed, "inner", "note.txt"), FileAttributes.ReadOnly);
var single = Path.Combine(temp, "single.txt");
File.WriteAllText(single, "one");
Check(Shredder.Measure([doomed, single]) == (3, 3_000_010), "what will be shredded is measured");
long shredDone = 0, shredTotal = 0;
var shredded = Shredder.Shred([doomed, single, Path.Combine(temp, "missing.txt")], (d, t) => (shredDone, shredTotal) = (d, t));
Check(shredded.Count == 3 && shredded.Errors.Count == 0 && !Directory.Exists(doomed) && !File.Exists(single) && shredDone == shredTotal && shredTotal == 3_000_010,
      $"files and a whole folder are overwritten and gone ({shredded.Count}, {string.Join("; ", shredded.Errors)})");
Check(History.Load().SelectMany(run => run.Moves).Count(move => move.Count == 3 && move[2] == "erased" && (move[0] == doomed || move[0] == single)) == 2,
      "and logged as erased");
Check(Shredder.IsProtected(@"C:\") && Shredder.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
      && Shredder.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers"))
      && Shredder.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) && Shredder.IsProtected(AppInfo.DataDir)
      && !Shredder.IsProtected(Path.Combine(temp, "anything")), "Windows, the programs, the profile and whole drives are refused");
Check(Shredder.Shred([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]) is { Count: 0, Errors.Count: 1 }, "a protected folder is not touched");

var dropRoot = Path.Combine(temp, "DropRoot");
var dropFrom = Path.Combine(temp, "DropFrom");
Directory.CreateDirectory(dropRoot); Directory.CreateDirectory(Path.Combine(dropFrom, "Folder"));
foreach (var name in new[] { "a.pdf", "b.mp3", "c.unknownkind", @"Folder\d.jpg", @"Folder\e.zip" }) File.WriteAllText(Path.Combine(dropFrom, name), "x");
var dropConfig = new Config { OtherFolder = "" };
var dropPlan = Sorter.PlanDropped(dropConfig, [Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "b.mp3"), Path.Combine(dropFrom, "c.unknownkind"), Path.Combine(dropFrom, "Folder")], dropRoot);
Check(dropPlan.Count == 4 && dropPlan[0].Folder == Path.Combine(dropRoot, "Documents") && dropPlan[1].Folder == Path.Combine(dropRoot, "Audio")
      && dropPlan.Count(m => m.Folder.StartsWith(Path.Combine(dropFrom, "Folder"))) == 2, $"dropped files go to the sorted folder, a dropped folder is sorted inside ({dropPlan.Count})");
Check(Sorter.PlanDropped(dropConfig, [Path.Combine(dropFrom, "a.pdf")], "").Count == 0, "no sorted folder, no plan");

VaultInbox.Add([Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "gone.pdf")]);
VaultInbox.Add([Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "b.mp3")]);
Check(VaultInbox.Take().SequenceEqual([Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "b.mp3")]) && VaultInbox.Take().Count == 0,
      "files waiting for the vault are handed over once");

// ---------------------------------------------------------------- courier: names, codes, routers
Section("courier");
Check(Portal.SafeName(@"..\..\Windows\evil.exe") == "evil.exe" && Portal.SafeName("a<b>:c?.txt") == "a_b__c_.txt" && Portal.SafeName("CON.txt") == "_CON.txt"
      && Portal.SafeName("...") == "file" && Portal.SafeName("/etc/passwd") == "passwd" && Portal.SafeName("") == "file", "names from another PC are made safe");
Check(Portal.SafePath(@"Photos\2024/../..\..\a.jpg") == "Photos/2024/file/file/file/a.jpg" && Portal.SafePath("/abs/x") == "abs/x" && Portal.SafePath("") == "file"
      && Portal.CleanLabel("  My\r\nPC\t ") == "My  PC" && Portal.CleanLabel("") == "PC", "paths can not climb out, and names are fit to show");
Check(PortalNode.PairingCode("AA", "BB", "1", "2") is { Length: 7 } pairCode && pairCode[3] == ' ' && pairCode == PortalNode.PairingCode("aa", "bb", "1", "2")
      && pairCode != PortalNode.PairingCode("BB", "AA", "1", "2") && pairCode != PortalNode.PairingCode("AA", "BB", "1", "3"), "the pairing code depends on both PCs and both random parts");
Check(PortalNat.ParseSsdpLocation("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLocation: http://192.168.1.1:49152/rootDesc.xml\r\nST: x\r\n\r\n")?.ToString()
          == "http://192.168.1.1:49152/rootDesc.xml"
      && PortalNat.ParseSsdpLocation("HTTP/1.1 200 OK\r\nLOCATION: http://example.com/x.xml\r\n\r\n") is null
      && PortalNat.ParseSsdpLocation("HTTP/1.1 200 OK\r\n\r\n") is null, "a router's answer to the search is read, and only a local one is taken");
var gatewayDescription = """
    <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceList><device><serviceList>
    <service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>/ctl/CmnIfCfg</controlURL></service>
    <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL></service>
    </serviceList></device></deviceList></device></root>
    """;
Check(PortalNat.ParseControl(gatewayDescription, new Uri("http://192.168.1.1:49152/rootDesc.xml")) is var (control, service)
      && control.ToString() == "http://192.168.1.1:49152/ctl/IPConn" && service == "urn:schemas-upnp-org:service:WANIPConnection:1"
      && PortalNat.ParseControl("<root><service><serviceType>urn:x</serviceType><controlURL>/a</controlURL></service></root>", new Uri("http://10.0.0.1/d.xml")) is null,
      "the router's description gives the place to ask for a port");
Check(PortalNat.ParseNatPmpMapping([0, 129, 0, 0, 0, 0, 0, 1, 0xC3, 0x50, 0xC3, 0x51, 0, 0, 0x0E, 0x10]) == 50001
      && PortalNat.ParseNatPmpMapping([0, 129, 0, 3, 0, 0, 0, 1, 0xC3, 0x50, 0xC3, 0x51, 0, 0, 0x0E, 0x10]) is null && PortalNat.ParseNatPmpMapping([0, 129]) is null,
      "a NAT-PMP answer gives the forwarded port, and a refusal is not taken for one");

System.Security.Cryptography.X509Certificates.X509Certificate2 Identity() =>
    System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(Portal.NewCertificate(), null);
async Task<bool> Until(Func<bool> condition, int seconds = 15)
{
    for (var waited = 0; waited < seconds * 20 && !condition(); waited++)
        await Task.Delay(50);
    return condition();
}

// ---------------------------------------------------------------- courier: a host and its clients (all pretended on this PC, over loopback)
Section("courier host and clients");
{
    var noRole = false;
    try
    {
        await PortalNode.Start(new PortalSettings { Folder = Path.Combine(temp, "CourierNone") }, local: true);
    }
    catch (IOException)
    {
        noRole = true;
    }
    Check(noRole, "Courier does not start before the PC is a host or a client");

    var hostSettings = new PortalSettings { Folder = Path.Combine(temp, "CourierHost"), Role = PortalSettings.Host };
    var hostSaved = 0;
    var host = await PortalNode.Start(hostSettings, () => hostSaved++, Identity(), local: true);
    var hostAt = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, host.LinkPort);
    PortalSettings ClientSettings(string name) => new() { Folder = Path.Combine(temp, name), Role = PortalSettings.Client, HostNumber = host.Number };
    var (aliceSettings, bobSettings) = (ClientSettings("CourierAlice"), ClientSettings("CourierBob"));
    var alice = await PortalNode.Start(aliceSettings, certificate: Identity(), local: true);
    var bob = await PortalNode.Start(bobSettings, certificate: Identity(), local: true);
    var stranger = await PortalNode.Start(ClientSettings("CourierStranger"), certificate: Identity(), local: true);
    var hostAsked = 0;
    host.Attention += _ => Interlocked.Increment(ref hostAsked);
    Check(host.IsHost && !alice.IsHost && host.Number.Length == 12 && host.Number != alice.Number && host.LinkPort > 0, "each PC has its ID and its role");

    using (var noCertificate = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
    {
        var turnedAway = false;
        try
        {
            using var answer = await noCertificate.GetAsync($"https://127.0.0.1:{host.Port}/portal/v1/who");
            turnedAway = !answer.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            turnedAway = true;
        }
        Check(turnedAway, "a caller that shows no certificate gets nothing");
    }

    // The clients reach the host across the path; the host reaches nobody first.
    var aliceHost = await alice.ConnectDirect(hostAt, host.Number);
    Check(aliceHost is { Online: true, Trusted: false } && aliceHost.Id == host.Id && alice.HostPeer == aliceHost, "a client finds the host by its address and ID");
    Check(await alice.ConnectDirect(hostAt, "000000000000") is null, "a host that answers under another ID than the one expected is not taken");
    Check(await host.ConnectDirect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, stranger.LinkPort)) is null, "a client takes no path it did not open itself");
    Check((await alice.ConnectById("12 34")).Problem == PortalConnectProblem.BadNumber && (await alice.ConnectById(alice.Number)).Problem == PortalConnectProblem.BadNumber
          && (await alice.ConnectById("123 456 789 012")).Problem == PortalConnectProblem.NoDirectory, "a wrong ID, or no directory, is said plainly");
    var early = alice.Send(aliceHost!, [Make(Path.Combine(temp, "CourierOut"), "early.txt", "too early")])!;
    Check(await Until(() => early.State == "failed") && !Directory.EnumerateFiles(hostSettings.Folder).Any(), "nothing can be sent before pairing");

    // Pairing: both sides show the same code, each user confirms on their own PC.
    async Task<PortalPeer> Join(PortalNode client)
    {
        var found = client.HostPeer ?? await client.ConnectDirect(hostAt, host.Number);
        var asked = await client.Pair(found!);
        Check(asked is { Code.Length: 7 } && await Until(() => host.Pairings.Any(pairing => pairing.PeerId == client.Id && pairing.Code == asked.Code)),
              "a client's pairing reaches the host with the same code");
        client.Confirm(asked!);
        host.Confirm(host.Pairings.Single(pairing => pairing.PeerId == client.Id));
        return found!;
    }
    var aliceToHost = await Join(alice);
    Check(host.Peers.Single(peer => peer.Id == alice.Id) is { Trusted: true, Online: true } && hostSettings.Devices.Count == 1 && hostSaved >= 1
          && aliceSettings.Devices is [{ }] && aliceSettings.Devices[0].Id == host.Id && hostAsked == 1, "the host and the client trust each other, and both save it");
    var bobToHost = await Join(bob);
    Check(hostSettings.Devices.Count == 2, "a host has as many clients as join it");

    // Client to host, host to client.
    var payload = new byte[20_000_000];
    Random.Shared.NextBytes(payload);
    var payloadFile = Path.Combine(temp, "CourierOut", "holiday video.mp4");
    File.WriteAllBytes(payloadFile, payload);
    var upward = alice.Send(aliceToHost, [payloadFile])!;
    Check(await Until(() => host.Requests.Count == 1) && upward.State == "waiting" && host.Requests[0] is { Files: 1, Size: 20_000_000 }, "the host is asked before anything is taken");
    host.Accept(host.Requests[0].Id);
    Check(await Until(() => !upward.Running, 60) && upward.State == "done" && File.ReadAllBytes(Path.Combine(hostSettings.Folder, "holiday video.mp4")).SequenceEqual(payload),
          $"a file of several pieces reaches the host whole ({upward.State} {upward.Note})");
    var album = Path.Combine(temp, "CourierOut", "Album");
    Make(Path.Combine(album, "inner", "deep"), "three.txt", "three");
    Make(album, "empty.txt", "");
    alice.SetAutoAccept(host.Id, true);
    var downward = host.Send(host.Peers.Single(peer => peer.Id == alice.Id), [album])!;
    Check(await Until(() => !downward.Running, 30) && downward.State == "done" && File.ReadAllText(Path.Combine(aliceSettings.Folder, "Album", "inner", "deep", "three.txt")) == "three"
          && new FileInfo(Path.Combine(aliceSettings.Folder, "Album", "empty.txt")).Length == 0, $"a folder goes from the host to a client, structure and all ({downward.State} {downward.Note})");

    // Client to client, through the host.
    Check(await Until(() => alice.OtherClients.Any(client => client.Id == bob.Id && client.Online), 25) && alice.OtherClients.All(client => client.Id != alice.Id),
          "a client learns from the host which other clients there are");
    var forBob = alice.OtherClients.Single(client => client.Id == bob.Id);
    var note = Make(Path.Combine(temp, "CourierOut", "Letters"), "for bob.txt", "hello Bob, from Alice");
    var across = alice.Send(aliceToHost, [note], forBob)!;
    Check(await Until(() => bob.Requests.Count == 1, 30) && bob.Requests[0].PeerName.Contains(alice.Name) && host.Requests.Count == 0,
          "files for another client pass the host without asking its user, and the client they are for is asked");
    bob.Accept(bob.Requests[0].Id);
    Check(await Until(() => File.Exists(Path.Combine(bobSettings.Folder, "for bob.txt")), 30) && File.ReadAllText(Path.Combine(bobSettings.Folder, "for bob.txt")) == "hello Bob, from Alice"
          && across.State == "done", $"and arrive there ({across.State} {across.Note})");
    Check(await Until(() => !Directory.Exists(Portal.RelayFolder) || !Directory.EnumerateFileSystemEntries(Portal.RelayFolder).Any(), 10)
          && !File.Exists(Path.Combine(hostSettings.Folder, "for bob.txt")), "the host keeps no copy");
    var refusedAcross = alice.Send(aliceToHost, [note], forBob)!;
    Check(await Until(() => bob.Requests.Count == 1, 30), "another one waits on the client it is for");
    bob.Refuse(bob.Requests[0].Id);
    Check(await Until(() => !Directory.Exists(Portal.RelayFolder) || !Directory.EnumerateFileSystemEntries(Portal.RelayFolder).Any(), 15)
          && !File.Exists(Path.Combine(bobSettings.Folder, "for bob (1).txt")), "refused on the way, it never arrives and the host lets it go");
    var nobody = alice.Send(aliceToHost, [note], new PortalClient(new string('A', 64), "Nobody", true))!;
    Check(await Until(() => nobody.State == "failed") && nobody.Note.Length > 0, "a client the host does not have is said plainly");

    // A PC that was never paired, and a client asked to pair by someone.
    var strangerHost = await stranger.ConnectDirect(hostAt, host.Number);
    var intruding = stranger.Send(strangerHost!, [payloadFile])!;
    Check(await Until(() => intruding.State == "failed") && host.Requests.Count == 0, "a PC that was never paired can not even ask");
    host.Forget(bob.Id);
    var afterForget = bob.Send(bobToHost, [note])!;
    Check(await Until(() => afterForget.State == "failed") && hostSettings.Devices.Count == 1, "a forgotten client must pair again");

    await host.Stop();
    await alice.Stop();
    await bob.Stop();
    await stranger.Stop();
    Check(host.Stopped, "Courier stops");
}
var allowed = 0;
File.WriteAllText(Path.Combine(helperDir, "helper-command.json"), """{"id":"p1","action":"allow_portal","services":["Spooler"]}""");
AdminHelper.Poll(helperDir, [], (_, _) => { allowed += 100; return true; }, () => { allowed++; return true; });
Check(allowed == 1 && File.ReadAllText(Path.Combine(helperDir, "helper-result.json")).Contains("\"portal\""), "the helper firewall request does that one thing only");
Check(!Portal.FirewallAllows(@"C:\nowhere\nothing.exe"), "the firewall can be read");

// ---------------------------------------------------------------- the link across the internet (pretended on this PC)
Section("tunnel");
{
    // Two ends joined by a bad line: one packet in twelve is lost, and some arrive late and out of order.
    ReliableStream? near = null, far = null;
    var line = new Random(7);
    void Carry(Func<ReliableStream?> to, byte[] packet, int length)
    {
        var copy = packet.AsSpan(0, length).ToArray();
        lock (line)
        {
            if (line.Next(12) == 0)
                return;
            var late = line.Next(6) == 0 ? line.Next(5, 40) : 0;
            _ = Task.Delay(late).ContinueWith(_ => to()?.OnPacket(copy[0], copy.AsSpan(1)));
        }
    }
    near = new ReliableStream((packet, length) => Carry(() => far, packet, length));
    far = new ReliableStream((packet, length) => Carry(() => near, packet, length));
    using var beat = new CancellationTokenSource();
    _ = Task.Run(async () =>
    {
        while (!beat.IsCancellationRequested)
        {
            near.Tick();
            far.Tick();
            await Task.Delay(10);
        }
    });
    var outward = new byte[3_000_000];
    var homeward = new byte[700_000];
    Random.Shared.NextBytes(outward);
    Random.Shared.NextBytes(homeward);
    async Task<byte[]> ReadAll(Stream from)
    {
        var all = new MemoryStream();
        var piece = new byte[70_000];
        for (int read; (read = await from.ReadAsync(piece)) > 0;)
            all.Write(piece, 0, read);
        return all.ToArray();
    }
    var gotOutward = ReadAll(far);
    var gotHomeward = ReadAll(near);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var writing = Task.WhenAll(
        Task.Run(async () => { await near.WriteAsync(outward); await near.Complete(); }),
        Task.Run(async () => { await far.WriteAsync(homeward); await far.Complete(); }));
    await Task.WhenAny(Task.WhenAll(writing, gotOutward, gotHomeward), Task.Delay(60_000));
    var finished = writing.IsCompletedSuccessfully && gotOutward.IsCompletedSuccessfully && gotHomeward.IsCompletedSuccessfully;
    Check(finished && gotOutward.Result.SequenceEqual(outward) && gotHomeward.Result.SequenceEqual(homeward),
          $"data crosses a line that loses and shuffles packets, whole and in order, both ways ({clock.ElapsedMilliseconds} ms; {near.Describe()})");
    Check(clock.ElapsedMilliseconds < 30_000, $"and at a usable pace despite the losses ({clock.ElapsedMilliseconds} ms for 3.7 MB)");
    for (var waited = 0; waited < 100 && !(near.Over && far.Over); waited++)
        await Task.Delay(50);
    Check(near.Over && far.Over && !near.Failed && !far.Failed, "and both ends see it finish cleanly");
    beat.Cancel();
}
{
    // Two real sockets on this PC: open a path, open a stream, send and get it homeward.
    using var caller = new PortalLink(System.Net.IPAddress.Loopback);
    using var answerer = new PortalLink(System.Net.IPAddress.Loopback);
    using var closedDoor = new PortalLink(System.Net.IPAddress.Loopback);
    var answererAt = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, answerer.Port);
    answerer.AllowPath = _ => true;
    answerer.Accepted = async (stream, _) =>
    {
        // Sends homeward whatever arrives.
        var piece = new byte[60_000];
        for (int read; (read = await stream.ReadAsync(piece)) > 0;)
            await stream.WriteAsync(piece.AsMemory(0, read));
        await stream.Complete();
    };
    Check(!await caller.PunchTo(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, closedDoor.Port), TimeSpan.FromSeconds(1.2)),
          "a PC that is not reachable does not answer a knock");
    Check(await caller.PunchTo(answererAt, TimeSpan.FromSeconds(5)) && caller.IsOpen(answererAt), "a path opens to a PC that is");
    var message = new byte[2_500_000];
    Random.Shared.NextBytes(message);
    await using var line = await caller.Connect(answererAt);
    var echoed = Task.Run(async () =>
    {
        var all = new MemoryStream();
        var piece = new byte[60_000];
        for (int read; (read = await line.ReadAsync(piece)) > 0;)
            all.Write(piece, 0, read);
        return all.ToArray();
    });
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var sendingIt = Task.Run(async () => { await line.WriteAsync(message); await line.Complete(); });
    Check(await Task.WhenAny(Task.WhenAll(sendingIt, echoed), Task.Delay(30_000)) != sendingIt && echoed.IsCompletedSuccessfully && echoed.Result.SequenceEqual(message),
          $"a stream over real sockets carries data outward and homeward ({message.Length * 2 / 1048576.0 / watch.Elapsed.TotalSeconds:0} MB/s on this PC)");
    var refusedStream = false;
    try
    {
        await closedDoor.Connect(answererAt);
    }
    catch (IOException)
    {
        refusedStream = true;
    }
    Check(refusedStream, "no stream without an open path");
}

// ---------------------------------------------------------------- the public directory's format, and IDs
Section("courier IDs");
{
    var packed = Bencode.Encode(new Dictionary<string, object>
    {
        ["t"] = new byte[] { 0, 7 }, ["y"] = "q", ["q"] = "get_peers", ["a"] = new Dictionary<string, object> { ["id"] = new byte[20], ["n"] = -42L, ["l"] = new List<object> { "x", 3L } },
    });
    Check(Encoding.Latin1.GetString(packed).StartsWith("d1:ad2:id20:") && Bencode.Decode(packed) is Dictionary<string, object> unpacked
          && unpacked["a"] is Dictionary<string, object> inner && (long)inner["n"] == -42 && inner["l"] is List<object> { Count: 2 }
          && Encoding.ASCII.GetString((byte[])unpacked["q"]) == "get_peers", "the directory's message format is written and read back");
    Check(Bencode.Decode(Encoding.ASCII.GetBytes("d1:a")) is null && Bencode.Decode(Encoding.ASCII.GetBytes("5:ab")) is null && Bencode.Decode(Encoding.ASCII.GetBytes("i12")) is null
          && Bencode.Decode(Encoding.ASCII.GetBytes(new string('l', 50))) is null && Bencode.Decode([]) is null, "broken messages from strangers are refused, not crashed on");
    var someId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("a certificate")));
    Check(Portal.NumberOf(someId) is { Length: 12 } digits && digits.All(char.IsDigit) && digits == Portal.NumberOf(someId.ToLowerInvariant())
          && Portal.ShowNumber("482019337615") == "482 019 337 615" && Portal.DirectoryKey(digits, false).Length == 20
          && !Portal.DirectoryKey(digits, false).SequenceEqual(Portal.DirectoryKey(digits, true)), "a PC's ID is twelve digits made from its certificate");

}

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

Section("clipboard history");
ClipboardHistory.Clear(keepPinned: false);
ClipboardHistory.Add(new ClipEntry { Text = "first" }, 3);
ClipboardHistory.Add(new ClipEntry { Text = "second" }, 3);
ClipboardHistory.Add(new ClipEntry { Text = "first" }, 3);
Check(ClipboardHistory.Entries.Select(e => e.Text).SequenceEqual(["first", "second"]), "copying the same text again moves it to the top");
ClipboardHistory.SetPinned(ClipboardHistory.Entries[1], true);
foreach (var text in new[] { "a", "b", "c", "d" })
    ClipboardHistory.Add(new ClipEntry { Text = text }, 3);
Check(ClipboardHistory.Entries.Count(e => !e.Pinned) == 3 && ClipboardHistory.Entries.Any(e => e.Text == "second" && e.Pinned)
      && !ClipboardHistory.Entries.Any(e => e.Text == "first"), "the limit drops the oldest unpinned items, never pinned ones");
Check(File.Exists(Path.Combine(ClipboardHistory.Folder, "history.json")), "the history is kept on disk");
ClipboardHistory.Clear();
Check(ClipboardHistory.Entries.Count == 1 && ClipboardHistory.Entries[0].Pinned, "Clear keeps pinned items");
ClipboardHistory.Apply(new ClipboardSettings { KeepOnDisk = false });
Check(!File.Exists(Path.Combine(ClipboardHistory.Folder, "history.json")), "turning off Keep after restart removes the file");
Check(new ClipEntry { Kind = "files", Files = [Path.Combine(temp, "a.txt"), Path.Combine(temp, "b.txt")] }.Preview == "a.txt (+1)"
      && new ClipEntry { Text = "one" + Environment.NewLine + "two" }.Preview == "one two", "list previews");

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
