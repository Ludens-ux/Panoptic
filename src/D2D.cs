using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Panoptic
{
    /// <summary>
    /// Dibujo con Direct2D (placa de video) y texto con DirectWrite, sin librerías externas: las funciones de
    /// Windows se llaman por su posición en la tabla de cada interfaz (el orden está verificado con las
    /// definiciones públicas de d2d1.h y dwrite.h). Dibuja sobre el lienzo de una ventana en capas (un DC con
    /// transparencia por píxel); si algo falla, quien lo usa vuelve a GDI+.
    /// </summary>
    internal sealed class D2DPainter : IDisposable
    {
        // ---------------------------------------------------------------- estructuras
        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)] struct RectF { public float L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct PointF2 { public float X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SizeU { public uint W, H; }
        [StructLayout(LayoutKind.Sequential)] struct RoundedRect { public RectF Rect; public float RX, RY; }
        [StructLayout(LayoutKind.Sequential)] struct EllipseF { public PointF2 Center; public float RX, RY; }
        [StructLayout(LayoutKind.Sequential)] struct Matrix { public float M11, M12, M21, M22, Dx, Dy; }
        [StructLayout(LayoutKind.Sequential)] struct PixelFormat2 { public uint Format; public int AlphaMode; }
        [StructLayout(LayoutKind.Sequential)] struct RTProps { public int Type; public PixelFormat2 PixelFormat; public float DpiX, DpiY; public int Usage, MinLevel; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapProps { public PixelFormat2 PixelFormat; public float DpiX, DpiY; }
        [StructLayout(LayoutKind.Sequential)] struct StrokeProps { public int StartCap, EndCap, DashCap, LineJoin; public float MiterLimit; public int DashStyle; public float DashOffset; }
        [StructLayout(LayoutKind.Sequential)] struct ArcSegment { public PointF2 Point; public float W, H, Rotation; public int Sweep, ArcSize; }
        [StructLayout(LayoutKind.Sequential)] struct LayerParams { public RectF Bounds; public IntPtr Mask; public int MaskAA; public Matrix MaskTransform; public float Opacity; public IntPtr OpacityBrush; public int Options; }
        [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct Trimming { public int Granularity; public uint Delimiter, DelimiterCount; }
        [StructLayout(LayoutKind.Sequential)] struct TextMetrics { public float Left, Top, Width, WidthWithSpace, Height, LayoutWidth, LayoutHeight; public uint MaxBidi, LineCount; }

        static readonly Matrix Identity = new Matrix { M11 = 1, M22 = 1 };

        // ---------------------------------------------------------------- funciones exportadas
        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);

        // ---------------------------------------------------------------- métodos (por posición en la tabla)
        delegate uint ReleaseFn(IntPtr self);
        // ID2D1Factory
        delegate int CreateRoundedRectGeometryFn(IntPtr self, ref RoundedRect r, out IntPtr geo);           // 6
        delegate int CreatePathGeometryFn(IntPtr self, out IntPtr geo);                                     // 10
        delegate int CreateStrokeStyleFn(IntPtr self, ref StrokeProps p, IntPtr dashes, uint count, out IntPtr style); // 11
        delegate int CreateDCRenderTargetFn(IntPtr self, ref RTProps p, out IntPtr rt);                     // 16
        // ID2D1RenderTarget
        delegate int CreateBitmapFn(IntPtr self, SizeU size, IntPtr data, uint pitch, ref BitmapProps p, out IntPtr bmp); // 4
        delegate int CreateSolidColorBrushFn(IntPtr self, ref ColorF c, IntPtr props, out IntPtr brush);   // 8
        delegate void FillRectangleFn(IntPtr self, ref RectF r, IntPtr brush);                              // 17
        delegate void DrawRoundedRectangleFn(IntPtr self, ref RoundedRect r, IntPtr brush, float w, IntPtr style); // 18
        delegate void FillRoundedRectangleFn(IntPtr self, ref RoundedRect r, IntPtr brush);                 // 19
        delegate void DrawEllipseFn(IntPtr self, ref EllipseF e, IntPtr brush, float w, IntPtr style);      // 20
        delegate void DrawGeometryFn(IntPtr self, IntPtr geo, IntPtr brush, float w, IntPtr style);         // 22
        delegate void FillGeometryFn(IntPtr self, IntPtr geo, IntPtr brush, IntPtr opacityBrush);           // 23
        delegate void DrawBitmapFn(IntPtr self, IntPtr bmp, ref RectF dst, float opacity, int interp, IntPtr src); // 26
        delegate void DrawTextFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, uint len, IntPtr format, ref RectF r, IntPtr brush, int options, int measuring); // 27
        delegate void SetTransformFn(IntPtr self, ref Matrix m);                                            // 30
        delegate void SetModeFn(IntPtr self, int mode);                                                     // 32, 34
        delegate void PushLayerFn(IntPtr self, ref LayerParams p, IntPtr layer);                            // 40
        delegate void VoidFn(IntPtr self);                                                                  // 41, 46, 48
        delegate void PushClipFn(IntPtr self, ref RectF r, int aa);                                         // 45
        delegate void ClearFn(IntPtr self, ref ColorF c);                                                   // 47
        delegate int EndDrawFn(IntPtr self, IntPtr tag1, IntPtr tag2);                                      // 49
        delegate int BindDCFn(IntPtr self, IntPtr hdc, ref Rect32 r);                                       // 57
        // ID2D1SolidColorBrush
        delegate void SetColorFn(IntPtr self, ref ColorF c);                                                // 8
        // ID2D1PathGeometry / ID2D1GeometrySink
        delegate int OpenFn(IntPtr self, out IntPtr sink);                                                  // 17
        delegate void SetFillModeFn(IntPtr self, int mode);                                                 // 3
        delegate void BeginFigureFn(IntPtr self, PointF2 p, int begin);                                     // 5
        delegate void AddLinesFn(IntPtr self, [In] PointF2[] pts, uint count);                              // 6
        delegate void EndFigureFn(IntPtr self, int end);                                                    // 8
        delegate int CloseFn(IntPtr self);                                                                  // 9
        delegate void AddArcFn(IntPtr self, ref ArcSegment a);                                              // 14
        // IDWriteFactory / IDWriteTextFormat / IDWriteTextLayout
        delegate int CreateTextFormatFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch, float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format); // 15
        delegate int CreateTextLayoutFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, uint len, IntPtr format, float maxW, float maxH, out IntPtr layout); // 18
        delegate int CreateEllipsisFn(IntPtr self, IntPtr format, out IntPtr sign);                         // 20
        delegate int SetIntFn(IntPtr self, int value);                                                      // 3, 4, 5
        delegate int SetTrimmingFn(IntPtr self, ref Trimming t, IntPtr sign);                               // 9
        delegate int GetMetricsFn(IntPtr self, out TextMetrics m);                                          // 60

        static readonly Dictionary<IntPtr, Delegate> fnCache = new Dictionary<IntPtr, Delegate>();

        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr fp = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            Delegate d;
            if (!fnCache.TryGetValue(fp, out d)) fnCache[fp] = d = Marshal.GetDelegateForFunctionPointer(fp, typeof(T));
            return (T)(object)d;
        }

        static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Fn<ReleaseFn>(p, 2)(p); p = IntPtr.Zero; }
        }

        static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException(what + " falló (0x" + hr.ToString("X8") + ")"); }

        // ---------------------------------------------------------------- recursos compartidos
        static IntPtr factory, dwrite, roundStroke;
        static bool unavailable;

        IntPtr rt, brush;
        Size bound;
        readonly Dictionary<Bitmap, IntPtr> bitmaps = new Dictionary<Bitmap, IntPtr>();
        readonly Stack<Matrix> transforms = new Stack<Matrix>();
        Matrix current = Identity;

        /// <summary>Crea el pintor, o devuelve null si Direct2D no está disponible (escritorio remoto, sin placa de video…).</summary>
        public static D2DPainter TryCreate()
        {
            if (unavailable || IntPtr.Size != 8) return null;
            try
            {
                if (factory == IntPtr.Zero)
                {
                    var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");   // ID2D1Factory
                    Check(D2D1CreateFactory(0, ref iid, IntPtr.Zero, out factory), "D2D1CreateFactory");
                    var iw = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");    // IDWriteFactory
                    Check(DWriteCreateFactory(0, ref iw, out dwrite), "DWriteCreateFactory");
                    var sp = new StrokeProps { StartCap = 2, EndCap = 2, DashCap = 0, LineJoin = 2, MiterLimit = 10 };
                    Check(Fn<CreateStrokeStyleFn>(factory, 11)(factory, ref sp, IntPtr.Zero, 0, out roundStroke), "CreateStrokeStyle");
                }
                var p = new D2DPainter();
                p.CreateTarget();
                return p;
            }
            catch { unavailable = true; return null; }
        }

        void CreateTarget()
        {
            var props = new RTProps
            {
                Type = 0,                                                                  // por placa de video si se puede
                PixelFormat = new PixelFormat2 { Format = 87, AlphaMode = 1 },            // B8G8R8A8, alfa premultiplicado
                DpiX = 96, DpiY = 96                                                       // 1 unidad = 1 píxel
            };
            Check(Fn<CreateDCRenderTargetFn>(factory, 16)(factory, ref props, out rt), "CreateDCRenderTarget");
            var c = new ColorF { A = 1 };
            Check(Fn<CreateSolidColorBrushFn>(rt, 8)(rt, ref c, IntPtr.Zero, out brush), "CreateSolidColorBrush");
            Fn<SetModeFn>(rt, 34)(rt, 2);   // texto en escala de grises: ClearType no funciona sobre transparencia
            bound = Size.Empty;
        }

        void ReleaseTarget()
        {
            foreach (var b in bitmaps.Values) { var x = b; Release(ref x); }
            bitmaps.Clear();
            Release(ref brush);
            Release(ref rt);
        }

        // ---------------------------------------------------------------- cuadro
        /// <summary>
        /// Empieza a dibujar sobre una zona del DC del lienzo. Las coordenadas siguen siendo las del lienzo:
        /// solo se dibuja (y se copia de la placa de video) la zona indicada.
        /// </summary>
        public void Begin(IntPtr hdc, Rectangle zone)
        {
            if (rt == IntPtr.Zero) CreateTarget();
            var r = new Rect32 { L = zone.Left, T = zone.Top, R = zone.Right, B = zone.Bottom };
            Check(Fn<BindDCFn>(rt, 57)(rt, hdc, ref r), "BindDC");
            bound = zone.Size;
            Fn<VoidFn>(rt, 48)(rt);   // BeginDraw
            transforms.Clear();
            current = new Matrix { M11 = 1, M22 = 1, Dx = -zone.X, Dy = -zone.Y };
            Apply();
        }

        /// <summary>Termina el cuadro. Devuelve false si hubo que descartar el dispositivo (se recrea en el próximo).</summary>
        public bool End()
        {
            int hr = Fn<EndDrawFn>(rt, 49)(rt, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0) { ReleaseTarget(); return false; }
            return true;
        }

        // ---------------------------------------------------------------- primitivas
        static ColorF ToF(Color c) { return new ColorF { R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f }; }
        static RectF ToF(RectangleF r) { return new RectF { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom }; }

        IntPtr Brush(Color c)
        {
            var f = ToF(c);
            Fn<SetColorFn>(brush, 8)(brush, ref f);
            return brush;
        }

        public void Clear(Color c) { var f = ToF(c); Fn<ClearFn>(rt, 47)(rt, ref f); }

        public void FillRect(RectangleF r, Color c)
        {
            if (c.A == 0) return;
            var f = ToF(r);
            Fn<FillRectangleFn>(rt, 17)(rt, ref f, Brush(c));
        }

        public void FillRound(RectangleF r, float radius, Color c)
        {
            if (c.A == 0) return;
            var rr = new RoundedRect { Rect = ToF(r), RX = radius, RY = radius };
            Fn<FillRoundedRectangleFn>(rt, 19)(rt, ref rr, Brush(c));
        }

        public void StrokeRound(RectangleF r, float radius, Color c, float width)
        {
            if (c.A == 0) return;
            var rr = new RoundedRect { Rect = ToF(r), RX = radius, RY = radius };
            Fn<DrawRoundedRectangleFn>(rt, 18)(rt, ref rr, Brush(c), width, IntPtr.Zero);
        }

        public void StrokeEllipse(RectangleF r, Color c, float width)
        {
            var e = new EllipseF { Center = new PointF2 { X = r.X + r.Width / 2, Y = r.Y + r.Height / 2 }, RX = r.Width / 2, RY = r.Height / 2 };
            Fn<DrawEllipseFn>(rt, 20)(rt, ref e, Brush(c), width, IntPtr.Zero);
        }

        /// <summary>Arco con extremos redondeados (ángulos en grados, 0 = a la derecha, sentido horario).</summary>
        public void StrokeArc(RectangleF r, float startDeg, float sweepDeg, Color c, float width)
        {
            sweepDeg = Math.Min(359.9f, sweepDeg);
            if (sweepDeg <= 0) return;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = r.Width / 2, ry = r.Height / 2;
            double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
            WithPath(sink =>
            {
                Fn<BeginFigureFn>(sink, 5)(sink, new PointF2 { X = cx + rx * (float)Math.Cos(a0), Y = cy + ry * (float)Math.Sin(a0) }, 1);
                var arc = new ArcSegment
                {
                    Point = new PointF2 { X = cx + rx * (float)Math.Cos(a1), Y = cy + ry * (float)Math.Sin(a1) },
                    W = rx, H = ry, Rotation = 0, Sweep = 1, ArcSize = sweepDeg > 180 ? 1 : 0
                };
                Fn<AddArcFn>(sink, 14)(sink, ref arc);
                Fn<EndFigureFn>(sink, 8)(sink, 0);
            }, geo => Fn<DrawGeometryFn>(rt, 22)(rt, geo, Brush(c), width, roundStroke));
        }

        /// <summary>Línea quebrada con uniones redondeadas.</summary>
        public void Polyline(PointF[] pts, Color c, float width)
        {
            if (pts == null || pts.Length < 2) return;
            WithPath(sink => Figure(sink, pts, 1, 0), geo => Fn<DrawGeometryFn>(rt, 22)(rt, geo, Brush(c), width, roundStroke));
        }

        public void FillPolygon(PointF[] pts, Color c)
        {
            if (pts == null || pts.Length < 3 || c.A == 0) return;
            WithPath(sink => Figure(sink, pts, 0, 1), geo => Fn<FillGeometryFn>(rt, 23)(rt, geo, Brush(c), IntPtr.Zero));
        }

        static void Figure(IntPtr sink, PointF[] pts, int begin, int end)
        {
            Fn<BeginFigureFn>(sink, 5)(sink, new PointF2 { X = pts[0].X, Y = pts[0].Y }, begin);
            var rest = new PointF2[pts.Length - 1];
            for (int i = 1; i < pts.Length; i++) rest[i - 1] = new PointF2 { X = pts[i].X, Y = pts[i].Y };
            Fn<AddLinesFn>(sink, 6)(sink, rest, (uint)rest.Length);
            Fn<EndFigureFn>(sink, 8)(sink, end);
        }

        static void WithPath(Action<IntPtr> build, Action<IntPtr> use)
        {
            IntPtr geo = IntPtr.Zero, sink = IntPtr.Zero;
            try
            {
                Check(Fn<CreatePathGeometryFn>(factory, 10)(factory, out geo), "CreatePathGeometry");
                Check(Fn<OpenFn>(geo, 17)(geo, out sink), "Open");
                Fn<SetFillModeFn>(sink, 3)(sink, 0);
                build(sink);
                Check(Fn<CloseFn>(sink, 9)(sink), "Close");
                Release(ref sink);
                use(geo);
            }
            finally { Release(ref sink); Release(ref geo); }
        }

        // ---------------------------------------------------------------- imágenes
        public void Image(Bitmap src, RectangleF dest, float opacity)
        {
            if (src == null) return;
            IntPtr bmp;
            if (!bitmaps.TryGetValue(src, out bmp))
            {
                if (bitmaps.Count > 64) { foreach (var b in bitmaps.Values) { var x = b; Release(ref x); } bitmaps.Clear(); }
                var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var bp = new BitmapProps { PixelFormat = new PixelFormat2 { Format = 87, AlphaMode = 1 }, DpiX = 96, DpiY = 96 };
                    Check(Fn<CreateBitmapFn>(rt, 4)(rt, new SizeU { W = (uint)src.Width, H = (uint)src.Height }, data.Scan0, (uint)data.Stride, ref bp, out bmp), "CreateBitmap");
                }
                finally { src.UnlockBits(data); }
                bitmaps[src] = bmp;
            }
            var d = ToF(dest);
            Fn<DrawBitmapFn>(rt, 26)(rt, bmp, ref d, opacity, 1, IntPtr.Zero);
        }

        /// <summary>Olvida la copia en la placa de video de una imagen que cambió o se descartó.</summary>
        public void Forget(Bitmap src)
        {
            IntPtr b;
            if (src != null && bitmaps.TryGetValue(src, out b)) { Release(ref b); bitmaps.Remove(src); }
        }

        // ---------------------------------------------------------------- recortes, capas y transformaciones
        public void PushClip(RectangleF r) { var f = ToF(r); Fn<PushClipFn>(rt, 45)(rt, ref f, 1); }
        public void PopClip() { Fn<VoidFn>(rt, 46)(rt); }

        static LayerParams Layer(float opacity, IntPtr mask)
        {
            return new LayerParams
            {
                Bounds = new RectF { L = -1e7f, T = -1e7f, R = 1e7f, B = 1e7f },
                Mask = mask, MaskAA = 0, MaskTransform = Identity, Opacity = opacity, Options = 0
            };
        }

        public void PushOpacity(float opacity) { var p = Layer(opacity, IntPtr.Zero); Fn<PushLayerFn>(rt, 40)(rt, ref p, IntPtr.Zero); }

        /// <summary>Todo lo que se dibuje hasta PopLayer queda recortado por un rectángulo redondeado (bordes suavizados).</summary>
        public void PushRoundMask(RectangleF r, float radius)
        {
            var rr = new RoundedRect { Rect = ToF(r), RX = radius, RY = radius };
            IntPtr geo;
            Check(Fn<CreateRoundedRectGeometryFn>(factory, 6)(factory, ref rr, out geo), "CreateRoundedRectangleGeometry");
            try { var p = Layer(1, geo); Fn<PushLayerFn>(rt, 40)(rt, ref p, IntPtr.Zero); }
            finally { Release(ref geo); }   // la capa conserva su propia referencia
        }

        public void PopLayer() { Fn<VoidFn>(rt, 41)(rt); }

        void Apply() { var m = current; Fn<SetTransformFn>(rt, 30)(rt, ref m); }

        static Matrix Mul(Matrix a, Matrix b)   // primero a, después b
        {
            return new Matrix
            {
                M11 = a.M11 * b.M11 + a.M12 * b.M21, M12 = a.M11 * b.M12 + a.M12 * b.M22,
                M21 = a.M21 * b.M11 + a.M22 * b.M21, M22 = a.M21 * b.M12 + a.M22 * b.M22,
                Dx = a.Dx * b.M11 + a.Dy * b.M21 + b.Dx, Dy = a.Dx * b.M12 + a.Dy * b.M22 + b.Dy
            };
        }

        public void PushTranslate(float dx, float dy)
        {
            transforms.Push(current);
            current = Mul(new Matrix { M11 = 1, M22 = 1, Dx = dx, Dy = dy }, current);
            Apply();
        }

        public void PushScale(float scale, float cx, float cy)
        {
            transforms.Push(current);
            current = Mul(new Matrix { M11 = scale, M22 = scale, Dx = cx - scale * cx, Dy = cy - scale * cy }, current);
            Apply();
        }

        public void PopTransform()
        {
            if (transforms.Count > 0) current = transforms.Pop();
            Apply();
        }

        // ---------------------------------------------------------------- texto (DirectWrite)
        static readonly Dictionary<string, IntPtr> formats = new Dictionary<string, IntPtr>();

        /// <summary>Familia y peso de DirectWrite a partir del nombre de GDI ("Segoe UI Variable Text Semibold").</summary>
        static void Family(Font f, out string family, out int weight)
        {
            family = f.FontFamily.Name;
            weight = f.Bold ? 700 : 400;
            foreach (var suffix in new[] { " Semibold", " Semib", " SemiBold" })
                if (family.EndsWith(suffix)) { family = family.Substring(0, family.Length - suffix.Length); weight = 600; }
            if (family.EndsWith(" Light")) { family = family.Substring(0, family.Length - 6); weight = 300; }
        }

        static float PixelSize(Font f)
        {
            switch (f.Unit)
            {
                case GraphicsUnit.Point: return f.Size * 96f / 72f;
                case GraphicsUnit.Pixel: return f.Size;
                default: return f.SizeInPoints * 96f / 72f;
            }
        }

        static IntPtr Format(Font f, int h, int v, bool ellipsis)
        {
            string family; int weight;
            Family(f, out family, out weight);
            float size = PixelSize(f);
            string key = family + "|" + weight + "|" + size + "|" + h + "|" + v + "|" + ellipsis;
            IntPtr fmt;
            if (formats.TryGetValue(key, out fmt)) return fmt;
            Check(Fn<CreateTextFormatFn>(dwrite, 15)(dwrite, family, IntPtr.Zero, weight, f.Italic ? 2 : 0, 5, size, "es-ar", out fmt), "CreateTextFormat");
            Fn<SetIntFn>(fmt, 3)(fmt, h);       // alineación horizontal
            Fn<SetIntFn>(fmt, 4)(fmt, v);       // alineación vertical
            Fn<SetIntFn>(fmt, 5)(fmt, 1);       // sin salto de línea
            if (ellipsis)
            {
                IntPtr sign;
                if (Fn<CreateEllipsisFn>(dwrite, 20)(dwrite, fmt, out sign) >= 0)
                {
                    var t = new Trimming { Granularity = 1 };   // por carácter
                    Fn<SetTrimmingFn>(fmt, 9)(fmt, ref t, sign);
                    Release(ref sign);
                }
            }
            formats[key] = fmt;
            return fmt;
        }

        static void Align(TextFormatFlags flags, out int h, out int v)
        {
            h = (flags & TextFormatFlags.HorizontalCenter) != 0 ? 2 : (flags & TextFormatFlags.Right) != 0 ? 1 : 0;
            v = (flags & TextFormatFlags.VerticalCenter) != 0 ? 2 : (flags & TextFormatFlags.Bottom) != 0 ? 1 : 0;
        }

        public void Text(string text, Font f, RectangleF r, Color c, TextFormatFlags flags, bool ellipsis)
        {
            if (string.IsNullOrEmpty(text) || c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            int h, v;
            Align(flags, out h, out v);
            var fmt = Format(f, h, v, ellipsis);
            var rf = ToF(r);
            Fn<DrawTextFn>(rt, 27)(rt, text, (uint)text.Length, fmt, ref rf, Brush(c), 2 /* recortar al rectángulo */, 0);
        }

        /// <summary>Ancho del texto según DirectWrite (el mismo motor que lo dibuja).</summary>
        public static float Measure(string text, Font f)
        {
            if (string.IsNullOrEmpty(text) || dwrite == IntPtr.Zero) return 0;
            IntPtr layout = IntPtr.Zero;
            try
            {
                var fmt = Format(f, 0, 0, false);
                Check(Fn<CreateTextLayoutFn>(dwrite, 18)(dwrite, text, (uint)text.Length, fmt, 100000f, 100000f, out layout), "CreateTextLayout");
                TextMetrics m;
                Check(Fn<GetMetricsFn>(layout, 60)(layout, out m), "GetMetrics");
                return m.WidthWithSpace;
            }
            finally { Release(ref layout); }
        }

        public void Dispose() { ReleaseTarget(); }
    }
}
