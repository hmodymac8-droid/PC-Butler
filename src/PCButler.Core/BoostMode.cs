// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>What Boost does, as chosen on the Booster page.</summary>
public sealed class BoostSettings
{
    /// <summary>Raise the chosen program's priority and lower the background programs'.</summary>
    [JsonPropertyName("priorities")] public bool Priorities { get; set; } = true;
    /// <summary>Switch to the high-performance power plan while Boost is on.</summary>
    [JsonPropertyName("power_plan")] public bool PowerPlan { get; set; } = true;
    /// <summary>Push background programs' memory out to the page file. Off by default: they reload it later.</summary>
    [JsonPropertyName("trim_memory")] public bool TrimMemory { get; set; }
    /// <summary>Pause a few Windows services while Boost is on. Only works while the administrator helper runs.</summary>
    [JsonPropertyName("services")] public bool Services { get; set; }
    /// <summary>Program file names (game.exe) that switch Boost on while they are running.</summary>
    [JsonPropertyName("auto_apps")] public List<string> AutoApps { get; set; } = [];
}

/// <summary>A program whose priority Boost changed, and what it was before.</summary>
public sealed class BoostedProcess
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    /// <summary>Its start time, so a new program that got the same number is never touched.</summary>
    [JsonPropertyName("started")] public long Started { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("raised")] public bool Raised { get; set; }
}

/// <summary>Everything Boost changed, kept in boost.json so it can be put back even after a crash.</summary>
public sealed class BoostState
{
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    /// <summary>Switched on by a program from the automatic list, so it goes off when that program closes.</summary>
    [JsonPropertyName("auto")] public bool Auto { get; set; }
    /// <summary>The program that was sped up; "" when none was chosen.</summary>
    [JsonPropertyName("favoured")] public string Favoured { get; set; } = "";
    [JsonPropertyName("previous_plan")] public string PreviousPlan { get; set; } = "";
    [JsonPropertyName("boost_plan")] public string BoostPlan { get; set; } = "";
    [JsonPropertyName("plan_name")] public string PlanName { get; set; } = "";
    [JsonPropertyName("processes")] public List<BoostedProcess> Processes { get; set; } = [];
    /// <summary>Bytes of memory that became available, measured right after trimming.</summary>
    [JsonPropertyName("freed")] public long Freed { get; set; }
    [JsonPropertyName("trimmed")] public int Trimmed { get; set; }
    /// <summary>Windows services paused through the administrator helper.</summary>
    [JsonPropertyName("paused_services")] public List<string> PausedServices { get; set; } = [];

    [JsonIgnore] public int Lowered => Processes.Count(process => !process.Raised);
}

public sealed record WindowedProgram(int Pid, string Name, string Title);

/// <summary>
/// Boost: give one program the PC's attention for a while. The chosen program's priority
/// is raised, background programs' priorities are lowered, the power plan is switched to
/// high performance and, if asked, background programs' memory is trimmed. Nothing here
/// needs administrator rights, nothing is closed, and Windows' own programs are never
/// touched. Every change is written to boost.json first, so Stop (or the next start of
/// PC Butler, after a crash) puts everything back.
/// </summary>
public static class BoostMode
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string StatePath => Path.Combine(AppInfo.DataDir, "boost.json");

    /// <summary>What Boost has changed, while it is on.</summary>
    public static BoostState? Active { get; private set; }

    private static void Save(BoostState state)
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Options));
    }

    private static BoostState? Load()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<BoostState>(File.ReadAllText(StatePath)) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Programs with a window, to choose the one to speed up from.</summary>
    public static List<WindowedProgram> WindowedPrograms()
    {
        var found = new List<WindowedProgram>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName + ".exe";
                    if (process.SessionId == session && process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero
                        && process.MainWindowTitle.Length > 0 && !Booster.ProtectedNames.Contains(name))
                        found.Add(new WindowedProgram(process.Id, name, process.MainWindowTitle));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return found.OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Switch Boost on. favouredPid is the program to speed up (0 = none, only quieten the
    /// background). "only" narrows which programs may be touched; the tests use it.
    /// </summary>
    public static BoostState Start(BoostSettings settings, int favouredPid = 0, bool auto = false, Func<Process, bool>? only = null)
    {
        lock (Gate)
        {
            if (Active is not null || File.Exists(StatePath))
                Stop();
            var state = new BoostState { Started = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Auto = auto };

            if (settings.PowerPlan)
            {
                var plans = Booster.PowerPlans();
                var current = plans.FirstOrDefault(plan => plan.Active);
                var fast = plans.FirstOrDefault(plan => plan.Guid.Equals(UltimatePerformance, StringComparison.OrdinalIgnoreCase))
                           ?? plans.FirstOrDefault(plan => plan.Guid.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase));
                if (current is not null && fast is not null && !fast.Active)
                {
                    (state.PreviousPlan, state.BoostPlan, state.PlanName) = (current.Guid, fast.Guid, fast.Name);
                    Save(state); // before the switch, so the old plan is known whatever happens next
                    if (Booster.SetPowerPlan(fast) is not null)
                        (state.PreviousPlan, state.BoostPlan, state.PlanName) = ("", "", "");
                }
            }

            var favouredName = "";
            if (favouredPid != 0)
            {
                try
                {
                    using var favoured = Process.GetProcessById(favouredPid);
                    favouredName = favoured.ProcessName + ".exe";
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    favouredPid = 0;
                }
            }
            state.Favoured = favouredName;

            var session = Process.GetCurrentProcess().SessionId;
            var before = Booster.MemoryStatus().Available;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        // Only your own programs: never Windows' own, never services, never PC Butler.
                        if (process.SessionId != session || process.Id == Environment.ProcessId || (only is not null && !only(process)))
                            continue;
                        var name = process.ProcessName + ".exe";
                        if (Booster.ProtectedNames.Contains(name))
                            continue;
                        var isFavoured = process.Id == favouredPid;
                        // The chosen program's other processes (a browser's tabs, a game's launcher) are left as they are.
                        if (!isFavoured && name.Equals(favouredName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Only programs at the normal priority: one set differently was set so on purpose.
                        if (settings.Priorities && process.PriorityClass == ProcessPriorityClass.Normal)
                        {
                            var entry = new BoostedProcess
                            {
                                Pid = process.Id, Started = process.StartTime.Ticks, Name = name,
                                Priority = (int)ProcessPriorityClass.Normal, Raised = isFavoured,
                            };
                            process.PriorityClass = isFavoured ? ProcessPriorityClass.High : ProcessPriorityClass.BelowNormal;
                            state.Processes.Add(entry);
                        }
                        if (settings.TrimMemory && !isFavoured && EmptyWorkingSet(process.Handle))
                            state.Trimmed++;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
                                                      or NotSupportedException)
                    {
                        // Ended meanwhile, or running as administrator: left alone.
                    }
                }
            }
            if (settings.TrimMemory)
            {
                Thread.Sleep(300); // Windows takes a moment to count the memory as available
                state.Freed = Math.Max(0, Booster.MemoryStatus().Available - before);
            }
            if (settings.Services && only is null && AdminHelper.Running)
            {
                var running = AdminHelper.PausableServices.Select(service => service.Key).Where(AdminHelper.IsRunning).ToList();
                if (running.Count > 0)
                    state.PausedServices = AdminHelper.Send("stop_services", running) ?? [];
            }
            Save(state);
            Log.Info($"Boost on: {state.Processes.Count} priorities changed, plan {(state.PlanName.Length > 0 ? state.PlanName : "unchanged")}, "
                     + $"{state.Trimmed} trimmed");
            return Active = state;
        }
    }

    /// <summary>Switch Boost off and put everything back. Returns what had been changed, or null when it was off.</summary>
    public static BoostState? Stop()
    {
        lock (Gate)
        {
            var state = Active ?? Load();
            Active = null;
            if (state is null)
            {
                DeleteState();
                return null;
            }
            foreach (var entry in state.Processes)
            {
                try
                {
                    using var process = Process.GetProcessById(entry.Pid);
                    if (process.StartTime.Ticks == entry.Started)
                        process.PriorityClass = (ProcessPriorityClass)entry.Priority;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                                                  or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Closed since: nothing to put back.
                }
            }
            if (state.PreviousPlan.Length > 0)
            {
                var plans = Booster.PowerPlans();
                var previous = plans.FirstOrDefault(plan => plan.Guid.Equals(state.PreviousPlan, StringComparison.OrdinalIgnoreCase));
                // If you picked another plan yourself meanwhile, that choice stays.
                if (previous is not null && plans.Any(plan => plan.Active && plan.Guid.Equals(state.BoostPlan, StringComparison.OrdinalIgnoreCase)))
                    Booster.SetPowerPlan(previous);
            }
            // If the helper has gone, it already resumed them itself on its way out.
            if (state.PausedServices.Count > 0 && AdminHelper.Running)
                AdminHelper.Send("start_services", state.PausedServices);
            DeleteState();
            Log.Info("Boost off: priorities, power plan and services put back");
            return state;
        }
    }

    private static void DeleteState()
    {
        try
        {
            File.Delete(StatePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not remove {StatePath}: {error.Message}");
        }
    }

    /// <summary>At startup: if PC Butler was closed or crashed while Boost was on, put everything back.</summary>
    public static bool Recover()
    {
        if (!File.Exists(StatePath))
            return false;
        Log.Info("Boost was left on; putting everything back");
        Stop();
        return true;
    }

    /// <summary>
    /// The automatic list: switch Boost on when one of its programs is running and off when
    /// none is any more. Returns the program's name when it switched on, "" when it switched
    /// off, null when nothing changed. A Boost you switched on yourself is left alone.
    /// </summary>
    public static string? AutoCheck(BoostSettings settings)
    {
        if (Active is { Auto: false } || (Active is null && settings.AutoApps.Count == 0))
            return null;
        Process? running = null;
        foreach (var app in settings.AutoApps)
        {
            var copies = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.Trim()));
            // A program's main process is usually the one with the window; failing that, the biggest.
            var best = copies.OrderByDescending(HasWindow).ThenByDescending(Memory).FirstOrDefault();
            foreach (var copy in copies)
            {
                if (copy != best || running is not null)
                    copy.Dispose();
            }
            running ??= best;
        }
        using (running)
        {
            if (Active is null && running is not null)
            {
                var name = running.ProcessName + ".exe";
                Start(settings, running.Id, auto: true);
                return name;
            }
            if (Active is { Auto: true } && running is null)
            {
                Stop();
                return "";
            }
        }
        return null;

        static bool HasWindow(Process process)
        {
            try
            {
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        static long Memory(Process process)
        {
            try
            {
                return process.WorkingSet64;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }
}
