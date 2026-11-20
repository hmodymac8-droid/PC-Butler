// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using PCButler.Core;

namespace PCButler.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Agent unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Seldom("agent unobserved " + e.Exception.GetBaseException().Message, TimeSpan.FromHours(1), $"ERROR Agent unobserved: {e.Exception}");
            e.SetObserved();
        };
        if (arguments.Contains("--auto-on"))
        {
            // The setup wizard's "sort automatically" option was ticked.
            var config = ConfigStore.Load();
            config.AutoSort = true;
            ConfigStore.Save(config);
        }
        Mutex instance;
        bool first;
        try
        {
            instance = new Mutex(true, Link.Agent.MutexName, out first);
        }
        catch (UnauthorizedAccessException)
        {
            return 0; // one runs already, with other rights
        }
        using (instance)
        {
            if (!first)
                return 0; // one runs already
            AppInfo.ImportLegacyData();
            if (!AgentNeeds.Any(ConfigStore.Load()))
            {
                // Nothing that needs to run in the background is switched on: no process at all.
                Log.Info("Agent: no background feature is on, so nothing stays running");
                return 0;
            }
            new Agent().Run();
        }
        return 0;
    }
}
