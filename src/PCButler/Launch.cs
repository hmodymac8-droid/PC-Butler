// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// What this process is. PC Butler runs as several small processes: the floating board (plain
/// launch), one process per open tool (--tool &lt;id&gt;, or the tool's own PCButler.&lt;Tool&gt;.exe), and
/// the companion (--ui) that shows the background agent's windows and runs Courier. The agent
/// itself is PCButler.Agent.exe, which has no WPF at all.
/// </summary>
public enum AppMode { Board, Companion, Tool }

/// <summary>The tools the board lists, grouped as the board shows them, and how each is started.</summary>
public static class Tools
{
    /// <summary>(key, icon, name, colour, its tools) of each block on the board.</summary>
    public static readonly (string Key, string Glyph, string Name, string Color, string[] Members)[] Groups =
    [
        ("files", "", "Files", "#42d8a5", ["search", "preview", "duplicates", "storage"]),
        ("cleanup", "", "Clean up", "#5aabf1", ["cleaner", "programs", "shredder"]),
        ("speed", "", "Speed", "#edb755", ["booster", "benchmark", Overlay]),
        ("network", "", "Network", "#5cc6d6", ["internet", "lan"]),
        ("share", "", "Share & protect", "#b697f1", ["portal", "clipboard", "vault"]),
        ("history", "", "History", "#d99a6c", ["history"]),
    ];

    /// <summary>The quick search box and command palette: a process of its own with no window of its own (--popup).</summary>
    public const string Popup = Launcher.Popup;

    /// <summary>Not a window: the board's switch for the performance overlay (it runs in the companion).</summary>
    public const string Overlay = "overlay";

    /// <summary>One line about each tool: the subtitle its page already shows.</summary>
    public static readonly Dictionary<string, string> Lines = new()
    {
        ["search"] = "Every file and folder on your drives, found as you type.",
        ["preview"] = "A little organization. A lot more peace of mind.",
        ["duplicates"] = "Find extra copies. Keep what matters.",
        ["storage"] = "See what fills your disk. Decide what stays.",
        ["cleaner"] = "Clear the clutter. Make room for what’s next.",
        ["programs"] = "Everything installed, in one list.",
        ["shredder"] = "Gone means gone.",
        ["booster"] = "A lighter startup. A smoother day.",
        ["benchmark"] = "See how your PC is really doing, while you play.",
        [Overlay] = "Frames per second and temperatures over your games.",
        ["internet"] = "How fast is your connection, and what is using it?",
        ["lan"] = "Which devices are on your network?",
        ["portal"] = "Files between your PCs, across the internet.",
        ["clipboard"] = "Everything you copy, ready to paste again.",
        ["vault"] = "Your private space. Under your control.",
        ["history"] = "Every change, all in one place.",
        ["settings"] = "Your folders. Your rules.",
        ["about"] = "A little help, whenever you need it.",
    };

    /// <summary>A window this program can open with --tool.</summary>
    public static bool IsTool(string key) => ToolWindow.PageList.Any(page => page.Key == key);

    public static string Glyph(string key) =>
        key == Overlay ? "" : ToolWindow.PageList.FirstOrDefault(page => page.Key == key).Glyph ?? "";

    /// <summary>The tool's name in the user's language.</summary>
    public static string Name(string key) =>
        key == Overlay ? Ui.T("Performance overlay") : Ui.T(ToolWindow.PageList.FirstOrDefault(page => page.Key == key).Name ?? key);

    public static void Open(string key, params string[] extra) => Launcher.Open(key, extra);
    public static void OpenWith(string key, IEnumerable<string> paths) => Launcher.OpenWith(key, paths);
    public static void OpenBoard() => Launcher.OpenBoard();
    public static void OpenPopup(bool palette) => Launcher.OpenPopup(palette);
    public static void StartAgent() => Launcher.StartAgent();
    public static string? KeyOfExe(string? path) => Launcher.KeyOfExe(path);
    public static List<string> TakeList(string file) => Launcher.TakeList(file);
}

/// <summary>
/// Each tool is its own entry on the taskbar: grouped apart from the others, with its own name,
/// and pinned it starts that tool again (not the board).
/// </summary>
public static class TaskbarIdentity
{
    public static string IdFor(string tool) => "PCButler.Tool." + tool;

    /// <summary>For the whole process (notifications, jump lists). Call before any window is shown.</summary>
    public static void ForProcess(string id)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(id);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    /// <summary>For one window: its id, and what a pin of it starts.</summary>
    public static void ForWindow(IntPtr handle, string id, string relaunchArguments, string displayName)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(handle, ref iid, out var store) != 0 || store is null)
                return;
            try
            {
                var exe = Environment.ProcessPath!;
                // The relaunch details first, the id last: Windows reads them when the id is set.
                Set(store, 2, $"\"{exe}\" {relaunchArguments}");
                Set(store, 3, $"{exe},0");
                Set(store, 4, displayName);
                Set(store, 5, id);
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            Log.Warning($"Taskbar identity: {error.Message}");
        }
    }

    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static void Set(IPropertyStore store, uint id, string value)
    {
        var key = new PropertyKey { FormatId = AppUserModel, PropertyId = id };
        var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string id);
}
