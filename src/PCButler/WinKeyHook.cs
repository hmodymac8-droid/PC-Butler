// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Win32;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Shortcuts with the Win key. Windows keeps many of them for itself (Win + F opens Feedback
/// Hub), and refuses to hand them to RegisterHotKey, so these are caught by a low-level keyboard
/// hook instead: it sees the keys before Windows acts on them, swallows the chosen combination,
/// and lets every other key through untouched. It needs no administrator rights, and is there
/// only while PC Butler runs; closed, Win + F does what Windows normally does.
///
/// The hook runs on a thread of its own, so a busy window never makes typing lag elsewhere, and
/// does as little as possible: compare the key, post a message, return.
///
/// Windows does not pass keys to the hook while a program running as administrator is in front.
/// </summary>
public sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int WM_APP = 0x8000, WM_QUIT = 0x12;
    private const int Injected = 0x10;
    /// <summary>A key code no keyboard has: pressing it in between tells Windows the Win key was used with something, so the Start menu stays shut.</summary>
    private const byte MaskKey = 0xE8;

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private readonly HookProc proc; // kept here, or the collector would take it away from Windows
    private readonly Action<int> fired;
    private (uint Modifiers, int Key, int Id)[] wanted = [];
    private Thread? thread;
    private uint threadId;
    private IntPtr hook;
    private int swallowing; // the key whose release is swallowed too

    public WinKeyHook(Action<int> fired)
    {
        this.fired = fired;
        proc = Proc;
    }

    public bool Running => hook != IntPtr.Zero;

    /// <summary>Catch these combinations (ModifierKeys flags, virtual key, an id handed back when pressed). Returns false if Windows refused the hook.</summary>
    public bool Set(IReadOnlyList<(ModifierKeys Modifiers, int Key, int Id)> shortcuts)
    {
        wanted = shortcuts.Select(item => ((uint)item.Modifiers, item.Key, item.Id)).ToArray();
        if (wanted.Length == 0)
        {
            Stop();
            return true;
        }
        if (thread is not null)
            return Running;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "PC Butler Win-key shortcuts", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (!Running)
        {
            Stop();
            return false;
        }
        return true;
    }

    private void Loop(ManualResetEventSlim ready)
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            Log.Warning($"Win-key shortcuts: the keyboard hook was refused (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        if (hook == IntPtr.Zero)
            return;
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message != WM_APP)
                continue;
            keybd_event(MaskKey, 0, 0, IntPtr.Zero);
            keybd_event(MaskKey, 0, 2, IntPtr.Zero);
            var id = (int)message.WParam;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => fired(id));
        }
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr Proc(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var flags = Marshal.ReadInt32(data, 8);
            var kind = (int)message;
            if ((flags & Injected) == 0 || key != MaskKey) // keys from an on-screen keyboard count; only our own mask key is let through
            {
                if (kind is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (swallowing == key)
                        return 1; // held down: Windows repeats it
                    foreach (var (modifiers, wantedKey, id) in wanted)
                    {
                        if (wantedKey == key && Modifiers() == modifiers)
                        {
                            swallowing = key;
                            PostThreadMessage(threadId, WM_APP, id, IntPtr.Zero);
                            return 1;
                        }
                    }
                }
                else if (kind is WM_KEYUP or WM_SYSKEYUP && swallowing == key)
                {
                    swallowing = 0;
                    return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    /// <summary>The modifier keys held now, as ModifierKeys flags.</summary>
    private static uint Modifiers()
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        return (Down(0x12) ? 1u : 0) | (Down(0x11) ? 2u : 0) | (Down(0x10) ? 4u : 0) | (Down(0x5B) || Down(0x5C) ? 8u : 0);
    }

    public void Stop()
    {
        if (thread is null)
            return;
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, 0, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        (thread, threadId) = (null, 0);
    }

    public void Dispose() => Stop();

    // --- the fallback: taking a Win + letter shortcut away from File Explorer ---

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>
    /// When the hook cannot be used: File Explorer gives up Win + this letter (its per-user
    /// DisabledHotkeys list), so RegisterHotKey can have it after File Explorer restarts. Returns
    /// false when the letter was already given up or could not be.
    /// </summary>
    public static bool FreeFromExplorer(char letter) => EditDisabled(letter, add: true);

    /// <summary>Give the letter back to File Explorer (after its next restart).</summary>
    public static bool ReturnToExplorer(char letter) => EditDisabled(letter, add: false);

    private static bool EditDisabled(char letter, bool add)
    {
        letter = char.ToUpperInvariant(letter);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Advanced);
            var letters = key.GetValue("DisabledHotkeys") as string ?? "";
            if (add == letters.Contains(letter))
                return false;
            letters = add ? letters + letter : letters.Replace(letter.ToString(), "");
            if (letters.Length > 0)
                key.SetValue("DisabledHotkeys", letters);
            else
                key.DeleteValue("DisabledHotkeys", throwOnMissingValue: false);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Warning($"DisabledHotkeys could not be changed: {error.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Window;
        public int Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int message, nint wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
