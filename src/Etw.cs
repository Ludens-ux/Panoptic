using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Panoptic
{
    /// <summary>
    /// Sesión de ETW en tiempo real (sin librerías externas). Recibe los eventos de los proveedores del kernel
    /// (procesos, red, archivos) en un hilo propio. Requiere permisos de administrador y un proceso de 64 bits.
    /// </summary>
    internal sealed class EtwSession : IDisposable
    {
        // ---- API de ETW
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint StartTrace(out ulong handle, string name, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint control);

        [DllImport("advapi32.dll")]
        static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", ExactSpelling = true)]
        static extern ulong OpenTrace(IntPtr logfile);

        [DllImport("advapi32.dll")]
        static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

        [DllImport("advapi32.dll")]
        static extern uint CloseTrace(ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void EventRecordCallback(IntPtr record);

        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_CONTROL_CODE_DISABLE_PROVIDER = 0, EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint EVENT_TRACE_REAL_TIME_MODE = 0x100;
        const uint WNODE_FLAG_TRACED_GUID = 0x20000;
        const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

        // Tamaños y desplazamientos en 64 bits (WNODE_HEADER 48 bytes, EVENT_TRACE_PROPERTIES 120 bytes,
        // EVENT_TRACE_LOGFILEW 448 bytes, EVENT_RECORD 112 bytes).
        const int PropsSize = 120, NameBytes = 1024;
        const int LogfileSize = 448;

        public static bool Supported { get { return IntPtr.Size == 8; } }

        readonly string name;
        ulong session, trace = INVALID_PROCESSTRACE_HANDLE;
        IntPtr logfile = IntPtr.Zero;
        readonly EventRecordCallback callback;
        Thread thread;
        readonly Action<IntPtr> onEvent;

        public EtwSession(string name, Action<IntPtr> onEvent)
        {
            this.name = name;
            this.onEvent = onEvent;
            callback = Record; // se guarda en un campo para que el recolector no lo libere
        }

        IntPtr Properties()
        {
            IntPtr p = Marshal.AllocHGlobal(PropsSize + NameBytes);
            for (int i = 0; i < PropsSize + NameBytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, PropsSize + NameBytes);                  // Wnode.BufferSize
            byte[] g = Guid.NewGuid().ToByteArray();
            Marshal.Copy(g, 0, p + 24, 16);                                    // Wnode.Guid
            Marshal.WriteInt32(p, 40, 1);                                      // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, (int)WNODE_FLAG_TRACED_GUID);            // Wnode.Flags
            Marshal.WriteInt32(p, 48, 64);                                     // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 8);                                      // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);                                     // MaximumBuffers
            Marshal.WriteInt32(p, 64, (int)EVENT_TRACE_REAL_TIME_MODE);        // LogFileMode
            Marshal.WriteInt32(p, 68, 1);                                      // FlushTimer (s)
            Marshal.WriteInt32(p, 116, PropsSize);                             // LoggerNameOffset
            return p;
        }

        /// <summary>Detiene una sesión con este nombre que haya quedado de una ejecución anterior.</summary>
        public static void StopExisting(string name)
        {
            var s = new EtwSession(name, null);
            IntPtr p = s.Properties();
            try { ControlTrace(0, name, p, EVENT_TRACE_CONTROL_STOP); } finally { Marshal.FreeHGlobal(p); }
        }

        public void Start()
        {
            StopExisting(name);
            IntPtr p = Properties();
            try
            {
                uint r = StartTrace(out session, name, p);
                if (r != 0) throw new System.ComponentModel.Win32Exception((int)r);
            }
            finally { Marshal.FreeHGlobal(p); }

            logfile = Marshal.AllocHGlobal(LogfileSize);
            for (int i = 0; i < LogfileSize; i++) Marshal.WriteByte(logfile, i, 0);
            IntPtr namePtr = Marshal.StringToHGlobalUni(name);
            Marshal.WriteIntPtr(logfile, 8, namePtr);                                                         // LoggerName
            Marshal.WriteInt32(logfile, 28, (int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD)); // ProcessTraceMode
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));               // EventRecordCallback
            trace = OpenTrace(logfile);
            if (trace == INVALID_PROCESSTRACE_HANDLE) throw new InvalidOperationException("OpenTrace falló.");
            thread = new Thread(() =>
            {
                lock (diag) diag[name] = "corriendo";
                try { uint r = ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); lock (diag) diag[name] = "fin " + r; }
                catch (Exception ex) { lock (diag) diag[name] = ex.GetType().Name; }
            })
            { IsBackground = true, Name = "ETW " + name };
            thread.Start();
        }

        public void Enable(Guid provider, ulong keywords, byte level)
        {
            uint r = EnableTraceEx2(session, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, level, keywords, 0, 0, IntPtr.Zero);
            if (r != 0) throw new System.ComponentModel.Win32Exception((int)r);
        }

        public void Disable(Guid provider)
        {
            EnableTraceEx2(session, ref provider, EVENT_CONTROL_CODE_DISABLE_PROVIDER, 0, 0, 0, 0, IntPtr.Zero);
        }

        void Record(IntPtr rec)
        {
            Interlocked.Increment(ref received);
            try { if (onEvent != null) onEvent(rec); } catch { Interlocked.Increment(ref failed); }
        }

        // ---- Diagnóstico: eventos recibidos, con error, y cómo terminó cada sesión.
        static long received, failed;
        static readonly System.Collections.Generic.Dictionary<string, string> diag = new System.Collections.Generic.Dictionary<string, string>();

        public static string Diagnostics
        {
            get
            {
                var sb = new StringBuilder("recibidos=" + Interlocked.Read(ref received) + " errores=" + Interlocked.Read(ref failed));
                lock (diag) foreach (var kv in diag) sb.Append(" " + kv.Key + ":" + kv.Value);
                return sb.ToString();
            }
        }

        // ---- Lectura de EVENT_RECORD (64 bits)
        public static int ProcessId(IntPtr rec) { return Marshal.ReadInt32(rec, 12); }
        public static Guid Provider(IntPtr rec) { var b = new byte[16]; Marshal.Copy(rec + 24, b, 0, 16); return new Guid(b); }
        public static int Id(IntPtr rec) { return (ushort)Marshal.ReadInt16(rec, 40); }
        public static int Version(IntPtr rec) { return Marshal.ReadByte(rec, 42); }
        public static int DataLength(IntPtr rec) { return (ushort)Marshal.ReadInt16(rec, 86); }
        public static IntPtr Data(IntPtr rec) { return Marshal.ReadIntPtr(rec, 96); }

        /// <summary>Lee una cadena UTF-16 terminada en cero dentro de los datos del evento.</summary>
        public static string String(IntPtr data, int offset, int length)
        {
            if (offset >= length) return "";
            var sb = new StringBuilder();
            for (int i = offset; i + 1 < length; i += 2)
            {
                char c = (char)Marshal.ReadInt16(data, i);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            try
            {
                IntPtr p = Properties();
                try { ControlTrace(session, null, p, EVENT_TRACE_CONTROL_STOP); } finally { Marshal.FreeHGlobal(p); }
            }
            catch { }
            if (trace != INVALID_PROCESSTRACE_HANDLE) CloseTrace(trace);
            trace = INVALID_PROCESSTRACE_HANDLE;
        }
    }
}
