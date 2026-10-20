using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Panoptic
{
    // ==================================================================== Historial de USB

    internal sealed class UsbDevice
    {
        public string Id, Name, Kind, Vendor, Product, Serial, VidPid, Manufacturer;
        public bool RealSerial, Present;
        public DateTime? FirstInstall, LastArrival, LastRemoval;
    }

    /// <summary>
    /// Todos los dispositivos de almacenamiento USB y los teléfonos o cámaras (MTP) que se conectaron alguna vez.
    /// Windows guarda cada uno con su primera conexión, la última y la última desconexión, aunque hoy no esté.
    /// </summary>
    internal static class UsbHistory
    {
        static readonly Guid DiskDrives = new Guid("4d36e967-e325-11ce-bfc1-08002be10318");
        static readonly Guid PortableDevices = new Guid("eec5ad98-8080-425f-922a-dabf3de3f69a");

        public static List<UsbDevice> Scan()
        {
            var list = new List<UsbDevice>();
            Collect(DiskDrives, "Almacenamiento", list);
            Collect(PortableDevices, "Teléfono o cámara", list);
            return list.OrderByDescending(d => d.LastArrival ?? d.FirstInstall ?? DateTime.MinValue).ToList();
        }

        static void Collect(Guid cls, string kind, List<UsbDevice> list)
        {
            // Sin DIGCF_PRESENT: también los que ya no están conectados.
            IntPtr set = SetupDiGetClassDevs(ref cls, null, IntPtr.Zero, 0);
            if (set == new IntPtr(-1)) return;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var sb = new StringBuilder(512);
                    uint req;
                    if (!SetupDiGetDeviceInstanceId(set, ref data, sb, (uint)sb.Capacity, out req)) continue;
                    string id = sb.ToString();
                    string parent = PropString(set, ref data, Parent) ?? "";
                    bool usb = id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                               parent.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
                    // Los volúmenes de un pendrive también aparecen como dispositivos portátiles (SWD\WPDBUSENUM): se omiten.
                    if (!usb || id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) continue;

                    var d = new UsbDevice { Id = id, Kind = kind };
                    d.Name = PropString(set, ref data, FriendlyName) ?? PropString(set, ref data, BusReportedDesc) ?? PropString(set, ref data, DeviceDesc) ?? id;
                    d.Manufacturer = PropString(set, ref data, Manufacturer);
                    d.FirstInstall = PropDate(set, ref data, FirstInstallDate);
                    d.LastArrival = PropDate(set, ref data, LastArrivalDate);
                    d.LastRemoval = PropDate(set, ref data, LastRemovalDate);
                    d.Present = PropBool(set, ref data, IsPresent);
                    ParseIds(d, id, parent);
                    list.Add(d);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        /// <summary>Marca, modelo y número de serie desde los identificadores (USBSTOR\DISK&VEN_x&PROD_y&REV_z\SERIE&0).</summary>
        static void ParseIds(UsbDevice d, string id, string parent)
        {
            var parts = id.Split('\\');
            if (id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                foreach (var t in parts[1].Split('&'))
                {
                    if (t.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase)) d.Vendor = Clean(t.Substring(4));
                    if (t.StartsWith("PROD_", StringComparison.OrdinalIgnoreCase)) d.Product = Clean(t.Substring(5));
                }
                d.Serial = parts[2];
                int amp = d.Serial.LastIndexOf('&');
                if (amp > 0 && d.Serial.Length - amp <= 3) d.Serial = d.Serial.Substring(0, amp);   // "&0" = número de LUN
            }
            // El dispositivo USB "padre" tiene el fabricante (VID), el producto (PID) y el número de serie real.
            string usb = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ? id : parent.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ? parent : null;
            // Dispositivos compuestos (teléfonos): la interfaz (…&MI_00) cuelga del dispositivo real.
            if (usb != null && usb.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) > 0)
            {
                string up2 = ParentOf(usb);
                if (up2 != null && up2.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) usb = up2;
            }
            if (usb != null)
            {
                var up = usb.Split('\\');
                if (up.Length >= 3)
                {
                    string ids = up[1];
                    int mi = ids.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
                    if (mi > 0) ids = ids.Substring(0, mi);
                    d.VidPid = ids.Replace("VID_", "").Replace("PID_", "").Replace("&", ":");
                    if (d.Serial == null || IsGenerated(d.Serial)) d.Serial = up[2];
                }
            }
            // Los discos USB rápidos (UAS) llevan "MSFT30" delante del número de serie.
            if (d.Serial != null && d.Serial.StartsWith("MSFT30", StringComparison.OrdinalIgnoreCase) && d.Serial.Length > 6) { string sn = d.Serial.Substring(6), trimmed = sn.TrimStart('0'); d.Serial = trimmed.Length > 0 ? trimmed : sn; } // Windows completa con ceros adelante
            // Windows inventa un número cuando el dispositivo no tiene uno propio (segundo carácter '&').
            d.RealSerial = d.Serial != null && !IsGenerated(d.Serial);
            // Nombre sin el sufijo que agrega Windows.
            foreach (var suffix in new[] { " SCSI Disk Device", " USB Device" })
                if (d.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) d.Name = d.Name.Substring(0, d.Name.Length - suffix.Length);
        }

        /// <summary>Identificador del dispositivo padre (aunque no esté conectado).</summary>
        static string ParentOf(string instanceId)
        {
            uint dev, parent;
            if (CM_Locate_DevNode(out dev, instanceId, 1 /* CM_LOCATE_DEVNODE_PHANTOM */) != 0) return null;
            if (CM_Get_Parent(out parent, dev, 0) != 0) return null;
            var sb = new StringBuilder(512);
            return CM_Get_Device_ID(parent, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
        static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
        static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, int length, uint flags);

        static bool IsGenerated(string serial) { return serial.Length > 1 && serial[1] == '&'; }

        static string Clean(string s) { return s.Replace('_', ' ').Trim(); }

        // ---- Propiedades del dispositivo (SetupAPI)
        static readonly DEVPROPKEY FriendlyName = Key("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly DEVPROPKEY DeviceDesc = Key("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        static readonly DEVPROPKEY Manufacturer = Key("a45c254e-df1c-4efd-8020-67d146a850e0", 13);
        static readonly DEVPROPKEY BusReportedDesc = Key("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
        static readonly DEVPROPKEY IsPresent = Key("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 5);
        static readonly DEVPROPKEY Parent = Key("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);
        static readonly DEVPROPKEY FirstInstallDate = Key("83da6326-97a6-4088-9453-a1923f573b29", 101);
        static readonly DEVPROPKEY LastArrivalDate = Key("83da6326-97a6-4088-9453-a1923f573b29", 102);
        static readonly DEVPROPKEY LastRemovalDate = Key("83da6326-97a6-4088-9453-a1923f573b29", 103);

        static DEVPROPKEY Key(string g, uint pid) { return new DEVPROPKEY { fmtid = new Guid(g), pid = pid }; }

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key, out uint type)
        {
            var buf = new byte[1024];
            uint req;
            if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type, buf, (uint)buf.Length, out req, 0)) return null;
            Array.Resize(ref buf, (int)req);
            return buf;
        }

        static string PropString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            uint type;
            var b = Prop(set, ref data, key, out type);
            if (b == null || type != 0x12) return null; // DEVPROP_TYPE_STRING
            string s = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
            return s.Length > 0 ? s : null;
        }

        static DateTime? PropDate(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            uint type;
            var b = Prop(set, ref data, key, out type);
            if (b == null || type != 0x10 || b.Length < 8) return null; // DEVPROP_TYPE_FILETIME
            long ft = BitConverter.ToInt64(b, 0);
            try { return ft > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime() : (DateTime?)null; } catch { return null; }
        }

        static bool PropBool(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            uint type;
            var b = Prop(set, ref data, key, out type);
            return b != null && type == 0x11 && b.Length > 0 && b[0] != 0; // DEVPROP_TYPE_BOOLEAN
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        static extern IntPtr SetupDiGetClassDevs(ref Guid cls, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, uint size, out uint required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW")]
        static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }

    // ==================================================================== Encendidos, apagados y fallas

    internal enum PowerKind { Encendido, Apagado, Reinicio, ApagadoInesperado, PantallazoAzul, Suspension, Reanudacion }

    internal sealed class PowerEvent
    {
        public DateTime Time;
        public PowerKind Kind;
        public string Detail = "";
        public TimeSpan? Duration;   // en un apagado: cuánto estuvo encendida; en una reanudación: cuánto estuvo suspendida
        public bool Requested;       // pedido de apagado o reinicio (se une con el apagado real)
        public bool Failure { get { return Kind == PowerKind.ApagadoInesperado || Kind == PowerKind.PantallazoAzul; } }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case PowerKind.Encendido: return "Encendido";
                    case PowerKind.Apagado: return "Apagado";
                    case PowerKind.Reinicio: return "Reinicio";
                    case PowerKind.ApagadoInesperado: return "Apagado inesperado";
                    case PowerKind.PantallazoAzul: return "Pantallazo azul";
                    case PowerKind.Suspension: return "Suspensión";
                    default: return "Reanudación";
                }
            }
        }
    }

    /// <summary>Línea de tiempo de encendidos, apagados, suspensiones y fallas desde el registro "Sistema" de Windows.</summary>
    internal static class PowerHistory
    {
        public static List<PowerEvent> Scan(int days)
        {
            long ms = (long)days * 24 * 3600 * 1000;
            string xpath =
                "*[System[TimeCreated[timediff(@SystemTime) <= " + ms + "] and (" +
                "(Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13)) or " +
                "(Provider[@Name='EventLog'] and EventID=6008) or " +
                "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=42 or EventID=107 or EventID=506 or EventID=507)) or " +
                "(Provider[@Name='User32'] and EventID=1074) or " +
                "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001))]]";
            var raw = new List<PowerEvent>();
            using (var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath)))
            {
                EventRecord r;
                while ((r = reader.ReadEvent()) != null)
                    using (r)
                    {
                        var e = Parse(r);
                        if (e != null) raw.Add(e);
                    }
            }
            return Pair(raw.OrderBy(e => e.Time).ToList());
        }

        static PowerEvent Parse(EventRecord r)
        {
            if (!r.TimeCreated.HasValue) return null;
            var e = new PowerEvent { Time = r.TimeCreated.Value };
            var p = r.Properties;
            Func<int, string> P = i => i < p.Count && p[i].Value != null ? p[i].Value.ToString() : "";
            switch (r.ProviderName)
            {
                case "Microsoft-Windows-Kernel-General":
                    e.Kind = r.Id == 12 ? PowerKind.Encendido : PowerKind.Apagado;
                    break;
                case "EventLog":
                    e.Kind = PowerKind.ApagadoInesperado;
                    e.Detail = "Windows no se apagó correctamente la vez anterior (corte de luz, batería agotada o botón de encendido)";
                    break;
                case "Microsoft-Windows-Kernel-Power":
                    if (r.Id == 41)
                    {
                        long code;
                        long.TryParse(P(0), out code);
                        if (code != 0)
                        {
                            e.Kind = PowerKind.PantallazoAzul;
                            e.Detail = "Código de error 0x" + code.ToString("X8") + BugcheckName(code);
                        }
                        else
                        {
                            e.Kind = PowerKind.ApagadoInesperado;
                            e.Detail = "Se reinició sin apagarse bien: dejó de responder, se cortó la energía o se mantuvo apretado el botón";
                        }
                    }
                    else if (r.Id == 42 || r.Id == 506) { e.Kind = PowerKind.Suspension; if (r.Id == 506) e.Detail = "Inactividad (pantalla apagada)"; }
                    else { e.Kind = PowerKind.Reanudacion; if (r.Id == 507) e.Detail = "Salió de la inactividad"; }
                    break;
                case "User32":
                {
                    // Proceso, equipo, motivo, código, tipo (apagar / reiniciar), comentario, usuario.
                    string proc = P(0), reason = P(2), type = P(4).ToLowerInvariant(), user = P(6);
                    int paren = proc.IndexOf(" (");
                    if (paren > 0) proc = proc.Substring(0, paren);
                    proc = System.IO.Path.GetFileName(proc);
                    e.Kind = type.Contains("rein") || type.Contains("restart") ? PowerKind.Reinicio : PowerKind.Apagado;
                    int slash = user.LastIndexOf('\\');
                    string who = slash >= 0 ? user.Substring(slash + 1) : user;
                    bool system = user.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) || who.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);
                    string why = Reason(reason);
                    e.Detail = Origin(proc) + (who.Length > 0 && !system ? " · usuario " + who : "") + (why.Length > 0 ? " · " + why : "");
                    e.Requested = true;
                    break;
                }
                case "Microsoft-Windows-WER-SystemErrorReporting":
                {
                    e.Kind = PowerKind.PantallazoAzul;
                    string text = P(0);
                    e.Detail = "Informe del error: " + (text.Length > 0 ? text : "sin detalle") + (P(1).Length > 0 ? " · volcado en " + P(1) : "");
                    break;
                }
                default: return null;
            }
            return e;
        }

        /// <summary>Une los pedidos de apagado con el apagado real y calcula cuánto duró cada sesión y cada suspensión.</summary>
        static List<PowerEvent> Pair(List<PowerEvent> events)
        {
            var result = new List<PowerEvent>();
            DateTime? on = null, sleep = null;
            PowerEvent request = null;
            foreach (var e in events)
            {
                if (e.Requested) { request = e; continue; }
                switch (e.Kind)
                {
                    case PowerKind.Encendido:
                        // Apagado sin pedido seguido de un encendido al instante: fue un reinicio.
                        var last = result.LastOrDefault();
                        if (last != null && last.Kind == PowerKind.Apagado && (e.Time - last.Time).TotalMinutes <= 2)
                        {
                            last.Kind = PowerKind.Reinicio;
                            if (last.Detail.Length == 0) last.Detail = "Reinicio de Windows";
                        }
                        on = e.Time; sleep = null;
                        break;
                    case PowerKind.Apagado:
                        // Si hubo un pedido en los minutos anteriores, el apagado lleva su motivo y su tipo.
                        if (request != null && (e.Time - request.Time).TotalMinutes <= 10) { e.Kind = request.Kind; e.Detail = request.Detail; }
                        if (on.HasValue) e.Duration = e.Time - on.Value;
                        on = null; request = null;
                        break;
                    case PowerKind.ApagadoInesperado:
                    case PowerKind.PantallazoAzul:
                        // Kernel-Power 41 y el aviso 6008 describen la misma caída: se deja una sola fila.
                        var prev = result.LastOrDefault(x => x.Failure && Math.Abs((x.Time - e.Time).TotalMinutes) <= 5);
                        if (prev != null)
                        {
                            if (e.Kind == PowerKind.PantallazoAzul && prev.Kind != PowerKind.PantallazoAzul) { prev.Kind = e.Kind; prev.Detail = e.Detail; }
                            else if (e.Kind == PowerKind.PantallazoAzul && e.Detail.StartsWith("Informe")) prev.Detail += " · " + e.Detail;
                            continue;
                        }
                        break;
                    case PowerKind.Suspension:
                        if (sleep.HasValue) continue;   // varias entradas seguidas de inactividad: se toma la primera
                        sleep = e.Time;
                        break;
                    case PowerKind.Reanudacion:
                        if (!sleep.HasValue) continue;
                        e.Duration = e.Time - sleep.Value;
                        sleep = null;
                        break;
                }
                result.Add(e);
            }
            return result;
        }

        /// <summary>Quién pidió el apagado o el reinicio, en palabras.</summary>
        static string Origin(string exe)
        {
            switch (exe.ToLowerInvariant())
            {
                case "startmenuexperiencehost.exe": return "Desde el menú Inicio";
                case "explorer.exe": return "Desde el escritorio (Alt+F4 o menú)";
                case "trustedinstaller.exe": return "Por una actualización de Windows";
                case "musnotificationux.exe": case "musnotifyicon.exe": case "usoclient.exe": case "mousocoreworker.exe": return "Por Windows Update";
                case "winlogon.exe": return "Por Windows (inicio de sesión)";
                case "shutdown.exe": return "Con el comando shutdown";
                case "logonui.exe": return "Desde la pantalla de bloqueo";
                case "wininit.exe": case "csrss.exe": return "Por Windows";
                default: return "Pedido por " + exe;
            }
        }

        /// <summary>Motivo del apagado (Windows lo registra en inglés).</summary>
        static string Reason(string r)
        {
            if (string.IsNullOrEmpty(r) || r.StartsWith("No title", StringComparison.OrdinalIgnoreCase)) return "";
            string planned = r.EndsWith("(Planned)") ? " (planeado)" : r.EndsWith("(Unplanned)") ? "" : "";
            string b = r.Replace("(Planned)", "").Replace("(Unplanned)", "").Trim();
            switch (b)
            {
                case "Other": return "";
                case "Operating System: Upgrade": return "Actualización del sistema" + planned;
                case "Operating System: Service pack": return "Paquete de servicio" + planned;
                case "Operating System: Reconfiguration": return "Cambio de configuración" + planned;
                case "Operating System: Recovery": return "Recuperación del sistema" + planned;
                case "Application: Installation": return "Instalación de un programa" + planned;
                case "Application: Maintenance": return "Mantenimiento de un programa" + planned;
                case "Hardware: Installation": return "Instalación de hardware" + planned;
                case "Hardware: Maintenance": return "Mantenimiento de hardware" + planned;
                case "Power Failure: Cord Unplugged": return "Se desenchufó";
                case "Power Failure: Environment": return "Falla de energía";
                default: return b + planned;
            }
        }

        static string BugcheckName(long code)
        {
            switch (code)
            {
                case 0x0A: return " (IRQL_NOT_LESS_OR_EQUAL)";
                case 0x1E: return " (KMODE_EXCEPTION_NOT_HANDLED)";
                case 0x3B: return " (SYSTEM_SERVICE_EXCEPTION)";
                case 0x50: return " (PAGE_FAULT_IN_NONPAGED_AREA)";
                case 0x7E: return " (SYSTEM_THREAD_EXCEPTION_NOT_HANDLED)";
                case 0x9F: return " (DRIVER_POWER_STATE_FAILURE)";
                case 0xA0: return " (INTERNAL_POWER_ERROR)";
                case 0xD1: return " (DRIVER_IRQL_NOT_LESS_OR_EQUAL)";
                case 0xEF: return " (CRITICAL_PROCESS_DIED)";
                case 0x116: return " (VIDEO_TDR_FAILURE)";
                case 0x124: return " (WHEA_UNCORRECTABLE_ERROR: falla de hardware)";
                case 0x133: return " (DPC_WATCHDOG_VIOLATION)";
                case 0x139: return " (KERNEL_SECURITY_CHECK_FAILURE)";
                case 0x154: return " (UNEXPECTED_STORE_EXCEPTION)";
                case 0x1A: return " (MEMORY_MANAGEMENT)";
                case 0xC2: return " (BAD_POOL_CALLER)";
                default: return "";
            }
        }
    }
}
