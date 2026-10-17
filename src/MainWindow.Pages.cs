using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Panoptic
{
    internal sealed partial class MainWindow
    {
        // ================================================================== Rendimiento: GPU, temperatura, batería

        void DetailGpu(Graphics g, Rectangle r, Snapshot snap)
        {
            var b = DetailHeader(g, r, "GPU", snap.GpuName);
            int gh = Math.Max(S(120), b.Height - S(250));
            int y = BigGraph(g, b, b.Y, gh, "% de uso" + (string.IsNullOrEmpty(snap.GpuEngine) ? "" : " (motor más usado: " + snap.GpuEngine + ")"), "100 %",
                snap.GpuHistory, 100, ResColors.Gpu(th), null);
            var top = snap.Procs.Where(p => p.Gpu > 0.5f).OrderByDescending(p => p.Gpu).FirstOrDefault();
            Stats(g, b, y, new[]
            {
                new[] { "Uso", Format.Percent(snap.Gpu) },
                new[] { "Memoria dedicada", Format.Bytes(snap.GpuDedicated) },
                new[] { "Memoria compartida", Format.Bytes(snap.GpuShared) },
                new[] { "Más uso", top != null ? top.Display : "—" },
            }, new[]
            {
                new[] { "Adaptador:", snap.GpuName ?? "—" },
            });
        }

        void DetailTemp(Graphics g, Rectangle r, Snapshot snap)
        {
            var b = DetailHeader(g, r, "Temperatura", "Zona térmica ACPI");
            int gh = Math.Max(S(120), b.Height - S(250));
            float[] hist = snap.TempHistory;
            int y = BigGraph(g, b, b.Y, gh, "Temperatura (°C)", "110 °C", hist, 110, ResColors.Temp(th), null);
            float max = hist.Where(v => v > 0).DefaultIfEmpty(0).Max(), min = hist.Where(v => v > 0).DefaultIfEmpty(0).Min();
            Stats(g, b, y, new[]
            {
                new[] { "Actual", snap.Temperature.ToString("0") + " °C" },
                new[] { "Máxima (1 min)", max.ToString("0") + " °C" },
                new[] { "Mínima (1 min)", min.ToString("0") + " °C" },
            }, new[]
            {
                new[] { "Alerta a partir de:", Settings.TempThreshold + " °C" },
                new[] { "Fuente:", "Sensor del equipo (ACPI)" },
            });
            DrawText(g, "Es la temperatura que informa el equipo; puede diferir de la de cada núcleo del procesador.", fonts.Caption,
                new Rectangle(b.X, b.Bottom - S(20), b.Width, S(18)), th.TextTertiary);
        }

        void DetailBattery(Graphics g, Rectangle r, Snapshot snap)
        {
            string state = snap.OnAc ? (snap.Charging ? "Cargando" : "Enchufada") : "Con batería";
            var b = DetailHeader(g, r, "Batería", state);
            int gh = Math.Max(S(120), b.Height - S(250));
            int y = BigGraph(g, b, b.Y, gh, "Carga en 60 segundos", "100 %", snap.BatteryHistory, 100, ResColors.Battery(th), null);
            string remaining = snap.BatteryMinutes > 0 ? (snap.BatteryMinutes / 60) + " h " + (snap.BatteryMinutes % 60) + " min" : (snap.OnAc ? "—" : "Calculando…");
            string rate = snap.BatteryRateMw != 0 ? (Math.Abs(snap.BatteryRateMw) / 1000.0).ToString("0.0", Format.Es) + " W" + (snap.BatteryRateMw > 0 ? " (cargando)" : " (consumo)") : "—";
            Stats(g, b, y, new[]
            {
                new[] { "Carga", snap.BatteryPercent.ToString("0") + " %" },
                new[] { "Tiempo restante", remaining },
                new[] { "Potencia", rate },
                new[] { "Salud", snap.BatteryHealth > 0 ? snap.BatteryHealth.ToString("0") + " %" : "—" },
            }, new[]
            {
                new[] { "Estado:", state },
                new[] { "Alerta de batería baja:", Settings.BatteryLow + " %" },
            });
            if (snap.BatteryHealth <= 0)
                DrawText(g, "La salud de la batería se calcula con la capacidad de diseño; este equipo no la informa sin permisos de administrador.",
                    fonts.Caption, new Rectangle(b.X, b.Bottom - S(20), b.Width, S(18)), th.TextTertiary);
        }

        // ================================================================== Historial

        int historyRange = 1; // 0 = 1 hora, 1 = 24 horas, 2 = 7 días
        string histStamp;
        List<HistoryPoint> histPoints = new List<HistoryPoint>();
        readonly Dictionary<string, Tuple<float[], float, float, int>> histCache = new Dictionary<string, Tuple<float[], float, float, int>>();

        void PaintHistory(Graphics g, Rectangle c)
        {
            PageTitle(g, c, "Historial", "Promedio de cada minuto · se conservan " + Settings.HistoryDays + " días · " + ShownPath(HistoryStore.FilePath));
            int used = Chips(g, c.X + S(24), c.Y + S(72), c.Right - S(24), "hist", new[] { "Última hora", "Últimas 24 horas", "Últimos 7 días" }, historyRange, i => historyRange = i);
            TimeSpan span = historyRange == 0 ? TimeSpan.FromHours(1) : historyRange == 1 ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7);
            var to = DateTime.Now;
            var from = to - span;
            // Los puntos se leen una vez por minuto (o al cambiar el rango): el historial solo cambia cada minuto.
            string stamp = host.History.Version + "|" + historyRange + "|" + to.ToString("yyyyMMddHHmm");
            if (stamp != histStamp) { histStamp = stamp; histPoints = host.History.Range(from); histCache.Clear(); }
            var pts = histPoints;

            listView = new Rectangle(c.X, c.Y + S(72) + used, c.Width, c.Bottom - (c.Y + S(72) + used));
            var charts = new List<Tuple<string, Func<HistoryPoint, float>, float, Color, Func<float, string>>>();
            charts.Add(Tuple.Create("CPU", (Func<HistoryPoint, float>)(p => p.Cpu), 100f, ResColors.Cpu(th), (Func<float, string>)(v => v.ToString("0") + " %")));
            charts.Add(Tuple.Create("Memoria", (Func<HistoryPoint, float>)(p => p.Mem), 100f, ResColors.Memory(th), (Func<float, string>)(v => v.ToString("0") + " %")));
            charts.Add(Tuple.Create("GPU", (Func<HistoryPoint, float>)(p => p.Gpu), 100f, ResColors.Gpu(th), (Func<float, string>)(v => v.ToString("0") + " %")));
            charts.Add(Tuple.Create("Disco (el más activo)", (Func<HistoryPoint, float>)(p => p.Disk), 100f, ResColors.Disk(th), (Func<float, string>)(v => v.ToString("0") + " %")));
            float netMax = Math.Max(125000f, pts.Count > 0 ? (float)pts.Max(p => p.NetBps) * 1.15f : 125000f);
            charts.Add(Tuple.Create("Red", (Func<HistoryPoint, float>)(p => (float)p.NetBps), netMax, ResColors.Net(th), (Func<float, string>)(v => Format.Bits(v))));
            if (pts.Any(p => !float.IsNaN(p.Temp)))
                charts.Add(Tuple.Create("Temperatura", (Func<HistoryPoint, float>)(p => p.Temp), 110f, ResColors.Temp(th), (Func<float, string>)(v => v.ToString("0") + " °C")));
            if (pts.Any(p => !float.IsNaN(p.Battery)))
                charts.Add(Tuple.Create("Batería", (Func<HistoryPoint, float>)(p => p.Battery), 100f, ResColors.Battery(th), (Func<float, string>)(v => v.ToString("0") + " %")));

            int chartH = S(176);
            listContent = charts.Count * (chartH + S(12)) + S(8);
            ClampScroll();
            var state = g.Save();
            g.SetClip(listView);
            int y = listView.Y - ScrollPos;
            foreach (var ch in charts)
            {
                if (y + chartH >= listView.Y && y <= listView.Bottom)
                    HistoryChart(g, new Rectangle(c.X + S(24), y, c.Width - S(48), chartH), ch.Item1, pts, ch.Item2, ch.Item3, ch.Item4, ch.Item5, from, to);
                y += chartH + S(12);
            }
            g.Restore(state);
            if (pts.Count == 0)
                DrawText(g, "Todavía no hay datos guardados para este período. El historial se completa a medida que el monitor está abierto.",
                    fonts.Caption, new Rectangle(c.X + S(24), listView.Y - S(22), c.Width - S(48), S(18)), th.TextSecondary);
            DrawScrollbar(g);
        }

        void HistoryChart(Graphics g, Rectangle r, string title, List<HistoryPoint> pts, Func<HistoryPoint, float> sel, float max, Color color,
            Func<float, string> fmt, DateTime from, DateTime to)
        {
            Card(g, r);
            var gr = new Rectangle(r.X + S(16), r.Y + S(40), r.Width - S(32), r.Height - S(70));
            int cols = Math.Max(2, gr.Width);
            double total = (to - from).TotalSeconds;

            // La serie se calcula una vez por minuto (o al cambiar el rango o el tamaño), no en cada cuadro.
            string ck = title + "|" + cols + "|" + histStamp;
            Tuple<float[], float, float, int> cached;
            if (!histCache.TryGetValue(ck, out cached))
            {
                var vals = pts.Select(sel).Where(v => !float.IsNaN(v)).ToList();
                // Un valor por columna de píxeles (promedio); sin datos = hueco (la PC estaba apagada o el monitor cerrado).
                var buckets = new float[cols];
                var counts = new int[cols];
                foreach (var p in pts)
                {
                    float v = sel(p);
                    if (float.IsNaN(v)) continue;
                    int i = (int)((p.Time - from).TotalSeconds / total * (cols - 1));
                    if (i < 0 || i >= cols) continue;
                    buckets[i] += v; counts[i]++;
                }
                // Rellenar huecos de menos de 2 minutos para que la línea sea continua.
                double secPerCol = total / cols;
                int bridge = Math.Max(1, (int)Math.Ceiling(120 / secPerCol));
                var ser = new float[cols];
                for (int i = 0; i < cols; i++) ser[i] = counts[i] > 0 ? buckets[i] / counts[i] : float.NaN;
                int lastIdx = -1;
                for (int i = 0; i < cols; i++)
                {
                    if (float.IsNaN(ser[i])) continue;
                    if (lastIdx >= 0 && i - lastIdx > 1 && i - lastIdx <= bridge)
                        for (int k = lastIdx + 1; k < i; k++) ser[k] = ser[lastIdx] + (ser[i] - ser[lastIdx]) * (k - lastIdx) / (float)(i - lastIdx);
                    lastIdx = i;
                }
                cached = Tuple.Create(ser, vals.Count > 0 ? vals.Average() : 0f, vals.Count > 0 ? vals.Max() : 0f, vals.Count);
                histCache[ck] = cached;
            }
            var series = cached.Item1;

            DrawText(g, title, fonts.BodyStrong, new Rectangle(r.X + S(16), r.Y + S(10), S(260), S(22)), th.Text);
            if (cached.Item4 > 0)
                DrawText(g, "Promedio " + fmt(cached.Item2) + "   ·   Máximo " + fmt(cached.Item3), fonts.Caption,
                    new Rectangle(r.X + S(280), r.Y + S(10), r.Width - S(296), S(22)), th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var b = new SolidBrush(th.GraphBg)) g.FillRectangle(b, gr);
            using (var pen = new Pen(Color.FromArgb(th.Dark ? 28 : 36, color)))
                for (int i = 1; i < 4; i++) { int yy = gr.Y + gr.Height * i / 4; g.DrawLine(pen, gr.X, yy, gr.Right, yy); }

            var st = g.Save();
            g.SetClip(Rectangle.Intersect(gr, listView));
            var seg = new List<PointF>();
            Action flush = () =>
            {
                if (seg.Count >= 2)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(seg[0].X, gr.Bottom, seg[0].X, seg[0].Y);
                        path.AddLines(seg.ToArray());
                        path.AddLine(seg[seg.Count - 1].X, seg[seg.Count - 1].Y, seg[seg.Count - 1].X, gr.Bottom);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Color.FromArgb(th.Dark ? 60 : 45, color))) g.FillPath(b, path);
                    }
                    using (var pen = new Pen(color, Math.Max(1.2f, 1.5f * s))) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, seg.ToArray()); }
                }
                else if (seg.Count == 1)
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, seg[0].X - 2 * s, seg[0].Y - 2 * s, 4 * s, 4 * s);
                seg.Clear();
            };
            for (int i = 0; i < cols; i++)
            {
                if (float.IsNaN(series[i])) { flush(); continue; }
                float v = Math.Max(0, Math.Min(max, series[i]));
                seg.Add(new PointF(gr.X + i * (gr.Width - 1f) / (cols - 1), gr.Bottom - 1 - (gr.Height - 2) * v / max));
            }
            flush();

            // Cursor: muestra fecha y valor bajo el mouse.
            var mouse = PointToClient(Cursor.Position);
            if (gr.Contains(mouse) && listView.Contains(mouse))
            {
                int i = Math.Max(0, Math.Min(cols - 1, mouse.X - gr.X));
                using (var pen = new Pen(th.TextSecondary, 1)) { pen.DashStyle = DashStyle.Dot; g.DrawLine(pen, mouse.X, gr.Y, mouse.X, gr.Bottom); }
                var t = from.AddSeconds(total * i / (cols - 1));
                string label = t.ToString(historyRange == 2 ? "ddd d HH:mm" : "HH:mm", Format.Es) + "   " + (float.IsNaN(series[i]) ? "sin datos" : fmt(series[i]));
                int lw = MeasureText(label, fonts.Caption) + S(16);
                var lr = new Rectangle(Math.Min(gr.Right - lw, mouse.X + S(8)), gr.Y + S(6), lw, S(24));
                Fill(g, lr, th.Dark ? Color.FromArgb(235, 44, 44, 44) : Color.FromArgb(240, 252, 252, 252), S(4));
                DrawText(g, label, fonts.Caption, lr, th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.Restore(st);

            // Eje de tiempo
            string f = historyRange == 2 ? "ddd d" : "HH:mm";
            DrawText(g, from.ToString(f, Format.Es), fonts.Caption, new Rectangle(gr.X, gr.Bottom + S(4), S(120), S(18)), th.TextSecondary);
            DrawText(g, from.AddSeconds(total / 2).ToString(f, Format.Es), fonts.Caption, new Rectangle(gr.X + gr.Width / 2 - S(60), gr.Bottom + S(4), S(120), S(18)), th.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, "ahora", fonts.Caption, new Rectangle(gr.Right - S(120), gr.Bottom + S(4), S(120), S(18)), th.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // ================================================================== Conexiones

        int connFilter = 0; // 0 todas, 1 establecidas, 2 en escucha, 3 UDP
        List<Connection> connections = new List<Connection>();
        DateTime connAt = DateTime.MinValue;

        void PaintConnections(Graphics g, Rectangle c, Snapshot snap)
        {
            if ((DateTime.Now - connAt).TotalSeconds >= 2)
            {
                connAt = DateTime.Now;
                try { connections = NetConnections.Snapshot(true); } catch { }
            }
            var byPid = snap.Procs.GroupBy(p => p.Pid).ToDictionary(x => x.Key, x => x.First());
            foreach (var cn in connections)
            {
                ProcMetric pm;
                cn.Process = byPid.TryGetValue(cn.Pid, out pm) ? pm.Display : SecurityWatch.ProcessName(cn.Pid);
                if (cn.RemoteIp != null && cn.Remote.Length > 0) cn.Host = NetConnections.HostName(cn.RemoteIp);
            }
            int est = connections.Count(x => x.State == "Establecida");
            int programs = connections.Where(x => x.State == "Establecida" && !IsLocal(x.RemoteIp)).Select(x => x.Pid).Distinct().Count();
            var helper = host.Helper;
            bool traffic = helper != null && helper.Connected;
            var byConn = traffic ? helper.NetByConn : new Dictionary<string, NetTraffic>();
            string top = "";
            if (traffic)
            {
                var busiest = helper.NetByPid.OrderByDescending(kv => kv.Value.SentBps + kv.Value.RecvBps).FirstOrDefault();
                if (busiest.Value != null && busiest.Value.SentBps + busiest.Value.RecvBps >= 1024)
                {
                    ProcMetric bp;
                    top = " · más tráfico: " + (byPid.TryGetValue(busiest.Key, out bp) ? bp.Display : SecurityWatch.ProcessName(busiest.Key)) +
                        " (" + Format.Bits(busiest.Value.SentBps + busiest.Value.RecvBps) + ")";
                }
            }
            PageTitle(g, c, "Conexiones", est + " conexiones establecidas · " + programs + " programas conectados a internet" +
                (traffic ? top : " · el tráfico por programa requiere las funciones de administrador"));
            Func<object, NetTraffic> tr = o =>
            {
                var x = (Connection)o;
                NetTraffic t;
                return byConn.TryGetValue(HelperClient.ConnKey(x.Pid, x.Proto, x.Remote, x.RemotePort), out t) ? t : null;
            };
            SearchBox(g, new Rectangle(c.Right - S(24) - S(260), c.Y + S(16), S(260), S(32)), "Buscar programa, IP o puerto");
            int used = Chips(g, c.X + S(24), c.Y + S(72), c.Right - S(24), "conn", new[] { "Todas TCP", "Establecidas", "En escucha", "UDP" }, connFilter, i => connFilter = i);

            string q = Query;
            IEnumerable<Connection> rows = connections;
            if (connFilter == 0) rows = rows.Where(x => x.Proto.StartsWith("TCP"));
            if (connFilter == 1) rows = rows.Where(x => x.State == "Establecida");
            if (connFilter == 2) rows = rows.Where(x => x.Listening);
            if (connFilter == 3) rows = rows.Where(x => x.Proto.StartsWith("UDP"));
            if (q.Length > 0)
                rows = rows.Where(x => (x.Process ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || x.Local.Contains(q) || x.Remote.Contains(q) ||
                    x.LocalPort.ToString() == q || x.RemotePort.ToString() == q || (x.Host ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            var cols = new List<Column>
            {
                Col("Programa", 0, false, o => ((Connection)o).Process),
                new Column { Title = "PID", Width = 70, Right = true, Text = o => ((Connection)o).Pid.ToString(), Sort = o => ((Connection)o).Pid },
                Col("Protocolo", 80, false, o => ((Connection)o).Proto),
                new Column { Title = "Dirección local", Width = 190, Text = o => Endpoint(((Connection)o).Local, ((Connection)o).LocalPort), Sort = o => ((Connection)o).LocalPort },
                Col("Dirección remota", 260, false, o =>
                {
                    var x = (Connection)o;
                    if (x.Remote.Length == 0) return "";
                    return string.IsNullOrEmpty(x.Host) ? Endpoint(x.Remote, x.RemotePort) : x.Host + ":" + x.RemotePort;
                }),
                new Column { Title = "Estado", Width = 120, Text = o => ((Connection)o).State,
                    Color = o => ((Connection)o).Listening && (((Connection)o).Local == "0.0.0.0" || ((Connection)o).Local == "::") ? (Color?)th.Caution : null },
            };
            if (traffic)
            {
                cols.Add(new Column { Title = "Envío", Width = 96, Right = true,
                    Text = o => { var t = tr(o); return t != null && t.SentBps >= 1 ? Format.Bits(t.SentBps) : ""; },
                    Sort = o => { var t = tr(o); return t != null ? t.SentBps : 0; } });
                cols.Add(new Column { Title = "Recepción", Width = 96, Right = true,
                    Text = o => { var t = tr(o); return t != null && t.RecvBps >= 1 ? Format.Bits(t.RecvBps) : ""; },
                    Sort = o => { var t = tr(o); return t != null ? t.RecvBps : 0; } });
                cols.Add(new Column { Title = "Total", Width = 90, Right = true,
                    Text = o => { var t = tr(o); return t != null ? Format.Bytes((ulong)(t.SentTotal + t.RecvTotal)) : ""; },
                    Sort = o => { var t = tr(o); return t != null ? t.SentTotal + t.RecvTotal : 0; } });
                cols[4].Width = 220;
            }
            Table(g, new Rectangle(c.X, c.Y + S(72) + used, c.Width, c.Bottom - (c.Y + S(72) + used)), "conn", cols, rows.Cast<object>().ToList(),
                o => ((Connection)o).Key,
                o => { ProcMetric pm; return byPid.TryGetValue(((Connection)o).Pid, out pm) ? pm.Icon : null; },
                o => Glyphs.Globe,
                (o, pt) =>
                {
                    var x = (Connection)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, "Copiar dirección remota", Glyphs.Copy, x.Remote.Length > 0, () => { try { Clipboard.SetText(Endpoint(x.Remote, x.RemotePort)); } catch { } });
                    FluentMenu.Add(m, "Ver información de la IP en la web", Glyphs.OpenExternal, x.Remote.Length > 0 && !IsLocal(x.RemoteIp),
                        () => { try { Process.Start("https://ipinfo.io/" + x.Remote); } catch { } });
                    FluentMenu.Separator(m);
                    ProcMetric pm;
                    bool has = byPid.TryGetValue(x.Pid, out pm);
                    FluentMenu.Add(m, "Abrir ubicación del programa", Glyphs.Folder, has && pm.Path != null, () => host.OpenFileLocation(pm));
                    FluentMenu.Add(m, "Finalizar programa", Glyphs.Cancel, has, () => host.EndTask(pm, this));
                    if (traffic && has && pm.Path != null)
                    {
                        FluentMenu.Add(m, ProcessActions.IsBlocked(pm.Path) ? "Desbloquear en el firewall" : "Bloquear su conexión a internet", Glyphs.Shield, true,
                            () => { ProcessActions.ToggleFirewall(pm.Path, pm.Display, this); Invalidate(); });
                    }
                    m.Show(this, pt);
                }, "No hay conexiones que coincidan");
        }

        static string Endpoint(string ip, int port)
        {
            return ip.Contains(":") ? "[" + ip + "]:" + port : ip + ":" + port;
        }

        static bool IsLocal(System.Net.IPAddress ip)
        {
            if (ip == null) return true;
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (b.Length == 4) return b[0] == 10 || b[0] == 0 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(System.Net.IPAddress.IPv6Any) || (b[0] & 0xFE) == 0xFC;
        }

        // ================================================================== Programas de inicio

        List<StartupItem> startupItems;
        bool startupLoading;
        readonly Dictionary<string, Bitmap> fileIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        Bitmap FileIcon(string path)
        {
            if (path == null) return null;
            Bitmap b;
            if (fileIcons.TryGetValue(path, out b)) return b;
            try { using (var ic = Icon.ExtractAssociatedIcon(path)) b = ic != null ? ic.ToBitmap() : null; } catch { b = null; }
            fileIcons[path] = b;
            return b;
        }

        void LoadStartup()
        {
            if (startupLoading) return;
            startupLoading = true;
            List<StartupItem> result = null;
            RunInBackground("No se pudo leer la lista de inicio", () => result = Startup.Scan(), () => { if (result != null) startupItems = result; startupLoading = false; });
        }

        void PaintStartup(Graphics g, Rectangle c)
        {
            if (startupItems == null) LoadStartup();
            int enabled = startupItems == null ? 0 : startupItems.Count(i => i.Enabled);
            PageTitle(g, c, "Programas de inicio", startupItems == null ? "Leyendo…" : startupItems.Count + " programas · " + enabled + " habilitados · los cambios de \"equipo\" piden permisos de administrador");
            var sel = startupItems == null ? null : startupItems.FirstOrDefault(i => i.Id == Selected("startup"));
            int bw = S(140);
            StandardButton(g, "st-refresh", new Rectangle(c.Right - S(24) - S(44), c.Y + S(16), S(44), S(32)), Glyphs.Restart, "", false, !startupLoading, LoadStartup);
            StandardButton(g, "st-toggle", new Rectangle(c.Right - S(24) - S(52) - bw, c.Y + S(16), bw, S(32)), null,
                sel != null && !sel.Enabled ? "Habilitar" : "Deshabilitar", true, sel != null && (sel.CanToggle || sel.Kind == "Tarea"), () => ToggleStartup(sel));
            SearchBox(g, new Rectangle(c.Right - S(24) - S(60) - bw - S(260), c.Y + S(16), S(260), S(32)), "Buscar");
            if (startupItems == null) return;
            string q = Query;
            var rows = startupItems.Where(i => q.Length == 0 || (i.Name + " " + i.Command + " " + i.Publisher).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Cast<object>().ToList();
            var cols = new List<Column>
            {
                Col("Nombre", 0, false, o => ((StartupItem)o).Name),
                Col("Editor", 190, false, o => ((StartupItem)o).Publisher ?? ""),
                new Column { Title = "Estado", Width = 120, Text = o => ((StartupItem)o).Enabled ? "Habilitado" : "Deshabilitado",
                    Color = o => ((StartupItem)o).Enabled ? (Color?)null : th.TextTertiary },
                Col("Origen", 230, false, o => ((StartupItem)o).Location),
            };
            Table(g, new Rectangle(c.X, c.Y + S(76), c.Width, c.Bottom - c.Y - S(76)), "startup", cols, rows, o => ((StartupItem)o).Id,
                o => FileIcon(((StartupItem)o).Path), o => Glyphs.Startup,
                (o, pt) =>
                {
                    var it = (StartupItem)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, it.Enabled ? "Deshabilitar" : "Habilitar", it.Enabled ? Glyphs.Stop : Glyphs.Play, it.CanToggle || it.Kind == "Tarea", () => ToggleStartup(it));
                    FluentMenu.Add(m, "Abrir ubicación del archivo", Glyphs.Folder, it.Path != null,
                        () => { try { Process.Start("explorer.exe", "/select,\"" + it.Path + "\""); } catch { } });
                    FluentMenu.Add(m, "Copiar comando", Glyphs.Copy, true, () => { try { Clipboard.SetText(it.Command ?? ""); } catch { } });
                    m.Show(this, pt);
                }, "No hay programas de inicio que coincidan");
        }

        void ToggleStartup(StartupItem it)
        {
            if (it == null) return;
            bool target = !it.Enabled;
            RunInBackground("No se pudo cambiar " + it.Name, () =>
            {
                Startup.SetEnabled(it, target);
                Activity.Info(Category.Inicio, (target ? "Habilitaste" : "Deshabilitaste") + " el programa de inicio " + it.Name + " (" + it.Location + ")");
            }, LoadStartup);
        }

        // ================================================================== Servicios

        List<ServiceItem> services;
        DateTime servicesAt = DateTime.MinValue;
        bool servicesLoading;
        int serviceFilter = 0;

        void LoadServices()
        {
            if (servicesLoading) return;
            servicesLoading = true;
            servicesAt = DateTime.Now;
            List<ServiceItem> result = null;
            RunInBackground("No se pudo leer la lista de servicios", () => result = Services.List(), () => { if (result != null) services = result; servicesLoading = false; });
        }

        void PaintServices(Graphics g, Rectangle c)
        {
            if (services == null || (DateTime.Now - servicesAt).TotalSeconds > 5) LoadServices();
            int running = services == null ? 0 : services.Count(x => x.Running);
            PageTitle(g, c, "Servicios", services == null ? "Leyendo…" : services.Count + " servicios · " + running + " en ejecución · iniciar o detener puede pedir permisos de administrador");
            var sel = services == null ? null : services.FirstOrDefault(x => x.Name == Selected("services"));
            int bw = S(108);
            int x0 = c.Right - S(24);
            StandardButton(g, "sv-open", new Rectangle(x0 - S(44), c.Y + S(16), S(44), S(32)), Glyphs.OpenExternal, "", false, true,
                () => { try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); } catch { } });
            StandardButton(g, "sv-restart", new Rectangle(x0 - S(52) - bw, c.Y + S(16), bw, S(32)), Glyphs.Restart, "Reiniciar", false, sel != null && sel.Running, () => ControlService(sel, "restart"));
            StandardButton(g, "sv-stop", new Rectangle(x0 - S(60) - 2 * bw, c.Y + S(16), bw, S(32)), Glyphs.Stop, "Detener", false, sel != null && sel.Running && sel.CanStop, () => ControlService(sel, "stop"));
            StandardButton(g, "sv-start", new Rectangle(x0 - S(68) - 3 * bw, c.Y + S(16), bw, S(32)), Glyphs.Play, "Iniciar", false, sel != null && !sel.Running && sel.StartType != "Deshabilitado", () => ControlService(sel, "start"));
            SearchBox(g, new Rectangle(x0 - S(76) - 3 * bw - S(220), c.Y + S(16), S(220), S(32)), "Buscar servicio");
            int used = Chips(g, c.X + S(24), c.Y + S(72), c.Right - S(24), "svc", new[] { "Todos", "En ejecución", "Detenidos", "Automáticos" }, serviceFilter, i => serviceFilter = i);
            if (services == null) return;
            string q = Query;
            IEnumerable<ServiceItem> rows = services;
            if (serviceFilter == 1) rows = rows.Where(x => x.Running);
            if (serviceFilter == 2) rows = rows.Where(x => !x.Running);
            if (serviceFilter == 3) rows = rows.Where(x => x.StartType != null && x.StartType.StartsWith("Automático"));
            if (q.Length > 0) rows = rows.Where(x => (x.Display + " " + x.Name + " " + x.Description).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var cols = new List<Column>
            {
                Col("Nombre", 0, false, o => ((ServiceItem)o).Display),
                Col("Nombre del servicio", 200, false, o => ((ServiceItem)o).Name),
                new Column { Title = "Estado", Width = 120, Text = o => ((ServiceItem)o).Status, Color = o => ((ServiceItem)o).Running ? (Color?)th.Text : null },
                Col("Tipo de inicio", 180, false, o => ((ServiceItem)o).StartType),
            };
            Table(g, new Rectangle(c.X, c.Y + S(72) + used, c.Width, c.Bottom - (c.Y + S(72) + used)), "services", cols, rows.Cast<object>().ToList(),
                o => ((ServiceItem)o).Name, null, o => Glyphs.Services,
                (o, pt) =>
                {
                    var it = (ServiceItem)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, "Iniciar", Glyphs.Play, !it.Running && it.StartType != "Deshabilitado", () => ControlService(it, "start"));
                    FluentMenu.Add(m, "Detener", Glyphs.Stop, it.Running && it.CanStop, () => ControlService(it, "stop"));
                    FluentMenu.Add(m, "Reiniciar", Glyphs.Restart, it.Running, () => ControlService(it, "restart"));
                    FluentMenu.Separator(m);
                    FluentMenu.Add(m, "Copiar ruta del ejecutable", Glyphs.Copy, it.ImagePath != null, () => { try { Clipboard.SetText(it.ImagePath); } catch { } });
                    if (it.Description != null) FluentMenu.Add(m, it.Description.Length > 70 ? it.Description.Substring(0, 70) + "…" : it.Description, Glyphs.Info, false, null);
                    m.Show(this, pt);
                }, "No hay servicios que coincidan");
        }

        void ControlService(ServiceItem it, string action)
        {
            if (it == null) return;
            string verb = action == "start" ? "Iniciaste" : action == "stop" ? "Detuviste" : "Reiniciaste";
            RunInBackground("No se pudo cambiar el servicio " + it.Display, () =>
            {
                Services.Control(it.Name, action);
                Activity.Info(Category.Servicio, verb + " el servicio " + it.Display + " (" + it.Name + ")");
            }, () => { servicesAt = DateTime.MinValue; });
        }

        // ================================================================== Configuración

        bool helperBusy;

        /// <summary>Ruta abreviada con %LOCALAPPDATA% (más corta y sin el nombre del usuario).</summary>
        static string ShownPath(string path)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return path.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path.Substring(local.Length) : path;
        }

        void PaintSettings(Graphics g, Rectangle c)
        {
            PageTitle(g, c, "Configuración", null);
            listView = new Rectangle(c.X, c.Y + S(60), c.Width, c.Bottom - c.Y - S(60));
            var state = g.Save();
            g.SetClip(listView);
            int x = c.X + S(24), w = Math.Min(c.Width - S(48), S(900));
            int y = listView.Y - ScrollPos;
            int rowH = S(68), gap = S(4);

            Action<string> section = t => { DrawText(g, t, fonts.BodyStrong, new Rectangle(x, y + S(12), w, S(22)), th.Text); y += S(42); };
            Action<string, string, string, bool, Action> toggle = (glyph, title, desc, on, click) =>
            {
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), glyph, title, desc, S(52));
                ToggleSwitch(g, "set-" + title, cr, on, title, () => { click(); Invalidate(); });
                y += rowH + gap;
            };
            Action<string, string, string, string, Action, Action> stepper = (glyph, title, desc, value, minus, plus) =>
            {
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), glyph, title, desc, S(140));
                Stepper(g, "step-" + title, new Rectangle(cr.X, cr.Y + (cr.Height - S(32)) / 2, S(140), S(32)), value, title,
                    () => { minus(); Invalidate(); }, () => { plus(); Invalidate(); });
                y += rowH + gap;
            };
            Func<int, int, int, int> clamp = (v, lo, hi) => Math.Max(lo, Math.Min(hi, v));

            // ---- General
            section("General");
            toggle(Glyphs.Startup, "Iniciar con Windows", "Abrir el monitor en el área de notificación al iniciar sesión", host.Autostart, host.ToggleAutostart);
            toggle(Glyphs.Widget, "Widget en el escritorio", "Mostrar el widget con los anillos de uso", host.WidgetVisible, host.ToggleWidget);
            {
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), "", "Atajo de teclado", "Abre o cierra esta ventana desde cualquier programa", S(220));
                var br = new Rectangle(cr.Right - S(220), cr.Y + (cr.Height - S(32)) / 2, S(220), S(32));
                StandardButton(g, "hotkey", br, null, Settings.HotkeyNames[Math.Max(0, Math.Min(3, Settings.Hotkey))], false, true, () =>
                {
                    var m = NewMenu();
                    for (int i = 0; i < Settings.HotkeyNames.Length; i++)
                    {
                        int opt = i;
                        var it = FluentMenu.Add(m, Settings.HotkeyNames[i], null, true, () => { Settings.Hotkey = opt; host.ApplyHotkey(); Invalidate(); });
                        it.Checked = Settings.Hotkey == i;
                    }
                    m.Show(this, new Point(br.X, br.Bottom + S(2)));
                });
                y += rowH + gap;
            }

            Action<string, string, string, string[], int, Action<int>> segmented = (glyph, title, desc, opts, sel, pick) =>
            {
                int bw = 0;
                foreach (var o in opts) bw = Math.Max(bw, MeasureText(o, fonts.Body) + S(28));
                int total = opts.Length * bw + (opts.Length - 1) * S(4);
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), glyph, title, desc, total);
                for (int i = 0; i < opts.Length; i++)
                {
                    int opt = i;
                    var r = new Rectangle(cr.Right - total + i * (bw + S(4)), cr.Y + (cr.Height - S(32)) / 2, bw, S(32));
                    var hh = StandardButton(g, "seg-" + title + i, r, null, opts[i], sel == i, true, () => { pick(opt); Invalidate(); });
                    if (hh != null) { hh.Role = AccessibleRole.RadioButton; hh.Checked = sel == i; hh.Name = title + ": " + opts[i]; }
                }
                y += rowH + gap;
            };

            segmented(Glyphs.Performance, "Ícono de la bandeja", "El ícono del programa o un gráfico de barras con el uso de la CPU",
                new[] { "Programa", "Uso de CPU" }, Settings.TrayAppIcon ? 0 : 1, i => { Settings.TrayAppIcon = i == 0; host.WidgetChanged(); });

            // ---- Widget
            section("Widget");
            segmented(Glyphs.Widget, "Estilo", "El widget clásico con anillos o la isla dinámica arriba de la pantalla",
                new[] { "Widget", "Isla" }, Settings.IsIsland ? 1 : 0, i => { Settings.WidgetStyle = i == 1 ? "Isla" : "Widget"; host.WidgetChanged(); });

            if (Settings.IsIsland)
            {
                segmented("", "Posición", "Arriba al centro o justo encima de la barra de tareas",
                    new[] { "Arriba", "Sobre la barra de tareas" }, Settings.IslandPosition == "Top" ? 0 : 1,
                    i => { Settings.IslandPosition = i == 0 ? "Top" : "Taskbar"; host.WidgetChanged(); });
                toggle("", "Expandir al pasar el mouse", "Se abre a los 250 ms y se cierra al salir; si no, con un clic", Settings.IslandHoverExpand,
                    () => { Settings.IslandHoverExpand = !Settings.IslandHoverExpand; host.WidgetChanged(); });
                toggle(Glyphs.Warning, "Mostrar alertas en la isla", "Descargas, temperatura alta, procesos que acaparan la CPU, cargador y USB", Settings.IslandAlerts,
                    () => { Settings.IslandAlerts = !Settings.IslandAlerts; host.WidgetChanged(); });
                segmented("", "Colores", "Un color por métrica o todo con el color de acento de Windows",
                    new[] { "Por métrica", "Acento" }, Settings.IslandColorMode == "Accent" ? 1 : 0,
                    i => { Settings.IslandColorMode = i == 1 ? "Accent" : "PerMetric"; host.WidgetChanged(); });
                segmented(Glyphs.Performance, "Vista compacta", "También se cambia con la rueda del mouse sobre la isla",
                    new[] { "CPU · Mem · Temp", "CPU · GPU · Disco", "Red" }, Settings.IslandCompactSet,
                    i => { Settings.IslandCompactSet = i; host.WidgetChanged(); });
            }
            else
            {
            {
                Card(g, new Rectangle(x, y, w, S(100)));
                Glyph(g, Glyphs.Widget, fonts.Glyph16, new Rectangle(x + S(16), y + S(12), S(20), S(40)), th.Text);
                DrawText(g, "Anillos a mostrar", fonts.Body, new Rectangle(x + S(52), y + S(13), w - S(68), S(20)), th.Text);
                DrawText(g, "Elegí qué medidores aparecen en el widget", fonts.Caption, new Rectangle(x + S(52), y + S(33), w - S(68), S(18)), th.TextSecondary);
                int cx = x + S(46), cy = y + S(66);
                for (int i = 0; i < Settings.WidgetRingIds.Length; i++)
                {
                    string id = Settings.WidgetRingIds[i];
                    bool on = Settings.WidgetRing(id);
                    int bw = MeasureText(Settings.WidgetRingNames[i], fonts.Body) + S(44);
                    var r = new Rectangle(cx, cy - S(6), bw, S(30));
                    string key = "ring-" + id;
                    var h = AddHit(key, r, () => { Settings.WidgetRing(id, !Settings.WidgetRing(id)); host.WidgetChanged(); Invalidate(); }, Settings.WidgetRingNames[i]);
                    h.Role = AccessibleRole.CheckButton; h.Checked = on;
                    if (IsHover(key)) Fill(g, r, th.HoverOverlay, S(4));
                    var box = new Rectangle(r.X + S(6), r.Y + (r.Height - S(18)) / 2, S(18), S(18));
                    if (on) { Fill(g, box, th.Accent, S(3)); Glyph(g, Glyphs.CheckMark, fonts.Glyph12, box, th.OnAccent); }
                    else using (var p = Theme.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), S(3)))
                        using (var pen = new Pen(th.ToggleOffStroke)) g.DrawPath(pen, p);
                    DrawText(g, Settings.WidgetRingNames[i], fonts.Body, new Rectangle(box.Right + S(8), r.Y, r.Width - S(32), r.Height), th.Text);
                    cx += bw + S(8);
                }
                y += S(100) + gap;
            }
            toggle(Glyphs.Wifi, "Mostrar la red", "Velocidad de bajada y subida con su gráfico", Settings.WidgetNetwork, () => { Settings.WidgetNetwork = !Settings.WidgetNetwork; host.WidgetChanged(); });
            {
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), "", "Tamaño", null, S(270));
                string[] sizes = { "Chico", "Normal", "Grande" };
                int sx = cr.Right - S(270);
                for (int i = 0; i < 3; i++)
                {
                    var r = new Rectangle(sx + i * S(90), cr.Y + (cr.Height - S(32)) / 2, S(86), S(32));
                    int opt = i;
                    StandardButton(g, "size" + i, r, null, sizes[i], Settings.WidgetSize == i, true, () => { Settings.WidgetSize = opt; host.WidgetChanged(); Invalidate(); });
                }
                y += rowH + gap;
            }
            }

            // ---- Alertas
            section("Alertas");
            toggle(Glyphs.Warning, "Notificaciones de Windows", "Además de anotarlas en el registro, avisar con una notificación", Settings.AlertNotify, () => Settings.AlertNotify = !Settings.AlertNotify);
            stepper(Glyphs.Cpu, "CPU", "Avisar cuando supere este uso", Settings.CpuThreshold + " %", () => Settings.CpuThreshold = clamp(Settings.CpuThreshold - 5, 30, 100), () => Settings.CpuThreshold = clamp(Settings.CpuThreshold + 5, 30, 100));
            stepper(Glyphs.Cpu, "Duración", "Segundos seguidos por encima del límite de CPU", Settings.CpuSeconds + " s", () => Settings.CpuSeconds = clamp(Settings.CpuSeconds - 5, 5, 300), () => Settings.CpuSeconds = clamp(Settings.CpuSeconds + 5, 5, 300));
            stepper(Glyphs.Memory, "Memoria", "Avisar cuando supere este uso", Settings.MemThreshold + " %", () => Settings.MemThreshold = clamp(Settings.MemThreshold - 5, 30, 100), () => Settings.MemThreshold = clamp(Settings.MemThreshold + 5, 30, 100));
            stepper(Glyphs.Gpu, "GPU", "Avisar cuando supere este uso durante 15 segundos", Settings.GpuThreshold + " %", () => Settings.GpuThreshold = clamp(Settings.GpuThreshold - 5, 30, 100), () => Settings.GpuThreshold = clamp(Settings.GpuThreshold + 5, 30, 100));
            stepper(Glyphs.Temperature, "Temperatura", "Avisar a partir de esta temperatura", Settings.TempThreshold + " °C", () => Settings.TempThreshold = clamp(Settings.TempThreshold - 5, 50, 110), () => Settings.TempThreshold = clamp(Settings.TempThreshold + 5, 50, 110));
            stepper(Glyphs.HardDrive, "Espacio libre mínimo", "Avisar cuando un disco tenga menos espacio libre", Settings.DiskFreePercent + " %", () => Settings.DiskFreePercent = clamp(Settings.DiskFreePercent - 1, 1, 50), () => Settings.DiskFreePercent = clamp(Settings.DiskFreePercent + 1, 1, 50));
            stepper(Glyphs.Battery, "Batería baja", "Avisar al bajar de esta carga sin estar enchufada", Settings.BatteryLow + " %", () => Settings.BatteryLow = clamp(Settings.BatteryLow - 5, 5, 50), () => Settings.BatteryLow = clamp(Settings.BatteryLow + 5, 5, 50));

            // ---- Seguridad
            section("Seguridad");
            toggle(Glyphs.Startup, "Avisar programas de inicio nuevos", "Revisa cada 5 minutos el registro, las carpetas de Inicio y las tareas programadas", Settings.WatchStartup, () => Settings.WatchStartup = !Settings.WatchStartup);
            toggle(Glyphs.Services, "Avisar servicios nuevos", "Cuando se instala o se elimina un servicio de Windows", Settings.WatchServices, () => Settings.WatchServices = !Settings.WatchServices);
            toggle(Glyphs.Globe, "Avisar puertos en escucha nuevos", "Cuando un programa empieza a aceptar conexiones de red", Settings.WatchListeners, () => Settings.WatchListeners = !Settings.WatchListeners);
            toggle(Glyphs.Lock, "Integración con WriteBlocker", "Sumar los eventos del bloqueo de escritura a la línea de tiempo del registro", Settings.WriteBlockerBridge, () => Settings.WriteBlockerBridge = !Settings.WriteBlockerBridge);

            // ---- Funciones de administrador
            section("Funciones de administrador");
            {
                var hc = host.Helper;
                bool installed = hc != null && hc.Installed, connected = hc != null && hc.Connected;
                string status = helperBusy ? "Esperando el permiso de administrador…"
                    : !installed ? "Registro de procesos con línea de comandos, auditoría de inicios de sesión, tráfico y archivos por programa, salud SMART de los discos, firewall y control de procesos de otros usuarios"
                    : connected ? "Activas: el ayudante con privilegios está conectado"
                    : "Activadas, pero el ayudante no responde (se reintenta solo; requiere Windows de 64 bits)";
                var cr = SettingCard(g, new Rectangle(x, y, w, S(84)), Glyphs.Shield, "Activar funciones de administrador", status, S(52));
                ToggleSwitch(g, "set-helper", cr, installed, "Activar funciones de administrador", () =>
                {
                    if (hc == null || helperBusy) return;
                    bool on = !installed;
                    helperBusy = true;
                    string err = null;
                    RunInBackground("Funciones de administrador", () => { if (!hc.SetInstalled(on, out err) && err != null) throw new Exception(err); },
                        () =>
                        {
                            helperBusy = false;
                            if (err == null) Activity.Info(Category.Seguridad, on ? "Activaste las funciones de administrador" : "Desactivaste las funciones de administrador");
                        });
                    Invalidate();
                });
                y += S(84) + gap;
                if (connected && hc.Blocked.Count > 0)
                {
                    foreach (var path in hc.Blocked.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList())
                    {
                        string bp = path;
                        var br = SettingCard(g, new Rectangle(x, y, w, rowH), Glyphs.Firewall, System.IO.Path.GetFileName(bp), "Bloqueado en el firewall · " + bp, S(130));
                        StandardButton(g, "unblock-" + bp, new Rectangle(br.Right - S(130), br.Y + (br.Height - S(32)) / 2, S(130), S(32)), null, "Desbloquear", false, true,
                            () => { ProcessActions.ToggleFirewall(bp, System.IO.Path.GetFileName(bp), this); Invalidate(); });
                        y += rowH + gap;
                    }
                }
            }

            // ---- Datos
            section("Datos");
            stepper(Glyphs.History, "Conservar historial", "Días de historial de rendimiento guardados", Settings.HistoryDays + (Settings.HistoryDays == 1 ? " día" : " días"),
                () => Settings.HistoryDays = clamp(Settings.HistoryDays - 1, 1, 30), () => Settings.HistoryDays = clamp(Settings.HistoryDays + 1, 1, 30));
            {
                var cr = SettingCard(g, new Rectangle(x, y, w, rowH), Glyphs.Folder, "Carpeta de datos", ShownPath(System.IO.Path.GetDirectoryName(HistoryStore.FilePath)), S(120));
                StandardButton(g, "datafolder", new Rectangle(cr.Right - S(120), cr.Y + (cr.Height - S(32)) / 2, S(120), S(32)), null, "Abrir", false, true,
                    () => { try { Process.Start("explorer.exe", "\"" + System.IO.Path.GetDirectoryName(HistoryStore.FilePath) + "\""); } catch { } });
                y += rowH + gap;
            }
            {
                SettingCard(g, new Rectangle(x, y, w, rowH), Glyphs.Info, AppInfo.Name + " " + AppInfo.Version, "Autor: " + AppInfo.Author, 0);
                y += rowH + gap;
            }

            listContent = y + ScrollPos - listView.Y + S(16);
            g.Restore(state);
            ClampScroll();
            DrawScrollbar(g);
        }
    }
}
