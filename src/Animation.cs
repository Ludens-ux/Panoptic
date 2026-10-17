using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>
    /// Reloj de alta precisión para las animaciones. DateTime.Now en .NET Framework solo avanza cada ~15,6 ms,
    /// lo que hace que algunos cuadros repitan posición y otros salten.
    /// </summary>
    internal static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double Ms { get { return sw.Elapsed.TotalMilliseconds; } }
    }

    /// <summary>
    /// Lienzo de una ventana en capas: un DIB de 32 bits premultiplicado sobre el que se dibuja con GDI+ y que se
    /// entrega a Windows con UpdateLayeredWindow (transparencia por píxel, sin copias intermedias).
    /// </summary>
    internal sealed class LayeredCanvas : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);

        public readonly int Width, Height;
        readonly IntPtr dib, dc, old;
        readonly Bitmap bitmap;
        public readonly Graphics Graphics;

        public LayeredCanvas(int w, int h)
        {
            Width = Math.Max(1, w); Height = Math.Max(1, h);
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            dib = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            dc = CreateCompatibleDC(IntPtr.Zero);
            old = SelectObject(dc, dib);
            // El Bitmap usa la misma memoria del DIB: lo que dibuja GDI+ es lo que recibe Windows.
            bitmap = new Bitmap(Width, Height, Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
            Graphics = Graphics.FromImage(bitmap);
            Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        }

        public void Present(IntPtr hwnd, System.Drawing.Point screen)
        {
            Graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            GdiFlush();
            var dst = new POINT { X = screen.X, Y = screen.Y };
            var size = new SIZE { W = Width, H = Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { Op = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }

        public void Dispose()
        {
            Graphics.Dispose();
            bitmap.Dispose();
            SelectObject(dc, old);
            DeleteDC(dc);
            DeleteObject(dib);
        }
    }

    /// <summary>
    /// Ciclo de animación sincronizado con el refresco de la pantalla: espera cada composición de DWM
    /// (DwmFlush, que hace de vsync) y llama a <c>frame</c> en el hilo de la interfaz. Solo corre mientras hay
    /// una animación; mientras tanto sube la precisión del temporizador del sistema a 1 ms.
    /// </summary>
    internal sealed class FrameLoop : IDisposable
    {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        readonly Control owner;
        readonly Action frame;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;
        volatile bool running, disposed;
        int pending;
        bool highResTimer;

        public FrameLoop(Control owner, Action frame)
        {
            this.owner = owner;
            this.frame = frame;
        }

        public bool Running { get { return running; } }

        public void Start()
        {
            if (running || disposed) return;
            running = true;
            if (thread == null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "Animación" };
                thread.Start();
            }
            wake.Set();
        }

        public void Stop()
        {
            running = false;
            if (highResTimer) { timeEndPeriod(1); highResTimer = false; }
        }

        /// <summary>Duración de un cuadro del monitor principal (16,7 ms a 60 Hz, 6,9 ms a 144 Hz).</summary>
        static double FramePeriod()
        {
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1)
                    return 1000.0 / dm.dmDisplayFrequency;
            }
            catch { }
            return 1000.0 / 60;
        }

        void Run()
        {
            double period = FramePeriod();
            double lastPeriodCheck = Clock.Ms;
            while (!disposed)
            {
                if (!running) { wake.WaitOne(); period = FramePeriod(); continue; }
                double t0 = Clock.Ms;
                // DwmFlush espera la próxima composición. Si DWM no tiene nada que componer puede volver al
                // instante: en ese caso se completa el cuadro con una espera corta para no girar en vacío.
                bool ok = false;
                try { ok = DwmFlush() == 0; } catch { }
                double el = Clock.Ms - t0;
                if (!ok || el < period * 0.5)
                {
                    // Sin vsync de DWM: se espera con el temporizador, en precisión de 1 ms solo mientras haga falta.
                    if (!highResTimer) { timeBeginPeriod(1); highResTimer = true; }
                    Thread.Sleep(Math.Max(1, (int)(period - el)));
                }
                if (Clock.Ms - lastPeriodCheck > 5000) { period = FramePeriod(); lastPeriodCheck = Clock.Ms; }
                if (!running) continue;
                // Un solo cuadro pendiente a la vez: si la interfaz está ocupada, no se acumulan.
                if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
                {
                    try { owner.BeginInvoke(new Action(DoFrame)); }
                    catch { Interlocked.Exchange(ref pending, 0); }
                }
            }
        }

        void DoFrame()
        {
            Interlocked.Exchange(ref pending, 0);
            if (running && !owner.IsDisposed) frame();
        }

        public void Dispose()
        {
            Stop();
            disposed = true;
            wake.Set();
        }
    }
}
