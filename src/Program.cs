using System;
using System.Threading;
using System.Windows.Forms;

namespace Panoptic
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && args[0] == "--render")
                return Preview.Render(args);
            if (args.Length >= 2 && args[0] == "--selftest")
                return SelfTest(args[1]);
            // Ayudante con privilegios y su instalación (se ejecutan elevados).
            if (args.Length >= 1 && args[0] == "--helper") return HelperService.Run();
            if (args.Length >= 1 && args[0] == "--install-helper") return HelperService.Install();
            if (args.Length >= 1 && args[0] == "--uninstall-helper") return HelperService.Uninstall();
            if (args.Length >= 3 && args[0] == "--helper-query") return HelperQuery(args);
            if (args.Length >= 2 && args[0] == "--profile")
                return Profile(args[1]);

            bool startInTray = Array.IndexOf(args, "--tray") >= 0;
            string suffix = "";
            string profile = Environment.GetEnvironmentVariable("PANOPTIC_PROFILE");
            if (!string.IsNullOrEmpty(profile))
            {
                // Modo de medición: corre al lado de la instancia normal, sin tocar archivos ni configuración.
                App.ProfileMode = true;
                Activity.MemoryOnly = true;
                HistoryStore.ReadOnly = true;
                Surface.ProfilePaint = true;
                startInTray = true;
                suffix = ".Profile";
                StartProfileTimer(profile);
            }
            if (!App.ProfileMode) Migration.Run();
            bool created;
            using (var mutex = new Mutex(true, @"Local\Panoptic.SingleInstance" + suffix, out created))
            {
                if (!created)
                {
                    // Ya está abierto: mostrar la ventana de la instancia que corre.
                    try { using (var ev = EventWaitHandle.OpenExisting(@"Local\Panoptic.Show" + suffix)) ev.Set(); } catch { }
                    return 0;
                }
                using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Panoptic.Show" + suffix))
                    Application.Run(new App(ev, startInTray));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Después de 5 s mide 30 s de uso real (CPU y pintado de cada ventana), lo guarda y cierra.</summary>
        static void StartProfileTimer(string path)
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            TimeSpan cpu0 = TimeSpan.Zero;
            var t0 = DateTime.UtcNow;
            new Thread(() =>
            {
                Thread.Sleep(5000);
                me.Refresh(); cpu0 = me.TotalProcessorTime; t0 = DateTime.UtcNow;
                lock (Surface.PaintStats) Surface.PaintStats.Clear();
                Thread.Sleep(30000);
                me.Refresh();
                double secs = (DateTime.UtcNow - t0).TotalSeconds;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("CPU del proceso: " + ((me.TotalProcessorTime - cpu0).TotalMilliseconds / secs).ToString("0.0") + " ms por segundo");
                sb.AppendLine("Memoria privada: " + (me.PrivateMemorySize64 / 1048576.0).ToString("0.0") + " MB");
                lock (Surface.PaintStats)
                    foreach (var kv in Surface.PaintStats)
                        sb.AppendLine(kv.Key.PadRight(14) + (kv.Value[0] / secs).ToString("0.0") + " cuadros/s, " +
                            (kv.Value[0] > 0 ? kv.Value[1] / kv.Value[0] : 0).ToString("0.00") + " ms por cuadro, " +
                            (kv.Value[1] / secs).ToString("0.0") + " ms/s");
                System.IO.File.WriteAllText(path, sb.ToString());
                Environment.Exit(0);
            }) { IsBackground = true }.Start();
        }

        /// <summary>Diagnóstico: envía comandos al ayudante y guarda las respuestas (--helper-query archivo CMD [CMD…]; "WAIT n" espera n segundos).</summary>
        static int HelperQuery(string[] args)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                using (var p = new System.IO.Pipes.NamedPipeClientStream(".", HelperService.PipeName, System.IO.Pipes.PipeDirection.InOut))
                {
                    p.Connect(3000);
                    var r = new System.IO.StreamReader(p, new System.Text.UTF8Encoding(false));
                    var w = new System.IO.StreamWriter(p, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i].StartsWith("WAIT ")) { Thread.Sleep(int.Parse(args[i].Substring(5)) * 1000); continue; }
                        sb.AppendLine("> " + args[i]);
                        w.Write(args[i].Replace(' ', '\t') + "\n"); w.Flush();
                        string l;
                        while ((l = r.ReadLine()) != null && l != "END") sb.AppendLine(l);
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR " + ex.Message); }
            System.IO.File.WriteAllText(args[1], sb.ToString());
            return 0;
        }

        /// <summary>Mide cuánto tarda cada parte del muestreo durante 30 s (diagnóstico de rendimiento).</summary>
        static int Profile(string path)
        {
            Activity.MemoryOnly = true;
            var me = System.Diagnostics.Process.GetCurrentProcess();
            var sb = new System.Text.StringBuilder();
            using (var s = new Sampler())
            {
                var hist = new HistoryStore();
                s.Sampled += hist.Add;
                Thread.Sleep(3000);
                lock (Sampler.ProfileMs) { Sampler.ProfileMs.Clear(); Sampler.ProfileSamples = 0; }
                me.Refresh();
                var cpu0 = me.TotalProcessorTime;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Thread.Sleep(30000);
                me.Refresh();
                double cpuMs = (me.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalSeconds;
                int n = Math.Max(1, Sampler.ProfileSamples);
                sb.AppendLine("CPU del proceso: " + cpuMs.ToString("0.0") + " ms por segundo");
                foreach (var kv in Sampler.ProfileMs) sb.AppendLine(kv.Key.PadRight(14) + (kv.Value / n).ToString("0.00") + " ms por muestra");
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            return 0;
        }

        /// <summary>Toma muestras reales durante unos segundos y las vuelca a un archivo (diagnóstico).</summary>
        static int SelfTest(string path)
        {
            Activity.MemoryOnly = true;
            var sb = new System.Text.StringBuilder();
            using (var s = new Sampler())
            {
                s.ProcessStarted += p => { lock (sb) sb.AppendLine("START " + p.Display + " " + p.Pid); };
                s.ProcessExited += (p, t) => { lock (sb) sb.AppendLine("EXIT " + p.Display + " " + p.Pid); };
                Thread.Sleep(5500);
                var x = s.Latest;
                lock (sb)
                {
                    sb.AppendLine("CPU " + x.Cpu + " % " + x.CpuMhz + " MHz " + x.CpuName + " cores " + x.Cores + " logical " + x.Logical);
                    sb.AppendLine("MEM " + Format.Bytes(x.MemUsed) + " / " + Format.Bytes(x.MemTotal) + " cache " + Format.Bytes(x.MemCached));
                    sb.AppendLine("PROCS " + x.ProcessCount + " threads " + x.ThreadCount + " handles " + x.HandleCount + " uptime " + x.Uptime);
                    foreach (var d in x.Disks) sb.AppendLine("DISK " + d.Title + " | " + d.Name + " | " + d.Kind + " | " + d.Active + " % | " + Format.Bytes(d.VolumeFree) + " libres");
                    foreach (var n in x.Nets) sb.AppendLine("NET " + n.Kind + " | " + n.Name + " | " + n.Description + " | " + n.Ipv4 + " | " + n.Ssid + " | R " + Format.Bits(n.RecvBps));
                    foreach (var p in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(x.Procs, q => q.Cpu), 8))
                        sb.AppendLine("PROC " + p.Display + " | " + p.Cpu + " % | " + Format.Bytes(p.Memory) + " | app " + p.IsApp + " | icon " + (p.Icon != null));
                    sb.AppendLine("APPS " + System.Linq.Enumerable.Count(x.Procs, q => q.IsApp));
                    sb.AppendLine("GPU " + x.GpuAvailable + " " + x.Gpu + " % " + x.GpuName + " motor " + x.GpuEngine + " ded " + Format.Bytes(x.GpuDedicated) + " sh " + Format.Bytes(x.GpuShared));
                    sb.AppendLine("TEMP " + x.Temperature);
                    sb.AppendLine("BAT " + x.HasBattery + " " + x.BatteryPercent + " % ac " + x.OnAc + " salud " + x.BatteryHealth + " W " + x.BatteryRateMw);
                }
            }
            var st = Startup.Scan();
            sb.AppendLine("STARTUP " + st.Count);
            foreach (var i in st) sb.AppendLine("  " + i.Kind + " | " + i.Name + " | " + i.Location + " | " + (i.Enabled ? "on" : "off") + " | " + i.Publisher);
            sb.AppendLine("SERVICES " + Services.List().Count);
            var cn = NetConnections.Snapshot(true);
            sb.AppendLine("CONN " + cn.Count + " listen " + System.Linq.Enumerable.Count(cn, c => c.Listening));
            {
                {
                }
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            return 0;
        }
    }
}
