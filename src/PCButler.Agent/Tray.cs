// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using PCButler.Core;
using static PCButler.Agent.Native;

namespace PCButler.Agent;

/// <summary>One item of the tray icon's menu.</summary>
internal sealed record MenuEntry(string Text, Action? Run, bool Checked = false, bool Enabled = true, bool Bold = false)
{
    public static readonly MenuEntry Separator = new("", null);
}

/// <summary>
/// The tray icon, through Windows' own Shell_NotifyIcon: a click opens the board, a right-click
/// its menu, and it shows notifications. Added again when Explorer restarts.
/// </summary>
internal sealed class Tray : IDisposable
{
    private const uint Callback = WM_APP + 2, IconId = 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NIN_BALLOONUSERCLICK = 0x405, NIN_BALLOONTIMEOUT = 0x404;
    private readonly MessageLoop loop;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr icon;
    private readonly bool ownIcon;
    private string tip = AppInfo.Name;
    private bool added;

    /// <summary>The icon was clicked.</summary>
    public Action? Clicked;
    /// <summary>What the menu shows, built at each right-click.</summary>
    public Func<List<MenuEntry>>? Menu;
    /// <summary>A notification was clicked.</summary>
    public Action? NoticeClicked;
    /// <summary>A notification went away unclicked.</summary>
    public Action? NoticeGone;

    public Tray(MessageLoop loop)
    {
        this.loop = loop;
        // The logo alone on a transparent background, as File Explorer's menu uses it too.
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "logo.ico"), 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        ownIcon = icon != IntPtr.Zero;
        if (!ownIcon)
            icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        loop.Listen(OnMessage);
        Add();
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = loop.Handle, uID = IconId, uFlags = flags,
        uCallbackMessage = Callback, hIcon = icon, szTip = tip, szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!added)
        {
            // Already there (the agent started again quickly): take it over.
            added = Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>The tooltip (127 characters at most).</summary>
    public void SetTip(string text)
    {
        tip = text.Length > 127 ? text[..127] : text;
        var data = Data(NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>A notification beside the icon (it stays in the notification centre).</summary>
    public void Notify(string text)
    {
        var data = Data(NIF_INFO);
        data.szInfo = text.Length > 255 ? text[..254] + "…" : text;
        data.szInfoTitle = AppInfo.Name;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
            Log.Warning("Notification failed");
    }

    private bool OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == taskbarCreated)
        {
            Add(); // Explorer restarted: the icon is gone from the new taskbar
            return true;
        }
        if (message != Callback)
            return false;
        switch ((uint)lParam.ToInt64() & 0xFFFF)
        {
            case WM_LBUTTONUP:
                Clicked?.Invoke();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
            case NIN_BALLOONUSERCLICK:
                NoticeClicked?.Invoke();
                break;
            case NIN_BALLOONTIMEOUT:
                NoticeGone?.Invoke();
                break;
        }
        return true;
    }

    private void ShowMenu()
    {
        if (Menu?.Invoke() is not { Count: > 0 } entries)
            return;
        var menu = CreatePopupMenu();
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry == MenuEntry.Separator)
                {
                    AppendMenu(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
                    continue;
                }
                var flags = (entry.Checked ? 0x8u /* MF_CHECKED */ : 0) | (entry.Enabled && entry.Run is not null ? 0 : 0x1u /* MF_GRAYED */);
                AppendMenu(menu, flags, (UIntPtr)(index + 1), entry.Text);
                if (entry.Bold)
                    SetMenuDefaultItem(menu, (uint)(index + 1), 0);
            }
            GetCursorPos(out var point);
            // The menu closes when it loses the focus only if the window behind it has it.
            SetForegroundWindow(loop.Handle);
            var flagsAt = 0x100u /* TPM_RETURNCMD */ | 0x2u /* TPM_RIGHTBUTTON */ | (Tr.IsArabic ? 0x8000u /* TPM_LAYOUTRTL */ | 0x8u /* TPM_RIGHTALIGN */ : 0);
            var chosen = TrackPopupMenuEx(menu, flagsAt, point.X, point.Y, loop.Handle, IntPtr.Zero);
            PostMessage(loop.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= entries.Count)
                entries[chosen - 1].Run?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = false;
        }
        if (ownIcon)
            DestroyIcon(icon);
    }
}
