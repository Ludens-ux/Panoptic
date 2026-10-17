using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>
    /// Paso del nombre anterior ("Monitor del sistema", SysMonitor) a Panoptic: configuración, datos, inicio con
    /// Windows y funciones de administrador. Se hace una sola vez, al primer inicio de la versión nueva.
    /// </summary>
    internal static class Migration
    {
        const string OldName = "SysMonitor";
        public const string OldTask = "SysMonitorHelper";
        public const string OldRulePrefix = "SysMonitor: bloquear ";
        public const string OldEtwMain = "SysMonitor-Main", OldEtwFiles = "SysMonitor-Files";

        /// <summary>Verdadero si la versión anterior tenía activadas las funciones de administrador.</summary>
        public static bool HelperWasInstalled;

        public static void Run()
        {
            try { MoveRegistry(); } catch { }
            try { MoveData(); } catch { }
            try { MoveAutostart(); } catch { }
            try { HelperWasInstalled = Exec("schtasks.exe", "/Query /TN \"" + OldTask + "\"") == 0; } catch { }
        }

        static void MoveRegistry()
        {
            using (var old = Registry.CurrentUser.OpenSubKey(@"Software\" + OldName))
            {
                if (old == null) return;
                // Si la clave nueva ya existe, solo se completan los valores que le falten.
                using (var dst = Registry.CurrentUser.CreateSubKey(@"Software\Panoptic")) Copy(old, dst);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + OldName, false);
        }

        static void Copy(RegistryKey src, RegistryKey dst)
        {
            foreach (var n in src.GetValueNames())
                if (dst.GetValue(n) == null) dst.SetValue(n, src.GetValue(n), src.GetValueKind(n));
            foreach (var s in src.GetSubKeyNames())
                using (var a = src.OpenSubKey(s))
                using (var b = dst.CreateSubKey(s)) Copy(a, b);
        }

        static void MoveData()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string from = Path.Combine(local, OldName), to = Path.Combine(local, "Panoptic");
            if (!Directory.Exists(from)) return;
            if (!Directory.Exists(to)) { Directory.Move(from, to); return; }
            // Si las dos existen, se completan los archivos que falten en la nueva.
            foreach (var f in Directory.GetFiles(from))
            {
                string target = Path.Combine(to, Path.GetFileName(f));
                if (!File.Exists(target)) File.Move(f, target);
            }
            try { Directory.Delete(from, true); } catch { }
        }

        static void MoveAutostart()
        {
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (run == null || run.GetValue(OldName) == null) return;
                run.DeleteValue(OldName, false);
                run.SetValue("Panoptic", "\"" + Application.ExecutablePath + "\" --tray");
            }
        }

        static int Exec(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode;
            }
        }
    }
}
