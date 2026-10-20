using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>Preferencias del usuario (HKCU\Software\Panoptic).</summary>
    internal static class Settings
    {
        const string Key = @"Software\Panoptic";

        public static int Get(string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }

        public static void Set(string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        public static string GetString(string name, string def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) { var v = k == null ? null : k.GetValue(name) as string; return v ?? def; } }
            catch { return def; }
        }

        public static void SetString(string name, string value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ?? "", RegistryValueKind.String); } catch { }
        }

        public static bool Flag(string name, bool def) { return Get(name, def ? 1 : 0) == 1; }
        public static void SetFlag(string name, bool value) { Set(name, value ? 1 : 0); }

        // ---- Alertas
        public static bool AlertNotify { get { return Flag("AlertNotify", true); } set { SetFlag("AlertNotify", value); } }
        public static int CpuThreshold { get { return Get("AlertCpu", 90); } set { Set("AlertCpu", value); } }
        public static int CpuSeconds { get { return Get("AlertCpuSeconds", 15); } set { Set("AlertCpuSeconds", value); } }
        public static int MemThreshold { get { return Get("AlertMem", 90); } set { Set("AlertMem", value); } }
        public static int GpuThreshold { get { return Get("AlertGpu", 95); } set { Set("AlertGpu", value); } }
        public static int TempThreshold { get { return Get("AlertTemp", 90); } set { Set("AlertTemp", value); } }
        public static int DiskFreePercent { get { return Get("AlertDiskFree", 10); } set { Set("AlertDiskFree", value); } }
        public static int BatteryLow { get { return Get("AlertBattery", 15); } set { Set("AlertBattery", value); } }

        // ---- Seguridad
        public static bool WatchStartup { get { return Flag("WatchStartup", true); } set { SetFlag("WatchStartup", value); } }
        public static bool WatchServices { get { return Flag("WatchServices", true); } set { SetFlag("WatchServices", value); } }
        public static bool WatchListeners { get { return Flag("WatchListeners", true); } set { SetFlag("WatchListeners", value); } }
        public static bool WriteBlockerBridge { get { return Flag("WriteBlockerBridge", true); } set { SetFlag("WriteBlockerBridge", value); } }

        // ---- Atajo de teclado global: 0 apagado, 1 Ctrl+Shift+M, 2 Ctrl+Alt+M, 3 Win+Shift+M
        public static int Hotkey { get { return Get("Hotkey", 1); } set { Set("Hotkey", value); } }
        public static readonly string[] HotkeyNames = { "Desactivado", "Ctrl + Mayús + M", "Ctrl + Alt + M", "Windows + Mayús + M" };

        // ---- Historial
        /// <summary>Días que se conserva el registro de actividad.</summary>
        public static int LogDays { get { return Get("LogDays", 30); } set { Set("LogDays", value); } }
        public static readonly int[] LogDayOptions = { 7, 15, 30, 60, 90, 180, 365 };
        public static int HistoryDays { get { return Get("HistoryDays", 7); } set { Set("HistoryDays", value); } }

        // ---- Estilo del widget: el widget clásico o la isla dinámica
        /// <summary>"Widget" o "Isla".</summary>
        public static string WidgetStyle { get { return GetString("WidgetStyle", "Isla") == "Widget" ? "Widget" : "Isla"; } set { SetString("WidgetStyle", value); } }
        public static bool IsIsland { get { return WidgetStyle == "Isla"; } }
        /// <summary>"Top" (arriba al centro) o "Taskbar" (sobre la barra de tareas).</summary>
        /// <summary>"Top", "Taskbar" (abajo, sobre la barra de tareas), "Left" o "Right".</summary>
        public static string IslandPosition
        {
            get
            {
                string v = GetString("IslandPosition", "Top");
                return v == "Taskbar" || v == "Left" || v == "Right" ? v : "Top";
            }
            set { SetString("IslandPosition", value); }
        }
        public static readonly string[] IslandPositions = { "Top", "Taskbar", "Left", "Right" };
        public static readonly string[] IslandPositionNames = { "Arriba", "Abajo", "Izquierda", "Derecha" };
        /// <summary>La isla se esconde en el borde y aparece al acercar el mouse.</summary>
        public static bool IslandAutoHide { get { return Flag("IslandAutoHide", false); } set { SetFlag("IslandAutoHide", value); } }
        public static bool IslandHoverExpand { get { return Flag("IslandHoverExpand", false); } set { SetFlag("IslandHoverExpand", value); } }
        public static bool IslandAlerts { get { return Flag("IslandAlerts", true); } set { SetFlag("IslandAlerts", value); } }
        /// <summary>"PerMetric" (un color por métrica) o "Accent" (todos con el color de acento).</summary>
        public static string IslandColorMode { get { return GetString("IslandColorMode", "PerMetric") == "Accent" ? "Accent" : "PerMetric"; } set { SetString("IslandColorMode", value); } }
        /// <summary>0 CPU·Memoria·Temperatura, 1 CPU·GPU·Disco, 2 Red.</summary>
        public static int IslandCompactSet { get { return Math.Max(0, Math.Min(2, Get("IslandCompactSet", 0))); } set { Set("IslandCompactSet", value); } }

        // ---- Widget
        public static readonly string[] WidgetRingIds = { "cpu", "mem", "disk", "gpu", "temp", "bat" };
        public static readonly string[] WidgetRingNames = { "CPU", "Memoria", "Disco", "GPU", "Temperatura", "Batería" };
        public static bool WidgetRing(string id) { return Flag("WidgetRing_" + id, id == "cpu" || id == "mem" || id == "disk"); }
        public static void WidgetRing(string id, bool on) { SetFlag("WidgetRing_" + id, on); }
        /// <summary>Ícono de la bandeja: el del programa (true) o las barras de uso de CPU (false).</summary>
        public static bool TrayAppIcon { get { return Flag("TrayAppIcon", true); } set { SetFlag("TrayAppIcon", value); } }
        public static bool WidgetNetwork { get { return Flag("WidgetNetwork", true); } set { SetFlag("WidgetNetwork", value); } }
        /// <summary>0 chico, 1 normal, 2 grande.</summary>
        public static int WidgetSize { get { return Get("WidgetSize", 1); } set { Set("WidgetSize", value); } }
    }

    internal sealed class HistoryPoint
    {
        public DateTime Time;
        public float Cpu, Mem, Gpu, Disk, Temp, Battery;
        public double NetBps;
    }

    /// <summary>
    /// Historial de largo plazo: promedio de cada minuto guardado en un CSV, con los días configurados.
    /// </summary>
    internal sealed class HistoryStore
    {
        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Panoptic", "historial.csv"); }
        }

        readonly object gate = new object();
        readonly List<HistoryPoint> points = new List<HistoryPoint>();
        DateTime minute = DateTime.MinValue;
        double cpu, mem, gpu, disk, temp, bat, net;
        int n, tempN, batN;

        public HistoryStore()
        {
            Load();
        }

        /// <summary>Acumula una muestra por segundo; al cambiar el minuto guarda el promedio.</summary>
        public void Add(Snapshot s)
        {
            var m = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0);
            if (minute != DateTime.MinValue && m != minute && n > 0) Flush();
            minute = m;
            cpu += s.Cpu; mem += s.MemPercent; gpu += s.Gpu;
            disk += s.Disks.Count > 0 ? s.Disks.Max(d => d.Active) : 0;
            net += s.Nets.Sum(x => x.SendBps + x.RecvBps);
            if (!double.IsNaN(s.Temperature)) { temp += s.Temperature; tempN++; }
            if (s.HasBattery) { bat += s.BatteryPercent; batN++; }
            n++;
        }

        public static bool ReadOnly; // modo de medición: no escribir el archivo

        void Flush()
        {
            var p = new HistoryPoint
            {
                Time = minute, Cpu = (float)(cpu / n), Mem = (float)(mem / n), Gpu = (float)(gpu / n), Disk = (float)(disk / n),
                NetBps = net / n, Temp = tempN > 0 ? (float)(temp / tempN) : float.NaN, Battery = batN > 0 ? (float)(bat / batN) : float.NaN
            };
            cpu = mem = gpu = disk = temp = bat = net = 0; n = tempN = batN = 0;
            Version++;
            lock (gate)
            {
                points.Add(p);
                var limit = DateTime.Now.AddDays(-Settings.HistoryDays);
                int drop = points.FindIndex(x => x.Time >= limit);
                if (drop > 0) points.RemoveRange(0, drop);
            }
            if (ReadOnly) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, Line(p) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static string Line(HistoryPoint p)
        {
            var c = CultureInfo.InvariantCulture;
            return p.Time.ToString("yyyy-MM-dd HH:mm", c) + ";" + p.Cpu.ToString("0.0", c) + ";" + p.Mem.ToString("0.0", c) + ";" +
                   p.Gpu.ToString("0.0", c) + ";" + p.Disk.ToString("0.0", c) + ";" + p.NetBps.ToString("0", c) + ";" +
                   (float.IsNaN(p.Temp) ? "" : p.Temp.ToString("0.0", c)) + ";" + (float.IsNaN(p.Battery) ? "" : p.Battery.ToString("0", c));
        }

        void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var limit = DateTime.Now.AddDays(-Settings.HistoryDays);
                var c = CultureInfo.InvariantCulture;
                var keep = new List<string>();
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split(';');
                    if (f.Length < 8) continue;
                    DateTime t;
                    if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm", c, DateTimeStyles.None, out t) || t < limit) continue;
                    var p = new HistoryPoint { Time = t };
                    float.TryParse(f[1], NumberStyles.Float, c, out p.Cpu);
                    float.TryParse(f[2], NumberStyles.Float, c, out p.Mem);
                    float.TryParse(f[3], NumberStyles.Float, c, out p.Gpu);
                    float.TryParse(f[4], NumberStyles.Float, c, out p.Disk);
                    double.TryParse(f[5], NumberStyles.Float, c, out p.NetBps);
                    p.Temp = f[6].Length > 0 ? float.Parse(f[6], c) : float.NaN;
                    p.Battery = f[7].Length > 0 ? float.Parse(f[7], c) : float.NaN;
                    points.Add(p);
                    keep.Add(line);
                }
                // Reescribir el archivo sin los días que ya no se conservan.
                File.WriteAllLines(FilePath, keep, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Solo para las capturas de vista previa.</summary>
        internal void ReplaceForPreview(IEnumerable<HistoryPoint> pts) { lock (gate) { points.Clear(); points.AddRange(pts); Version++; } }

        /// <summary>Cambia cada vez que se agrega un minuto.</summary>
        public volatile int Version;

        public List<HistoryPoint> Range(DateTime from)
        {
            lock (gate) return points.Where(p => p.Time >= from).ToList();
        }
    }
}
