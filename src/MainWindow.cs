using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>Ventana principal: Rendimiento, Procesos y Registro, con navegación lateral de Windows 11.</summary>
    internal sealed partial class MainWindow : Surface
    {
        readonly IHost host;
        readonly Timer refresh = new Timer();
        readonly TextBox search = new TextBox();
        string page = "perf";
        // Como en Windows 11: el menú lateral se contrae solo en ventanas angostas (el botón lo abre o cierra igual).
        bool? navChoice;
        bool navWasWide;
        bool navExpanded
        {
            get
            {
                bool wide = ClientSize.Width >= S(1000);
                if (wide != navWasWide) { navWasWide = wide; navChoice = null; }
                return navChoice ?? wide;
            }
        }
        readonly Dictionary<string, string> searchText = new Dictionary<string, string>();

        // Rendimiento
        string resource = "cpu";

        // Procesos
        string sortCol = "cpu";
        bool sortAsc;
        int selectedPid = -1;
        readonly Dictionary<string, int> scroll = new Dictionary<string, int>();
        Rectangle listView, thumb;
        int listContent;
        bool draggingThumb;
        int dragStartY, dragStartScroll;
        Rectangle searchBox;

        // Registro
        Category? logFilter;
        string logCacheKey;
        List<ActivityEntry> logCache = new List<ActivityEntry>();
        int logDays;

        public MainWindow(IHost host)
        {
            this.host = host;
            Text = AppInfo.Name;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(760, 520);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            refresh.Interval = 1000;
            refresh.Tick += (o, e) =>
            {
                if (WindowState == FormWindowState.Minimized) return;
                Eye.Shared.ObserveIfStale(host.Latest);
                if (Eye.Shared.Moving(Clock.Ms)) RequestMotion();
                Invalidate();
            };

            search.BorderStyle = BorderStyle.None;
            search.Visible = false;
            search.TextChanged += (o, e) => { searchText[page] = search.Text; scroll[page] = 0; Invalidate(); };
            search.KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) { search.Text = ""; e.SuppressKeyPress = true; } };
            search.GotFocus += (o, e) => Invalidate();
            search.LostFocus += (o, e) => Invalidate();
            Controls.Add(search);
            RestorePlacement();
        }

        internal void PreviewPage(string p, string res, int pid, int scrollPx = 0)
        {
            page = p; if (res != null) resource = res; selectedPid = pid; scroll[p] = scrollPx;
            if (p == "usb") usbItems = UsbHistory.Scan();
            if (p == "power") powerItems = PowerHistory.Scan(powerDays);
        }

        /// <summary>Selecciona un proceso en la lista (desde la isla).</summary>
        public void SelectProcess(int pid)
        {
            Navigate("procs");
            selectedPid = pid;
            Invalidate();
        }

        public void ShowPage(string p)
        {
            bool perfRes = p == "cpu" || p == "mem" || p == "disk" || p == "net" || p == "gpu" || p == "temp" || p == "bat";
            if (p != null) Navigate(perfRes ? "perf" : p);
            if (p == "cpu" || p == "mem" || p == "gpu" || p == "temp" || p == "bat") resource = p;
            else if (p == "disk") { var snap = host.Latest; var d = snap != null ? snap.Disks.OrderByDescending(x => x.Active).FirstOrDefault() : null; resource = d != null ? "disk" + d.Number : "cpu"; }
            else if (p == "net") { var snap = host.Latest; var n = snap != null ? snap.Nets.FirstOrDefault() : null; resource = n != null ? "net" + n.Id : "cpu"; }
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            refresh.Start();
            Invalidate();
        }

        protected override void ApplyDwm()
        {
            base.ApplyDwm();
            if (!IsHandleCreated) return;
            // Con Mica la barra de título queda con el fondo del sistema; sin transparencia se iguala al fondo.
            int v = th.Glass ? unchecked((int)0xFFFFFFFF) /* DWMWA_COLOR_DEFAULT */ : Native.ColorRef(th.Bg);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CAPTION_COLOR, ref v, 4);
            int border = Native.ColorRef(Theme.Mix(th.Bg, th.Dark ? Color.White : Color.Black, 0.09f));
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, 4);
            search.BackColor = SearchFill;
            search.ForeColor = th.Text;
        }

        protected override int Backdrop { get { return Native.DWMSBT_TRANSIENTWINDOW; } }

        /// <summary>Fondo opaco del cuadro de búsqueda (el TextBox de Windows no admite transparencia).</summary>
        Color SearchFill { get { return Theme.Mix(th.Bg, th.Dark ? Color.White : Color.Black, 0.05f); } }

        /// <summary>La ventana principal se dibuja con la placa de video (Direct2D); si no se puede, con GDI+.</summary>
        protected override bool UseGpu { get { return true; } }

        protected override void OnScaleChanged()
        {
            search.Font = fonts.Body;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                SetScale((m.WParam.ToInt64() & 0xFFFF) / 96f);
                var rect = (RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(RECT));
                SetBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                return;
            }
            base.WndProc(ref m);
        }

        struct RECT { public int Left, Top, Right, Bottom; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePlacement();
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // Cerrar la ventana no cierra el monitor: sigue en el área de notificación.
                e.Cancel = true;
                refresh.Stop();
                if (host.Helper != null) host.Helper.WantFiles = false;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        void Navigate(string p)
        {
            if (page == p) return;
            page = p;
            string t;
            search.Text = searchText.TryGetValue(p, out t) ? t : "";
            Invalidate();
        }

        // ------------------------------------------------------------------ pintado

        protected override void PaintSurface(Graphics g)
        {
            var psw = System.Diagnostics.Stopwatch.StartNew();
            // Cada zona se pinta una sola vez y con copia directa (sin mezclar transparencias): limpiar toda la
            // ventana y después volver a pintar la capa de contenido costaba más de 4 ms por cuadro.
            // El panel lateral se abre y se cierra con un resorte.
            int navW = (int)Math.Round(Spring("nav-w", navExpanded ? S(208) : S(52), 0.34f, 0.9f));
            // Una sola superficie casi negra para el riel y el contenido.
            FastFill(g, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), th.Clear);
            Stat("1 fondo", psw);
            PaintNav(g, navW);
            Stat("2 nav", psw);


            // El registro de archivos solo corre mientras esta página está a la vista.
            if (host.Helper != null) host.Helper.WantFiles = page == "files";
            search.Visible = page == "procs" || page == "net" || page == "files" || page == "usb" || page == "power" || page == "startup" || page == "services" || page == "log";
            var snap = host.Latest;
            var area = new Rectangle(navW, 0, ClientSize.Width - navW, ClientSize.Height);
            if (snap == null)
            {
                DrawText(g, "Midiendo…", fonts.Body, area, th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // Cabecera "isla": la misma píldora que la isla compacta, siempre visible arriba del contenido.
            IslandHeader(g, area, snap);
            // La página empieza debajo de la cabecera (el título queda 14 px más abajo).
            var content = new Rectangle(area.X, S(44), area.Width - S(4), area.Height - S(44));
            var pageState = ClipP(g, new Rectangle(area.X, S(50), area.Width, area.Height - S(50)));
            if (page == "perf") PaintPerformance(g, content, snap);
            else if (page == "procs") PaintProcesses(g, content, snap);
            else if (page == "hist") PaintHistory(g, content);
            else if (page == "net") PaintConnections(g, content, snap);
            else if (page == "files") PaintFiles(g, content, snap);
            else if (page == "usb") PaintUsb(g, content);
            else if (page == "power") PaintPower(g, content);
            else if (page == "startup") PaintStartup(g, content);
            else if (page == "services") PaintServices(g, content);
            else if (page == "settings") PaintSettings(g, content);
            else PaintLog(g, content);
            UnclipP(g, pageState);
            Stat("4 pagina", psw);
        }

        // ------------------------------------------------------------------ cabecera "isla"

        Rectangle headerEye;
        static readonly Theme PillTokens = new Theme { Dark = true };

        /// <summary>
        /// Píldora negra centrada arriba del contenido: el ojo vivo, tres mini anillos (CPU, memoria y temperatura) y
        /// la bajada de red. Clic en un anillo: Rendimiento con esa métrica.
        /// </summary>
        void IslandHeader(Graphics g, Rectangle area, Snapshot snap)
        {
            // La píldora es negra también con el tema claro: los anillos usan los colores del tema oscuro.
            var cells = new List<Tuple<string, string, float, string, Color>>();
            cells.Add(Tuple.Create("cpu", "CPU", snap.Cpu, Pct(snap.Cpu), ResColors.Cpu(PillTokens)));
            cells.Add(Tuple.Create("mem", "Memoria", snap.MemPercent, snap.MemPercent.ToString("0") + "%", ResColors.Memory(PillTokens)));
            if (!double.IsNaN(snap.Temperature))
                cells.Add(Tuple.Create("temp", "Temperatura", (float)Math.Min(100, snap.Temperature), snap.Temperature.ToString("0") + "°", ResColors.Temp(PillTokens)));
            else if (snap.GpuAvailable)
                cells.Add(Tuple.Create("gpu", "GPU", snap.Gpu, Pct(snap.Gpu), ResColors.Gpu(PillTokens)));
            var net = snap.Nets.OrderByDescending(n => n.RecvBps + n.SendBps).FirstOrDefault();
            string netText = "↓ " + Format.Bits(net != null ? net.RecvBps : 0);
            bool narrow = ClientSize.Width < S(640);
            var valFont = fonts.Body13Strong;
            int valW = MeasureText("100%", valFont);
            int cellW = S(16) + S(6) + valW;
            int netW = narrow ? 0 : S(1) + S(14) + MeasureText("↓ 888,8 Mbps", fonts.Caption);
            int w = S(12) + S(24) + cells.Count * (S(14) + cellW) + netW + S(16);
            var pill = new Rectangle(area.X + (area.Width - w) / 2, S(4), w, S(38));
            var pf = new RectangleF(pill.X + .5f, pill.Y + .5f, pill.Width - 1, pill.Height - 1);
            FillRoundP(g, pf, S(19), Color.Black);
            StrokeRoundP(g, pf, S(19), Color.FromArgb(23, 255, 255, 255), 1f);

            int x = pill.X + S(12), cy = pill.Y + pill.Height / 2;
            headerEye = new Rectangle(x, cy - S(12), S(24), S(24));
            Eye.Shared.Draw(g, d2d, headerEye, false);
            var eh = AddHit("hdr-eye", headerEye, () => { Eye.Shared.Blink(); RequestMotion(); }, "Ojo de Panoptic");
            eh.Focusable = false;
            x += S(24);
            // En la píldora negra el texto es siempre claro (también con el tema claro).
            foreach (var c in cells)
            {
                x += S(14);
                var cr = new Rectangle(x, pill.Y, cellW, pill.Height);
                string id = c.Item1;
                var h = AddHit("hdr-" + id, cr, () => ShowPage(id), c.Item2 + " " + c.Item4);
                h.Tip = c.Item2;
                float v = Spring("hdr-v-" + id, c.Item3, 0.35f);
                DrawRing(g, new RectangleF(x, cy - S(8), S(16), S(16)), v, c.Item5, 2.8f * s);
                DrawText(g, c.Item4, valFont, new Rectangle(x + S(22), pill.Y, valW + S(4), pill.Height), Color.White);
                x += cellW;
            }
            if (!narrow)
            {
                x += S(14);
                FillRoundP(g, new RectangleF(x, cy - S(8), S(1), S(16)), 0, Color.FromArgb(31, 255, 255, 255));
                x += S(1) + S(14);
                DrawText(g, netText, fonts.Caption, new Rectangle(x, pill.Y, pill.Right - x - S(8), pill.Height), Color.FromArgb(0x9D, 0x9D, 0x9D));
            }
        }

        protected override bool MotionExtra(double now)
        {
            Eye.Shared.Step(now);
            return Visible && Eye.Shared.Moving(now);
        }

        static void Stat(string k, System.Diagnostics.Stopwatch sw)
        {
            if (!ProfilePaint) return;
            double[] st;
            if (!PaintStats.TryGetValue(k, out st)) PaintStats[k] = st = new double[2];
            st[0]++; st[1] += sw.Elapsed.TotalMilliseconds; sw.Restart();
        }

        int navSelY = -1;   // posición del ítem seleccionado (destino de la píldora que se desliza)

        static readonly string[] NavKeys = { "perf", "procs", "hist", "net", "files", "usb", "power", "startup", "services", "log" };

        void PaintNav(Graphics g, int navW)
        {
            // Destino de la píldora: se calcula antes de dibujar (los ítems van en este orden; Configuración, abajo).
            int idx = Array.IndexOf(NavKeys, page);
            int selY = page == "settings" ? ClientSize.Height - S(48) : idx >= 0 ? S(56) + idx * S(42) : -1;
            if (selY >= 0 && navSelY != selY)
            {
                if (navSelY < 0) SetSpring("nav-sel", selY);
                navSelY = selY;
            }
            // Píldora de selección: se desliza hasta el ítem elegido y la marca de acento se estira mientras viaja.
            if (navSelY >= 0)
            {
                float ay = Spring("nav-sel", navSelY, 0.12f, 1f);
                var pill = new Rectangle(S(8), (int)Math.Round(ay), navW - S(16), S(40));
                Fill(g, pill, th.Dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0), S(8));
            }
            int y = S(8);
            SubtleButton(g, "nav-toggle", new Rectangle(S(8), y, S(36), S(36)), Glyphs.Menu, null,
                navExpanded ? "Contraer navegación" : "Expandir navegación", () => { navChoice = !navExpanded; Invalidate(); });
            y += S(48);
            NavItem(g, "perf", Glyphs.Performance, "Rendimiento", navW, ref y);
            NavItem(g, "procs", Glyphs.Processes, "Procesos", navW, ref y);
            NavItem(g, "hist", Glyphs.History, "Historial", navW, ref y);
            NavItem(g, "net", Glyphs.Globe, "Conexiones", navW, ref y);
            NavItem(g, "files", Glyphs.Folder, "Archivos", navW, ref y);
            NavItem(g, "usb", Glyphs.Usb, "Dispositivos USB", navW, ref y);
            NavItem(g, "power", Glyphs.Power, "Encendidos", navW, ref y);
            NavItem(g, "startup", Glyphs.Startup, "Inicio", navW, ref y);
            NavItem(g, "services", Glyphs.Services, "Servicios", navW, ref y);
            NavItem(g, "log", Glyphs.Log, "Registro", navW, ref y);
            int by = ClientSize.Height - S(48);
            NavItem(g, "settings", Glyphs.Settings, "Configuración", navW, ref by);
        }

        void NavItem(Graphics g, string key, string glyph, string label, int navW, ref int y)
        {
            var r = new Rectangle(S(8), y, navW - S(16), S(40));
            bool sel = page == key;
            var h = AddHit("nav-" + key, r, () => Navigate(key), label);
            h.Role = AccessibleRole.PageTab;
            h.Checked = sel;
            if (!navExpanded) h.Tip = label;
            if (!sel) HoverFill(g, h.Key, r, S(8));
            float sc = PressScale(h.Key, 0.97f);
            var st = BeginScale(g, r, sc);
            // Cuadrado de 22 × 22 con el glifo: acento al 20 % si está elegido; si no, blanco al 5 %.
            var sq = new Rectangle(r.X + S(7), r.Y + S(9), S(22), S(22));
            Color ink = th.Dark ? Color.White : Color.Black;
            Fill(g, sq, sel ? Color.FromArgb(51, th.Accent) : Color.FromArgb(13, ink), S(4));
            Glyph(g, glyph, fonts.Glyph12, sq, sel ? th.Accent : Theme.Mix(th.Bg, ink, 0.7f));
            // Mientras el panel se abre o se cierra, el texto aparece recortado por el ancho.
            if (navW >= S(100))
            {
                var tr = new Rectangle(sq.Right + S(12), r.Y, r.Right - sq.Right - S(16), r.Height);
                var cs = ClipP(g, tr);
                DrawText(g, label, fonts.Body, new Rectangle(tr.X, tr.Y, Math.Max(tr.Width, S(160)), tr.Height), th.Text);
                UnclipP(g, cs);
            }
            EndScale(g, st, sc);
            y += S(42);
        }

        // ------------------------------------------------------------------ Rendimiento

        sealed class RingCell
        {
            public string Id, Label, Value, Sub;
            public float Ring;
            public Color Color;
        }

        Color detailColor;
        string detailValue;
        int detailTop, detailBottom, detailH;

        /// <summary>
        /// Rendimiento: una grilla de anillos (la de la isla expandida), la tarjeta de red y debajo el detalle de la
        /// métrica elegida. La página se desplaza si no entra.
        /// </summary>
        void PaintPerformance(Graphics g, Rectangle c, Snapshot snap)
        {
            int pad = S(24);
            PageTitle(g, c, "Rendimiento", null);
            int top = c.Y + S(64);

            // Fila principal: CPU, memoria, disco, GPU, temperatura y batería (las que existan en el equipo).
            var main = new List<RingCell>();
            main.Add(new RingCell { Id = "cpu", Label = "CPU", Ring = snap.Cpu, Value = Pct(snap.Cpu),
                Sub = snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : Short(snap.CpuName), Color = ResColors.Cpu(th) });
            main.Add(new RingCell { Id = "mem", Label = "Memoria", Ring = snap.MemPercent, Value = snap.MemPercent.ToString("0") + "%",
                Sub = Format.Bytes(snap.MemUsed) + " / " + Format.Bytes(snap.MemTotal), Color = ResColors.Memory(th) });
            var disks = snap.Disks.OrderBy(d => d.Number).ToList();
            if (disks.Count > 0) main.Add(DiskCell(disks[0]));
            if (snap.GpuAvailable)
                main.Add(new RingCell { Id = "gpu", Label = "GPU", Ring = snap.Gpu, Value = Pct(snap.Gpu),
                    Sub = double.IsNaN(snap.GpuTemperature) ? Format.Bytes(snap.GpuDedicated) + " en uso" : snap.GpuTemperature.ToString("0") + " °C · " + Format.Bytes(snap.GpuDedicated),
                    Color = ResColors.Gpu(th) });
            if (!double.IsNaN(snap.Temperature))
                main.Add(new RingCell { Id = "temp", Label = "Temperatura", Ring = (float)Math.Min(100, snap.Temperature), Value = snap.Temperature.ToString("0") + "°",
                    Sub = snap.Temperature >= Settings.TempThreshold ? "Alta" : "Normal", Color = ResColors.Temp(th) });
            if (snap.HasBattery)
                main.Add(new RingCell { Id = "bat", Label = "Batería", Ring = snap.BatteryPercent, Value = snap.BatteryPercent.ToString("0") + "%",
                    Sub = snap.OnAc ? (snap.Charging ? "Cargando" : "Enchufada") : "Con batería", Color = ResColors.Battery(th) });
            // Segunda fila: los demás discos y un anillo por adaptador de red.
            var extra = new List<RingCell>();
            for (int i = 1; i < disks.Count; i++) extra.Add(DiskCell(disks[i]));
            foreach (var n in snap.Nets)
            {
                double use = n.LinkSpeed > 0 ? (n.RecvBps + n.SendBps) * 8 * 100 / n.LinkSpeed : 0;
                extra.Add(new RingCell { Id = "net" + n.Id, Label = n.Kind, Ring = (float)Math.Min(100, use), Value = use >= 1 ? use.ToString("0") + "%" : "0%",
                    Sub = "↓ " + Format.Bits(n.RecvBps) + " · ↑ " + Format.Bits(n.SendBps), Color = ResColors.Net(th) });
            }
            var all = main.Concat(extra).ToList();
            if (!all.Any(x => x.Id == resource)) resource = "cpu";

            listView = new Rectangle(c.X, top, c.Width, c.Bottom - top);
            int W = c.Width - 2 * pad, gap = S(4);
            int cols = Math.Max(1, main.Count);
            int cellW = (W - gap * (cols - 1)) / cols, cellH = S(124);
            int rows = 1 + (extra.Count + cols - 1) / cols;
            var net = snap.Nets.FirstOrDefault();
            int netH = net != null ? S(56) + S(14) : 0;
            if (detailH == 0) detailH = S(440);
            listContent = rows * (cellH + gap) - gap + S(14) + netH + detailH + S(16);
            ClampScroll();
            var state = ClipP(g, listView);
            int y = top - ScrollPos;

            for (int i = 0; i < all.Count; i++)
            {
                int col = i < main.Count ? i : (i - main.Count) % cols, row = i < main.Count ? 0 : 1 + (i - main.Count) / cols;
                var r = new Rectangle(c.X + pad + col * (cellW + gap), y + row * (cellH + gap), cellW, cellH);
                RingTile(g, r, all[i]);
            }
            y += rows * (cellH + gap) - gap + S(14);

            if (net != null)
            {
                NetCard(g, new Rectangle(c.X + pad, y, W, S(56)), net);
                y += S(56) + S(14);
            }

            // Detalle de la métrica elegida en una tarjeta (su alto se mide en el cuadro anterior).
            var sel = all.First(x => x.Id == resource);
            detailColor = sel.Color;
            detailValue = sel.Value;
            var card = new Rectangle(c.X + pad, y, W, detailH);
            Card(g, card);
            var dr = new Rectangle(card.X + S(18), card.Y + S(16), card.Width - S(36), card.Height - S(32));
            detailTop = card.Y;
            detailBottom = 0;
            if (resource == "cpu") DetailCpu(g, dr, snap);
            else if (resource == "mem") DetailMem(g, dr, snap);
            else if (resource == "gpu") DetailGpu(g, dr, snap);
            else if (resource == "temp") DetailTemp(g, dr, snap);
            else if (resource == "bat") DetailBattery(g, dr, snap);
            else if (resource.StartsWith("disk")) DetailDisk(g, dr, snap.Disks.FirstOrDefault(d => "disk" + d.Number == resource));
            else DetailNet(g, dr, snap.Nets.FirstOrDefault(n => "net" + n.Id == resource));
            UnclipP(g, state);
            int want = detailBottom > 0 ? detailBottom - detailTop + S(16) : detailH;
            if (Math.Abs(want - detailH) > 1) { detailH = want; Invalidate(); }
            DrawScrollbar(g);
        }

        RingCell DiskCell(DiskMetric d)
        {
            return new RingCell { Id = "disk" + d.Number, Label = d.Title, Ring = d.Active, Value = Pct(d.Active),
                Sub = "L " + Format.Rate(d.ReadBps) + " · E " + Format.Rate(d.WriteBps), Color = ResColors.Disk(th) };
        }

        /// <summary>
        /// Celda de la grilla: anillo de 64 px con el valor adentro, la etiqueta y el subtítulo en el color de la métrica
        /// (una línea, con elipsis). Elegida: fondo del color al 12 % y borde al 33 %.
        /// </summary>
        void RingTile(Graphics g, Rectangle r, RingCell cell)
        {
            string key = "res-" + cell.Id;
            bool sel = cell.Id == resource;
            var vis = Rectangle.Intersect(r, listView);
            if (vis.Height <= 0) return;
            var h = AddHit(key, vis, () => { resource = cell.Id; Invalidate(); }, cell.Label + ", " + cell.Value + ", " + cell.Sub);
            h.Role = AccessibleRole.ListItem; h.Checked = sel;
            var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1);
            if (sel)
            {
                FillRoundP(g, rf, S(8), Color.FromArgb(31, cell.Color));
                StrokeRoundP(g, rf, S(8), Color.FromArgb(84, cell.Color), 1f);
            }
            else HoverFill(g, key, r, S(8));
            float v = Spring("ring-" + cell.Id, cell.Ring, 0.35f);
            var ring = new RectangleF(r.X + (r.Width - S(64)) / 2f, r.Y + S(10), S(64), S(64));
            DrawRing(g, ring, v, cell.Color, 3.5f * s);
            DrawText(g, cell.Value, fonts.Display16, Rectangle.Round(ring), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            DrawText(g, cell.Label, fonts.Caption, new Rectangle(r.X + S(6), r.Y + S(80), r.Width - S(12), S(16)), th.Text, center);
            DrawText(g, cell.Sub, fonts.Small, new Rectangle(r.X + S(6), r.Y + S(97), r.Width - S(12), S(15)), cell.Color, center);
        }

        /// <summary>Tarjeta ancha de red (como en la isla expandida): adaptador, gráfico de 40 s, bajada y subida.</summary>
        void NetCard(Graphics g, Rectangle r, NetMetric n)
        {
            string key = "res-net" + n.Id;
            var vis = Rectangle.Intersect(r, listView);
            if (vis.Height <= 0) return;
            var h = AddHit(key, vis, () => { resource = "net" + n.Id; Invalidate(); }, n.Kind + ": bajada " + Format.Bits(n.RecvBps) + ", subida " + Format.Bits(n.SendBps));
            h.Role = AccessibleRole.ListItem; h.Checked = resource == "net" + n.Id;
            Color c = ResColors.Net(th);
            Card(g, r);
            HoverFill(g, key, r, S(8));
            string name = n.Kind == "Wi-Fi" && !string.IsNullOrEmpty(n.Ssid) ? "Wi-Fi" : n.Kind;
            int nameW = Math.Max(S(70), MeasureText(name, fonts.Body13Strong) + S(16));
            DrawText(g, name, fonts.Body13Strong, new Rectangle(r.X + S(16), r.Y, nameW, r.Height), c);
            string down = "↓ " + Format.Bits(n.RecvBps), up = "↑ " + Format.Bits(n.SendBps);
            int upW = MeasureText(up, fonts.Caption), downW = MeasureText(down, fonts.Body13Strong);
            DrawText(g, up, fonts.Caption, new Rectangle(r.Right - S(16) - upW, r.Y, upW + S(2), r.Height), th.TextSecondary);
            DrawText(g, down, fonts.Body13Strong, new Rectangle(r.Right - S(16) - upW - S(12) - downW, r.Y, downW + S(2), r.Height), th.Text);
            var gr = new Rectangle(r.X + S(16) + nameW, r.Y + S(12), r.Width - S(32) - nameW - upW - downW - S(28), r.Height - S(24));
            if (gr.Width > S(40))
            {
                var hist = Last(Sum(n.SendHistory, n.RecvHistory), 40);
                Graph(g, gr, hist, Math.Max(1, hist.Max()) * 1.2f, c, false, false);
            }
        }

        static string Pct(float v) { return v.ToString("0") + "%"; }

        static float[] Last(float[] a, int n)
        {
            if (a.Length <= n) return a;
            var r = new float[n];
            Array.Copy(a, a.Length - n, r, 0, n);
            return r;
        }

        static float[] Sum(float[] a, float[] b)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] + b[i];
            return r;
        }

        /// <summary>Encabezado del detalle: nombre en el color de la métrica, subtítulo y el valor actual a la derecha.</summary>
        Rectangle DetailHeader(Graphics g, Rectangle r, string title, string right)
        {
            int vw = MeasureText(detailValue ?? "", fonts.Display22) + S(4);
            DrawText(g, title, fonts.Subtitle, new Rectangle(r.X, r.Y, r.Width - vw - S(12), S(28)), detailColor);
            DrawText(g, right ?? "", fonts.Body13, new Rectangle(r.X, r.Y + S(28), r.Width - vw - S(12), S(18)), th.TextSecondary);
            DrawText(g, detailValue ?? "", fonts.Display22, new Rectangle(r.Right - vw, r.Y, vw, S(32)), th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return new Rectangle(r.X, r.Y + S(58), r.Width, r.Height - S(58));
        }

        /// <summary>Alto del gráfico principal del detalle: 150 px.</summary>
        int GraphH(Rectangle b) { return S(150); }

        int BigGraph(Graphics g, Rectangle r, int y, int height, string label, string max, float[] values, float vmax, Color c, float[] second)
        {
            DrawText(g, label, fonts.Small, new Rectangle(r.X, y, r.Width * 2 / 3, S(16)), th.TextTertiary);
            DrawText(g, max, fonts.Small, new Rectangle(r.X + r.Width / 2, y, r.Width / 2, S(16)), th.TextTertiary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var gr = new Rectangle(r.X, y + S(18), r.Width, height);
            FillRoundP(g, gr, S(4), th.GraphBg);
            Graph(g, gr, values, vmax, c, true, false);
            if (second != null)
            {
                // Segunda serie (enviar) como línea punteada sobre la primera.
                var pts = new PointF[second.Length];
                for (int i = 0; i < second.Length; i++)
                    pts[i] = new PointF(gr.X + (gr.Width - 1f) * i / (second.Length - 1), gr.Bottom - 1 - (gr.Height - 2f) * Math.Min(vmax, second[i]) / vmax);
                LinesP(g, pts, c, Math.Max(1.2f, 1.5f * s), 1);
            }
            DrawText(g, "60 segundos", fonts.Small, new Rectangle(r.X, gr.Bottom + S(2), r.Width / 2, S(16)), th.TextTertiary);
            DrawText(g, "0", fonts.Small, new Rectangle(r.X + r.Width / 2, gr.Bottom + S(2), r.Width / 2, S(16)), th.TextTertiary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return gr.Bottom + S(30);
        }

        /// <summary>Estadísticas en una grilla autoajustable (mínimo 130 px por celda): etiqueta de 11 px y valor de 18 px.</summary>
        void Stats(Graphics g, Rectangle r, int y, IList<string[]> big, IList<string[]> small)
        {
            var items = big.Concat(small).ToList();
            int per = Math.Max(1, r.Width / S(130));
            int colW = r.Width / per, rowH = S(48);
            for (int i = 0; i < items.Count; i++)
            {
                int x = r.X + (i % per) * colW, yy = y + (i / per) * rowH;
                string label = items[i][0].TrimEnd(':');
                DrawText(g, label, fonts.Small, new Rectangle(x, yy, colW - S(10), S(16)), Mix55);
                DrawText(g, items[i][1], fonts.Display18, new Rectangle(x, yy + S(16), colW - S(10), S(26)), th.Text);
            }
            detailBottom = y + ((items.Count + per - 1) / per) * rowH;
        }

        void DetailCpu(Graphics g, Rectangle r, Snapshot snap)
        {
            var b = DetailHeader(g, r, "CPU", snap.CpuName);
            int gh = GraphH(b);
            int y = BigGraph(g, b, b.Y, gh, "% de uso", "100 %", snap.CpuHistory, 100, ResColors.Cpu(th), null);
            Stats(g, b, y, new[]
            {
                new[] { "Uso", Format.Percent(snap.Cpu) },
                new[] { "Velocidad", snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : "—" },
                new[] { "Procesos", snap.ProcessCount.ToString("N0", Format.Es) },
                new[] { "Subprocesos", snap.ThreadCount.ToString("N0", Format.Es) },
                new[] { "Identificadores", snap.HandleCount.ToString("N0", Format.Es) },
                new[] { "Tiempo activo", Format.Uptime(snap.Uptime) },
            }, new[]
            {
                new[] { "Velocidad base:", snap.CpuBaseMhz > 0 ? Format.Ghz(snap.CpuBaseMhz) : "—" },
                new[] { "Núcleos:", snap.Cores > 0 ? snap.Cores.ToString() : "—" },
                new[] { "Procesadores lógicos:", snap.Logical.ToString() },
            });
        }

        void DetailMem(Graphics g, Rectangle r, Snapshot snap)
        {
            var b = DetailHeader(g, r, "Memoria", Format.Bytes(snap.MemTotal));
            int gh = GraphH(b);
            int y = BigGraph(g, b, b.Y, gh, "Uso de memoria", Format.Bytes(snap.MemTotal), snap.MemHistory, 100, ResColors.Memory(th), null);
            Stats(g, b, y, new[]
            {
                new[] { "En uso", Format.Bytes(snap.MemUsed) },
                new[] { "Disponible", Format.Bytes(snap.MemTotal - snap.MemUsed) },
                new[] { "Confirmada", Format.Bytes(snap.CommitUsed) + " / " + Format.Bytes(snap.CommitLimit) },
                new[] { "En caché", snap.MemCached > 0 ? Format.Bytes(snap.MemCached) : "—" },
            }, new[]
            {
                new[] { "Uso:", Format.Percent(snap.MemPercent) },
                new[] { "Total instalado:", Format.Bytes(snap.MemTotal) },
            });
        }

        void DetailDisk(Graphics g, Rectangle r, DiskMetric d)
        {
            if (d == null) return;
            var b = DetailHeader(g, r, d.Title, d.Name);
            int gh = S(150);
            int y = BigGraph(g, b, b.Y, gh, "Tiempo activo", "100 %", d.ActivityHistory, 100, ResColors.Disk(th), null);
            float tmax = Math.Max(1048576, d.TransferHistory.Max() * 1.2f);
            y = BigGraph(g, b, y, S(90), "Velocidad de transferencia del disco", Format.Rate(tmax), d.TransferHistory, tmax, ResColors.Disk(th), null);
            var big = new List<string[]>
            {
                new[] { "Tiempo activo", Format.Percent(d.Active) },
                new[] { "Tiempo de respuesta", d.ResponseMs.ToString("0.0", Format.Es) + " ms" },
                new[] { "Lectura", Format.Rate(d.ReadBps) },
                new[] { "Escritura", Format.Rate(d.WriteBps) },
            };
            var small = new List<string[]>
            {
                new[] { "Capacidad:", d.Capacity > 0 ? Format.Bytes(d.Capacity) : "—" },
                new[] { "Tipo:", string.IsNullOrEmpty(d.Kind) ? "—" : d.Kind },
                new[] { "Unidades:", string.IsNullOrEmpty(d.Letters) ? "sin letra" : d.Letters },
                new[] { "Espacio libre:", d.VolumeTotal > 0 ? Format.Bytes(d.VolumeFree) + " de " + Format.Bytes(d.VolumeTotal) : "—" },
            };
            // Salud SMART (la lee el ayudante con privilegios).
            var hc = host.Helper;
            SmartInfo sm;
            if (hc != null && hc.Smart.TryGetValue(d.Number, out sm))
            {
                if (sm.Temp.HasValue) big.Add(new[] { "Temperatura", sm.Temp.Value + " °C" });
                small.Add(new[] { "Salud (SMART):", sm.HealthName });
                if (sm.Wear.HasValue) small.Add(new[] { "Desgaste:", sm.Wear.Value + " %" });
                if (sm.Hours.HasValue) small.Add(new[] { "Horas encendido:", sm.Hours.Value.ToString("N0", Format.Es) + " h" });
                if (sm.ReadErrors.HasValue || sm.WriteErrors.HasValue)
                    small.Add(new[] { "Errores sin corregir:", ((sm.ReadErrors ?? 0) + (sm.WriteErrors ?? 0)).ToString("N0", Format.Es) });
                if (sm.TempMax.HasValue) small.Add(new[] { "Temperatura máxima:", sm.TempMax.Value + " °C" });
            }
            Stats(g, b, y, big, small);
        }

        void DetailNet(Graphics g, Rectangle r, NetMetric n)
        {
            if (n == null) return;
            var b = DetailHeader(g, r, n.Kind, n.Description);
            float max = Math.Max(125000, Math.Max(n.SendHistory.Max(), n.RecvHistory.Max()) * 1.2f);
            int gh = GraphH(b);
            int y = BigGraph(g, b, b.Y, gh, "Rendimiento (línea punteada: envío)", Format.Bits(max), n.RecvHistory, max, ResColors.Net(th), n.SendHistory);
            Stats(g, b, y, new[]
            {
                new[] { "Enviar", Format.Bits(n.SendBps) },
                new[] { "Recibir", Format.Bits(n.RecvBps) },
            }, new[]
            {
                new[] { "Adaptador:", n.Name },
                new[] { "SSID:", string.IsNullOrEmpty(n.Ssid) ? "—" : n.Ssid },
                new[] { "Tipo de conexión:", n.Kind },
                new[] { "Dirección IPv4:", n.Ipv4 ?? "—" },
                new[] { "Velocidad de vínculo:", n.LinkSpeed > 0 ? Format.Bits(n.LinkSpeed / 8.0).Replace("bps", "bps") : "—" },
            });
        }

        // ------------------------------------------------------------------ búsqueda

        void SearchBox(Graphics g, Rectangle r, string placeholder)
        {
            searchBox = r;
            bool focused = search.Focused;
            Color fill = SearchFill;
            search.BackColor = fill;
            { var rr = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1); FillRoundP(g, rr, S(4), fill); StrokeRoundP(g, rr, S(4), ControlStroke, 1f); }
            // Línea inferior de acento cuando tiene el foco, como los cuadros de texto de Windows 11.
            if (focused) Fill(g, new Rectangle(r.X + S(1), r.Bottom - S(2), r.Width - S(2), S(2)), th.Accent, S(1));
            Glyph(g, Glyphs.Search, fonts.Glyph12, new Rectangle(r.Right - S(32), r.Y, S(24), r.Height), th.TextSecondary);
            int th1 = search.PreferredHeight;
            var tb = new Rectangle(r.X + S(10), r.Y + (r.Height - th1) / 2, r.Width - S(46), th1);
            if (search.Bounds != tb) search.Bounds = tb;
            if (search.Text.Length == 0 && !focused)
                DrawText(g, placeholder, fonts.Body, new Rectangle(r.X + S(11), r.Y, r.Width - S(46), r.Height), th.TextSecondary);
            AddHit("search", r, () => search.Focus(), placeholder).Focusable = false;
        }

        string Query { get { return (search.Text ?? "").Trim(); } }

        // ------------------------------------------------------------------ desplazamiento

        // Desplazamiento: "scroll" guarda el destino y lo que se ve lo sigue con un resorte (suave, como en iOS).
        // Al llegar a un borde la lista se pasa un poco y vuelve (rebote).
        int ScrollTarget { get { int v; return scroll.TryGetValue(page, out v) ? v : 0; } set { scroll[page] = value; } }

        /// <summary>Posición que se dibuja. Asignarla salta sin animar (por ejemplo, al cambiar un filtro).</summary>
        int ScrollPos
        {
            get { return (int)Math.Round(Spring("scroll:" + page, ScrollTarget, 0.3f, 1f)); }
            set { ScrollTarget = value; Spring("scroll:" + page, value); SetSpring("scroll:" + page, value); }
        }

        void ClampScroll()
        {
            int max = Math.Max(0, listContent - listView.Height);
            if (ScrollTarget > max) ScrollTarget = max;
            if (ScrollTarget < 0) ScrollTarget = 0;
        }

        void DrawScrollbar(Graphics g)
        {
            thumb = Rectangle.Empty;
            if (listContent <= listView.Height) return;
            int trackH = listView.Height - S(8);
            int h = Math.Max(S(32), trackH * listView.Height / listContent);
            int y = listView.Y + S(4) + (trackH - h) * ScrollPos / Math.Max(1, listContent - listView.Height);
            bool hot = draggingThumb || new Rectangle(listView.Right - S(14), listView.Y, S(14), listView.Height).Contains(PointToClient(Cursor.Position));
            int w = hot ? S(6) : S(2);
            thumb = new Rectangle(listView.Right - S(10), y, S(10), h);
            Fill(g, new Rectangle(listView.Right - S(4) - w, y, w, h), th.TextTertiary, w / 2f);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = Math.Max(0, listContent - listView.Height);
            int want = ScrollTarget - e.Delta * S(96) / 120;
            // Contra el borde: el destino queda en el borde y el resorte recibe un empujón (se pasa y vuelve).
            if (want < 0 && ScrollTarget <= 0 || want > max && ScrollTarget >= max)
                KickSpring("scroll:" + page, (want < 0 ? -1 : 1) * Math.Min(S(900), Math.Abs(e.Delta) * S(6)));
            ScrollTarget = Math.Max(0, Math.Min(max, want));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && thumb.Contains(e.Location))
            {
                draggingThumb = true; dragStartY = e.Y; dragStartScroll = ScrollPos; Capture = true;
                return;
            }
            if (!searchBox.Contains(e.Location) && search.Focused) ActiveControl = null;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (draggingThumb)
            {
                int trackH = listView.Height - S(8);
                int h = Math.Max(S(32), trackH * listView.Height / Math.Max(1, listContent));
                int range = Math.Max(1, trackH - h);
                ScrollPos = dragStartScroll + (e.Y - dragStartY) * (listContent - listView.Height) / range;
                ClampScroll();
                Invalidate();
                return;
            }
            base.OnMouseMove(e);
            if (!headerEye.IsEmpty)
            {
                float dx = e.X - (headerEye.X + headerEye.Width / 2f), dy = e.Y - (headerEye.Y + headerEye.Height / 2f);
                Eye.Shared.LookFrom(this, dx * dx + dy * dy <= S(500) * (float)S(500), dx / S(160) * 5.5f, dy / S(160) * 4f);
                if (Eye.Shared.Moving(Clock.Ms)) RequestMotion();
            }
            if (page == "hist") Invalidate();
            else if (listView.Contains(e.Location) || draggingThumb) Invalidate(new Rectangle(listView.Right - S(16), listView.Y, S(16), listView.Height));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (draggingThumb) { draggingThumb = false; Capture = false; Invalidate(); return; }
            base.OnMouseUp(e);
        }

        // ------------------------------------------------------------------ Procesos

        void PaintProcesses(Graphics g, Rectangle c, Snapshot snap)
        {
            int pad = S(24);
            PageTitle(g, c, "Procesos", null);
            var sel = snap.Procs.FirstOrDefault(p => p.Pid == selectedPid);
            int bw = S(150);
            StandardButton(g, "end", new Rectangle(c.Right - pad - bw, c.Y + S(16), bw, S(32)), null, "Finalizar tarea", true, sel != null,
                () => { if (sel != null) host.EndTask(sel, this); });
            SearchBox(g, new Rectangle(c.Right - pad - bw - S(8) - S(200), c.Y + S(16), S(200), S(32)), "Buscar un proceso");

            // Encabezados de columna
            int hy = c.Y + S(64);
            var cols = Columns(c);
            Header(g, cols[0], hy, "Nombre", "name", false, null);
            Header(g, cols[1], hy, "CPU", "cpu", true, Format.Percent(snap.Cpu));
            Header(g, cols[2], hy, "Memoria", "mem", true, snap.MemPercent.ToString("0") + " %");
            Header(g, cols[3], hy, "Disco", "disk", true, Format.Rate(snap.Procs.Sum(p => p.DiskBps)));
            Header(g, cols[5], hy, "GPU", "gpu", true, snap.GpuAvailable ? Format.Percent(snap.Gpu) : null);
            Header(g, cols[4], hy, "PID", "pid", true, null);
            if (NetColumn) Header(g, cols[6], hy, "Red", "net", true, Format.Bits(snap.Procs.Sum(p => NetRate(p.Pid))));
            LineP(g, HeaderLine, 1, c.X + S(12), hy + S(48), c.Right - S(12), hy + S(48));

            listView = new Rectangle(c.X, hy + S(49), c.Width, c.Bottom - hy - S(49));
            string q = Query;
            IEnumerable<ProcMetric> all = snap.Procs.Where(p => p.Name != "Idle");
            if (q.Length > 0)
                all = all.Where(p => p.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Pid.ToString() == q);
            var apps = Sort(all.Where(p => p.IsApp)).ToList();
            var bg = Sort(all.Where(p => !p.IsApp)).ToList();

            int rowH = S(36);
            listContent = S(40) * 2 + (apps.Count + bg.Count) * rowH + S(8);
            ClampScroll();
            var state = ClipP(g, listView);
            int y = listView.Y - ScrollPos;
            y = Group(g, c, cols, y, "Aplicaciones", apps, rowH, snap);
            y = Group(g, c, cols, y, "Procesos en segundo plano", bg, rowH, snap);
            UnclipP(g, state);
            if (apps.Count + bg.Count == 0)
                DrawText(g, q.Length > 0 ? "Ningún proceso coincide con \"" + q + "\"" : "Sin procesos", fonts.Body, listView, th.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawScrollbar(g);
        }

        Rectangle[] Columns(Rectangle c)
        {
            int right = c.Right - S(24);
            int pidW = S(76), gpuW = S(76), diskW = S(100), memW = S(104), cpuW = S(76), netW = NetColumn ? S(104) : 0;
            int x4 = right - pidW, x5 = x4 - gpuW, x6 = x5 - netW, x3 = x6 - diskW, x2 = x3 - memW, x1 = x2 - cpuW;
            return new[]
            {
                new Rectangle(c.X + S(12), 0, x1 - c.X - S(12), 0),
                new Rectangle(x1, 0, cpuW, 0), new Rectangle(x2, 0, memW, 0),
                new Rectangle(x3, 0, diskW, 0), new Rectangle(x4, 0, pidW, 0),
                new Rectangle(x5, 0, gpuW, 0), new Rectangle(x6, 0, netW, 0),
            };
        }

        /// <summary>El tráfico por proceso solo existe con el ayudante con privilegios conectado.</summary>
        bool NetColumn { get { var h = host.Helper; return h != null && h.Connected; } }

        double NetRate(int pid)
        {
            var h = host.Helper;
            NetTraffic t;
            return h != null && h.NetByPid.TryGetValue(pid, out t) ? t.SentBps + t.RecvBps : 0;
        }

        void Header(Graphics g, Rectangle col, int y, string label, string key, bool right, string total)
        {
            var r = new Rectangle(col.X, y, col.Width, S(48));
            string hk = "col-" + key;
            AddHit(hk, r, () =>
            {
                if (sortCol == key) sortAsc = !sortAsc; else { sortCol = key; sortAsc = key == "name"; }
                Invalidate();
            }, "Ordenar por " + label).Role = AccessibleRole.ColumnHeader;
            HoverFill(g, hk, r, S(4));
            var flags = right ? TextFormatFlags.Right | TextFormatFlags.VerticalCenter : TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            var inner = new Rectangle(r.X + S(10), r.Y, r.Width - S(20), r.Height);
            if (total != null)
            {
                DrawText(g, total, fonts.Body13Strong, new Rectangle(inner.X, r.Y + S(6), inner.Width, S(20)), th.Text, flags);
                DrawText(g, label, fonts.Caption, new Rectangle(inner.X, r.Y + S(26), inner.Width, S(16)), Mix55, flags);
            }
            else DrawText(g, label, fonts.Caption, new Rectangle(inner.X, r.Y + S(26), inner.Width, S(16)), Mix55, flags);
            if (sortCol == key)
                Glyph(g, sortAsc ? Glyphs.ChevronUp : Glyphs.ChevronDown, fonts.Glyph12,
                    new Rectangle(r.X + r.Width / 2 - S(8), r.Y, S(16), S(10)), Mix55);
        }

        IEnumerable<ProcMetric> Sort(IEnumerable<ProcMetric> list)
        {
            switch (sortCol)
            {
                case "name": return sortAsc ? list.OrderBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase) : list.OrderByDescending(p => p.Display, StringComparer.CurrentCultureIgnoreCase);
                case "mem": return sortAsc ? list.OrderBy(p => p.Memory) : list.OrderByDescending(p => p.Memory);
                case "disk": return sortAsc ? list.OrderBy(p => p.DiskBps) : list.OrderByDescending(p => p.DiskBps);
                case "gpu": return sortAsc ? list.OrderBy(p => p.Gpu) : list.OrderByDescending(p => p.Gpu);
                case "net": return sortAsc ? list.OrderBy(p => NetRate(p.Pid)) : list.OrderByDescending(p => NetRate(p.Pid));
                case "pid": return sortAsc ? list.OrderBy(p => p.Pid) : list.OrderByDescending(p => p.Pid);
                default: return sortAsc ? list.OrderBy(p => p.Cpu) : list.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory);
            }
        }

        int Group(Graphics g, Rectangle c, Rectangle[] cols, int y, string title, List<ProcMetric> list, int rowH, Snapshot snap)
        {
            if (list.Count == 0) return y;
            DrawText(g, title + " (" + list.Count + ")", fonts.Body13Strong, new Rectangle(c.X + S(22), y + S(16), S(400), S(18)), th.Text);
            y += S(40);
            foreach (var p in list)
            {
                if (y + rowH >= listView.Y && y <= listView.Bottom) ProcRow(g, c, cols, new Rectangle(c.X + S(12), y, c.Width - S(36), rowH), p, snap);
                y += rowH;
            }
            return y;
        }

        void ProcRow(Graphics g, Rectangle c, Rectangle[] cols, Rectangle r, ProcMetric p, Snapshot snap)
        {
            string key = "proc" + p.Pid;
            bool sel = p.Pid == selectedPid;
            var vis = Rectangle.Intersect(r, listView);
            var h = AddHit(key, vis, () => { selectedPid = p.Pid; Invalidate(); }, p.Display);
            h.Role = AccessibleRole.Row;
            h.Checked = sel;
            h.RightClick = pt =>
            {
                selectedPid = p.Pid;
                Invalidate();
                var m = Menus.ForProcess(th, s, host, p, this);
                m.Closed += (o, e) => BeginInvoke(new Action(m.Dispose));
                m.Show(this, pt);
            };
            if (sel) SelectedRow(g, r);
            else HoverFill(g, key, r, S(4));

            // Mapa de calor: la celda se tiñe con el color de su métrica, más intenso cuanto más alto el valor.
            if (p.Cpu > 1) Heat(g, cols[1], r, p.Cpu / 25f, ResColors.Cpu(th));
            if (p.Memory > 150L * 1048576) Heat(g, cols[2], r, (float)(p.Memory / (2048.0 * 1048576)), ResColors.Memory(th));
            if (p.DiskBps > 51200) Heat(g, cols[3], r, (float)(p.DiskBps / (20.0 * 1048576)), ResColors.Disk(th));
            if (p.Gpu > 1) Heat(g, cols[5], r, p.Gpu / 25f, ResColors.Gpu(th));

            var ir = new Rectangle(r.X + S(14), r.Y + (r.Height - S(16)) / 2, S(16), S(16));
            if (p.Icon != null) DrawIcon(g, p.Icon, ir);
            else Glyph(g, Glyphs.App, fonts.Glyph16, ir, th.TextSecondary);
            DrawText(g, p.Display, fonts.Body, new Rectangle(ir.Right + S(12), r.Y, cols[0].Right - ir.Right - S(16), r.Height), th.Text);
            var right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            DrawText(g, Format.Percent(p.Cpu), fonts.Body, Cell(cols[1], r), th.Text, right);
            DrawText(g, Format.Bytes(p.Memory), fonts.Body, Cell(cols[2], r), th.Text, right);
            DrawText(g, p.DiskBps >= 51200 ? Format.Rate(p.DiskBps) : "0 MB/s", fonts.Body, Cell(cols[3], r), p.DiskBps >= 51200 ? th.Text : Mix55, right);
            DrawText(g, p.Pid.ToString(), fonts.Body, Cell(cols[4], r), Mix55, right);
            DrawText(g, Format.Percent(p.Gpu), fonts.Body, Cell(cols[5], r), th.Text, right);
            if (cols[6].Width > 0)
            {
                double net = NetRate(p.Pid);
                if (net >= 1024) Heat(g, cols[6], r, (float)(net / (10.0 * 1048576)), ResColors.Net(th));
                DrawText(g, net >= 1024 ? Format.Bits(net) : "0 Mbps", fonts.Body, Cell(cols[6], r), th.Text, right);
            }
        }

        Rectangle Cell(Rectangle col, Rectangle row) { return new Rectangle(col.X + S(10), row.Y, col.Width - S(20), row.Height); }

        /// <summary>Celda teñida con el color de la métrica: del 10 % al 35 % según la intensidad (0 a 1).</summary>
        void Heat(Graphics g, Rectangle col, Rectangle row, float intensity, Color c)
        {
            intensity = Math.Max(0, Math.Min(1, intensity));
            var cell = new Rectangle(col.X + S(4), row.Y + S(4), col.Width - S(8), row.Height - S(8));
            Fill(g, cell, Color.FromArgb((int)(255 * (0.10f + 0.25f * intensity)), c), S(3));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F) && page != "perf") { search.Focus(); search.SelectAll(); return true; }
            if (keyData == Keys.Delete && page == "procs" && !search.Focused)
            {
                var snap = host.Latest;
                var p = snap == null ? null : snap.Procs.FirstOrDefault(x => x.Pid == selectedPid);
                if (p != null) host.EndTask(p, this);
                return true;
            }
            if (page == "procs" && !search.Focused && (keyData == Keys.Up || keyData == Keys.Down))
            {
                MoveSelection(keyData == Keys.Down ? 1 : -1);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MoveSelection(int dir)
        {
            var snap = host.Latest;
            if (snap == null) return;
            var rows = hits.Where(h => h.Key.StartsWith("proc")).Select(h => int.Parse(h.Key.Substring(4))).ToList();
            if (rows.Count == 0) return;
            int i = rows.IndexOf(selectedPid);
            i = i < 0 ? 0 : Math.Max(0, Math.Min(rows.Count - 1, i + dir));
            selectedPid = rows[i];
            Invalidate();
        }

        // ------------------------------------------------------------------ Registro

        void PaintLog(Graphics g, Rectangle c)
        {
            int pad = S(24);
            PageTitle(g, c, "Registro", null);
            int bw = S(108);
            StandardButton(g, "logfile", new Rectangle(c.Right - pad - bw, c.Y + S(16), bw, S(32)), null, "Archivo", false, true, host.OpenLogFile);
            StandardButton(g, "logexport", new Rectangle(c.Right - pad - 2 * bw - S(8), c.Y + S(16), bw, S(32)), null, "Exportar", true, true,
                () => ShowExportMenu(new Point(c.Right - pad - 2 * bw - S(8), c.Y + S(50))));
            SearchBox(g, new Rectangle(c.Right - pad - 2 * bw - S(16) - S(200), c.Y + S(16), S(200), S(32)), "Buscar en el registro");

            // Filtros
            int fy = c.Y + S(64);
            int sel = logFilter.HasValue ? Array.IndexOf(LogCategories, logFilter.Value) + 1 : 0;
            var labels = new[] { "Todo" }.Concat(LogCategories.Select(ActivityEntry.CategoryName)).ToArray();
            int used = Chips(g, c.X + pad, fy, c.Right - pad, "log", labels, sel, i => logFilter = i == 0 ? (Category?)null : LogCategories[i - 1]);

            listView = new Rectangle(c.X, fy + used, c.Width, c.Bottom - fy - used);
            string q = Query;
            // La lista filtrada y ordenada se reutiliza mientras no lleguen eventos nuevos ni cambien los filtros.
            string logKey = Activity.Version + "|" + logFilter + "|" + q;
            if (logKey != logCacheKey)
            {
                logCacheKey = logKey;
                logCache = Activity.Snapshot().Where(e => (!logFilter.HasValue || e.Category == logFilter.Value) &&
                    (q.Length == 0 || e.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).OrderByDescending(e => e.Time).ToList();
                logDays = logCache.Select(e => e.Time.Date).Distinct().Count();
            }
            var entries = logCache;

            int rowH = S(36), dayH = S(40);
            int days = logDays;
            listContent = entries.Count * rowH + days * dayH + S(8);
            ClampScroll();
            var state = ClipP(g, listView);
            int y = listView.Y - ScrollPos;
            DateTime? day = null;
            foreach (var e in entries)
            {
                if (day != e.Time.Date)
                {
                    day = e.Time.Date;
                    if (y + dayH >= listView.Y && y <= listView.Bottom)
                        DrawText(g, DayLabel(e.Time.Date), fonts.Body13Strong, new Rectangle(c.X + S(22), y + S(16), S(400), S(18)), th.Text);
                    y += dayH;
                }
                if (y + rowH >= listView.Y && y <= listView.Bottom) LogRow(g, new Rectangle(c.X + S(12), y, c.Width - S(36), rowH), e);
                y += rowH;
            }
            UnclipP(g, state);
            if (entries.Count == 0)
                DrawText(g, q.Length > 0 || logFilter.HasValue ? "No hay eventos que coincidan" : "Todavía no hay actividad registrada",
                    fonts.Body, listView, th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawScrollbar(g);
        }

        static string DayLabel(DateTime d)
        {
            if (d == DateTime.Today) return "Hoy";
            if (d == DateTime.Today.AddDays(-1)) return "Ayer";
            string s = d.ToString("dddd d 'de' MMMM", Format.Es);
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        void Chip(Graphics g, ref int x, int y, string text, Category? cat)
        {
            bool sel = logFilter == cat;
            int w = MeasureText(text, fonts.Body) + S(28);
            var r = new Rectangle(x, y, w, S(32));
            string key = "chip-" + text;
            var h = AddHit(key, r, () => { logFilter = cat; ScrollPos = 0; Invalidate(); }, "Filtrar: " + text);
            h.Checked = sel;
            if (sel) Fill(g, r, IsHover(key) ? th.AccentHover : th.Accent, r.Height / 2f);
            else
            {
                { var rr = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1); FillRoundP(g, rr, r.Height / 2f, th.ControlFill); HoverFill(g, key, Rectangle.Round(rr), r.Height / 2f); StrokeRoundP(g, rr, r.Height / 2f, ControlStroke, 1f); }
            }
            DrawText(g, text, fonts.Body, r, sel ? th.OnAccent : th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += w + S(8);
        }

        void LogRow(Graphics g, Rectangle r, ActivityEntry e)
        {
            string key = "log" + e.Time.Ticks + e.Message.GetHashCode();
            var h = AddHit(key, Rectangle.Intersect(r, listView), null, e.Time.ToString("HH:mm:ss") + " " + e.Message);
            h.Role = AccessibleRole.ListItem;
            h.Tip = e.Message.Length > 80 ? e.Message : null;
            h.RightClick = pt =>
            {
                var m = FluentMenu.Create(th, s);
                FluentMenu.Add(m, "Copiar", Glyphs.Copy, true, () =>
                {
                    try { Clipboard.SetText(e.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ActivityEntry.CategoryName(e.Category) + "  " + e.Message); } catch { }
                });
                m.Closed += (o, a) => BeginInvoke(new Action(m.Dispose));
                m.Show(this, pt);
            };
            HoverFill(g, key, r, S(4));
            // Grilla: hora (70 px) | etiqueta del tipo (108 px) | texto.
            DrawText(g, e.Time.ToString("HH:mm:ss"), fonts.Body13, new Rectangle(r.X + S(10), r.Y, S(70), r.Height), Mix55);
            string cat = ActivityEntry.CategoryName(e.Category);
            Color cc = e.Severity == Severity.Error ? th.Critical : ResColors.Category(th, e.Category);
            int pw = Math.Min(S(100), MeasureText(cat, fonts.Small) + S(18));
            Pill(g, new Rectangle(r.X + S(80), r.Y + (r.Height - S(20)) / 2, pw, S(20)), cat, cc);
            DrawText(g, e.Message, fonts.Body13, new Rectangle(r.X + S(188), r.Y, r.Width - S(198), r.Height), th.Text);
        }

        // ------------------------------------------------------------------ posición guardada

        const string PlacementKey = @"Software\Panoptic";

        void RestorePlacement()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var def = new Rectangle(wa.X + (wa.Width - 1040) / 2, wa.Y + (wa.Height - 700) / 2, 1040, 700);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(PlacementKey))
                {
                    var v = k == null ? null : k.GetValue("MainWindow") as string;
                    if (v != null)
                    {
                        var p = v.Split(',').Select(int.Parse).ToArray();
                        var r = new Rectangle(p[0], p[1], p[2], p[3]);
                        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) def = r;
                        if (p.Length > 4 && p[4] == 1) WindowState = FormWindowState.Maximized;
                    }
                }
            }
            catch { }
            Bounds = def;
        }

        void SavePlacement()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                using (var k = Registry.CurrentUser.CreateSubKey(PlacementKey))
                    k.SetValue("MainWindow", r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," + (WindowState == FormWindowState.Maximized ? 1 : 0));
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refresh.Dispose();
            base.Dispose(disposing);
        }
    }
}
