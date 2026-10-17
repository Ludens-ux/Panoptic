using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>Uso de la GPU por motor y por proceso, como lo calcula el Administrador de tareas.</summary>
    internal sealed class GpuSensor
    {
        Dictionary<string, CounterSample> last = new Dictionary<string, CounterSample>();
        public string Name;
        public bool Available = true;
        DateTime lastMem = DateTime.MinValue;
        public ulong Dedicated, Shared;

        public GpuSensor()
        {
            Name = ReadName();
            try { Available = PerformanceCounterCategory.Exists("GPU Engine"); } catch { Available = false; }
        }

        /// <summary>Uso total (el motor más ocupado del adaptador más ocupado) y uso por PID.</summary>
        public float Sample(out Dictionary<int, float> perPid, out string busiestEngine)
        {
            perPid = new Dictionary<int, float>();
            busiestEngine = null;
            if (!Available) return 0;
            InstanceDataCollection data;
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var all = cat.ReadCategory();
                data = all["Utilization Percentage"];
            }
            catch { return 0; }
            if (data == null) return 0;

            var now = new Dictionary<string, CounterSample>();
            var byEngine = new Dictionary<string, float>();             // luid+engtype → suma
            var byPidEngine = new Dictionary<string, float>();          // pid+engtype → suma
            foreach (InstanceData inst in data.Values)
            {
                string name = inst.InstanceName;
                now[name] = inst.Sample;
                CounterSample prev;
                if (!last.TryGetValue(name, out prev)) continue;
                float v;
                try { v = CounterSample.Calculate(prev, inst.Sample); } catch { continue; }
                if (v <= 0) continue;
                // pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D
                int pid = ParseInt(Between(name, "pid_", "_"));
                string luid = Between(name, "luid_", "_phys");
                string eng = After(name, "engtype_");
                string ek = luid + "|" + eng;
                float cur;
                byEngine[ek] = (byEngine.TryGetValue(ek, out cur) ? cur : 0) + v;
                string pk = pid + "|" + eng;
                byPidEngine[pk] = (byPidEngine.TryGetValue(pk, out cur) ? cur : 0) + v;
            }
            last = now;
            foreach (var kv in byPidEngine)
            {
                int pid = ParseInt(kv.Key.Substring(0, kv.Key.IndexOf('|')));
                float cur;
                if (!perPid.TryGetValue(pid, out cur) || kv.Value > cur) perPid[pid] = Math.Min(100, kv.Value);
            }
            float best = 0;
            foreach (var kv in byEngine)
                if (kv.Value > best) { best = kv.Value; busiestEngine = kv.Key.Substring(kv.Key.IndexOf('|') + 1); }

            if ((DateTime.UtcNow - lastMem).TotalSeconds >= 2) { lastMem = DateTime.UtcNow; ReadMemory(); }
            return Math.Min(100, best);
        }

        void ReadMemory()
        {
            try
            {
                var all = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
                ulong ded = 0, sh = 0;
                foreach (InstanceData i in all["Dedicated Usage"].Values) ded = Math.Max(ded, (ulong)Math.Max(0, i.RawValue));
                foreach (InstanceData i in all["Shared Usage"].Values) sh = Math.Max(sh, (ulong)Math.Max(0, i.RawValue));
                Dedicated = ded; Shared = sh;
            }
            catch { }
        }

        static string ReadName()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject mo in q.Get())
                        using (mo)
                        {
                            string n = (mo["Name"] as string ?? "").Trim();
                            string l = n.ToLowerInvariant();
                            if (n.Length == 0 || l.Contains("virtual") || l.Contains("parsec") || l.Contains("basic display") || l.Contains("remote")) continue;
                            return n;
                        }
            }
            catch { }
            return "GPU";
        }

        static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            if (i < 0) return "";
            i += a.Length;
            int j = s.IndexOf(b, i, StringComparison.Ordinal);
            return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
        }

        static string After(string s, string a)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            return i < 0 ? "" : s.Substring(i + a.Length);
        }

        static int ParseInt(string s) { int v; return int.TryParse(s, out v) ? v : -1; }
    }

    /// <summary>Temperatura de las zonas térmicas ACPI (contador "Thermal Zone Information", sin permisos de administrador).</summary>
    internal sealed class ThermalSensor
    {
        readonly List<PerformanceCounter> counters = new List<PerformanceCounter>();
        readonly bool highPrecision;

        public ThermalSensor()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("Thermal Zone Information")) return;
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                highPrecision = cat.CounterExists("High Precision Temperature");
                foreach (var inst in cat.GetInstanceNames())
                    counters.Add(new PerformanceCounter("Thermal Zone Information", highPrecision ? "High Precision Temperature" : "Temperature", inst, true));
            }
            catch { counters.Clear(); }
        }

        public bool Available { get { return counters.Count > 0; } }

        /// <summary>Temperatura más alta en °C, o NaN si no hay sensores.</summary>
        public double Read()
        {
            double best = double.NaN;
            foreach (var c in counters)
            {
                try
                {
                    double k = c.NextValue();
                    if (highPrecision) k /= 10.0;
                    double cdeg = k - 273.15;
                    if (cdeg > -20 && cdeg < 150 && (double.IsNaN(best) || cdeg > best)) best = cdeg;
                }
                catch { }
            }
            return best;
        }
    }

    /// <summary>Estado de la batería: carga, enchufe, tiempo restante, salud y potencia.</summary>
    internal sealed class BatterySensor
    {
        public bool Present;
        public float Health = -1;          // capacidad completa / capacidad de diseño, en %
        public ulong DesignMwh, FullMwh;
        DateTime lastRate = DateTime.MinValue;
        public int RateMw;                 // positivo cargando, negativo descargando

        public BatterySensor()
        {
            var ps = SystemInformation.PowerStatus;
            Present = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
            if (!Present) return;
            try
            {
                var scope = new ManagementScope(@"\\.\root\WMI");
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData")))
                    foreach (ManagementObject mo in q.Get()) using (mo) DesignMwh += Convert.ToUInt64(mo["DesignedCapacity"]);
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
                    foreach (ManagementObject mo in q.Get()) using (mo) FullMwh += Convert.ToUInt64(mo["FullChargedCapacity"]);
                if (DesignMwh > 0 && FullMwh > 0) Health = (float)Math.Min(100, 100.0 * FullMwh / DesignMwh);
            }
            catch { }
        }

        public void Sample(Snapshot s)
        {
            if (!Present) return;
            var ps = SystemInformation.PowerStatus;
            s.HasBattery = true;
            s.BatteryPercent = Math.Max(0, Math.Min(100, ps.BatteryLifePercent * 100f));
            s.OnAc = ps.PowerLineStatus == PowerLineStatus.Online;
            s.Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            s.BatteryMinutes = ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining / 60 : -1;
            s.BatteryHealth = Health;
            if ((DateTime.UtcNow - lastRate).TotalSeconds >= 10)
            {
                lastRate = DateTime.UtcNow;
                try
                {
                    var scope = new ManagementScope(@"\\.\root\WMI");
                    int rate = 0;
                    using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ChargeRate, DischargeRate FROM BatteryStatus")))
                        foreach (ManagementObject mo in q.Get())
                            using (mo)
                            {
                                int c = Convert.ToInt32(mo["ChargeRate"]), d = Convert.ToInt32(mo["DischargeRate"]);
                                rate += c > 0 ? c : -d;
                            }
                    RateMw = rate;
                }
                catch { }
            }
            s.BatteryRateMw = RateMw;
        }
    }
}
