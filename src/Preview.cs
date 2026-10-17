using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace SysMonitor
{
    /// <summary>
    /// Exporta capturas con datos de ejemplo, sin leer el sistema:
    /// Monitor.exe --render carpeta [dark|light]
    /// </summary>
    internal static class Preview
    {
        sealed class DemoHost : IHost
        {
            public Snapshot Snap;
            public Snapshot Latest { get { return Snap; } }
            public bool WidgetVisible { get { return true; } }
            public bool Autostart { get { return true; } }
            public void ToggleWidget() { } public void ToggleAutostart() { } public void OpenMain(string page) { }
            public void EndTask(ProcMetric p, IWin32Window owner) { } public void OpenFileLocation(ProcMetric p) { }
            public void OpenTaskManager() { } public void OpenLogFile() { } public void Exit() { }
            public HistoryStore Store;
            public HistoryStore History { get { return Store; } }
            public void ApplyHotkey() { } public void WidgetChanged() { } public void OpenProcess(int pid) { }
            public HelperClient Helper { get { return null; } }
        }

        static float[] Wave(float baseV, float amp, float freq, float noise, int seed)
        {
            var r = new Random(seed);
            var v = new float[History.Size];
            for (int i = 0; i < v.Length; i++)
                v[i] = Math.Max(0, Math.Min(100, baseV + amp * (float)Math.Sin(i * freq) + (float)(r.NextDouble() - 0.5) * noise));
            return v;
        }

        static Snapshot Demo()
        {
            var s = new Snapshot
            {
                Cpu = 23, CpuMhz = 3420, CpuBaseMhz = 2100, CpuName = "Intel(R) Core(TM) i7-1260P", Logical = 16, Cores = 12,
                CpuHistory = Wave(20, 10, 0.25f, 14, 1),
                MemTotal = 16UL << 30, MemUsed = (ulong)(9.8 * (1UL << 30)), MemPercent = 61, MemCached = 4UL << 30,
                CommitUsed = 14UL << 30, CommitLimit = 22UL << 30, MemHistory = Wave(60, 2, 0.1f, 2, 2),
                ProcessCount = 287, ThreadCount = 3912, HandleCount = 128344, Uptime = new TimeSpan(0, 3, 12, 41), Time = DateTime.Now,
                GpuAvailable = true, Gpu = 18, GpuName = "Intel(R) Iris(R) Xe Graphics", GpuEngine = "3D", GpuHistory = Wave(15, 10, 0.35f, 10, 7),
                GpuDedicated = 128UL << 20, GpuShared = 1200UL << 20, Temperature = 54, TempHistory = Wave(53, 2, 0.1f, 2, 8),
                HasBattery = true, BatteryPercent = 87, OnAc = true, Charging = true, BatteryMinutes = -1, BatteryHealth = 91, BatteryRateMw = 24500,
                BatteryHistory = Wave(86, 1, 0.05f, 0.5f, 9)
            };
            s.Disks.Add(new DiskMetric { Number = 0, Name = "Micron MTFDKBK512QFM", Letters = "C:", Kind = "SSD (NVMe)", Active = 4, ReadBps = 1.2e6, WriteBps = 3.1e5, ResponseMs = 0.4, Capacity = 512UL * 1000000000, VolumeTotal = 476UL << 30, VolumeFree = 212UL << 30, ActivityHistory = Wave(5, 4, 0.5f, 8, 3), TransferHistory = Wave(30, 20, 0.4f, 30, 4) });
            s.Disks.Add(new DiskMetric { Number = 1, Name = "Kingston DataTraveler 3.0", Letters = "D:", Kind = "USB", Active = 0, Capacity = 123UL * 1000000000, VolumeTotal = 115UL << 30, VolumeFree = 98UL << 30, ActivityHistory = new float[History.Size], TransferHistory = new float[History.Size] });
            var recv = Wave(40, 30, 0.3f, 40, 5); var send = Wave(8, 5, 0.3f, 8, 6);
            for (int i = 0; i < recv.Length; i++) { recv[i] *= 30000; send[i] *= 30000; }
            s.Nets.Add(new NetMetric { Id = "w", Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX211 160MHz", Kind = "Wi-Fi", Ssid = "Casa", Ipv4 = "192.168.0.24", SendBps = 2.1e5, RecvBps = 1.4e6, LinkSpeed = 866700000, SendHistory = send, RecvHistory = recv });
            Func<string, string, float, double, bool, int, ProcMetric> P = (n, d, c, mb, app, pid) =>
                new ProcMetric { Name = n, Description = d, Cpu = c, Memory = (ulong)(mb * 1048576), IsApp = app, Pid = pid, DiskBps = c > 3 ? 2.4e5 : 0 };
            s.Procs.AddRange(new[]
            {
                P("chrome", "Google Chrome", 8.4f, 1240, true, 10420), P("Code", "Visual Studio Code", 3.1f, 680, true, 8812),
                P("explorer", "Explorador de Windows", 0.6f, 142, true, 5512), P("Spotify", "Spotify", 1.2f, 310, true, 14220),
                P("WINWORD", "Microsoft Word", 0.2f, 205, true, 7716), P("MsMpEng", "Antimalware Service Executable", 2.6f, 260, false, 4120),
                P("dwm", "Administrador de ventanas del escritorio", 1.8f, 98, false, 1312), P("svchost", "Host de servicio: Servicio de directivas de diagnóstico", 0.3f, 24, false, 2244),
                P("SearchIndexer", "Microsoft Windows Search Indexer", 0.1f, 48, false, 6630), P("audiodg", "Aislamiento de gráficos de dispositivos de audio de Windows", 0.4f, 12, false, 9932),
                P("System", null, 0.9f, 1, false, 4), P("SysMonitor", "Monitor del sistema", 0.3f, 38, false, 12008),
            });
            return s;
        }

        static void Seed()
        {
            Activity.MemoryOnly = true;
            Activity.Info(Category.Monitor, "Monitor del sistema " + AppInfo.Version + " iniciado. Windows está encendido desde hace 3 h 12 min.");
            Activity.Info(Category.Red, "Conectado a Wi-Fi \"Casa\", IP 192.168.0.24");
            Activity.Info(Category.Programa, "Se abrió Google Chrome (chrome.exe, PID 10420)");
            Activity.Info(Category.Dispositivo, "Se conectó Kingston DataTraveler 3.0");
            Activity.Info(Category.Dispositivo, "Unidad disponible: D: (PENDRIVE, exFAT, 115 GB)");
            Activity.Add(Category.Alerta, Severity.Advertencia, "CPU por encima del 90 % durante 15 segundos (más uso: Google Chrome, 72 %)");
            Activity.Add(Category.Sistema, Severity.Error, "Un programa se cerró inesperadamente: Nombre de la aplicación con errores: Spotify.exe (evento 1000)");
            Activity.Info(Category.Sesion, "Sesión bloqueada");
            Activity.Info(Category.Sesion, "Sesión desbloqueada");
            Activity.Info(Category.Programa, "Se cerró Microsoft Word (PID 7716, estuvo abierto 42 min)");
        }

        static void Save(Control c, string path)
        {
            var th = Theme.Load();
            var surface = c as Surface;
            if (th.Glass && surface != null)
            {
                // Simula el fondo del sistema: un fondo de colores desenfocado con el tinte de Mica/Acrílico.
                using (var layer = surface.RenderToBitmap())
                using (var bmp = new Bitmap(layer.Width, layer.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height),
                        Color.FromArgb(0, 120, 212), Color.FromArgb(180, 60, 140), 35f))
                        g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);
                    using (var spot = new SolidBrush(Color.FromArgb(160, 255, 170, 60)))
                        g.FillEllipse(spot, bmp.Width / 3, bmp.Height / 4, bmp.Width / 2, bmp.Height / 2);
                    int tint = c is MainWindow ? 225 : 205;
                    using (var t = new SolidBrush(th.Dark ? Color.FromArgb(tint, 32, 32, 32) : Color.FromArgb(tint, 243, 243, 243)))
                        g.FillRectangle(t, 0, 0, bmp.Width, bmp.Height);
                    g.DrawImageUnscaled(layer, 0, 0);
                    bmp.Save(path, ImageFormat.Png);
                }
                return;
            }
            if (c is MainWindow)
            {
                // Sin el marco clásico que dibuja DrawToBitmap: barra de título de Windows 11 dibujada aparte.
                using (var client = ((Surface)c).RenderToBitmap())
                using (var bmp = Windows11Frame(client, th)) bmp.Save(path, ImageFormat.Png);
                return;
            }
            using (var bmp = new Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>Ventana de Windows 11 alrededor de una captura: barra de título, esquinas redondeadas y borde.</summary>
        static Bitmap Windows11Frame(Bitmap client, Theme th)
        {
            const int bar = 32, radius = 8;
            var bmp = new Bitmap(client.Width, client.Height + bar, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var all = new RectangleF(0, 0, bmp.Width - 1, bmp.Height - 1);
                using (var shape = Theme.Round(all, radius))
                {
                    g.SetClip(shape);
                    using (var b = new SolidBrush(th.Clear)) g.FillRectangle(b, 0, 0, bmp.Width, bar);
                    g.DrawImageUnscaled(client, 0, bar);
                    using (var icon = TrayIcon.DrawApp(16)) g.DrawImageUnscaled(icon, 14, (bar - 16) / 2);
                    using (var f = new Font(Fonts.TextFamily, 12f, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(th.Text))
                    using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                        g.DrawString(AppInfo.Name, f, b, new RectangleF(42, 0, 300, bar), sf);
                    string[] caption = { "", "", "" };
                    using (var f = new Font(Fonts.IconFamily, 10f, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(th.Text))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        for (int i = 0; i < 3; i++)
                            g.DrawString(caption[i], f, b, new RectangleF(bmp.Width - (3 - i) * 46, 0, 46, bar), sf);
                    g.ResetClip();
                    using (var pen = new Pen(th.Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0))) g.DrawPath(pen, shape);
                }
            }
            return bmp;
        }

        public static int Render(string[] args)
        {
            string dir = args[1];
            if (args.Length > 2) Theme.ForceDark = args[2] != "light";
            Theme.ForceGlass = args.Length > 3 && args[3] == "glass";
            Seed();
            var host = new DemoHost { Snap = Demo(), Store = new HistoryStore() };
            var rnd = new Random(3);
            var pts = new List<HistoryPoint>();
            for (int i = 0; i < 1440; i++)
            {
                if (i > 300 && i < 420) continue; // hueco: la PC estuvo apagada
                float day = (float)Math.Sin(i / 1440.0 * Math.PI * 2);
                pts.Add(new HistoryPoint { Time = DateTime.Now.AddMinutes(-1440 + i), Cpu = Math.Max(2, 22 + 14 * day + (float)rnd.NextDouble() * 18),
                    Mem = 58 + 8 * day + (float)rnd.NextDouble() * 4, Gpu = Math.Max(0, 10 + 8 * day + (float)rnd.NextDouble() * 12),
                    Disk = (float)rnd.NextDouble() * (rnd.Next(30) == 0 ? 90 : 12), NetBps = 2e5 + rnd.NextDouble() * 1.5e6,
                    Temp = 50 + 6 * day + (float)rnd.NextDouble() * 3, Battery = 100 - (i % 400) / 6f });
            }
            host.Store.ReplaceForPreview(pts);

            using (var f = new TrayFlyout(host))
            {
                f.CreateControl();
                f.PrepareRender();
                Save(f, System.IO.Path.Combine(dir, "flyout.png"));
            }
            // Isla dinámica: estados principales y contextuales (se restaura la posición elegida por el usuario).
            IslandForm.DisableLayered = true; // las capturas se toman con la ventana normal
            string savedPosition = Settings.IslandPosition;
            var islandShots = new[]
            {
                Tuple.Create("isla-compacta", false, "Top", (string)null),
                Tuple.Create("isla-expandida", true, "Top", (string)null),
                Tuple.Create("isla-barra", false, "Taskbar", (string)null),
                Tuple.Create("isla-barra-expandida", true, "Taskbar", (string)null),
                Tuple.Create("isla-descarga", false, "Top", "download"),
                Tuple.Create("isla-temperatura", false, "Top", "temp"),
                Tuple.Create("isla-proceso", false, "Top", "hog"),
                Tuple.Create("isla-cargador", false, "Top", "charger"),
                Tuple.Create("isla-usb", false, "Top", "usb"),
            };
            try
            {
                foreach (var shot in islandShots)
                    using (var isl = new IslandForm(host))
                    {
                        isl.CreateControl();
                        var hh = isl.Handle;
                        isl.PreviewState(shot.Item2, shot.Item3, shot.Item4);
                        Save(isl, System.IO.Path.Combine(dir, shot.Item1 + ".png"));
                    }
            }
            finally { Settings.IslandPosition = savedPosition; }

            using (var w = new WidgetForm(host))
            {
                w.CreateControl();
                var h = w.Handle;
                w.Relayout();
                Save(w, System.IO.Path.Combine(dir, "widget.png"));
            }
            foreach (var page in new[] { "perf", "procs", "log", "hist", "net", "net-scroll", "files", "startup", "services", "settings" })
            {
                using (var m = new MainWindow(host))
                {
                    m.CreateControl();
                    var h = m.Handle;
                    m.WindowState = FormWindowState.Normal;
                    m.Size = new Size(page.EndsWith("-scroll") ? 820 : 1040, 700);
                    m.PreviewPage(page.Replace("-scroll", ""), "cpu", 10420, page.EndsWith("-scroll") ? 50 : 0);
                    Save(m, System.IO.Path.Combine(dir, "main-" + page + ".png"));
                }
            }
            var th = Theme.Load();
            using (var icon = TrayIcon.Draw(23, host.Snap.CpuHistory, th, 32)) icon.Save(System.IO.Path.Combine(dir, "tray.png"), ImageFormat.Png);
            foreach (int sz in new[] { 16, 20, 24, 32 })
                using (var icon = TrayIcon.DrawApp(sz)) icon.Save(System.IO.Path.Combine(dir, "tray-app-" + sz + ".png"), ImageFormat.Png);
            return 0;
        }
    }
}
