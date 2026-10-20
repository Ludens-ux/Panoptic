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
            refresh.Tick += (o, e) => { if (WindowState != FormWindowState.Minimized) Invalidate(); };

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
            search.BackColor = th.Dark ? Theme.Hex(0x2D2D2D) : Theme.Hex(0xFFFFFF);
            search.ForeColor = th.Text;
        }

        protected override int Backdrop { get { return Native.DWMSBT_MICA; } }

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
            SetSpring("page-enter", 0f);
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
            int navW = (int)Math.Round(Spring("nav-w", navExpanded ? S(220) : S(52), 0.34f, 0.9f));
            var content = new Rectangle(navW, 0, ClientSize.Width - navW, ClientSize.Height);
            // Fondo: cada zona se pinta una sola vez con un relleno rápido (antes, limpiar y repintar costaba más de 4 ms por cuadro).
            FastFill(g, new Rectangle(0, 0, navW, ClientSize.Height), th.Clear);
            FastFill(g, content, th.Layer);
            // Esquina superior izquierda redondeada de la capa (área de contenido de Windows 11).
            int rad = S(8);
            FastFill(g, new Rectangle(content.X, content.Y, rad, rad), th.Clear);
            // El cuarto de círculo de la esquina como polígono (sirve igual con GDI+ y con Direct2D).
            var corner = new PointF[11];
            corner[0] = new PointF(content.X + rad, content.Y + rad);
            for (int i = 0; i <= 9; i++)
            {
                double a = Math.PI + Math.PI / 2 * i / 9;
                corner[i + 1] = new PointF(content.X + rad + rad * (float)Math.Cos(a), content.Y + rad + rad * (float)Math.Sin(a));
            }
            AreaP(g, corner, th.Layer);
            var edge = new PointF[10];
            for (int i = 0; i < 10; i++) edge[i] = new PointF(corner[i + 1].X + .5f, corner[i + 1].Y + .5f);
            LinesP(g, edge, th.LayerStroke, 1);
            LineP(g, th.LayerStroke, 1, content.X + rad, content.Y + .5f, content.Right, content.Y + .5f);
            LineP(g, th.LayerStroke, 1, content.X + .5f, content.Y + rad, content.X + .5f, content.Bottom);
            Stat("1 fondo", psw);
            PaintNav(g, navW);
            Stat("2 nav", psw);


            // El registro de archivos solo corre mientras esta página está a la vista.
            if (host.Helper != null) host.Helper.WantFiles = page == "files";
            search.Visible = page == "procs" || page == "net" || page == "files" || page == "usb" || page == "power" || page == "startup" || page == "services" || page == "log";
            var snap = host.Latest;
            if (snap == null)
            {
                DrawText(g, "Midiendo…", fonts.Body, content, th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // Entrada de página: sube unos píxeles y aparece con un fundido (se puede interrumpir con otro clic).
            float enter = Spring("page-enter", 1f, 0.42f, 0.88f);
            var pageState = ClipP(g, content);
            if (enter < 0.999f) TranslateP(g, pageState, 0, (float)Math.Round((1 - enter) * S(28)));
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
            if (enter < 0.999f)
            {
                int a = (int)(255 * Math.Max(0, 1 - enter * 1.6f));
                if (a > 0) FillRoundP(g, new Rectangle(content.X + 1, content.Y + 1, content.Width - 1, content.Height - 1), 0, Color.FromArgb(a, th.Layer.R, th.Layer.G, th.Layer.B));
            }
            Stat("4 pagina", psw);
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
            int selY = page == "settings" ? ClientSize.Height - S(48) : idx >= 0 ? S(48) + idx * S(44) : -1;
            if (selY >= 0 && navSelY != selY)
            {
                if (navSelY < 0) SetSpring("nav-sel", selY);
                navSelY = selY;
            }
            // Píldora de selección: se desliza hasta el ítem elegido y la marca de acento se estira mientras viaja.
            if (navSelY >= 0)
            {
                float ay = Spring("nav-sel", navSelY, 0.36f, 0.82f);
                var pill = new Rectangle(S(4), (int)Math.Round(ay), navW - S(8), S(40));
                Fill(g, pill, th.Dark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0), S(4));
                float stretch = Math.Min(S(18), Math.Abs(ay - navSelY) * 0.35f);
                int mh = (int)(S(16) + stretch);
                Fill(g, new Rectangle(pill.X, pill.Y + pill.Height / 2 - mh / 2, S(3), mh), th.Accent, S(1.5f));
            }
            int y = S(4);
            SubtleButton(g, "nav-toggle", new Rectangle(S(6), y, S(40), S(36)), Glyphs.Menu, null,
                navExpanded ? "Contraer navegación" : "Expandir navegación", () => { navChoice = !navExpanded; Invalidate(); });
            y += S(44);
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
            var r = new Rectangle(S(4), y, navW - S(8), S(40));
            bool sel = page == key;
            var h = AddHit("nav-" + key, r, () => Navigate(key), label);
            h.Role = AccessibleRole.PageTab;
            h.Checked = sel;
            if (!navExpanded) h.Tip = label;
            HoverFill(g, h.Key, r, S(4));
            float sc = PressScale(h.Key, 0.97f);
            var st = BeginScale(g, r, sc);
            Glyph(g, glyph, fonts.Glyph16, new Rectangle(r.X + S(12), r.Y, S(20), r.Height), th.Text);
            // Mientras el panel se abre o se cierra, el texto aparece recortado por el ancho.
            if (navW >= S(100))
            {
                var tr = new Rectangle(r.X + S(44), r.Y, r.Width - S(48), r.Height);
                var cs = ClipP(g, tr);
                DrawText(g, label, fonts.Body, new Rectangle(tr.X, tr.Y, Math.Max(tr.Width, S(160)), tr.Height), th.Text);
                UnclipP(g, cs);
            }
            EndScale(g, st, sc);
            y += S(44);
        }

        // ------------------------------------------------------------------ Rendimiento

        void PaintPerformance(Graphics g, Rectangle c, Snapshot snap)
        {
            int pad = S(24);
            DrawText(g, "Rendimiento", fonts.Subtitle, new Rectangle(c.X + pad, c.Y + S(16), S(300), S(32)), th.Text);
            int top = c.Y + S(64);

            // Lista de recursos
            int listW = S(236);
            var items = new List<Tuple<string, string, string, float[], float, Color>>();
            items.Add(Tuple.Create("cpu", "CPU", Format.Percent(snap.Cpu) + (snap.CpuMhz > 0 ? "  " + Format.Ghz(snap.CpuMhz) : ""), snap.CpuHistory, 100f, ResColors.Cpu(th)));
            items.Add(Tuple.Create("mem", "Memoria", Format.Bytes(snap.MemUsed) + " / " + Format.Bytes(snap.MemTotal) + " (" + snap.MemPercent.ToString("0") + " %)", snap.MemHistory, 100f, ResColors.Memory(th)));
            if (snap.GpuAvailable)
                items.Add(Tuple.Create("gpu", "GPU", Format.Percent(snap.Gpu) + "  " + Short(snap.GpuName), snap.GpuHistory, 100f, ResColors.Gpu(th)));
            if (!double.IsNaN(snap.Temperature))
                items.Add(Tuple.Create("temp", "Temperatura", snap.Temperature.ToString("0") + " °C", snap.TempHistory, 110f, ResColors.Temp(th)));
            if (snap.HasBattery)
                items.Add(Tuple.Create("bat", "Batería", snap.BatteryPercent.ToString("0") + " %" + (snap.OnAc ? (snap.Charging ? "  cargando" : "  enchufada") : "  con batería"),
                    snap.BatteryHistory, 100f, ResColors.Battery(th)));
            foreach (var d in snap.Disks)
                items.Add(Tuple.Create("disk" + d.Number, d.Title, (string.IsNullOrEmpty(d.Kind) ? "" : d.Kind + "  ") + Format.Percent(d.Active), d.ActivityHistory, 100f, ResColors.Disk(th)));
            foreach (var n in snap.Nets)
            {
                var h = Sum(n.SendHistory, n.RecvHistory);
                items.Add(Tuple.Create("net" + n.Id, n.Kind, "E: " + Format.Bits(n.SendBps) + "  R: " + Format.Bits(n.RecvBps), h, Math.Max(1, h.Max()) * 1.2f, ResColors.Net(th)));
            }
            if (!items.Any(i => i.Item1 == resource)) resource = "cpu";

            int iy = top;
            foreach (var it in items)
            {
                var r = new Rectangle(c.X + S(12), iy, listW, S(60));
                bool sel = it.Item1 == resource;
                string key = "res-" + it.Item1;
                var hh = AddHit(key, r, () => { resource = it.Item1; Invalidate(); }, it.Item2 + ", " + it.Item3);
                hh.Role = AccessibleRole.ListItem; hh.Checked = sel;
                if (sel) Fill(g, r, th.Dark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0), S(4));
                HoverFill(g, key, r, S(4));
                if (sel) Fill(g, new Rectangle(r.X, r.Y + r.Height / 2 - S(8), S(3), S(16)), th.Accent, S(1.5f));
                var gr = new Rectangle(r.X + S(12), r.Y + S(10), S(64), S(40));
                FillRoundP(g, gr, 0, th.GraphBg);
                Graph(g, gr, it.Item4, it.Item5, it.Item6, false, true);
                DrawText(g, it.Item2, fonts.Body, new Rectangle(gr.Right + S(12), r.Y + S(11), r.Width - S(96), S(20)), th.Text);
                DrawText(g, it.Item3, fonts.Caption, new Rectangle(gr.Right + S(12), r.Y + S(31), r.Width - S(96), S(18)), th.TextSecondary);
                iy += S(62);
            }

            // Detalle
            var dr = new Rectangle(c.X + listW + S(36), top - S(4), c.Right - (c.X + listW + S(36)) - pad, c.Bottom - top - S(16));
            if (resource == "cpu") DetailCpu(g, dr, snap);
            else if (resource == "mem") DetailMem(g, dr, snap);
            else if (resource == "gpu") DetailGpu(g, dr, snap);
            else if (resource == "temp") DetailTemp(g, dr, snap);
            else if (resource == "bat") DetailBattery(g, dr, snap);
            else if (resource.StartsWith("disk")) DetailDisk(g, dr, snap.Disks.FirstOrDefault(d => "disk" + d.Number == resource));
            else DetailNet(g, dr, snap.Nets.FirstOrDefault(n => "net" + n.Id == resource));
        }

        static float[] Sum(float[] a, float[] b)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] + b[i];
            return r;
        }

        Rectangle DetailHeader(Graphics g, Rectangle r, string title, string right)
        {
            DrawText(g, title, fonts.Title, new Rectangle(r.X, r.Y, r.Width / 2, S(40)), th.Text);
            DrawText(g, right, fonts.Body, new Rectangle(r.X + r.Width / 3, r.Y + S(8), r.Width * 2 / 3, S(28)), th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return new Rectangle(r.X, r.Y + S(48), r.Width, r.Height - S(48));
        }

        int BigGraph(Graphics g, Rectangle r, int y, int height, string label, string max, float[] values, float vmax, Color c, float[] second)
        {
            DrawText(g, label, fonts.Caption, new Rectangle(r.X, y, r.Width / 2, S(18)), th.TextSecondary);
            DrawText(g, max, fonts.Caption, new Rectangle(r.X + r.Width / 2, y, r.Width / 2, S(18)), th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var gr = new Rectangle(r.X, y + S(20), r.Width, height);
            FillRoundP(g, gr, 0, th.GraphBg);
            Graph(g, gr, values, vmax, c, true, true);
            if (second != null)
            {
                // Segunda serie (enviar) como línea punteada sobre la primera.
                var pts = new PointF[second.Length];
                for (int i = 0; i < second.Length; i++)
                    pts[i] = new PointF(gr.X + 1 + (gr.Width - 2f) * i / (second.Length - 1), gr.Bottom - 1 - (gr.Height - 2f) * Math.Min(vmax, second[i]) / vmax);
                LinesP(g, pts, c, Math.Max(1.2f, 1.5f * s), 1);
            }
            DrawText(g, "60 segundos", fonts.Caption, new Rectangle(r.X, gr.Bottom + S(2), r.Width / 2, S(18)), th.TextSecondary);
            DrawText(g, "0", fonts.Caption, new Rectangle(r.X + r.Width / 2, gr.Bottom + S(2), r.Width / 2, S(18)), th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return gr.Bottom + S(28);
        }

        void Stats(Graphics g, Rectangle r, int y, IList<string[]> big, IList<string[]> small)
        {
            int colW = S(132);
            int x = r.X, row = y;
            foreach (var st in big)
            {
                if (x + colW > r.X + r.Width * 0.55 && x > r.X) { x = r.X; row += S(56); }
                DrawText(g, st[0], fonts.Caption, new Rectangle(x, row, colW - S(8), S(18)), th.TextSecondary);
                DrawText(g, st[1], fonts.Subtitle, new Rectangle(x, row + S(18), colW - S(8), S(30)), th.Text);
                x += colW;
            }
            int sx = r.X + (int)(r.Width * 0.58), sy = y;
            foreach (var st in small)
            {
                DrawText(g, st[0], fonts.Caption, new Rectangle(sx, sy, S(150), S(20)), th.TextSecondary);
                DrawText(g, st[1], fonts.Caption, new Rectangle(sx + S(150), sy, r.Right - sx - S(150), S(20)), th.Text);
                sy += S(22);
            }
        }

        void DetailCpu(Graphics g, Rectangle r, Snapshot snap)
        {
            var b = DetailHeader(g, r, "CPU", snap.CpuName);
            int gh = Math.Max(S(120), b.Height - S(250));
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
            int gh = Math.Max(S(120), b.Height - S(250));
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
            int gh = Math.Max(S(90), (b.Height - S(300)) * 2 / 3);
            int y = BigGraph(g, b, b.Y, gh, "Tiempo activo", "100 %", d.ActivityHistory, 100, ResColors.Disk(th), null);
            float tmax = Math.Max(1048576, d.TransferHistory.Max() * 1.2f);
            y = BigGraph(g, b, y, Math.Max(S(60), gh / 2), "Velocidad de transferencia del disco", Format.Rate(tmax), d.TransferHistory, tmax, ResColors.Disk(th), null);
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
            int gh = Math.Max(S(120), b.Height - S(250));
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
            Color fill = focused ? (th.Dark ? Theme.Hex(0x1F1F1F) : Theme.Hex(0xFFFFFF)) : (th.Dark ? Theme.Hex(0x2D2D2D) : Theme.Hex(0xFBFBFB));
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
            if (page == "perf") return;
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
            DrawText(g, "Procesos", fonts.Subtitle, new Rectangle(c.X + pad, c.Y + S(16), S(200), S(32)), th.Text);
            var sel = snap.Procs.FirstOrDefault(p => p.Pid == selectedPid);
            int bw = S(150);
            StandardButton(g, "end", new Rectangle(c.Right - pad - bw, c.Y + S(16), bw, S(32)), Glyphs.Cancel, "Finalizar tarea", false, sel != null,
                () => { if (sel != null) host.EndTask(sel, this); });
            SearchBox(g, new Rectangle(c.Right - pad - bw - S(8) - S(260), c.Y + S(16), S(260), S(32)), "Buscar un proceso");

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
            LineP(g, ControlStroke, 1, c.X + S(12), hy + S(48), c.Right - S(12), hy + S(48));

            listView = new Rectangle(c.X, hy + S(49), c.Width, c.Bottom - hy - S(49));
            string q = Query;
            IEnumerable<ProcMetric> all = snap.Procs.Where(p => p.Name != "Idle");
            if (q.Length > 0)
                all = all.Where(p => p.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Pid.ToString() == q);
            var apps = Sort(all.Where(p => p.IsApp)).ToList();
            var bg = Sort(all.Where(p => !p.IsApp)).ToList();

            int rowH = S(32);
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
                DrawText(g, total, fonts.BodyStrong, new Rectangle(inner.X, r.Y + S(6), inner.Width, S(20)), th.Text, flags);
                DrawText(g, label, fonts.Caption, new Rectangle(inner.X, r.Y + S(26), inner.Width, S(16)), th.TextSecondary, flags);
            }
            else DrawText(g, label, fonts.Caption, new Rectangle(inner.X, r.Y + S(26), inner.Width, S(16)), th.TextSecondary, flags);
            if (sortCol == key)
                Glyph(g, sortAsc ? Glyphs.ChevronUp : Glyphs.ChevronDown, fonts.Glyph12,
                    new Rectangle(r.X + r.Width / 2 - S(8), r.Y, S(16), S(10)), th.TextSecondary);
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
            DrawText(g, title + " (" + list.Count + ")", fonts.BodyStrong, new Rectangle(c.X + S(24), y + S(12), S(400), S(24)), th.Text);
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
            if (sel) Fill(g, r, th.Dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0), S(4));
            else HoverFill(g, key, r, S(4));
            if (sel) Fill(g, new Rectangle(r.X, r.Y + r.Height / 2 - S(8), S(3), S(16)), th.Accent, S(1.5f));

            // Celdas con color según la intensidad, como el Administrador de tareas.
            Heat(g, cols[1], r, p.Cpu / 100f);
            Heat(g, cols[2], r, snap.MemTotal > 0 ? (float)p.Memory / snap.MemTotal * 4 : 0);
            Heat(g, cols[3], r, (float)(p.DiskBps / (20.0 * 1048576)));
            Heat(g, cols[5], r, p.Gpu / 100f);

            var ir = new Rectangle(r.X + S(14), r.Y + (r.Height - S(16)) / 2, S(16), S(16));
            if (p.Icon != null) DrawIcon(g, p.Icon, ir);
            else Glyph(g, Glyphs.App, fonts.Glyph16, ir, th.TextSecondary);
            DrawText(g, p.Display, fonts.Body, new Rectangle(ir.Right + S(12), r.Y, cols[0].Right - ir.Right - S(16), r.Height), th.Text);
            var right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            DrawText(g, Format.Percent(p.Cpu), fonts.Body, Cell(cols[1], r), th.Text, right);
            DrawText(g, Format.Bytes(p.Memory), fonts.Body, Cell(cols[2], r), th.Text, right);
            DrawText(g, p.DiskBps >= 51200 ? Format.Rate(p.DiskBps) : "0 MB/s", fonts.Body, Cell(cols[3], r), th.Text, right);
            DrawText(g, p.Pid.ToString(), fonts.Body, Cell(cols[4], r), th.TextSecondary, right);
            DrawText(g, Format.Percent(p.Gpu), fonts.Body, Cell(cols[5], r), th.Text, right);
            if (cols[6].Width > 0)
            {
                double net = NetRate(p.Pid);
                Heat(g, cols[6], r, (float)(net / (10.0 * 1048576)));
                DrawText(g, net >= 1024 ? Format.Bits(net) : "0 Mbps", fonts.Body, Cell(cols[6], r), th.Text, right);
            }
        }

        Rectangle Cell(Rectangle col, Rectangle row) { return new Rectangle(col.X + S(10), row.Y, col.Width - S(20), row.Height); }

        void Heat(Graphics g, Rectangle col, Rectangle row, float intensity)
        {
            intensity = Math.Max(0, Math.Min(1, intensity));
            if (intensity < 0.02f) return;
            var cell = new Rectangle(col.X + S(2), row.Y + S(2), col.Width - S(4), row.Height - S(4));
            Color baseC = th.Dark ? Theme.Hex(0xE8B730) : Theme.Hex(0xF2C14E);
            Fill(g, cell, Color.FromArgb((int)(18 + 110 * intensity), baseC), S(3));
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
            DrawText(g, "Registro de actividad", fonts.Subtitle, new Rectangle(c.X + pad, c.Y + S(16), S(320), S(32)), th.Text);
            int bw = S(124);
            StandardButton(g, "logfile", new Rectangle(c.Right - pad - bw, c.Y + S(16), bw, S(32)), Glyphs.OpenExternal, "Archivo", false, true, host.OpenLogFile);
            StandardButton(g, "logexport", new Rectangle(c.Right - pad - 2 * bw - S(8), c.Y + S(16), bw, S(32)), Glyphs.Export, "Exportar", true, true,
                () => ShowExportMenu(new Point(c.Right - pad - 2 * bw - S(8), c.Y + S(50))));
            SearchBox(g, new Rectangle(c.Right - pad - 2 * bw - S(16) - S(240), c.Y + S(16), S(240), S(32)), "Buscar en el registro");

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
                        DrawText(g, DayLabel(e.Time.Date), fonts.BodyStrong, new Rectangle(c.X + pad, y + S(12), S(400), S(24)), th.Text);
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
            DrawText(g, e.Time.ToString("HH:mm:ss"), fonts.Body, new Rectangle(r.X + S(12), r.Y, S(72), r.Height), th.TextSecondary);
            Color gc = e.Severity == Severity.Error ? th.Critical : e.Severity == Severity.Advertencia ? th.Caution : th.TextSecondary;
            Glyph(g, ActivityEntry.CategoryGlyph(e.Category), fonts.Glyph16, new Rectangle(r.X + S(92), r.Y, S(20), r.Height), gc);
            DrawText(g, ActivityEntry.CategoryName(e.Category), fonts.Caption, new Rectangle(r.X + S(124), r.Y, S(96), r.Height), th.TextSecondary);
            DrawText(g, e.Message, fonts.Body, new Rectangle(r.X + S(224), r.Y, r.Width - S(236), r.Height), th.Text);
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
