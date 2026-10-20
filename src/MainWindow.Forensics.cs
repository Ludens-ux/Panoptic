using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Panoptic
{
    internal sealed partial class MainWindow
    {
        static string When(DateTime? t) { return t.HasValue ? t.Value.ToString("dd/MM/yy HH:mm", Format.Es) : ""; }

        // ================================================================== Dispositivos USB

        List<UsbDevice> usbItems;
        bool usbLoading;

        void LoadUsb()
        {
            if (usbLoading) return;
            usbLoading = true;
            List<UsbDevice> result = null;
            RunInBackground("No se pudo leer el historial de USB", () => result = UsbHistory.Scan(), () => { if (result != null) usbItems = result; usbLoading = false; });
        }

        void PaintUsb(Graphics g, Rectangle c)
        {
            if (usbItems == null) LoadUsb();
            int now = usbItems == null ? 0 : usbItems.Count(d => d.Present);
            PageTitle(g, c, "Dispositivos USB", usbItems == null ? "Leyendo…"
                : usbItems.Count + " dispositivos · " + now + " conectados ahora · historial que guarda Windows, incluso de antes de instalar " + AppInfo.Name);
            int bw = S(120);
            StandardButton(g, "usb-refresh", new Rectangle(c.Right - S(24) - S(44), c.Y + S(16), S(44), S(32)), Glyphs.Restart, "", false, !usbLoading, LoadUsb);
            StandardButton(g, "usb-export", new Rectangle(c.Right - S(24) - S(52) - bw, c.Y + S(16), bw, S(32)), Glyphs.Export, "Exportar", false, usbItems != null && usbItems.Count > 0,
                () => ExportCsv("dispositivos-usb", new[] { "Dispositivo", "Tipo", "Fabricante", "VID:PID", "Número de serie", "Serie propia", "Primera conexión", "Última conexión", "Última desconexión", "Conectado", "Identificador" },
                    usbItems.Select(d => new[] { d.Name, d.Kind, d.Manufacturer ?? "", d.VidPid ?? "", d.Serial ?? "", d.RealSerial ? "sí" : "no (la asigna Windows)",
                        When(d.FirstInstall), When(d.LastArrival), When(d.LastRemoval), d.Present ? "sí" : "no", d.Id })));
            SearchBox(g, new Rectangle(c.Right - S(24) - S(60) - bw - S(260), c.Y + S(16), S(260), S(32)), "Buscar dispositivo o número de serie");
            if (usbItems == null) return;
            if (!tableSort.ContainsKey("usb")) { tableSort["usb"] = 4; tableAsc["usb"] = false; } // más reciente arriba

            string q = Query;
            var rows = usbItems.Where(d => q.Length == 0 || (d.Name + " " + d.Serial + " " + d.VidPid + " " + d.Manufacturer).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Cast<object>().ToList();
            var cols = new List<Column>
            {
                Col("Dispositivo", 0, false, o => ((UsbDevice)o).Name),
                Col("Tipo", 90, false, o => ((UsbDevice)o).Kind == "Almacenamiento" ? "Disco" : "Teléfono"),
                new Column { Title = "Número de serie", Width = 170, Text = o => ((UsbDevice)o).Serial ?? "",
                    Color = o => ((UsbDevice)o).RealSerial ? (Color?)null : th.TextTertiary },
                new Column { Title = "Primera conexión", Width = 118, Text = o => When(((UsbDevice)o).FirstInstall), Sort = o => ((UsbDevice)o).FirstInstall ?? DateTime.MinValue },
                new Column { Title = "Última conexión", Width = 118, Text = o => When(((UsbDevice)o).LastArrival), Sort = o => ((UsbDevice)o).LastArrival ?? DateTime.MinValue },
                new Column { Title = "Desconexión", Width = 118, Text = o => ((UsbDevice)o).Present ? "Conectado" : When(((UsbDevice)o).LastRemoval),
                    Sort = o => ((UsbDevice)o).Present ? DateTime.MaxValue : ((UsbDevice)o).LastRemoval ?? DateTime.MinValue,
                    Color = o => ((UsbDevice)o).Present ? (Color?)th.Accent : null },
            };
            Table(g, new Rectangle(c.X, c.Y + S(76), c.Width, c.Bottom - c.Y - S(76)), "usb", cols, rows, o => ((UsbDevice)o).Id,
                o => null, o => ((UsbDevice)o).Kind == "Almacenamiento" ? Glyphs.Usb : Glyphs.App,
                (o, pt) =>
                {
                    var d = (UsbDevice)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, "Copiar número de serie", Glyphs.Copy, d.Serial != null, () => Copy(d.Serial));
                    FluentMenu.Add(m, "Copiar todos los datos", Glyphs.Copy, true, () => Copy(
                        "Dispositivo: " + d.Name + "\r\nTipo: " + d.Kind + (d.Manufacturer != null ? "\r\nFabricante: " + d.Manufacturer : "") +
                        "\r\nVID:PID: " + d.VidPid + "\r\nNúmero de serie: " + d.Serial + (d.RealSerial ? "" : " (asignado por Windows: el dispositivo no tiene uno propio)") +
                        "\r\nPrimera conexión: " + When(d.FirstInstall) + "\r\nÚltima conexión: " + When(d.LastArrival) +
                        "\r\nÚltima desconexión: " + When(d.LastRemoval) + "\r\nConectado ahora: " + (d.Present ? "sí" : "no") + "\r\nIdentificador: " + d.Id));
                    if (!d.RealSerial) FluentMenu.Add(m, "El número de serie lo asigna Windows (el dispositivo no tiene uno propio)", Glyphs.Info, false, null);
                    m.Show(this, pt);
                }, "No hay dispositivos que coincidan");
        }

        // ================================================================== Encendidos, apagados y fallas

        List<PowerEvent> powerItems;
        bool powerLoading;
        int powerDays = 30, powerFilter = 0; // 0 encendidos y apagados, 1 fallas, 2 suspensiones, 3 todo

        void LoadPower()
        {
            if (powerLoading) return;
            powerLoading = true;
            List<PowerEvent> result = null;
            int days = powerDays;
            RunInBackground("No se pudo leer el registro de Windows", () => result = PowerHistory.Scan(days), () => { if (result != null) powerItems = result; powerLoading = false; });
        }

        void PaintPower(Graphics g, Rectangle c)
        {
            if (powerItems == null) LoadPower();
            string sub = "Leyendo…";
            if (powerItems != null)
            {
                var boot = powerItems.LastOrDefault(e => e.Kind == PowerKind.Encendido);
                int boots = powerItems.Count(e => e.Kind == PowerKind.Encendido), fails = powerItems.Count(e => e.Failure);
                sub = (boot != null ? "Encendida desde el " + boot.Time.ToString("dd/MM HH:mm", Format.Es) + " (hace " + Format.Duration(DateTime.Now - boot.Time) + ") · " : "") +
                      boots + " encendidos · " + (fails == 0 ? "sin fallas" : fails + (fails == 1 ? " falla" : " fallas")) + " en " + powerDays + " días";
            }
            PageTitle(g, c, "Encendidos y fallas", sub);
            int bw = S(150);
            StandardButton(g, "pw-refresh", new Rectangle(c.Right - S(24) - S(44), c.Y + S(16), S(44), S(32)), Glyphs.Restart, "", false, !powerLoading, LoadPower);
            var rr = new Rectangle(c.Right - S(24) - S(52) - bw, c.Y + S(16), bw, S(32));
            StandardButton(g, "pw-range", rr, Glyphs.History, "Últimos " + powerDays + " días", false, true, () =>
            {
                var m = NewMenu();
                foreach (int d in new[] { 7, 30, 90, 365 })
                {
                    int days = d;
                    var it = FluentMenu.Add(m, d == 365 ? "Último año" : "Últimos " + d + " días", null, true, () => { powerDays = days; powerItems = null; Invalidate(); });
                    it.Checked = powerDays == d;
                }
                m.Show(this, new Point(rr.X, rr.Bottom + S(2)));
            });
            SearchBox(g, new Rectangle(rr.X - S(8) - S(240), c.Y + S(16), S(240), S(32)), "Buscar");
            int used = Chips(g, c.X + S(24), c.Y + S(72), c.Right - S(24), "power",
                new[] { "Encendidos y apagados", "Fallas", "Suspensiones", "Todo" }, powerFilter, i => powerFilter = i);
            if (powerItems == null) return;
            if (!tableSort.ContainsKey("power")) { tableSort["power"] = 1; tableAsc["power"] = false; }

            IEnumerable<PowerEvent> rows = powerItems;
            if (powerFilter == 0) rows = rows.Where(e => e.Kind != PowerKind.Suspension && e.Kind != PowerKind.Reanudacion);
            if (powerFilter == 1) rows = rows.Where(e => e.Failure);
            if (powerFilter == 2) rows = rows.Where(e => e.Kind == PowerKind.Suspension || e.Kind == PowerKind.Reanudacion);
            string q = Query;
            if (q.Length > 0) rows = rows.Where(e => (e.KindName + " " + e.Detail).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            var cols = new List<Column>
            {
                new Column { Title = "Evento", Width = 170, Text = o => ((PowerEvent)o).KindName, Sort = o => ((PowerEvent)o).Kind.ToString(),
                    Color = o => ((PowerEvent)o).Failure ? (Color?)th.Critical : null },
                new Column { Title = "Fecha", Width = 130, Text = o => When(((PowerEvent)o).Time), Sort = o => ((PowerEvent)o).Time },
                new Column { Title = "Duración", Width = 120, Text = o => DurationText((PowerEvent)o),
                    Sort = o => ((PowerEvent)o).Duration ?? TimeSpan.Zero },
                Col("Detalle", 0, false, o => ((PowerEvent)o).Detail),
            };
            string empty = powerFilter == 1 ? "Sin fallas en los últimos " + powerDays + " días" : "No hay eventos que coincidan";
            Table(g, new Rectangle(c.X, c.Y + S(72) + used, c.Width, c.Bottom - (c.Y + S(72) + used)), "power", cols, rows.Cast<object>().ToList(),
                o => ((PowerEvent)o).Time.Ticks + "|" + ((PowerEvent)o).Kind,
                o => null, o => PowerGlyph((PowerEvent)o),
                (o, pt) =>
                {
                    var e = (PowerEvent)o;
                    var m = NewMenu();
                    FluentMenu.Add(m, "Copiar", Glyphs.Copy, true, () => Copy(When(e.Time) + " · " + e.KindName + (e.Duration.HasValue ? " · " + DurationText(e) : "") + (e.Detail.Length > 0 ? " · " + e.Detail : "")));
                    FluentMenu.Add(m, "Abrir el Visor de eventos", Glyphs.OpenExternal, true, () => { try { System.Diagnostics.Process.Start("eventvwr.msc", "/c:System"); } catch { } });
                    m.Show(this, pt);
                }, empty);
        }

        static string DurationText(PowerEvent e)
        {
            if (!e.Duration.HasValue) return "";
            string d = Format.Duration(e.Duration.Value);
            return e.Kind == PowerKind.Reanudacion ? d + " en pausa" : d;
        }

        static string PowerGlyph(PowerEvent e)
        {
            switch (e.Kind)
            {
                case PowerKind.Reinicio: return Glyphs.Restart;
                case PowerKind.ApagadoInesperado: return Glyphs.Warning;
                case PowerKind.PantallazoAzul: return Glyphs.Error;
                case PowerKind.Suspension: case PowerKind.Reanudacion: return Glyphs.History;
                default: return Glyphs.Power;
            }
        }

        // ================================================================== utilidades

        static void Copy(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch { }
        }

        /// <summary>Guarda una tabla como CSV (separado por punto y coma, como lo abre Excel en español).</summary>
        void ExportCsv(string name, string[] header, IEnumerable<string[]> rows)
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = name + "-" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Func<string, string> esc = v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(";", header.Select(esc)));
                foreach (var r in rows) sb.AppendLine(string.Join(";", r.Select(esc)));
                try { System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); }
                catch (Exception ex) { MessageBox.Show(this, "No se pudo guardar: " + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
