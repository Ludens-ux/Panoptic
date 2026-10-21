using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>Historial circular de 60 muestras (un minuto).</summary>
    internal sealed class History
    {
        public const int Size = 60;
        readonly float[] buf = new float[Size];
        int count, head;

        public void Add(float v)
        {
            buf[head] = v;
            head = (head + 1) % Size;
            if (count < Size) count++;
        }

        /// <summary>Copia de los valores, del más viejo al más nuevo, siempre de largo Size (relleno con 0 al principio).</summary>
        public float[] Snapshot()
        {
            var r = new float[Size];
            for (int i = 0; i < count; i++)
                r[Size - count + i] = buf[(head - count + i + Size * 2) % Size];
            return r;
        }
    }

    internal sealed class DiskMetric
    {
        public int Number;
        public string Name, Letters, Kind, Instance;
        public float Active;
        public double ReadBps, WriteBps, ResponseMs;
        public ulong Capacity, VolumeTotal, VolumeFree;
        public float[] ActivityHistory;
        public float[] TransferHistory;
        public string Title { get { return "Disco " + Number + (string.IsNullOrEmpty(Letters) ? "" : " (" + Letters + ")"); } }
    }

    internal sealed class NetMetric
    {
        public string Id, Name, Description, Kind, Ipv4, Ssid;
        public double SendBps, RecvBps;
        public long LinkSpeed;
        public float[] SendHistory, RecvHistory;
    }

    internal sealed class ProcMetric
    {
        public int Pid;
        public string Name, Description, Path;
        public float Cpu, Gpu;
        public ulong Memory;      // memoria privada (como "Memoria" en el Administrador de tareas)
        public double DiskBps;
        public bool IsApp;
        public DateTime Started;
        public Bitmap Icon;
        public string Display { get { return string.IsNullOrEmpty(Description) ? Name : Description; } }
    }

    internal sealed class Snapshot
    {
        public float Cpu;
        public double CpuMhz, CpuBaseMhz;
        public string CpuName;
        public int Logical, Cores;
        public float[] CpuHistory;

        public ulong MemTotal, MemUsed, MemCached, CommitUsed, CommitLimit;
        public float MemPercent;
        public float[] MemHistory;

        public List<DiskMetric> Disks = new List<DiskMetric>();
        public List<NetMetric> Nets = new List<NetMetric>();
        public List<ProcMetric> Procs = new List<ProcMetric>();
        public int ProcessCount, ThreadCount, HandleCount;
        public TimeSpan Uptime;
        public DateTime Time;

        public float Gpu;
        public string GpuName, GpuEngine;
        public float[] GpuHistory;
        public ulong GpuDedicated, GpuShared;
        public bool GpuAvailable;

        public double Temperature = double.NaN;      // CPU (sensor ACPI del equipo)
        public double GpuTemperature = double.NaN;   // GPU dedicada (NaN si el driver no la informa)
        public float[] TempHistory;

        public bool HasBattery, OnAc, Charging;
        public float BatteryPercent, BatteryHealth = -1;
        public int BatteryMinutes = -1, BatteryRateMw;
        public float[] BatteryHistory;
    }

    /// <summary>
    /// Toma una muestra por segundo de CPU, memoria, discos y red, y cada dos segundos de los procesos.
    /// Corre en un hilo propio; la interfaz lee la última muestra publicada.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        public volatile Snapshot Latest;
        public event Action<ProcMetric> ProcessStarted;
        public event Action<ProcMetric, TimeSpan> ProcessExited;
        public event Action<string> Alert;
        /// <summary>Se dispara en el hilo del muestreo después de cada muestra.</summary>
        public event Action<Snapshot> Sampled;

        readonly Timer timer;
        int busy;
        long tick;

        // CPU
        ulong lastIdle, lastKernel, lastUser;
        readonly History cpuHist = new History(), memHist = new History();
        PerformanceCounter cpuPerf;
        double lastMhz;
        readonly string cpuName;
        readonly double cpuBaseMhz;
        volatile int cores;

        // Discos
        sealed class DiskState
        {
            public DiskMetric Meta;
            public PerformanceCounter Idle, Read, Write, Resp;
            public History Activity = new History(), Transfer = new History();
        }
        List<DiskState> disks = new List<DiskState>();
        volatile bool disksDirty = true;

        // Red
        sealed class NetState
        {
            public NetworkInterface Nic;
            public long LastSent, LastRecv;
            public History Send = new History(), Recv = new History();
            public string Ssid;
        }
        readonly Dictionary<string, NetState> nets = new Dictionary<string, NetState>();
        volatile bool netsDirty = true;

        // Procesos
        sealed class ProcState
        {
            public ProcMetric M = new ProcMetric();
            public ulong CpuTime, IoBytes, CreationTime;
            public bool Fresh;
        }
        Dictionary<long, ProcState> procs = new Dictionary<long, ProcState>();
        bool procBaseline;
        DateTime lastProcSample = DateTime.MinValue;
        readonly Dictionary<string, Tuple<string, Bitmap>> fileInfoCache = new Dictionary<string, Tuple<string, Bitmap>>(StringComparer.OrdinalIgnoreCase);
        public volatile bool DetailedProcesses = true;

        // Alertas
        int cpuHighCount, cpuLowCount, memHighCount, memLowCount;
        bool cpuAlerted, memAlerted, gpuAlerted, tempAlerted, batteryAlerted;
        int gpuHighCount, gpuLowCount;

        // GPU, temperatura y batería
        // Se crean en segundo plano: al encender la PC, WMI y los contadores de rendimiento pueden tardar minutos.
        GpuSensor gpu;
        ThermalSensor thermal;
        BatterySensor battery;
        volatile bool sensorsReady, disksReady;
        readonly History gpuHist = new History(), tempHist = new History(), batHist = new History();
        Dictionary<int, float> gpuPerPid = new Dictionary<int, float>();
        readonly HashSet<string> lowSpaceAlerted = new HashSet<string>();

        public Sampler()
        {
            cpuName = ReadCpuName(out cpuBaseMhz);
            WarmUp();
            NetworkChange.NetworkAddressChanged += (s, e) => netsDirty = true;
            Native.FILETIME i, k, u;
            Native.GetSystemTimes(out i, out k, out u);
            lastIdle = i.Value; lastKernel = k.Value; lastUser = u.Value;
            timer = new Timer(Tick, null, 0, 1000);
        }

        /// <summary>
        /// Lo que depende de WMI o de los contadores de rendimiento se prepara en segundo plano: la primera medición
        /// (CPU, memoria, red y procesos) sale enseguida y el resto se suma apenas está listo.
        /// </summary>
        void WarmUp()
        {
            new Thread(() =>
            {
                try { battery = new BatterySensor(); } catch { }
                try { cores = ReadCoreCount(); } catch { }
                try { var c = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true); c.NextValue(); cpuPerf = c; } catch { }
                try { thermal = new ThermalSensor(); } catch { }
                try { gpu = new GpuSensor(); } catch { }
                sensorsReady = true;
            }) { IsBackground = true, Name = "Sensores" }.Start();
            new Thread(() =>
            {
                try { RebuildDisks(); } catch { disksDirty = true; }
                disksReady = true;
            }) { IsBackground = true, Name = "Discos" }.Start();
        }

        public void InvalidateDisks() { disksDirty = true; }
        public void InvalidateNetworks() { netsDirty = true; }

        void Tick(object state)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try { Sample(); }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // ---- Perfilado (solo diagnóstico)
        public static readonly Dictionary<string, double> ProfileMs = new Dictionary<string, double>();
        public static int ProfileSamples;
        readonly Stopwatch sw = new Stopwatch();
        void Mark(string name) { double v; ProfileMs.TryGetValue(name, out v); ProfileMs[name] = v + sw.Elapsed.TotalMilliseconds; sw.Restart(); }

        void Sample()
        {
            sw.Restart(); ProfileSamples++;
            tick++;
            var s = new Snapshot();
            s.Time = DateTime.Now;
            s.Uptime = TimeSpan.FromMilliseconds(Native.GetTickCount64());

            // ---- CPU
            Native.FILETIME fi, fk, fu;
            Native.GetSystemTimes(out fi, out fk, out fu);
            ulong idle = fi.Value - lastIdle, kernel = fk.Value - lastKernel, user = fu.Value - lastUser;
            lastIdle = fi.Value; lastKernel = fk.Value; lastUser = fu.Value;
            ulong total = kernel + user;
            s.Cpu = total == 0 ? 0 : Clamp(100f * (total - idle) / total);
            cpuHist.Add(s.Cpu);
            s.CpuHistory = cpuHist.Snapshot();
            s.CpuName = cpuName;
            s.CpuBaseMhz = cpuBaseMhz;
            s.Logical = Environment.ProcessorCount;
            s.Cores = cores;
            // La velocidad se lee cada 2 s: el contador "% Processor Performance" es lento (tiene una instancia por procesador lógico).
            if (cpuPerf != null && tick % 2 == 1) try { lastMhz = cpuBaseMhz * cpuPerf.NextValue() / 100.0; } catch { }
            s.CpuMhz = lastMhz;

            // ---- Memoria
            var m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref m))
            {
                s.MemTotal = m.ullTotalPhys;
                s.MemUsed = m.ullTotalPhys - m.ullAvailPhys;
                s.MemPercent = Clamp(100f * s.MemUsed / Math.Max(1UL, s.MemTotal));
                s.CommitLimit = m.ullTotalPageFile;
                s.CommitUsed = m.ullTotalPageFile - m.ullAvailPageFile;
            }
            Native.PERFORMANCE_INFORMATION pi;
            if (Native.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION))))
            {
                ulong page = pi.PageSize.ToUInt64();
                s.MemCached = pi.SystemCache.ToUInt64() * page;
                s.ThreadCount = (int)pi.ThreadCount;
                s.HandleCount = (int)pi.HandleCount;
            }
            memHist.Add(s.MemPercent);
            s.MemHistory = memHist.Snapshot();

            Mark("cpu+mem");
            // ---- GPU, temperatura y batería
            string engine = null;
            if (sensorsReady && gpu != null)
            {
                s.GpuAvailable = gpu.Available;
                s.Gpu = gpu.Sample(out gpuPerPid, out engine);
                s.GpuName = gpu.Name;
                s.GpuDedicated = gpu.Dedicated;
                s.GpuShared = gpu.Shared;
                s.GpuTemperature = gpu.Temperature;
            }
            s.GpuEngine = engine;
            gpuHist.Add(s.Gpu);
            s.GpuHistory = gpuHist.Snapshot();
            if (sensorsReady && thermal != null && thermal.Available) s.Temperature = thermal.Read();
            tempHist.Add(double.IsNaN(s.Temperature) ? 0 : (float)s.Temperature);
            s.TempHistory = tempHist.Snapshot();
            if (sensorsReady && battery != null) battery.Sample(s);
            batHist.Add(s.BatteryPercent);
            s.BatteryHistory = batHist.Snapshot();

            Mark("gpu+temp+bat");
            // ---- Discos
            if (disksReady && disksDirty) RebuildDisks();
            foreach (var d in disksReady ? disks : new List<DiskState>())
            {
                var dm = d.Meta;
                var o = new DiskMetric
                {
                    Number = dm.Number, Name = dm.Name, Letters = dm.Letters, Kind = dm.Kind, Instance = dm.Instance,
                    Capacity = dm.Capacity
                };
                try
                {
                    o.Active = Clamp(100f - d.Idle.NextValue());
                    o.ReadBps = d.Read.NextValue();
                    o.WriteBps = d.Write.NextValue();
                    o.ResponseMs = d.Resp.NextValue() * 1000.0;
                }
                catch { disksDirty = true; }
                d.Activity.Add(o.Active);
                d.Transfer.Add((float)(o.ReadBps + o.WriteBps));
                o.ActivityHistory = d.Activity.Snapshot();
                o.TransferHistory = d.Transfer.Snapshot();
                foreach (var l in (dm.Letters ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var di = new DriveInfo(l);
                        if (di.IsReady) { o.VolumeTotal += (ulong)di.TotalSize; o.VolumeFree += (ulong)di.TotalFreeSpace; }
                    }
                    catch { }
                }
                s.Disks.Add(o);
            }

            Mark("discos");
            // ---- Red
            if (netsDirty) RebuildNets();
            foreach (var kv in nets)
            {
                var n = kv.Value;
                var o = new NetMetric { Id = kv.Key, Name = n.Nic.Name, Description = n.Nic.Description, Ssid = n.Ssid };
                o.Kind = n.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                try
                {
                    var st = n.Nic.GetIPStatistics();
                    long sent = st.BytesSent, recv = st.BytesReceived;
                    if (n.LastSent > 0) { o.SendBps = Math.Max(0, sent - n.LastSent); o.RecvBps = Math.Max(0, recv - n.LastRecv); }
                    n.LastSent = sent; n.LastRecv = recv;
                    o.LinkSpeed = n.Nic.Speed;
                    var ip = n.Nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ip != null) o.Ipv4 = ip.Address.ToString();
                }
                catch { netsDirty = true; }
                n.Send.Add((float)o.SendBps);
                n.Recv.Add((float)o.RecvBps);
                o.SendHistory = n.Send.Snapshot();
                o.RecvHistory = n.Recv.Snapshot();
                s.Nets.Add(o);
            }

            Mark("red");
            // ---- Procesos (cada 2 s)
            var prev = Latest;
            if ((DateTime.UtcNow - lastProcSample).TotalMilliseconds >= 1900 || prev == null)
            {
                SampleProcesses(s);
                lastProcSample = DateTime.UtcNow;
            }
            else
            {
                s.Procs = prev.Procs;
                s.ProcessCount = prev.ProcessCount;
            }

            Mark("procesos");
            CheckAlerts(s);
            Mark("alertas");
            Latest = s;
            var sampled = Sampled;
            if (sampled != null) try { sampled(s); } catch { }
        }

        // ------------------------------------------------------------------ procesos

        void SampleProcesses(Snapshot s)
        {
            double elapsed = lastProcSample == DateTime.MinValue ? 1 : (DateTime.UtcNow - lastProcSample).TotalSeconds;
            HashSet<int> apps;
            try { apps = Native.PidsWithWindows(); } catch { apps = new HashSet<int>(); }
            int mySession = Process.GetCurrentProcess().SessionId;
            var seen = new Dictionary<long, ProcState>();
            Process[] list = Process.GetProcesses();
            foreach (var p in list)
            {
                using (p)
                {
                    int pid;
                    string name;
                    try { pid = p.Id; name = p.ProcessName; } catch { continue; }
                    if (pid == 0) continue;

                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    ulong creation = 0, cpuTime = 0, io = 0, mem = 0;
                    string path = null;
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            Native.FILETIME c, e, k, u;
                            if (Native.GetProcessTimes(h, out c, out e, out k, out u)) { creation = c.Value; cpuTime = k.Value + u.Value; }
                            Native.IO_COUNTERS ioc;
                            if (Native.GetProcessIoCounters(h, out ioc)) io = ioc.ReadTransferCount + ioc.WriteTransferCount;
                            Native.PROCESS_MEMORY_COUNTERS_EX mc;
                            if (Native.GetProcessMemoryInfo(h, out mc, (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS_EX))))
                                mem = mc.PrivateUsage.ToUInt64();
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    long key = ((long)pid << 32) ^ (long)(creation & 0xFFFFFFFF);
                    ProcState ps;
                    if (!procs.TryGetValue(key, out ps))
                    {
                        ps = new ProcState();
                        ps.Fresh = true;
                        ps.CreationTime = creation;
                        ps.CpuTime = cpuTime;
                        ps.IoBytes = io;
                        ps.M.Pid = pid;
                        ps.M.Name = name;
                        ps.M.Started = creation > 0 ? DateTime.FromFileTime((long)creation) : DateTime.Now;
                        path = ImagePath(pid);
                        ps.M.Path = path;
                        if (path != null)
                        {
                            var info = FileInfoFor(path);
                            ps.M.Description = info.Item1;
                            ps.M.Icon = info.Item2;
                        }
                    }
                    else ps.Fresh = false;

                    var m = new ProcMetric
                    {
                        Pid = pid, Name = name, Description = ps.M.Description, Path = ps.M.Path, Icon = ps.M.Icon,
                        Started = ps.M.Started, Memory = mem, IsApp = apps.Contains(pid), Gpu = GpuOf(pid)
                    };
                    if (!ps.Fresh || procBaseline)
                    {
                        m.Cpu = Clamp((float)((cpuTime - Math.Min(cpuTime, ps.CpuTime)) / (elapsed * 1e7 * Environment.ProcessorCount) * 100.0));
                        m.DiskBps = (io - Math.Min(io, ps.IoBytes)) / elapsed;
                    }
                    ps.CpuTime = cpuTime;
                    ps.IoBytes = io;
                    ps.M = m;
                    seen[key] = ps;
                    s.Procs.Add(m);

                    if (ps.Fresh && procBaseline && ProcessStarted != null) try { if (InSession(p, mySession)) ProcessStarted(m); } catch { }
                }
            }

            if (procBaseline && ProcessExited != null)
                foreach (var kv in procs)
                    if (!seen.ContainsKey(kv.Key))
                        try { ProcessExited(kv.Value.M, DateTime.Now - kv.Value.M.Started); } catch { }

            procs = seen;
            procBaseline = true;
            s.ProcessCount = s.Procs.Count;

        }

        float GpuOf(int pid) { float v; return gpuPerPid.TryGetValue(pid, out v) ? v : 0; }

        static bool InSession(Process p, int session)
        {
            try { return p.SessionId == session; } catch { return false; }
        }

        static string ImagePath(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(h); }
        }

        Tuple<string, Bitmap> FileInfoFor(string path)
        {
            Tuple<string, Bitmap> r;
            if (fileInfoCache.TryGetValue(path, out r)) return r;
            string desc = null;
            Bitmap bmp = null;
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            if (desc != null) desc = desc.Trim();
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(path))
                    if (ic != null) bmp = ic.ToBitmap();
            }
            catch { }
            r = Tuple.Create(string.IsNullOrEmpty(desc) ? null : desc, bmp);
            fileInfoCache[path] = r;
            return r;
        }

        public static bool EndProcess(int pid, out string error)
        {
            error = null;
            IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE, false, pid);
            if (h == IntPtr.Zero) { error = new System.ComponentModel.Win32Exception().Message; return false; }
            try
            {
                if (Native.TerminateProcess(h, 1)) return true;
                error = new System.ComponentModel.Win32Exception().Message;
                return false;
            }
            finally { Native.CloseHandle(h); }
        }

        // ------------------------------------------------------------------ discos

        void RebuildDisks()
        {
            disksDirty = false;
            foreach (var d in disks) { DisposeCounter(d.Idle); DisposeCounter(d.Read); DisposeCounter(d.Write); DisposeCounter(d.Resp); }
            var old = disks.ToDictionary(d => d.Meta.Instance, d => d);
            var list = new List<DiskState>();
            var meta = ReadDiskMeta();
            string[] instances;
            try { instances = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames(); }
            catch { instances = new string[0]; }
            foreach (var inst in instances.Where(i => i != "_Total").OrderBy(i => i, StringComparer.Ordinal))
            {
                int sp = inst.IndexOf(' ');
                int num;
                if (!int.TryParse(sp > 0 ? inst.Substring(0, sp) : inst, out num)) continue;
                var ds = new DiskState();
                DiskState prev;
                if (old.TryGetValue(inst, out prev)) { ds.Activity = prev.Activity; ds.Transfer = prev.Transfer; }
                var dm = new DiskMetric { Number = num, Instance = inst };
                dm.Letters = sp > 0 ? string.Join(", ", inst.Substring(sp + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) : "";
                Tuple<string, string, ulong> info;
                if (meta.TryGetValue(num, out info)) { dm.Name = info.Item1; dm.Kind = info.Item2; dm.Capacity = info.Item3; }
                else { dm.Name = "Disco " + num; dm.Kind = ""; }
                ds.Meta = dm;
                try
                {
                    ds.Idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst, true);
                    ds.Read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true);
                    ds.Write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true);
                    ds.Resp = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", inst, true);
                    ds.Idle.NextValue(); ds.Read.NextValue(); ds.Write.NextValue(); ds.Resp.NextValue();
                }
                catch { continue; }
                list.Add(ds);
            }
            disks = list;
        }

        static void DisposeCounter(PerformanceCounter c) { if (c != null) try { c.Dispose(); } catch { } }

        /// <summary>Número de disco → (modelo, tipo "SSD"/"HDD"/"USB"/…, capacidad).</summary>
        static Dictionary<int, Tuple<string, string, ulong>> ReadDiskMeta()
        {
            var r = new Dictionary<int, Tuple<string, string, ulong>>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject mo in q.Get())
                        using (mo)
                        {
                            int n;
                            if (!int.TryParse(Convert.ToString(mo["DeviceId"]), out n)) continue;
                            int media = mo["MediaType"] == null ? 0 : Convert.ToInt32(mo["MediaType"]);
                            int bus = mo["BusType"] == null ? 0 : Convert.ToInt32(mo["BusType"]);
                            string kind = bus == 7 ? "USB" : bus == 12 || bus == 13 ? "SD" : media == 4 ? "SSD" : media == 3 ? "HDD" : bus == 17 ? "SSD" : "";
                            if (bus == 17) kind = "SSD (NVMe)";
                            r[n] = Tuple.Create(((mo["FriendlyName"] as string) ?? ("Disco " + n)).Trim(), kind,
                                mo["Size"] == null ? 0UL : Convert.ToUInt64(mo["Size"]));
                        }
            }
            catch { }
            return r;
        }

        // ------------------------------------------------------------------ red

        void RebuildNets()
        {
            netsDirty = false;
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return; }
            var keep = new HashSet<string>();
            foreach (var n in all)
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                string d = (n.Description ?? "").ToLowerInvariant();
                if (d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("bluetooth") ||
                    d.Contains("loopback") || d.Contains("wan miniport") || d.Contains("vpn") || d.Contains("tap-") ||
                    d.Contains("tunnel") || d.Contains("tailscale") || d.Contains("wireguard") || d.Contains("zerotier") || d.Contains("pseudo")) continue;
                keep.Add(n.Id);
                NetState st;
                if (!nets.TryGetValue(n.Id, out st)) { st = new NetState(); nets[n.Id] = st; }
                st.Nic = n;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) st.Ssid = CurrentSsid();
            }
            foreach (var k in nets.Keys.Where(k => !keep.Contains(k)).ToList()) nets.Remove(k);
        }

        /// <summary>Nombre de la red Wi-Fi actual (lee "netsh wlan show interfaces").</summary>
        public static string CurrentSsid()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in output.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("SSID de BSS", StringComparison.OrdinalIgnoreCase)
                            && !t.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                        {
                            int i = t.IndexOf(':');
                            if (i > 0) return t.Substring(i + 1).Trim();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ alertas

        void CheckAlerts(Snapshot s)
        {
            if (Alert == null) return;
            int cpuT = Settings.CpuThreshold, cpuSec = Math.Max(1, Settings.CpuSeconds), memT = Settings.MemThreshold;
            if (s.Cpu >= cpuT) { cpuHighCount++; cpuLowCount = 0; } else if (s.Cpu < cpuT - 20) { cpuLowCount++; cpuHighCount = 0; }
            if (!cpuAlerted && cpuHighCount >= cpuSec)
            {
                cpuAlerted = true;
                var top = s.Procs.OrderByDescending(p => p.Cpu).FirstOrDefault();
                Alert("CPU por encima del " + cpuT + " % durante " + cpuSec + " segundos" + (top != null && top.Cpu > 10 ? " (más uso: " + top.Display + ", " + top.Cpu.ToString("0") + " %)" : ""));
            }
            if (cpuAlerted && cpuLowCount >= 10) cpuAlerted = false;

            if (s.MemPercent >= memT) { memHighCount++; memLowCount = 0; } else if (s.MemPercent < memT - 10) { memLowCount++; memHighCount = 0; }
            if (!memAlerted && memHighCount >= 10)
            {
                memAlerted = true;
                var top = s.Procs.OrderByDescending(p => p.Memory).FirstOrDefault();
                Alert("Memoria por encima del " + memT + " %" + (top != null ? " (más uso: " + top.Display + ", " + Format.Bytes(top.Memory) + ")" : ""));
            }
            if (memAlerted && memLowCount >= 10) memAlerted = false;

            int gpuT = Settings.GpuThreshold;
            if (s.Gpu >= gpuT) { gpuHighCount++; gpuLowCount = 0; } else if (s.Gpu < gpuT - 20) { gpuLowCount++; gpuHighCount = 0; }
            if (!gpuAlerted && gpuHighCount >= 15)
            {
                gpuAlerted = true;
                var top = s.Procs.OrderByDescending(p => p.Gpu).FirstOrDefault();
                Alert("GPU por encima del " + gpuT + " % durante 15 segundos" + (top != null && top.Gpu > 10 ? " (más uso: " + top.Display + ")" : ""));
            }
            if (gpuAlerted && gpuLowCount >= 10) gpuAlerted = false;

            int tempT = Settings.TempThreshold;
            if (!double.IsNaN(s.Temperature))
            {
                if (!tempAlerted && s.Temperature >= tempT) { tempAlerted = true; Alert("Temperatura alta del CPU: " + s.Temperature.ToString("0") + " °C"); }
                else if (tempAlerted && s.Temperature < tempT - 8) tempAlerted = false;
            }

            if (s.HasBattery)
            {
                int low = Settings.BatteryLow;
                if (!batteryAlerted && !s.OnAc && s.BatteryPercent <= low) { batteryAlerted = true; Alert("Batería baja: " + s.BatteryPercent.ToString("0") + " %"); }
                else if (batteryAlerted && (s.OnAc || s.BatteryPercent > low + 5)) batteryAlerted = false;
            }

            if (tick % 30 == 1)
            {
                double minFree = Settings.DiskFreePercent / 100.0;
                foreach (var di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!di.IsReady || di.DriveType != DriveType.Fixed || di.TotalSize == 0) continue;
                        double free = (double)di.TotalFreeSpace / di.TotalSize;
                        string key = di.Name;
                        if (free < minFree && lowSpaceAlerted.Add(key))
                            Alert("Poco espacio en " + di.Name.TrimEnd('\\') + ": quedan " + Format.Bytes((ulong)di.TotalFreeSpace) + " libres (" + (free * 100).ToString("0") + " %)");
                        else if (free > minFree + 0.05) lowSpaceAlerted.Remove(key);
                    }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------ datos fijos

        static string ReadCpuName(out double mhz)
        {
            mhz = 0;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    object v = k.GetValue("~MHz");
                    if (v is int) mhz = (int)v;
                    return ((k.GetValue("ProcessorNameString") as string) ?? "Procesador").Trim();
                }
            }
            catch { return "Procesador"; }
        }

        static int ReadCoreCount()
        {
            try
            {
                int n = 0;
                using (var q = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                    foreach (ManagementObject mo in q.Get()) using (mo) n += Convert.ToInt32(mo["NumberOfCores"]);
                return n;
            }
            catch { return 0; }
        }

        static float Clamp(float v) { return v < 0 ? 0 : v > 100 ? 100 : v; }

        public void Dispose()
        {
            timer.Dispose();
        }
    }

    /// <summary>Formatos de números en español.</summary>
    internal static class Format
    {
        public static readonly CultureInfo Es = new CultureInfo("es-ES");

        public static string Bytes(ulong b)
        {
            double v = b;
            if (v >= 1099511627776) return (v / 1099511627776).ToString("0.0", Es) + " TB";
            if (v >= 1073741824) return (v / 1073741824).ToString(v >= 107374182400 ? "0" : "0.0", Es) + " GB";
            if (v >= 1048576) return (v / 1048576).ToString("0", Es) + " MB";
            if (v >= 1024) return (v / 1024).ToString("0", Es) + " KB";
            return b + " B";
        }

        public static string Rate(double bytesPerSec)
        {
            if (bytesPerSec >= 1073741824) return (bytesPerSec / 1073741824).ToString("0.0", Es) + " GB/s";
            if (bytesPerSec >= 1048576) return (bytesPerSec / 1048576).ToString("0.0", Es) + " MB/s";
            if (bytesPerSec >= 1024) return (bytesPerSec / 1024).ToString("0", Es) + " KB/s";
            return bytesPerSec.ToString("0", Es) + " B/s";
        }

        /// <summary>Velocidad de red en bits, como el Administrador de tareas.</summary>
        public static string Bits(double bytesPerSec)
        {
            double b = bytesPerSec * 8;
            if (b >= 1e9) return (b / 1e9).ToString("0.0", Es) + " Gbps";
            if (b >= 1e6) return (b / 1e6).ToString("0.0", Es) + " Mbps";
            if (b >= 1e3) return (b / 1e3).ToString("0", Es) + " Kbps";
            return b.ToString("0", Es) + " bps";
        }

        public static string Percent(float v) { return v.ToString(v < 10 ? "0.0" : "0", Es) + " %"; }

        public static string Ghz(double mhz) { return (mhz / 1000).ToString("0.00", Es) + " GHz"; }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalDays >= 1) return (int)t.TotalDays + " d " + t.Hours + " h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " min";
            return Math.Max(0, (int)t.TotalSeconds) + " s";
        }

        public static string Uptime(TimeSpan t)
        {
            return string.Format("{0}:{1:00}:{2:00}:{3:00}", (int)t.TotalDays, t.Hours, t.Minutes, t.Seconds);
        }
    }
}
