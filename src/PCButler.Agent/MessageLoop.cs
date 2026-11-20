// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Collections.Concurrent;
using static PCButler.Agent.Native;

namespace PCButler.Agent;

/// <summary>
/// The agent's one thread for everything that is not background work: a hidden window that
/// receives Windows' messages (tray clicks, shortcuts, clipboard changes), timers, and work
/// posted from other threads. It is also the synchronization context, so an await carries on here.
/// </summary>
internal sealed class MessageLoop : SynchronizationContext
{
    private const uint Posted = WM_APP + 1;
    private readonly WndProc proc; // kept here, or the collector would take it away from Windows
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly Dictionary<nuint, Action> timers = [];
    private readonly List<Func<uint, IntPtr, IntPtr, bool>> handlers = [];
    private readonly uint threadId;
    private nuint nextTimer = 1;

    public IntPtr Handle { get; }

    public MessageLoop()
    {
        threadId = GetCurrentThreadId();
        proc = Proc;
        var name = "PCButler.Agent." + Guid.NewGuid().ToString("N");
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = proc, hInstance = GetModuleHandle(null), lpszClassName = name,
        };
        RegisterClassEx(ref windowClass);
        // A real top-level window, never shown: a message-only one would miss "TaskbarCreated" (Explorer restarted).
        Handle = CreateWindowEx(0, name, "PC Butler agent", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
    }

    public bool OnLoopThread => GetCurrentThreadId() == threadId;

    /// <summary>Handle a window message; return true when it was handled.</summary>
    public void Listen(Func<uint, IntPtr, IntPtr, bool> handler) => handlers.Add(handler);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        queue.Enqueue((callback, state));
        PostMessage(Handle, Posted, IntPtr.Zero, IntPtr.Zero);
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (OnLoopThread)
        {
            callback(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? failed = null;
        Post(_ =>
        {
            try
            {
                callback(state);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (failed is not null)
            throw new InvalidOperationException(failed.Message, failed);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Post(Action action) => Post(_ => action(), null);

    /// <summary>Run on the loop's thread and wait for the answer (from the pipe's thread).</summary>
    public T Invoke<T>(Func<T> work)
    {
        T result = default!;
        Send(_ => result = work(), null);
        return result;
    }

    /// <summary>Every <paramref name="interval"/> on the loop's thread, until stopped.</summary>
    public nuint Every(TimeSpan interval, Action action)
    {
        var id = nextTimer++;
        timers[id] = action;
        SetTimer(Handle, (UIntPtr)id, (uint)Math.Max(10, interval.TotalMilliseconds), IntPtr.Zero);
        return id;
    }

    /// <summary>Once, after <paramref name="delay"/>.</summary>
    public void After(TimeSpan delay, Action action)
    {
        nuint id = 0;
        id = Every(delay, () =>
        {
            Stop(id);
            action();
        });
    }

    public void Stop(nuint id)
    {
        KillTimer(Handle, (UIntPtr)id);
        timers.Remove(id);
    }

    public void Run()
    {
        SetSynchronizationContext(this);
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Quit() => Post(() => PostQuitMessage(0));

    private IntPtr Proc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == Posted)
            {
                while (queue.TryDequeue(out var item))
                    item.Callback(item.State);
                return IntPtr.Zero;
            }
            if (message == WM_TIMER && timers.TryGetValue((nuint)wParam, out var tick))
            {
                tick();
                return IntPtr.Zero;
            }
            foreach (var handler in handlers.ToList())
                if (handler(message, wParam, lParam))
                    return IntPtr.Zero;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // One failing feature must never take the tray icon and the others down.
            PCButler.Core.Log.Error($"Agent: {error}");
        }
        if (message == WM_QUERYENDSESSION)
            return new IntPtr(1);
        return DefWindowProc(window, message, wParam, lParam);
    }
}
