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
    private const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312, HotkeyId = 0xB070;
    private readonly HwndSource source;
    private readonly KeyHook hook = new();
    private ClipboardSettings settings = new();
    private bool listening, hotkey;
    private DateTime ignoreUntil; // our own copy, when an item is pasted again
    private ClipboardPicker? picker;

    public ClipboardManager()
    {
        // HWND_MESSAGE: a window that is never shown, only there for messages.
        source = new HwndSource(new HwndSourceParameters("PC Butler clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    public ClipboardSettings Settings => settings;

    /// <summary>Apply the settings: start or stop recording, and register the shortcut. Returns an error to show, or null.</summary>
    public string? Apply(ClipboardSettings wanted)
    {
        settings = wanted;
        ClipboardHistory.Apply(wanted);
        if (wanted.Enabled && !listening)
            listening = AddClipboardFormatListener(source.Handle);
        else if (!wanted.Enabled && listening)
        {
            RemoveClipboardFormatListener(source.Handle);
            listening = false;
        }
        if (hotkey)
            UnregisterHotKey(source.Handle, HotkeyId);
        hotkey = false;
        hook.Clear();
        if (!wanted.Enabled || wanted.Shortcut.Length == 0)
            return null;
        if (!Shortcut.TryParse(wanted.Shortcut, out var modifiers, out var key))
            return Ui.T("“{0}” is not a shortcut PC Butler can use.", wanted.Shortcut);
        hotkey = RegisterHotKey(source.Handle, HotkeyId, (uint)modifiers | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key));
        // Windows keeps Win+V (its own clipboard history) and other Win shortcuts for itself: those are taken over with a keyboard hook.
        if (!hotkey && modifiers.HasFlag(ModifierKeys.Windows) && hook.Set(modifiers, key, ShowPicker))
            return null;
        return hotkey ? null : Ui.T("{0} is already used by another program. Choose another shortcut.", wanted.Shortcut);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_CLIPBOARDUPDATE)
        {
            // The program that copied may still hold the clipboard for a moment.
            _ = Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(60);
                Record();
            });
        }
        else if (message == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ShowPicker();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Open the list over the program in front, which gets the pick pasted into it.</summary>
    public void ShowPicker()
    {
        var target = GetForegroundWindow();
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

    // --- recording ---

    private void Record()
    {
        if (!settings.Enabled || DateTime.Now < ignoreUntil || Excluded())
            return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Read() is { } entry)
                    ClipboardHistory.Add(entry, settings.Limit);
                return;
            }
            catch (Exception error) when (error is COMException or ExternalException)
            {
                Thread.Sleep(50); // another program has the clipboard open
            }
        }
    }

    /// <summary>Password managers mark what they copy as not for clipboard history; that is respected.</summary>
    private static bool Excluded()
    {
        if (IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"))
            || IsClipboardFormatAvailable(RegisterClipboardFormat("Clipboard Viewer Ignore")))
            return true;
        try
        {
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream flag && flag.Length >= 4 && new BinaryReader(flag).ReadInt32() == 0)
                return true;
        }
        catch (Exception error) when (error is COMException or ExternalException)
        {
        }
        return false;
    }

    private ClipEntry? Read()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            return files.Count > 0 ? new ClipEntry { Kind = "files", Files = files } : null;
        }
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            return string.IsNullOrWhiteSpace(text) || text.Length > ClipboardHistory.MaxText ? null : new ClipEntry { Kind = "text", Text = text };
        }
        if (settings.Pictures && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            var entry = new ClipEntry { Kind = "image", Width = image.PixelWidth, Height = image.PixelHeight };
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            var bytes = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
            pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
            entry.Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (ClipboardHistory.Entries.Any(known => known.Kind == "image" && known.Hash == entry.Hash))
                return entry; // already kept: it just moves to the top
            Directory.CreateDirectory(ClipboardHistory.ImageFolder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using (var file = File.Create(entry.ImageFile))
                encoder.Save(file);
            return entry;
        }
        return null;
    }

    // --- putting an item back ---

    /// <summary>Put an item on the clipboard (it moves to the top of the history). Returns false when the clipboard was busy.</summary>
    public bool Copy(ClipEntry entry)
    {
        ignoreUntil = DateTime.Now.AddMilliseconds(800);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                switch (entry.Kind)
                {
                    case "files":
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange(entry.Files.ToArray());
                        Clipboard.SetFileDropList(list);
                        break;
                    case "image":
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.UriSource = new Uri(entry.ImageFile);
                        image.EndInit();
                        Clipboard.SetImage(image);
                        break;
                    default:
                        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, entry.Text), copy: true);
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
    public async void Paste(ClipEntry entry, IntPtr target)
    {
        if (!Copy(entry) || !settings.PasteOnPick || target == IntPtr.Zero)
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

    public void Dispose()
    {
        if (hotkey)
            UnregisterHotKey(source.Handle, HotkeyId);
        hook.Dispose();
        if (listening)
            RemoveClipboardFormatListener(source.Handle);
        source.Dispose();
    }

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
