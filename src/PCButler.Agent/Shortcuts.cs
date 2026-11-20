// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using PCButler.Core;
using static PCButler.Agent.Native;

namespace PCButler.Agent;

/// <summary>
/// The shortcuts the agent holds: the Hotkeys tab's actions and the clipboard list's. Windows'
/// RegisterHotKey takes most; Win shortcuts Windows keeps for itself (Win+F, Win+V) are caught by
/// a low-level keyboard hook instead. The texts of the problems are the Settings page's.
/// </summary>
internal sealed class Shortcuts : IDisposable
{
    // Modifier flags as RegisterHotKey and the saved shortcuts use them.
    private const uint Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;
    private const int FirstId = 0xB100, ClipboardId = 0xB070;
    /// <summary>The Hotkeys tab's actions, in the order the Settings page lists them (their ids follow it).</summary>
    public static readonly string[] Actions = ["open", "palette", "search", "searchpage", "sort", "boost", "overlay", "dropzone", "courier"];
    /// <summary>Starts the message for a Win shortcut Windows would not give up (the Settings page reads it).</summary>
    public const string HeldByWindows = "held-by-windows:";

    private readonly MessageLoop loop;
    private readonly Action<string> run;
    private readonly Action picker;
    private readonly WinHook hook;
    private readonly List<int> registered = [];
    private bool clipboardRegistered;
    private List<(uint Modifiers, int Key, int Id)> actionHook = [];
    private (uint Modifiers, int Key, int Id)? clipboardHook;

    public Shortcuts(MessageLoop loop, Action<string> run, Action picker)
    {
        this.loop = loop;
        this.run = run;
        this.picker = picker;
        hook = new WinHook(id => loop.Post(() => Fire(id)));
        loop.Listen((message, wParam, _) =>
        {
            if (message != WM_HOTKEY)
                return false;
            Fire(wParam.ToInt32());
            return true;
        });
    }

    private void Fire(int id)
    {
        if (id == ClipboardId)
            picker();
        else if (id - FirstId is var index && index >= 0 && index < Actions.Length)
            run(Actions[index]);
    }

    /// <summary>Hold the Hotkeys tab's shortcuts (defaults filled in). Returns, per action, why its shortcut could not be used.</summary>
    public Dictionary<string, string> Apply(Dictionary<string, string> saved)
    {
        var wanted = HotkeyDefaults.Effective(saved);
        foreach (var id in registered)
            UnregisterHotKey(loop.Handle, id);
        registered.Clear();
        var errors = new Dictionary<string, string>();
        var withWin = new List<(uint Modifiers, int Key, int Id, string Keys, string Action)>();
        for (var index = 0; index < Actions.Length; index++)
        {
            var action = Actions[index];
            if (!wanted.TryGetValue(action, out var keys) || keys.Length == 0)
                continue;
            if (!TryParse(keys, out var modifiers, out var key))
                errors[action] = Tr.T("“{0}” is not a shortcut PC Butler can use.", keys);
            else if ((modifiers & Win) != 0)
                withWin.Add((modifiers, key, FirstId + index, keys, action));
            else if (RegisterHotKey(loop.Handle, FirstId + index, modifiers | NoRepeat, (uint)key))
                registered.Add(FirstId + index);
            else
                errors[action] = Tr.T("Another program already uses {0}, and Windows does not say which one. Choose another shortcut, or close that program and try again.", keys);
        }
        actionHook = withWin.Select(item => (item.Modifiers, item.Key, item.Id)).ToList();
        if (!SetHook())
        {
            // No hook: Windows' own registration takes a Win shortcut only once File Explorer has let go of it.
            actionHook = [];
            foreach (var (modifiers, key, id, keys, action) in withWin)
            {
                if (RegisterHotKey(loop.Handle, id, modifiers | NoRepeat, (uint)key))
                    registered.Add(id);
                else
                    errors[action] = HeldByWindows + Tr.T("Windows keeps {0} for itself, and PC Butler could not take it over.", keys);
            }
            SetHook();
        }
        return errors;
    }

    /// <summary>Hold the clipboard list's shortcut (while clipboard history is on). Returns an error to show, or null.</summary>
    public string? ApplyClipboard(ClipboardSettings settings)
    {
        if (clipboardRegistered)
            UnregisterHotKey(loop.Handle, ClipboardId);
        clipboardRegistered = false;
        clipboardHook = null;
        SetHook();
        if (!settings.Enabled || settings.Shortcut.Length == 0)
            return null;
        if (!TryParse(settings.Shortcut, out var modifiers, out var key))
            return Tr.T("“{0}” is not a shortcut PC Butler can use.", settings.Shortcut);
        clipboardRegistered = RegisterHotKey(loop.Handle, ClipboardId, modifiers | NoRepeat, (uint)key);
        // Windows keeps Win+V (its own clipboard history) and other Win shortcuts for itself: those are taken over with the hook.
        if (!clipboardRegistered && (modifiers & Win) != 0)
        {
            clipboardHook = (modifiers, key, ClipboardId);
            if (SetHook())
                return null;
            clipboardHook = null;
            return modifiers == Win && key == 0x56
                ? Tr.T("Win+V belongs to Windows' own clipboard history, and PC Butler could not take it over. Choose another shortcut.")
                : Tr.T("Windows keeps {0} for itself, and PC Butler could not take it over.", settings.Shortcut);
        }
        return clipboardRegistered ? null : Tr.T("Another program already uses {0}, and Windows does not say which one. Choose another shortcut, or close that program and try again.",
                                                 settings.Shortcut);
    }

    private bool SetHook()
    {
        var all = actionHook.ToList();
        if (clipboardHook is { } clip)
            all.Add(clip);
        return hook.Set(all);
    }

    public void Dispose()
    {
        foreach (var id in registered)
            UnregisterHotKey(loop.Handle, id);
        if (clipboardRegistered)
            UnregisterHotKey(loop.Handle, ClipboardId);
        hook.Dispose();
    }

    // --- shortcuts as text ("Win+V", "Ctrl+Alt+F12"), as the Settings page writes them ---

    public static bool TryParse(string text, out uint modifiers, out int key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Control; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                case "win" or "windows": modifiers |= Win; break;
                default:
                    if (key != 0 || KeyCode(part) is not { } code)
                        return false;
                    key = code;
                    break;
            }
        }
        // A plain letter alone would take that letter away from every program.
        return key != 0 && modifiers != 0;
    }

    /// <summary>The virtual key of a key's name as WPF spells it (A, D1, F5, Space, OemTilde…).</summary>
    private static int? KeyCode(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
            return char.ToUpperInvariant(name[0]);
        if (name.Length == 2 && (name[0] is 'D' or 'd') && char.IsAsciiDigit(name[1]))
            return name[1];
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            return name[0];
        if ((name.StartsWith('F') || name.StartsWith('f')) && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return 0x6F + f;
        if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[6..], out var pad) && pad is >= 0 and <= 9)
            return 0x60 + pad;
        return Named.TryGetValue(name, out var code) ? code : null;
    }

    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Back"] = 0x08,
        ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["Prior"] = 0x21,
        ["PageDown"] = 0x22, ["Next"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C, ["Snapshot"] = 0x2C, ["Pause"] = 0x13, ["Scroll"] = 0x91, ["Apps"] = 0x5D,
        ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Separator"] = 0x6C, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["OemSemicolon"] = 0xBA, ["Oem1"] = 0xBA, ["OemPlus"] = 0xBB, ["OemComma"] = 0xBC, ["OemMinus"] = 0xBD, ["OemPeriod"] = 0xBE,
        ["OemQuestion"] = 0xBF, ["Oem2"] = 0xBF, ["OemTilde"] = 0xC0, ["Oem3"] = 0xC0, ["OemOpenBrackets"] = 0xDB, ["Oem4"] = 0xDB,
        ["OemPipe"] = 0xDC, ["Oem5"] = 0xDC, ["OemCloseBrackets"] = 0xDD, ["Oem6"] = 0xDD, ["OemQuotes"] = 0xDE, ["Oem7"] = 0xDE,
        ["OemBackslash"] = 0xE2, ["Oem102"] = 0xE2,
    };
}

/// <summary>
/// Shortcuts with the Win key, caught by a low-level keyboard hook on a thread of its own: it
/// swallows the chosen combination and lets every other key through untouched.
/// </summary>
internal sealed class WinHook(Action<int> fired) : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, Injected = 0x10;
    /// <summary>A key code no keyboard has: pressed in between, it keeps the Start menu shut.</summary>
    private const byte MaskKey = 0xE8;
    private HookProc? proc;
    private (uint Modifiers, int Key, int Id)[] wanted = [];
    private Thread? thread;
    private uint threadId;
    private IntPtr hook;
    private int swallowing;

    public bool Running => hook != IntPtr.Zero;

    /// <summary>Catch these combinations. Returns false if Windows refused the hook.</summary>
    public bool Set(IReadOnlyList<(uint Modifiers, int Key, int Id)> shortcuts)
    {
        wanted = shortcuts.ToArray();
        if (wanted.Length == 0)
        {
            Stop();
            return true;
        }
        if (thread is not null)
            return Running;
        proc ??= Proc;
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
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc!, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            Log.Warning($"Win-key shortcuts: the keyboard hook was refused (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        if (hook == IntPtr.Zero)
            return;
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message != WM_APP)
                continue;
            keybd_event(MaskKey, 0, 0, IntPtr.Zero);
            keybd_event(MaskKey, 0, 2, IntPtr.Zero);
            fired((int)message.wParam);
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
            if ((flags & Injected) == 0 || key != MaskKey)
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
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        (thread, threadId) = (null, 0);
    }

    public void Dispose() => Stop();
}
