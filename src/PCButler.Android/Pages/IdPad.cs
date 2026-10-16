// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using KeyEvent = Android.Views.KeyEvent;
using KeyEventActions = Android.Views.KeyEventActions;
using Keycode = Android.Views.Keycode;
using PCButler.Core;

namespace PCButler.Android.Pages;

/// <summary>
/// Typing another device's ID: the digits shown big, and a keypad of the app's own under them
/// (no text box, no phone keyboard). A real keyboard works too: digits, Backspace and Delete,
/// Enter to connect, Ctrl+V to paste, Ctrl+C to copy, Ctrl+X to cut, Ctrl+A to select all
/// (the next digit then starts over), and Esc to clear.
/// </summary>
public sealed class IdPad : VerticalStackLayout
{
    private const int Most = 9; // six digits; nine for the IDs of versions up to 1.21
    private readonly Label shown = new()
    {
        FontSize = 30, FontFamily = "monospace", FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center,
        CharacterSpacing = 2, LineBreakMode = LineBreakMode.NoWrap,
    };
    private readonly Border display;
    private bool selected;

    /// <summary>The digits typed so far.</summary>
    public string Digits { get; private set; } = "";

    /// <summary>Enter on a keyboard, or the keypad's own enter.</summary>
    public event Action? Submitted;

    /// <summary>The digits changed.</summary>
    public event Action? Changed;

    public IdPad()
    {
        Spacing = 10;
        display = Theme.BoxOf(shown, 14);
        display.Padding = new Thickness(12, 16);
        Children.Add(display);
        var keys = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var column = 0; column < 3; column++)
            keys.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < 4; row++)
            keys.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var number = 1; number <= 9; number++)
        {
            var digit = (char)('0' + number);
            keys.Add(PadKey(digit.ToString(), () => Type(digit)), (number - 1) % 3, (number - 1) / 3);
        }
        keys.Add(PadKey(Tr.T("Paste"), () => _ = Paste(), small: true), 0, 3);
        keys.Add(PadKey("0", () => Type('0')), 1, 3);
        keys.Add(PadKey("⌫", Back), 2, 3);
        Children.Add(keys);
        Show();
    }

    private static Button PadKey(string text, Action action, bool small = false)
    {
        var key = Theme.Plain(text, action);
        (key.FontSize, key.HeightRequest, key.Padding, key.BackgroundColor) = (small ? 14 : 22, 54, new Thickness(0), Theme.SurfaceRaised);
        return key;
    }

    public void Clear()
    {
        (Digits, selected) = ("", false);
        Show();
    }

    private void Type(char digit)
    {
        if (selected)
            (Digits, selected) = ("", false);
        if (Digits.Length < Most)
            Digits += digit;
        Show();
    }

    private void Back()
    {
        if (selected)
            (Digits, selected) = ("", false);
        else if (Digits.Length > 0)
            Digits = Digits[..^1];
        Show();
    }

    private async Task Paste()
    {
        var text = await Clipboard.Default.GetTextAsync() ?? "";
        var digits = Portal.NormalizeNumber(text);
        if (digits.Length == 0)
            return;
        (Digits, selected) = (digits[..Math.Min(digits.Length, Most)], false);
        Show();
    }

    /// <summary>"205 937" as it is typed, with dashes for the digits still to come.</summary>
    private void Show()
    {
        var full = Digits.Length > 6 ? Most : 6;
        var text = Digits.PadRight(full, '–');
        shown.Text = full == 6 ? $"{text[..3]} {text[3..]}" : $"{text[..3]} {text[3..6]} {text[6..]}";
        shown.TextColor = Digits.Length == 0 ? Theme.Muted : Theme.Primary;
        display.BackgroundColor = selected ? AppPage.TickedBack : Theme.Surface;
        Changed?.Invoke();
    }

    /// <summary>A key of a real keyboard (a PC's, through the emulator or a phone's own): true when it was taken.</summary>
    public bool Key(KeyEvent key)
    {
        if (!IsVisible || key.Action != KeyEventActions.Down)
            return false;
        var code = key.KeyCode;
        if (key.IsCtrlPressed)
        {
            switch (code)
            {
                case Keycode.V:
                    _ = Paste();
                    return true;
                case Keycode.C:
                case Keycode.X:
                    if (Digits.Length > 0)
                        _ = Clipboard.Default.SetTextAsync(Digits);
                    if (code == Keycode.X)
                        Clear();
                    return true;
                case Keycode.A:
                    selected = Digits.Length > 0;
                    Show();
                    return true;
            }
            return false;
        }
        if (code is >= Keycode.Num0 and <= Keycode.Num9)
            Type((char)('0' + (code - Keycode.Num0)));
        else if (code is >= Keycode.Numpad0 and <= Keycode.Numpad9)
            Type((char)('0' + (code - Keycode.Numpad0)));
        else if (code is Keycode.Del or Keycode.ForwardDel)
            Back();
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
