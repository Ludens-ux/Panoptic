using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>
    /// Avisos de la isla que vienen del sistema: cámara y micrófono en uso, eventos del WriteBlocker, capturas de
    /// pantalla, batería, red e internet, y volumen y brillo.
    /// </summary>
    internal sealed partial class IslandForm
    {
        static readonly Color CameraColor = Color.FromArgb(48, 209, 88);     // verde, como el indicador del iPhone
        static readonly Color MicColor = Color.FromArgb(255, 159, 10);       // naranja

        // ================================================================== 1. Cámara y micrófono
        // Windows anota cada uso en el registro de privacidad: mientras una app usa la cámara o el micrófono,
        // su entrada tiene inicio y no tiene fin (LastUsedTimeStop = 0). Se lee sin permisos.

        bool camOn, micOn;
        readonly HashSet<string> privacySeen = new HashSet<string>();
        readonly Dictionary<string, string> appNames = new Dictionary<string, string>();

        sealed class PrivacyUse { public string Id, Name; }

        void CheckPrivacy()
        {
            var cams = InUse("webcam");
            var mics = InUse("microphone");
            camOn = cams.Count > 0;
            micOn = mics.Count > 0;
            var now = new HashSet<string>();
            foreach (var u in cams) { now.Add(u.Id); NotifyPrivacy(u, true); }
            foreach (var u in mics) { now.Add(u.Id); NotifyPrivacy(u, false); }
            privacySeen.RemoveWhere(id => !now.Contains(id));
        }

        void NotifyPrivacy(PrivacyUse u, bool camera)
        {
            if (!privacySeen.Add(u.Id)) return;
            Enqueue(new Ctx
            {
                Kind = "privacy", Key = "priv" + u.Id, Seconds = 5,
                Glyph = camera ? Glyphs.Camera : Glyphs.Microphone, Accent = camera ? CameraColor : MicColor,
                Title = camera ? "Cámara en uso" : "Micrófono en uso", Subtitle = u.Name
            });
        }

        List<PrivacyUse> InUse(string capability)
        {
            var list = new List<PrivacyUse>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability))
                {
                    if (root == null) return list;
                    ScanUses(root, capability, false, list);
                    using (var np = root.OpenSubKey("NonPackaged")) if (np != null) ScanUses(np, capability, true, list);
                }
            }
            catch { }
            return list;
        }

        void ScanUses(RegistryKey key, string capability, bool nonPackaged, List<PrivacyUse> list)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                if (name == "NonPackaged") continue;
                try
                {
                    using (var k = key.OpenSubKey(name))
                    {
                        if (k == null) continue;
                        object start = k.GetValue("LastUsedTimeStart"), stop = k.GetValue("LastUsedTimeStop");
                        if (!(start is long) || !(stop is long) || (long)start == 0 || (long)stop != 0) continue;
                        list.Add(new PrivacyUse { Id = capability + "|" + name + "|" + start, Name = AppName(name, nonPackaged) });
                    }
                }
                catch { }
            }
        }

        /// <summary>Nombre legible de la app: la descripción del ejecutable, o el nombre del paquete de la Tienda.</summary>
        string AppName(string entry, bool nonPackaged)
        {
            string n;
            if (appNames.TryGetValue(entry, out n)) return n;
            if (nonPackaged)
            {
                string path = entry.Replace('#', '\\');
                try { n = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { n = null; }
                if (string.IsNullOrWhiteSpace(n)) n = Path.GetFileNameWithoutExtension(path);
            }
            else
            {
                // "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Windows Camera"
                n = entry.Split('_')[0];
                int dot = n.LastIndexOf('.');
                if (dot >= 0) n = n.Substring(dot + 1);
                n = System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
            }
            appNames[entry] = n.Trim();
            return appNames[entry];
        }

        /// <summary>Puntos de color en la compacta mientras la cámara (verde) o el micrófono (naranja) están en uso.</summary>
        void PaintPrivacyDots(Graphics g)
        {
            if (Mascot) return;   // con la mascota, el punto va en el ojo
            if (!camOn && !micOn) return;
            var r = AreaRect;
            float d = S(6), x = r.Right - S(8) - d, y = r.Y + S(5);
            if (Side) { x = r.X + r.Width / 2f - d / 2f; y = r.Bottom - S(10); }
            if (micOn) { FillEllipseP(g, new RectangleF(x, y, d, d), MicColor); x -= Side ? 0 : d + S(3); if (Side) y -= d + S(3); }
            if (camOn) FillEllipseP(g, new RectangleF(x, y, d, d), CameraColor);
        }

        // ================================================================== 2. WriteBlocker
        // Panoptic ya importa el registro del WriteBlocker; acá se convierten sus eventos en avisos.

        sealed class PendingWrite { public string Name; public DateTime At; }
        readonly Dictionary<string, PendingWrite> wbPending = new Dictionary<string, PendingWrite>();

        static string DiskId(string desc)
        {
            int p = desc.IndexOf(" (");
            return p > 0 ? desc.Substring(0, p) : desc;
        }

        static string DiskName(string desc)
        {
            int a = desc.IndexOf('('), b = desc.IndexOf(", N/S");
            if (b < 0) b = desc.IndexOf(')', a + 1);
            return a >= 0 && b > a ? desc.Substring(a + 1, b - a - 1) : desc;
        }

        void OnWriteBlocker(ActivityEntry e)
        {
            string m = e.Message;
            string key = "wb" + e.Time.Ticks + m.GetHashCode();
            if (m.StartsWith("Conectado: "))
            {
                string desc = m.Substring("Conectado: ".Length);
                int br = desc.IndexOf(" [");
                string clean = br > 0 ? desc.Substring(0, br) : desc;
                if (m.Contains("[solo lectura]") || m.Contains("[escritura bloqueada"))
                    Enqueue(WbCtx(key, "Disco protegido", DiskName(clean) + " · solo lectura", false));
                else if (m.Contains("[escritura]"))
                    wbPending[DiskId(clean)] = new PendingWrite { Name = DiskName(clean), At = DateTime.Now };   // se espera: puede protegerse solo
                else if (m.Contains("[fuera de línea]"))
                    Enqueue(WbCtx(key, "Disco conectado fuera de línea", DiskName(clean) + " · sin montar", false));
            }
            else if (m.Contains(" -> solo lectura"))
            {
                string desc = m.Substring(0, m.IndexOf(" -> "));
                wbPending.Remove(DiskId(desc));
                Enqueue(WbCtx(key, "Disco protegido", DiskName(desc) + (m.Contains("automático") ? " · automáticamente" : " · solo lectura"), false));
            }
            else if (m.Contains(" -> escritura habilitada"))
            {
                string desc = m.Substring(0, m.IndexOf(" -> "));
                wbPending.Remove(DiskId(desc));
                Enqueue(WbCtx(key, "Escritura habilitada", DiskName(desc), true));
            }
            else if (m.StartsWith("Modo forense: "))
                Enqueue(WbCtx(key, m.Contains("desactivado") ? "Modo forense desactivado" : "Modo forense activado",
                    m.Contains("desactivado") ? "Los discos nuevos pueden recibir escritura" : "Todas las protecciones activas", m.Contains("desactivado")));
            else if (m.Contains("NO protegido"))
                Enqueue(WbCtx(key, "El disco NO está protegido", "Verificación del WriteBlocker", true));
        }

        Ctx WbCtx(string key, string title, string subtitle, bool critical)
        {
            return new Ctx
            {
                Kind = "wb", Key = key, Title = title, Subtitle = subtitle, Critical = critical, Seconds = critical ? 10 : 5,
                Glyph = critical ? Glyphs.Warning : Glyphs.Lock, Accent = critical ? (Color?)null : ResColors.Battery(th)
            };
        }

        /// <summary>Un disco conectado con escritura que el WriteBlocker no protegió en 3 segundos: aviso crítico.</summary>
        void CheckWriteBlockerPending()
        {
            foreach (var kv in wbPending.Where(p => (DateTime.Now - p.Value.At).TotalSeconds >= 3).ToList())
            {
                wbPending.Remove(kv.Key);
                Enqueue(WbCtx("wbw" + kv.Key + kv.Value.At.Ticks, "Disco con escritura habilitada", kv.Value.Name, true));
            }
        }

        // ================================================================== 4. Capturas de pantalla
        // Al copiarse una imagen al portapapeles (Win+Mayús+S, Impr Pant…) se muestra una miniatura. Solo se
        // guarda en disco si se toca "Guardar".

        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        const int WM_CLIPBOARDUPDATE = 0x031D;
        DateTime lastClipboard = DateTime.MinValue;

        void OnClipboardChanged()
        {
            if (!Settings.IslandAlerts || (DateTime.Now - lastClipboard).TotalMilliseconds < 600) return;
            Image img;
            try
            {
                if (!Clipboard.ContainsImage()) return;
                img = Clipboard.GetImage();
            }
            catch { return; }
            if (img == null) return;
            lastClipboard = DateTime.Now;
            var thumb = new Bitmap(S(56), S(34), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float k = Math.Max((float)thumb.Width / img.Width, (float)thumb.Height / img.Height);
                float w = img.Width * k, h = img.Height * k;
                g.DrawImage(img, (thumb.Width - w) / 2, (thumb.Height - h) / 2, w, h);
            }
            var shot = img;
            Enqueue(new Ctx
            {
                Kind = "shot", Key = "shot" + DateTime.Now.Ticks, Icon = thumb, Height = 46, Seconds = 6,
                Title = "Captura copiada", Subtitle = img.Width + " × " + img.Height + " · en el portapapeles",
                Button = "Guardar", Primary = true, ButtonAction = () => SaveShot(shot)
            });
        }

        void SaveShot(Image img)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Capturas de Panoptic");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Captura " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
                img.Save(file, ImageFormat.Png);
                Activity.Info(Category.Programa, "Captura guardada en " + file);
                Enqueue(new Ctx
                {
                    Kind = "info", Key = "saved" + DateTime.Now.Ticks, Glyph = Glyphs.Photo, Seconds = 5,
                    Title = "Captura guardada", Subtitle = "Imágenes\\Capturas de Panoptic",
                    Button = "Abrir", ButtonAction = () => { try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }
                });
            }
            catch (Exception ex) { Activity.Add(Category.Alerta, Severity.Error, "No se pudo guardar la captura: " + ex.Message); }
        }

        // ================================================================== 6. Batería

        [StructLayout(LayoutKind.Sequential)]
        struct SystemPowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag; public int LifeTime, FullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

        float lastBatteryPct = -1;
        bool lastSaver, saverKnown;

        static string Remaining(Snapshot s)
        {
            return s.BatteryMinutes > 0 ? (s.BatteryMinutes / 60 > 0 ? s.BatteryMinutes / 60 + " h " : "") + s.BatteryMinutes % 60 + " min restantes" : "";
        }

        /// <summary>Se llama antes de actualizar el estado del cargador (usa el estado anterior).</summary>
        void CheckBattery(Snapshot snap)
        {
            if (!snap.HasBattery) return;
            float pct = snap.BatteryPercent;
            if (lastBatteryPct >= 0)
            {
                if (snap.OnAc && pct >= 99.5f && lastBatteryPct < 99.5f)
                    Enqueue(new Ctx { Kind = "info", Key = "batfull", Glyph = Glyphs.Battery, Accent = ResColors.Battery(th), Seconds = 6,
                        Title = "Batería cargada", Subtitle = "100 % · ya podés desenchufar" });
                int low = Settings.BatteryLow;
                if (!snap.OnAc && pct <= low && lastBatteryPct > low)
                    Enqueue(new Ctx { Kind = "info", Key = "batlow", Glyph = Glyphs.Battery, Critical = true, Seconds = 10,
                        Title = "Batería baja · " + pct.ToString("0") + " %", Subtitle = Remaining(snap).Length > 0 ? Remaining(snap) : "Conectá el cargador",
                        Button = "Ahorro", ButtonAction = () => { try { Process.Start("ms-settings:batterysaver"); } catch { } } });
            }
            if (lastOnAcKnown && lastOnAc && !snap.OnAc)
                Enqueue(new Ctx { Kind = "info", Key = "unplug", Glyph = Glyphs.Battery, Accent = ResColors.Battery(th), Seconds = 4,
                    Title = "Con batería · " + pct.ToString("0") + " %", Subtitle = Remaining(snap).Length > 0 ? Remaining(snap) : "Cargador desconectado" });
            lastBatteryPct = pct;

            SystemPowerStatus ps;
            if (GetSystemPowerStatus(out ps))
            {
                bool saver = (ps.SystemStatusFlag & 1) != 0;
                if (saverKnown && saver && !lastSaver)
                    Enqueue(new Ctx { Kind = "info", Key = "saver", Glyph = Glyphs.Battery, Accent = th.Caution, Seconds = 4,
                        Title = "Ahorro de batería activado", Subtitle = "Se reduce el brillo y la actividad en segundo plano" });
                lastSaver = saver; saverKnown = true;
            }
        }

        // ================================================================== 7. Red, VPN e internet

        static readonly string[] VpnWords = { "vpn", "wireguard", "openvpn", "tap-windows", "fortinet", "anyconnect", "globalprotect", "nordlynx", "tailscale", "zerotier", "pulse secure", "softether", "proton" };

        static bool IsVpn(string description)
        {
            string l = description.ToLowerInvariant();
            return VpnWords.Any(w => l.Contains(w));
        }

        void OnNetworkActivity(string m)
        {
            if (m.StartsWith("Conectado a "))
            {
                string what = m.Substring("Conectado a ".Length);
                int ip = what.IndexOf(", IP ");
                string ipText = ip > 0 ? what.Substring(ip + 2) : "";
                if (ip > 0) what = what.Substring(0, ip);
                bool vpn = IsVpn(what), wifi = what.StartsWith("Wi-Fi");
                Enqueue(new Ctx
                {
                    Kind = "info", Key = "net" + what + DateTime.Now.Ticks, Seconds = 4,
                    Glyph = vpn ? Glyphs.Vpn : wifi ? Glyphs.Wifi : Glyphs.Ethernet, Accent = vpn ? (Color?)th.Accent : ResColors.Net(th),
                    Title = vpn ? "VPN conectada" : "Conectado", Subtitle = (vpn ? NetName(what) : what.Replace("\"", "")) + (ipText.Length > 0 && !vpn ? " · " + ipText : "")
                });
            }
            else if (m.StartsWith("Desconectado de "))
            {
                string what = m.Substring("Desconectado de ".Length);
                bool vpn = IsVpn(what);
                Enqueue(new Ctx
                {
                    Kind = "info", Key = "netoff" + what + DateTime.Now.Ticks, Seconds = 4,
                    Glyph = vpn ? Glyphs.Vpn : what.StartsWith("Wi-Fi") ? Glyphs.Wifi : Glyphs.Ethernet, Accent = vpn ? (Color?)th.Caution : null,
                    Title = vpn ? "VPN desconectada" : "Desconectado", Subtitle = vpn ? NetName(what) : what.Replace("\"", "")
                });
            }
        }

        /// <summary>"Conexión VPN (WireGuard Tunnel)" → "WireGuard Tunnel".</summary>
        static string NetName(string what)
        {
            int a = what.IndexOf('('), b = what.LastIndexOf(')');
            return a >= 0 && b > a ? what.Substring(a + 1, b - a - 1) : what;
        }

        // Internet: con una red conectada pero sin salida (por ejemplo, Wi-Fi sin servicio o un portal cautivo).
        object networkList;
        int netTick, offlineChecks;
        bool offlineNotified;

        void CheckInternet(Snapshot snap)
        {
            if (++netTick % 3 != 0) return;   // cada 3 segundos
            bool hasNetwork = snap.Nets.Count > 0;
            bool online = true;
            try
            {
                if (networkList == null) networkList = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
                online = ((dynamic)networkList).IsConnectedToInternet;
            }
            catch { return; }
            if (hasNetwork && !online)
            {
                if (++offlineChecks == 2 && !offlineNotified)   // 6 s seguidos: no es un corte de un instante
                {
                    offlineNotified = true;
                    var n = snap.Nets.OrderByDescending(x => x.RecvBps + x.SendBps).FirstOrDefault();
                    Enqueue(new Ctx { Kind = "info", Key = "offline" + DateTime.Now.Ticks, Glyph = Glyphs.Globe, Critical = true, Seconds = 8,
                        Title = "Sin internet", Subtitle = (n != null ? (n.Kind == "Wi-Fi" && !string.IsNullOrEmpty(n.Ssid) ? "Wi-Fi \"" + n.Ssid + "\"" : n.Kind) + " conectado, " : "") + "pero sin salida" });
                }
            }
            else
            {
                if (online && offlineNotified)
                    Enqueue(new Ctx { Kind = "info", Key = "online" + DateTime.Now.Ticks, Glyph = Glyphs.Globe, Accent = ResColors.Net(th), Seconds = 4,
                        Title = "Internet restablecido", Subtitle = "La conexión volvió" });
                offlineChecks = 0;
                offlineNotified = false;
            }
        }

        // ================================================================== 8. Volumen y brillo

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out uint count);
            int SetMasterVolumeLevel(float level, IntPtr context);
            int SetMasterVolumeLevelScalar(float level, IntPtr context);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channel, float level, IntPtr context);
            int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            int GetChannelVolumeLevel(uint channel, out float level);
            int GetChannelVolumeLevelScalar(uint channel, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        readonly Timer volumeTimer = new Timer { Interval = 150 };
        IAudioEndpointVolume endpoint;
        DateTime endpointAt = DateTime.MinValue;
        float lastVolume = -1;
        bool lastMute;
        ManagementEventWatcher brightnessWatcher;

        void StartLevels()
        {
            volumeTimer.Tick -= OnVolumeTick;
            volumeTimer.Tick += OnVolumeTick;
            volumeTimer.Start();
            try
            {
                if (brightnessWatcher == null)
                {
                    brightnessWatcher = new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    brightnessWatcher.EventArrived += (o, e) =>
                    {
                        try
                        {
                            int level = Convert.ToInt32(e.NewEvent["Brightness"]);
                            BeginInvoke(new Action(() => ShowLevel("brightness", Glyphs.Brightness, "Brillo", level, level + " %")));
                        }
                        catch { }
                    };
                }
                brightnessWatcher.Start();
            }
            catch { brightnessWatcher = null; }   // pantallas sin control de brillo (monitores externos)
        }

        void StopLevels()
        {
            volumeTimer.Stop();
            try { if (brightnessWatcher != null) brightnessWatcher.Stop(); } catch { }
        }

        void OnVolumeTick(object sender, EventArgs e)
        {
            try
            {
                // El dispositivo de salida puede cambiar (auriculares): se vuelve a pedir cada 5 segundos.
                if (endpoint == null || (DateTime.Now - endpointAt).TotalSeconds > 5)
                {
                    endpointAt = DateTime.Now;
                    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                    IMMDevice dev;
                    if (en.GetDefaultAudioEndpoint(0 /* salida */, 1 /* multimedia */, out dev) != 0 || dev == null) { endpoint = null; return; }
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    object o;
                    dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o);
                    endpoint = (IAudioEndpointVolume)o;
                }
                float v; bool mute;
                endpoint.GetMasterVolumeLevelScalar(out v);
                endpoint.GetMute(out mute);
                if (lastVolume >= 0 && (Math.Abs(v - lastVolume) > 0.004f || mute != lastMute))
                    ShowLevel("volume", mute || v < 0.005f ? Glyphs.Mute : Glyphs.Volume, mute ? "Silenciado" : "Volumen", mute ? 0 : v * 100, mute ? "" : (v * 100).ToString("0") + " %");
                lastVolume = v; lastMute = mute;
            }
            catch { endpoint = null; }
        }

        /// <summary>Volumen o brillo: aparece enseguida (sin esperar la cola) y se actualiza mientras se mueve.</summary>
        void ShowLevel(string key, string glyph, string title, float level, string value)
        {
            if (!Settings.IslandAlerts || expanded || !islandOn) return;
            if (active != null && active.Kind == "level" && active.Key == key)
            {
                active.Glyph = glyph; active.Title = title; active.Level = level; active.Value = value;
                active.Until = DateTime.Now.AddSeconds(1.6);
                Invalidate();
                return;
            }
            queue.RemoveAll(q => q.Kind == "level");
            var c = new Ctx { Kind = "level", Key = key, Glyph = glyph, Title = title, Level = level, Value = value, Height = 38, Seconds = 2 };
            if (active != null && !active.Critical) { queue.Insert(0, active); active = null; }
            queue.Insert(active != null ? Math.Min(1, queue.Count) : 0, c);
            NextContext();
        }

        void PaintLevel(Graphics g, Ctx c)
        {
            var r = AreaRect;
            int x = r.X + S(16), cy = r.Y + r.Height / 2;
            Glyph(g, c.Glyph, fonts.Glyph16, new Rectangle(x, cy - S(9), S(18), S(18)), th.Text);
            x += S(18) + S(10);
            int tw = MeasureText(c.Title, fValue13) + S(12);
            DrawText(g, c.Title, fValue13, new Rectangle(x, r.Y, tw, r.Height), th.Text);
            x += tw;
            int vw = MeasureText("100 %", fCaption12) + S(4);
            var bar = new Rectangle(x, cy - S(2), r.Right - S(16) - vw - S(10) - x, S(4));
            Bar(g, bar, c.Level / 100f, th.Accent);
            DrawText(g, c.Value, fCaption12, new Rectangle(r.Right - S(16) - vw, r.Y, vw, r.Height), Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            AddHit("ctx", r, null, c.Title + " " + c.Value).Focusable = false;
        }

        void PaintShot(Graphics g, Ctx c)
        {
            var r = AreaRect;
            int x = r.X + S(10), cy = r.Y + r.Height / 2;
            if (c.Icon != null)
            {
                var ir = new Rectangle(x, cy - c.Icon.Height / 2, c.Icon.Width, c.Icon.Height);
                DrawIcon(g, c.Icon, ir);
                StrokeRoundP(g, new RectangleF(ir.X + .5f, ir.Y + .5f, ir.Width - 1, ir.Height - 1), S(3), th.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0), 1f);
                x = ir.Right + S(12);
            }
            TwoLines(g, c, x, r);
            ContextButton(g, c, r);
        }

        // ================================================================== enganche con la isla

        /// <summary>Cada segundo, con los datos nuevos.</summary>
        void CheckSystemEvents(Snapshot snap)
        {
            if (!Settings.IslandAlerts) return;
            CheckPrivacy();
            CheckWriteBlockerPending();
            CheckBattery(snap);
            CheckInternet(snap);
        }
    }
}
