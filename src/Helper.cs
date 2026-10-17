using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysMonitor
{
    /// <summary>
    /// Ayudante con privilegios (Monitor.exe --helper). Corre elevado mediante una tarea programada y atiende a la
    /// ventana por un canal con nombre que solo acepta conexiones del propio Monitor.exe.
    /// </summary>
    internal static class HelperService
    {
        public const string PipeName = "SysMonitor.Helper";
        public const string TaskName = "SysMonitorHelper";
        public const string Protocol = "1";

        static readonly Guid KernelProcess = new Guid("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
        static readonly Guid KernelNetwork = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
        static readonly Guid KernelFile = new Guid("EDD08927-9CC4-4E65-B970-C2560FB5C289");

        static readonly string instance = Guid.NewGuid().ToString("N");
        static readonly object gate = new object();
        static bool quit;

        // ---------------------------------------------------------------- eventos para la ventana

        sealed class Ev { public long Seq; public DateTime Time; public Category Cat; public Severity Sev; public string Msg; }
        static readonly List<Ev> events = new List<Ev>();
        static long seq;

        static void Emit(Category c, Severity s, string msg)
        {
            lock (events)
            {
                events.Add(new Ev { Seq = ++seq, Time = DateTime.Now, Cat = c, Sev = s, Msg = msg });
                if (events.Count > 5000) events.RemoveRange(0, events.Count - 5000);
            }
        }

        // ---------------------------------------------------------------- arranque

        public static int Run()
        {
            if (!IsAdmin() || !EtwSession.Supported) return 2;
            bool created;
            using (var mutex = new Mutex(true, @"Local\SysMonitor.Helper", out created))
            {
                if (!created) return 0;
                Emit(Category.Monitor, Severity.Info, "Funciones de administrador activas");
                StartProcessAndNetwork();
                StartSecurityLog();
                new Thread(SmartLoop) { IsBackground = true }.Start();
                new Thread(RateLoop) { IsBackground = true }.Start();
                for (int i = 0; i < 3; i++) new Thread(PipeLoop) { IsBackground = true }.Start();
                while (!quit) Thread.Sleep(500);
                StopFiles();
                if (mainSession != null) mainSession.Dispose();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; }
        }

        // ---------------------------------------------------------------- procesos y red (ETW)

        static EtwSession mainSession;
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();

        static void StartProcessAndNetwork()
        {
            foreach (var p in Process.GetProcesses()) using (p) try { names[p.Id] = p.ProcessName + ".exe"; } catch { }
            try
            {
                mainSession = new EtwSession("SysMonitor-Main", OnMainEvent);
                mainSession.Start();
                mainSession.Enable(KernelProcess, 0x10, 4);          // WINEVENT_KEYWORD_PROCESS
                mainSession.Enable(KernelNetwork, 0x10 | 0x20, 4);   // IPv4 e IPv6
            }
            catch (Exception ex) { Emit(Category.Monitor, Severity.Error, "No se pudo iniciar la captura de eventos del sistema: " + ex.Message); }
        }

        static void OnMainEvent(IntPtr rec)
        {
            var prov = EtwSession.Provider(rec);
            if (prov == KernelNetwork) OnNetwork(rec);
            else if (prov == KernelProcess) OnProcess(rec);
        }

        // Ejecutables que suelen usarse para atacar ("living off the land") o para ocultar comandos.
        static readonly HashSet<string> Lolbins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mshta.exe", "rundll32.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "certutil.exe", "bitsadmin.exe",
            "msbuild.exe", "installutil.exe", "wmic.exe", "cmstp.exe", "odbcconf.exe", "regasm.exe", "regsvcs.exe", "forfiles.exe"
        };
        static readonly HashSet<string> Office = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "msaccess.exe", "onenote.exe", "acrord32.exe", "acrobat.exe"
        };
        static readonly HashSet<string> Quiet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conhost.exe", "svchost.exe", "backgroundTaskHost.exe", "RuntimeBroker.exe", "dllhost.exe", "WmiPrvSE.exe",
            "SearchProtocolHost.exe", "SearchFilterHost.exe", "taskhostw.exe", "smartscreen.exe", "audiodg.exe", "ctfmon.exe",
            "sppsvc.exe", "MoUsoCoreWorker.exe", "CompPkgSrv.exe", "WerFault.exe", "wermgr.exe", "SgrmBroker.exe",
            "msedgewebview2.exe", "crashpad_handler.exe", "chrome_crashpad_handler.exe", "TiWorker.exe", "UsoClient.exe"
        };
        static readonly HashSet<string> OurApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Monitor.exe", "WriteBlocker.exe" };

        static void OnProcess(IntPtr rec)
        {
            int id = EtwSession.Id(rec);
            IntPtr d = EtwSession.Data(rec);
            int len = EtwSession.DataLength(rec);
            if (id == 2) return; // los cierres ya los registra la ventana
            if (id != 1 || len < 24) return;
            int pid = Marshal.ReadInt32(d, 0);
            int ppid, nameAt;
            if (EtwSession.Version(rec) >= 3)
            {
                // v3: ProcessID, ProcessSequenceNumber(8), CreateTime(8), ParentProcessID, ParentProcessSequenceNumber(8),
                // SessionID, Flags, ProcessTokenElevationType, ProcessTokenIsElevated, MandatoryLabel (SID), ImageName…
                ppid = Marshal.ReadInt32(d, 20);
                int sidAt = 48;
                nameAt = len > sidAt + 8 ? sidAt + 8 + 4 * Marshal.ReadByte(d, sidAt + 1) : len;
            }
            else
            {
                // v0–v2: ProcessID, CreateTime(8), ParentProcessID, SessionID, [Flags], ImageName…
                ppid = Marshal.ReadInt32(d, 12);
                nameAt = EtwSession.Version(rec) >= 1 ? 24 : 20;
            }
            string image = EtwSession.String(d, nameAt, len);
            string exe = Path.GetFileName(image);
            if (string.IsNullOrEmpty(exe) || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) exe = ImageName(pid);
            if (string.IsNullOrEmpty(exe)) return;
            string parent;
            lock (names) { names.TryGetValue(ppid, out parent); names[pid] = exe; }
            parent = parent ?? ("PID " + ppid);
            if (OurApps.Contains(parent) || OurApps.Contains(exe) || Quiet.Contains(exe)) return;

            string cmd = CommandLine(pid);
            string lower = (cmd ?? "").ToLowerInvariant();
            bool suspicious = Lolbins.Contains(exe) ||
                (Office.Contains(parent) && (exe.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) || exe.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))) ||
                ((exe.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) || exe.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) &&
                 (lower.Contains(" -enc") || lower.Contains(" -e ") || lower.Contains("-encodedcommand") || lower.Contains("-w hidden") || lower.Contains("-windowstyle hidden") || lower.Contains("downloadstring") || lower.Contains("iex")));
            string msg = "Se ejecutó " + exe + " (PID " + pid + ") desde " + parent +
                         (cmd != null ? ": " + Trim(cmd, 400) : " (terminó antes de leer su línea de comandos)");
            if (suspicious) msg = "Ejecución sospechosa: " + msg;
            Emit(suspicious ? Category.Alerta : Category.Programa, suspicious ? Severity.Advertencia : Severity.Info, msg);
        }

        static string ImageName(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : null;
            }
            finally { Native.CloseHandle(h); }
        }

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr info, int len, out int ret);

        /// <summary>Línea de comandos de un proceso (ProcessCommandLineInformation, Windows 8.1+).</summary>
        static string CommandLine(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = 4096, ret;
                buf = Marshal.AllocHGlobal(size);
                int st = NtQueryInformationProcess(h, 60, buf, size, out ret);
                if (st == unchecked((int)0xC0000004) && ret > 0) // STATUS_INFO_LENGTH_MISMATCH
                {
                    Marshal.FreeHGlobal(buf);
                    size = ret; buf = Marshal.AllocHGlobal(size);
                    st = NtQueryInformationProcess(h, 60, buf, size, out ret);
                }
                if (st != 0) return null;
                int chars = (ushort)Marshal.ReadInt16(buf, 0) / 2;
                IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size == 8 ? 8 : 4);
                return chars > 0 ? Marshal.PtrToStringUni(text, chars) : null;
            }
            catch { return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); Native.CloseHandle(h); }
        }

        // ---- Red por programa: tamaño de cada envío y recepción TCP/UDP
        sealed class Traffic { public long Sent, Recv, SentTotal, RecvTotal; public double SentBps, RecvBps; }
        static readonly Dictionary<int, Traffic> byPid = new Dictionary<int, Traffic>();
        static readonly Dictionary<string, Traffic> byConn = new Dictionary<string, Traffic>();

        static void OnNetwork(IntPtr rec)
        {
            int id = EtwSession.Id(rec);
            bool send, v6;
            string proto;
            switch (id)
            {
                case 10: send = true; v6 = false; proto = "TCP"; break;
                case 11: send = false; v6 = false; proto = "TCP"; break;
                case 26: send = true; v6 = true; proto = "TCPv6"; break;
                case 27: send = false; v6 = true; proto = "TCPv6"; break;
                case 42: send = true; v6 = false; proto = "UDP"; break;
                case 43: send = false; v6 = false; proto = "UDP"; break;
                case 58: send = true; v6 = true; proto = "UDPv6"; break;
                case 59: send = false; v6 = true; proto = "UDPv6"; break;
                default: return;
            }
            IntPtr d = EtwSession.Data(rec);
            int len = EtwSession.DataLength(rec);
            if (len < (v6 ? 44 : 20)) return;
            int pid = Marshal.ReadInt32(d, 0);
            int size = Marshal.ReadInt32(d, 4);
            string remote;
            int rport;
            if (v6)
            {
                var a = new byte[16]; Marshal.Copy(d + 8, a, 0, 16);
                remote = new System.Net.IPAddress(a).ToString();
                rport = Swap((ushort)Marshal.ReadInt16(d, 40));
            }
            else
            {
                remote = new System.Net.IPAddress((uint)Marshal.ReadInt32(d, 8)).ToString();
                rport = Swap((ushort)Marshal.ReadInt16(d, 16));
            }
            string ck = pid + "|" + proto + "|" + remote + "|" + rport;
            lock (gate)
            {
                Traffic t;
                if (!byPid.TryGetValue(pid, out t)) byPid[pid] = t = new Traffic();
                Traffic c;
                if (!byConn.TryGetValue(ck, out c)) byConn[ck] = c = new Traffic();
                if (send) { t.Sent += size; t.SentTotal += size; c.Sent += size; c.SentTotal += size; }
                else { t.Recv += size; t.RecvTotal += size; c.Recv += size; c.RecvTotal += size; }
            }
        }

        static int Swap(ushort v) { return ((v & 0xFF) << 8) | (v >> 8); }

        /// <summary>Cada segundo convierte lo acumulado en velocidades y limpia lo inactivo.</summary>
        static void RateLoop()
        {
            var sw = Stopwatch.StartNew();
            while (!quit)
            {
                Thread.Sleep(1000);
                double secs = Math.Max(0.5, sw.Elapsed.TotalSeconds);
                sw.Restart();
                // La captura de archivos es costosa: se apaga sola si la ventana deja de pedirla.
                if (fileSession != null && (DateTime.UtcNow - fileRequested).TotalSeconds > 15) StopFiles();
                lock (gate)
                {
                    foreach (var t in byPid.Values.Concat(byConn.Values)) { t.SentBps = t.Sent / secs; t.RecvBps = t.Recv / secs; t.Sent = t.Recv = 0; }
                    foreach (var f in files.Values) { f.ReadBps = f.Read / secs; f.WriteBps = f.Write / secs; f.Read = f.Write = 0; }
                    if (byConn.Count > 4000) foreach (var k in byConn.Where(x => x.Value.SentBps == 0 && x.Value.RecvBps == 0).Select(x => x.Key).ToList()) byConn.Remove(k);
                    if (files.Count > 20000) foreach (var k in files.Where(x => x.Value.ReadBps == 0 && x.Value.WriteBps == 0).Select(x => x.Key).ToList()) files.Remove(k);
                }
            }
        }

        // ---------------------------------------------------------------- archivos por proceso (bajo demanda)

        sealed class FileIo { public int Pid; public string Path; public long Read, Write, ReadTotal, WriteTotal; public double ReadBps, WriteBps; }
        static readonly Dictionary<string, FileIo> files = new Dictionary<string, FileIo>();
        static readonly Dictionary<long, string> fileObjects = new Dictionary<long, string>();
        static readonly Dictionary<long, string> fileKeys = new Dictionary<long, string>();
        static EtwSession fileSession;
        static DateTime fileRequested;

        static void StartFiles()
        {
            fileRequested = DateTime.UtcNow;
            if (fileSession != null) return;
            try
            {
                fileSession = new EtwSession("SysMonitor-Files", OnFileEvent);
                fileSession.Start();
                // FILENAME | FILEIO | CREATE | READ | WRITE
                fileSession.Enable(KernelFile, 0x10 | 0x20 | 0x80 | 0x100 | 0x200, 4);
            }
            catch (Exception ex) { fileSession = null; Emit(Category.Monitor, Severity.Error, "No se pudo iniciar la captura de archivos: " + ex.Message); }
        }

        static void StopFiles()
        {
            if (fileSession == null) return;
            fileSession.Dispose();
            fileSession = null;
            lock (gate) { files.Clear(); fileObjects.Clear(); fileKeys.Clear(); }
        }

        static void OnFileEvent(IntPtr rec)
        {
            int id = EtwSession.Id(rec);
            IntPtr d = EtwSession.Data(rec);
            int len = EtwSession.DataLength(rec);
            switch (id)
            {
                case 12: // Create: Irp, FileObject, IssuingThreadId, CreateOptions, CreateAttributes, ShareAccess, FileName
                    if (len < 34) return;
                    lock (gate) fileObjects[Marshal.ReadInt64(d, 8)] = DosPath(EtwSession.String(d, 32, len));
                    break;
                case 10: // NameCreate: FileKey, FileName
                case 32: // NameRundown
                    if (len < 10) return;
                    lock (gate) fileKeys[Marshal.ReadInt64(d, 0)] = DosPath(EtwSession.String(d, 8, len));
                    break;
                case 14: // Close: Irp, FileObject, FileKey
                    if (len < 24) return;
                    lock (gate) fileObjects.Remove(Marshal.ReadInt64(d, 8));
                    break;
                case 15: // Read: ByteOffset, Irp, FileObject, FileKey, IssuingThreadId, IOSize
                case 16: // Write
                    if (len < 40) return;
                    long obj = Marshal.ReadInt64(d, 16), key = Marshal.ReadInt64(d, 24);
                    int size = Marshal.ReadInt32(d, 36);
                    int pid = EtwSession.ProcessId(rec);
                    lock (gate)
                    {
                        string path;
                        if (!fileObjects.TryGetValue(obj, out path) && !fileKeys.TryGetValue(key, out path)) path = "(archivo abierto antes de empezar a medir)";
                        string k = pid + "|" + path;
                        FileIo f;
                        if (!files.TryGetValue(k, out f)) files[k] = f = new FileIo { Pid = pid, Path = path };
                        if (id == 15) { f.Read += size; f.ReadTotal += size; } else { f.Write += size; f.WriteTotal += size; }
                    }
                    break;
            }
        }

        // \Device\HarddiskVolume3\... → C:\...
        static Dictionary<string, string> devices;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int QueryDosDevice(string name, StringBuilder target, int max);

        static string DosPath(string nt)
        {
            if (string.IsNullOrEmpty(nt) || !nt.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return nt;
            if (devices == null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dr in Environment.GetLogicalDrives())
                {
                    var sb = new StringBuilder(260);
                    string letter = dr.TrimEnd('\\');
                    if (QueryDosDevice(letter, sb, sb.Capacity) > 0) map[sb.ToString()] = letter;
                }
                devices = map;
            }
            foreach (var kv in devices)
                if (nt.StartsWith(kv.Key + @"\", StringComparison.OrdinalIgnoreCase)) return kv.Value + nt.Substring(kv.Key.Length);
            return nt;
        }

        // ---------------------------------------------------------------- registro de Seguridad

        static EventLog security;
        static readonly Queue<DateTime> failedLogons = new Queue<DateTime>();
        static DateTime lastBruteAlert = DateTime.MinValue;

        static void StartSecurityLog()
        {
            try
            {
                security = new EventLog("Security");
                security.EntryWritten += (s, e) => { try { OnSecurity(e.Entry); } catch { } };
                security.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Emit(Category.Monitor, Severity.Error, "No se pudo leer el registro de Seguridad: " + ex.Message); }
        }

        static string LogonType(string t)
        {
            switch (t)
            {
                case "2": return "local";
                case "3": return "por red";
                case "4": return "tarea programada";
                case "5": return "servicio";
                case "7": return "desbloqueo";
                case "8": return "por red (texto claro)";
                case "9": return "credenciales nuevas";
                case "10": return "escritorio remoto";
                case "11": return "credenciales guardadas";
                default: return "tipo " + t;
            }
        }

        static bool SystemAccount(string user)
        {
            if (string.IsNullOrEmpty(user) || user == "-") return true;
            string u = user.ToUpperInvariant();
            return u.EndsWith("$") || u == "SYSTEM" || u == "SISTEMA" || u.StartsWith("DWM-") || u.StartsWith("UMFD-") ||
                   u == "LOCAL SERVICE" || u == "NETWORK SERVICE" || u == "ANONYMOUS LOGON";
        }

        static string Arg(string[] a, int i) { return a != null && i < a.Length ? a[i] : ""; }

        static void OnSecurity(EventLogEntry en)
        {
            int id = (int)(en.InstanceId & 0xFFFF);
            var a = en.ReplacementStrings;
            switch (id)
            {
                case 4624:
                {
                    string user = Arg(a, 5), type = Arg(a, 8), ip = Arg(a, 18);
                    if (SystemAccount(user)) return;
                    bool remoteIp = ip != "" && ip != "-" && ip != "::1" && ip != "127.0.0.1";
                    if (type != "2" && type != "7" && type != "10" && type != "11" && !(type == "3" && remoteIp)) return;
                    Emit(Category.Seguridad, type == "10" || type == "3" ? Severity.Advertencia : Severity.Info,
                        "Inicio de sesión de " + Arg(a, 6) + "\\" + user + " (" + LogonType(type) + (remoteIp ? ", desde " + ip : "") + ")");
                    break;
                }
                case 4625:
                {
                    string user = Arg(a, 5), ip = Arg(a, 19);
                    Emit(Category.Seguridad, Severity.Advertencia, "Inicio de sesión fallido para " + (user == "" ? "un usuario desconocido" : Arg(a, 6) + "\\" + user) +
                        " (" + LogonType(Arg(a, 10)) + (ip != "" && ip != "-" ? ", desde " + ip : "") + ")");
                    lock (failedLogons)
                    {
                        failedLogons.Enqueue(DateTime.Now);
                        while (failedLogons.Count > 0 && (DateTime.Now - failedLogons.Peek()).TotalMinutes > 5) failedLogons.Dequeue();
                        if (failedLogons.Count >= 5 && (DateTime.Now - lastBruteAlert).TotalMinutes > 10)
                        {
                            lastBruteAlert = DateTime.Now;
                            Emit(Category.Alerta, Severity.Advertencia, failedLogons.Count + " inicios de sesión fallidos en 5 minutos: posible intento de adivinar la contraseña");
                        }
                    }
                    break;
                }
                case 4720: Emit(Category.Seguridad, Severity.Advertencia, "Se creó la cuenta de usuario " + Arg(a, 0) + " (por " + Arg(a, 4) + ")"); break;
                case 4726: Emit(Category.Seguridad, Severity.Advertencia, "Se eliminó la cuenta de usuario " + Arg(a, 0) + " (por " + Arg(a, 4) + ")"); break;
                case 4740: Emit(Category.Seguridad, Severity.Advertencia, "Se bloqueó la cuenta " + Arg(a, 0) + " por intentos fallidos"); break;
                case 4728:
                case 4732:
                case 4756:
                {
                    string group = Arg(a, 2), member = Arg(a, 0);
                    if (member == "-" || member == "") member = Arg(a, 1);
                    bool admins = group.IndexOf("admin", StringComparison.OrdinalIgnoreCase) >= 0;
                    Emit(admins ? Category.Alerta : Category.Seguridad, admins ? Severity.Advertencia : Severity.Info,
                        "Se agregó " + member + " al grupo " + group + " (por " + Arg(a, 6) + ")");
                    break;
                }
                case 4698: Emit(Category.Seguridad, Severity.Advertencia, "Se creó la tarea programada " + Arg(a, 4) + " (por " + Arg(a, 1) + ")"); break;
                case 1102: Emit(Category.Alerta, Severity.Error, "Se borró el registro de Seguridad de Windows (por " + Arg(a, 1) + ")"); break;
            }
        }

        // ---------------------------------------------------------------- SMART

        static string smart = "";

        static void SmartLoop()
        {
            while (!quit)
            {
                try { smart = ReadSmart(); } catch { }
                for (int i = 0; i < 120 && !quit; i++) Thread.Sleep(500);
            }
        }

        static string ReadSmart()
        {
            var sb = new StringBuilder();
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ObjectId, DeviceId, HealthStatus FROM MSFT_PhysicalDisk")))
                foreach (ManagementObject pd in q.Get())
                    using (pd)
                    {
                        string id = Convert.ToString(pd["DeviceId"]);
                        int health = pd["HealthStatus"] == null ? 5 : Convert.ToInt32(pd["HealthStatus"]);
                        string oid = Convert.ToString(pd["ObjectId"]).Replace("\\", "\\\\").Replace("\"", "\\\"");
                        string temp = "", tmax = "", wear = "", hours = "", rerr = "", werr = "", cycles = "";
                        try
                        {
                            using (var rq = new ManagementObjectSearcher(scope, new ObjectQuery(
                                "ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId=\"" + oid + "\"} WHERE ResultClass = MSFT_StorageReliabilityCounter")))
                                foreach (ManagementObject rc in rq.Get())
                                    using (rc)
                                    {
                                        temp = V(rc, "Temperature"); tmax = V(rc, "TemperatureMax"); wear = V(rc, "Wear");
                                        hours = V(rc, "PowerOnHours"); cycles = V(rc, "StartStopCycleCount");
                                        rerr = V(rc, "ReadErrorsUncorrected"); werr = V(rc, "WriteErrorsUncorrected");
                                    }
                        }
                        catch { }
                        sb.Append("S\t").Append(id).Append('\t').Append(health).Append('\t').Append(temp).Append('\t').Append(tmax).Append('\t')
                          .Append(wear).Append('\t').Append(hours).Append('\t').Append(rerr).Append('\t').Append(werr).Append('\t').Append(cycles).Append('\n');
                    }
            return sb.ToString();
        }

        static string V(ManagementObject o, string p)
        {
            try { var v = o[p]; return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); } catch { return ""; }
        }

        // ---------------------------------------------------------------- firewall

        const string RulePrefix = "SysMonitor: bloquear ";

        static string FirewallBlock(string path, bool block)
        {
            if (!File.Exists(path)) return "ERR\tNo se encontró el programa.";
            string name = RulePrefix + Path.GetFileName(path);
            if (block)
            {
                int c1 = RunHidden("netsh.exe", "advfirewall firewall add rule name=\"" + name + "\" dir=out action=block program=\"" + path + "\" enable=yes profile=any");
                int c2 = RunHidden("netsh.exe", "advfirewall firewall add rule name=\"" + name + "\" dir=in action=block program=\"" + path + "\" enable=yes profile=any");
                return c1 == 0 && c2 == 0 ? "OK" : "ERR\tEl firewall rechazó la regla.";
            }
            RunHidden("netsh.exe", "advfirewall firewall delete rule name=\"" + name + "\" program=\"" + path + "\"");
            return "OK";
        }

        static string FirewallList()
        {
            var sb = new StringBuilder();
            try
            {
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic r in policy.Rules)
                {
                    string n = r.Name;
                    if (n == null || !n.StartsWith(RulePrefix)) continue;
                    string app = r.ApplicationName;
                    if (app != null && seen.Add(app)) sb.Append("B\t").Append(app).Append('\n');
                }
            }
            catch { }
            return sb.ToString();
        }

        static int RunHidden(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
        }

        // ---------------------------------------------------------------- canal con la ventana

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);

        static string ImageOf(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(h); }
        }

        static void PipeLoop()
        {
            var me = WindowsIdentity.GetCurrent().User;
            while (!quit)
            {
                try
                {
                    // Solo el usuario de esta sesión puede conectarse, y además se verifica que sea Monitor.exe.
                    var sec = new PipeSecurity();
                    sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None, 65536, 65536, sec))
                    {
                        pipe.WaitForConnection();
                        uint cpid;
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out cpid)) continue;
                        string img = ImageOf((int)cpid);
                        if (img == null || !string.Equals(Path.GetFullPath(img), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                            continue; // otro programa intentó usar el ayudante: se rechaza
                        var reader = new StreamReader(pipe, new UTF8Encoding(false));
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                        string line;
                        while (!quit && pipe.IsConnected && (line = reader.ReadLine()) != null)
                        {
                            string reply;
                            try { reply = Handle(line.Split('\t')); } catch (Exception ex) { reply = "ERR\t" + Clean(ex.Message) + "\n"; }
                            writer.Write(reply);
                            if (!reply.EndsWith("\n")) writer.Write("\n");
                            writer.Write("END\n");
                            writer.Flush();
                        }
                    }
                }
                catch { Thread.Sleep(200); }
            }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
        static string Trim(string s, int max) { s = Clean(s); return s.Length > max ? s.Substring(0, max) + "…" : s; }

        static string Handle(string[] a)
        {
            string cmd = a[0];
            var sb = new StringBuilder();
            switch (cmd)
            {
                case "PING":
                    return "OK\t" + Protocol + "\t" + instance + "\t" + EtwSession.Diagnostics;
                case "EVENTS":
                {
                    long since = a.Length > 1 ? long.Parse(a[1]) : 0;
                    sb.Append("I\t").Append(instance).Append('\n');
                    lock (events)
                        foreach (var e in events.Where(x => x.Seq > since))
                            sb.Append(e.Seq).Append('\t').Append(e.Time.Ticks).Append('\t').Append((int)e.Cat).Append('\t').Append((int)e.Sev).Append('\t').Append(Clean(e.Msg)).Append('\n');
                    return sb.ToString();
                }
                case "NET":
                    lock (gate)
                    {
                        foreach (var kv in byPid)
                            sb.Append("P\t").Append(kv.Key).Append('\t').Append(N(kv.Value.SentBps)).Append('\t').Append(N(kv.Value.RecvBps)).Append('\t')
                              .Append(kv.Value.SentTotal).Append('\t').Append(kv.Value.RecvTotal).Append('\n');
                        foreach (var kv in byConn.Where(x => x.Value.SentBps > 0 || x.Value.RecvBps > 0 || x.Value.SentTotal + x.Value.RecvTotal > 0).OrderByDescending(x => x.Value.SentTotal + x.Value.RecvTotal).Take(3000))
                            sb.Append("C\t").Append(kv.Key.Replace('|', '\t')).Append('\t').Append(N(kv.Value.SentBps)).Append('\t').Append(N(kv.Value.RecvBps)).Append('\t')
                              .Append(kv.Value.SentTotal).Append('\t').Append(kv.Value.RecvTotal).Append('\n');
                    }
                    return sb.ToString();
                case "FILES":
                    if (a.Length > 1 && a[1] == "on") StartFiles(); else StopFiles();
                    return "OK";
                case "FILESTATS":
                    fileRequested = DateTime.UtcNow;
                    lock (gate)
                        foreach (var f in files.Values.OrderByDescending(x => x.ReadBps + x.WriteBps).ThenByDescending(x => x.ReadTotal + x.WriteTotal).Take(400))
                            sb.Append("F\t").Append(f.Pid).Append('\t').Append(Clean(f.Path)).Append('\t').Append(N(f.ReadBps)).Append('\t').Append(N(f.WriteBps)).Append('\t')
                              .Append(f.ReadTotal).Append('\t').Append(f.WriteTotal).Append('\n');
                    return sb.ToString();
                case "SMART":
                    return smart;
                case "KILL":
                {
                    using (var p = Process.GetProcessById(int.Parse(a[1]))) p.Kill();
                    return "OK";
                }
                case "PRIO":
                {
                    using (var p = Process.GetProcessById(int.Parse(a[1]))) p.PriorityClass = (ProcessPriorityClass)int.Parse(a[2]);
                    return "OK";
                }
                case "AFFINITY":
                {
                    using (var p = Process.GetProcessById(int.Parse(a[1]))) p.ProcessorAffinity = new IntPtr(long.Parse(a[2], NumberStyles.HexNumber));
                    return "OK";
                }
                case "PRIOGET":
                {
                    using (var p = Process.GetProcessById(int.Parse(a[1])))
                        return "OK\t" + (int)p.PriorityClass + "\t" + p.ProcessorAffinity.ToInt64().ToString("X");
                }
                case "FWBLOCK": return FirewallBlock(a[1], true);
                case "FWUNBLOCK": return FirewallBlock(a[1], false);
                case "FWLIST": return FirewallList();
                case "QUIT":
                    quit = true;
                    return "OK";
            }
            return "ERR\tComando desconocido";
        }

        static string N(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- instalación (se ejecuta elevado)

        /// <summary>Crea la tarea programada que inicia el ayudante con privilegios al iniciar sesión, y lo arranca.</summary>
        public static int Install()
        {
            if (!IsAdmin()) return 5;
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(Application.ExecutablePath);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Funciones de administrador del Monitor del sistema</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--helper</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "SysMonitorHelper.task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                int code = RunHidden("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                if (code != 0) return code;
                RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
                return 0;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static int Uninstall()
        {
            if (!IsAdmin()) return 5;
            RunHidden("schtasks.exe", "/End /TN \"" + TaskName + "\"");
            RunHidden("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
            // Cerrar el ayudante si quedó corriendo.
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Monitor.exe'"))
                    foreach (ManagementObject p in q.Get())
                        using (p)
                            if (Convert.ToString(p["CommandLine"]).Contains("--helper"))
                                try { using (var pr = Process.GetProcessById(Convert.ToInt32(p["ProcessId"]))) pr.Kill(); } catch { }
            }
            catch { }
            EtwSession.StopExisting("SysMonitor-Main");
            EtwSession.StopExisting("SysMonitor-Files");
            return 0;
        }
    }
}
