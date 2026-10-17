// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCButler.Core;

/// <summary>One thing a tweak changes.</summary>
public abstract record TweakStep;

/// <summary>
/// A registry value. Value is a number or text; the text "{now}" and "{later}" stand for
/// the moment it is applied and a day ten years on. Notify names what Windows is told
/// changed, for values it only reads again when told.
/// </summary>
public sealed record RegistryStep(bool Machine, string Path, string Name, object Value, string Notify = "") : TweakStep;

/// <summary>A Windows service that is stopped and kept from starting.</summary>
public sealed record ServiceStep(string Service) : TweakStep;

/// <summary>The fastest power plan the PC has.</summary>
public sealed record PowerPlanStep : TweakStep;

/// <summary>Window and control animations.</summary>
public sealed record AnimationStep : TweakStep;

/// <summary>
/// A change to Windows that stays until it is undone. Level is the lowest preset that
/// includes it (1 low … 4 extreme; 0 = in no preset, only chosen by hand). Admin tweaks are carried out by the
/// administrator helper.
/// </summary>
public sealed record Tweak(string Key, int Level, bool Admin, string Title, string Detail, TweakStep[] Steps, string Group = "tune");

/// <summary>What has been changed, kept in tuneup.json: for each tweak, what every step found before it.</summary>
public sealed class TuneState
{
    /// <summary>"low", "medium", "high", "extreme" or "custom"; "" when nothing is applied.</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("when")] public string When { get; set; } = "";
    /// <summary>Tweak key -> one entry per step: what was there before, or "" when the step changed nothing.</summary>
    [JsonPropertyName("applied")] public Dictionary<string, List<string>> Applied { get; set; } = [];
}

/// <summary>What Apply or Undo did. NeedsHelper lists the tweaks left for the administrator helper, which was not running.</summary>
public sealed record TuneResult(int Changed, List<string> NeedsHelper);

/// <summary>
/// Tune-up: lasting changes to Windows that make the whole PC lighter, in presets from low
/// to extreme. Before a value is changed, what it was is written to tuneup.json, and Undo
/// puts every one back. Changes to the user's own settings are made here; the ones that
/// need administrator rights (services, machine-wide values) are made by the helper.
/// </summary>
public static partial class TuneUp
{
    public static readonly string[] Levels = ["low", "medium", "high", "extreme"];

    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion";
    private const string UpdateSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    public const string WindowsUpdate = "windows_update";

    public static readonly Tweak[] Catalogue =
    [
        new("game_mode", 1, false, "Game Mode on", "Windows gives a game in the foreground the processor and the graphics card first.",
            [new RegistryStep(false, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), new RegistryStep(false, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1)]),
        new("game_recording", 1, false, "No background game recording", "The Xbox Game Bar stops recording games in the background. Recording by hand still works.",
            [new RegistryStep(false, @"System\GameConfigStore", "GameDVR_Enabled", 0), new RegistryStep(false, Explorer + @"\GameDVR", "AppCaptureEnabled", 0)]),
        new("startup_delay", 1, false, "No wait before startup apps", "Windows waits about ten seconds after sign-in before it opens startup apps. This removes the wait.",
            [new RegistryStep(false, Explorer + @"\Explorer\Serialize", "StartupDelayInMSec", 0)]),
        new("tips", 1, false, "No tips, suggestions or promoted apps", "Windows stops showing tips and suggestions and stops installing promoted apps by itself.",
            [
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0),
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SoftLandingEnabled", 0),
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0),
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
                new RegistryStep(false, Explorer + @"\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0),
            ]),
        new("power_plan", 2, false, "Fastest power plan", "The processor stays at full speed. Uses more power; on a laptop the battery runs down sooner.", [new PowerPlanStep()]),
        new("transparency", 2, false, "No transparency effects", "The taskbar, Start and windows are drawn solid, which is less work for the graphics card.",
            [new RegistryStep(false, Explorer + @"\Themes\Personalize", "EnableTransparency", 0, "ImmersiveColorSet")]),
        new("telemetry", 2, true, "Telemetry services off", "Stops the two services that collect and send usage data to Microsoft.",
            [new ServiceStep("DiagTrack"), new ServiceStep("dmwappushservice")]),
        new("animations", 3, false, "No window animations", "Windows open, close and minimise at once, without the animation.", [new AnimationStep()]),
        new("sysmain", 3, true, "SysMain off", "Windows stops loading programs into memory ahead of time: less disk and memory work in the background, "
                                              + "but programs may open a little slower the first time.", [new ServiceStep("SysMain")]),
        new("scheduler", 3, true, "More processor time for the foreground", "Background tasks are held back less often for games and media, network "
                                                                            + "throttling during media playback is lifted, and Windows stops slowing down background programs to save power.",
            [
                new RegistryStep(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10),
                new RegistryStep(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1),
                new RegistryStep(true, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1),
            ]),
        new("indexing", 0, true, "Search indexing off", "Windows stops reading your files in the background to index them. Searching for files, and in "
                                                        + "Start, becomes slower, and the Photos app can fail to open pictures after a restart.", [new ServiceStep("WSearch")]),
        new("rare_services", 4, true, "Rarely used services off", "Error reporting, offline maps, fax and the shop-demo service. Crashes are no longer "
                                                                  + "reported to Microsoft.",
            [new ServiceStep("WerSvc"), new ServiceStep("MapsBroker"), new ServiceStep("Fax"), new ServiceStep("RetailDemo")]),

        // Not part of the tune-up: the switch in Settings that holds Windows updates back.
        new(WindowsUpdate, 0, true, "Stop Windows updates", "",
            [
                new RegistryStep(true, UpdateSettings, "FlightSettingsMaxPauseDays", 3650),
                new RegistryStep(true, UpdateSettings, "PauseUpdatesStartTime", "{now}"),
                new RegistryStep(true, UpdateSettings, "PauseUpdatesExpiryTime", "{later}"),
                new RegistryStep(true, UpdateSettings, "PauseFeatureUpdatesStartTime", "{now}"),
                new RegistryStep(true, UpdateSettings, "PauseFeatureUpdatesEndTime", "{later}"),
                new RegistryStep(true, UpdateSettings, "PauseQualityUpdatesStartTime", "{now}"),
                new RegistryStep(true, UpdateSettings, "PauseQualityUpdatesEndTime", "{later}"),
                new RegistryStep(true, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1),
            ], "update"),
    ];

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string StatePath => Path.Combine(AppInfo.DataDir, "tuneup.json");

    /// <summary>The tweaks of a preset: its own and those of every preset below it.</summary>
    public static List<string> ForLevel(string level)
    {
        var number = Array.IndexOf(Levels, level) + 1;
        return Catalogue.Where(tweak => tweak.Group == "tune" && tweak.Level >= 1 && tweak.Level <= number).Select(tweak => tweak.Key).ToList();
    }

    public static TuneState Load(string? statePath = null)
    {
        try
        {
            var path = statePath ?? StatePath;
            return File.Exists(path) ? JsonSerializer.Deserialize<TuneState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private static void Save(TuneState state, string? statePath)
    {
        var path = statePath ?? StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
    }

    /// <summary>The helper, when it is running: sends it a request and returns its answer.</summary>
    private static List<string>? Helper(string action, List<string> items) => AdminHelper.Running ? AdminHelper.Send(action, items, 60) : null;

    /// <summary>True while this tweak is applied (and not yet undone).</summary>
    public static bool IsApplied(string key, string? statePath = null) => Load(statePath).Applied.ContainsKey(key);

    /// <summary>
    /// Apply these tweaks. With a level ("low" … "custom") this is the tune-up: whatever it
    /// had applied before is undone first, so the chosen tweaks are exactly what is in force.
    /// With level null the tweaks are added to what is there (the Settings switches).
    /// </summary>
    public static TuneResult Apply(IEnumerable<string> keys, string? level, Tweak[]? catalogue = null, string? statePath = null,
                                   Func<string, List<string>, List<string>?>? admin = null)
    {
        catalogue ??= Catalogue;
        admin ??= Helper;
        lock (Gate)
        {
            var needsHelper = new List<string>();
            if (level is not null)
                needsHelper.AddRange(UndoLocked(tweak => tweak.Group == "tune", catalogue, statePath, admin).NeedsHelper);
            var state = Load(statePath);
            var wanted = keys.Select(key => catalogue.FirstOrDefault(tweak => tweak.Key == key)).OfType<Tweak>()
                             .Where(tweak => !state.Applied.ContainsKey(tweak.Key)).ToList();
            var changed = 0;
            var notify = new HashSet<string>();
            foreach (var tweak in wanted.Where(tweak => !tweak.Admin))
            {
                var before = tweak.Steps.Select(Do).ToList();
                if (before.All(entry => entry.Length == 0))
                    continue; // already as wanted: nothing of ours to undo later
                state.Applied[tweak.Key] = before;
                Save(state, statePath);
                changed++;
                notify.UnionWith(tweak.Steps.OfType<RegistryStep>().Select(step => step.Notify));
            }
            var forHelper = wanted.Where(tweak => tweak.Admin).ToList();
            if (forHelper.Count > 0)
            {
                var answer = admin("tune_apply", forHelper.Select(tweak => tweak.Key).ToList());
                if (answer is null)
                    needsHelper.AddRange(forHelper.Select(tweak => tweak.Key));
                foreach (var tweak in answer is null ? [] : forHelper)
                {
                    var before = Enumerable.Range(0, tweak.Steps.Length).Select(index => Entry(answer!, tweak.Key, index)).ToList();
                    if (before.All(entry => entry.Length == 0))
                        continue;
                    state.Applied[tweak.Key] = before;
                    changed++;
                }
            }
            if (level is not null)
                (state.Level, state.When) = (state.Applied.Keys.Any(key => catalogue.Any(tweak => tweak.Key == key && tweak.Group == "tune")) ? level : "",
                                             DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Save(state, statePath);
            Tell(notify);
            Log.Info($"Tune-up: {changed} tweak(s) applied" + (needsHelper.Count > 0 ? $", {needsHelper.Count} waiting for the helper" : ""));
            return new TuneResult(changed, needsHelper.Distinct().ToList());
        }
    }

    /// <summary>Put back everything the tune-up changed (group "tune"), or one group or tweak of your choosing.</summary>
    public static TuneResult Undo(Func<Tweak, bool>? which = null, Tweak[]? catalogue = null, string? statePath = null,
                                  Func<string, List<string>, List<string>?>? admin = null)
    {
        lock (Gate)
            return UndoLocked(which ?? (tweak => tweak.Group == "tune"), catalogue ?? Catalogue, statePath, admin ?? Helper);
    }

    private static TuneResult UndoLocked(Func<Tweak, bool> which, Tweak[] catalogue, string? statePath, Func<string, List<string>, List<string>?> admin)
    {
        var state = Load(statePath);
        var mine = state.Applied.Keys.Select(key => catalogue.FirstOrDefault(tweak => tweak.Key == key)).OfType<Tweak>().Where(which).ToList();
        var changed = 0;
        var notify = new HashSet<string>();
        foreach (var tweak in mine.Where(tweak => !tweak.Admin))
        {
            var before = state.Applied[tweak.Key];
            for (var index = 0; index < tweak.Steps.Length && index < before.Count; index++)
            {
                if (before[index].Length > 0)
                    Restore(tweak.Steps[index], before[index]);
            }
            state.Applied.Remove(tweak.Key);
            changed++;
            notify.UnionWith(tweak.Steps.OfType<RegistryStep>().Select(step => step.Notify));
        }
        var needsHelper = new List<string>();
        var forHelper = mine.Where(tweak => tweak.Admin).ToList();
        if (forHelper.Count > 0)
        {
            var entries = forHelper.SelectMany(tweak => state.Applied[tweak.Key].Select((before, index) => (before, index))
                                                             .Where(item => item.before.Length > 0 && item.index < tweak.Steps.Length)
                                                             .Select(item => $"{tweak.Key}#{item.index}={item.before}")).ToList();
            if (admin("tune_restore", entries) is null)
                needsHelper.AddRange(forHelper.Select(tweak => tweak.Key));
            else
            {
                foreach (var tweak in forHelper)
                    state.Applied.Remove(tweak.Key);
                changed += forHelper.Count;
            }
        }
        if (!state.Applied.Keys.Any(key => catalogue.Any(tweak => tweak.Key == key && tweak.Group == "tune")))
            state.Level = "";
        Save(state, statePath);
        Tell(notify);
        if (changed > 0)
            Log.Info($"Tune-up: {changed} tweak(s) put back");
        return new TuneResult(changed, needsHelper);
    }

    private static string Entry(List<string> answer, string key, int index)
    {
        var start = $"{key}#{index}=";
        return answer.FirstOrDefault(entry => entry.StartsWith(start, StringComparison.Ordinal))?[start.Length..] ?? "";
    }

    // --- the helper's side ---

    [GeneratedRegex(@"^([a-z_]+)#(\d+)=(.*)$", RegexOptions.Singleline)] private static partial Regex EntryPattern();
    [GeneratedRegex(@"^(-|d:-?\d{1,10})$")] private static partial Regex NumberBefore();
    [GeneratedRegex(@"^(-|s:[0-9TZ:\-]{0,32})$")] private static partial Regex TextBefore();
    [GeneratedRegex(@"^[23]:[01]:[01]$")] private static partial Regex ServiceBefore();

    /// <summary>The helper applies the administrator tweaks among these keys and answers "key#step=what was there before".</summary>
    public static List<string> HelperApply(IEnumerable<string> keys, Func<TweakStep, string>? change = null)
    {
        change ??= Do;
        var done = new List<string>();
        foreach (var tweak in keys.Distinct().Select(key => Catalogue.FirstOrDefault(item => item.Key == key && item.Admin)).OfType<Tweak>())
        {
            for (var index = 0; index < tweak.Steps.Length; index++)
            {
                if (change(tweak.Steps[index]) is { Length: > 0 } before)
                    done.Add($"{tweak.Key}#{index}={before}");
            }
        }
        return done;
    }

    /// <summary>
    /// The helper puts back what it is handed, but only for steps in the catalogue and only
    /// values of the right kind: a number or "not there" where a number was set, a start
    /// type for a service. Anyone can write the request file, so it gets no further.
    /// </summary>
    public static List<string> HelperRestore(IEnumerable<string> entries, Func<TweakStep, string, bool>? restore = null)
    {
        restore ??= Restore;
        var done = new List<string>();
        foreach (var entry in entries)
        {
            var match = EntryPattern().Match(entry);
            if (!match.Success || Catalogue.FirstOrDefault(item => item.Key == match.Groups[1].Value && item.Admin) is not { } tweak
                || !int.TryParse(match.Groups[2].Value, out var index) || index >= tweak.Steps.Length)
                continue;
            var (step, before) = (tweak.Steps[index], match.Groups[3].Value);
            var allowed = step switch
            {
                RegistryStep { Machine: true, Value: int } => NumberBefore().IsMatch(before),
                RegistryStep { Machine: true, Value: string } => TextBefore().IsMatch(before),
                ServiceStep => ServiceBefore().IsMatch(before),
                _ => false,
            };
            if (allowed && restore(step, before))
                done.Add(entry);
        }
        return done;
    }

    // --- the steps ---

    private static string Resolve(object value) => value switch
    {
        "{now}" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "{later}" => DateTime.UtcNow.AddDays(3650).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        _ => value.ToString()!,
    };

    /// <summary>Carry out a step. Returns what was there before, or "" when nothing was changed (already so, or not possible).</summary>
    public static string Do(TweakStep step)
    {
        try
        {
            switch (step)
            {
                case RegistryStep registry:
                    using (var key = (registry.Machine ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(registry.Path))
                    {
                        var before = key.GetValue(registry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
                        {
                            null => "-", int number => "d:" + number, string text => "s:" + text, _ => "",
                        };
                        var target = registry.Value is int wanted ? "d:" + wanted : "s:" + Resolve(registry.Value);
                        if (before.Length == 0 || before == target)
                            return "";
                        if (registry.Value is int value)
                            key.SetValue(registry.Name, value, RegistryValueKind.DWord);
                        else
                            key.SetValue(registry.Name, target[2..], RegistryValueKind.String);
                        return before;
                    }
                case ServiceStep service:
                    {
                        var (start, delayed) = ServiceStart(service.Service);
                        if (start is not (2 or 3))
                            return ""; // not on this PC, already off, or a kind of service this must not touch
                        var running = AdminHelper.IsRunning(service.Service);
                        AdminHelper.RunSc("config", service.Service, "start=", "disabled");
                        if (ServiceStart(service.Service).Start != 4)
                            return "";
                        if (running)
                            AdminHelper.RunSc("stop", service.Service);
                        return $"{start}:{(delayed ? 1 : 0)}:{(running ? 1 : 0)}";
                    }
                case PowerPlanStep:
                    {
                        var plans = Booster.PowerPlans();
                        var current = plans.FirstOrDefault(plan => plan.Active);
                        var fast = plans.FirstOrDefault(plan => plan.Guid.Equals(BoostMode.UltimatePerformance, StringComparison.OrdinalIgnoreCase))
                                   ?? plans.FirstOrDefault(plan => plan.Guid.Equals(BoostMode.HighPerformance, StringComparison.OrdinalIgnoreCase));
                        return current is null || fast is null || fast.Active || Booster.SetPowerPlan(fast) is not null ? "" : current.Guid;
                    }
                case AnimationStep:
                    {
                        var (window, controls) = Animations();
                        if (!window && !controls)
                            return "";
                        SetAnimations(false, false);
                        return $"{(window ? 1 : 0)}:{(controls ? 1 : 0)}";
                    }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning($"Tune-up could not change {step}: {error.Message}");
        }
        return "";
    }

    /// <summary>Put a step back to what Do found.</summary>
    public static bool Restore(TweakStep step, string before)
    {
        try
        {
            switch (step)
            {
                case RegistryStep registry:
                    using (var key = (registry.Machine ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(registry.Path))
                    {
                        // A value changed again since (by you, or by Windows) is left as it is now.
                        if (registry.Value is int mine && !(key.GetValue(registry.Name) is int now && now == mine))
                            return true;
                        if (before == "-")
                            key.DeleteValue(registry.Name, throwOnMissingValue: false);
                        else if (before.StartsWith("d:", StringComparison.Ordinal) && int.TryParse(before[2..], out var number))
                            key.SetValue(registry.Name, number, RegistryValueKind.DWord);
                        else if (before.StartsWith("s:", StringComparison.Ordinal))
                            key.SetValue(registry.Name, before[2..], RegistryValueKind.String);
                        else
                            return false;
                        return true;
                    }
                case ServiceStep service:
                    {
                        var parts = before.Split(':');
                        if (parts.Length != 3 || ServiceStart(service.Service).Start != 4)
                            return parts.Length == 3; // switched back on by someone else meanwhile: theirs
                        AdminHelper.RunSc("config", service.Service, "start=", parts[0] == "3" ? "demand" : parts[1] == "1" ? "delayed-auto" : "auto");
                        if (parts[2] == "1")
                            AdminHelper.RunSc("start", service.Service);
                        return ServiceStart(service.Service).Start != 4;
                    }
                case PowerPlanStep:
                    {
                        var plans = Booster.PowerPlans();
                        var previous = plans.FirstOrDefault(plan => plan.Guid.Equals(before, StringComparison.OrdinalIgnoreCase));
                        var fastInUse = plans.Any(plan => plan.Active && (plan.Guid.Equals(BoostMode.UltimatePerformance, StringComparison.OrdinalIgnoreCase)
                                                                          || plan.Guid.Equals(BoostMode.HighPerformance, StringComparison.OrdinalIgnoreCase)));
                        // If you picked another plan yourself meanwhile, that choice stays.
                        return previous is null || !fastInUse || Booster.SetPowerPlan(previous) is null;
                    }
                case AnimationStep:
                    {
                        var parts = before.Split(':');
                        if (parts.Length == 2)
                            SetAnimations(parts[0] == "1", parts[1] == "1");
                        return true;
                    }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning($"Tune-up could not put {step} back: {error.Message}");
        }
        return false;
    }

    /// <summary>A service's start type (2 automatic, 3 manual, 4 disabled; -1 when there is no such service) and whether its start is delayed.</summary>
    public static (int Start, bool Delayed) ServiceStart(string service)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            return key?.GetValue("Start") is int start ? (start, key.GetValue("DelayedAutostart") is 1) : (-1, false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return (-1, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref AnimationInfo info, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref int value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, IntPtr value, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wide, string text, uint flags, uint timeout, out IntPtr result);

    private static (bool Window, bool Controls) Animations()
    {
        var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>() };
        var controls = 0;
        SystemParametersInfo(0x0048, info.Size, ref info, 0);
        SystemParametersInfo(0x1042, 0, ref controls, 0);
        return (info.MinAnimate != 0, controls != 0);
    }

    private static void SetAnimations(bool window, bool controls)
    {
        var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = window ? 1 : 0 };
        SystemParametersInfo(0x0049, info.Size, ref info, 3);
        SystemParametersInfo(0x1043, 0, controls ? 1 : 0, 3);
    }

    /// <summary>Tell open programs that a setting changed, so it shows without signing out.</summary>
    private static void Tell(IEnumerable<string> what)
    {
        foreach (var name in what.Where(name => name.Length > 0))
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, name, 2, 1000, out _);
    }
}

/// <summary>The PC's name in Windows.</summary>
public static partial class PcName
{
    [GeneratedRegex("^(?![0-9]+$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$")] private static partial Regex Pattern();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetComputerNameExW(int type, string name);

    /// <summary>The name in use now.</summary>
    public static string Current => Environment.MachineName;

    /// <summary>The name Windows will use after the next restart, when one is waiting; otherwise the current one.</summary>
    public static string Pending
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
                return key?.GetValue("ComputerName") as string is { Length: > 0 } name ? name : Current;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return Current;
            }
        }
    }

    /// <summary>Windows' rule for a PC's name: 1 to 15 letters, digits and hyphens, not only digits, no hyphen at either end.</summary>
    public static bool Valid(string name) => Pattern().IsMatch(name);

    /// <summary>Give the PC a new name; Windows uses it after a restart. Administrator only.</summary>
    public static bool Set(string name) => Valid(name) && SetComputerNameExW(5, name);
}

/// <summary>This PC's addresses: on its own network, and as the internet sees it.</summary>
public static class NetworkAddresses
{
    /// <summary>The address on the local network: that of the adapter the internet is reached through.</summary>
    public static string Local()
    {
        try
        {
            // Nothing is sent: a UDP socket only works out which adapter would be used.
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("1.1.1.1", 53);
            return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return "";
        }
    }

    /// <summary>The address the internet sees, as Cloudflare reports it back; "" when there is no connection.</summary>
    public static async Task<string> Public(CancellationToken cancel = default) =>
        await Public(System.Net.Sockets.AddressFamily.InterNetwork, cancel) is { Length: > 0 } four ? four
            : await Public(System.Net.Sockets.AddressFamily.InterNetworkV6, cancel);

    /// <summary>Asked over IPv4 first, as that is the address people mean; over IPv6 when the connection has no IPv4.</summary>
    private static async Task<string> Public(System.Net.Sockets.AddressFamily family, CancellationToken cancel)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, stop) =>
                {
                    var socket = new System.Net.Sockets.Socket(family, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, stop);
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, stop);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            var text = await client.GetStringAsync("https://speed.cloudflare.com/cdn-cgi/trace", cancel);
            var line = text.Split('\n').FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal));
            return line is not null && System.Net.IPAddress.TryParse(line[3..].Trim(), out var address) ? address.ToString() : "";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            return "";
        }
    }
}
