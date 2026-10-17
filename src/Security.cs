using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Panoptic
{
    // ====================================================================== conexiones de red

    internal sealed class Connection
    {
        public string Proto, Local, Remote, State;
        public IPAddress RemoteIp;
        public int LocalPort, RemotePort, Pid;
        public string Process, Host;
        public bool Listening { get { return State == "Escuchando"; } }
        public string Key { get { return Proto + Local + ":" + LocalPort + ">" + Remote + ":" + RemotePort + "#" + Pid; } }
    }

    /// <summary>Tabla de conexiones TCP/UDP con el proceso dueño (GetExtendedTcpTable/GetExtendedUdpTable).</summary>
    internal static class NetConnections
    {
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        const int AF_INET = 2, AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

        static readonly string[] TcpStates =
        {
            "", "Cerrada", "Escuchando", "SYN enviado", "SYN recibido", "Establecida", "FIN espera 1", "FIN espera 2",
            "Cerrando (espera)", "Cerrando", "Último ACK", "Espera de tiempo", "Eliminando TCB"
        };

        public static List<Connection> Snapshot(bool includeUdp)
        {
            var list = new List<Connection>();
            ReadTcp(AF_INET, list);
            ReadTcp(AF_INET6, list);
            if (includeUdp) { ReadUdp(AF_INET, list); ReadUdp(AF_INET6, list); }
            return list;
        }

        static IntPtr Table(bool tcp, int af, out int size)
        {
            size = 0;
            int cls = tcp ? TCP_TABLE_OWNER_PID_ALL : UDP_TABLE_OWNER_PID;
            if (tcp) GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, cls, 0);
            else GetExtendedUdpTable(IntPtr.Zero, ref size, true, af, cls, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size + 4096);
                int sz = size + 4096;
                uint r = tcp ? GetExtendedTcpTable(buf, ref sz, true, af, cls, 0) : GetExtendedUdpTable(buf, ref sz, true, af, cls, 0);
                if (r == 0) { size = sz; return buf; }
                Marshal.FreeHGlobal(buf);
                size = sz;
            }
            return IntPtr.Zero;
        }

        static void ReadTcp(int af, List<Connection> list)
        {
            int size;
            IntPtr buf = Table(true, af, out size);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                IntPtr p = buf + 4;
                for (int i = 0; i < n; i++)
                {
                    var c = new Connection { Proto = af == AF_INET ? "TCP" : "TCPv6" };
                    if (af == AF_INET)
                    {
                        // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 × 4 bytes)
                        int state = Marshal.ReadInt32(p);
                        c.Local = new IPAddress((uint)Marshal.ReadInt32(p + 4)).ToString();
                        c.LocalPort = Port(Marshal.ReadInt32(p + 8));
                        c.RemoteIp = new IPAddress((uint)Marshal.ReadInt32(p + 12));
                        c.RemotePort = Port(Marshal.ReadInt32(p + 16));
                        c.Pid = Marshal.ReadInt32(p + 20);
                        c.State = state >= 0 && state < TcpStates.Length ? TcpStates[state] : state.ToString();
                        p += 24;
                    }
                    else
                    {
                        // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                        var la = new byte[16]; Marshal.Copy(p, la, 0, 16);
                        c.Local = new IPAddress(la).ToString();
                        c.LocalPort = Port(Marshal.ReadInt32(p + 20));
                        var ra = new byte[16]; Marshal.Copy(p + 24, ra, 0, 16);
                        c.RemoteIp = new IPAddress(ra);
                        c.RemotePort = Port(Marshal.ReadInt32(p + 44));
                        int state = Marshal.ReadInt32(p + 48);
                        c.Pid = Marshal.ReadInt32(p + 52);
                        c.State = state >= 0 && state < TcpStates.Length ? TcpStates[state] : state.ToString();
                        p += 56;
                    }
                    c.Remote = c.Listening ? "" : c.RemoteIp.ToString();
                    if (c.Listening) c.RemotePort = 0;
                    list.Add(c);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void ReadUdp(int af, List<Connection> list)
        {
            int size;
            IntPtr buf = Table(false, af, out size);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                IntPtr p = buf + 4;
                for (int i = 0; i < n; i++)
                {
                    var c = new Connection { Proto = af == AF_INET ? "UDP" : "UDPv6", State = "", Remote = "" };
                    if (af == AF_INET)
                    {
                        c.Local = new IPAddress((uint)Marshal.ReadInt32(p)).ToString();
                        c.LocalPort = Port(Marshal.ReadInt32(p + 4));
                        c.Pid = Marshal.ReadInt32(p + 8);
                        p += 12;
                    }
                    else
                    {
                        var la = new byte[16]; Marshal.Copy(p, la, 0, 16);
                        c.Local = new IPAddress(la).ToString();
                        c.LocalPort = Port(Marshal.ReadInt32(p + 20));
                        c.Pid = Marshal.ReadInt32(p + 24);
                        p += 28;
                    }
                    list.Add(c);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

        // ---- Nombres de host (DNS inverso, en segundo plano y con caché)
        static readonly Dictionary<string, string> hosts = new Dictionary<string, string>();

        public static string HostName(IPAddress ip)
        {
            if (ip == null) return null;
            string key = ip.ToString();
            if (IPAddress.IsLoopback(ip) || key == "0.0.0.0" || key == "::") return null;
            lock (hosts)
            {
                string h;
                if (hosts.TryGetValue(key, out h)) return h;
                hosts[key] = null;
            }
            ThreadPool.QueueUserWorkItem(o =>
            {
                string name = null;
                try { name = Dns.GetHostEntry(ip).HostName; } catch { }
                if (name == key) name = null;
                lock (hosts) hosts[key] = name ?? "";
            });
            return null;
        }
    }

    // ====================================================================== programas de inicio

    internal sealed class StartupItem
    {
        public string Name, Command, Location, Kind, Path, Publisher;
        public bool Enabled = true, NeedsAdmin, CanToggle = true;
        // Para habilitar/deshabilitar
        public RegistryHive ApprovedHive;
        public string ApprovedKey, ApprovedValue, TaskPath;
        public string Id { get { return Kind + "|" + Location + "|" + Name; } }
    }

    /// <summary>
    /// Programas que arrancan con Windows: claves Run del registro, carpetas de Inicio y tareas programadas
    /// al iniciar sesión o al encender. Se habilitan y deshabilitan igual que en el Administrador de tareas.
    /// </summary>
    internal static class Startup
    {
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupItem> Scan()
        {
            var list = new List<StartupItem>();
            RunKey(list, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registro (usuario)", "Run", false);
            RunKey(list, RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registro (equipo)", "Run", true);
            RunKey(list, RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registro (equipo, 32 bits)", "Run32", true);
            RunKey(list, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "Registro (usuario, una vez)", null, false);
            RunKey(list, RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "Registro (equipo, una vez)", null, true);
            Folder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Carpeta Inicio (usuario)", RegistryHive.CurrentUser, false);
            Folder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Carpeta Inicio (todos)", RegistryHive.LocalMachine, true);
            Tasks(list);
            foreach (var i in list)
            {
                i.Path = ExtractPath(i.Command);
                if (i.Command != null && i.Command.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // Acceso directo: mostrar el programa al que apunta.
                    try
                    {
                        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                        dynamic lnk = shell.CreateShortcut(i.Command);
                        string target = lnk.TargetPath;
                        string args = lnk.Arguments;
                        if (!string.IsNullOrEmpty(target)) { i.Command = target + (string.IsNullOrEmpty(args) ? "" : " " + args); i.Path = File.Exists(target) ? target : null; }
                    }
                    catch { }
                }
                if (i.Path != null) try { i.Publisher = FileVersionInfo.GetVersionInfo(i.Path).CompanyName; } catch { }
            }
            return list;
        }

        static void RunKey(List<StartupItem> list, RegistryHive hive, RegistryView view, string key, string location, string approvedName, bool admin)
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, view))
                using (var k = b.OpenSubKey(key))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        if (name.Length == 0) continue;
                        var it = new StartupItem
                        {
                            Name = name, Command = Convert.ToString(k.GetValue(name)), Location = location, Kind = "Registro",
                            NeedsAdmin = admin, CanToggle = approvedName != null, ApprovedHive = hive,
                            ApprovedKey = approvedName == null ? null : Approved + approvedName, ApprovedValue = name
                        };
                        it.Enabled = approvedName == null || IsApproved(hive, it.ApprovedKey, name);
                        list.Add(it);
                    }
                }
            }
            catch { }
        }

        static void Folder(List<StartupItem> list, string dir, string location, RegistryHive hive, bool admin)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                {
                    string name = System.IO.Path.GetFileName(f);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var it = new StartupItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(f), Command = f, Location = location, Kind = "Carpeta",
                        NeedsAdmin = admin, ApprovedHive = hive, ApprovedKey = Approved + "StartupFolder", ApprovedValue = name
                    };
                    it.Enabled = IsApproved(hive, it.ApprovedKey, name);
                    list.Add(it);
                }
            }
            catch { }
        }

        /// <summary>Tareas programadas que se ejecutan al encender o al iniciar sesión (sin las de Microsoft).</summary>
        static void Tasks(List<StartupItem> list)
        {
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return;
                dynamic svc = Activator.CreateInstance(t);
                svc.Connect();
                Folder(svc.GetFolder("\\"), list);
            }
            catch { }
        }

        static void Folder(dynamic folder, List<StartupItem> list)
        {
            try
            {
                string path = folder.Path;
                if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
                foreach (dynamic task in folder.GetTasks(1))
                {
                    try
                    {
                        dynamic def = task.Definition;
                        bool atStart = false;
                        string when = "";
                        foreach (dynamic trig in def.Triggers)
                        {
                            int type = trig.Type;
                            if (type == 8) { atStart = true; when = "al encender"; }
                            if (type == 9) { atStart = true; when = "al iniciar sesión"; }
                        }
                        if (!atStart) continue;
                        string cmd = "";
                        foreach (dynamic act in def.Actions)
                            if ((int)act.Type == 0) { cmd = ((string)act.Path ?? "") + " " + ((string)act.Arguments ?? ""); break; }
                        list.Add(new StartupItem
                        {
                            Name = task.Name, Command = cmd.Trim(), Location = "Tarea programada (" + when + ")", Kind = "Tarea",
                            Enabled = task.Enabled, TaskPath = task.Path
                        });
                    }
                    catch { }
                }
                foreach (dynamic sub in folder.GetFolders(0)) Folder(sub, list);
            }
            catch { }
        }

        static bool IsApproved(RegistryHive hive, string key, string value)
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var k = b.OpenSubKey(key))
                {
                    var data = k == null ? null : k.GetValue(value) as byte[];
                    return data == null || data.Length == 0 || (data[0] & 1) == 0; // impar = deshabilitado
                }
            }
            catch { return true; }
        }

        /// <summary>Habilita o deshabilita. Si hace falta, pide permisos de administrador (UAC).</summary>
        public static void SetEnabled(StartupItem it, bool enabled)
        {
            if (it.Kind == "Tarea")
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                dynamic svc = Activator.CreateInstance(t);
                svc.Connect();
                dynamic task = svc.GetFolder("\\").GetTask(it.TaskPath);
                try { task.Enabled = enabled; }
                catch (Exception)
                {
                    Elevated("schtasks.exe", "/Change /TN \"" + it.TaskPath + "\" " + (enabled ? "/ENABLE" : "/DISABLE"));
                }
                return;
            }
            if (!it.CanToggle) throw new InvalidOperationException("Esta entrada se ejecuta una sola vez y no se puede deshabilitar.");
            var data = new byte[12];
            data[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            if (!it.NeedsAdmin)
            {
                using (var b = RegistryKey.OpenBaseKey(it.ApprovedHive, RegistryView.Registry64))
                using (var k = b.CreateSubKey(it.ApprovedKey))
                    k.SetValue(it.ApprovedValue, data, RegistryValueKind.Binary);
                return;
            }
            string hex = BitConverter.ToString(data).Replace("-", "");
            Elevated("reg.exe", "add \"HKLM\\" + it.ApprovedKey + "\" /v \"" + it.ApprovedValue + "\" /t REG_BINARY /d " + hex + " /f /reg:64");
        }

        /// <summary>Ejecuta un comando con permisos de administrador (muestra el aviso de UAC) y espera.</summary>
        public static void Elevated(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(15000);
                if (p.HasExited && p.ExitCode != 0) throw new InvalidOperationException(exe + " terminó con el código " + p.ExitCode + ".");
            }
        }

        public static string ExtractPath(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            string c = Environment.ExpandEnvironmentVariables(cmd.Trim());
            string path;
            if (c.StartsWith("\"")) { int e = c.IndexOf('"', 1); path = e > 1 ? c.Substring(1, e - 1) : c.Trim('"'); }
            else
            {
                int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = e > 0 ? c.Substring(0, e + 4) : c.Split(' ')[0];
            }
            return File.Exists(path) ? path : null;
        }
    }

    // ====================================================================== servicios

    internal sealed class ServiceItem
    {
        public string Name, Display, Status, StartType, Description, ImagePath;
        public bool Running, CanStop;
    }

    internal static class Services
    {
        public static List<ServiceItem> List()
        {
            var list = new List<ServiceItem>();
            ServiceController[] all;
            try { all = ServiceController.GetServices(); } catch { return list; }
            foreach (var sc in all)
            {
                using (sc)
                {
                    var it = new ServiceItem { Name = sc.ServiceName, Display = sc.DisplayName };
                    try
                    {
                        it.Running = sc.Status == ServiceControllerStatus.Running;
                        it.Status = StatusName(sc.Status);
                        it.CanStop = sc.CanStop;
                    }
                    catch { it.Status = "—"; }
                    it.StartType = StartTypeOf(sc.ServiceName);
                    try
                    {
                        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName))
                        {
                            if (k != null)
                            {
                                it.ImagePath = k.GetValue("ImagePath") as string;
                                var d = k.GetValue("Description") as string;
                                it.Description = d != null && !d.StartsWith("@") ? d : null;
                            }
                        }
                    }
                    catch { }
                    list.Add(it);
                }
            }
            return list;
        }

        static string StartTypeOf(string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (k == null) return "—";
                    int start = k.GetValue("Start") is int ? (int)k.GetValue("Start") : -1;
                    bool delayed = k.GetValue("DelayedAutostart") is int && (int)k.GetValue("DelayedAutostart") == 1;
                    switch (start)
                    {
                        case 0: return "Arranque";
                        case 1: return "Sistema";
                        case 2: return delayed ? "Automático (retrasado)" : "Automático";
                        case 3: return "Manual";
                        case 4: return "Deshabilitado";
                        default: return "—";
                    }
                }
            }
            catch { return "—"; }
        }

        static string StatusName(ServiceControllerStatus s)
        {
            switch (s)
            {
                case ServiceControllerStatus.Running: return "En ejecución";
                case ServiceControllerStatus.Stopped: return "Detenido";
                case ServiceControllerStatus.Paused: return "En pausa";
                case ServiceControllerStatus.StartPending: return "Iniciando";
                case ServiceControllerStatus.StopPending: return "Deteniendo";
                default: return s.ToString();
            }
        }

        /// <summary>Inicia, detiene o reinicia. Si no hay permisos, lo pide con UAC.</summary>
        public static void Control(string name, string action)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (action == "stop" || action == "restart")
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                    }
                    if (action == "start" || action == "restart")
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                var w = ex.InnerException as System.ComponentModel.Win32Exception;
                if (w == null || w.NativeErrorCode != 5) throw; // 5 = acceso denegado
                string cmd = action == "restart"
                    ? "/c sc stop \"" + name + "\" & timeout /t 3 /nobreak >nul & sc start \"" + name + "\""
                    : "/c sc " + (action == "stop" ? "stop" : "start") + " \"" + name + "\"";
                Startup.Elevated("cmd.exe", cmd);
            }
        }
    }

    // ====================================================================== vigilancia

    /// <summary>
    /// Detecta cambios relevantes para la seguridad y los anota en el registro: programas de inicio nuevos,
    /// servicios nuevos, puertos en escucha nuevos y eventos de WriteBlocker.
    /// </summary>
    internal sealed class SecurityWatch : IDisposable
    {
        readonly Timer timer;
        int busy;
        long tick;
        HashSet<string> listeners;
        FileSystemWatcher wbWatcher;
        readonly object wbGate = new object();
        static readonly string DataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Panoptic");
        public static string WriteBlockerLog
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WriteBlocker", "actividad.log"); }
        }

        public SecurityWatch()
        {
            SetupWriteBlocker();
            timer = new Timer(Tick, null, 45000, 10000); // el primer escaneo, que es el más pesado, espera a que termine el arranque
        }

        void Tick(object o)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                if (Settings.WatchListeners) CheckListeners();
                if (tick % 30 == 0) // cada 5 minutos (y al arrancar)
                {
                    if (Settings.WatchStartup) CheckStartup();
                    if (Settings.WatchServices) CheckServices();
                }
                tick++;
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // ---- Puertos en escucha
        void CheckListeners()
        {
            var now = NetConnections.Snapshot(false).Where(c => c.Listening).ToList();
            var keys = new HashSet<string>(now.Select(c => c.Proto + " " + c.Local + ":" + c.LocalPort + " " + ProcessName(c.Pid)));
            if (listeners != null)
                foreach (var c in now)
                {
                    string k = c.Proto + " " + c.Local + ":" + c.LocalPort + " " + ProcessName(c.Pid);
                    if (!listeners.Contains(k))
                    {
                        bool exposed = c.Local == "0.0.0.0" || c.Local == "::";
                        Activity.Add(Category.Red, exposed ? Severity.Advertencia : Severity.Info,
                            "Nuevo puerto en escucha: " + c.Proto + " " + c.Local + ":" + c.LocalPort + " (" + ProcessName(c.Pid) + ", PID " + c.Pid + ")" +
                            (exposed ? ", accesible desde la red" : ""));
                    }
                }
            listeners = keys;
        }

        // Nombres de procesos por PID: una sola enumeración cada 2 s (GetProcessById recorre todos los procesos en cada llamada).
        static Dictionary<int, string> names = new Dictionary<int, string>();
        static DateTime namesAt = DateTime.MinValue;
        static readonly object namesGate = new object();

        public static string ProcessName(int pid)
        {
            if (pid == 0) return "Inactivo del sistema";
            if (pid == 4) return "System";
            lock (namesGate)
            {
                if ((DateTime.UtcNow - namesAt).TotalSeconds > 2 || !names.ContainsKey(pid) && (DateTime.UtcNow - namesAt).TotalMilliseconds > 500)
                {
                    var map = new Dictionary<int, string>();
                    foreach (var p in Process.GetProcesses())
                        using (p) try { map[p.Id] = p.ProcessName + ".exe"; } catch { }
                    names = map;
                    namesAt = DateTime.UtcNow;
                }
                string n;
                return names.TryGetValue(pid, out n) ? n : "PID " + pid;
            }
        }

        // ---- Programas de inicio
        void CheckStartup()
        {
            var items = Startup.Scan();
            var current = items.Select(i => i.Id + "|" + i.Command).ToList();
            Compare("inicio.txt", current, added =>
            {
                var it = items.FirstOrDefault(i => i.Id + "|" + i.Command == added);
                if (it != null)
                    Activity.Add(Category.Inicio, Severity.Advertencia, "Nuevo programa de inicio: " + it.Name + " (" + it.Location + "): " + it.Command);
            }, removed =>
            {
                var p = removed.Split('|');
                if (p.Length >= 3) Activity.Info(Category.Inicio, "Se quitó el programa de inicio " + p[2] + " (" + p[1] + ")");
            });
        }

        // ---- Servicios
        void CheckServices()
        {
            var list = Services.List();
            // Servicios por usuario (AarSvc_90a86, CDPUserSvc_90a86…): Windows les cambia el sufijo en cada inicio de
            // sesión. Se comparan sin el sufijo para no avisarlos como nuevos cada vez.
            var names = new HashSet<string>(list.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            Func<string, string> norm = key =>
            {
                var p = key.Split('|');
                var m = System.Text.RegularExpressions.Regex.Match(p[0], "^(.+)_[0-9a-fA-F]{4,8}$");
                if (!m.Success || !names.Contains(m.Groups[1].Value)) return key;
                string suffix = p[0].Substring(m.Groups[1].Value.Length);
                return m.Groups[1].Value + "_*" + (p.Length > 1 ? "|" + (p[1].EndsWith(suffix) ? p[1].Substring(0, p[1].Length - suffix.Length) + "_*" : p[1]) : "");
            };
            var current = list.Select(s => norm(s.Name + "|" + s.Display)).Distinct().ToList();
            Compare("servicios.txt", current, added =>
            {
                var p = added.Split('|');
                var sv = list.FirstOrDefault(s => s.Name == p[0]) ?? list.FirstOrDefault(s => norm(s.Name + "|" + s.Display) == added);
                Activity.Add(Category.Servicio, Severity.Advertencia, "Servicio nuevo: " + (p.Length > 1 ? p[1] : p[0]) + " (" + p[0] + ")" +
                    (sv != null && sv.ImagePath != null ? ": " + sv.ImagePath : ""));
            }, removed =>
            {
                var p = removed.Split('|');
                Activity.Info(Category.Servicio, "Se eliminó el servicio " + (p.Length > 1 ? p[1] : p[0]) + " (" + p[0] + ")");
            }, norm);
        }

        /// <summary>Compara con la lista guardada (también entre sesiones) y avisa lo nuevo y lo quitado.</summary>
        static void Compare(string file, List<string> current, Action<string> onAdded, Action<string> onRemoved, Func<string, string> normalize = null)
        {
            string path = System.IO.Path.Combine(DataDir, file);
            Directory.CreateDirectory(DataDir);
            if (File.Exists(path))
            {
                var saved = File.ReadAllLines(path, Encoding.UTF8);
                var known = new HashSet<string>(normalize != null ? saved.Select(normalize) : saved);
                var cur = new HashSet<string>(current);
                foreach (var a in current.Where(c => !known.Contains(c))) onAdded(a);
                foreach (var r in known.Where(k => !cur.Contains(k))) onRemoved(r);
            }
            if (!App.ProfileMode) File.WriteAllLines(path, current, Encoding.UTF8);
        }

        // ---- WriteBlocker: sus eventos se suman a la línea de tiempo
        void SetupWriteBlocker()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(WriteBlockerLog);
                Directory.CreateDirectory(dir);
                ImportWriteBlocker();
                wbWatcher = new FileSystemWatcher(dir, "actividad.log");
                wbWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                wbWatcher.Changed += (s, e) => ImportWriteBlocker();
                wbWatcher.Created += (s, e) => ImportWriteBlocker();
                wbWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        void ImportWriteBlocker()
        {
            if (!Settings.WriteBlockerBridge) return;
            lock (wbGate)
            {
                try
                {
                    if (!File.Exists(WriteBlockerLog)) return;
                    long offset;
                    long.TryParse(Settings.GetString("WriteBlockerOffset", "-1"), out offset);
                    using (var fs = new FileStream(WriteBlockerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (offset < 0) offset = Math.Max(0, fs.Length - 20000); // primera vez: lo más reciente
                        if (offset > fs.Length) offset = 0;                        // el archivo se recreó
                        fs.Seek(offset, SeekOrigin.Begin);
                        using (var r = new StreamReader(fs, Encoding.UTF8))
                        {
                            string text = r.ReadToEnd();
                            int lastNl = text.LastIndexOf('\n');
                            if (lastNl < 0) return;
                            string complete = text.Substring(0, lastNl + 1);
                            foreach (var raw in complete.Split('\n'))
                            {
                                string line = raw.TrimEnd('\r');
                                if (line.Length < 21) continue;
                                DateTime t;
                                if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                                string msg = line.Substring(19).Trim();
                                var sev = msg.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || msg.Contains("NO protegido") ? Severity.Error
                                    : msg.Contains("escritura habilitada") || msg.Contains("desactivado") ? Severity.Advertencia : Severity.Info;
                                Activity.AddAt(t, Category.Bloqueo, sev, msg);
                            }
                            if (!App.ProfileMode) Settings.SetString("WriteBlockerOffset", (offset + Encoding.UTF8.GetByteCount(complete)).ToString());
                        }
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            if (wbWatcher != null) wbWatcher.Dispose();
        }
    }
}
