using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysMonitor
{
    internal enum Category { Programa, Dispositivo, Red, Energia, Sesion, Sistema, Alerta, Monitor, Inicio, Servicio, Bloqueo, Seguridad }
    internal enum Severity { Info, Advertencia, Error }

    internal sealed class ActivityEntry
    {
        public DateTime Time;
        public Category Category;
        public Severity Severity;
        public string Message;

        public static string CategoryName(Category c)
        {
            switch (c)
            {
                case Category.Programa: return "Programas";
                case Category.Dispositivo: return "Dispositivos";
                case Category.Red: return "Red";
                case Category.Energia: return "Energía";
                case Category.Sesion: return "Sesión";
                case Category.Sistema: return "Windows";
                case Category.Alerta: return "Alertas";
                case Category.Inicio: return "Inicio";
                case Category.Servicio: return "Servicios";
                case Category.Bloqueo: return "WriteBlocker";
                case Category.Seguridad: return "Seguridad";
                default: return "Monitor";
            }
        }

        public static string CategoryGlyph(Category c)
        {
            switch (c)
            {
                case Category.Programa: return Glyphs.App;
                case Category.Dispositivo: return Glyphs.Usb;
                case Category.Red: return Glyphs.Wifi;
                case Category.Energia: return Glyphs.Power;
                case Category.Sesion: return Glyphs.Lock;
                case Category.Sistema: return Glyphs.Error;
                case Category.Alerta: return Glyphs.Warning;
                case Category.Inicio: return Glyphs.Startup;
                case Category.Servicio: return Glyphs.Settings;
                case Category.Bloqueo: return Glyphs.Lock;
                case Category.Seguridad: return Glyphs.Shield;
                default: return Glyphs.Info;
            }
        }
    }

    /// <summary>Registro de lo que pasa en la PC: en memoria (para la interfaz) y en un archivo de texto.</summary>
    internal static class Activity
    {
        static readonly List<ActivityEntry> entries = new List<ActivityEntry>();
        static readonly object gate = new object();
        public static event Action<ActivityEntry> Added;
        const int MaxEntries = 5000;
        public static bool MemoryOnly; // vista previa: no escribir en el archivo

        public static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SysMonitor", "registro.log");
            }
        }

        public static void Add(Category c, Severity sev, string message) { AddAt(DateTime.Now, c, sev, message); }

        /// <summary>Agrega un evento con su propia fecha (por ejemplo, importado del registro de WriteBlocker).</summary>
        public static void AddAt(DateTime time, Category c, Severity sev, string message)
        {
            var e = new ActivityEntry { Time = time, Category = c, Severity = sev, Message = message };
            lock (gate)
            {
                // Evitar duplicados idénticos seguidos (por ejemplo, dispositivos con varias interfaces).
                var last = entries.Count > 0 ? entries[entries.Count - 1] : null;
                if (last != null && last.Message == message && (e.Time - last.Time).TotalSeconds < 3) return;
                entries.Add(e);
                Version++;
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
                if (!MemoryOnly) try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath,
                        e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + c + "\t" + sev + "\t" +
                        message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            var h = Added;
            if (h != null) try { h(e); } catch { }
        }

        public static void Info(Category c, string m) { Add(c, Severity.Info, m); }

        /// <summary>Cambia con cada evento nuevo: permite reutilizar listas ya filtradas.</summary>
        public static volatile int Version;

        public static List<ActivityEntry> Snapshot()
        {
            lock (gate) return new List<ActivityEntry>(entries);
        }

        /// <summary>Carga las últimas entradas del archivo de sesiones anteriores.</summary>
        public static void LoadPrevious(int max)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                var list = new List<ActivityEntry>();
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - max)))
                {
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    DateTime t;
                    Category c;
                    Severity s;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    if (!Enum.TryParse(p[1], out c) || !Enum.TryParse(p[2], out s)) continue;
                    list.Add(new ActivityEntry { Time = t, Category = c, Severity = s, Message = p[3] });
                }
                lock (gate) { entries.InsertRange(0, list); Version++; }
            }
            catch { }
        }
    }

    /// <summary>Escucha eventos del sistema y los vuelca al registro de actividad.</summary>
    internal sealed class SystemWatcher : NativeWindow, IDisposable
    {
        static readonly Guid UsbDevice = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        static readonly Guid DiskDevice = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
        static readonly Guid MonitorDevice = new Guid("E6F07B5F-EE97-4a90-B076-33F57BF4EAA7");

        // Procesos que Windows abre y cierra todo el tiempo: no aportan al registro.
        static readonly HashSet<string> Noisy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conhost", "backgroundTaskHost", "RuntimeBroker", "svchost", "SearchProtocolHost", "SearchFilterHost",
            "WmiPrvSE", "dllhost", "smartscreen", "taskhostw", "sihost", "ctfmon", "audiodg", "SearchIndexer",
            "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "consent", "CompPkgSrv", "WMIADAP", "wermgr",
            "WerFault", "SystemSettingsBroker", "UserOOBEBroker", "ApplicationFrameHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "TextInputHost", "SearchHost", "LockApp", "WidgetService", "Widgets",
            "msedgewebview2", "netsh", "schtasks", "mountvol", "pnputil", "diskpart", "cmd", "powershell", "where",
            "git", "tasklist", "find", "findstr", "reg", "sc", "ping", "timeout", "rundll32", "MpCmdRun", "NisSrv",
            "SecurityHealthHost", "AggregatorHost", "SgrmBroker", "csc", "cvtres", "SDXHelper", "OfficeClickToRun",
            "crashpad_handler", "chrome_crashpad_handler", "identity_helper", "msedge_proxy", "fontdrvhost", "dwm",
            "upfc", "SppExtComObj", "sppsvc", "DeviceCensus", "CompatTelRunner", "DismHost", "MusNotification",
            "MusNotifyIcon", "GameBarPresenceWriter", "PhoneExperienceHost", "CrossDeviceResume", "SysMonitor"
        };

        IntPtr notification;
        EventLog systemLog, appLog;
        readonly HashSet<string> upNets = new HashSet<string>();
        PowerLineStatus lastLine;
        bool lowBatteryAlerted;
        readonly Dictionary<string, string> netNames = new Dictionary<string, string>();

        public SystemWatcher(Sampler sampler)
        {
            var cp = new CreateParams();
            cp.Caption = "SysMonitor.Watcher";
            CreateHandle(cp);

            var f = new Native.DEV_BROADCAST_DEVICEINTERFACE();
            f.dbcc_size = Marshal.SizeOf(typeof(Native.DEV_BROADCAST_DEVICEINTERFACE));
            f.dbcc_devicetype = Native.DBT_DEVTYP_DEVICEINTERFACE;
            IntPtr buf = Marshal.AllocHGlobal(f.dbcc_size);
            try
            {
                Marshal.StructureToPtr(f, buf, false);
                notification = Native.RegisterDeviceNotification(Handle, buf,
                    Native.DEVICE_NOTIFY_WINDOW_HANDLE | Native.DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
            }
            finally { Marshal.FreeHGlobal(buf); }

            sampler.ProcessStarted += p =>
            {
                if (Noisy.Contains(p.Name)) return;
                // Con las funciones de administrador activas, el ayudante registra cada ejecución con su línea de comandos.
                if (HelperClient.Instance != null && HelperClient.Instance.Connected) return;
                Activity.Info(Category.Programa, "Se abrió " + p.Display +
                    (p.Display != p.Name ? " (" + p.Name + ".exe, PID " + p.Pid + ")" : " (PID " + p.Pid + ")"));
            };
            sampler.ProcessExited += (p, ran) =>
            {
                if (Noisy.Contains(p.Name)) return;
                Activity.Info(Category.Programa, "Se cerró " + p.Display + " (PID " + p.Pid + ", estuvo abierto " + Format.Duration(ran) + ")");
            };
            sampler.Alert += msg => Activity.Add(Category.Alerta, Severity.Advertencia, msg);

            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.SessionSwitch += OnSession;
            SystemEvents.DisplaySettingsChanged += OnDisplay;
            SystemEvents.TimeChanged += OnTime;
            NetworkChange.NetworkAddressChanged += OnNetwork;
            NetworkChange.NetworkAvailabilityChanged += OnAvailability;
            lastLine = SystemInformation.PowerStatus.PowerLineStatus;
            foreach (var n in UpInterfaces()) { upNets.Add(n.Id); netNames[n.Id] = Describe(n); }

            systemLog = Watch("System");
            appLog = Watch("Application");
        }

        // ---- Visor de eventos de Windows (solo errores y críticos)
        EventLog Watch(string name)
        {
            try
            {
                var log = new EventLog(name);
                log.EntryWritten += (s, e) =>
                {
                    var en = e.Entry;
                    // 7045: el Administrador de servicios instaló un servicio nuevo (señal clásica de malware).
                    if (en.Source == "Service Control Manager" && (en.InstanceId & 0xFFFF) == 7045)
                    {
                        string first = "";
                        try { first = string.Join(" ", (en.Message ?? "").Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries)); } catch { }
                        if (first.Length > 260) first = first.Substring(0, 260) + "…";
                        Activity.Add(Category.Servicio, Severity.Advertencia, "Se instaló un servicio: " + first);
                        return;
                    }
                    if (en.EntryType != EventLogEntryType.Error) return;
                    string msg = "";
                    try { msg = (en.Message ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""; } catch { }
                    if (msg.Length > 220) msg = msg.Substring(0, 220) + "…";
                    string what = en.Source == "Application Error" ? "Un programa se cerró inesperadamente"
                        : en.Source == "Application Hang" ? "Un programa dejó de responder"
                        : "Error de " + en.Source;
                    Activity.Add(Category.Sistema, Severity.Error, what + (msg.Length > 0 ? ": " + msg : "") + " (evento " + (en.InstanceId & 0xFFFF) + ")");
                };
                log.EnableRaisingEvents = true;
                return log;
            }
            catch { return null; }
        }

        // ---- Dispositivos
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 /* WM_HOTKEY */ && m.WParam.ToInt32() == HotkeyId)
            {
                if (HotkeyPressed != null) HotkeyPressed();
                return;
            }
            if (m.Msg == Native.WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
            {
                long ev = m.WParam.ToInt64();
                if (ev == Native.DBT_DEVICEARRIVAL || ev == Native.DBT_DEVICEREMOVECOMPLETE)
                {
                    bool arrived = ev == Native.DBT_DEVICEARRIVAL;
                    try
                    {
                        var hdr = (Native.DEV_BROADCAST_HDR)Marshal.PtrToStructure(m.LParam, typeof(Native.DEV_BROADCAST_HDR));
                        if (hdr.dbch_devicetype == Native.DBT_DEVTYP_DEVICEINTERFACE)
                        {
                            var di = (Native.DEV_BROADCAST_DEVICEINTERFACE)Marshal.PtrToStructure(m.LParam, typeof(Native.DEV_BROADCAST_DEVICEINTERFACE));
                            string path = Marshal.PtrToStringUni(new IntPtr(m.LParam.ToInt64() + 28));
                            if (di.dbcc_classguid == UsbDevice)
                                Activity.Info(Category.Dispositivo, (arrived ? "Se conectó " : "Se desconectó ") + DeviceName(path, "un dispositivo USB"));
                            else if (di.dbcc_classguid == MonitorDevice)
                                Activity.Info(Category.Dispositivo, (arrived ? "Se conectó la pantalla " : "Se desconectó la pantalla ") + DeviceName(path, ""));
                            if (di.dbcc_classguid == DiskDevice && DiskChanged != null) DiskChanged();
                        }
                        else if (hdr.dbch_devicetype == Native.DBT_DEVTYP_VOLUME)
                        {
                            var vol = (Native.DEV_BROADCAST_VOLUME)Marshal.PtrToStructure(m.LParam, typeof(Native.DEV_BROADCAST_VOLUME));
                            for (int i = 0; i < 26; i++)
                                if ((vol.dbcv_unitmask & (1u << i)) != 0)
                                    Activity.Info(Category.Dispositivo, (arrived ? "Unidad disponible: " : "Unidad quitada: ") + (char)('A' + i) + ":" + VolumeLabel((char)('A' + i), arrived));
                            if (DiskChanged != null) DiskChanged();
                        }
                    }
                    catch { }
                }
            }
            base.WndProc(ref m);
        }

        public event Action DiskChanged;

        // ---- Atajo de teclado global
        public event Action HotkeyPressed;
        const int HotkeyId = 0x5301;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        /// <summary>Registra el atajo elegido en Configuración. Devuelve false si otro programa ya lo usa.</summary>
        public bool SetHotkey(int option)
        {
            UnregisterHotKey(Handle, HotkeyId);
            if (option <= 0) return true;
            const uint ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8, NOREPEAT = 0x4000;
            uint mods = option == 1 ? CTRL | SHIFT : option == 2 ? CTRL | ALT : WIN | SHIFT;
            return RegisterHotKey(Handle, HotkeyId, mods | NOREPEAT, 0x4D /* M */);
        }

        static string VolumeLabel(char letter, bool arrived)
        {
            if (!arrived) return "";
            try
            {
                var d = new DriveInfo(letter + ":");
                if (!d.IsReady) return "";
                return " (" + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel + ", ") +
                       d.DriveFormat + ", " + Format.Bytes((ulong)d.TotalSize) + ")";
            }
            catch { return ""; }
        }

        /// <summary>\\?\USB#VID_0951&amp;PID_1666#123#{guid} → nombre del dispositivo en el registro.</summary>
        static string DeviceName(string path, string fallback)
        {
            try
            {
                string p = path.StartsWith(@"\\?\") ? path.Substring(4) : path;
                int i = p.LastIndexOf("#{", StringComparison.Ordinal);
                if (i > 0) p = p.Substring(0, i);
                string inst = p.Replace('#', '\\');
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + inst))
                {
                    if (k != null)
                    {
                        string name = (k.GetValue("FriendlyName") as string) ?? (k.GetValue("DeviceDesc") as string);
                        if (name != null)
                        {
                            int semi = name.LastIndexOf(';');
                            if (semi >= 0) name = name.Substring(semi + 1);
                            return name.Trim();
                        }
                    }
                }
            }
            catch { }
            return fallback;
        }

        // ---- Energía, sesión, pantalla, hora
        void OnPower(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) Activity.Info(Category.Energia, "La PC entró en suspensión");
            else if (e.Mode == PowerModes.Resume) Activity.Info(Category.Energia, "La PC se reanudó");
            else if (e.Mode == PowerModes.StatusChange)
            {
                var ps = SystemInformation.PowerStatus;
                if (ps.PowerLineStatus != lastLine && ps.PowerLineStatus != PowerLineStatus.Unknown)
                {
                    lastLine = ps.PowerLineStatus;
                    Activity.Info(Category.Energia, ps.PowerLineStatus == PowerLineStatus.Online
                        ? "Conectada a la corriente (batería " + Pct(ps.BatteryLifePercent) + ")"
                        : "Funcionando con batería (" + Pct(ps.BatteryLifePercent) + ")");
                }
                if (ps.PowerLineStatus == PowerLineStatus.Offline && ps.BatteryLifePercent <= 0.15f && !lowBatteryAlerted)
                {
                    lowBatteryAlerted = true;
                    Activity.Add(Category.Alerta, Severity.Advertencia, "Batería baja: " + Pct(ps.BatteryLifePercent));
                }
                if (ps.BatteryLifePercent > 0.2f || ps.PowerLineStatus == PowerLineStatus.Online) lowBatteryAlerted = false;
            }
        }

        static string Pct(float f) { return f > 1 ? "desconocida" : (f * 100).ToString("0") + " %"; }

        void OnSession(object s, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock: Activity.Info(Category.Sesion, "Sesión bloqueada"); break;
                case SessionSwitchReason.SessionUnlock: Activity.Info(Category.Sesion, "Sesión desbloqueada"); break;
                case SessionSwitchReason.SessionLogon: Activity.Info(Category.Sesion, "Inicio de sesión"); break;
                case SessionSwitchReason.SessionLogoff: Activity.Info(Category.Sesion, "Cierre de sesión"); break;
                case SessionSwitchReason.RemoteConnect: Activity.Add(Category.Sesion, Severity.Advertencia, "Conexión remota a esta sesión"); break;
                case SessionSwitchReason.RemoteDisconnect: Activity.Info(Category.Sesion, "Se cerró la conexión remota"); break;
                case SessionSwitchReason.ConsoleConnect: Activity.Info(Category.Sesion, "Sesión conectada a la consola"); break;
            }
        }

        void OnDisplay(object s, EventArgs e)
        {
            var screens = Screen.AllScreens;
            Activity.Info(Category.Dispositivo, "Cambió la configuración de pantalla (" + screens.Length + (screens.Length == 1 ? " pantalla, " : " pantallas, ") +
                Screen.PrimaryScreen.Bounds.Width + "×" + Screen.PrimaryScreen.Bounds.Height + ")");
        }

        void OnTime(object s, EventArgs e)
        {
            Activity.Add(Category.Sistema, Severity.Advertencia, "Se cambió la fecha u hora del sistema");
        }

        // ---- Red
        static IEnumerable<NetworkInterface> UpInterfaces()
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
            foreach (var n in all)
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                yield return n;
            }
        }

        static string Describe(NetworkInterface n)
        {
            if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                string ssid = Sampler.CurrentSsid();
                return "Wi-Fi" + (string.IsNullOrEmpty(ssid) ? " (" + n.Name + ")" : " \"" + ssid + "\"");
            }
            return n.Name + " (" + n.Description + ")";
        }

        void OnNetwork(object s, EventArgs e)
        {
            try
            {
                var now = UpInterfaces().ToList();
                var ids = new HashSet<string>(now.Select(n => n.Id));
                foreach (var n in now)
                    if (upNets.Add(n.Id))
                    {
                        string d = Describe(n);
                        netNames[n.Id] = d;
                        string ip = n.GetIPProperties().UnicastAddresses
                            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Select(a => a.Address.ToString()).FirstOrDefault();
                        Activity.Info(Category.Red, "Conectado a " + d + (ip != null ? ", IP " + ip : ""));
                    }
                foreach (var id in upNets.Where(x => !ids.Contains(x)).ToList())
                {
                    upNets.Remove(id);
                    string name;
                    Activity.Info(Category.Red, "Desconectado de " + (netNames.TryGetValue(id, out name) ? name : "una red"));
                }
            }
            catch { }
        }

        void OnAvailability(object s, NetworkAvailabilityEventArgs e)
        {
            Activity.Add(Category.Red, e.IsAvailable ? Severity.Info : Severity.Advertencia,
                e.IsAvailable ? "La red está disponible" : "Sin conexión de red");
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= OnPower;
            SystemEvents.SessionSwitch -= OnSession;
            SystemEvents.DisplaySettingsChanged -= OnDisplay;
            SystemEvents.TimeChanged -= OnTime;
            NetworkChange.NetworkAddressChanged -= OnNetwork;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
            if (systemLog != null) systemLog.Dispose();
            if (appLog != null) appLog.Dispose();
            if (notification != IntPtr.Zero) Native.UnregisterDeviceNotification(notification);
            DestroyHandle();
        }
    }
}
