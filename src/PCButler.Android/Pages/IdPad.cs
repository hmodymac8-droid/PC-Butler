// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using KeyEvent = Android.Views.KeyEvent;
using KeyEventActions = Android.Views.KeyEventActions;
using Keycode = Android.Views.Keycode;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Typing another device's ID: one box for the digits, with a paste button at its end. A real
/// keyboard works too while the box isn't focused: digits, Backspace and Delete, Enter to
/// connect, Ctrl+V to paste and Esc to clear.
/// </summary>
public sealed class IdPad : ContentView
{
    private const int Most = 9; // six digits; nine for the IDs of versions up to 1.21
    private readonly DigitsEntry entry = new()
    {
        FontSize = 22, FontFamily = "monospace", FontAttributes = FontAttributes.Bold, CharacterSpacing = 3, Placeholder = "000000",
        PlaceholderColor = Theme.Muted, TextColor = Theme.Primary, MaxLength = Most,
    };

    /// <summary>The digits typed so far.</summary>
    public string Digits { get; private set; } = "";

    /// <summary>Enter on a keyboard, or the phone keyboard's own Done.</summary>
    public event Action? Submitted;

    /// <summary>The digits changed.</summary>
    public event Action? Changed;

    public IdPad()
    {
        entry.TextChanged += (_, _) =>
        {
            var digits = new string((entry.Text ?? "").Where(char.IsAsciiDigit).Take(Most).ToArray());
            if (digits != (entry.Text ?? ""))
            {
                entry.Text = digits;
                return;
            }
            Digits = digits;
            Changed?.Invoke();
        };
        entry.Completed += (_, _) => Submitted?.Invoke();
        var paste = Theme.IconButton("i_clipboard_paste_g.png", () => _ = Paste(), Colors.Transparent);
        Content = Theme.Field(entry, paste);
    }

    public void Clear() => entry.Text = "";

    private void Set(string digits) => entry.Text = digits[..Math.Min(digits.Length, Most)];

    private async Task Paste()
    {
        var text = await Clipboard.Default.GetTextAsync() ?? "";
        var digits = Portal.NormalizeNumber(text);
        if (digits.Length > 0)
            Set(digits);
    }

    /// <summary>A key of a real keyboard (a PC's, through the emulator or a phone's own): true when it was taken.</summary>
    public bool Key(KeyEvent key)
    {
        // With the box focused, the box itself takes the keys.
        if (!IsVisible || entry.IsFocused || key.Action != KeyEventActions.Down)
            return false;
        var code = key.KeyCode;
        if (key.IsCtrlPressed)
        {
            if (code != Keycode.V)
                return false;
            _ = Paste();
            return true;
        }
        if (code is >= Keycode.Num0 and <= Keycode.Num9)
            Set(Digits + (char)('0' + (code - Keycode.Num0)));
        else if (code is >= Keycode.Numpad0 and <= Keycode.Numpad9)
            Set(Digits + (char)('0' + (code - Keycode.Numpad0)));
        else if (code is Keycode.Del or Keycode.ForwardDel)
            Set(Digits.Length > 0 ? Digits[..^1] : "");
        else if (code == Keycode.Escape)
            Clear();
        else if (code is Keycode.Enter or Keycode.NumpadEnter)
            Submitted?.Invoke();
        else
            return false;
        return true;
    }
}

/// <summary>The keys of a real keyboard, passed from the activity to whatever wants them (the ID pad).</summary>
public static class KeyInput
{
    public static Func<KeyEvent, bool>? Handler { get; set; }
}
