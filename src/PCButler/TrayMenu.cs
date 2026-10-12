// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 hmodymac8-droid
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using PCButler.Core;
using Forms = System.Windows.Forms;

namespace PCButler;

/// <summary>
/// The tray icon's right-click menu, in the program's dark look: the state of automatic
/// sorting at the top, then the actions, each with its icon. Arabic reads right to left.
/// </summary>
public static class TrayMenu
{
    private static readonly Color Back = Color.FromArgb(0x14, 0x15, 0x17);
    private static readonly Color Line = Color.FromArgb(0x2a, 0x2b, 0x2e);
    private static readonly Color Hover = Color.FromArgb(0x24, 0x27, 0x2a);
    private static readonly Color Text = Color.FromArgb(0xe9, 0xeb, 0xee);
    private static readonly Color Muted = Color.FromArgb(0xa7, 0xab, 0xb1);
    private static readonly Color Dim = Color.FromArgb(0x5c, 0x5f, 0x64);
    private static readonly Color Primary = Color.FromArgb(0x48, 0xd7, 0xa1);
    private static readonly Color Amber = Color.FromArgb(0xe6, 0xb5, 0x5d);
    private static readonly Color Gray = Color.FromArgb(0x7a, 0x7d, 0x82);

    /// <summary>One menu entry: its icon (a Segoe Fluent Icons code), text, and what it does.</summary>
    public sealed record Entry(string Glyph, string Text, Action Run, bool Enabled = true, bool Main = false);

    /// <summary>Give the menu its look once; Fill replaces its items each time it opens.</summary>
    public static void Style(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.ShowImageMargin = true;
        menu.BackColor = Back;
        menu.ForeColor = Text;
        menu.Padding = new Forms.Padding(4, 6, 4, 6);
        menu.HandleCreated += (_, _) =>
        {
            // Windows 11: rounded corners and a border in the program's line colour.
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int));
            var border = ColorTranslator.ToWin32(Line);
            DwmSetWindowAttribute(menu.Handle, 34, ref border, sizeof(int));
        };
    }

    /// <summary>
    /// Fill the menu: the state line ("Automatic sorting on", with its coloured dot), then the
    /// actions; null in actions draws a separator.
    /// </summary>
    public static void Fill(Forms.ContextMenuStrip menu, Config config, IEnumerable<Entry?> entries)
    {
        var scale = menu.DeviceDpi / 96f;
        var font = new Font("Segoe UI", 9.75f);
        menu.Font = font;
        menu.RightToLeft = Tr.IsArabic ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
        menu.ImageScalingSize = new Size((int)(18 * scale), (int)(18 * scale));
        foreach (Forms.ToolStripItem old in menu.Items)
            old.Image?.Dispose();
        menu.Items.Clear();

        var (state, color) = !config.AutoSortOn ? ("Automatic sorting off", Gray)
                           : config.IsPaused ? ("Automatic sorting paused", Amber)
                           : ("Automatic sorting on", Primary);
        menu.Items.Add(new Forms.ToolStripMenuItem(Ui.T(state))
        {
            Image = Glyph("●", color, scale, symbol: false), Tag = "state", // enabled, so Windows keeps the dot in colour; clicking it does nothing
            Padding = new Forms.Padding(0, 3, 0, 3),
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var action in entries)
        {
            if (action is null)
            {
                menu.Items.Add(new Forms.ToolStripSeparator());
                continue;
            }
            var item = new Forms.ToolStripMenuItem(action.Text)
            {
                Image = Glyph(action.Glyph, action.Enabled ? (action.Main ? Primary : Muted) : Dim, scale),
                Enabled = action.Enabled, Padding = new Forms.Padding(0, 4, 0, 4),
            };
            if (action.Main)
                item.Font = new Font(font, FontStyle.Bold);
            item.Click += (_, _) => action.Run();
            menu.Items.Add(item);
        }
    }

    /// <summary>An icon drawn from the icon font, so it matches the window's icons.</summary>
    private static Bitmap Glyph(string code, Color color, float scale, bool symbol = true)
    {
        var size = (int)Math.Round(18 * scale);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(symbol ? IconFamily.Value : "Segoe UI", (symbol ? 15 : 11) * scale, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(code, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    private static readonly Lazy<string> IconFamily = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(family => family.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    });

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    /// <summary>Dark background, rounded highlight, muted text for what can't be clicked.</summary>
    private sealed class Renderer : Forms.ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(Back);

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            // Windows draws the border (see Style); nothing to add here.
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || e.Item.Tag as string == "state")
                return;
            var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = Rounded(bounds, (int)(4 * (e.Item.Owner?.DeviceDpi ?? 96) / 96f));
            using var brush = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Tag as string == "state" ? Muted : e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class Colors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }
}
