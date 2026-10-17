// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Clipboard sharing through Courier: text copied on this PC goes to every connected PC and
/// phone, and text they send lands on this PC's clipboard, ready for Ctrl+V. On while
/// Courier's "Share the clipboard" setting is on. What password managers mark as private is
/// never sent. Lives as long as the program.
/// </summary>
public sealed class ClipboardShare
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly HwndSource source;
    private bool listening;
    private string last = ""; // the last text sent or received: the same text never goes back and forth
    private DateTime ignoreUntil;

    public ClipboardShare()
    {
        source = new HwndSource(new HwndSourceParameters("PC Butler clipboard sharing") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    /// <summary>Whether copied text is shared, read from the saved settings when Courier starts or the setting changes.</summary>
    public static bool On { get; set; } = true;

    /// <summary>Follow the clipboard, or stop.</summary>
    public void Apply(bool on)
    {
        On = on;
        if (on && !listening)
            listening = AddClipboardFormatListener(source.Handle);
        else if (!on && listening)
        {
            RemoveClipboardFormatListener(source.Handle);
            listening = false;
        }
    }

    /// <summary>Text from a PC or phone: on the clipboard. Called on Courier's thread; false when sharing is off.</summary>
    public bool Received(PortalPeer peer, string text)
    {
        if (!On)
            return false;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            last = text;
            ignoreUntil = DateTime.Now.AddMilliseconds(800);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                    App.Current.ClipboardArrived(peer.Name, text);
                    return;
                }
                catch (Exception error) when (error is COMException or ExternalException)
                {
                    Thread.Sleep(60); // another program has the clipboard open
                }
            }
            Log.Warning($"Text from {peer.Name} could not be put on the clipboard");
        });
        return true;
    }

    /// <summary>Send this text to every connected PC and phone. Returns how many took it, and the first reason one did not.</summary>
    public static async Task<(int Sent, string? Problem)> SendToAll(string text)
    {
        if (Courier.Node is not { } node)
            return (0, Ui.T("Courier is off."));
        var ready = Courier.Targets(node).Where(target => target.Ready).ToList();
        if (ready.Count == 0)
            return (0, Ui.T("No PC or phone is connected."));
        var answers = await Task.WhenAll(ready.Select(target => node.SendClipboard(target.Tag, text)));
        var problem = answers.FirstOrDefault(answer => answer is not null);
        if (problem == PortalNode.OldVersion)
            problem = Ui.T("The other side has an older PC Butler: update it to share the clipboard.");
        return (answers.Count(answer => answer is null), problem);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_CLIPBOARDUPDATE && On && DateTime.Now >= ignoreUntil && Courier.Node is not null)
            _ = Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(80); // the program that copied may still hold the clipboard
                if (Read() is not { } text || text == last)
                    return;
                last = text;
                await SendToAll(text);
            });
        return IntPtr.Zero;
    }

    /// <summary>The copied text, unless it is empty, too long, or marked private by a password manager.</summary>
    private static string? Read()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"))
                    || IsClipboardFormatAvailable(RegisterClipboardFormat("Clipboard Viewer Ignore")))
                    return null;
                if (Clipboard.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream flag && flag.Length >= 4 && new System.IO.BinaryReader(flag).ReadInt32() == 0)
                    return null;
                if (!Clipboard.ContainsText())
                    return null;
                var text = Clipboard.GetText();
                return string.IsNullOrWhiteSpace(text) || text.Length > PortalNode.MaxClipboard ? null : text;
            }
            catch (Exception error) when (error is COMException or ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
}
