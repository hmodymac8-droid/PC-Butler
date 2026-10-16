// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCButler.Core;

/// <summary>A request from the main program to the helper, and the helper's answer.</summary>
public sealed class HelperMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>"stop_services", "start_services", "allow_portal", "tune_apply", "tune_restore", "rename_pc", "fps" or "net".</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    /// <summary>Asked: the services to act on. Answered: the ones it worked for.</summary>
    [JsonPropertyName("services")] public List<string> Services { get; set; } = [];
}

/// <summary>
/// The few things that need administrator rights. PC Butler itself never has them; the
/// helper (see Sensors) is a second copy that does, started only when the user asks and
/// Windows' permission prompt is accepted. The main program leaves a request in a file,
/// the helper carries it out and leaves the answer in another.
///
/// Any program can write that file, so the helper only accepts what is listed here: it
/// can pause and resume three named Windows services, let Courier through the firewall
/// for this program's own file, apply and put back the tweaks in TuneUp's catalogue, and
/// give the PC a new name, and count the frames programs show. Nothing else.
/// </summary>
public static class AdminHelper
{
    /// <summary>Services that are safe to pause for a while: (service name, what Windows calls it, what pausing costs).</summary>
    public static readonly (string Key, string Name, string Cost)[] PausableServices =
    [
        ("Spooler", "Print Spooler", "no printing while paused"),
        ("DiagTrack", "Connected User Experiences and Telemetry", "nothing you would notice"),
        ("SysMain", "SysMain", "programs may open a little slower at first"),
    ];

    private static string CommandFile(string directory) => Path.Combine(directory, "helper-command.json");
    private static string ResultFile(string directory) => Path.Combine(directory, "helper-result.json");

    /// <summary>True while the helper is running (it keeps its sensors file fresh).</summary>
    public static bool Running => Sensors.Latest() is not null;

    /// <summary>
    /// Ask the helper to stop or start services and wait for it. Returns the services it
    /// worked for, or null if the helper did not answer.
    /// </summary>
    public static List<string>? Send(string action, IEnumerable<string> services, int timeoutSeconds = 25, string? directory = null)
    {
        directory ??= AppInfo.DataDir;
        var message = new HelperMessage { Id = Guid.NewGuid().ToString("N"), Action = action, Services = services.ToList() };
        try
        {
            File.Delete(ResultFile(directory));
            var temp = CommandFile(directory) + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(message));
            File.Move(temp, CommandFile(directory), overwrite: true);
            var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(200);
                if (!File.Exists(ResultFile(directory)))
                    continue;
                try
                {
                    var answer = JsonSerializer.Deserialize<HelperMessage>(File.ReadAllText(ResultFile(directory)));
                    if (answer?.Id == message.Id)
                    {
                        File.Delete(ResultFile(directory));
                        return answer.Services;
                    }
                }
                catch (Exception error) when (error is IOException or JsonException)
                {
                    // Still being written; look again.
                }
            }
            File.Delete(CommandFile(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not reach the helper: {error.Message}");
        }
        return null;
    }

    /// <summary>
    /// The helper's side: carry out a waiting request, if there is one. "stopped" is the
    /// helper's own list of what it has paused, so it can resume them when it ends.
    /// </summary>
    public static void Poll(string directory, List<string> stopped, Func<string, bool, bool>? change = null, Func<bool>? allowPortal = null,
                            Func<TweakStep, string>? tune = null, Func<TweakStep, string, bool>? untune = null, Func<string, bool>? rename = null)
    {
        rename ??= PcName.Set;
        change ??= ChangeService;
        allowPortal ??= Portal.AllowThroughFirewall;
        HelperMessage? message;
        try
        {
            if (!File.Exists(CommandFile(directory)))
                return;
            message = JsonSerializer.Deserialize<HelperMessage>(File.ReadAllText(CommandFile(directory)));
            File.Delete(CommandFile(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (message is null || message.Action is not ("stop_services" or "start_services" or "allow_portal" or "tune_apply" or "tune_restore" or "rename_pc" or "fps" or "net"))
            return;
        var services = message.Action is "stop_services" or "start_services";
        var stop = message.Action == "stop_services";
        var done = new List<string>();
        // The firewall request carries nothing to act on: the rule is always for this program's own file.
        if (message.Action == "allow_portal" && allowPortal())
            done.Add("portal");
        if (message.Action == "tune_apply")
            done.AddRange(TuneUp.HelperApply(message.Services, tune));
        if (message.Action == "tune_restore")
            done.AddRange(TuneUp.HelperRestore(message.Services, untune));
        // Counting frames: on or off, nothing else to say.
        if (message.Action == "fps" && message.Services is ["on"] && FpsMeter.Start())
            done.Add("on");
        if (message.Action == "fps" && message.Services is ["off"])
        {
            FpsMeter.Stop();
            done.Add("off");
        }
        // Counting internet use per program, for the record of app usage: on or off the same way.
        if (message.Action == "net" && message.Services is ["on"] && NetMeter.Start())
            done.Add("on");
        if (message.Action == "net" && message.Services is ["off"])
        {
            NetMeter.Stop();
            done.Add("off");
        }
        // A name Windows would refuse is refused here already.
        if (message.Action == "rename_pc" && message.Services is [var name] && PcName.Valid(name) && rename(name))
            done.Add(name);
        foreach (var service in services ? message.Services : [])
        {
            // Only the listed services, spelled as listed; and only resume what this helper paused.
            var key = PausableServices.FirstOrDefault(item => item.Key.Equals(service, StringComparison.OrdinalIgnoreCase)).Key;
            if (key is null || (!stop && !stopped.Contains(key)) || !change(key, stop))
                continue;
            done.Add(key);
            if (stop)
                stopped.Add(key);
            else
                stopped.Remove(key);
        }
        try
        {
            var temp = ResultFile(directory) + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(new HelperMessage { Id = message.Id, Action = message.Action, Services = done }));
            File.Move(temp, ResultFile(directory), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>When the helper ends: resume whatever it paused.</summary>
    public static void ResumeAll(List<string> stopped)
    {
        foreach (var service in stopped.ToList())
            ChangeService(service, stop: false);
        stopped.Clear();
    }

    internal static string RunSc(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return output;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>True if the service is running now. Needs no special rights.</summary>
    public static bool IsRunning(string service) => RunSc("query", service).Contains("RUNNING", StringComparison.Ordinal);

    /// <summary>Stop a running service, or start a stopped one. True if it ended up that way (administrator only).</summary>
    private static bool ChangeService(string service, bool stop)
    {
        if (IsRunning(service) != stop)
            return !stop; // already stopped: not ours to resume later; already running: fine
        RunSc(stop ? "stop" : "start", service);
        for (var tries = 0; tries < 20; tries++)
        {
            Thread.Sleep(250);
            if (IsRunning(service) != stop)
                return true;
        }
        return false;
    }
}
