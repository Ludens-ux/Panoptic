using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace SysMonitor
{
    internal sealed partial class MainWindow
    {
        static readonly Category[] LogCategories =
        {
            Category.Programa, Category.Dispositivo, Category.Red, Category.Energia, Category.Sesion,
            Category.Sistema, Category.Alerta, Category.Inicio, Category.Servicio, Category.Bloqueo, Category.Seguridad
        };

        void ShowExportMenu(Point at)
        {
            var m = NewMenu();
            FluentMenu.Add(m, "Hoy", null, true, () => Export(DateTime.Today, "hoy"));
            FluentMenu.Add(m, "Últimos 7 días", null, true, () => Export(DateTime.Today.AddDays(-6), "ultimos-7-dias"));
            FluentMenu.Add(m, "Últimos 30 días", null, true, () => Export(DateTime.Today.AddDays(-29), "ultimos-30-dias"));
            FluentMenu.Add(m, "Todo el registro", null, true, () => Export(DateTime.MinValue, "completo"));
            m.Show(this, at);
        }

        /// <summary>Lee todo el archivo de registro (no solo lo que está en memoria) desde una fecha.</summary>
        static List<ActivityEntry> ReadLogFile(DateTime from)
        {
            var list = new List<ActivityEntry>();
            try
            {
                if (!File.Exists(Activity.FilePath)) return list;
                using (var fs = new FileStream(Activity.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var p = line.Split('\t');
                        if (p.Length < 4) continue;
                        DateTime t; Category c; Severity s;
                        if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) || t < from) continue;
                        if (!Enum.TryParse(p[1], out c) || !Enum.TryParse(p[2], out s)) continue;
                        list.Add(new ActivityEntry { Time = t, Category = c, Severity = s, Message = p[3] });
                    }
                }
            }
            catch { }
            return list;
        }

        void Export(DateTime from, string suffix)
        {
            string q = searchText.ContainsKey("log") ? searchText["log"].Trim() : "";
            var filter = logFilter;
            var entries = ReadLogFile(from)
                .Where(e => (!filter.HasValue || e.Category == filter.Value) && (q.Length == 0 || e.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(e => e.Time).ToList();
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "No hay eventos para exportar con ese rango y esos filtros.", "Exportar registro", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Exportar registro de actividad";
                dlg.Filter = "Página web (*.html)|*.html|Valores separados por comas (*.csv)|*.csv";
                dlg.FileName = "registro-" + Environment.MachineName.ToLowerInvariant() + "-" + suffix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool csv = dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    string range = from == DateTime.MinValue ? "todo el registro" : "desde el " + from.ToString("dd/MM/yyyy");
                    string scope = (filter.HasValue ? "categoría " + ActivityEntry.CategoryName(filter.Value) : "todas las categorías") + (q.Length > 0 ? ", búsqueda \"" + q + "\"" : "");
                    File.WriteAllText(dlg.FileName, csv ? Csv(entries) : Html(entries, range, scope), new UTF8Encoding(true));
                    Activity.Info(Category.Monitor, "Exportaste " + entries.Count + " eventos del registro a " + dlg.FileName);
                    Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "No se pudo exportar: " + ex.Message, "Exportar registro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        static string Csv(List<ActivityEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Fecha;Hora;Categoría;Gravedad;Evento");
            foreach (var e in entries)
                sb.AppendLine(e.Time.ToString("yyyy-MM-dd") + ";" + e.Time.ToString("HH:mm:ss") + ";" + ActivityEntry.CategoryName(e.Category) + ";" +
                    e.Severity + ";\"" + e.Message.Replace("\"", "\"\"") + "\"");
            return sb.ToString();
        }

        static string Html(List<ActivityEntry> entries, string range, string scope)
        {
            Func<string, string> h = WebUtility.HtmlEncode;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Registro de actividad - ").Append(h(Environment.MachineName)).Append("</title>");
            sb.Append("<style>body{font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;margin:32px;color:#1a1a1a;background:#f9f9f9}");
            sb.Append("h1{font-size:24px;font-weight:600;margin:0 0 4px}p.meta{color:#5c5c5c;margin:0 0 20px;font-size:13px}");
            sb.Append("table{border-collapse:collapse;width:100%;background:#fff;border:1px solid #e5e5e5;font-size:13px}");
            sb.Append("th{text-align:left;font-weight:600;background:#f3f3f3;padding:8px 10px;border-bottom:1px solid #e5e5e5}");
            sb.Append("td{padding:7px 10px;border-bottom:1px solid #f0f0f0;vertical-align:top}td.t{white-space:nowrap;color:#5c5c5c;font-variant-numeric:tabular-nums}");
            sb.Append("tr.Advertencia td.s{color:#9d5d00;font-weight:600}tr.Error td.s{color:#c42b1c;font-weight:600}");
            sb.Append("tr.day td{background:#f3f3f3;font-weight:600}footer{margin-top:16px;color:#8a8a8a;font-size:12px}</style></head><body>");
            sb.Append("<h1>Registro de actividad</h1><p class=\"meta\">Equipo <b>").Append(h(Environment.MachineName)).Append("</b> · usuario ")
              .Append(h(Environment.UserName)).Append(" · ").Append(h(range)).Append(" · ").Append(h(scope)).Append(" · ")
              .Append(entries.Count).Append(" eventos · generado el ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")).Append("</p>");
            sb.Append("<table><tr><th>Hora</th><th>Categoría</th><th>Gravedad</th><th>Evento</th></tr>");
            DateTime? day = null;
            foreach (var e in entries)
            {
                if (day != e.Time.Date)
                {
                    day = e.Time.Date;
                    sb.Append("<tr class=\"day\"><td colspan=\"4\">").Append(h(e.Time.ToString("dddd d 'de' MMMM 'de' yyyy", Format.Es))).Append("</td></tr>");
                }
                sb.Append("<tr class=\"").Append(e.Severity).Append("\"><td class=\"t\">").Append(e.Time.ToString("HH:mm:ss")).Append("</td><td>")
                  .Append(h(ActivityEntry.CategoryName(e.Category))).Append("</td><td class=\"s\">").Append(e.Severity).Append("</td><td>")
                  .Append(h(e.Message)).Append("</td></tr>");
            }
            sb.Append("</table><footer>").Append(h(AppInfo.Name + " " + AppInfo.Version + " · " + AppInfo.Author)).Append("</footer></body></html>");
            return sb.ToString();
        }
    }
}
