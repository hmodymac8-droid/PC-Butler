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

// a category that deletes its files instead of moving them
config.Categories.Add(new Category { Folder = "", Extensions = [".nfo"], Delete = true });
var nfo = Make(downloads, "scene.nfo", "release notes", ageDays: 10);
var nfoPlan = Sorter.PlanMoves(config).Where(move => move.Name == "scene.nfo").ToList();
Check(nfoPlan is [{ Delete: true }], "a file of a type set to be deleted is planned for deleting, not moving");
var nfoRun = Sorter.MoveFiles(nfoPlan);
Check(nfoRun is { Count: 1, Deleted: 1, Errors.Count: 0 } && !File.Exists(nfo) && History.Load()[^1].Moves[0] is [_, "", "deleted"],
      "it goes to the Recycle Bin, and History lists it");
Check(Sorter.UndoMoves([(History.Load()[^1].Time, 0)]).Count == 1 && File.Exists(nfo) && !Sorter.PlanMoves(config).Any(move => move.Name == "scene.nfo"),
      "brought back, it is not deleted again by itself");
config.Categories.RemoveAt(config.Categories.Count - 1);
File.Delete(nfo);

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
{
    // An app that is its repository: the zip GitHub makes holds one folder, whose contents become the app's folder.
    var repo = Path.Combine(temp, "zipped", "someapp-main");
    Make(Path.Combine(repo, "tools"), "run.ps1", "new");
    Make(repo, "app.vbs", "new launcher");
    var zipFile = Path.Combine(temp, "someapp.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(temp, "zipped"), zipFile);
    var appFolder = Path.Combine(temp, "Programs", "SomeApp");
    Make(appFolder, "app.vbs", "old launcher");
    Make(appFolder, "settings.json", "mine");
    Programs.Unpack(zipFile, appFolder);
    Check(File.ReadAllText(Path.Combine(appFolder, "app.vbs")) == "new launcher" && File.ReadAllText(Path.Combine(appFolder, "tools", "run.ps1")) == "new"
          && File.ReadAllText(Path.Combine(appFolder, "settings.json")) == "mine" && !Directory.Exists(Path.Combine(appFolder, "someapp-main"))
          && !Directory.Exists(zipFile + ".unpacked"), "an app unpacked from its repository's zip replaces its files and keeps what it made itself");
    var appDc = Programs.RecommendedApps.Single(app => app.Name == "AndroidDC");
    Check(appDc is { Source: AppSource.Archive, Archive: "https://github.com/mPhpMaster/androiddc/archive/refs/heads/main.zip" } && appDc.Steps.Length == 2
          && Programs.FindInstalled(appDc, []) is null, "AndroidDC is installed from its repository's zip, then its own setup steps");
}
InstalledProgram Listed(string name, string version) => new() { Name = name, Version = version, Hive = "HKCU", Key = name };
List<InstalledProgram> some = [Listed("Microsoft Edge", "130.0"), Listed("Microsoft Edge WebView2 Runtime", "130.0"),
                               Listed("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38", "14.38"),
                               Listed("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.36", "14.36")];
Check(Programs.FindInstalled(new ProgramUpdate("Microsoft Edge WebView2 Runtime", "Microsoft.EdgeWebView2Runtime", "130.0", "131.0"), some)?.Name == "Microsoft Edge WebView2 Runtime"
      && Programs.FindInstalled(new ProgramUpdate("Microsoft Edge", "Microsoft.Edge", "130.0", "131.0"), some)?.Name == "Microsoft Edge"
      && Programs.FindInstalled(new ProgramUpdate("Microsoft Ed", "X.Y", "1", "2"), some) is null,
      "an update is for the program of that very name, not one whose name starts the same");
Check(Programs.FindInstalled(new ProgramUpdate("Microsoft Visual C++ 2015-2022 Redistrib…", "Microsoft.VCRedist.2015+.x86", "14.36", "14.40"), some)?.Version == "14.36"
      && Programs.FindInstalled(new ProgramUpdate("Microsoft Visual C++ 2015-2022 Redistrib…", "Microsoft.VCRedist.2015+.x64", "14.20", "14.40"), some) is null,
      "a name winget cut short is matched by its start, and by its version when several start the same");

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
      && ExplorerMenu.InstalledCommand("courier") == ExplorerMenu.CommandFor(@"C:\Apps\PC Butler\PCButler.exe", "courier")
      && ExplorerMenu.CommandFor("P.exe", "sort") == "\"P.exe\" --act sort \"%1\"", "each submenu item runs the program with its action");
using (var fileMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTest\Classes\*\shell\PCButler"))
    Check(fileMenu?.GetValue("MUIVerb") as string == "PC Butler" && fileMenu.GetValue("SubCommands") as string == ""
          && fileMenu.OpenSubKey("command") is null && fileMenu.OpenSubKey(@"shell")!.GetSubKeyNames().SequenceEqual(["1sort", "2courier", "3vault", "4shred"])
          && fileMenu.OpenSubKey(@"shell\3vault")!.GetValue("CommandFlags") is ExplorerMenu.SeparatorBefore
          && fileMenu.OpenSubKey(@"shell\4shred")!.GetValue("CommandFlags") is ExplorerMenu.SeparatorBefore
          && fileMenu.OpenSubKey(@"shell\2courier")!.GetValue("CommandFlags") is null,
          "files get the submenu, in order, in groups with a line between them");
using (var folderMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTest\Classes\Directory\shell\PCButler\shell"))
    Check(folderMenu is not null && !folderMenu.GetSubKeyNames().Contains("3vault") && folderMenu.GetSubKeyNames().SequenceEqual(["1sort", "2courier", "4shred"]),
          "folders get it too, without the Vault");
using (var share = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PCButlerTest\Classes\Directory\shell\PCButler\shell\2courier"))
    Check(share?.GetValue("MUIVerb") as string == "Share with Courier" && share.GetValue("MultiSelectModel") as string == "Player",
          "files and folders can be shared with Courier, several at once");
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
CourierInbox.Add([Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "gone.pdf"), Path.Combine(dropFrom, "Folder")]);
CourierInbox.Add([Path.Combine(dropFrom, "a.pdf")]);
Check(CourierInbox.Take().SequenceEqual([Path.Combine(dropFrom, "a.pdf"), Path.Combine(dropFrom, "Folder")]) && CourierInbox.Take().Count == 0,
      "files and folders shared with Courier are handed over once");

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

// ---------------------------------------------------------------- courier: PCs that are all alike (all pretended on this PC, over loopback)
Section("courier PCs");
{
    var officeSettings = new PortalSettings { Folder = Path.Combine(temp, "CourierOffice") };
    var officeSaved = 0;
    var office = await PortalNode.Start(officeSettings, () => officeSaved++, Identity(), local: true);
    var officeAt = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, office.LinkPort);
    var (aliceSettings, bobSettings) = (new PortalSettings { Folder = Path.Combine(temp, "CourierAlice") }, new PortalSettings { Folder = Path.Combine(temp, "CourierBob") });
    var alice = await PortalNode.Start(aliceSettings, certificate: Identity(), local: true);
    var bob = await PortalNode.Start(bobSettings, certificate: Identity(), local: true);
    var stranger = await PortalNode.Start(new PortalSettings { Folder = Path.Combine(temp, "CourierStranger") }, certificate: Identity(), local: true);
    var strangerAt = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, stranger.LinkPort);
    var bobAt = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, bob.LinkPort);
    var officeAsked = 0;
    office.Attention += _ => Interlocked.Increment(ref officeAsked);
    var officeGot = new List<PortalTransfer>();
    office.Received += transfer => { lock (officeGot) officeGot.Add(transfer); };
    Check(Portal.IsNumber(office.Number) && office.Number != alice.Number && alice.Number != bob.Number && office.LinkPort > 0 && alice.LinkPort > 0,
          "each PC has its own ID, with nothing to choose first");

    {
        // Over a real path, a caller that shows no certificate of its own gets nothing.
        using var bare = new PortalLink(System.Net.IPAddress.Loopback);
        var turnedAway = !await bare.PunchTo(officeAt, TimeSpan.FromSeconds(5));
        if (!turnedAway)
        {
            try
            {
                await using var line = await bare.Connect(officeAt);
                await using var tls = new System.Net.Security.SslStream(line, false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync("portal.direct");
                await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /portal/v1/who HTTP/1.1\r\nHost: x\r\n\r\n"));
                var reply = new byte[256];
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                turnedAway = await tls.ReadAsync(reply, wait.Token) == 0;
            }
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            {
                turnedAway = true;
            }
        }
        Check(turnedAway, "a caller that shows no certificate gets nothing");
    }

    // Any PC reaches any other by its address and ID.
    var aliceOffice = await alice.ConnectDirect(officeAt, office.Number);
    Check(aliceOffice is { Online: true, Trusted: false } && aliceOffice.Id == office.Id && alice.PeerOf(office.Number) == aliceOffice, "a PC finds another by its address and ID");
    Check(await alice.ConnectDirect(officeAt, "000000000") is null, "a PC that answers under another ID than the one expected is not taken");
    Check(await office.ConnectDirect(strangerAt, stranger.Number) is { } reached && reached.Id == stranger.Id, "every PC can be reached, not only one");
    Check((await alice.ConnectById("K7M")).Problem == PortalConnectProblem.BadNumber && (await alice.ConnectById(alice.Number)).Problem == PortalConnectProblem.BadNumber
          && (await alice.ConnectById("418 205 937")).Problem == PortalConnectProblem.NoDirectory, "a wrong ID, or no directory, is said plainly");
    var early = alice.Send(aliceOffice!, [Make(Path.Combine(temp, "CourierOut"), "early.txt", "too early")])!;
    Check(await Until(() => early.State == "failed") && !Directory.EnumerateFiles(officeSettings.Folder).Any(), "nothing can be sent before pairing");

    // Pairing: both sides show the same code; the PC asked confirms.
    async Task<PortalPeer> Join(PortalNode from, PortalNode to, System.Net.IPEndPoint toAt)
    {
        var found = from.PeerOf(to.Number) is { Online: true } known ? known : await from.ConnectDirect(toAt, to.Number);
        var asked = await from.Pair(found!);
        Check(asked is { Code.Length: 7 } && await Until(() => to.Pairings.Any(pairing => pairing.PeerId == from.Id && pairing.Code == asked.Code)),
              "a pairing reaches the other PC with the same code");
        to.Confirm(to.Pairings.Single(pairing => pairing.PeerId == from.Id));
        return found!;
    }
    var aliceToOffice = await Join(alice, office, officeAt);
    Check(office.Peers.Single(peer => peer.Id == alice.Id) is { Trusted: true, Online: true } && officeSettings.Devices.Count == 1 && officeSaved >= 1
          && aliceSettings.Devices is [{ }] && aliceSettings.Devices[0].Id == office.Id && officeAsked == 1, "the two PCs trust each other, and both save it");
    Check(PortalSavedList.Find(officeSettings, alice.Number) is { } aliceOnList && PortalSavedList.Find(aliceSettings, office.Number) is not null && !aliceOnList.Stay,
          "both PCs have the other on their list, whichever side asked");
    var bobToOffice = await Join(bob, office, officeAt);
    Check(officeSettings.Devices.Count == 2, "a PC has as many PCs connected as join it");

    // One way, and the other.
    var payload = new byte[20_000_000];
    Random.Shared.NextBytes(payload);
    var payloadFile = Path.Combine(temp, "CourierOut", "holiday video.mp4");
    File.WriteAllBytes(payloadFile, payload);
    Check(office.Peers.Single(peer => peer.Id == alice.Id).AutoAccept && aliceSettings.Devices[0].AutoAccept, "a paired PC's files are taken without a click");
    office.SetAutoAccept(alice.Id, false);
    var upward = alice.Send(aliceToOffice, [payloadFile])!;
    Check(await Until(() => office.Requests.Count == 1) && upward.State == "waiting" && office.Requests[0] is { Files: 1, Size: 20_000_000 }, "asking first, nothing is taken before Accept");
    office.Accept(office.Requests[0].Id);
    Check(await Until(() => !upward.Running, 60) && upward.State == "done" && File.ReadAllBytes(Path.Combine(officeSettings.Folder, "holiday video.mp4")).SequenceEqual(payload),
          $"a file of several pieces arrives whole ({upward.State} {upward.Note})");
    Check(await Until(() => officeGot.Count == 1) && officeGot[0].Name == "holiday video.mp4" && officeGot[0].Path == Path.Combine(officeSettings.Folder, "holiday video.mp4"),
          "the PC that received it is told, with where the file is");
    var album = Path.Combine(temp, "CourierOut", "Album");
    Make(Path.Combine(album, "inner", "deep"), "three.txt", "three");
    Make(album, "empty.txt", "");
    alice.SetAutoAccept(office.Id, true);
    var downward = office.Send(office.Peers.Single(peer => peer.Id == alice.Id), [album])!;
    Check(await Until(() => !downward.Running, 30) && downward.State == "done" && File.ReadAllText(Path.Combine(aliceSettings.Folder, "Album", "inner", "deep", "three.txt")) == "three"
          && new FileInfo(Path.Combine(aliceSettings.Folder, "Album", "empty.txt")).Length == 0, $"a folder goes back the other way, structure and all ({downward.State} {downward.Note})");

    // Pause and resume, from the sending PC and from the receiving one.
    var bigFilm = new byte[90_000_000];
    Random.Shared.NextBytes(bigFilm);
    var bigFile = Path.Combine(temp, "CourierOut", "bigFilm film.mkv");
    File.WriteAllBytes(bigFile, bigFilm);
    office.SetAutoAccept(alice.Id, true);
    var held = alice.Send(aliceToOffice, [bigFile])!;
    Check(await Until(() => held.Done > 0, 30), "a big file starts moving");
    held.Pause();
    await Task.Delay(1200);
    var (heldAt, heldFor) = (held.Done, held.Elapsed);
    var heldThere = office.Transfers.Last(transfer => transfer.Incoming);
    await Task.Delay(1200);
    Check(held is { Paused: true, State: "active" } && held.Done == heldAt && heldAt < bigFilm.Length && held.Elapsed == heldFor && held.Remaining is null,
          $"Pause holds a transfer where it is, and its clock too ({held.Done} of {bigFilm.Length})");
    Check(heldThere.PausedThere && heldThere.State == "active", "the receiving PC is told the sender paused");
    held.Resume();
    Check(await Until(() => !held.Running, 60) && held.State == "done" && File.ReadAllBytes(Path.Combine(officeSettings.Folder, "bigFilm film.mkv")).SequenceEqual(bigFilm)
          && held.Elapsed > heldFor, $"Resume carries on from there, and the file arrives whole ({held.State} {held.Note})");
    var heldBack = alice.Send(aliceToOffice, [bigFile])!;
    Check(await Until(() => office.Transfers.Last(transfer => transfer.Incoming) is var arriving && arriving != heldThere && arriving.Done > 0, 30), "another one starts");
    var arrivingHere = office.Transfers.Last(transfer => transfer.Incoming);
    arrivingHere.Pause();
    Check(await Until(() => heldBack.PausedThere, 20) && heldBack.State == "active", "paused on the receiving PC, the sender waits and says so");
    var arrivedSoFar = arrivingHere.Done;
    await Task.Delay(1000);
    Check(arrivingHere.Done == arrivedSoFar && arrivedSoFar < bigFilm.Length, "and nothing more arrives meanwhile");
    arrivingHere.Resume();
    Check(await Until(() => !heldBack.Running, 60) && heldBack.State == "done" && !heldBack.PausedThere
          && File.ReadAllBytes(Path.Combine(officeSettings.Folder, "bigFilm film (1).mkv")).SequenceEqual(bigFilm), $"and goes on when the receiver resumes ({heldBack.State} {heldBack.Note})");
    office.SetAutoAccept(alice.Id, false);

    // Two PCs that both connected to the office connect to each other straight away: there is no host in between.
    var aliceToBob = await Join(alice, bob, bobAt);
    bob.SetAutoAccept(alice.Id, false);
    var note = Make(Path.Combine(temp, "CourierOut", "Letters"), "for bob.txt", "hello Bob, from Alice");
    var across = alice.Send(aliceToBob, [note])!;
    Check(await Until(() => bob.Requests.Count == 1, 30) && bob.Requests[0].PeerName == alice.Name && office.Requests.Count == 0, "files go straight to the PC they are for");
    bob.Accept(bob.Requests[0].Id);
    Check(await Until(() => !across.Running, 30) && File.ReadAllText(Path.Combine(bobSettings.Folder, "for bob.txt")) == "hello Bob, from Alice"
          && across.State == "done" && !File.Exists(Path.Combine(officeSettings.Folder, "for bob.txt")), $"and arrive there, through nobody ({across.State} {across.Note})");

    // Kept connected: Connect marks a PC, Disconnect ends it on both sides and neither connects back by itself.
    alice.Connect(office.Number);
    Check(PortalSavedList.Find(aliceSettings, office.Number) is { Stay: true } && await Until(() => alice.StateOf(office.Number) == PortalState.Connected, 20),
          "a PC connected to stays connected");
    await alice.Disconnect(office.Number);
    Check(await Until(() => office.Peers.Single(peer => peer.Id == alice.Id) is { Online: false }, 6) && alice.StateOf(office.Number) == PortalState.NotConnected
          && office.StateOf(alice.Number) == PortalState.NotConnected, "Disconnect ends it on both PCs at once");
    await Task.Delay(3000);
    Check(alice.PeerOf(office.Number) is { Online: false } && office.Peers.Single(peer => peer.Id == alice.Id) is { Online: false, Trusted: true }
          && PortalSavedList.Find(aliceSettings, office.Number) is { Stay: false }, "and neither connects back by itself; they stay paired");
    alice.Connect(office.Number);
    Check(await Until(() => alice.StateOf(office.Number) == PortalState.Connected && office.Peers.Single(peer => peer.Id == alice.Id).Online, 20),
          "Connect again, and it is back without pairing again");

    // A PC that stops is seen gone at once by the PCs connected to it, not a minute later.
    await bob.Stop();
    Check(await Until(() => alice.PeerOf(bob.Number) is { Online: false } && office.Peers.Single(peer => peer.Id == bob.Id) is { Online: false }, 6),
          "a PC that stops Courier shows as disconnected on the others at once");

    // Kicked out: removed on one side, the other no longer trusted and off the list.
    await office.Disconnect(alice.Number, forget: true);
    Check(await Until(() => alice.PeerOf(office.Number) is { Trusted: false, Online: false }, 6) && PortalSavedList.Find(officeSettings, alice.Number) is null
          && !officeSettings.Devices.Any(device => device.Id == alice.Id) && PortalSavedList.Find(aliceSettings, office.Number) is { Stay: false },
          "Remove kicks a PC out: both stop trusting each other, and the one kicked keeps the other on its list to ask again");
    var afterKick = alice.Send(aliceToOffice, [note])!;
    Check(await Until(() => afterKick.State == "failed"), "a PC kicked out can send nothing");

    // A PC that was never paired.
    var strangerOffice = await stranger.ConnectDirect(officeAt, office.Number);
    var intruding = stranger.Send(strangerOffice!, [payloadFile])!;
    Check(await Until(() => intruding.State == "failed") && office.Requests.Count == 0, "a PC that was never paired can not even ask");

    // A PC that asks for a password: none, or a wrong one, is turned away; the right one lets the PC in without a click.
    office.UseIncoming(PortalSettings.WithPassword, Portal.HashPassword("open sesame"));
    var asksBefore = officeAsked;
    Check(await stranger.Pair(strangerOffice!) is null && stranger.StateOf(office.Number) == PortalState.NeedsPassword
          && !office.Peers.Any(peer => peer.Id == stranger.Id && peer.Trusted), "a PC with a password turns away a PC that gives none, and that PC is told a password is needed");
    stranger.GivePassword(office.Number, "open says me");
    Check(await stranger.Pair(strangerOffice!) is null && stranger.StateOf(office.Number) == PortalState.WrongPassword, "a wrong password is turned away, and said so");
    stranger.GivePassword(office.Number, "open sesame");
    var letIn = await stranger.Pair(strangerOffice!);
    Check(letIn is not null && stranger.StateOf(office.Number) == PortalState.Connected && office.Peers.Any(peer => peer.Id == stranger.Id && peer.Trusted) && officeAsked == asksBefore,
          "the right password lets the PC in at once, on both sides, without asking");
    var withPassword = stranger.Send(strangerOffice!, [note])!;
    Check(await Until(() => !withPassword.Running, 30) && withPassword.State == "done", $"and it can send at once ({withPassword.State} {withPassword.Note})");
    office.UseIncoming(PortalSettings.Ask, "");

    await office.Stop();
    await alice.Stop();
    await stranger.Stop();
    Check(office.Stopped, "Courier stops");
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
    var shortId = Portal.NumberOf(someId);
    Check(shortId.Length == 9 && shortId.All(char.IsAsciiDigit) && Portal.IsNumber(shortId) && shortId == Portal.NumberOf(someId.ToLowerInvariant())
          && Portal.NumberOf(new string('0', 64)) == "000000000" && Portal.DirectoryKey(shortId, false).Length == 20
          && !Portal.DirectoryKey(shortId, false).SequenceEqual(Portal.DirectoryKey(shortId, true)), $"a PC's ID is nine digits made from its certificate ({shortId})");
    Check(Portal.ShowNumber("418205937") == "418 205 937" && Portal.NormalizeNumber(" 418-205 937 ") == "418205937" && Portal.NormalizeNumber("K7M-2QX") == "72"
          && !Portal.IsNumber("41820593") && !Portal.IsNumber("4182059370") && !Portal.IsNumber("K7M2QX123") && Portal.NormalizeNumber("") == "",
          "an ID is digits only, read however it is typed");

    // ------------------------------------------------------------ tune-up: lasting changes that can all be put back
    Section("tune-up");
    var tuneRoot = $@"Software\PCButlerTests\{Guid.NewGuid():N}";
    var tuneState = Path.Combine(temp, "tuneup-test.json");
    using (var tuneKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(tuneRoot))
    {
        tuneKey.SetValue("Glass", 1, Microsoft.Win32.RegistryValueKind.DWord);
        tuneKey.SetValue("AlreadyOff", 0, Microsoft.Win32.RegistryValueKind.DWord);
        Tweak[] testTweaks =
        [
            new("glass", 1, false, "Glass", "", [new RegistryStep(false, tuneRoot, "Glass", 0), new RegistryStep(false, tuneRoot, "Fresh", 7)]),
            new("nothing", 1, false, "Nothing", "", [new RegistryStep(false, tuneRoot, "AlreadyOff", 0)]),
            new("deep", 2, false, "Deep", "", [new RegistryStep(false, tuneRoot + @"\Deeper", "Stamp", "{later}")]),
            new("service", 2, true, "Service", "", [new ServiceStep("Pretend")]),
            new("other", 0, true, "Other", "", [new ServiceStep("Elsewhere")], "update"),
        ];
        var helperAsked = new List<string>();
        var helperThere = false;
        List<string>? PretendHelper(string action, List<string> items)
        {
            if (!helperThere)
                return null;
            helperAsked.Add(action + " " + string.Join(",", items));
            return action == "tune_apply" ? items.Select(item => $"{item}#0=2:0:1").ToList() : items;
        }
        var tuned = TuneUp.Apply(["glass", "nothing", "deep", "service", "no-such-tweak"], "medium", testTweaks, tuneState, PretendHelper);
        Check(tuned.Changed == 2 && tuned.NeedsHelper.SequenceEqual(["service"]) && tuneKey.GetValue("Glass") is 0 && tuneKey.GetValue("Fresh") is 7
              && tuneKey.OpenSubKey("Deeper")?.GetValue("Stamp") is string stamp && stamp.EndsWith('Z') && stamp.Length == 20,
              "a preset changes what it lists, and leaves what needs the helper when the helper is not there");
        var written = TuneUp.Load(tuneState);
        Check(written.Level == "medium" && written.Applied["glass"].SequenceEqual(["d:1", "-"]) && !written.Applied.ContainsKey("nothing") && !written.Applied.ContainsKey("service")
              && TuneUp.IsApplied("deep", tuneState), "what was there before is written down, and a value already as wanted is not counted as ours");
        helperThere = true;
        Check(TuneUp.Apply(["other"], null, testTweaks, tuneState, PretendHelper).Changed == 1 && TuneUp.Load(tuneState) is { Level: "medium" } both
              && both.Applied.ContainsKey("glass") && both.Applied["other"].SequenceEqual(["2:0:1"]), "a switch outside the tune-up is added without disturbing it");
        tuneKey.SetValue("Fresh", 99, Microsoft.Win32.RegistryValueKind.DWord); // changed by the user since
        var retuned = TuneUp.Apply(["glass", "service"], "custom", testTweaks, tuneState, PretendHelper);
        Check(retuned is { Changed: 2, NeedsHelper.Count: 0 } && tuneKey.OpenSubKey("Deeper")?.GetValue("Stamp") is null && tuneKey.GetValue("Fresh") is 7
              && TuneUp.Load(tuneState) is { Level: "custom" } custom && custom.Applied.Keys.Order().SequenceEqual(["glass", "other", "service"])
              && custom.Applied["glass"].SequenceEqual(["d:1", "d:99"]) && helperAsked.Contains("tune_apply service"),
              "choosing again puts the old choice back first, so only the new one is in force");
        helperThere = false;
        var half = TuneUp.Undo(null, testTweaks, tuneState, PretendHelper);
        Check(half.Changed == 1 && half.NeedsHelper.SequenceEqual(["service"]) && tuneKey.GetValue("Glass") is 1 && tuneKey.GetValue("Fresh") is 99
              && TuneUp.Load(tuneState) is { Level: "custom" } leftOver && leftOver.Applied.ContainsKey("service"), "Undo puts values back, and keeps what it could not undo for the next try");
        helperThere = true;
        Check(TuneUp.Undo(null, testTweaks, tuneState, PretendHelper) is { Changed: 1, NeedsHelper.Count: 0 } && helperAsked.Contains("tune_restore service#0=2:0:1")
              && TuneUp.Load(tuneState) is { Level: "" } after && after.Applied.Keys.SequenceEqual(["other"]), "with the helper, the rest goes back too; the other switch stays");
        Check(TuneUp.Undo(tweak => tweak.Key == "other", testTweaks, tuneState, PretendHelper).Changed == 1 && TuneUp.Load(tuneState).Applied.Count == 0, "and can be undone by itself");
    }
    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(tuneRoot, throwOnMissingSubKey: false);
    Check(TuneUp.ForLevel("low").Count >= 3 && TuneUp.Levels.Zip(TuneUp.Levels.Skip(1)).All(pair => TuneUp.ForLevel(pair.First).All(TuneUp.ForLevel(pair.Second).Contains)
              && TuneUp.ForLevel(pair.First).Count < TuneUp.ForLevel(pair.Second).Count)
          && TuneUp.ForLevel("extreme").Count == TuneUp.Catalogue.Count(tweak => tweak.Group == "tune") && !TuneUp.ForLevel("extreme").Contains(TuneUp.WindowsUpdate)
          && TuneUp.ForLevel("low").All(key => !TuneUp.Catalogue.First(tweak => tweak.Key == key).Admin)
          && TuneUp.Catalogue.Select(tweak => tweak.Key).Distinct().Count() == TuneUp.Catalogue.Length
          && TuneUp.Catalogue.All(tweak => tweak.Steps.Length > 0 && tweak.Admin == tweak.Steps.Any(step => step is ServiceStep or RegistryStep { Machine: true })),
          "each preset holds the one below it and more; Low needs no administrator rights; machine-wide changes are all marked for the helper");
    var helperDid = new List<string>();
    var applied = TuneUp.HelperApply(["sysmain", "game_mode", "windows_update", "evil", "sysmain"], step => { helperDid.Add(step.ToString()!); return step is ServiceStep ? "2:1:1" : "-"; });
    Check(applied.Count == 9 && applied[0] == "sysmain#0=2:1:1" && helperDid.Count == 9 && helperDid.All(did => !did.Contains("GameBar")),
          "the helper applies administrator tweaks from the catalogue only, each once");
    var restored = TuneUp.HelperRestore(
        ["sysmain#0=2:1:1", "sysmain#0=4:0:0", "sysmain#5=2:0:0", "game_mode#0=d:0", "scheduler#0=d:20", "scheduler#0=s:text", "scheduler#1=-", "evil#0=-",
         "windows_update#1=s:2026-01-01T00:00:00Z", "windows_update#1=s:calc.exe", "windows_update#0=d:35", @"sysmain#0=..\..\x"],
        (step, before) => { helperDid.Add("back " + before); return true; });
    Check(restored.SequenceEqual(["sysmain#0=2:1:1", "scheduler#0=d:20", "scheduler#1=-", "windows_update#1=s:2026-01-01T00:00:00Z", "windows_update#0=d:35"]),
          $"the helper puts back only values of the right kind, in places the catalogue names ({string.Join(" ", restored)})");
    Check(TuneUp.ServiceStart("RpcSs").Start == 2 && TuneUp.ServiceStart("no-such-service").Start == -1, "a service's start type can be read");
    string? renamedTo = null;
    foreach (var wanted in new[] { "Bad Name!", "12345", "-edge", "this-name-is-too-long", "Study-PC" })
    {
        File.WriteAllText(Path.Combine(helperDir, "helper-command.json"), System.Text.Json.JsonSerializer.Serialize(new HelperMessage { Id = "n", Action = "rename_pc", Services = [wanted] }));
        AdminHelper.Poll(helperDir, [], rename: name => { renamedTo = (renamedTo ?? "") + name; return true; });
    }
    Check(renamedTo == "Study-PC" && PcName.Valid("A") && PcName.Valid("PC-01") && !PcName.Valid("") && PcName.Pending.Length > 0, "a PC is renamed only to a name Windows allows");
    Check(System.Net.IPAddress.TryParse(NetworkAddresses.Local(), out var localIp) || NetworkAddresses.Local() == "", $"the local address is read ({localIp})");

    // ------------------------------------------------------------ benchmark: what a game is, frame counts, sessions
    Section("benchmark");
    ForegroundApp Front(string name, bool fullScreen, string title = "A window") => new(4242, name, title, fullScreen, 0, 0, 1920, 1080);
    Check(GameDetector.LooksLikeGame(Front("eldenring.exe", true), []) && !GameDetector.LooksLikeGame(Front("eldenring.exe", false), [])
          && GameDetector.LooksLikeGame(Front("emulator.exe", false), ["notes.exe", " Emulator.exe "]), "a game fills the screen, or is on the user's list");
    Check(!GameDetector.LooksLikeGame(Front("chrome.exe", true), []) && !GameDetector.LooksLikeGame(Front("explorer.exe", true), [])
          && !GameDetector.LooksLikeGame(Front("vlc.exe", true), []) && !GameDetector.LooksLikeGame(Front("mystery.exe", true, ""), [])
          && GameDetector.LooksLikeGame(Front("chrome.exe", true), ["chrome.exe"]), "a browser, a player or the desktop in full screen is not a game, unless listed");
    Check(GameDetector.Foreground() is null or { Name.Length: > 4, Right: > 0 }, "the window in front can be read");
    Check(FpsMeter.Choose(144, 300) == 144 && FpsMeter.Choose(0, 60) == 60 && FpsMeter.Choose(0, 0) == 0, "a program's own frame count is used before the graphics kernel's");
    var fpsDir = Path.Combine(temp, "fps");
    Directory.CreateDirectory(fpsDir);
    Check(FpsMeter.Latest(fpsDir) is null, "no frame counts while nobody is counting");
    var countingStarted = FpsMeter.Start(fpsDir);
    FpsMeter.Stop();
    Check(true, $"asking Windows to count frames without administrator rights is refused quietly, not with a crash (started: {countingStarted})");
    File.WriteAllText(FpsMeter.FileIn(fpsDir), System.Text.Json.JsonSerializer.Serialize(new FpsMeter.FpsReading { Time = DateTime.UtcNow, Fps = { [4242] = 143, [77] = 60 } }));
    Check(FpsMeter.Latest(fpsDir) is { Count: 2 } counts && counts[4242] == 143, "the helper's counts are read by program");
    File.WriteAllText(FpsMeter.FileIn(fpsDir), System.Text.Json.JsonSerializer.Serialize(new FpsMeter.FpsReading { Time = DateTime.UtcNow.AddSeconds(-30), Fps = { [4242] = 143 } }));
    Check(FpsMeter.Latest(fpsDir) is null, "counts the helper stopped refreshing are not shown as if they were live");
    File.WriteAllText(Path.Combine(helperDir, "helper-command.json"), """{"id":"f1","action":"fps","services":["off"]}""");
    AdminHelper.Poll(helperDir, []);
    Check(File.ReadAllText(Path.Combine(helperDir, "helper-result.json")).Contains("\"off\""), "the helper takes the request to stop counting frames");
    File.WriteAllText(Path.Combine(helperDir, "helper-command.json"), """{"id":"f2","action":"fps","services":["C:\\evil.etl"]}""");
    AdminHelper.Poll(helperDir, []);
    Check(File.ReadAllText(Path.Combine(helperDir, "helper-result.json")).Contains("\"services\":[]"), "and nothing but on or off");
    Check(CpuSensor.Choose([("CPU Core #1", 61f), ("CPU Package", 64.26f), ("CPU Core #1 Distance to TjMax", 39f), ("Core Max", 66f)]) == 64.3
          && CpuSensor.Choose([("CCD1 (Tdie)", 50f), ("Core (Tctl/Tdie)", 58.5f)]) == 58.5 && CpuSensor.Choose([("CPU Core #1", 61f), ("CPU Core #2", 67f)]) == 67
          && CpuSensor.Choose([("CPU Package", null), ("CPU Core #1 Distance to TjMax", 39f)]) is null && CpuSensor.Choose([]) is null,
          "the processor's temperature is its package (Intel) or Tctl/Tdie (AMD), else its hottest core, and never a distance to the limit");
    Check(CpuSensor.DriverInstalled || CpuSensor.Read() is null, "without the driver no processor temperature is claimed");
    Check(NetworkMeter.Megabits(125_000) == "1.00" && NetworkMeter.Megabits(2_500_000) == "20.0" && NetworkMeter.Megabits(50_000_000) == "400",
          "network speed is given in megabits a second");
    var meter = new NetworkMeter();
    var firstSpeed = meter.Read();
    Thread.Sleep(300);
    Check(firstSpeed is null && meter.Read() is null or { Down: >= 0, Up: >= 0 }, "network speed needs two readings");
    Check(UsageMonitor.VideoMemory() is var (videoUsed, videoTotal) && videoUsed is null or >= 0 && (videoTotal is null || videoTotal > 0),
          $"the graphics card's memory can be read ({UsageMonitor.VideoMemory()})");
    var arranged = new OverlaySettings { Order = ["clock", "nonsense", "fps", "clock"] };
    Check(arranged.OrderedRows() is { Count: 9 } inOrder && inOrder[0] == "clock" && inOrder[1] == "fps" && inOrder[2] == "frame_time" && inOrder.Distinct().Count() == 9
          && new OverlaySettings().OrderedRows().SequenceEqual(OverlaySettings.Rows), "the overlay's readings come in the user's order, the rest after them, each once");
    arranged.SetShown("clock", true);
    arranged.SetShown("fps", false);
    Check(arranged.Shows("clock") && !arranged.Shows("fps") && arranged.Shows("cpu") && !arranged.Shows("nonsense") && OverlaySettings.Rows.All(row =>
          {
              var probe = new OverlaySettings();
              probe.SetShown(row, !probe.Shows(row));
              return probe.Shows(row) != new OverlaySettings().Shows(row);
          }), "every reading can be switched on and off by its name");
    Check(OverlaySettings.IsColour("#3DD68C") && OverlaySettings.IsColour("#ffffff") && !OverlaySettings.IsColour("3DD68C") && !OverlaySettings.IsColour("#3DD68")
          && !OverlaySettings.IsColour("#GGGGGG") && !OverlaySettings.IsColour("red"), "a colour is #RRGGBB and nothing else");
    (arranged.Accent, arranged.Scale, arranged.Horizontal, arranged.OffsetX, arranged.Games) = ("#FF0000", 200, true, 400, ["game.exe"]);
    arranged.ResetLook();
    Check(arranged is { Accent: OverlaySettings.DefaultAccent, Scale: 100, Horizontal: false, OffsetX: 16, Order.Count: 0, Games.Count: 1 } && arranged.Shows("clock"),
          "Reset the look puts colours, size, place and order back, and leaves what is shown alone");
    var recorder = new BenchmarkRecorder("eldenring.exe");
    for (var tick = 0; tick < 5; tick++)
        recorder.Add(new BenchmarkSample(60, 40, 90, 50, 70, 65));
    Check(recorder.Finish() is null, "a few seconds in a game are not a session");
    for (var tick = 0; tick < 195; tick++)
        recorder.Add(new BenchmarkSample(tick < 2 ? 20 : tick % 2 == 0 ? 100 : 140, tick % 2 == 0 ? 30 : 50, null, 60, tick == 9 ? 88 : 70, null));
    Check(recorder.Finish() is { Game: "eldenring.exe", Seconds: 200, FpsLow: 20, FpsHigh: 140, CpuAverage: 40, GpuAverage: 90, CpuHottest: 88, GpuHottest: 65 } played
          && played.FpsAverage is > 100 and < 125, "a session keeps the average, the slowest 1%, the highest, and the hottest readings");
    var noFps = new BenchmarkRecorder("quiet.exe");
    for (var tick = 0; tick < 12; tick++)
        noFps.Add(new BenchmarkSample(null, 10, null, 30, null, null));
    Check(noFps.Finish() is { FpsAverage: null, FpsLow: null, GpuAverage: null, CpuHottest: null, CpuAverage: 10 }, "without the helper a session simply has no frame rate");
    BenchmarkLog.Clear();
    for (var number = 0; number < 45; number++)
        BenchmarkLog.Add(new BenchmarkSession { Game = $"game{number}.exe", Seconds = 60 });
    Check(BenchmarkLog.All() is { Count: 40 } kept && kept[0].Game == "game44.exe", "the log keeps the newest sessions, newest first");
    BenchmarkLog.Clear();
    Check(BenchmarkLog.All().Count == 0 && new Config().Overlay is { Enabled: false, Fps: true } && new Config().Hotkeys.Count == 0, "the overlay and every hotkey are off until switched on");

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
ClipboardHistory.Clear(keepPinned: false);
ClipboardHistory.Add(new ClipEntry { Text = "Riyadh, King Fahd Road 12" }, 50);
ClipboardHistory.Add(new ClipEntry { Text = "meeting at nine" }, 50);
var address = ClipboardHistory.Entries.Single(entry => entry.Text.StartsWith("Riyadh"));
ClipboardHistory.SetTag(address, "  #home  address ", true);
ClipboardHistory.SetTag(address, "work", true);
ClipboardHistory.SetTag(ClipboardHistory.Entries.Single(entry => entry.Text.StartsWith("meeting")), "work", true);
Check(address.Tags.SequenceEqual(["home address", "work"]) && ClipboardHistory.AllTags().First() == "work"
      && ClipboardHistory.Ordered("#work").Count == 2 && ClipboardHistory.Ordered("#home address").Single() == address
      && ClipboardHistory.Ordered("#hom").Count == 0 && ClipboardHistory.Ordered("home").Single() == address, "items are tagged, and found by their tags");
ClipboardHistory.SetTag(address, "WORK", false);
Check(address.Tags.SequenceEqual(["home address"]) && ClipboardHistory.Ordered("#work").Count == 1, "a tag is taken away whatever its case");
ClipboardHistory.Clear(keepPinned: false);
Check(WebSearch.Google("  what   is" + Environment.NewLine + "PC Butler ") == "https://www.google.com/search?q=what%20is%20PC%20Butler"
      && WebSearch.IsPicture(Path.Combine(temp, "b.JPG")) && !WebSearch.IsPicture("notes.txt"), "a text is searched on Google as one line; pictures are told apart");
Check(new ClipEntry { Kind = "files", Files = [Path.Combine(temp, "a.txt"), Path.Combine(temp, "b.txt")] }.Preview == "a.txt (+1)"
      && new ClipEntry { Text = "one" + Environment.NewLine + "two" }.Preview == "one two", "list previews");

// an item with a password of its own
ClipboardHistory.Apply(new ClipboardSettings { KeepOnDisk = true });
ClipboardHistory.Add(new ClipEntry { Text = "IBAN SA03 8000 0000 6080 1016 7519" }, 50);
var lockedClip = ClipboardHistory.Entries[0];
ClipboardHistory.SetTag(lockedClip, "bank", true);
Check(!ClipboardHistory.Lock(lockedClip, "") && ClipboardHistory.Lock(lockedClip, "s3cret!") && lockedClip is { Hidden: true, Text: "", Locked: true }
      && lockedClip.Preview != "" && !lockedClip.Preview.Contains("IBAN"), "a locked item shows nothing of what it holds");
var onDisk = File.ReadAllText(Path.Combine(ClipboardHistory.Folder, "history.json"));
Check(!onDisk.Contains("IBAN") && !onDisk.Contains("6080") && onDisk.Contains("aesgcm"), "and nothing of it is on the disk, only its encrypted form");
Check(ClipboardHistory.Ordered("IBAN").Count == 0 && ClipboardHistory.Ordered("#bank").Single() == lockedClip, "a search finds it by its tags only");
Check(ClipboardHistory.Peek(lockedClip, "wrong") is null && ClipboardHistory.Peek(lockedClip, "s3cret!")?.Text == "IBAN SA03 8000 0000 6080 1016 7519" && lockedClip.Hidden,
      "the right password gives its content for one paste, and it stays locked");
Check(!ClipboardHistory.Unlock(lockedClip, "wrong") && lockedClip.Hidden && ClipboardHistory.Unlock(lockedClip, "s3cret!") && lockedClip is { Open: true, Hidden: false }
      && lockedClip.Text.StartsWith("IBAN"), "unlocked, it can be used again and again");
ClipboardHistory.Save();
Check(!File.ReadAllText(Path.Combine(ClipboardHistory.Folder, "history.json")).Contains("IBAN"), "even unlocked, what it holds is never written out");
ClipboardHistory.Relock(lockedClip);
Check(lockedClip is { Hidden: true, Text: "" }, "locked again, the password is asked again");
ClipboardHistory.Add(new ClipEntry { Text = "IBAN SA03 8000 0000 6080 1016 7519" }, 50);
Check(ClipboardHistory.Entries.Count(entry => entry.Locked) == 1 && ClipboardHistory.Entries.Count == 2, "copying the same text again does not open the locked one");
Check(!ClipboardHistory.RemoveLock(lockedClip, "nope") && ClipboardHistory.RemoveLock(lockedClip, "s3cret!") && lockedClip is { Locked: false, Hidden: false }
      && lockedClip.Text.StartsWith("IBAN") && File.ReadAllText(Path.Combine(ClipboardHistory.Folder, "history.json")).Contains("IBAN"),
      "with its password taken off, it is an ordinary item again");
ClipboardHistory.Clear(keepPinned: false);
ClipboardHistory.Apply(new ClipboardSettings { KeepOnDisk = false });

Section("app usage");
{
    AppUsageLog.Clear();
    var usageMorning = DateTime.Today.AddHours(9);
    AppUsageLog.Record(usageMorning, new Dictionary<string, AppUse>
    {
        ["chrome"] = new() { Seconds = 5, CpuSeconds = 2, GpuSeconds = 1, Read = 1000, Written = 10, MemoryPeak = 400 << 20, MemorySeconds = 5.0 * (400 << 20) },
        ["game"] = new() { Seconds = 5, CpuSeconds = 10, GpuSeconds = 5, MemoryPeak = 2L << 30, MemorySeconds = 5.0 * (2L << 30), Sent = 300, Received = 9000, NetSeconds = 5 },
    });
    AppUsageLog.Record(usageMorning.AddSeconds(5), new Dictionary<string, AppUse> { ["Chrome"] = new() { Seconds = 5, CpuSeconds = 1, Read = 500, MemoryPeak = 200 << 20, MemorySeconds = 5.0 * (200 << 20) } });
    AppUsageLog.Record(usageMorning.AddDays(-3), new Dictionary<string, AppUse> { ["chrome"] = new() { Seconds = 100, CpuSeconds = 50 } });
    var today = AppUsageLog.Report(DateTime.Today, DateTime.Today);
    var chrome = today.Single(row => row.App.Equals("chrome", StringComparison.OrdinalIgnoreCase)).Use;
    Check(today.Count == 2 && today[0].App == "game" && chrome is { Seconds: 10, CpuSeconds: 3, Read: 1500, MemoryPeak: 400 << 20 } && chrome.AverageMemory == 300 << 20,
          "what each app uses adds up per day, by app, with its peak and average memory");
    Check(Math.Abs(chrome.CpuPercent(2) - 15) < 0.01 && Math.Abs(today[0].Use.GpuPercent - 100) < 0.01 && chrome.CpuPercent(1) <= 100,
          "and its share of the processor and the graphics card while it ran");
    var week = AppUsageLog.Report(DateTime.Today.AddDays(-6), DateTime.Today);
    Check(week.Single(row => row.App.Equals("chrome", StringComparison.OrdinalIgnoreCase)).Use.CpuSeconds == 53 && week[0].App.Equals("chrome", StringComparison.OrdinalIgnoreCase),
          "a longer period adds the days up");
    AppUsageLog.Save();
    AppUsageLog.Reload();
    Check(AppUsageLog.Report(DateTime.Today, DateTime.Today).Count == 2 && AppUsageLog.FirstDay == DateTime.Today.AddDays(-3), "the record is kept on disk");
    var csv = AppUsageLog.Csv([new AppUseRow("a, \"quoted\" app", today[0].Use)], 4).Split("\r\n");
    Check(csv[0].StartsWith("App,Time running") && csv[1].StartsWith("\"a, \"\"quoted\"\" app\",") && csv[1].Contains(",2048,") && csv.Length == 3,
          "the report is a spreadsheet, names quoted where they must be");
    AppUsageLog.Clear();
    Check(AppUsageLog.FirstDay is null && AppUsageLog.Report(DateTime.Today.AddDays(-90), DateTime.Today).Count == 0, "and it can be cleared");
}

Section("courier saved PCs and passwords");
{
    var kept = Portal.HashPassword("correct horse");
    Check(Portal.CheckPassword("correct horse", kept) && !Portal.CheckPassword("Correct horse", kept) && !Portal.CheckPassword("", kept)
          && !Portal.CheckPassword("correct horse", "") && !kept.Contains("correct") && kept != Portal.HashPassword("correct horse"),
          "a password is kept salted and stretched, and only the same one matches");
    var someHost = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("a host")));
    var saved = new PortalSettings { Role = "client", HostNumber = "111222333",
                                     Devices = [new PortalDevice { Id = someHost, Name = "Old host" }, new PortalDevice { Id = "AA", Name = "Broken" }] };
    PortalSavedList.Start(saved);
    Check(PortalSavedList.Find(saved, "111222333") is { Stay: true } && PortalSavedList.Find(saved, Portal.NumberOf(someHost)) is { Name: "Old host", Stay: false }
          && saved.Saved.Count == 2 && saved.Role == "" && saved.HostNumber == "" && saved.Used,
          "a PC of before brings its host (kept connected) and its paired PCs onto the list");
    var oldHost = new PortalSettings { Role = "host", Devices = [new PortalDevice { Id = someHost, Name = "Laptop" }] };
    PortalSavedList.Start(oldHost);
    Check(oldHost.Saved is [{ Name: "Laptop", Stay: false }] && oldHost.Used, "a host of before has its clients on the list");
    Check(PortalSavedList.Add(saved, "444 555 666", "Office") && !PortalSavedList.Add(saved, "12", "") && saved.Saved.Count == 3
          && PortalSavedList.Add(saved, "444555666", "") && PortalSavedList.Find(saved, "444555666")!.Name == "Office", "PCs are added by their ID, once each");
    PortalSavedList.Rename(saved, "444555666", "  Work PC ");
    Check(PortalSavedList.Find(saved, "444555666")!.Name == "Work PC" && PortalSavedList.Remember(saved, "444555666", "DESKTOP-1") == "Work PC"
          && PortalSavedList.Remember(saved, "777888999", "Laptop") == "Laptop" && saved.Saved.Count == 4, "a name given stays; a PC connected to is added under its own");
    var oldFavourite = new PortalSettings { Saved = [new PortalSaved { Number = "123456789", Favorite = true }] };
    PortalSavedList.Start(oldFavourite);
    Check(oldFavourite.Saved is [{ Stay: true, Favorite: false }], "the favourite of before stays connected");
    PortalSavedList.Remove(saved, Portal.NumberOf(someHost));
    Check(PortalSavedList.Find(saved, Portal.NumberOf(someHost)) is null && !saved.Devices.Any(device => device.Id == someHost), "removed: off the list and no longer trusted");
}

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
