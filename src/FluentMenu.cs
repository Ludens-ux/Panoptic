using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>Menú contextual con el aspecto de los menús de Windows 11.</summary>
    internal static class FluentMenu
    {
        public static ContextMenuStrip Create(Theme th, float scale)
        {
            var m = new ContextMenuStrip();
            m.Renderer = new FluentMenuRenderer(th, scale);
            m.ShowImageMargin = true;
            m.ShowCheckMargin = false;
            m.Font = new Font(Fonts.TextFamily, 14f * scale, GraphicsUnit.Pixel);
            m.ImageScalingSize = new Size(S(16, scale), S(16, scale));
            m.Padding = new Padding(0, S(4, scale), 0, S(4, scale));
            m.BackColor = th.MenuBg;
            m.Opened += (s, e) =>
            {
                int v = th.Dark ? 1 : 0;
                Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
                v = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
                v = Native.ColorRef(th.WindowBorder);
                Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_BORDER_COLOR, ref v, 4);
            };
            return m;
        }

        public static ToolStripMenuItem Add(ContextMenuStrip m, string text, string glyph, bool enabled, Action onClick)
        {
            float scale = ((FluentMenuRenderer)m.Renderer).Scale;
            var item = new ToolStripMenuItem(text);
            item.Tag = glyph;
            item.Image = new Bitmap(S(16, scale), S(16, scale));
            item.Enabled = enabled;
            item.Padding = new Padding(S(4, scale), S(5, scale), S(24, scale), S(5, scale));
            if (onClick != null) item.Click += (s, e) => onClick();
            m.Items.Add(item);
            return item;
        }

        /// <summary>Agrega una opción al submenú de <paramref name="parent"/> (con el mismo estilo).</summary>
        public static ToolStripMenuItem AddSub(ToolStripMenuItem parent, string text, string glyph, bool enabled, Action onClick)
        {
            var owner = parent.Owner as ContextMenuStrip;
            var renderer = owner != null ? owner.Renderer as FluentMenuRenderer : null;
            float scale = renderer != null ? renderer.Scale : 1f;
            if (parent.DropDownItems.Count == 0 && renderer != null)
            {
                var dd = parent.DropDown as ToolStripDropDownMenu;
                parent.DropDown.Renderer = renderer;
                parent.DropDown.BackColor = owner.BackColor;
                parent.DropDown.Font = owner.Font;
                if (dd != null) { dd.ShowImageMargin = true; dd.ShowCheckMargin = false; dd.Padding = owner.Padding; }
                parent.DropDown.Opened += (s, e) => Round(parent.DropDown, owner);
            }
            var item = new ToolStripMenuItem(text);
            item.Tag = glyph;
            item.Image = new Bitmap(S(16, scale), S(16, scale));
            item.Enabled = enabled;
            item.Padding = new Padding(S(4, scale), S(5, scale), S(24, scale), S(5, scale));
            if (onClick != null) item.Click += (s, e) => onClick();
            parent.DropDownItems.Add(item);
            return item;
        }

        static void Round(ToolStripDropDown d, ContextMenuStrip owner)
        {
            var r = owner.Renderer as FluentMenuRenderer;
            if (r == null) return;
            int v = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(d.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
        }

        public static void Separator(ContextMenuStrip m)
        {
            m.Items.Add(new ToolStripSeparator());
        }

        static int S(float v, float scale) { return (int)Math.Round(v * scale); }
    }

    internal sealed class FluentMenuRenderer : ToolStripRenderer
    {
        readonly Theme th;
        public readonly float Scale;
        Font glyphFont;

        public FluentMenuRenderer(Theme th, float scale)
        {
            this.th = th;
            Scale = scale;
            glyphFont = new Font(Fonts.IconFamily, 16f * scale, GraphicsUnit.Pixel);
        }

        int S(float v) { return (int)Math.Round(v * Scale); }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(th.MenuBg);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(S(4), 1, e.Item.Width - S(8), e.Item.Height - 2);
            using (var p = Theme.Round(r, S(4)))
            using (var b = new SolidBrush(th.MenuHover))
                g.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? th.Text : th.TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            var glyph = e.Item.Tag as string;
            var item = e.Item as ToolStripMenuItem;
            if (item != null && item.Checked) glyph = Glyphs.CheckMark;
            if (string.IsNullOrEmpty(glyph)) return;
            var r = e.ImageRectangle;
            r.Offset(S(6), 0);
            TextRenderer.DrawText(e.Graphics, glyph, glyphFont, r, e.Item.Enabled ? th.Text : th.TextDisabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            OnRenderItemImage(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(th.Dark ? Theme.Hex(0x3D3D3D) : Theme.Hex(0xE5E5E5)))
                e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }
    }
}
