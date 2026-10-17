using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Panoptic
{
    internal interface IHost
    {
        Snapshot Latest { get; }
        bool WidgetVisible { get; }
        bool Autostart { get; }
        void ToggleWidget();
        void ToggleAutostart();
        void OpenMain(string page);
        void EndTask(ProcMetric p, IWin32Window owner);
        void OpenFileLocation(ProcMetric p);
        void OpenTaskManager();
        void OpenLogFile();
        HistoryStore History { get; }
        void ApplyHotkey();
        void WidgetChanged();
        void OpenProcess(int pid);
        HelperClient Helper { get; }
        void Exit();
    }

    /// <summary>Menús contextuales compartidos.</summary>
    internal static class Menus
    {
        public static ContextMenuStrip ForProcess(Theme th, float s, IHost host, ProcMetric p, IWin32Window owner)
        {
            var m = FluentMenu.Create(th, s);
            FluentMenu.Add(m, "Finalizar tarea", Glyphs.Cancel, true, () => host.EndTask(p, owner));
            FluentMenu.Add(m, "Abrir ubicación del archivo", Glyphs.Folder, p.Path != null, () => host.OpenFileLocation(p));
            FluentMenu.Separator(m);
            ProcessActions.AddMenuItems(m, p, owner, th, s);
            FluentMenu.Separator(m);
            FluentMenu.Add(m, "Copiar nombre y PID", Glyphs.Copy, true, () =>
            {
                try { Clipboard.SetText(p.Display + " (" + p.Name + ".exe, PID " + p.Pid + ")" + (p.Path != null ? "\r\n" + p.Path : "")); } catch { }
            });
            return m;
        }

        public static ContextMenuStrip Settings(Theme th, float s, IHost host, bool includeOpen)
        {
            var m = FluentMenu.Create(th, s);
            if (includeOpen) FluentMenu.Add(m, "Abrir Panoptic", Glyphs.OpenExternal, true, () => host.OpenMain(null));
            var w = FluentMenu.Add(m, "Widget en el escritorio", Glyphs.Widget, true, host.ToggleWidget);
            w.Checked = host.WidgetVisible;
            var a = FluentMenu.Add(m, "Iniciar con Windows", Glyphs.Startup, true, host.ToggleAutostart);
            a.Checked = host.Autostart;
            FluentMenu.Add(m, "Administrador de tareas de Windows", Glyphs.Performance, true, host.OpenTaskManager);
            FluentMenu.Add(m, "Abrir el archivo de registro", Glyphs.Log, true, host.OpenLogFile);
            FluentMenu.Add(m, "Acerca de " + AppInfo.Name, Glyphs.Info, true, () =>
                MessageBox.Show(AppInfo.Name + "\nVersión " + AppInfo.Version + "\nAutor: " + AppInfo.Author +
                    "\n\nRendimiento en vivo, procesos y registro de actividad de la PC.", "Acerca de " + AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information));
            FluentMenu.Separator(m);
            FluentMenu.Add(m, "Salir", Glyphs.Cancel, true, host.Exit);
            return m;
        }
    }
}
