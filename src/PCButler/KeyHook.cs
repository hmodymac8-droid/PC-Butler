// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace PCButler;

/// <summary>
/// A shortcut Windows keeps for itself, such as Win+V (its own clipboard history), cannot be
/// registered: RegisterHotKey refuses it. A low-level keyboard hook sees the keys before
/// Windows does, so it can take such a shortcut over: the key is swallowed and the action runs.
/// The hook lives on its own thread, so a busy window never slows typing anywhere.
/// </summary>
public sealed class KeyHook : IDisposable
{
    private sealed record Binding(ModifierKeys Modifiers, uint Key, Action Run);

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    /// <summary>A key code no keyboard has: pressing it makes Windows treat Win (or Alt) as used in a combination, so letting go does not open Start (or the menu bar).</summary>
    private const ushort VK_MASK = 0xE8;

    private readonly HookProc proc;
    private volatile Binding? binding;
    private Thread? thread;
    private uint threadId;
    private bool down;

    public KeyHook() => proc = Callback;

    /// <summary>Take over these keys; the action runs on the UI thread. Returns false when Windows refused the hook.</summary>
    public bool Set(ModifierKeys modifiers, Key key, Action run)
    {
        binding = new Binding(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), run);
        if (thread is not null)
            return true;
        var ready = new ManualResetEventSlim();
        var hooked = false;
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            var handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            hooked = handle != IntPtr.Zero;
            ready.Set();
            if (!hooked)
                return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            UnhookWindowsHookEx(handle);
        }) { IsBackground = true, Name = "PC Butler keyboard hook" };
        thread.Start();
        ready.Wait();
        if (!hooked)
        {
            thread = null;
            binding = null;
        }
        return hooked;
    }

    /// <summary>Give the keys back to Windows.</summary>
    public void Clear()
    {
        binding = null;
        if (thread is null)
            return;
        PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
        thread = null;
    }

    public void Dispose() => Clear();

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && binding is { } wanted)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.Key == wanted.Key)
            {
                var message = wParam.ToInt32();
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN && Held() == wanted.Modifiers)
                {
                    if (!down) // a held key repeats: the action runs once
                    {
                        down = true;
                        Mask();
                        Application.Current?.Dispatcher.BeginInvoke(wanted.Run);
                    }
                    return 1;
                }
                if (message is WM_KEYUP or WM_SYSKEYUP && down)
                {
                    down = false;
                    return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static ModifierKeys Held()
    {
        static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        var held = ModifierKeys.None;
        if (Pressed(VK_CONTROL)) held |= ModifierKeys.Control;
        if (Pressed(VK_MENU)) held |= ModifierKeys.Alt;
        if (Pressed(VK_SHIFT)) held |= ModifierKeys.Shift;
        if (Pressed(VK_LWIN) || Pressed(VK_RWIN)) held |= ModifierKeys.Windows;
        return held;
    }

    private static void Mask()
    {
        var inputs = new[] { Input(VK_MASK, false), Input(VK_MASK, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Input(ushort key, bool up) => new()
    {
        Type = 1, // keyboard
        Union = new InputUnion { Keyboard = new KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } },
    };

    // --- Windows ---

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint Key, Scan, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    // INPUT is a type and a union whose largest member is MOUSEINPUT; SendInput refuses a wrong size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
