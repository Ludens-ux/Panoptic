using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Panoptic
{
    /// <summary>
    /// La mascota de Panoptic: un ojo con forma de lente (aro, cuerpo, iris con diafragma, pupila y brillo) que
    /// reacciona a lo que pasa en la PC. Se dibuja con vectores sobre una grilla de 48 unidades y se anima con
    /// resortes interrumpibles: cualquier cambio de estado parte del valor actual.
    /// </summary>
    internal sealed class Eye
    {
        public static readonly Color Accent = Theme.Hex(0xA98BF0);
        public static readonly Color HotIris = Theme.Hex(0xFF8A80);
        public static readonly Color OfflineIris = Theme.Hex(0x5A5A5A);
        static readonly Color Back = Theme.Hex(0x1C1C1C), Body = Theme.Hex(0x2B2B2B);
        static readonly Color RingDark = Theme.Hex(0xF3F3F3), RingLight = Theme.Hex(0x1A1A1A);
        static readonly Color Blade = Color.FromArgb(89, 0, 0, 0);   // negro al 35 %
        public const float RestPupil = 4.2f;

        // ---- Estado de base (lo fija la isla según la PC)
        float baseLid = 1, basePupil = RestPupil;
        Color baseIris = Accent;
        bool happy, dim, fixedPupil;
        float lookX, lookY;

        // ---- Reacciones pasajeras (en ms del Clock)
        double surpriseUntil = -1e9, lookDownUntil = -1e9, blinkStart = -1e9, flashStart = -1e9;
        public double NextBlink;
        readonly Random rnd = new Random();

        // ---- Resortes
        float lid = 1, vLid, px, vPx, py, vPy, pr = RestPupil, vPr, colorT = 1, vColor;
        Color irisFrom = Accent, irisTo = Accent;

        /// <summary>Punto de cámara o micrófono (Color.Empty: ninguno).</summary>
        public Color Dot = Color.Empty;

        public Eye() { NextBlink = Clock.Ms + rnd.Next(4000, 8000); }

        /// <summary>
        /// El ojo de la app: una sola lógica que dibujan la isla flotante y la cabecera de la ventana principal.
        /// </summary>
        public static readonly Eye Shared = new Eye();

        /// <summary>Animado: con "Ojo animado" activado y con las animaciones de Windows encendidas.</summary>
        public static bool Animated { get { return Settings.EyeAnimated && Native.AnimationsEnabled(); } }

        // ---- Lo que informa la isla (eventos que solo ella vigila)
        public bool Offline, Camera, Microphone;
        public DateTime GuardUntil = DateTime.MinValue, HappyUntil = DateTime.MinValue;
        DateTime lastObserve = DateTime.MinValue;

        /// <summary>
        /// Estado según la PC. Prioridad: en guardia > sin internet > caliente > concentrado > contento > dormido >
        /// reposo (las reacciones pasajeras, como sorpresa o destello, van por encima de todo).
        /// </summary>
        public void Observe(Snapshot snap)
        {
            var now = DateTime.Now;
            lastObserve = now;
            bool offline = Offline || (snap != null && snap.Nets.Count == 0);
            bool hot = snap != null && !double.IsNaN(snap.Temperature) && snap.Temperature >= Settings.TempThreshold;
            bool busy = snap != null && snap.Cpu > 80;
            bool asleep = Native.IdleMs() > 5 * 60 * 1000;
            if (now < GuardUntil) SetState(0.9f, 2.2f, Accent, false, false, true);
            else if (offline) SetState(1, 2.5f, OfflineIris, false, true, false);
            else if (hot) SetState(0.8f, RestPupil, HotIris, false, false, false);
            else if (busy) SetState(0.45f, RestPupil, Accent, false, false, false);
            else if (now < HappyUntil) SetState(1, RestPupil, Accent, true, false, false);
            else if (asleep) SetState(0, RestPupil, Accent, false, false, false);
            else SetState(1, RestPupil, Accent, false, false, false);
            Dot = Camera ? Theme.Hex(0x6CCB5F) : Microphone ? Theme.Hex(0xF7A146) : Color.Empty;
            if (Clock.Ms >= NextBlink) Blink();
        }

        /// <summary>Lo llama quien no recibe los eventos de la isla: actualiza solo si la isla no lo hizo hace poco.</summary>
        public void ObserveIfStale(Snapshot snap)
        {
            if ((DateTime.Now - lastObserve).TotalSeconds >= 2.5) Observe(snap);
            else if (Clock.Ms >= NextBlink) Blink();
        }

        object lookOwner;

        /// <summary>Mirar al cursor desde una ventana: la que lo tiene cerca manda; al alejarse, el ojo vuelve al centro.</summary>
        public void LookFrom(object source, bool near, float x, float y)
        {
            if (near) { lookOwner = source; Look(x, y); }
            else if (lookOwner == source) { lookOwner = null; Look(0, 0); }
        }

        /// <summary>Estado según la PC: apertura, tamaño de pupila, color del iris y variantes.</summary>
        public void SetState(float lidTarget, float pupil, Color iris, bool isHappy, bool isDim, bool pupilFixed)
        {
            baseLid = lidTarget; basePupil = pupil; happy = isHappy; dim = isDim; fixedPupil = pupilFixed;
            if (iris.ToArgb() != irisTo.ToArgb())
            {
                irisFrom = IrisNow;
                irisTo = iris;
                colorT = 0; vColor = 0;
            }
            baseIris = iris;
        }

        /// <summary>Hacia dónde mira (unidades, ±5.5 en x y ±4 en y).</summary>
        public void Look(float x, float y)
        {
            lookX = Math.Max(-5.5f, Math.Min(5.5f, x));
            lookY = Math.Max(-4f, Math.Min(4f, y));
        }

        public bool Asleep { get { return baseLid <= 0; } }

        public void Blink()
        {
            double now = Clock.Ms;
            NextBlink = now + rnd.Next(4000, 8000);
            if (Asleep || happy || now - blinkStart < 150) return;
            blinkStart = now;
        }

        public void Surprise() { surpriseUntil = Clock.Ms + 400; }
        public void LookDown() { lookDownUntil = Clock.Ms + 1500; }
        public void Flash() { flashStart = Clock.Ms; }

        Color IrisNow { get { return Lerp(irisFrom, irisTo, colorT); } }

        // Destino de cada resorte en este instante (las reacciones pasajeras tienen prioridad).
        void Targets(double now, out float tLid, out float tX, out float tY, out float tR)
        {
            tLid = baseLid; tR = basePupil;
            tX = fixedPupil || Asleep ? 0 : lookX;
            tY = fixedPupil || Asleep ? 0 : lookY;
            if (now < lookDownUntil) { tX = 0; tY = 4; }
            if (now < surpriseUntil) { tLid = 1; tR = 6; }
        }

        /// <summary>Hay algo moviéndose (resortes o reacciones en curso).</summary>
        public bool Moving(double now)
        {
            if (!Animated) return false;
            float tLid, tX, tY, tR;
            Targets(now, out tLid, out tX, out tY, out tR);
            return lid != tLid || px != tX || py != tY || pr != tR || colorT != 1 ||
                   vLid != 0 || vPx != 0 || vPy != 0 || vPr != 0 || vColor != 0 ||
                   now - blinkStart < 150 || now - flashStart < 210 || now < surpriseUntil || now < lookDownUntil;
        }

        double lastStep = -1e9;

        /// <summary>
        /// Avanza los resortes hasta <paramref name="now"/>. Pueden llamarlo varias ventanas en el mismo cuadro:
        /// el tiempo se cuenta una sola vez.
        /// </summary>
        public void Step(double now)
        {
            float dt = (float)Math.Min(0.05, (now - lastStep) / 1000.0);
            if (dt < 0.0005f && dt >= 0) return;
            lastStep = now;
            if (dt < 0) dt = 0.016f;
            float tLid, tX, tY, tR;
            Targets(now, out tLid, out tX, out tY, out tR);
            Spring(ref lid, ref vLid, tLid, dt, 260, 22, 0.002f);
            Spring(ref px, ref vPx, tX, dt, 180, 18, 0.01f);
            Spring(ref py, ref vPy, tY, dt, 180, 18, 0.01f);
            Spring(ref pr, ref vPr, tR, dt, 220, 20, 0.01f);
            Spring(ref colorT, ref vColor, 1, dt, 180, 18, 0.002f);
        }

        static void Spring(ref float x, ref float v, float target, float dt, float k, float c, float eps)
        {
            int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004f));
            float h = dt / n;
            for (int i = 0; i < n; i++) { float a = -k * (x - target) - c * v; v += a * h; x += v * h; }
            if (Math.Abs(x - target) < eps && Math.Abs(v) < eps * 10) { x = target; v = 0; }
        }

        /// <summary>Lo que se dibuja en este instante (resortes más parpadeo y destello).</summary>
        Pose Current(double now)
        {
            var l = new Pose { Lid = lid, X = px, Y = py, R = pr, Iris = IrisNow, Happy = happy, Dim = dim, Dot = Dot };
            double b = now - blinkStart;
            if (b < 60) l.Lid *= 1 - Smooth((float)(b / 60));
            else if (b < 150) l.Lid *= Smooth((float)((b - 60) / 90));
            double f = now - flashStart;
            if (f < 210)
            {
                l.Flash = true;
                float k = f < 40 ? Smooth((float)(f / 40)) : f < 120 ? 1 : 1 - Smooth((float)((f - 120) / 90));
                l.Lid = l.Lid + (0.08f - l.Lid) * k;
                if (l.Lid < 0.08f && k > 0.5f) l.Lid = 0.08f;
            }
            return l;
        }

        static float Smooth(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        public void Draw(Graphics g, D2DPainter d, RectangleF r, bool lightBg)
        {
            Pose p = Animated ? Current(Clock.Ms) : Rest;
            if (!Animated) { p.Iris = irisTo; p.Dot = Dot; }
            Paint(g, d, r, p, lightBg, false);
        }

        // ================================================================== dibujo

        struct Pose
        {
            public float Lid, X, Y, R;
            public Color Iris, Dot;
            public bool Happy, Flash, Dim;
        }

        static Pose Rest { get { return new Pose { Lid = 1, R = RestPupil, Iris = Accent, Dot = Color.Empty }; } }

        /// <summary>
        /// Dibuja el ojo centrado en <paramref name="r"/> (el lado menor es el tamaño). Con <paramref name="d"/> usa
        /// Direct2D; si no, GDI+ sobre <paramref name="g"/>. <paramref name="simple"/>: sin diafragma ni brillo (16 px).
        /// </summary>
        static void Paint(Graphics g, D2DPainter d, RectangleF r, Pose s, bool lightBg, bool simple)
        {
            float size = Math.Min(r.Width, r.Height), k = size / 48f;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            float ringW = (size <= 18 ? 3.6f : 3f) * k;
            Color ring = s.Flash ? Color.White : lightBg ? RingLight : RingDark;
            Color iris = s.Flash ? Color.White : s.Iris;

            Ellipse(g, d, cx, cy, 18 * k, Back);
            if (s.Happy)
            {
                // Arco M13,29 Q24,15 35,29: el párpado inferior sonriendo.
                var pts = new PointF[13];
                for (int i = 0; i < pts.Length; i++)
                {
                    float t = i / 12f, u = 1 - t;
                    float x = u * u * 13 + 2 * u * t * 24 + t * t * 35, y = u * u * 29 + 2 * u * t * 15 + t * t * 29;
                    pts[i] = new PointF(cx + (x - 24) * k, cy + (y - 24) * k);
                }
                Stroke(g, d, pts, Accent, 4.5f * k);
            }
            else if (s.Lid < 0.08f)
                Stroke(g, d, new[] { new PointF(cx - 15 * k, cy), new PointF(cx + 15 * k, cy) }, iris, 3.5f * k);
            else
            {
                Ellipse(g, d, cx, cy, 17 * k, Body);
                // Párpados: un recorte horizontal (el iris nunca sale del cuerpo, así que no hace falta el círculo).
                float h = 17 * k * Math.Min(s.Lid, 1f);
                var clip = new RectangleF(cx - 17 * k, cy - h, 34 * k, 2 * h);
                GraphicsState saved = null;
                if (d != null) d.PushClip(clip); else { saved = g.Save(); g.IntersectClip(clip); }
                float qx = cx + s.X * k, qy = cy + s.Y * k;
                Ellipse(g, d, qx, qy, 11 * k, iris);
                if (!s.Dim && !simple)
                    for (int i = 0; i < 6; i++)
                    {
                        double a = i * Math.PI / 3, b = a + 0.5;
                        float r0 = s.R + 1.5f, r1 = 9.5f;
                        Segment(g, d, qx + (float)Math.Cos(a) * r0 * k, qy + (float)Math.Sin(a) * r0 * k,
                                      qx + (float)Math.Cos(b) * r1 * k, qy + (float)Math.Sin(b) * r1 * k, Blade, 1f * k);
                    }
                Ellipse(g, d, qx, qy, s.R * k, Color.Black);
                if (!s.Dim && !simple) Ellipse(g, d, qx - 3 * k, qy - 3.5f * k, 1.8f * k, Color.White);
                if (d != null) d.PopClip(); else g.Restore(saved);
            }
            // Aro, siempre encima.
            var rr = new RectangleF(cx - 18 * k, cy - 18 * k, 36 * k, 36 * k);
            if (d != null) d.StrokeEllipse(rr, ring, ringW);
            else using (var p = new Pen(ring, ringW)) g.DrawEllipse(p, rr);

            if (!s.Dot.IsEmpty)
            {
                Ellipse(g, d, cx + 15 * k, cy - 15 * k, 5.5f * k, Color.Black);
                Ellipse(g, d, cx + 15 * k, cy - 15 * k, 4f * k, s.Dot);
            }
        }

        static void Ellipse(Graphics g, D2DPainter d, float x, float y, float rad, Color c)
        {
            var r = new RectangleF(x - rad, y - rad, 2 * rad, 2 * rad);
            if (d != null) d.FillEllipse(r, c);
            else using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
        }

        static void Segment(Graphics g, D2DPainter d, float x1, float y1, float x2, float y2, Color c, float w)
        {
            if (d != null) d.Line(x1, y1, x2, y2, c, w, 0);
            else using (var p = new Pen(c, w)) g.DrawLine(p, x1, y1, x2, y2);
        }

        static void Stroke(Graphics g, D2DPainter d, PointF[] pts, Color c, float w)
        {
            if (d != null) { d.Polyline(pts, c, w); return; }
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, pts);
        }

        static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ================================================================== íconos estáticos

        /// <summary>Ícono de bandeja: el ojo en reposo, versión simple.</summary>
        public static Bitmap DrawTray(int size, bool lightTaskbar)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                // El círculo del ojo mide 39/48 del lado: se agranda para que ocupe el ícono entero.
                float grow = size * 48f / 39f;
                Paint(g, null, new RectangleF((size - grow) / 2, (size - grow) / 2, grow, grow), Rest, lightTaskbar, size <= 24);
            }
            return bmp;
        }

        /// <summary>Ícono de la app (igual que icon\app-icon.ico): cuadrado redondeado oscuro con el ojo en reposo; hasta 32 px, sin diafragma ni brillo.</summary>
        public static Bitmap DrawApp(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                float w = size;
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, w - 1, w - 1), w * 11f / 48f))
                {
                    using (var b = new SolidBrush(Back)) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(115, 200, 180, 255), 1f)) g.DrawPath(pen, p);
                }
                float e = w * 0.708f, off = w * 7f / 48f;
                Paint(g, null, new RectangleF(off, off, e, e), Rest, false, size <= 32);
            }
            return bmp;
        }

        /// <summary>Genera el .ico multi-tamaño de la app (PNG embebidos) con el mismo código de dibujo.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = DrawApp(sizes[i]))
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs[i] = ms.ToArray(); }
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    bw.Write(b); bw.Write(b); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32); bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) bw.Write(p);
            }
        }

        /// <summary>Hoja de estados para revisar el diseño (vista previa).</summary>
        public static Bitmap StateSheet(int size, bool lightBg, Color bg)
        {
            var states = new[]
            {
                new Pose { Lid = 1, R = RestPupil, Iris = Accent },                         // reposo
                new Pose { Lid = 0.35f, R = RestPupil, Iris = Accent },                     // parpadeo (a mitad)
                new Pose { Lid = 1, X = 5.5f, Y = -2, R = RestPupil, Iris = Accent },       // mirando
                new Pose { Lid = 0.45f, R = RestPupil, Iris = Accent },                     // concentrado
                new Pose { Lid = 0.8f, R = RestPupil, Iris = HotIris },                     // caliente
                new Pose { Lid = 0, R = RestPupil, Iris = Accent },                         // dormido
                new Pose { Lid = 1, R = 6, Iris = Accent },                                 // sorprendido
                new Pose { Lid = 0.9f, R = 2.2f, Iris = Accent },                           // en guardia
                new Pose { Lid = 1, R = RestPupil, Iris = Accent, Happy = true },           // contento
                new Pose { Lid = 1, Y = 4, R = RestPupil, Iris = Accent },                  // mira abajo
                new Pose { Lid = 0.08f, R = RestPupil, Iris = Accent, Flash = true },       // destello
                new Pose { Lid = 1, R = RestPupil, Iris = Accent, Dot = Theme.Hex(0x6CCB5F) }, // cámara
                new Pose { Lid = 1, R = 2.5f, Iris = OfflineIris, Dim = true },             // sin internet
            };
            int pad = size / 3, cell = size + pad;
            var bmp = new Bitmap(states.Length * cell + pad, size + 2 * pad, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < states.Length; i++)
                {
                    var s = states[i];
                    if (s.Dot.ToArgb() == 0) s.Dot = Color.Empty;
                    Paint(g, null, new RectangleF(pad + i * cell, pad, size, size), s, lightBg, false);
                }
            }
            return bmp;
        }
    }
}
