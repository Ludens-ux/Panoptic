using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>
    /// Prioridad, afinidad y firewall de un proceso. Primero se intenta sin privilegios (procesos propios);
    /// si Windows lo niega, se pide al ayudante con privilegios.
    /// </summary>
    internal static class ProcessActions
    {
        public static readonly ProcessPriorityClass[] Priorities =
        {
            ProcessPriorityClass.High, ProcessPriorityClass.AboveNormal, ProcessPriorityClass.Normal,
            ProcessPriorityClass.BelowNormal, ProcessPriorityClass.Idle
        };

        public static string PriorityName(ProcessPriorityClass p)
        {
            switch (p)
            {
                case ProcessPriorityClass.RealTime: return "Tiempo real";
                case ProcessPriorityClass.High: return "Alta";
                case ProcessPriorityClass.AboveNormal: return "Superior a lo normal";
                case ProcessPriorityClass.BelowNormal: return "Inferior a lo normal";
                case ProcessPriorityClass.Idle: return "Baja";
                default: return "Normal";
            }
        }

        /// <summary>Prioridad y afinidad actuales (null si no se pueden leer).</summary>
        public static bool Read(int pid, out ProcessPriorityClass prio, out long mask)
        {
            prio = ProcessPriorityClass.Normal; mask = 0;
            try
            {
                using (var p = Process.GetProcessById(pid)) { prio = p.PriorityClass; mask = p.ProcessorAffinity.ToInt64(); return true; }
            }
            catch { }
            var h = HelperClient.Instance;
            var r = h != null ? h.Query("PRIOGET", pid.ToString()) : null;
            if (r == null || r.Length < 3) return false;
            prio = (ProcessPriorityClass)int.Parse(r[1]);
            mask = long.Parse(r[2], System.Globalization.NumberStyles.HexNumber);
            return true;
        }

        public static string SetPriority(int pid, ProcessPriorityClass prio)
        {
            try { using (var p = Process.GetProcessById(pid)) p.PriorityClass = prio; return null; }
            catch (Exception ex)
            {
                var h = HelperClient.Instance;
                return h != null && h.Connected ? h.Command("PRIO", pid.ToString(), ((int)prio).ToString()) : ex.Message;
            }
        }

        public static string SetAffinity(int pid, long mask)
        {
            try { using (var p = Process.GetProcessById(pid)) p.ProcessorAffinity = new IntPtr(mask); return null; }
            catch (Exception ex)
            {
                var h = HelperClient.Instance;
                return h != null && h.Connected ? h.Command("AFFINITY", pid.ToString(), mask.ToString("X")) : ex.Message;
            }
        }

        public static bool IsBlocked(string path)
        {
            var h = HelperClient.Instance;
            return h != null && path != null && h.Blocked.Contains(path);
        }

        public static string SetBlocked(string path, bool block)
        {
            var h = HelperClient.Instance;
            if (h == null) return "No disponible.";
            return h.Command(block ? "FWBLOCK" : "FWUNBLOCK", path);
        }

        /// <summary>Agrega al menú de un proceso las opciones de prioridad, afinidad y firewall.</summary>
        public static void AddMenuItems(ContextMenuStrip m, ProcMetric p, IWin32Window owner, Theme th, float s)
        {
            ProcessPriorityClass prio; long mask;
            bool readable = Read(p.Pid, out prio, out mask);
            var pri = FluentMenu.Add(m, "Prioridad", Glyphs.Performance, true, null);
            foreach (var level in Priorities)
            {
                var lv = level;
                var it = FluentMenu.AddSub(pri, PriorityName(level), null, true, () => Report(SetPriority(p.Pid, lv), owner,
                    "Prioridad de " + p.Display + ": " + PriorityName(lv)));
                it.Checked = readable && prio == level;
            }
            FluentMenu.Add(m, "Afinidad de procesadores…", Glyphs.Cpu, true, () =>
            {
                long cur = readable && mask != 0 ? mask : (1L << Math.Min(63, Environment.ProcessorCount)) - 1;
                using (var dlg = new AffinityDialog(p.Display, cur))
                {
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                    Report(SetAffinity(p.Pid, dlg.Mask), owner, "Afinidad de " + p.Display + ": " + Count(dlg.Mask) + " de " + Environment.ProcessorCount + " procesadores");
                }
            });
            var h = HelperClient.Instance;
            if (p.Path != null && h != null && h.Connected)
            {
                FluentMenu.Add(m, IsBlocked(p.Path) ? "Desbloquear en el firewall" : "Bloquear su conexión a internet", Glyphs.Shield, true,
                    () => ToggleFirewall(p.Path, p.Display, owner));
            }
        }

        /// <summary>Bloquea (con confirmación) o desbloquea un programa en el Firewall de Windows.</summary>
        public static void ToggleFirewall(string path, string display, IWin32Window owner)
        {
            bool blocked = IsBlocked(path);
            if (!blocked && MessageBox.Show(owner, "¿Bloquear todas las conexiones de " + display + "?\n\n" + path +
                "\n\nSe agrega una regla al Firewall de Windows (entrada y salida). Se puede quitar desde este menú o desde Configuración.",
                "Bloquear en el firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string err = SetBlocked(path, !blocked);
            if (err != null) MessageBox.Show(owner, err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Activity.Info(Category.Seguridad, (blocked ? "Desbloqueaste en el firewall " : "Bloqueaste en el firewall ") + display + " (" + path + ")");
        }

        static int Count(long mask) { int n = 0; while (mask != 0) { n += (int)(mask & 1); mask >>= 1; } return n; }

        static void Report(string error, IWin32Window owner, string done)
        {
            if (error == null) { Activity.Info(Category.Programa, done); return; }
            MessageBox.Show(owner, error + (HelperClient.Instance == null || !HelperClient.Instance.Connected
                ? "\n\nPara procesos del sistema o de otros usuarios, activá las funciones de administrador en Configuración." : ""),
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Cuadro para elegir en qué procesadores lógicos puede correr un proceso.</summary>
    internal sealed class AffinityDialog : Surface
    {
        public long Mask;
        readonly string name;
        readonly int count = Math.Min(64, Environment.ProcessorCount);

        public AffinityDialog(string processName, long mask)
        {
            name = processName;
            Mask = mask;
            Text = "Afinidad de procesadores";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
        }

        protected override int Backdrop { get { return Native.DWMSBT_MICA; } }

        protected override void OnScaleChanged()
        {
            int rows = (count + 3) / 4;
            ClientSize = new Size(S(440), S(150 + rows * 40));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            OnScaleChanged();
            int v = Native.ColorRef(th.Bg);
            if (!th.Glass) Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CAPTION_COLOR, ref v, 4);
        }

        protected override void PaintSurface(Graphics g)
        {
            g.Clear(th.Clear);
            int W = ClientSize.Width, pad = S(24);
            DrawText(g, "Procesadores que puede usar " + name, fonts.BodyStrong, new Rectangle(pad, S(16), W - 2 * pad, S(22)), th.Text);
            int cw = (W - 2 * pad - 3 * S(8)) / 4, y = S(52);
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                bool on = (Mask & (1L << i)) != 0;
                var r = new Rectangle(pad + (i % 4) * (cw + S(8)), y + (i / 4) * S(40), cw, S(32));
                string key = "cpu" + i;
                var h = AddHit(key, r, () => { Mask ^= 1L << idx; if (Mask == 0) Mask = 1L << idx; Invalidate(); }, "CPU " + i);
                h.Role = AccessibleRole.CheckButton; h.Checked = on;
                if (IsHover(key)) Fill(g, r, th.HoverOverlay, S(4));
                var box = new Rectangle(r.X + S(6), r.Y + (r.Height - S(18)) / 2, S(18), S(18));
                if (on) { Fill(g, box, th.Accent, S(3)); Glyph(g, Glyphs.CheckMark, fonts.Glyph12, box, th.OnAccent); }
                else using (var p = Theme.Round(new RectangleF(box.X + .5f, box.Y + .5f, box.Width - 1, box.Height - 1), S(3)))
                    using (var pen = new Pen(th.ToggleOffStroke)) g.DrawPath(pen, p);
                DrawText(g, "CPU " + i, fonts.Body, new Rectangle(box.Right + S(8), r.Y, r.Width - S(32), r.Height), th.Text);
            }
            int by = ClientSize.Height - S(52), bw = S(110);
            StandardButton(g, "all", new Rectangle(pad, by, bw, S(32)), null, "Todos", false, true, () => { Mask = (count >= 64 ? -1L : (1L << count) - 1); Invalidate(); });
            StandardButton(g, "cancel", new Rectangle(W - pad - bw, by, bw, S(32)), null, "Cancelar", false, true, () => { DialogResult = DialogResult.Cancel; Close(); });
            StandardButton(g, "ok", new Rectangle(W - pad - 2 * bw - S(8), by, bw, S(32)), null, "Aceptar", true, true, () => { DialogResult = DialogResult.OK; Close(); });
        }
    }
}
