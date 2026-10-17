using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Panoptic
{
    internal sealed class NetTraffic { public double SentBps, RecvBps; public long SentTotal, RecvTotal; }
    internal sealed class FileStat { public int Pid; public string Path; public double ReadBps, WriteBps; public long ReadTotal, WriteTotal; }
    internal sealed class SmartInfo
    {
        public int Disk, Health = 5;
        public int? Temp, TempMax, Wear, Hours, ReadErrors, WriteErrors, Cycles;
        public string HealthName
        {
            get { return Health == 0 ? "Correcta" : Health == 1 ? "Advertencia" : Health == 2 ? "Con fallas" : "Desconocida"; }
        }
    }

    /// <summary>
    /// Lado de la ventana del ayudante con privilegios: se conecta por el canal con nombre, trae cada segundo los
    /// eventos y el tráfico, y envía los comandos (finalizar, prioridad, afinidad, firewall).
    /// </summary>
    internal sealed class HelperClient : IDisposable
    {
        public static HelperClient Instance;

        readonly object gate = new object();
        NamedPipeClientStream pipe;
        StreamReader reader;
        StreamWriter writer;
        readonly Thread thread;
        volatile bool disposed;
        DateTime lastLaunch = DateTime.MinValue, lastSmart = DateTime.MinValue, lastFw = DateTime.MinValue, lastTaskCheck = DateTime.MinValue;
        bool taskExists;
        bool filesOn;

        public volatile bool Connected;
        public volatile bool WantFiles;
        public bool Installed { get { return taskExists; } }
        public Dictionary<int, NetTraffic> NetByPid = new Dictionary<int, NetTraffic>();
        public Dictionary<string, NetTraffic> NetByConn = new Dictionary<string, NetTraffic>();
        public List<FileStat> Files = new List<FileStat>();
        public Dictionary<int, SmartInfo> Smart = new Dictionary<int, SmartInfo>();
        public HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public event Action Changed;

        public HelperClient()
        {
            Instance = this;
            thread = new Thread(Loop) { IsBackground = true, Name = "Ayudante" };
            thread.Start();
        }

        // ---------------------------------------------------------------- conexión y sondeo

        void Loop()
        {
            while (!disposed)
            {
                try
                {
                    if ((DateTime.UtcNow - lastTaskCheck).TotalSeconds > 30) { taskExists = TaskExists(); lastTaskCheck = DateTime.UtcNow; }
                    if (!Connected) TryConnect();
                    if (Connected) Poll();
                }
                catch { Disconnect(); }
                Thread.Sleep(1000);
            }
        }

        void TryConnect()
        {
            if (!taskExists) return;
            try
            {
                var p = new NamedPipeClientStream(".", HelperService.PipeName, PipeDirection.InOut);
                p.Connect(300);
                lock (gate)
                {
                    pipe = p;
                    reader = new StreamReader(p, new UTF8Encoding(false));
                    writer = new StreamWriter(p, new UTF8Encoding(false)) { NewLine = "\n" };
                }
                var pong = Request("PING");
                Connected = pong.Count > 0 && pong[0].StartsWith("OK");
                if (Connected) { filesOn = false; lastSmart = lastFw = DateTime.MinValue; Raise(); }
            }
            catch
            {
                Disconnect();
                // El ayudante no responde: se lanza la tarea programada (no pide permisos: ya está autorizada).
                if ((DateTime.UtcNow - lastLaunch).TotalSeconds > 20)
                {
                    lastLaunch = DateTime.UtcNow;
                    Run("schtasks.exe", "/Run /TN \"" + HelperService.TaskName + "\"");
                }
            }
        }

        void Disconnect()
        {
            bool was = Connected;
            Connected = false;
            lock (gate)
            {
                try { if (pipe != null) pipe.Dispose(); } catch { }
                pipe = null; reader = null; writer = null;
            }
            if (was) Raise();
        }

        /// <summary>Envía un pedido y devuelve las líneas de la respuesta. Lanza si se cortó el canal.</summary>
        List<string> Request(string line)
        {
            lock (gate)
            {
                if (writer == null) throw new IOException("Sin conexión");
                writer.Write(line + "\n");
                writer.Flush();
                var lines = new List<string>();
                string l;
                while ((l = reader.ReadLine()) != null && l != "END") lines.Add(l);
                if (l == null) throw new IOException("El ayudante se desconectó");
                return lines;
            }
        }

        void Poll()
        {
            // Eventos nuevos → registro de actividad (continuando desde el último recibido, aun entre sesiones).
            string inst = Settings.GetString("HelperInstance", "");
            long since;
            long.TryParse(Settings.GetString("HelperSeq", "0"), out since);
            var ev = Request("EVENTS\t" + since);
            if (ev.Count > 0 && ev[0].StartsWith("I\t"))
            {
                string now = ev[0].Substring(2);
                if (now != inst)
                {
                    // El ayudante se reinició: sus números de evento empiezan de nuevo.
                    Settings.SetString("HelperInstance", now);
                    if (since > 0) { since = 0; Settings.SetString("HelperSeq", "0"); ev = Request("EVENTS\t0"); }
                }
                long last = since;
                foreach (var line in ev.Skip(1))
                {
                    var f = line.Split('\t');
                    if (f.Length < 5) continue;
                    long s = long.Parse(f[0]);
                    Activity.AddAt(new DateTime(long.Parse(f[1])), (Category)int.Parse(f[2]), (Severity)int.Parse(f[3]), f[4]);
                    last = Math.Max(last, s);
                }
                if (last != since) Settings.SetString("HelperSeq", last.ToString());
            }

            // Tráfico de red por programa y por conexión.
            var net = Request("NET");
            var byPid = new Dictionary<int, NetTraffic>();
            var byConn = new Dictionary<string, NetTraffic>();
            foreach (var line in net)
            {
                var f = line.Split('\t');
                if (f[0] == "P" && f.Length >= 6)
                    byPid[int.Parse(f[1])] = T(f, 2);
                else if (f[0] == "C" && f.Length >= 9)
                    byConn[ConnKey(int.Parse(f[1]), f[2], f[3], int.Parse(f[4]))] = T(f, 5);
            }
            NetByPid = byPid;
            NetByConn = byConn;

            // Archivos: solo mientras la página está abierta.
            if (WantFiles != filesOn) { Request("FILES\t" + (WantFiles ? "on" : "off")); filesOn = WantFiles; }
            if (WantFiles)
            {
                var list = new List<FileStat>();
                foreach (var line in Request("FILESTATS"))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7) continue;
                    list.Add(new FileStat
                    {
                        Pid = int.Parse(f[1]), Path = f[2], ReadBps = D(f[3]), WriteBps = D(f[4]),
                        ReadTotal = long.Parse(f[5]), WriteTotal = long.Parse(f[6])
                    });
                }
                Files = list;
            }

            if ((DateTime.UtcNow - lastSmart).TotalSeconds > 60)
            {
                lastSmart = DateTime.UtcNow;
                var map = new Dictionary<int, SmartInfo>();
                foreach (var line in Request("SMART"))
                {
                    var f = line.Split('\t');
                    if (f.Length < 10 || f[0] != "S") continue;
                    int disk;
                    if (!int.TryParse(f[1], out disk)) continue;
                    map[disk] = new SmartInfo
                    {
                        Disk = disk, Health = I(f[2]) ?? 5, Temp = I(f[3]), TempMax = I(f[4]), Wear = I(f[5]),
                        Hours = I(f[6]), ReadErrors = I(f[7]), WriteErrors = I(f[8]), Cycles = I(f[9])
                    };
                }
                Smart = map;
                CheckSmartAlerts(map);
            }

            if ((DateTime.UtcNow - lastFw).TotalSeconds > 15) RefreshBlocked();
            Raise();
        }

        void RefreshBlocked()
        {
            lastFw = DateTime.UtcNow;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Request("FWLIST"))
            {
                var f = line.Split('\t');
                if (f.Length >= 2 && f[0] == "B") set.Add(f[1]);
            }
            Blocked = set;
        }

        static NetTraffic T(string[] f, int i)
        {
            return new NetTraffic { SentBps = D(f[i]), RecvBps = D(f[i + 1]), SentTotal = long.Parse(f[i + 2]), RecvTotal = long.Parse(f[i + 3]) };
        }

        static double D(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
        static int? I(string s) { int v; return int.TryParse(s, out v) ? (int?)v : null; }

        public static string ConnKey(int pid, string proto, string remote, int port)
        {
            return pid + "|" + proto + "|" + remote + "|" + port;
        }

        void Raise() { var h = Changed; if (h != null) try { h(); } catch { } }

        // ---- Alertas de salud de los discos (una vez por disco y problema)
        readonly HashSet<string> smartAlerted = new HashSet<string>();

        void CheckSmartAlerts(Dictionary<int, SmartInfo> map)
        {
            foreach (var s in map.Values)
            {
                if (s.Health == 1 || s.Health == 2)
                    Alert("h" + s.Disk + s.Health, Severity.Error, "Disco " + s.Disk + ": salud " + s.HealthName.ToLowerInvariant() + " según SMART. Conviene hacer una copia de seguridad.");
                if (s.Wear.HasValue && s.Wear.Value >= 90)
                    Alert("w" + s.Disk, Severity.Advertencia, "Disco " + s.Disk + ": el SSD tiene un desgaste del " + s.Wear.Value + " %.");
                if (s.Temp.HasValue && s.Temp.Value >= 65)
                    Alert("t" + s.Disk, Severity.Advertencia, "Disco " + s.Disk + ": temperatura alta (" + s.Temp.Value + " °C).");
                if ((s.ReadErrors ?? 0) + (s.WriteErrors ?? 0) > 0)
                    Alert("e" + s.Disk + "-" + ((s.ReadErrors ?? 0) + (s.WriteErrors ?? 0)), Severity.Advertencia,
                        "Disco " + s.Disk + ": " + ((s.ReadErrors ?? 0) + (s.WriteErrors ?? 0)) + " errores de lectura o escritura sin corregir.");
            }
        }

        void Alert(string key, Severity sev, string msg)
        {
            if (smartAlerted.Add(key)) Activity.Add(Category.Alerta, sev, msg);
        }

        // ---------------------------------------------------------------- comandos

        /// <summary>Ejecuta un comando en el ayudante. Devuelve null si salió bien, o el mensaje de error.</summary>
        public string Command(params string[] parts)
        {
            if (!Connected) return "Las funciones de administrador no están activas. Activalas en Configuración.";
            try
            {
                var r = Request(string.Join("\t", parts));
                if (r.Count > 0 && r[0].StartsWith("ERR")) return r[0].Length > 4 ? r[0].Substring(4) : "Error";
                if (parts[0] == "FWBLOCK" || parts[0] == "FWUNBLOCK") RefreshBlocked();
                return null;
            }
            catch (Exception ex) { Disconnect(); return ex.Message; }
        }

        public string[] Query(params string[] parts)
        {
            if (!Connected) return null;
            try
            {
                var r = Request(string.Join("\t", parts));
                return r.Count > 0 && r[0].StartsWith("OK") ? r[0].Split('\t') : null;
            }
            catch { Disconnect(); return null; }
        }

        // ---------------------------------------------------------------- instalación

        static bool TaskExists()
        {
            return Run("schtasks.exe", "/Query /TN \"" + HelperService.TaskName + "\"") == 0;
        }

        /// <summary>Activa o desactiva las funciones de administrador (muestra el aviso de UAC).</summary>
        public bool SetInstalled(bool on, out string error)
        {
            error = null;
            try
            {
                if (!on && Connected) Command("QUIT");
                var psi = new ProcessStartInfo(Application.ExecutablePath, on ? "--install-helper" : "--uninstall-helper")
                { Verb = "runas", UseShellExecute = true };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0) { error = "La instalación terminó con el código " + p.ExitCode + "."; return false; }
                }
            }
            catch (System.ComponentModel.Win32Exception) { error = "Se canceló el permiso de administrador."; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
            taskExists = on;
            lastTaskCheck = DateTime.UtcNow;
            lastLaunch = DateTime.MinValue;
            if (!on) { Disconnect(); NetByPid = new Dictionary<int, NetTraffic>(); NetByConn = new Dictionary<string, NetTraffic>(); Smart = new Dictionary<int, SmartInfo>(); }
            Raise();
            return true;
        }

        static int Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public void Dispose()
        {
            disposed = true;
            Disconnect();
        }
    }
}
