// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;

namespace PCButler.Agent;

/// <summary>The few parts of Windows the agent talks to directly (it has no UI framework).</summary>
internal static class Native
{
    public delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    public const uint WM_NULL = 0, WM_DESTROY = 0x2, WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, WM_TIMER = 0x113,
                      WM_HOTKEY = 0x312, WM_CLIPBOARDUPDATE = 0x31D, WM_APP = 0x8000, WM_QUIT = 0x12,
                      WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
                                               IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
