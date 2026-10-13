// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PCButler.Core;

/// <summary>
/// Gathers the items of one right-click into one batch. File Explorer starts the program
/// once per selected item; the first copy collects the paths the others send it, so ten
/// selected files give one confirmation and one result, not ten.
/// </summary>
public static class MenuBatch
{
    /// <summary>How long the first copy waits for more paths after the last one arrived.</summary>
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromMilliseconds(700);

    private static string Name(string action)
    {
        // Per action and per data folder, so a test copy never joins the real program's batch.
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppInfo.DataDir.ToLowerInvariant())))[..8];
        return $"PCButler.Menu.{action}.{scope}";
    }

    /// <summary>
    /// Hand path to the copy collecting this action's batch and return null, or, when this is
    /// the first copy, collect the batch and return every path in it (path first).
    /// </summary>
    public static List<string>? Gather(string action, string path)
    {
        var name = Name(action);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var first = new Mutex(true, $@"Local\{name}", out var created);
            if (created)
            {
                try
                {
                    return Collect(name, path);
                }
                finally
                {
                    first.ReleaseMutex();
                }
            }
            if (Send(name, path))
                return null;
            // The collector had just finished: try again, perhaps as the next batch's collector.
            Thread.Sleep(100);
        }
        return [path]; // nobody to hand it to; act on it alone rather than drop it
    }

    private static bool Send(string name, string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine(path);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> Collect(string name, string path)
    {
        var paths = new List<string> { path };
        while (true)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waiting = server.WaitForConnectionAsync();
            if (!waiting.Wait(Quiet))
            {
                // Closing the pipe ends the wait with an error nobody needs to see.
                waiting.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                break; // quiet for a while: everyone has handed in
            }
            using var reader = new StreamReader(server, Encoding.UTF8);
            while (reader.ReadLine() is { Length: > 0 } line)
                paths.Add(line);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
