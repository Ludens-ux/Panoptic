using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>Panel compacto que se abre desde el área de notificación.</summary>
    internal sealed class TrayFlyout : Surface
    {
        const int LogicalWidth = 380;
        readonly IHost host;
        readonly Timer refresh = new Timer();
        bool sortByMemory;
        ContextMenuStrip openMenu;
        int suppressHide;
        public DateTime LastHidden = DateTime.MinValue;

        // animación de entrada
        readonly FrameLoop slide;
        Point slideFrom, slideTo;
        double slideStart;
        Screen screen;

        public TrayFlyout(IHost host)
        {
            this.host = host;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = AppInfo.Name;
            refresh.Interval = 1000;
            refresh.Tick += (o, e) => Invalidate();
            slide = new FrameLoop(this, OnSlide); // sincronizado con el refresco de la pantalla
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void ApplyDwm()
        {
            base.ApplyDwm();
            if (!IsHandleCreated) return;
            int v = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
            v = Native.ColorRef(th.WindowStroke);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, 4);
        }

        // ------------------------------------------------------------------ mostrar / ocultar

        public void ShowAt(Point cursor)
        {
            ReloadTheme();
            SetScale(Native.ScaleForPoint(cursor));
            screen = Screen.FromPoint(cursor);
            focusKey = null; focusVisible = false; hoverKey = null;
            Point off;
            var r = FlyoutBounds(out off);
            bool animate = Native.AnimationsEnabled();
            slideTo = r.Location;
            slideFrom = animate ? new Point(r.X + off.X, r.Y + off.Y) : r.Location;
            SetBounds(slideFrom.X, slideFrom.Y, r.Width, r.Height);
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            refresh.Start();
            if (animate) { slideStart = Clock.Ms; slideK = 0; slideV = 0; slide.Start(); }
        }

        internal void PrepareRender() { ClientSize = new Size(S(LogicalWidth), ContentHeight); }

        public void HideFlyout()
        {
            if (!Visible) return;
            if (openMenu != null) openMenu.Close();
            slide.Stop();
            refresh.Stop();
            Hide();
            LastHidden = DateTime.UtcNow;
        }

        int ContentHeight { get { return S(72) + S(310) + S(42) + S(5 * 36 + 8) + S(42) + S(4 * 30 + 8) + S(12) + S(48); } }

        Rectangle FlyoutBounds(out Point offset)
        {
            Rectangle wa = screen.WorkingArea, b = screen.Bounds;
            int m = S(12), d = S(40), w = S(LogicalWidth);
            int h = Math.Min(ContentHeight, wa.Height - 2 * m);
            int x = wa.Right - w - m, y = wa.Bottom - h - m;
            offset = new Point(0, d);
            if (wa.Top > b.Top) { y = wa.Top + m; offset = new Point(0, -d); }
            else if (wa.Left > b.Left) { x = wa.Left + m; offset = new Point(-d, 0); }
            else if (wa.Right < b.Right) offset = new Point(d, 0);
            else if (wa.Bottom == b.Bottom) y = wa.Bottom - h - S(56);
            return new Rectangle(x, y, w, h);
        }

        // Entrada con un resorte (sin rebote visible): arranca rápido y se asienta suave.
        float slideK, slideV;

        void OnSlide()
        {
            double now = Clock.Ms;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, (now - slideStart) / 1000.0));
            slideStart = now;
            float w = (float)(2 * Math.PI / 0.4), kk = w * w, c = 2 * w * 0.9f;
            int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004f));
            for (int i = 0; i < n; i++) { float a = -kk * (slideK - 1) - c * slideV; slideV += a * dt / n; slideK += slideV * dt / n; }
            if (Math.Abs(slideK - 1) < 0.002f && Math.Abs(slideV) < 0.02f) { slideK = 1; slideV = 0; slide.Stop(); }
            float k = slideK;
            Location = new Point(slideFrom.X + (int)Math.Round((slideTo.X - slideFrom.X) * k),
                                 slideFrom.Y + (int)Math.Round((slideTo.Y - slideFrom.Y) * k));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (openMenu != null || suppressHide > 0) return;
            HideFlyout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HideFlyout(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowMenu(ContextMenuStrip m, Point clientPt, bool above)
        {
            openMenu = m;
            m.Closed += (o, e) =>
            {
                if (openMenu == m) openMenu = null;
                BeginInvoke(new Action(() =>
                {
                    m.Dispose();
                    if (Visible && Form.ActiveForm != this && suppressHide == 0) HideFlyout();
                }));
            };
            m.Show(this, clientPt, above ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.BelowLeft);
        }

        void Open(string page)
        {
            HideFlyout();
            host.OpenMain(page);
        }

        // ------------------------------------------------------------------ pintado

        protected override void PaintSurface(Graphics g)
        {
            g.Clear(th.Clear);
            var snap = host.Latest;
            int W = ClientSize.Width, pad = S(12);
            int y = 0;

            // ---- Encabezado
            var tile = new Rectangle(S(16), S(16), S(40), S(40));
            Fill(g, tile, th.Accent, S(4));
            Glyph(g, Glyphs.Performance, fonts.Glyph20, tile, th.OnAccent);
            DrawText(g, AppInfo.Name, fonts.BodyStrong, new Rectangle(S(68), S(17), W - S(130), S(20)), th.Text);
            string sub = snap == null ? "Midiendo…" : "Encendido hace " + Format.Duration(snap.Uptime) + " · " + snap.ProcessCount + " procesos";
            DrawText(g, sub, fonts.Caption, new Rectangle(S(68), S(38), W - S(130), S(18)), th.TextSecondary);
            SubtleButton(g, "open", new Rectangle(W - S(12) - S(40), S(20), S(40), S(32)), Glyphs.OpenExternal, null,
                "Abrir Panoptic", () => Open(null));
            y = S(72);

            // ---- Mosaicos
            int gap = S(8);
            int tw = (W - 2 * pad - gap) / 2, tH = S(98);
            if (snap != null)
            {
                var disk = snap.Disks.OrderByDescending(d => d.Active).FirstOrDefault();
                var net = snap.Nets.OrderByDescending(n => n.SendBps + n.RecvBps).FirstOrDefault();
                Tile(g, "t-cpu", new Rectangle(pad, y, tw, tH), Glyphs.Cpu, "CPU", Format.Percent(snap.Cpu),
                    snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : snap.Logical + " procesadores lógicos", snap.CpuHistory, 100, ResColors.Cpu(th), "cpu");
                Tile(g, "t-mem", new Rectangle(pad + tw + gap, y, tw, tH), Glyphs.Memory, "Memoria", Format.Percent(snap.MemPercent),
                    Format.Bytes(snap.MemUsed) + " de " + Format.Bytes(snap.MemTotal), snap.MemHistory, 100, ResColors.Memory(th), "mem");
                if (disk != null)
                    Tile(g, "t-disk", new Rectangle(pad, y + tH + gap, tw, tH), Glyphs.HardDrive, disk.Title, Format.Percent(disk.Active),
                        "L " + Format.Rate(disk.ReadBps) + " · E " + Format.Rate(disk.WriteBps), disk.ActivityHistory, 100, ResColors.Disk(th), "disk");
                if (net != null)
                {
                    float[] hist = new float[History.Size];
                    float max = 1;
                    for (int i = 0; i < hist.Length; i++) { hist[i] = net.SendHistory[i] + net.RecvHistory[i]; max = Math.Max(max, hist[i]); }
                    Tile(g, "t-net", new Rectangle(pad + tw + gap, y + tH + gap, tw, tH), net.Kind == "Wi-Fi" ? Glyphs.Wifi : Glyphs.Ethernet,
                        net.Kind, Format.Bits(net.SendBps + net.RecvBps), "↓ " + Format.Bits(net.RecvBps) + " · ↑ " + Format.Bits(net.SendBps),
                        hist, max * 1.2f, ResColors.Net(th), "net");
                }
                else Tile(g, "t-net", new Rectangle(pad + tw + gap, y + tH + gap, tw, tH), Glyphs.Ethernet, "Red", "Sin conexión", "", null, 1, ResColors.Net(th), "net");
                // Tercera fila: GPU y, según el equipo, batería o temperatura.
                int y3 = y + 2 * (tH + gap);
                if (snap.GpuAvailable)
                    Tile(g, "t-gpu", new Rectangle(pad, y3, tw, tH), Glyphs.Gpu, "GPU", Format.Percent(snap.Gpu),
                        Format.Bytes(snap.GpuDedicated + snap.GpuShared) + " en uso", snap.GpuHistory, 100, ResColors.Gpu(th), "gpu");
                if (snap.HasBattery)
                    Tile(g, "t-bat", new Rectangle(pad + tw + gap, y3, tw, tH), Glyphs.Battery, "Batería", snap.BatteryPercent.ToString("0") + " %",
                        snap.OnAc ? (snap.Charging ? "Cargando" : "Enchufada") : (snap.BatteryMinutes > 0 ? (snap.BatteryMinutes / 60) + " h " + (snap.BatteryMinutes % 60) + " min restantes" : "Con batería"),
                        snap.BatteryHistory, 100, ResColors.Battery(th), "bat");
                else if (!double.IsNaN(snap.Temperature))
                    Tile(g, "t-temp", new Rectangle(pad + tw + gap, y3, tw, tH), Glyphs.Temperature, "Temperatura", snap.Temperature.ToString("0") + " °C",
                        "Sensor del equipo", snap.TempHistory, 110, ResColors.Temp(th), "temp");
            }
            y += 3 * tH + 2 * gap + S(6);

            // ---- Procesos
            DrawText(g, "Procesos", fonts.BodyStrong, new Rectangle(S(16), y + S(12), S(120), S(20)), th.Text);
            int segW = S(84);
            Segment(g, "sort-mem", new Rectangle(W - pad - segW, y + S(8), segW, S(28)), "Memoria", sortByMemory, () => sortByMemory = true);
            Segment(g, "sort-cpu", new Rectangle(W - pad - 2 * segW - S(4), y + S(8), segW, S(28)), "CPU", !sortByMemory, () => sortByMemory = false);
            y += S(42);
            int rowsH = S(5 * 36 + 8);
            var card = new Rectangle(pad, y, W - 2 * pad, rowsH);
            Card(g, card);
            if (snap != null)
            {
                var top = (sortByMemory ? snap.Procs.OrderByDescending(p => p.Memory) : snap.Procs.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory))
                    .Where(p => p.Name != "Idle").Take(5).ToList();
                int ry = y + S(4);
                foreach (var p in top)
                {
                    var r = new Rectangle(card.X + S(4), ry, card.Width - S(8), S(36));
                    ProcessRow(g, p, r);
                    ry += S(36);
                }
            }
            y += rowsH;

            // ---- Actividad reciente
            DrawText(g, "Actividad reciente", fonts.BodyStrong, new Rectangle(S(16), y + S(12), S(200), S(20)), th.Text);
            SubtleButton(g, "log-all", new Rectangle(W - pad - S(92), y + S(8), S(92), S(28)), null, "Ver todo", "Ver todo el registro", () => Open("log"));
            y += S(42);
            int logH = S(4 * 30 + 8);
            var lcard = new Rectangle(pad, y, W - 2 * pad, logH);
            Card(g, lcard);
            var recent = Activity.Snapshot().OrderBy(e => e.Time).ToList();
            int ly = y + S(4);
            for (int i = recent.Count - 1, n = 0; i >= 0 && n < 4; i--, n++)
            {
                var e = recent[i];
                var r = new Rectangle(lcard.X + S(12), ly, lcard.Width - S(24), S(30));
                DrawText(g, e.Time.ToString("HH:mm"), fonts.Caption, new Rectangle(r.X, r.Y, S(38), r.Height), th.TextSecondary);
                Color gc = e.Severity == Severity.Error ? th.Critical : e.Severity == Severity.Advertencia ? th.Caution : th.TextSecondary;
                Glyph(g, ActivityEntry.CategoryGlyph(e.Category), fonts.Glyph12, new Rectangle(r.X + S(42), r.Y, S(16), r.Height), gc);
                DrawText(g, e.Message, fonts.Caption, new Rectangle(r.X + S(66), r.Y, r.Width - S(66), r.Height), th.Text);
                AddHit("log" + i, r, () => Open("log"), e.Message).Tip = e.Message;
                ly += S(30);
            }
            if (recent.Count == 0)
                DrawText(g, "Todavía no hay actividad registrada", fonts.Caption, lcard, th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // ---- Pie
            int fh = S(48), fy = ClientSize.Height - fh;
            using (var b = new SolidBrush(th.Footer)) g.FillRectangle(b, 0, fy, W, fh);
            using (var pen = new Pen(th.FooterStroke)) g.DrawLine(pen, 0, fy, W, fy);
            string wtext = host.WidgetVisible ? "Ocultar widget" : "Mostrar widget";
            SubtleButton(g, "widget", new Rectangle(S(8), fy + S(8), MeasureText(wtext, fonts.Body) + S(48), S(32)),
                host.WidgetVisible ? Glyphs.Unpin : Glyphs.Pin, wtext, null, () => { host.ToggleWidget(); Invalidate(); });
            DrawText(g, "Versión " + AppInfo.Version, fonts.Caption, new Rectangle(S(170), fy, W - S(230), fh), th.TextTertiary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            SubtleButton(g, "settings", new Rectangle(W - S(48), fy + S(8), S(40), S(32)), Glyphs.Settings, null, "Más opciones",
                () => ShowMenu(Menus.Settings(th, s, host, true), new Point(W - S(8), fy - S(4)), true));
        }

        void Tile(Graphics g, string key, Rectangle r, string glyph, string label, string value, string sub, float[] hist, float max, Color c, string page)
        {
            AddHit(key, r, () => Open(page), label + ": " + value + ", " + sub);
            Card(g, r);
            HoverFill(g, key, r, S(6));
            Glyph(g, glyph, fonts.Glyph12, new Rectangle(r.X + S(12), r.Y + S(10), S(14), S(18)), th.TextSecondary);
            DrawText(g, label, fonts.Caption, new Rectangle(r.X + S(32), r.Y + S(10), r.Width - S(40), S(18)), th.TextSecondary);
            DrawText(g, value, fonts.Subtitle, new Rectangle(r.X + S(12), r.Y + S(28), r.Width - S(20), S(28)), th.Text);
            DrawText(g, sub, fonts.Caption, new Rectangle(r.X + S(12), r.Y + S(56), r.Width - S(20), S(16)), th.TextSecondary);
            if (hist != null)
                Graph(g, new Rectangle(r.X + 1, r.Bottom - S(24), r.Width - 2, S(22)), hist, max, c, false, false);
        }

        void Segment(Graphics g, string key, Rectangle r, string text, bool selected, Action click)
        {
            AddHit(key, r, () => { click(); Invalidate(); }, "Ordenar por " + text).Checked = selected;
            if (selected) Fill(g, r, th.ControlFill, S(4));
            else HoverFill(g, key, r, S(4));
            if (selected)
            {
                var ind = new Rectangle(r.X + r.Width / 2 - S(8), r.Bottom - S(3), S(16), S(3));
                Fill(g, ind, th.Accent, S(1.5f));
            }
            DrawText(g, text, selected ? fonts.BodyStrong : fonts.Body, r, selected ? th.Text : th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void ProcessRow(Graphics g, ProcMetric p, Rectangle r)
        {
            string key = "p" + p.Pid;
            var h = AddHit(key, r, null, p.Display + ", CPU " + Format.Percent(p.Cpu) + ", memoria " + Format.Bytes(p.Memory));
            Action menu = () =>
            {
                suppressHide++;
                var m = Menus.ForProcess(th, s, host, p, this);
                m.Closed += (o, e) => suppressHide--;
                ShowMenu(m, new Point(r.Right, r.Bottom), false);
            };
            h.Click = menu;
            h.RightClick = pt => menu();
            HoverFill(g, key, r, S(4));
            var ir = new Rectangle(r.X + S(10), r.Y + (r.Height - S(16)) / 2, S(16), S(16));
            if (p.Icon != null) DrawIcon(g, p.Icon, ir);
            else Glyph(g, Glyphs.App, fonts.Glyph16, ir, th.TextSecondary);
            int valW = S(62), memW = S(74);
            DrawText(g, p.Display, fonts.Body, new Rectangle(ir.Right + S(12), r.Y, r.Width - S(38) - valW - memW - S(8), r.Height), th.Text);
            DrawText(g, Format.Percent(p.Cpu), sortByMemory ? fonts.Caption : fonts.CaptionStrong,
                new Rectangle(r.Right - memW - valW - S(8), r.Y, valW, r.Height), sortByMemory ? th.TextSecondary : th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            DrawText(g, Format.Bytes(p.Memory), sortByMemory ? fonts.CaptionStrong : fonts.Caption,
                new Rectangle(r.Right - memW - S(8), r.Y, memW, r.Height), sortByMemory ? th.Text : th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { refresh.Dispose(); slide.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
