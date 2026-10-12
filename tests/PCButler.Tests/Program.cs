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
Check(Cleaner.Categories.Count == 6 && !Cleaner.ByKey("update_cache").Checked && Cleaner.ByKey("user_temp").Checked, "categories");
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
