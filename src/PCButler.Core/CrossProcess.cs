// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
namespace PCButler.Core;

/// <summary>
/// PC Butler runs as several processes now (the background agent, the board, one per open tool),
/// and any of them can write config.json or history.json. A named mutex per file keeps one write
/// (or read) from landing in the middle of another.
/// </summary>
public static class CrossProcess
{
    /// <summary>
    /// Added to every name shared between processes: a copy pointed at its own data folder
    /// (PCBUTLER_DATA, used for testing) counts as a separate program.
    /// </summary>
    public static readonly string Scope =
        Environment.GetEnvironmentVariable("PCBUTLER_DATA") is { Length: > 0 } custom
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                  System.Text.Encoding.UTF8.GetBytes(custom.ToLowerInvariant())))[..8]
            : "";

    /// <summary>Hold the lock called <paramref name="name"/> until the result is disposed (a few seconds at most).</summary>
    public static IDisposable Lock(string name)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, @"Local\PCButler.Lock." + name + Scope);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return Nothing.Instance; // held by a copy with other rights: carry on without it
        }
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            {
                mutex.Dispose();
                return Nothing.Instance; // a stuck process must never freeze the others
            }
        }
        catch (AbandonedMutexException)
        {
            // Its last holder ended without letting go: this thread owns it now.
        }
        return new Held(mutex);
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done)
                return;
            done = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            mutex.Dispose();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
