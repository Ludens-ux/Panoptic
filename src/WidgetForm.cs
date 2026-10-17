using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysMonitor
{
    /// <summary>Widget de escritorio: se puede mover, fijar arriba de todo y bloquear en su lugar.</summary>
    internal sealed class WidgetForm : Surface
    {
        const string Key = @"Software\SysMonitor";
        const int LogicalWidth = 300;
        readonly IHost host;
        readonly Timer refresh = new Timer();
        bool locked;

        public WidgetForm(IHost host)
        {
            this.host = host;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Widget del Monitor del sistema";
            refresh.Interval = 1000;
            refresh.Tick += (o, e) => Invalidate();
            LocationChanged += (o, e) => { if (VibrancyOn) Invalidate(); };
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                TopMost = k != null && (k.GetValue("WidgetTopMost") as int? ?? 0) == 1;
                locked = k != null && (k.GetValue("WidgetLocked") as int? ?? 0) == 1;
                transparency = k != null ? (k.GetValue("WidgetTransparency") as int? ?? 1) : 1;
                // La vibrancia se agregó después: activarla una vez para quien ya usaba el widget.
                if (k == null || k.GetValue("WidgetVibrancyOffered") == null) { transparency = VibrancyLevel; Save("WidgetTransparency", VibrancyLevel); Save("WidgetVibrancyOffered", 1); }
            }
        }

        // 0 = Normal (Acrílico de Windows), 1 = Alta (desenfoque con poco tinte), 2 = Máxima (solo desenfoque).
        int transparency;
        static readonly string[] TransparencyNames = { "Normal", "Alta", "Máxima", "Vibrancia (estilo macOS)" };
        const int VibrancyLevel = 3;

        // La vibrancia la dibuja la app: no depende del desenfoque de Windows, solo del interruptor de transparencia.
        bool VibrancyOn { get { return transparency == VibrancyLevel && th.TransparencyEnabled; } }
        readonly Vibrancy vibrancy = new Vibrancy();
        Color secondary, track;

        void SetTransparency(int level)
        {
            transparency = level;
            Save("WidgetTransparency", level);
            ApplyDwm();
            Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: no aparece en la barra de tareas ni en Alt+Tab
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void ApplyDwm()
        {
            base.ApplyDwm();
            if (!IsHandleCreated) return;
            int v = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
            v = Native.ColorRef(th.WindowStroke);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, 4);

            if (!th.Glass || transparency == VibrancyLevel)
            {
                // Sin transparencia de Windows, o con vibrancia (el fondo lo pinta la app): ventana opaca.
                Native.SetAccentBlur(Handle, Native.ACCENT_DISABLED, Color.Empty);
                if (th.Glass) Native.SetBackdrop(Handle, Native.DWMSBT_NONE);
                return;
            }
            if (transparency == 0)
            {
                Native.SetAccentBlur(Handle, Native.ACCENT_DISABLED, Color.Empty);
                Native.SetBackdrop(Handle, Native.DWMSBT_ACRYLIC);
                return;
            }
            // Más transparente que el Acrílico estándar: sin fondo del sistema y con desenfoque propio.
            Native.SetBackdrop(Handle, Native.DWMSBT_NONE);
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);
            Color tint = th.Dark ? Color.FromArgb(70, 24, 24, 24) : Color.FromArgb(70, 246, 246, 246);
            Native.SetAccentBlur(Handle, transparency == 1 ? Native.ACCENT_ENABLE_ACRYLICBLURBEHIND : Native.ACCENT_ENABLE_BLURBEHIND, tint);
        }

        /// <summary>Capa de tinte que se pinta encima del desenfoque para que el texto se lea bien.</summary>
        Color GlassTint
        {
            get
            {
                if (transparency == 2) return th.Dark ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);
                return Color.Transparent;
            }
        }

        protected override void OnThemeReloaded() { vibrancy.Invalidate(); }

        protected override void OnScaleChanged()
        {
            ClientSize = new Size(S(LogicalWidth), S(48 + 132 * RingRows() + (Settings.WidgetNetwork ? 62 : 0) + 6));
        }

        public void ShowWidget()
        {
            ReloadTheme();
            var pos = SavedPosition();
            SetScale(Native.ScaleForPoint(pos) * SizeFactor);
            OnScaleChanged();
            Location = pos;
            Show();
            refresh.Start();
        }

        public void HideWidget()
        {
            refresh.Stop();
            Hide();
        }

        Point SavedPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var def = new Point(wa.Right - (int)(LogicalWidth * Native.ScaleForPoint(wa.Location)) - 24, wa.Top + 24);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null && k.GetValue("WidgetX") is int && k.GetValue("WidgetY") is int)
                    {
                        var p = new Point((int)k.GetValue("WidgetX"), (int)k.GetValue("WidgetY"));
                        if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(new Point(p.X + 40, p.Y + 20)))) return p;
                    }
                }
            }
            catch { }
            return def;
        }

        void Save(string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        // ------------------------------------------------------------------ arrastre

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !locked && HitAt(e.Location) == null)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                Save("WidgetX", Left);
                Save("WidgetY", Top);
                return;
            }
            if (e.Button == MouseButtons.Right && HitAt(e.Location) == null) { ShowOptions(e.Location); return; }
            base.OnMouseDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                SetScale((m.WParam.ToInt64() & 0xFFFF) / 96f * SizeFactor);
                OnScaleChanged();
                Invalidate();
                return;
            }
            base.WndProc(ref m);
        }

        void ShowOptions(Point at)
        {
            var m = FluentMenu.Create(th, s);
            var top = FluentMenu.Add(m, "Siempre visible", Glyphs.Pin, true, () => { TopMost = !TopMost; Save("WidgetTopMost", TopMost ? 1 : 0); Invalidate(); });
            top.Checked = TopMost;
            var lk = FluentMenu.Add(m, "Bloquear posición", Glyphs.Lock, true, () => { locked = !locked; Save("WidgetLocked", locked ? 1 : 0); });
            lk.Checked = locked;
            FluentMenu.Separator(m);
            for (int i = 0; i < TransparencyNames.Length; i++)
            {
                int level = i;
                bool available = th.Glass || (i == VibrancyLevel && th.TransparencyEnabled);
                var it = FluentMenu.Add(m, "Transparencia: " + TransparencyNames[i], null, available, () => SetTransparency(level));
                it.Checked = available && transparency == i;
            }
            FluentMenu.Separator(m);
            FluentMenu.Add(m, "Personalizar widget…", Glyphs.Settings, true, () => host.OpenMain("settings"));
            FluentMenu.Add(m, "Abrir Monitor del sistema", Glyphs.OpenExternal, true, () => host.OpenMain(null));
            FluentMenu.Add(m, "Ocultar widget", Glyphs.Cancel, true, host.ToggleWidget);
            m.Closed += (o, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(this, at);
        }

        // ------------------------------------------------------------------ pintado

        // Valores mostrados en los anillos: se acercan suavemente al valor real (animación de 300 ms aprox.).
        readonly Dictionary<string, float> shown = new Dictionary<string, float>();
        readonly Dictionary<string, float> target = new Dictionary<string, float>();
        FrameLoop anim;
        double lastStep;

        float Animated(string key, float value)
        {
            target[key] = value;
            float cur;
            if (!shown.TryGetValue(key, out cur) || !Native.AnimationsEnabled()) { shown[key] = value; return value; }
            if (Math.Abs(cur - value) < 1.5f && (anim == null || !anim.Running)) { shown[key] = value; return value; } // cambios mínimos: sin animar
            if (Math.Abs(cur - value) > 0.05f)
            {
                if (anim == null) anim = new FrameLoop(this, AnimateFrame);
                if (!anim.Running) { lastStep = Clock.Ms; anim.Start(); }
            }
            return cur;
        }

        /// <summary>Un cuadro sincronizado con la pantalla: los anillos se acercan a su valor según el tiempo real (ease out).</summary>
        void AnimateFrame()
        {
            double now = Clock.Ms;
            float dt = (float)Math.Min(50, now - lastStep);
            lastStep = now;
            float k = 1f - (float)Math.Pow(0.004, dt / 350f); // unos 350 ms con ease out
            bool moving = false;
            foreach (var key in target.Keys.ToList())
            {
                float c = shown[key], t = target[key];
                c += (t - c) * k;
                if (Math.Abs(t - c) < 0.05f) c = t; else moving = true;
                shown[key] = c;
            }
            Invalidate();
            Update();
            if (!moving) anim.Stop();
        }

        protected override void PaintSurface(Graphics g)
        {
            secondary = th.TextSecondary;
            track = th.Dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0);
            if (VibrancyOn && vibrancy.Ensure(Bounds, th.Dark))
            {
                // Vibrancia: fondo de pantalla desenfocado y saturado, y colores tomados de ese fondo.
                vibrancy.Draw(g, Bounds, ClientRectangle);
                var avg = vibrancy.Average(Bounds);
                secondary = Vibrancy.Vibrant(avg, th.Dark, 0.82f, 255);
                track = Vibrancy.Vibrant(avg, th.Dark, 0.62f, 110);
            }
            else g.Clear(th.Glass ? GlassTint : th.Layer);
            int W = ClientSize.Width;
            Glyph(g, Glyphs.Performance, fonts.Glyph16, new Rectangle(S(14), S(12), S(20), S(24)), th.Accent);
            DrawText(g, "Monitor", fonts.BodyStrong, new Rectangle(S(42), S(12), S(140), S(24)), th.Text);
            SubtleButton(g, "more", new Rectangle(W - S(8) - S(32), S(8), S(32), S(32)), Glyphs.More, null, "Opciones del widget",
                () => ShowOptions(new Point(W - S(8), S(40))));
            var pin = SubtleButton(g, "pin", new Rectangle(W - S(8) - S(68), S(8), S(32), S(32)), TopMost ? Glyphs.Unpin : Glyphs.Pin, null,
                TopMost ? "Dejar de mostrar siempre visible" : "Mostrar siempre visible",
                () => { TopMost = !TopMost; Save("WidgetTopMost", TopMost ? 1 : 0); Invalidate(); });
            pin.Checked = TopMost;

            var snap = host.Latest;
            if (snap == null) return;

            // ---- Anillos de progreso (los elegidos en Configuración, de a tres por fila)
            int y = S(48);
            int colW = (W - S(16)) / 3;
            var rings = Rings(snap);
            for (int row = 0; row * 3 < rings.Count; row++)
            {
                var line = rings.Skip(row * 3).Take(3).ToList();
                int x0 = S(8) + (3 - line.Count) * colW / 2; // centrar filas incompletas
                for (int i = 0; i < line.Count; i++)
                    line[i](g, new Rectangle(x0 + i * colW, y, colW, S(132)));
                y += S(132);
            }
            if (!Settings.WidgetNetwork) return;

            // ---- Red (no es un porcentaje: valor y gráfico)
            var n = snap.Nets.OrderByDescending(x => x.SendBps + x.RecvBps).FirstOrDefault();
            var r = new Rectangle(S(8), y, W - S(16), S(62));
            if (n == null)
            {
                DrawText(g, "Sin conexión de red", fonts.Caption, r, secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var hist = new float[History.Size];
            for (int i = 0; i < hist.Length; i++) hist[i] = n.SendHistory[i] + n.RecvHistory[i];
            AddHit("row-net", r, () => host.OpenMain("net"), n.Kind + ": recibiendo " + Format.Bits(n.RecvBps) + ", enviando " + Format.Bits(n.SendBps)).Tip = "Abrir red en el Monitor";
            Card(g, r, VibrancyOn ? (th.Dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255)) : th.Card);
            if (IsHover("row-net")) Fill(g, r, th.HoverOverlay, S(6));
            Glyph(g, n.Kind == "Wi-Fi" ? Glyphs.Wifi : Glyphs.Ethernet, fonts.Glyph12, new Rectangle(r.X + S(10), r.Y + S(6), S(16), S(20)), secondary);
            DrawText(g, n.Kind, fonts.Caption, new Rectangle(r.X + S(32), r.Y + S(6), S(80), S(20)), secondary);
            DrawText(g, "↓ " + Format.Bits(n.RecvBps) + "   ↑ " + Format.Bits(n.SendBps), fonts.CaptionStrong,
                new Rectangle(r.X + S(90), r.Y + S(6), r.Width - S(100), S(20)), th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Graph(g, new Rectangle(r.X + S(10), r.Y + S(30), r.Width - S(20), S(24)), hist, Math.Max(1, hist.Max()) * 1.2f, ResColors.Net(th), false, false);
        }

        /// <summary>Anillos disponibles en este equipo, en el orden de Configuración.</summary>
        List<Action<Graphics, Rectangle>> Rings(Snapshot snap)
        {
            var list = new List<Action<Graphics, Rectangle>>();
            foreach (var id in Settings.WidgetRingIds)
            {
                if (!Settings.WidgetRing(id)) continue;
                switch (id)
                {
                    case "cpu":
                        list.Add((g, r) => Ring(g, r, "cpu", "CPU", snap.Cpu, snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : snap.Logical + " lógicos", ResColors.Cpu(th)));
                        break;
                    case "mem":
                        list.Add((g, r) => Ring(g, r, "mem", "Memoria", snap.MemPercent, Format.Bytes(snap.MemUsed), ResColors.Memory(th)));
                        break;
                    case "disk":
                        var d = snap.Disks.OrderByDescending(x => x.Active).FirstOrDefault();
                        if (d != null)
                            list.Add((g, r) => Ring(g, r, "disk", "Disco " + d.Number + (string.IsNullOrEmpty(d.Letters) ? "" : " (" + d.Letters.Split(',')[0] + ")"),
                                d.Active, Format.Rate(d.ReadBps + d.WriteBps), ResColors.Disk(th)));
                        break;
                    case "gpu":
                        if (snap.GpuAvailable)
                            list.Add((g, r) => Ring(g, r, "gpu", "GPU", snap.Gpu, Format.Bytes(snap.GpuDedicated + snap.GpuShared), ResColors.Gpu(th)));
                        break;
                    case "temp":
                        if (!double.IsNaN(snap.Temperature))
                            list.Add((g, r) => Ring(g, r, "temp", "Temperatura", (float)Math.Min(100, snap.Temperature), "", ResColors.Temp(th), snap.Temperature.ToString("0") + "°"));
                        break;
                    case "bat":
                        if (snap.HasBattery)
                            list.Add((g, r) => Ring(g, r, "bat", "Batería", snap.BatteryPercent,
                                snap.OnAc ? (snap.Charging ? "Cargando" : "Enchufada") : (snap.BatteryMinutes > 0 ? (snap.BatteryMinutes / 60) + " h " + (snap.BatteryMinutes % 60) + " min" : "Con batería"),
                                ResColors.Battery(th)));
                        break;
                }
            }
            return list;
        }

        float SizeFactor { get { int z = Settings.WidgetSize; return z == 0 ? 0.85f : z == 2 ? 1.2f : 1f; } }

        int RingRows()
        {
            var snap = host.Latest;
            int n = snap == null ? Settings.WidgetRingIds.Count(Settings.WidgetRing) : Rings(snap).Count;
            return Math.Max(1, (n + 2) / 3);
        }

        /// <summary>Vuelve a calcular tamaño y contenido después de cambiar la configuración.</summary>
        public void Relayout()
        {
            SetScale(Native.ScaleForWindow(Handle) * SizeFactor);
            OnScaleChanged();
            Invalidate();
        }

        /// <summary>Anillo de progreso (ProgressRing de Windows 11) con el porcentaje en el centro.</summary>
        void Ring(Graphics g, Rectangle col, string page, string label, float value, string detail, Color c)
        {
            Ring(g, col, page, label, value, detail, c, null);
        }

        void Ring(Graphics g, Rectangle col, string page, string label, float value, string detail, Color c, string centerText)
        {
            string key = "ring-" + page;
            AddHit(key, col, () => host.OpenMain(page), label + ": " + Format.Percent(value) + ", " + detail).Tip = "Abrir " + label + " en el Monitor";
            if (IsHover(key)) Fill(g, Rectangle.Inflate(col, -S(2), -S(2)), th.HoverOverlay, S(6));

            float v = Animated(page, value);
            float d = 76 * s, stroke = 7 * s;
            var rr = new RectangleF(col.X + (col.Width - d) / 2f, col.Y + S(8), d, d);
            var inner = RectangleF.Inflate(rr, -stroke / 2f, -stroke / 2f);
            using (var trackPen = new Pen(track, stroke))
                g.DrawEllipse(trackPen, inner);
            if (v > 0.2f)
                using (var pen = new Pen(c, stroke))
                {
                    pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawArc(pen, inner, -90f, Math.Max(1f, 360f * Math.Min(100f, v) / 100f));
                }
            var center = new Rectangle((int)rr.X, (int)rr.Y, (int)rr.Width, (int)rr.Height);
            DrawText(g, centerText ?? v.ToString("0") + "%", fonts.Subtitle, center, th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, label, fonts.Caption, new Rectangle(col.X, (int)rr.Bottom + S(8), col.Width, S(18)), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, detail, fonts.Caption, new Rectangle(col.X, (int)rr.Bottom + S(26), col.Width, S(18)), secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { refresh.Dispose(); vibrancy.Dispose(); if (anim != null) anim.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
