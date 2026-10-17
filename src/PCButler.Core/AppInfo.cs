// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core;

/// <summary>Names, version and file locations shared by the whole program.</summary>
public static class AppInfo
{
    public const string Name = "PC Butler";
    /// <summary>Must match the tag of the GitHub release this is published as (v1.22.16).</summary>
    public const string Version = "1.22.16";
    /// <summary>Where updates are looked for.</summary>
    public const string Repo = "hmodymac8-droid/PC-Butler";
    public const string ExeName = "PCButler.exe";
    public const string Copyright = "© 2026 hmodymac8-droid";
    public const string DonateUrl = "https://paypal.me/mfsafadi";

    /// <summary>
    /// Settings live outside the program folder so reinstalling keeps them.
    /// PCBUTLER_DATA overrides the location (used by the tests).
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCButler");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string LogFile => Path.Combine(DataDir, "pcbutler.log");
    /// <summary>Written by the installer after a silent update.</summary>
    public static string UpdateMarker => Path.Combine(DataDir, "updated.txt");

    /// <summary>The data folder of Downloads Sorter, the Python program this one replaces.</summary>
    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadsSorter");

    /// <summary>True for an installed copy (the setup puts its uninstaller beside the program).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// First run after moving from Downloads Sorter: bring its settings, history and vault
    /// along. They are copied, never moved, so the old program keeps working.
    /// </summary>
    public static void ImportLegacyData()
    {
        try
        {
            // The settings file, not the folder: the installer may have made the folder already.
            if (File.Exists(ConfigFile) || !Directory.Exists(LegacyDataDir))
                return;
            Directory.CreateDirectory(DataDir);
            foreach (var name in new[] { "config.json", "history.json" })
            {
                string source = Path.Combine(LegacyDataDir, name), target = Path.Combine(DataDir, name);
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
            var vault = Path.Combine(LegacyDataDir, "Vault");
            if (Directory.Exists(vault))
            {
                var target = Directory.CreateDirectory(Path.Combine(DataDir, "Vault")).FullName;
                foreach (var file in Directory.GetFiles(vault))
                    if (!File.Exists(Path.Combine(target, Path.GetFileName(file))))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not import Downloads Sorter data: {error.Message}");
        }
    }
}

/// <summary>
/// A plain text log in the data folder. It never grows past about a megabyte: then it
/// becomes pcbutler.old.log (replacing the one before) and a new one starts.
/// </summary>
public static class Log
{
    public const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastSaid = [];
    private static long size = -1;

    public static string OldFile => Path.ChangeExtension(AppInfo.LogFile, ".old.log");

    public static void Info(string message) => Write(message);
    public static void Warning(string message) => Write("WARNING " + message);
    public static void Error(string message) => Write("ERROR " + message);

    /// <summary>Like Info, but a message under the same key is written at most once in "every", so what repeats doesn't fill the log.</summary>
    public static void Seldom(string key, TimeSpan every, string message)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastSaid.TryGetValue(key, out var said) && now - said < every)
                return;
            if (LastSaid.Count > 500)
                LastSaid.Clear();
            LastSaid[key] = now;
        }
        Write(message);
    }

    private static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {message}{Environment.NewLine}";
                if (size < 0)
                    size = File.Exists(AppInfo.LogFile) ? new FileInfo(AppInfo.LogFile).Length : 0;
                if (size + line.Length > MaxBytes)
                {
                    if (File.Exists(AppInfo.LogFile))
                        File.Move(AppInfo.LogFile, OldFile, overwrite: true);
                    size = 0;
                }
                File.AppendAllText(AppInfo.LogFile, line);
                size += System.Text.Encoding.UTF8.GetByteCount(line);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the program.
        }
    }
}
