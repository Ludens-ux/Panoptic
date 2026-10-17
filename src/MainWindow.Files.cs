using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Panoptic
{
    internal sealed partial class MainWindow
    {
        int filesFilter = 0; // 0 todos, 1 solo escritura, 2 solo lectura

        /// <summary>Qué archivos lee y escribe cada programa (lo registra el ayudante mientras esta página está abierta).</summary>
        void PaintFiles(Graphics g, Rectangle c, Snapshot snap)
        {
            var hc = host.Helper;
            if (hc == null || !hc.Connected)
            {
                PageTitle(g, c, "Archivos", null);
                var r = new Rectangle(c.X + S(24), c.Y + S(72), Math.Min(c.Width - S(48), S(900)), S(84));
                var cr = SettingCard(g, r, Glyphs.Shield, "Requiere las funciones de administrador",
                    "Muestra qué archivos lee y escribe cada programa en este momento. Activalas en Configuración.", S(150));
                StandardButton(g, "files-settings", new Rectangle(cr.Right - S(150), cr.Y + (cr.Height - S(32)) / 2, S(150), S(32)), null, "Ir a Configuración", false, true,
                    () => Navigate("settings"));
                return;
            }
            var byPid = snap.Procs.GroupBy(p => p.Pid).ToDictionary(x => x.Key, x => x.First());
            Func<int, string> procName = pid => { ProcMetric pm; return byPid.TryGetValue(pid, out pm) ? pm.Display : SecurityWatch.ProcessName(pid); };
            var files = hc.Files;
            double rd = files.Sum(f => f.ReadBps), wr = files.Sum(f => f.WriteBps);
            PageTitle(g, c, "Archivos", files.Count == 0 ? "Registrando la actividad de archivos… (se detiene al salir de esta página)"
                : files.Count + " archivos con actividad · lectura " + Format.Rate(rd) + " · escritura " + Format.Rate(wr));
            SearchBox(g, new Rectangle(c.Right - S(24) - S(260), c.Y + S(16), S(260), S(32)), "Buscar programa o archivo");
            int used = Chips(g, c.X + S(24), c.Y + S(72), c.Right - S(24), "files", new[] { "Todos", "Escritura", "Lectura" }, filesFilter, i => filesFilter = i);

            string q = Query;
            IEnumerable<FileStat> rows = files;
            if (filesFilter == 1) rows = rows.Where(f => f.WriteTotal > 0);
            if (filesFilter == 2) rows = rows.Where(f => f.ReadTotal > 0);
            if (q.Length > 0)
                rows = rows.Where(f => (f.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    procName(f.Pid).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || f.Pid.ToString() == q);

            var cols = new List<Column>
            {
                Col("Archivo", 0, false, o => ((FileStat)o).Path),
                Col("Programa", 180, false, o => procName(((FileStat)o).Pid)),
                new Column { Title = "Lectura", Width = 96, Right = true, Text = o => ((FileStat)o).ReadBps >= 1 ? Format.Rate(((FileStat)o).ReadBps) : "", Sort = o => ((FileStat)o).ReadBps },
                new Column { Title = "Escritura", Width = 96, Right = true, Text = o => ((FileStat)o).WriteBps >= 1 ? Format.Rate(((FileStat)o).WriteBps) : "", Sort = o => ((FileStat)o).WriteBps,
                    Color = o => ((FileStat)o).WriteBps >= 1048576 ? (Color?)th.Caution : null },
                new Column { Title = "Leído", Width = 86, Right = true, Text = o => Format.Bytes((ulong)((FileStat)o).ReadTotal), Sort = o => ((FileStat)o).ReadTotal },
                new Column { Title = "Escrito", Width = 86, Right = true, Text = o => Format.Bytes((ulong)((FileStat)o).WriteTotal), Sort = o => ((FileStat)o).WriteTotal },
            };
            Table(g, new Rectangle(c.X, c.Y + S(72) + used, c.Width, c.Bottom - (c.Y + S(72) + used)), "files", cols,
                rows.OrderByDescending(f => f.ReadBps + f.WriteBps).ThenByDescending(f => f.ReadTotal + f.WriteTotal).Cast<object>().ToList(),
                o => ((FileStat)o).Pid + "|" + ((FileStat)o).Path,
                o => { ProcMetric pm; return byPid.TryGetValue(((FileStat)o).Pid, out pm) ? pm.Icon : null; },
                o => Glyphs.Folder,
                (o, pt) =>
                {
                    var f = (FileStat)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, "Abrir ubicación del archivo", Glyphs.Folder, f.Path != null && f.Path.Contains(":\\"),
                        () => { try { Process.Start("explorer.exe", "/select,\"" + f.Path + "\""); } catch { } });
                    FluentMenu.Add(m, "Copiar ruta", Glyphs.Copy, f.Path != null, () => { try { Clipboard.SetText(f.Path); } catch { } });
                    ProcMetric pm;
                    if (byPid.TryGetValue(f.Pid, out pm))
                    {
                        FluentMenu.Separator(m);
                        FluentMenu.Add(m, "Ver el proceso", Glyphs.Processes, true, () => { selectedPid = pm.Pid; Navigate("procs"); });
                        FluentMenu.Add(m, "Finalizar " + pm.Display, Glyphs.Cancel, true, () => host.EndTask(pm, this));
                    }
                    m.Show(this, pt);
                }, files.Count == 0 ? "Esperando actividad de archivos…" : "Ningún archivo coincide");
        }
    }
}
