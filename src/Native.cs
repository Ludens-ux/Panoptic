using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SysMonitor
{
    internal static class Native
    {
        // ---- DWM ----
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Fondo del sistema (Windows 11 22H2+): 1 ninguno, 2 Mica, 3 Acrílico, 4 Mica Alt.
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMSBT_NONE = 1, DWMSBT_MICA = 2, DWMSBT_ACRYLIC = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        // ---- Desenfoque con tinte configurable (SetWindowCompositionAttribute) ----
        public const int ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

        /// <summary>
        /// Desenfoque detrás de la ventana con un tinte propio (color y opacidad), más transparente que el
        /// Acrílico estándar. <paramref name="tint"/> es el color del tinte con su alfa.
        /// </summary>
        public static void SetAccentBlur(IntPtr hwnd, int state, System.Drawing.Color tint)
        {
            try
            {
                var accent = new ACCENT_POLICY();
                accent.AccentState = state;
                accent.GradientColor = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R; // AABBGGRR
                int size = Marshal.SizeOf(accent);
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, p, false);
                    var data = new WINCOMPATTRDATA { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, SizeOfData = size };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch { }
        }

        /// <summary>Aplica (o quita) Mica/Acrílico a la ventana.</summary>
        public static void SetBackdrop(IntPtr hwnd, int type)
        {
            try
            {
                var m = new MARGINS();
                if (type != DWMSBT_NONE) { m.Left = m.Right = m.Top = m.Bottom = -1; }
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4);
            }
            catch { }
        }

        public static int ColorRef(System.Drawing.Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // ---- DPI ----
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        public static float ScaleForPoint(System.Drawing.Point p)
        {
            try
            {
                POINT pt; pt.X = p.X; pt.Y = p.Y;
                IntPtr mon = MonitorFromPoint(pt, 2);
                uint x, y;
                if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) return x / 96f;
            }
            catch { }
            return 1f;
        }

        public static float ScaleForWindow(IntPtr hwnd)
        {
            try { uint d = GetDpiForWindow(hwnd); if (d > 0) return d / 96f; } catch { }
            return 1f;
        }

        public const int SPI_GETCLIENTAREAANIMATION = 0x1042;
        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(int action, int param, ref bool value, int winIni);

        // Se consulta muchas veces por cuadro durante las animaciones: se guarda 2 segundos.
        static bool animOn = true;
        static DateTime animAt = DateTime.MinValue;

        public static bool AnimationsEnabled()
        {
            if ((DateTime.UtcNow - animAt).TotalSeconds < 2) return animOn;
            bool on = true;
            try { SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0); } catch { }
            animAt = DateTime.UtcNow;
            return animOn = on;
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);

        /// <summary>
        /// SHQueryUserNotificationState: 2 = ocupado (app en pantalla completa), 3 = juego Direct3D en pantalla completa,
        /// 4 = modo presentación.
        /// </summary>
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        public static bool FullScreenAppRunning()
        {
            try
            {
                int st;
                if (SHQueryUserNotificationState(out st) != 0) return false;
                return st == 2 || st == 3 || st == 4;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // ---- Sistema ----
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint Low; public uint High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        /// <summary>Memoria en caché, identificadores, procesos e hilos del sistema en una sola llamada (mucho más rápida que los contadores).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

        // ---- Procesos ----
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_TERMINATE = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(IntPtr h, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS io);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo")]
        public static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX c, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr h, uint code);

        // ---- Ventanas (para separar aplicaciones de procesos en segundo plano) ----
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

        /// <summary>PIDs que tienen una ventana principal visible (lo que el Administrador de tareas llama "Aplicaciones").</summary>
        public static HashSet<int> PidsWithWindows()
        {
            var set = new HashSet<int>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, -20) & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
                int cloaked;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return true;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                set.Add(pid);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        // ---- Dispositivos ----
        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        public const int DBT_DEVTYP_VOLUME = 2;
        public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        public const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_HDR { public int dbch_size, dbch_devicetype, dbch_reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size, dbcc_devicetype, dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_VOLUME
        {
            public int dbcv_size, dbcv_devicetype, dbcv_reserved;
            public uint dbcv_unitmask;
            public ushort dbcv_flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);

        [DllImport("user32.dll")]
        public static extern bool UnregisterDeviceNotification(IntPtr h);
    }
}
