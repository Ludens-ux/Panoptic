using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>Tabla genérica con columnas ordenables, selección, desplazamiento y menú contextual.</summary>
    internal sealed partial class MainWindow
    {
        sealed class Column
        {
            public string Title;
            public int Width;                       // 0 = ocupa el espacio restante
            public bool Right;
            public Func<object, string> Text;
            public Func<object, IComparable> Sort;
            public Func<object, Color?> Color;
        }

        readonly Dictionary<string, int> tableSort = new Dictionary<string, int>();
        readonly Dictionary<string, bool> tableAsc = new Dictionary<string, bool>();
        readonly Dictionary<string, string> tableSel = new Dictionary<string, string>();

        string Selected(string table) { string v; return tableSel.TryGetValue(table, out v) ? v : null; }

        static Column Col(string title, int width, bool right, Func<object, string> text)
        {
            return new Column { Title = title, Width = width, Right = right, Text = text };
        }

        /// <summary>Dibuja la tabla en <paramref name="area"/> (incluye encabezados). Devuelve la fila seleccionada.</summary>
        object Table(Graphics g, Rectangle area, string id, List<Column> cols, List<object> rows, Func<object, string> rowKey,
            Func<object, Bitmap> icon, Func<object, string> glyph, Action<object, Point> menu, string empty)
        {
            // Anchos: las columnas fijas a la derecha, la flexible ocupa el resto.
            // Si no entra todo, las columnas fijas se achican para dejar al menos 180 px a la principal.
            // Primero se achican las columnas anchas (direcciones, nombres); las cortas (PID, protocolo, números)
            // solo si aun así no entra.
            int avail = area.Width - S(36);
            int bigW = cols.Where(c => c.Width >= 150).Sum(c => S(c.Width));
            int smallW = cols.Where(c => c.Width > 0 && c.Width < 150).Sum(c => S(c.Width));
            int over = bigW + smallW + S(180) - avail;
            if (over > 0) over -= S(40); // la columna principal puede bajar a 140 px
            float bigShrink = over > 0 && bigW > 0 ? Math.Max(0.35f, (bigW - over) / (float)bigW) : 1f;
            over -= bigW - (int)(bigW * bigShrink);
            float smallShrink = over > 0 && smallW > 0 ? Math.Max(0.85f, (smallW - over) / (float)smallW) : 1f;
            Func<Column, int> width = c => (int)(S(c.Width) * (c.Width >= 150 ? bigShrink : smallShrink));
            int fixedW = cols.Where(c => c.Width > 0).Sum(width);
            var rects = new Rectangle[cols.Count];
            int x = area.X + S(12);
            for (int i = 0; i < cols.Count; i++)
            {
                int w = cols[i].Width > 0 ? width(cols[i]) : Math.Max(S(140), avail - fixedW);
                rects[i] = new Rectangle(x, 0, w, 0);
                x += w;
            }

            int sortCol;
            if (!tableSort.TryGetValue(id, out sortCol)) sortCol = 0;
            bool asc;
            if (!tableAsc.TryGetValue(id, out asc)) asc = true;

            // Encabezados
            int hy = area.Y;
            for (int i = 0; i < cols.Count; i++)
            {
                var r = new Rectangle(rects[i].X, hy, rects[i].Width, S(36));
                string hk = id + "-col" + i;
                int col = i;
                AddHit(hk, r, () =>
                {
                    int cur; if (!tableSort.TryGetValue(id, out cur)) cur = 0;
                    bool a; if (!tableAsc.TryGetValue(id, out a)) a = true;
                    if (cur == col) tableAsc[id] = !a; else { tableSort[id] = col; tableAsc[id] = true; }
                    Invalidate();
                }, "Ordenar por " + cols[i].Title).Role = AccessibleRole.ColumnHeader;
                HoverFill(g, hk, r, S(4));
                var flags = cols[i].Right ? TextFormatFlags.Right | TextFormatFlags.VerticalCenter : TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
                DrawText(g, cols[i].Title, fonts.Caption, new Rectangle(r.X + S(10), r.Y, r.Width - S(20), r.Height), th.TextSecondary, flags);
                if (sortCol == i)
                    Glyph(g, asc ? Glyphs.ChevronUp : Glyphs.ChevronDown, fonts.Glyph12, new Rectangle(r.X + r.Width / 2 - S(8), r.Y + S(1), S(16), S(10)), th.TextSecondary);
            }
            LineP(g, ControlStroke, 1, area.X + S(12), hy + S(36), area.Right - S(12), hy + S(36));

            // Orden
            var c0 = cols[Math.Min(sortCol, cols.Count - 1)];
            Func<object, IComparable> key = c0.Sort ?? (o => (c0.Text(o) ?? "").ToLowerInvariant());
            var sorted = asc ? rows.OrderBy(key).ToList() : rows.OrderByDescending(key).ToList();

            listView = new Rectangle(area.X, hy + S(37), area.Width, area.Bottom - hy - S(37));
            int rowH = S(32);
            listContent = sorted.Count * rowH + S(8);
            ClampScroll();
            string sel = Selected(id);
            object selected = null;
            var state = ClipP(g, listView);
            int y = listView.Y + S(4) - ScrollPos;
            foreach (var row in sorted)
            {
                string rk = rowKey(row);
                if (rk == sel) selected = row;
                if (y + rowH >= listView.Y && y <= listView.Bottom)
                {
                    var r = new Rectangle(area.X + S(12), y, area.Width - S(36), rowH);
                    string hk = id + "-row-" + rk;
                    var h = AddHit(hk, Rectangle.Intersect(r, listView), () => { tableSel[id] = rk; Invalidate(); }, cols[0].Text(row));
                    h.Role = AccessibleRole.Row;
                    h.Checked = rk == sel;
                    var captured = row;
                    if (menu != null) h.RightClick = pt => { tableSel[id] = rk; Invalidate(); menu(captured, pt); };
                    if (rk == sel)
                    {
                        Fill(g, r, th.Dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0), S(4));
                        Fill(g, new Rectangle(r.X, r.Y + r.Height / 2 - S(8), S(3), S(16)), th.Accent, S(1.5f));
                    }
                    else HoverFill(g, hk, r, S(4));

                    for (int i = 0; i < cols.Count; i++)
                    {
                        var cell = new Rectangle(rects[i].X + S(10), y, rects[i].Width - S(20), rowH);
                        if (i == 0)
                        {
                            var ir = new Rectangle(cell.X, y + (rowH - S(16)) / 2, S(16), S(16));
                            var bmp = icon != null ? icon(row) : null;
                            if (bmp != null) DrawIcon(g, bmp, ir);
                            else if (glyph != null) Glyph(g, glyph(row), fonts.Glyph16, ir, th.TextSecondary);
                            cell = new Rectangle(ir.Right + S(12), y, rects[i].Right - ir.Right - S(22), rowH);
                        }
                        Color? cc = cols[i].Color != null ? cols[i].Color(row) : null;
                        DrawText(g, cols[i].Text(row), fonts.Body, cell, cc ?? (i == 0 ? th.Text : th.TextSecondary),
                            cols[i].Right ? TextFormatFlags.Right | TextFormatFlags.VerticalCenter : TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    }
                }
                y += rowH;
            }
            UnclipP(g, state);
            if (sorted.Count == 0)
                DrawText(g, empty, fonts.Body, listView, th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawScrollbar(g);
            return selected;
        }

        /// <summary>Fila de chips de filtro (con salto de línea si no entran). Devuelve la altura usada.</summary>
        int Chips(Graphics g, int x0, int y, int right, string group, string[] labels, int selected, Action<int> pick)
        {
            int x = x0, rowY = y;
            for (int i = 0; i < labels.Length; i++)
            {
                int w = MeasureText(labels[i], fonts.Body) + S(28);
                if (x + w > right && x > x0) { x = x0; rowY += S(40); }
                var r = new Rectangle(x, rowY, w, S(32));
                string key = "chip-" + group + i;
                int idx = i;
                var h = AddHit(key, r, () => { pick(idx); ScrollPos = 0; Invalidate(); }, labels[i]);
                h.Checked = i == selected;
                if (i == selected) Fill(g, r, IsHover(key) ? th.AccentHover : th.Accent, r.Height / 2f);
                else
                {
                    { var rr = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1); FillRoundP(g, rr, r.Height / 2f, th.ControlFill); HoverFill(g, key, Rectangle.Round(rr), r.Height / 2f); StrokeRoundP(g, rr, r.Height / 2f, ControlStroke, 1f); }
                }
                DrawText(g, labels[i], fonts.Body, r, i == selected ? th.OnAccent : th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += w + S(8);
            }
            return rowY + S(40) - y;
        }

        void PageTitle(Graphics g, Rectangle c, string title, string subtitle)
        {
            DrawText(g, title, fonts.Subtitle, new Rectangle(c.X + S(24), c.Y + S(16), S(360), S(32)), th.Text);
            if (subtitle != null)
                DrawText(g, subtitle, fonts.Caption, new Rectangle(c.X + S(24), c.Y + S(46), c.Width - S(48), S(18)), th.TextSecondary);
        }

        ContextMenuStrip NewMenu()
        {
            var m = FluentMenu.Create(th, s);
            m.Closed += (o, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        void RunInBackground(string what, Action work, Action done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(o =>
            {
                Exception error = null;
                try { work(); } catch (Exception ex) { error = ex; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (error != null)
                            MessageBox.Show(this, what + ": " + (error.InnerException ?? error).Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (done != null) done();
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        static string Short(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return "";
            return gpuName.Replace("(R)", "").Replace("(TM)", "").Replace("Graphics", "").Trim();
        }
    }
}
