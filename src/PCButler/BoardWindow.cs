// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The floating board: what a plain launch of PC Butler shows. Six blocks (Files, Clean up,
/// Speed, Network, Share &amp; protect, History); a click opens a block in place to show its
/// tools, and a click on a tool starts it in a window of its own and closes the board, process
/// and all. Esc or a click anywhere else closes it too. The footer says what runs in the background.
/// </summary>
public sealed class BoardWindow : Window
{
    private readonly ContentControl body = new();
    private readonly StackPanel links;
    private readonly TextBox find;
    private readonly bool firstStart;
    private bool wasActive, leaving, closing, refocused;
    private long activeSince;

    public BoardWindow(bool firstStart)
    {
        this.firstStart = firstStart;
        Ui.StyleWindow(this);
        Title = AppInfo.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Math.Min(820, SystemParameters.WorkArea.Width - 40);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Paint("Card");
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(1);

        // The header: logo, name and a line, then the tool finder and Settings.
        var header = new DockPanel { Margin = new Thickness(24, 20, 24, 16), LastChildFill = false };
        var logo = new Image { Source = Ui.AppIcon(), Width = 38, Height = 38 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text(AppInfo.Name, 17, weight: FontWeights.Bold));
        names.Children.Add(Ui.Muted(Ui.T("Pick a tool. The board steps aside."), 11.5));
        header.Children.Add(Ui.Row(0, logo, names));
        var settings = new Button
        {
            Content = Ui.Glyph("", 16, "Text"), ToolTip = Ui.T("Settings"), Width = 38, Height = 34, Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
        };
        settings.Click += (_, _) => Launch("settings");
        header.Children.Add(settings.Dock(Dock.Right));
        find = Ui.Field("", 240, Ui.T("Find a tool…"));
        find.VerticalAlignment = VerticalAlignment.Center;
        find.TextChanged += (_, _) => ShowFound();
        find.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Found(find.Text.Trim()).FirstOrDefault() is { } first)
            {
                e.Handled = true;
                Launch(first);
            }
        };
        header.Children.Add(Ui.Row(8, Ui.Glyph("", 14), find).Dock(Dock.Right));

        body.Margin = new Thickness(18, 0, 18, 0);
        body.MinHeight = 330;

        // The footer: what runs in the background, then What's new and Help.
        var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 14), LastChildFill = true };
        var running = AgentNeeds.Of(ConfigStore.Load()).Select(name => Ui.T(name)).ToList();
        links = Ui.Row(18, WhatsNewLink(), Link(Ui.T("Help & about"), () => Launch("about")));
        footer.Children.Add(links.Dock(Dock.Right));
        var state = Ui.Muted(running.Count > 0 ? Ui.T("Running in the background: {0}", string.Join(Tr.IsArabic ? "، " : ", ", running))
                                               : Ui.T("Nothing runs in the background."), 11.5);
        state.TextTrimming = TextTrimming.CharacterEllipsis;
        state.ToolTip = state.Text;
        footer.Children.Add(Ui.Row(8, Ui.Text("●", 9, running.Count > 0 ? "Primary" : "Dim"), state));

        var frame = new DockPanel();
        frame.Children.Add(header.Dock(Dock.Top));
        frame.Children.Add(new Border { BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer, Margin = new Thickness(0, 16, 0, 0) }
                               .Dock(Dock.Bottom));
        frame.Children.Add(body);
        Content = frame;

        ShowBlocks();
        SourceInitialized += (_, _) =>
        {
            // Windows 11 rounds the corners of a window without a title bar only when asked.
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        Loaded += (_, _) =>
        {
            Bring();
            LookForUpdate();
        };
        Activated += (_, _) =>
        {
            if (!wasActive)
                activeSince = Environment.TickCount64;
            wasActive = true;
        };
        Closing += (_, _) => closing = true;
        // A click anywhere else closes it, like a menu.
        Deactivated += (_, _) =>
        {
            if (!wasActive || leaving || closing)
                return;
            // Focus taken away the moment it opened (the program that started it was not in front):
            // that was no click elsewhere, so it asks for the focus back once instead of closing.
            if (!refocused && Environment.TickCount64 - activeSince < 800)
            {
                refocused = true;
                Dispatcher.BeginInvoke(Bring, System.Windows.Threading.DispatcherPriority.Input);
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!closing)
                    Close();
            });
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!closing)
                    Close();
            }
        };
        // Dragged by any empty spot.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or DockPanel or Grid or StackPanel && e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        };
    }

    /// <summary>Bring the board forward (a second launch, the tray icon), ready to type in.</summary>
    public void Bring()
    {
        if (!IsVisible)
            Show();
        Activate();
        find.Focus();
        Keyboard.Focus(find);
    }

    // --- the views ---

    /// <summary>The six blocks.</summary>
    private void ShowBlocks()
    {
        var grid = new UniformGrid { Columns = 3 };
        foreach (var group in Tools.Groups)
            grid.Children.Add(Block(group));
        body.Content = grid;
    }

    private Border Block((string Key, string Glyph, string Name, string Color, string[] Members) group)
    {
        var count = group.Members.Length;
        var top = new DockPanel { LastChildFill = false };
        top.Children.Add(Tile(group.Glyph, group.Color, 42, 20));
        top.Children.Add(Ui.Muted(count == 1 ? Ui.T("1 tool") : Ui.T("{0} tools", count), 11.5).Dock(Dock.Right));
        var line = Ui.Muted(string.Join(" · ", group.Members.Select(Tools.Name)), 12);
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = new StackPanel { Children = { top, Ui.Text(Ui.T(group.Name), 16, weight: FontWeights.Bold).Margin(0, 14), line.Margin(0, 4) } };
        return Card(content, group.Color, new Thickness(18, 16, 18, 16), () =>
        {
            if (count == 1)
                Launch(group.Members[0]);
            else
                ShowGroup(group.Key);
        }, Ui.T(group.Name));
    }

    /// <summary>One block opened in place: its tools as cards, the other blocks as a strip of buttons below.</summary>
    private void ShowGroup(string key)
    {
        var group = Tools.Groups.First(each => each.Key == key);
        var view = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        var back = new Button
        {
            Content = Ui.Glyph(Tr.IsArabic ? "" : "", 14, "Text"), ToolTip = Ui.T("Back"), Width = 36, Height = 32,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 12, 0),
        };
        back.Click += (_, _) =>
        {
            find.Text = "";
            ShowBlocks();
        };
        head.Children.Add(back);
        head.Children.Add(Tile(group.Glyph, group.Color, 34, 16));
        head.Children.Add(Ui.Text(Ui.T(group.Name), 18, weight: FontWeights.Bold).Margin(12));
        head.Children.Add(Ui.Muted(Ui.T("{0} tools", group.Members.Length), 12).Margin(10));
        view.Children.Add(head.Dock(Dock.Top));

        var others = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var other in Tools.Groups.Where(each => each.Key != key))
        {
            var button = new Button
            {
                Content = Ui.Row(7, Ui.Glyph(other.Glyph, 13), Ui.Text(Ui.T(other.Name), 12.5)),
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6),
            };
            ((TextBlock)((StackPanel)button.Content).Children[0]).Foreground = Ui.Hex(other.Color);
            var target = other;
            button.Click += (_, _) =>
            {
                if (target.Members.Length == 1)
                    Launch(target.Members[0]);
                else
                    ShowGroup(target.Key);
            };
            others.Children.Add(button);
        }
        view.Children.Add(others.Dock(Dock.Bottom));

        var tools = new UniformGrid { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
        foreach (var tool in group.Members)
            tools.Children.Add(ToolCard(tool, group.Color));
        view.Children.Add(tools);
        body.Content = view;
    }

    /// <summary>The tools whose name or line holds what was typed.</summary>
    private void ShowFound()
    {
        var text = find.Text.Trim();
        if (text.Length == 0)
        {
            ShowBlocks();
            return;
        }
        var found = Found(text).ToList();
        if (found.Count == 0)
        {
            var none = Ui.Muted(Ui.T("No tool matches “{0}”.", text), 14);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.VerticalAlignment = VerticalAlignment.Center;
            body.Content = none;
            return;
        }
        var grid = new UniformGrid { Columns = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var tool in found)
            grid.Children.Add(ToolCard(tool, ColorOf(tool)));
        body.Content = grid;
    }

    private static IEnumerable<string> Found(string text)
    {
        if (text.Length == 0)
            yield break;
        var all = Tools.Groups.SelectMany(group => group.Members.Select(member => (member, group.Name))).Append(("settings", "")).Append(("about", ""));
        foreach (var (tool, group) in all)
        {
            var english = ToolWindow.PageList.FirstOrDefault(page => page.Key == tool).Name ?? tool;
            string[] words = [Tools.Name(tool), english, Ui.T(Tools.Lines.GetValueOrDefault(tool, "")), Tools.Lines.GetValueOrDefault(tool, ""),
                              group.Length > 0 ? Ui.T(group) : "", group];
            if (words.Any(word => word.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
                yield return tool;
        }
    }

    private static string ColorOf(string tool) =>
        Tools.Groups.FirstOrDefault(group => group.Members.Contains(tool)).Color ?? "#42d8a5";

    private Border ToolCard(string tool, string color)
    {
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(Tools.Name(tool), 14.5, weight: FontWeights.Bold);
        if (tool == Tools.Overlay)
        {
            var on = ConfigStore.Load().Overlay.Enabled;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0),
                Background = on ? Ui.Paint("PrimaryTint") : Ui.Paint("Raised"),
                Child = Ui.Text(Ui.T(on ? "On" : "Off"), 10.5, on ? "Primary" : "Muted"),
            };
            text.Children.Add(Ui.Row(0, name, pill));
        }
        else
            text.Children.Add(name);
        text.Children.Add(Ui.Muted(Ui.T(Tools.Lines.GetValueOrDefault(tool, "")), 11.5, wrap: true).Margin(0, 3));
        var row = new DockPanel();
        row.Children.Add(Tile(Tools.Glyph(tool), color, 36, 17).Dock(Dock.Left));
        row.Children.Add(text);
        return Card(row, color, new Thickness(14, 14, 14, 14), () => Launch(tool), Tools.Name(tool));
    }

    // --- pieces ---

    private static Border Tile(string glyph, string color, double size, double glyphSize)
    {
        var mark = Ui.Glyph(glyph, glyphSize, "Primary");
        mark.Foreground = Ui.Hex(color);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4),
            Background = new SolidColorBrush(Ui.Mix(color, "#181b1f", 0.16)), Child = mark, VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>A rounded card that lights up in its colour under the mouse, and acts on a click, Enter or Space.</summary>
    private static Border Card(UIElement content, string color, Thickness padding, Action click, string name)
    {
        var card = new Border
        {
            Background = Ui.Paint("Secondary"), BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = padding, Margin = new Thickness(6), Child = content,
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, name);
        var lit = Ui.Hex(color);
        card.MouseEnter += (_, _) => card.BorderBrush = lit;
        card.MouseLeave += (_, _) => card.BorderBrush = card.IsKeyboardFocused ? lit : Ui.Paint("Line");
        card.GotKeyboardFocus += (_, _) => card.BorderBrush = lit;
        card.LostKeyboardFocus += (_, _) => card.BorderBrush = card.IsMouseOver ? lit : Ui.Paint("Line");
        var pressed = false;
        card.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            e.Handled = true; // not a drag of the window
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed)
                return;
            pressed = false;
            e.Handled = true;
            click();
        };
        card.MouseLeave += (_, _) => pressed = false;
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                click();
            }
        };
        return card;
    }

    private static TextBlock Link(string text, Action click, string brush = "Muted")
    {
        var link = Ui.Text(text, 11.5, brush);
        link.Cursor = Cursors.Hand;
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        link.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return link;
    }

    /// <summary>"What's new", in the accent while this version's list has not been seen yet.</summary>
    private TextBlock WhatsNewLink()
    {
        var seen = ConfigStore.Load().LastSeenVersion;
        var fresh = seen != AppInfo.Version && Changelog.ShouldShow(seen, firstStart);
        return Link(fresh ? Ui.T("What's new in {0}", AppInfo.Version) : Ui.T("What's new"),
                    () => Launch("about", "--whats-new"), fresh ? "Primary" : "Muted");
    }

    /// <summary>
    /// A newer version: the agent's last check says so (it looks every few hours), or, when no
    /// agent runs, a quick look now. Shown as a link in the footer that opens Help &amp; about.
    /// </summary>
    private void LookForUpdate()
    {
        Task.Run(() =>
        {
            var version = AgentLink.Running ? AgentLink.Send("update", 1000) : Updater.CheckForUpdate()?.Version;
            if (string.IsNullOrEmpty(version) || version == AppInfo.Version)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!closing)
                    links.Children.Insert(0, Link(Ui.T("Update to {0}", version), () => Launch("about", "--check-updates"), "Amber"));
            });
        });
    }

    // --- leaving ---

    /// <summary>Start the tool and close the board (its process ends with it).</summary>
    private void Launch(string tool, params string[] extra)
    {
        if (leaving)
            return;
        leaving = true;
        if (tool == Tools.Overlay)
        {
            // Not a window: the switch of the overlay, which the background agent runs.
            var config = ConfigStore.Load();
            config.Overlay.Enabled = !config.Overlay.Enabled;
            ConfigStore.Save(config);
            AgentLink.ConfigChanged();
            // Give the message a moment to leave before the process ends.
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                Close();
            };
            Hide();
            later.Start();
            return;
        }
        Tools.Open(tool, extra);
        if (!closing)
            Close();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
