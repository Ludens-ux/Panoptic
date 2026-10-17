using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>Aplicación de bandeja: ícono con el uso de CPU en vivo, panel, ventana principal y widget.</summary>
    internal sealed class App : ApplicationContext, IHost
    {
        const string SettingsKey = @"Software\Panoptic";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Panoptic";

        readonly Sampler sampler;
        readonly HistoryStore history = new HistoryStore();
        SecurityWatch security;
        readonly SystemWatcher watcher;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly TrayFlyout flyout;
        MainWindow main;
        WidgetForm widget;
        IslandForm island;
        /// <summary>Estilo que está en pantalla (para detectar el cambio de Widget a Isla y viceversa).</summary>
        string shownStyle;
        readonly System.Windows.Forms.Timer iconTimer = new System.Windows.Forms.Timer();
        readonly RegisteredWaitHandle showWait;
        IntPtr iconHandle;
        Theme trayTheme;
        string iconSignature;

        public App(EventWaitHandle showEvent, bool startInTray)
        {
            Activity.LoadPrevious(1000);
            sampler = new Sampler();
            flyout = new TrayFlyout(this);
            flyout.CreateControl();
            IntPtr force = flyout.Handle; // instala el contexto de sincronización de WinForms

            watcher = new SystemWatcher(sampler);
            watcher.DiskChanged += sampler.InvalidateDisks;
            sampler.Sampled += history.Add;
            security = new SecurityWatch();
            if (!ProfileMode) helper = new HelperClient();
            watcher.HotkeyPressed += () => { if (main != null && main.Visible && Form.ActiveForm == main) main.Hide(); else OpenMain(null); };
            if (!ProfileMode) ApplyHotkey();
            Activity.Added += OnActivity;
            var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            Activity.Info(Category.Monitor, AppInfo.Name + " " + AppInfo.Version + " iniciado. Windows está encendido desde hace " + Format.Duration(up) + ".");
            ReactivateHelperAfterRename();
            if (!ProfileMode) Activity.StartRetention();

            tray.MouseClick += OnTrayClick;
            tray.MouseDown += (o, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var old = tray.ContextMenuStrip;
                tray.ContextMenuStrip = Menus.Settings(Theme.Load(), Native.ScaleForPoint(Cursor.Position), this, true);
                if (old != null) old.Dispose();
            };
            UpdateTrayIcon();
            tray.Visible = true;
            iconTimer.Interval = 2000;
            iconTimer.Tick += (o, e) => UpdateTrayIcon();
            iconTimer.Start();

            SystemEvents.UserPreferenceChanged += OnPreferences;
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (o, t) => flyout.BeginInvoke(new Action(() => OpenMain(null))), null, -1, false);

            // Primera ejecución: iniciar con Windows por defecto.
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (k.GetValue("AutostartConfigured") == null)
                {
                    SetAutostart(true);
                    k.SetValue("AutostartConfigured", 1, RegistryValueKind.DWord);
                }
                else if (Autostart && !ProfileMode) SetAutostart(true); // actualizar la ruta si el programa se movió
                if ((k.GetValue("WidgetVisible") as int? ?? 0) == 1) ShowWidget();
            }
            if (!startInTray) OpenMain(null);
            string profPage = Environment.GetEnvironmentVariable("PANOPTIC_PROFILE_PAGE");
            if (ProfileMode && !string.IsNullOrEmpty(profPage)) OpenMain(profPage);
        }

        /// <summary>Avisos de Windows para las alertas y los cambios de seguridad (si están activados).</summary>
        void OnActivity(ActivityEntry e)
        {
            if (!Settings.AlertNotify || e.Severity == Severity.Info) return;
            if (e.Category != Category.Alerta && e.Category != Category.Inicio && e.Category != Category.Servicio && e.Category != Category.Red && e.Category != Category.Seguridad) return;
            if ((DateTime.Now - e.Time).TotalMinutes > 2) return; // no avisar eventos importados viejos
            string title = e.Category == Category.Alerta ? "Alerta" : e.Category == Category.Inicio ? "Programa de inicio nuevo"
                : e.Category == Category.Servicio ? "Cambio en los servicios" : e.Category == Category.Seguridad ? "Seguridad" : "Red";
            try { flyout.BeginInvoke(new Action(() => tray.ShowBalloonTip(6000, title, e.Message.Length > 240 ? e.Message.Substring(0, 240) + "…" : e.Message, ToolTipIcon.Warning))); } catch { }
        }

        // ------------------------------------------------------------------ IHost

        /// <summary>Modo de medición (variable PANOPTIC_PROFILE): no modifica la configuración ni los archivos del usuario.</summary>
        public static bool ProfileMode;

        public Snapshot Latest { get { return sampler.Latest; } }
        public HistoryStore History { get { return history; } }
        HelperClient helper;
        public HelperClient Helper { get { return helper; } }

        public void ApplyHotkey()
        {
            if (!watcher.SetHotkey(Settings.Hotkey))
                Activity.Add(Category.Monitor, Severity.Advertencia, "El atajo " + Settings.HotkeyNames[Settings.Hotkey] + " ya lo usa otro programa; elegí otro en Configuración.");
        }

        /// <summary>
        /// Si la versión anterior (Monitor del sistema) tenía activadas las funciones de administrador, se vuelven a
        /// instalar con el nombre nuevo: pide permiso una sola vez y borra la tarea vieja.
        /// </summary>
        void ReactivateHelperAfterRename()
        {
            if (helper == null || !Migration.HelperWasInstalled || helper.Installed) return;
            System.Threading.ThreadPool.QueueUserWorkItem(o =>
            {
                System.Threading.Thread.Sleep(3000);
                string err;
                if (helper.SetInstalled(true, out err))
                    Activity.Info(Category.Seguridad, "Funciones de administrador reactivadas con el nombre nuevo (" + AppInfo.Name + ")");
                else
                    Activity.Add(Category.Alerta, Severity.Advertencia, "Las funciones de administrador quedaron desactivadas tras el cambio de nombre: activalas en Configuración. " + err);
            });
        }

        public void WidgetChanged()
        {
            UpdateTrayIcon();
            if (!WidgetVisible) return;
            // Cambio de estilo: se oculta el anterior y se muestra el nuevo, sin reiniciar.
            if (shownStyle != Settings.WidgetStyle) { HideAllWidgets(); ShowWidget(); return; }
            if (Settings.IsIsland) island.Relayout(); else widget.Relayout();
        }

        public bool WidgetVisible
        {
            get { return (widget != null && !widget.IsDisposed && widget.Visible) || (island != null && !island.IsDisposed && (island.Visible || shownStyle == "Isla")); }
        }

        void HideAllWidgets()
        {
            if (widget != null && !widget.IsDisposed) widget.HideWidget();
            if (island != null && !island.IsDisposed) island.HideIsland();
            shownStyle = null;
        }

        public void OpenProcess(int pid)
        {
            OpenMain("procs");
            main.SelectProcess(pid);
        }

        public bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
        }

        void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue(RunName, false);
            }
        }

        public void ToggleAutostart()
        {
            bool on = !Autostart;
            SetAutostart(on);
            Activity.Info(Category.Monitor, "Iniciar con Windows: " + (on ? "activado" : "desactivado"));
            tray.ShowBalloonTip(4000, on ? "Se iniciará con Windows" : "Ya no se iniciará con Windows",
                on ? AppInfo.Name + " se abrirá en el área de notificación al iniciar sesión." : "Podés volver a activarlo desde Configuración.",
                ToolTipIcon.Info);
        }

        public void ToggleWidget()
        {
            if (WidgetVisible) { HideAllWidgets(); SaveSetting("WidgetVisible", 0); }
            else { ShowWidget(); SaveSetting("WidgetVisible", 1); }
        }

        void ShowWidget()
        {
            if (Settings.IsIsland)
            {
                if (island == null || island.IsDisposed) island = new IslandForm(this);
                island.ShowIsland();
            }
            else
            {
                if (widget == null || widget.IsDisposed) widget = new WidgetForm(this);
                widget.ShowWidget();
            }
            shownStyle = Settings.WidgetStyle;
        }

        public void OpenMain(string page)
        {
            flyout.HideFlyout();
            if (main == null || main.IsDisposed) main = new MainWindow(this);
            main.ShowPage(page);
        }

        public void EndTask(ProcMetric p, IWin32Window owner)
        {
            var own = owner as Form;
            string q = "¿Finalizar " + p.Display + " (PID " + p.Pid + ")?\n\nSe perderán los datos que no se hayan guardado en ese programa.";
            var res = own != null && own.Visible
                ? MessageBox.Show(own, q, "Finalizar tarea", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                : MessageBox.Show(q, "Finalizar tarea", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (res != DialogResult.Yes) return;
            string error;
            bool ok = Sampler.EndProcess(p.Pid, out error);
            // Procesos del sistema o de otro usuario: los finaliza el ayudante con privilegios.
            if (!ok && helper != null && helper.Connected)
            {
                string e2 = helper.Command("KILL", p.Pid.ToString());
                if (e2 == null) ok = true; else error = e2;
            }
            if (ok)
                Activity.Info(Category.Programa, "Finalizaste " + p.Display + " (PID " + p.Pid + ")");
            else
                MessageBox.Show("No se pudo finalizar " + p.Display + ": " + error +
                    "\n\nLos procesos del sistema o de otro usuario solo se pueden finalizar como administrador.",
                    "Finalizar tarea", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public void OpenFileLocation(ProcMetric p)
        {
            if (p.Path == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + p.Path + "\""); } catch { }
        }

        public void OpenTaskManager()
        {
            try { Process.Start("taskmgr.exe"); } catch { }
        }

        public void OpenLogFile()
        {
            try
            {
                if (!File.Exists(Activity.FilePath)) Activity.Info(Category.Monitor, "Registro creado");
                Process.Start("notepad.exe", "\"" + Activity.FilePath + "\"");
            }
            catch { }
        }

        public void Exit()
        {
            Activity.Info(Category.Monitor, AppInfo.Name + " cerrado");
            SystemEvents.UserPreferenceChanged -= OnPreferences;
            iconTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            showWait.Unregister(null);
            Activity.Added -= OnActivity;
            security.Dispose();
            if (helper != null) helper.Dispose();
            watcher.Dispose();
            sampler.Dispose();
            if (widget != null) widget.Dispose();
            if (island != null) island.Dispose();
            if (main != null) main.Dispose();
            flyout.Dispose();
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            ExitThread();
        }

        static void SaveSetting(string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        // ------------------------------------------------------------------ bandeja

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (flyout.Visible) { flyout.HideFlyout(); return; }
            if ((DateTime.UtcNow - flyout.LastHidden).TotalMilliseconds < 350) return;
            flyout.ShowAt(Cursor.Position);
        }

        void OnPreferences(object sender, UserPreferenceChangedEventArgs e)
        {
            trayTheme = null;
            iconSignature = null;
            UpdateTrayIcon();
            flyout.ReloadTheme();
            if (main != null && !main.IsDisposed) main.ReloadTheme();
            if (widget != null && !widget.IsDisposed) widget.ReloadTheme();
            if (island != null && !island.IsDisposed) island.ReloadTheme();
        }

        void UpdateTrayIcon()
        {
            var snap = sampler.Latest;
            float cpu = snap != null ? snap.Cpu : 0;
            var th = trayTheme ?? (trayTheme = Theme.Load());
            int size = SystemInformation.SmallIconSize.Width;
            // Solo se redibuja el ícono si cambiaron las barras (redondeadas al 5 %).
            bool app = Settings.TrayAppIcon;
            var sig = new System.Text.StringBuilder(size + "|" + th.TaskbarLight + "|" + app + "|");
            if (snap != null && !app) for (int i = snap.CpuHistory.Length - 6; i < snap.CpuHistory.Length; i++) sig.Append((int)(snap.CpuHistory[i] / 5)).Append(',');
            bool redraw = sig.ToString() != iconSignature;
            iconSignature = sig.ToString();
            if (redraw)
            using (var bmp = app ? TrayIcon.DrawApp(size) : TrayIcon.Draw(cpu, snap != null ? snap.CpuHistory : null, th, size))
            {
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = h;
            }
            string tip = snap == null ? AppInfo.Name
                : "CPU " + snap.Cpu.ToString("0") + " % · Memoria " + snap.MemPercent.ToString("0") + " %" +
                  (snap.Disks.Count > 0 ? " · Disco " + snap.Disks.Max(d => d.Active).ToString("0") + " %" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }
    }

    /// <summary>Ícono de bandeja: mini gráfico del uso de CPU de los últimos segundos.</summary>
    internal static class TrayIcon
    {
        /// <summary>El ícono del programa: cuadrado redondeado azul con el símbolo de rendimiento (igual que app.ico).</summary>
        public static Bitmap DrawApp(int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float w = size - 1;
                using (var path = Theme.Round(new RectangleF(0, 0, w, w), size * 0.22f))
                using (var b = new LinearGradientBrush(new Point(0, 0), new Point(size, size), Color.FromArgb(0, 120, 212), Color.FromArgb(0, 90, 158)))
                    g.FillPath(b, path);
                if (size < 28)
                {
                    // En tamaños chicos el marco del símbolo se pierde: solo la línea de pulso, más gruesa.
                    var pts = new[] { new PointF(.16f, .54f), new PointF(.36f, .54f), new PointF(.46f, .26f), new PointF(.58f, .76f), new PointF(.67f, .46f), new PointF(.84f, .46f) };
                    for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(pts[i].X * size, pts[i].Y * size);
                    using (var pen = new Pen(Color.White, Math.Max(1.4f, size / 11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, pts);
                }
                else
                using (var f = new Font(Fonts.IconFamily, size * 0.62f, GraphicsUnit.Pixel))
                using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("", f, Brushes.White, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }

        public static Bitmap Draw(float cpu, float[] history, Theme th, int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fg = th.TaskbarLight ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
                float pad = size / 16f;
                var frame = new RectangleF(pad, pad * 2, size - 2 * pad - 1, size - 4 * pad - 1);
                using (var path = Theme.Round(frame, size / 8f))
                using (var pen = new Pen(fg, Math.Max(1f, size / 16f))) g.DrawPath(pen, path);
                // Barras de los últimos 6 segundos (la de la derecha es el valor actual).
                const int bars = 6;
                float inner = frame.Width - 4 * pad;
                float bw = inner / bars;
                Color bc = th.TaskbarLight ? Theme.Hex(0x005FB8) : th.Accent;
                for (int i = 0; i < bars; i++)
                {
                    float v = history != null ? history[history.Length - bars + i] : (i == bars - 1 ? cpu : 0);
                    float h = Math.Max(size / 16f, (frame.Height - 4 * pad) * v / 100f);
                    var r = new RectangleF(frame.X + 2 * pad + i * bw + bw * 0.15f, frame.Bottom - 2 * pad - h, bw * 0.7f, h);
                    using (var b = new SolidBrush(bc)) g.FillRectangle(b, r);
                }
            }
            return bmp;
        }
    }
}
