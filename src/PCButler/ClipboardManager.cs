// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// Records what is copied while the clipboard manager is on, and opens the list of it with a
/// keyboard shortcut from any program. A hidden message window receives Windows' "the
/// clipboard changed" notice and the shortcut. Lives as long as the program.
/// </summary>
public sealed class ClipboardManager : IDisposable
{
    private ClipboardSettings settings = new();
    private ClipboardPicker? picker;

    public ClipboardSettings Settings => settings;

    /// <summary>
    /// Apply the settings. The background agent (PCButler.Agent.exe) records the copies and holds
    /// the shortcut, so it is asked to; returns its error to show, or null.
    /// </summary>
    public string? Apply(ClipboardSettings wanted)
    {
        Use(wanted);
        return AgentLink.Send("apply-clipboard", 4000, System.Text.Json.JsonSerializer.Serialize(wanted)) is { Length: > 0 } problem ? problem : null;
    }

    /// <summary>Take the settings for showing the list and putting items back, without asking the agent.</summary>
    public void Use(ClipboardSettings wanted)
    {
        settings = wanted;
        ClipboardHistory.Apply(wanted);
    }

    /// <summary>True for Win+V, the shortcut of Windows' own clipboard history.</summary>
    public static bool IsWindowsHistory(string keys) =>
        Shortcut.TryParse(keys, out var modifiers, out var key) && modifiers == ModifierKeys.Windows && key == System.Windows.Input.Key.V;

    /// <summary>Open the list over the program in front, which gets the pick pasted into it.</summary>
    public void ShowPicker() => ShowPicker(GetForegroundWindow());

    /// <summary>Open the list for that window (the agent says which one was in front when its shortcut was pressed).</summary>
    public void ShowPicker(IntPtr target)
    {
        if (target == IntPtr.Zero)
            target = GetForegroundWindow();
        if (picker is { IsVisible: true })
        {
            BringToFront(picker);
            return;
        }
        picker = new ClipboardPicker(this, target);
        picker.Closed += (_, _) => picker = null;
        picker.Show();
        BringToFront(picker);
    }

    /// <summary>
    /// Opened by the keyboard hook (Win+V), Windows does not let PC Butler take the front from the
    /// program there: sharing that program's input for a moment lets the list come forward and take the keys.
    /// </summary>
    private static void BringToFront(Window window)
    {
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        var front = GetForegroundWindow();
        if (handle == IntPtr.Zero || front == handle)
            return;
        var theirs = GetWindowThreadProcessId(front, IntPtr.Zero);
        var ours = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        SetForegroundWindow(handle);
        window.Activate();
        if (attached)
            AttachThreadInput(ours, theirs, false);
    }

    // --- putting an item back ---

    /// <summary>
    /// Put an item on the clipboard (it moves to the top of the history). content: what to put
    /// there when it is not the item itself (a locked item, opened with its password for this
    /// once). Returns false when the clipboard was busy, or the item is locked.
    /// </summary>
    public bool Copy(ClipEntry entry, ClipEntry? content = null)
    {
        var what = content ?? entry;
        if (what.Hidden)
            return false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                switch (what.Kind)
                {
                    case "files":
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange(what.Files.ToArray());
                        Clipboard.SetFileDropList(list);
                        break;
                    case "image":
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        // A locked picture, unlocked, is only in memory.
                        if (what.Picture is { } picture)
                            image.StreamSource = new MemoryStream(picture);
                        else
                            image.UriSource = new Uri(what.ImageFile);
                        image.EndInit();
                        Clipboard.SetImage(image);
                        break;
                    default:
                        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, what.Text), copy: true);
                        break;
                }
                ClipboardHistory.Touch(entry);
                return true;
            }
            catch (Exception error) when (error is COMException or ExternalException)
            {
                Thread.Sleep(50);
            }
            catch (Exception error) when (error is IOException or NotSupportedException)
            {
                Log.Warning($"A clipboard item could not be put back: {error.Message}");
                return false;
            }
        }
        return false;
    }

    /// <summary>Copy the item, bring the earlier window back and press Ctrl+V in it.</summary>
    public async void Paste(ClipEntry entry, IntPtr target, ClipEntry? content = null)
    {
        if (!Copy(entry, content) || !settings.PasteOnPick || target == IntPtr.Zero)
            return;
        SetForegroundWindow(target);
        await Task.Delay(150);
        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true),
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Warning($"Paste: SendInput was refused (error {Marshal.GetLastWin32Error()})");
    }

    public void Dispose() => picker?.Close();

    // --- Windows ---

    private const uint MOD_NOREPEAT = 0x4000;
    private const ushort VK_CONTROL = 0x11, VK_V = 0x56;

    private static INPUT Key(ushort key, bool up) => new()
    {
        Type = 1, // keyboard
        Union = new InputUnion { Keyboard = new KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } },
    };

    // INPUT is a type and a union whose largest member is MOUSEINPUT; SendInput refuses a wrong size (40 bytes on 64-bit).
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

    internal static int InputSize => Marshal.SizeOf<INPUT>();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
}

/// <summary>Shortcuts as text ("Win+V", "Ctrl+Alt+V") and back.</summary>
public static class Shortcut
{
    /// <summary>
    /// The modifier keys held now, for recording a shortcut. WPF can miss the Win key (Windows
    /// handles it first), so it is read straight from Windows.
    /// </summary>
    public static ModifierKeys Held()
    {
        var held = Keyboard.Modifiers;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            held |= ModifierKeys.Windows;
        return held;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !Enum.TryParse(part, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }
        // A plain letter alone would take that letter away from every program.
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
