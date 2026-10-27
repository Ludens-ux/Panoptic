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
            refresh.Tick += (o, e) =>
            {
                Eye.Shared.ObserveIfStale(host.Latest);
                if (Eye.Shared.Moving(Clock.Ms)) RequestMotion();
                Invalidate();
            };
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
            v = Native.ColorRef(th.WindowBorder);
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

        /// <summary>El panel se dibuja con la placa de video (Direct2D); si no se puede, con GDI+.</summary>
        protected override bool UseGpu { get { return true; } }

        // ---- Medidas (lenguaje "isla"): padding 14 14 10, separación vertical 12.
        const int Pad = 14, Gap = 12, TileH = 84, TileGap = 6, ProcRowH = 32, LogRowH = 28, FooterH = 48;

        int TileCount
        {
            get
            {
                var snap = host.Latest;
                if (snap == null) return 6;
                return 4 + (snap.GpuAvailable ? 1 : 0) + (snap.HasBattery ? 1 : 0) + (!double.IsNaN(snap.Temperature) ? 1 : 0);
            }
        }

        int ContentHeight
        {
            get
            {
                int rows = (TileCount + 1) / 2;
                return S(Pad + 36 + Gap + rows * (TileH + TileGap) - TileGap + Gap + 28 + 4 + 5 * ProcRowH + Gap + 24 + 4 + 4 * LogRowH + Gap + FooterH);
            }
        }

        protected override bool MotionExtra(double now)
        {
            Eye.Shared.Step(now);
            return Visible && Eye.Shared.Moving(now);
        }

        protected override void PaintSurface(Graphics g)
        {
            ClearP(g, th.Clear);
            var snap = host.Latest;
            int W = ClientSize.Width, pad = S(Pad);
            int y = pad;

            // ---- Encabezado: el ojo en un cuadrado de acento al 14 %, nombre, tiempo encendido y abrir
            var tile = new Rectangle(pad, y, S(36), S(36));
            Fill(g, tile, Color.FromArgb(36, th.Accent), S(8));
            Eye.Shared.Draw(g, d2d, Rectangle.Inflate(tile, -S(5), -S(5)), !th.Dark);
            int tx = tile.Right + S(12), btnW = S(32);
            DrawText(g, AppInfo.Name, fonts.BodyStrong, new Rectangle(tx, y, W - tx - pad - btnW - S(8), S(19)), th.Text);
            string sub = snap == null ? "Midiendo…" : "Encendido hace " + Format.Duration(snap.Uptime) + " · " + snap.ProcessCount + " procesos";
            DrawText(g, sub, fonts.Caption, new Rectangle(tx, y + S(19), W - tx - pad - btnW - S(8), S(17)), th.TextSecondary);
            SubtleButton(g, "open", new Rectangle(W - pad - btnW, y + S(4), btnW, S(28)), Glyphs.OpenExternal, null, "Abrir Panoptic", () => Open(null));
            y += S(36) + S(Gap);

            // ---- Tarjetas de métricas (2 columnas)
            int gap = S(TileGap), tw = (W - 2 * pad - gap) / 2, tH = S(TileH);
            int rows = (TileCount + 1) / 2;
            if (snap != null)
            {
                var tiles = new List<Action<Rectangle>>();
                var disk = snap.Disks.OrderByDescending(d => d.Active).FirstOrDefault();
                var net = snap.Nets.OrderByDescending(n => n.SendBps + n.RecvBps).FirstOrDefault();
                tiles.Add(r => Tile(g, "t-cpu", r, "CPU", snap.Cpu, snap.Cpu.ToString("0") + "%",
                    snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : snap.Logical + " procesadores lógicos", snap.CpuHistory, 100, ResColors.Cpu(th), "cpu"));
                tiles.Add(r => Tile(g, "t-mem", r, "Memoria", snap.MemPercent, snap.MemPercent.ToString("0") + "%",
                    Format.Bytes(snap.MemUsed) + " / " + Format.Bytes(snap.MemTotal), snap.MemHistory, 100, ResColors.Memory(th), "mem"));
                tiles.Add(r =>
                {
                    if (disk != null)
                        Tile(g, "t-disk", r, disk.Title, disk.Active, disk.Active.ToString("0") + "%",
                            "L " + Format.Rate(disk.ReadBps) + " · E " + Format.Rate(disk.WriteBps), disk.ActivityHistory, 100, ResColors.Disk(th), "disk");
                    else Tile(g, "t-disk", r, "Disco", 0, "—", "Sin discos", null, 1, ResColors.Disk(th), "disk");
                });
                tiles.Add(r =>
                {
                    if (net != null)
                    {
                        var hist = new float[net.SendHistory.Length];
                        float max = 1;
                        for (int i = 0; i < hist.Length; i++) { hist[i] = net.SendHistory[i] + net.RecvHistory[i]; max = Math.Max(max, hist[i]); }
                        double use = net.LinkSpeed > 0 ? (net.RecvBps + net.SendBps) * 8 * 100 / net.LinkSpeed : 0;
                        Tile(g, "t-net", r, net.Kind, (float)Math.Min(100, use), use >= 1 ? use.ToString("0") + "%" : "0%",
                            "↓ " + Format.Bits(net.RecvBps) + " · ↑ " + Format.Bits(net.SendBps), hist, max * 1.2f, ResColors.Net(th), "net");
                    }
                    else Tile(g, "t-net", r, "Red", 0, "—", "Sin conexión", null, 1, ResColors.Net(th), "net");
                });
                if (snap.GpuAvailable)
                    tiles.Add(r => Tile(g, "t-gpu", r, "GPU", snap.Gpu, snap.Gpu.ToString("0") + "%",
                        Format.Bytes(snap.GpuDedicated + snap.GpuShared) + " en uso" + (double.IsNaN(snap.GpuTemperature) ? "" : " · " + snap.GpuTemperature.ToString("0") + " °C"),
                        snap.GpuHistory, 100, ResColors.Gpu(th), "gpu"));
                if (snap.HasBattery)
                    tiles.Add(r => Tile(g, "t-bat", r, "Batería", snap.BatteryPercent, snap.BatteryPercent.ToString("0") + "%",
                        snap.OnAc ? (snap.Charging ? (snap.BatteryMinutes > 0 ? "Cargando · lleno en " + Format.Minutes(snap.BatteryMinutes) : "Cargando") : "Enchufada")
                                  : (snap.BatteryMinutes > 0 ? Format.Minutes(snap.BatteryMinutes) + " restantes" : "Con batería"),
                        snap.BatteryHistory, 100, ResColors.Battery(th), "bat"));
                if (!double.IsNaN(snap.Temperature))
                    tiles.Add(r => Tile(g, "t-temp", r, "Temperatura", (float)Math.Min(100, snap.Temperature), snap.Temperature.ToString("0") + "°",
                        snap.Temperature >= Settings.TempThreshold ? "Alta · sensor del equipo" : "Normal · sensor del equipo", snap.TempHistory, 110, ResColors.Temp(th), "temp"));
                for (int i = 0; i < tiles.Count; i++)
                    tiles[i](new Rectangle(pad + (i % 2) * (tw + gap), y + (i / 2) * (tH + gap), tw, tH));
            }
            y += rows * (tH + gap) - gap + S(Gap);

            // ---- Procesos: los 5 que más usan, por CPU o por memoria
            DrawText(g, "Procesos", fonts.Body13Strong, new Rectangle(pad, y, S(120), S(28)), th.Text);
            var seg = new Rectangle(W - pad - S(150), y + S(2), S(150), S(24));
            Fill(g, seg, Color.FromArgb(13, th.Dark ? Color.White : Color.Black), S(6));
            int half = (seg.Width - S(4)) / 2;
            Segment(g, "sort-cpu", new Rectangle(seg.X + S(2), seg.Y + S(2), half, seg.Height - S(4)), "CPU", !sortByMemory, () => sortByMemory = false);
            Segment(g, "sort-mem", new Rectangle(seg.X + S(2) + half, seg.Y + S(2), half, seg.Height - S(4)), "Memoria", sortByMemory, () => sortByMemory = true);
            y += S(28) + S(4);
            if (snap != null)
            {
                var top = (sortByMemory ? snap.Procs.OrderByDescending(p => p.Memory) : snap.Procs.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory))
                    .Where(p => p.Name != "Idle").Take(5).ToList();
                int ry = y;
                foreach (var p in top) { ProcessRow(g, p, new Rectangle(pad - S(6), ry, W - 2 * pad + S(12), S(ProcRowH))); ry += S(ProcRowH); }
            }
            y += 5 * S(ProcRowH) + S(Gap);

            // ---- Actividad reciente: hora, punto del color del tipo y texto
            DrawText(g, "Actividad reciente", fonts.Body13Strong, new Rectangle(pad, y, S(200), S(24)), th.Text);
            int allW = MeasureText("Ver todo", fonts.Caption) + S(16);
            var all = new Rectangle(W - pad - allW + S(8), y, allW, S(24));
            AddHit("log-all", all, () => Open("log"), "Ver todo el registro");
            HoverFill(g, "log-all", all, S(4));
            DrawText(g, "Ver todo", fonts.Caption, all, th.Dark ? Theme.Hex(0xC9B6FF) : th.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            y += S(24) + S(4);
            var recent = Activity.Snapshot().OrderBy(e => e.Time).ToList();
            int ly = y;
            for (int i = recent.Count - 1, n = 0; i >= 0 && n < 4; i--, n++)
            {
                var e = recent[i];
                var r = new Rectangle(pad, ly, W - 2 * pad, S(LogRowH));
                AddHit("log" + i, r, () => Open("log"), e.Message).Tip = e.Message;
                DrawText(g, e.Time.ToString("HH:mm"), fonts.Caption, new Rectangle(r.X, r.Y, S(38), r.Height), th.TextTertiary);
                Color dot = e.Severity == Severity.Error ? th.Critical : ResColors.Category(th, e.Category);
                FillEllipseP(g, new RectangleF(r.X + S(42), r.Y + (r.Height - S(6)) / 2f, S(6), S(6)), dot);
                DrawText(g, e.Message, fonts.Caption, new Rectangle(r.X + S(56), r.Y, r.Width - S(56), r.Height), th.Text);
                ly += S(LogRowH);
            }
            if (recent.Count == 0)
                DrawText(g, "Todavía no hay actividad registrada", fonts.Caption, new Rectangle(pad, y, W - 2 * pad, 4 * S(LogRowH)), th.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // ---- Pie
            int fh = S(FooterH), fy = ClientSize.Height - fh;
            LineP(g, th.Divider, 1, 0, fy, W, fy);
            string wtext = host.WidgetVisible ? "Ocultar widget" : "Mostrar widget";
            SubtleButton(g, "widget", new Rectangle(S(8), fy + S(8), MeasureText(wtext, fonts.Body) + S(24), S(32)), null, wtext, null,
                () => { host.ToggleWidget(); Invalidate(); });
            DrawText(g, "Versión " + AppInfo.Version, fonts.Caption, new Rectangle(S(170), fy, W - S(170) - S(52), fh), th.TextTertiary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            SubtleButton(g, "settings", new Rectangle(W - S(8) - S(32), fy + S(8), S(32), S(32)), Glyphs.Settings, null, "Más opciones",
                () => ShowMenu(Menus.Settings(th, s, host, true), new Point(W - S(8), fy - S(4)), true));
        }

        /// <summary>
        /// Tarjeta de métrica: anillo de 44 px con el valor adentro, el nombre en su color, el subtítulo en dos líneas
        /// (nunca con "…") y abajo, a sangre, la línea de historial.
        /// </summary>
        void Tile(Graphics g, string key, Rectangle r, string label, float ring, string value, string sub, float[] hist, float max, Color c, string page)
        {
            AddHit(key, r, () => Open(page), label + ": " + value + ", " + sub);
            var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1);
            var clip = ClipP(g, r);
            FillRoundP(g, rf, S(8), th.Card);
            HoverFill(g, key, r, S(8));
            if (hist != null && hist.Length > 1)
            {
                // Últimas 40 muestras, 22 px de alto, ocupando todo el ancho de la tarjeta.
                int n = Math.Min(40, hist.Length);
                var gr = new RectangleF(r.X + 1, r.Bottom - S(22) - 1, r.Width - 2, S(22));
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float v = Math.Max(0, Math.Min(max, hist[hist.Length - n + i]));
                    pts[i] = new PointF(gr.X + gr.Width * i / (n - 1), gr.Bottom - 1 - (gr.Height - 2) * v / Math.Max(0.0001f, max));
                }
                AreaP(g, UnderArea(pts, gr.Bottom), Color.FromArgb(51, c));
                LinesP(g, pts, c, Math.Max(1f, 1.3f * s));
            }
            StrokeRoundP(g, rf, S(8), th.CardStroke, 1f);
            UnclipP(g, clip);
            var rr = new RectangleF(r.X + S(10), r.Y + S(10), S(44), S(44));
            DrawRing(g, rr, Spring("fr-" + key, ring, 0.35f), c, 3f * s);
            DrawText(g, value, fonts.CaptionStrong, Rectangle.Round(rr), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int tx = r.X + S(10) + S(44) + S(10), tw = r.Right - tx - S(8);
            DrawText(g, label, fonts.Body13Strong, new Rectangle(tx, r.Y + S(10), tw, S(18)), c);
            var lines = TwoLines(sub, fonts.Small, tw);
            for (int i = 0; i < lines.Length; i++)
                DrawText(g, lines[i], fonts.Small, new Rectangle(tx, r.Y + S(29) + i * S(14), tw, S(14)), th.TextSecondary);
        }

        /// <summary>Parte el texto en hasta dos líneas que entren en <paramref name="width"/> (por " · " o por espacios).</summary>
        string[] TwoLines(string text, Font f, int width)
        {
            if (string.IsNullOrEmpty(text) || MeasureText(text, f) <= width) return new[] { text ?? "" };
            int dot = text.IndexOf(" · ");
            if (dot > 0 && MeasureText(text.Substring(0, dot), f) <= width) return new[] { text.Substring(0, dot), text.Substring(dot + 3) };
            var words = text.Split(' ');
            string first = words[0];
            int k = 1;
            while (k < words.Length && MeasureText(first + " " + words[k], f) <= width) { first += " " + words[k]; k++; }
            return new[] { first, string.Join(" ", words, k, words.Length - k) };
        }

        void Segment(Graphics g, string key, Rectangle r, string text, bool selected, Action click)
        {
            var h = AddHit(key, r, () => { click(); Invalidate(); }, "Ordenar por " + text);
            h.Checked = selected; h.Role = AccessibleRole.RadioButton;
            if (selected) Fill(g, r, Color.FromArgb(31, th.Dark ? Color.White : Color.Black), S(4));
            else HoverFill(g, key, r, S(4));
            DrawText(g, text, selected ? fonts.CaptionStrong : fonts.Caption, r, selected ? th.Text : th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Fila de 32 px: nombre | CPU (54) | memoria (54). La columna del criterio activo va en su color.</summary>
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
            int colW = S(54), x = r.X + S(6);
            var ir = new Rectangle(x, r.Y + (r.Height - S(16)) / 2, S(16), S(16));
            if (p.Icon != null) DrawIcon(g, p.Icon, ir);
            else Glyph(g, Glyphs.App, fonts.Glyph16, ir, th.TextSecondary);
            DrawText(g, p.Display, fonts.Body13, new Rectangle(ir.Right + S(10), r.Y, r.Right - S(6) - 2 * colW - ir.Right - S(16), r.Height), th.Text);
            var right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            DrawText(g, Format.Percent(p.Cpu), sortByMemory ? fonts.Caption : fonts.CaptionStrong,
                new Rectangle(r.Right - S(6) - 2 * colW, r.Y, colW, r.Height), sortByMemory ? th.TextSecondary : ResColors.Cpu(th), right);
            DrawText(g, Format.Bytes(p.Memory), sortByMemory ? fonts.CaptionStrong : fonts.Caption,
                new Rectangle(r.Right - S(6) - colW, r.Y, colW, r.Height), sortByMemory ? ResColors.Memory(th) : th.TextSecondary, right);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { refresh.Dispose(); slide.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
