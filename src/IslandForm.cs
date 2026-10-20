using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>
    /// Isla dinámica: una barra compacta arriba al centro (o sobre la barra de tareas) que se expande con un
    /// rebote para mostrar todas las métricas, y que muestra eventos contextuales durante unos segundos.
    /// Ver ISLA_DINAMICA.md.
    /// </summary>
    internal sealed class IslandForm : Surface
    {
        readonly IHost host;
        readonly Timer data = new Timer(), poll = new Timer(), screenCheck = new Timer();
        readonly FrameLoop anim;
        readonly Vibrancy vibrancy = new Vibrancy();

        bool expanded, pinned, fullScreen, islandOn, previewMode;
        DateTime lastData = DateTime.Now;

        // ---- Animación de tamaño (resorte) y fundido del contenido
        SizeF cur, from, to;                 // tamaño lógico (DIP)
        double resizeStart = -1e9;   // en ms del reloj de alta precisión (Clock)
        const int ResizeMs = 500;
        double fadeStart = -1e9;
        bool fadeToExpanded;

        // ---- Valores suavizados de los anillos
        readonly Dictionary<string, float> shown = new Dictionary<string, float>();
        readonly Dictionary<string, float> target = new Dictionary<string, float>();
        double lastValueStep;

        // ---- Mouse
        DateTime insideSince = DateTime.MinValue, outsideSince = DateTime.MinValue;
        bool wasInside;

        // ---- Fuentes propias de la isla
        Font fValue13, fSmall11, fDisplay16, fDisplay18, fCaption12;

        public IslandForm(IHost host)
        {
            this.host = host;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Isla de Panoptic";
            AccessibleName = "Isla de Panoptic";

            data.Interval = 1000;
            data.Tick += (o, e) => OnData();
            anim = new FrameLoop(this, OnAnimate);
            layered = !DisableLayered && !th.Glass;
            poll.Interval = 100;
            poll.Tick += (o, e) => OnPoll();
            screenCheck.Interval = 1000;
            screenCheck.Tick += (o, e) => CheckFullScreen();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Activity.Added += OnActivity;
            BuildFonts();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: sin barra de tareas, sin Alt+Tab, sin robar el foco
                if (!layered) cp.ClassStyle |= 0x20000; // CS_DROPSHADOW (en modo lienzo la sombra la dibuja la isla)
                if (layered) cp.ExStyle |= 0x80000; // WS_EX_LAYERED: lienzo con transparencia por píxel
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override int Backdrop { get { return Native.DWMSBT_ACRYLIC; } }

        protected override void ApplyDwm()
        {
            base.ApplyDwm();
            if (!IsHandleCreated) return;
            if (layered)
            {
                // Modo lienzo: la ventana es un rectángulo transparente; sin borde, esquinas ni sombra del sistema
                // (si no, Windows dibuja un marco alrededor de todo el lienzo).
                int none = 1; // DWMWCP_DONOTROUND
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref none, 4);
                int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref noBorder, 4);
                return;
            }
            int v = Native.DWMWCP_ROUND; // esquinas de 8 px de Windows 11
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
            UpdateBorder();
        }

        void UpdateBorder()
        {
            if (!IsHandleCreated || layered) return;
            Color c = active != null && active.Critical ? th.Critical
                : th.Dark ? Theme.Mix(th.Bg, Color.White, 0.09f) : Theme.Mix(th.Bg, Color.Black, 0.08f);
            int v = Native.ColorRef(c);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, 4);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (layered) BeginInvoke(new Action(ApplyBounds)); // ubicar el lienzo y dibujar apenas existe la ventana
        }

        protected override void OnThemeReloaded()
        {
            vibrancy.Invalidate();
            // Con el acrílico de Windows se usa una ventana normal; sin él, el lienzo en capas.
            bool want = !DisableLayered && !th.Glass;
            if (want != layered && IsHandleCreated)
            {
                layered = want;
                canvas = Rectangle.Empty;
                RecreateHandle();
                ApplyBounds();
            }
        }

        protected override void OnScaleChanged()
        {
            BuildFonts();
            ApplyBounds();
        }

        void BuildFonts()
        {
            foreach (var f in new[] { fValue13, fSmall11, fDisplay16, fDisplay18, fCaption12 }) if (f != null) f.Dispose();
            string text = Fonts.TextFamily;
            string strong = PickFamily("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
            string display = PickFamily("Segoe UI Variable Display Semib", "Segoe UI Semibold");
            fValue13 = new Font(strong, 13f * s, GraphicsUnit.Pixel);
            fSmall11 = new Font(text, 11f * s, GraphicsUnit.Pixel);
            fCaption12 = new Font(text, 12f * s, GraphicsUnit.Pixel);
            fDisplay16 = new Font(display, 16f * s, GraphicsUnit.Pixel);
            fDisplay18 = new Font(display, 18f * s, GraphicsUnit.Pixel);
        }

        static string PickFamily(string a, string b)
        {
            try { using (new FontFamily(a)) return a; } catch { return b; }
        }

        string Pos { get { return Settings.IslandPosition; } }
        bool Top { get { return Pos == "Top"; } }
        /// <summary>A la izquierda o a la derecha de la pantalla: la compacta es vertical.</summary>
        bool Side { get { return Pos == "Left" || Pos == "Right"; } }

        /// <summary>Hacia dónde sale la isla al ocultarse (el borde de la pantalla donde está).</summary>
        Point Outward
        {
            get
            {
                switch (Pos)
                {
                    case "Taskbar": return new Point(0, 1);
                    case "Left": return new Point(-1, 0);
                    case "Right": return new Point(1, 0);
                    default: return new Point(0, -1);
                }
            }
        }

        /// <summary>Posición de la forma (w × h píxeles) en pantalla, sin el desplazamiento de ocultarse.</summary>
        Rectangle HomeShape(int w, int h)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = S(10);
            switch (Pos)
            {
                case "Taskbar": return new Rectangle(wa.X + (wa.Width - w) / 2, wa.Bottom - gap - h, w, h);   // crece hacia arriba
                case "Left": return new Rectangle(wa.X + gap, wa.Y + (wa.Height - h) / 2, w, h);
                case "Right": return new Rectangle(wa.Right - gap - w, wa.Y + (wa.Height - h) / 2, w, h);
                default: return new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + gap, w, h);
            }
        }

        /// <summary>Desplazamiento (en píxeles) de la isla mientras se esconde o aparece.</summary>
        Point HideOffset
        {
            get
            {
                if (hideK <= 0) return Point.Empty;
                var o = Outward;
                int dist = (o.X != 0 ? Area.Width : Area.Height) + S(10) + (layered ? ShadowMargin : 0) + S(2);
                int d = (int)Math.Round(dist * hideK);
                return new Point(o.X * d, o.Y * d);
            }
        }

        // ================================================================== mostrar / ocultar

        public void ShowIsland()
        {
            ReloadTheme();
            var sc = Screen.PrimaryScreen;
            SetScale(Native.ScaleForPoint(new Point(sc.WorkingArea.X + sc.WorkingArea.Width / 2, sc.WorkingArea.Y + 5)));
            expanded = false;
            lastPos = Pos;
            hideK = slideFrom = slideTo = vHide = 0;
            resizing = false; vW = vH = 0;
            autoHidden = false;
            cur = from = to = TargetSize();
            ApplyBounds();
            islandOn = true;
            fullScreen = !App.ProfileMode && Native.FullScreenAppRunning();
            UpdateVisibility();
            data.Start();
            screenCheck.Start();
            UpdatePolling();
        }

        public void HideIsland()
        {
            islandOn = false;
            data.Stop(); anim.Stop(); poll.Stop(); screenCheck.Stop();
            Hide();
        }

        /// <summary>Cambio de borde: se ubica de inmediato en el lugar nuevo (sin animar desde el anterior).</summary>
        string lastPos;

        void ChangePosition()
        {
            lastPos = Pos;
            expanded = false;
            cur = from = to = TargetSize();
            resizing = false; vW = vH = 0;
            canvas = Rectangle.Empty;
            lastFrameKey = null;
            ApplyBounds();
            Invalidate();
            UpdatePolling();
        }

        /// <summary>Recalcula el tamaño y la posición después de un cambio en Configuración.</summary>
        public void Relayout()
        {
            if (Pos != lastPos) { lastPos = Pos; ReloadTheme(); ChangePosition(); return; }
            ReloadTheme();
            StartResize();
            UpdatePolling();
        }

        void OnDisplayChanged(object s, EventArgs e) { if (Visible) BeginInvoke(new Action(ApplyBounds)); }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A, WM_ERASEBKGND = 0x0014;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }   // sin borrar: evita el parpadeo al agrandar
            if (m.Msg == WM_DPICHANGED)
            {
                SetScale((m.WParam.ToInt64() & 0xFFFF) / 96f);
                return;
            }
            if (m.Msg == WM_SETTINGCHANGE && m.WParam.ToInt64() == 0x2F /* SPI_SETWORKAREA */) ApplyBounds();
            base.WndProc(ref m);
        }

        void CheckFullScreen()
        {
            bool fs = !App.ProfileMode && Native.FullScreenAppRunning();
            if (fs == fullScreen) return;
            fullScreen = fs;
            UpdatePolling();
            UpdateVisibility();
        }

        // Con una app en pantalla completa la isla se oculta y vuelve a aparecer solo cuando:
        // el cursor llega al borde de la pantalla donde está la isla, llega una notificación de la isla,
        // está expandida o el cursor está encima.
        DateTime revealUntil = DateTime.MinValue;

        // ---- Ocultar automáticamente: la isla se desliza fuera del borde y vuelve al acercar el mouse.
        float hideK;                         // 0 visible … 1 escondida
        float slideFrom, slideTo;
        double slideStart = -1e9;
        const int SlideMs = 220;
        bool autoHidden;
        DateTime nearUntil = DateTime.MinValue;

        void SetAutoHidden(bool hide)
        {
            if (hide == autoHidden) return;
            autoHidden = hide;
            slideFrom = hideK;
            slideTo = hide ? 1 : 0;
            if (!hide && !Visible && islandOn && !previewMode) { Show(); }
            if (!Native.AnimationsEnabled() || previewMode) { hideK = slideTo; AfterSlide(); return; }
            slideStart = Clock.Ms;
            StartAnim();
        }

        void AfterSlide()
        {
            ApplyBounds();
            if (hideK >= 1 && autoHidden && Visible) Hide();
            else if (layered) RenderLayered();
        }

        /// <summary>El cursor está contra el borde de la pantalla donde vive la isla, a la altura de la isla.</summary>
        bool NearEdge(Point p)
        {
            var sb = Screen.PrimaryScreen.Bounds;
            var home = HomeShape(S(cur.Width), S(cur.Height));
            int t = S(4), spanX = home.Width / 2 + S(120), spanY = home.Height / 2 + S(120);
            int cx = home.X + home.Width / 2, cy = home.Y + home.Height / 2;
            switch (Pos)
            {
                case "Taskbar": return p.Y >= sb.Bottom - t && Math.Abs(p.X - cx) <= spanX;
                case "Left": return p.X <= sb.Left + t && Math.Abs(p.Y - cy) <= spanY;
                case "Right": return p.X >= sb.Right - 1 - t && Math.Abs(p.Y - cy) <= spanY;
                default: return p.Y <= sb.Top + t && Math.Abs(p.X - cx) <= spanX;
            }
        }

        void UpdateVisibility()
        {
            if (!islandOn || previewMode) return;
            bool show = (!fullScreen || expanded || active != null || DateTime.Now < revealUntil) && !(autoHidden && hideK >= 1);
            if (show && !Visible) { ApplyBounds(); Show(); if (layered) RenderLayered(); }
            else if (!show && Visible) Hide();
        }

        // ================================================================== tamaño y posición

        /// <summary>Tamaño lógico que corresponde al estado actual.</summary>
        SizeF TargetSize()
        {
            if (expanded)
            {
                if (!Top) return new SizeF(402, 434);
                int cols = Cells(host.Latest).Count;
                return new SizeF(cols >= 6 ? 560 : cols * 90 + 32, 244);
            }
            if (active != null) return new SizeF(ContextWidth(active), active.Height);
            if (download != null) return new SizeF(ContextWidth(download), download.Height);
            if (Side) return SideCompactSize();
            return Top ? new SizeF(CompactWidth(), 38) : new SizeF(260, 36);
        }

        /// <summary>Compacta vertical (costados): ícono, un anillo por métrica y la red.</summary>
        SizeF SideCompactSize()
        {
            if (Settings.IslandCompactSet == 2) return new SizeF(68, 12 + 22 + 12 + 18 + 8 + 4 * 16 + 12);
            var snap = host.Latest;
            int groups = snap == null ? 3 : Math.Max(1, CompactMetrics(snap).Count);
            return new SizeF(68, 12 + 22 + 12 + groups * (24 + 4 + 16 + 10) + 2 * 16 + 12);
        }

        /// <summary>
        /// Ancho de la compacta según su contenido, con lugar fijo para la red ("↓ 888,8 Mbps")
        /// para que la isla no cambie de tamaño cada segundo.
        /// </summary>
        float CompactWidth()
        {
            float net = MeasureText("↓ 888,8 Mbps", fCaption12) / s;
            if (Settings.IslandCompactSet == 2)
                return Math.Max(300, 12 + 22 + 14 + MeasureText("Ethernet", fValue13) / s + 10 + 64 + 12 + 2 * net + 16);
            float val = MeasureText("88%", fValue13) / s + 2;
            var snap = host.Latest;
            int groups = snap == null ? 3 : Math.Max(1, CompactMetrics(snap).Count);
            return Math.Max(300, 12 + 22 + 14 + groups * (16 + 6 + val + 14) + 10 + net + 16);
        }

        void StartResize()
        {
            var t = TargetSize();
            if (Math.Abs(t.Width - to.Width) < 0.5f && Math.Abs(t.Height - to.Height) < 0.5f && !Animating) return;
            from = cur;
            to = t;
            if (!Native.AnimationsEnabled() || previewMode) { cur = to; ApplyBounds(); Invalidate(); return; }
            resizing = true;
            StartAnim();
        }

        // ---- Resortes de la isla: tamaño y deslizamiento. Interrumpibles: si cambia el destino a mitad de camino,
        // siguen desde donde están y con la velocidad que traían (como la isla dinámica del iPhone).
        bool resizing;
        float vW, vH, vHide;
        double lastAnim;
        const float ResizeResponse = 0.5f, ResizeDamping = 0.66f;     // rebote apenas visible
        const float SlideResponse = 0.42f, SlideDamping = 0.86f;

        static bool StepSpring(ref float x, ref float v, float target, float dt, float response, float damping, float eps)
        {
            float w = (float)(2 * Math.PI / response), k = w * w, c = 2 * w * damping;
            int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004f));
            float h = dt / n;
            for (int i = 0; i < n; i++) { float a = -k * (x - target) - c * v; v += a * h; x += v * h; }
            if (Math.Abs(x - target) < eps && Math.Abs(v) < eps * 10) { x = target; v = 0; return false; }
            return true;
        }

        void StartAnim()
        {
            if (!anim.Running) lastAnim = Clock.Ms;
            if (!anim.Running) lastValueStep = Clock.Ms; // sin esto, el primer paso "salta" todo el tiempo que estuvo quieta
            anim.Start();
        }

        bool Animating
        {
            get
            {
                double now = Clock.Ms;
                return resizing || now - fadeStart < 400 || hideK != slideTo || vHide != 0 || ValuesMoving();
            }
        }

        // ---- Lienzo máximo (ventana en capas con transparencia por píxel)
        // Sin el acrílico de Windows, la ventana se crea una sola vez con el tamaño más grande que puede tener
        // y lo que se anima es la forma dibujada: Windows no tiene que redimensionar nada en cada cuadro.
        bool layered;
        public static bool DisableLayered;      // vista previa
        Rectangle canvas;                       // posición y tamaño de la ventana en pantalla (modo en capas)
        LayeredCanvas surface;
        Bitmap content;
        bool renderQueued;
        string lastFrameKey;     // tamaño y borde del último cuadro completo (para redibujar solo el interior)
        int ShadowMargin { get { return S(14); } }    // lugar para la sombra alrededor de la forma

        /// <summary>Tamaño del contenido (la forma de la isla) en píxeles.</summary>
        Size Area { get { return layered ? new Size(Math.Max(S(40), S(cur.Width)), Math.Max(S(24), S(cur.Height))) : ClientSize; } }
        Rectangle AreaRect { get { return new Rectangle(Point.Empty, Area); } }

        /// <summary>Rectángulo de la forma dentro de la ventana.</summary>
        Rectangle ShapeClient
        {
            get
            {
                if (!layered) return ClientRectangle;
                var a = Area;
                int x = (canvas.Width - a.Width) / 2, y = (canvas.Height - a.Height) / 2;
                switch (Pos)
                {
                    case "Top": y = ShadowMargin; break;
                    case "Taskbar": y = canvas.Height - ShadowMargin - a.Height; break;
                    case "Left": x = ShadowMargin; break;
                    case "Right": x = canvas.Width - ShadowMargin - a.Width; break;
                }
                return new Rectangle(x, y, a.Width, a.Height);
            }
        }

        /// <summary>Rectángulo de la forma en coordenadas de pantalla.</summary>
        Rectangle ShapeScreen
        {
            get
            {
                if (!layered) return Bounds;
                var r = ShapeClient;
                r.Offset(canvas.Location);
                return r;
            }
        }

        void ApplyBounds()
        {
            if (!IsHandleCreated) return;
            var off = HideOffset;
            if (layered)
            {
                // El lienzo cubre el tamaño máximo, con margen para el rebote (la curva supera el 100 %).
                var big = TargetSizeExpanded();
                float bw = Math.Max(big.Width, Math.Max(cur.Width, to.Width)) * 1.12f, bh = Math.Max(big.Height, Math.Max(cur.Height, to.Height)) * 1.12f;
                int w = S(bw) + 2 * ShadowMargin, h = S(bh) + 2 * ShadowMargin;
                // El lienzo se ubica como si fuera la forma (sin los márgenes de la sombra) y después se corre.
                var home = HomeShape(w - 2 * ShadowMargin, h - 2 * ShadowMargin);
                int x = home.X - ShadowMargin + off.X, y = home.Y - ShadowMargin + off.Y;
                var r = new Rectangle(x, y, w, h);
                if (r != canvas)
                {
                    canvas = r;
                    Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
                RenderLayered();
                return;
            }
            {
                int w = Math.Max(S(40), S(cur.Width)), h = Math.Max(S(24), S(cur.Height));
                var home = HomeShape(w, h);
                Native.SetWindowPos(Handle, IntPtr.Zero, home.X + off.X, home.Y + off.Y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
        }

        SizeF TargetSizeExpanded()
        {
            bool e = expanded;
            expanded = true;
            try { return TargetSize(); } finally { expanded = e; }
        }

        /// <summary>En modo en capas, cada Invalidate() termina en un render del lienzo.</summary>
        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            if (!layered || renderQueued || !IsHandleCreated) return;
            renderQueued = true;
            BeginInvoke(new Action(() => { renderQueued = false; RenderLayered(); }));
        }

        void RenderLayered()
        {
            var sw = ProfilePaint ? System.Diagnostics.Stopwatch.StartNew() : null;
            RenderLayeredCore();
            if (sw != null)
            {
                double[] st;
                if (!PaintStats.TryGetValue("IslaLienzo", out st)) PaintStats["IslaLienzo"] = st = new double[2];
                st[0]++; st[1] += sw.Elapsed.TotalMilliseconds;
            }
        }

        void RenderLayeredCore()
        {
            if (!layered || !IsHandleCreated) return;
            if (canvas.Width <= 0) { ApplyBounds(); if (canvas.Width <= 0) return; return; } // todavía sin posición: se calcula y ApplyBounds dibuja
            if (surface == null || surface.Width != canvas.Width || surface.Height != canvas.Height)
            {
                if (surface != null) surface.Dispose();
                surface = new LayeredCanvas(canvas.Width, canvas.Height);
                lastFrameKey = null;
            }
            var shape = ShapeClient;
            contentOffset = shape.Location;
            if (content == null || content.Width != shape.Width || content.Height != shape.Height)
            {
                if (content != null) content.Dispose();
                content = new Bitmap(Math.Max(1, shape.Width), Math.Max(1, shape.Height), PixelFormat.Format32bppPArgb);
            }
            using (var cg = Graphics.FromImage(content))
            {
                cg.Clear(Color.Transparent);
                PaintAll(cg);
            }

            var g = surface.Graphics;
            float rad = S(8);
            Color border = active != null && active.Critical && !expanded ? th.Critical
                : th.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
            string frameKey = shape + "|" + border.ToArgb() + "|" + th.Dark;
            if (frameKey == lastFrameKey)
            {
                // Mismo tamaño que el cuadro anterior: la sombra y el borde ya están; solo se renueva el interior.
                g.SmoothingMode = SmoothingMode.None;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var p = Theme.Round(new RectangleF(shape.X + 1, shape.Y + 1, shape.Width - 2, shape.Height - 2), Math.Max(1, rad - 1)))
                using (var tb = new TextureBrush(content, WrapMode.Clamp))
                {
                    tb.TranslateTransform(shape.X, shape.Y);
                    g.FillPath(tb, p);
                }
                g.CompositingMode = CompositingMode.SourceOver;
                surface.Present(Handle, canvas.Location);
                return;
            }
            lastFrameKey = frameKey;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Sombra suave (las ventanas en capas no tienen la sombra de DWM).
            for (int i = 4; i >= 1; i--)
            {
                var sr = new RectangleF(shape.X - i * s * 2.5f, shape.Y - i * s * 2.5f + 2 * s, shape.Width + i * s * 5f, shape.Height + i * s * 5f);
                using (var p = Theme.Round(sr, rad + i * 2.5f * s))
                using (var b = new SolidBrush(Color.FromArgb(th.Dark ? 14 : 9, 0, 0, 0))) g.FillPath(b, p);
            }
            // Forma con esquinas de 8 px y bordes suavizados, rellena con el contenido.
            using (var p = Theme.Round(new RectangleF(shape.X, shape.Y, shape.Width, shape.Height), rad))
            using (var tb = new TextureBrush(content, WrapMode.Clamp))
            {
                tb.TranslateTransform(shape.X, shape.Y);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillPath(tb, p);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            using (var p = Theme.Round(new RectangleF(shape.X + .5f, shape.Y + .5f, shape.Width - 1, shape.Height - 1), rad))
            using (var pen = new Pen(border))
                g.DrawPath(pen, p);
            surface.Present(Handle, canvas.Location);
        }

        static double CubicBezier(double t, double x1, double y1, double x2, double y2)
        {
            double lo = 0, hi = 1, u = t;
            for (int i = 0; i < 20; i++)
            {
                u = (lo + hi) / 2;
                double x = 3 * (1 - u) * (1 - u) * u * x1 + 3 * (1 - u) * u * u * x2 + u * u * u;
                if (x < t) lo = u; else hi = u;
            }
            return 3 * (1 - u) * (1 - u) * u * y1 + 3 * (1 - u) * u * u * y2 + u * u * u; // puede superar 1 (rebote)
        }

        /// <summary>Un cuadro de animación, sincronizado con el refresco de la pantalla.</summary>
        void OnAnimate()
        {
            double now = Clock.Ms;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, (now - lastAnim) / 1000.0));
            lastAnim = now;
            bool resized = false;
            if (resizing)
            {
                float w = cur.Width, h = cur.Height;
                bool a = StepSpring(ref w, ref vW, to.Width, dt, ResizeResponse, ResizeDamping, 0.3f);
                bool b = StepSpring(ref h, ref vH, to.Height, dt, ResizeResponse, ResizeDamping, 0.3f);
                cur = new SizeF(w, h);
                resizing = a || b;
                resized = true;
            }
            // Deslizamiento al ocultarse o aparecer.
            if (hideK != slideTo || vHide != 0)
            {
                bool moving = StepSpring(ref hideK, ref vHide, slideTo, dt, SlideResponse, SlideDamping, 0.002f);
                if (!moving) AfterSlide(); else ApplyBounds();
            }
            StepValues();
            if (layered)
            {
                if (resized && !CanvasFits()) ApplyBounds(); else RenderLayered();
            }
            else
            {
                if (resized) ApplyBounds();
                Invalidate();
                Update(); // pintar en este mismo cuadro, sin esperar a que Windows lo agende
            }
            if (!Animating) anim.Stop();
        }

        bool CanvasFits()
        {
            return S(cur.Width) + 2 * ShadowMargin <= canvas.Width && S(cur.Height) + 2 * ShadowMargin <= canvas.Height;
        }

        /// <summary>Opacidad del contenido durante el cambio de estado.</summary>
        float ContentAlpha()
        {
            if (!Native.AnimationsEnabled() || previewMode) return 1;
            double el = Clock.Ms - fadeStart;
            if (fadeToExpanded) return (float)Math.Max(0, Math.Min(1, (el - 120) / 200));
            return (float)Math.Max(0, Math.Min(1, el / 200));
        }

        void SetExpanded(bool value)
        {
            if (expanded == value) return;
            expanded = value;
            if (!value) pinned = false;
            fadeStart = Clock.Ms;
            fadeToExpanded = value;
            if (value && active != null) DismissActive();
            StartResize();
            UpdatePolling();
            UpdateVisibility();
            StartAnim();
        }

        // ================================================================== valores suavizados

        float Value(string key, float v)
        {
            target[key] = v;
            float c;
            if (!shown.TryGetValue(key, out c) || !Native.AnimationsEnabled() || previewMode) { shown[key] = v; return v; }
            // Cambios de menos de 1,5 puntos no se animan (en el anillo no se ven) y no despiertan el ciclo de cuadros.
            if (Math.Abs(c - v) < 1.5f && !anim.Running) { shown[key] = v; return v; }
            if (Math.Abs(c - v) > 0.05f && !anim.Running) StartAnim(); // el valor cambió: animarlo ya, no en el próximo segundo
            return c;
        }

        bool ValuesMoving()
        {
            foreach (var k in target.Keys) { float c; if (shown.TryGetValue(k, out c) && Math.Abs(c - target[k]) > 0.05f) return true; }
            return false;
        }

        /// <summary>Acerca los anillos a su valor según el tiempo real transcurrido (ease out, unos 350 ms).</summary>
        void StepValues()
        {
            double now = Clock.Ms;
            float dt = (float)Math.Min(50, now - lastValueStep); // nunca más de 50 ms por paso: sin saltos
            lastValueStep = now;
            float k = 1f - (float)Math.Pow(0.004, dt / 350f); // unos 350 ms con ease out
            foreach (var key in target.Keys.ToList())
            {
                float c;
                if (!shown.TryGetValue(key, out c)) continue;
                float t = target[key];
                c += (t - c) * k;
                if (Math.Abs(t - c) < 0.05f) c = t;
                shown[key] = c;
            }
        }


        // ================================================================== datos y eventos contextuales

        sealed class Ctx
        {
            public string Kind, Key, Title, Subtitle, Value, Glyph, Button;
            public Color? Accent;
            public Bitmap Icon;
            public bool Critical, Primary;
            public int Height = 38, Seconds = 4;
            public Action ButtonAction;
            public DateTime Until;
            public float Level = -1;   // batería
        }

        readonly List<Ctx> queue = new List<Ctx>();
        Ctx active, download;
        readonly Dictionary<string, DateTime> lastShown = new Dictionary<string, DateTime>();
        int downloadSeconds;
        bool lastOnAc, lastOnAcKnown, tempAlerted;
        readonly Dictionary<int, int> hogSeconds = new Dictionary<int, int>();

        void Enqueue(Ctx c)
        {
            if (!Settings.IslandAlerts) return;
            DateTime last;
            if (lastShown.TryGetValue(c.Key, out last) && (DateTime.Now - last).TotalMinutes < 5) return; // no repetir en 5 min
            if (queue.Any(q => q.Key == c.Key) || (active != null && active.Key == c.Key)) return;
            lastShown[c.Key] = DateTime.Now;
            if (c.Critical) queue.Insert(0, c); else queue.Add(c);
            NextContext();
        }

        void NextContext()
        {
            if (active != null || expanded || queue.Count == 0) return;
            active = queue[0];
            queue.RemoveAt(0);
            active.Until = DateTime.Now.AddSeconds(active.Seconds);
            UpdateBorder();
            StartResize();
            Invalidate();
            UpdateVisibility(); // en pantalla completa, la notificación vuelve a mostrar la isla
        }

        void DismissActive()
        {
            if (active == null) return;
            active = null;
            UpdateBorder();
            StartResize();
            BeginInvoke(new Action(NextContext));
            BeginInvoke(new Action(UpdateVisibility));
        }

        void OnData()
        {
            var snap = host.Latest;
            lastData = DateTime.Now;
            if (snap != null && Settings.IslandAlerts)
            {
                // Descarga activa: bajada de más de 5 MB/s durante 5 s; se muestra mientras dure.
                var net = snap.Nets.OrderByDescending(n => n.RecvBps).FirstOrDefault();
                if (net != null && net.RecvBps > 5 * 1048576) downloadSeconds++; else downloadSeconds = 0;
                bool wasDownload = download != null;
                if (downloadSeconds >= 5)
                    download = new Ctx { Kind = "download", Key = "download", Title = net.Kind == "Wi-Fi" ? "Wi-Fi" : net.Name, Value = "↓ " + Format.Rate(net.RecvBps), Height = 38 };
                else download = null;
                if (wasDownload != (download != null)) StartResize();

                // Temperatura por encima del umbral.
                int limit = Settings.TempThreshold;
                if (!double.IsNaN(snap.Temperature))
                {
                    if (!tempAlerted && snap.Temperature >= limit)
                    {
                        tempAlerted = true;
                        Enqueue(new Ctx
                        {
                            Kind = "temp", Key = "temp", Critical = true, Height = 44, Seconds = 10,
                            Value = snap.Temperature.ToString("0") + "°", Title = "Temperatura alta", Subtitle = "Supera el umbral de " + limit + "°",
                            Button = "Ver", ButtonAction = () => host.OpenMain("temp")
                        });
                    }
                    else if (snap.Temperature < limit - 8) tempAlerted = false;
                }

                // Proceso acaparando CPU: más del 40 % durante 2 minutos.
                var busy = new HashSet<int>();
                foreach (var p in snap.Procs.Where(p => p.Cpu > 40 && p.Name != "Idle" && p.Pid > 4))
                {
                    int sec;
                    hogSeconds.TryGetValue(p.Pid, out sec);
                    hogSeconds[p.Pid] = ++sec;
                    busy.Add(p.Pid);
                    if (sec == 120)
                    {
                        var proc = p;
                        Enqueue(new Ctx
                        {
                            Kind = "hog", Key = "hog" + p.Pid, Height = 44, Seconds = 10, Icon = p.Icon, Glyph = Glyphs.App,
                            Title = p.Name + ".exe", Subtitle = p.Cpu.ToString("0") + " % CPU durante 2 min",
                            Button = "Finalizar tarea", Primary = true, ButtonAction = () => host.EndTask(proc, null)
                        });
                    }
                }
                foreach (var pid in hogSeconds.Keys.Where(k => !busy.Contains(k)).ToList()) hogSeconds.Remove(pid);

                // Cargador conectado.
                if (snap.HasBattery)
                {
                    if (lastOnAcKnown && !lastOnAc && snap.OnAc)
                        Enqueue(new Ctx
                        {
                            Kind = "charger", Key = "charger", Height = 38, Seconds = 4, Level = snap.BatteryPercent,
                            Value = snap.BatteryPercent.ToString("0") + " %", Title = "Cargando", Accent = ResColors.Battery(th)
                        });
                    lastOnAc = snap.OnAc; lastOnAcKnown = true;
                }
            }

            if (active != null && DateTime.Now > active.Until) DismissActive();
            if (!expanded && active == null && queue.Count > 0) NextContext();
            if (!expanded && active == null && download == null) StartResize(); // por si cambió la cantidad de anillos (por ejemplo, la batería)
            if (!Visible) return;
            if (!anim.Running && ValuesMoving()) StartAnim();
            Invalidate();
        }

        /// <summary>Dispositivos USB: toma los eventos del registro de actividad.</summary>
        void OnActivity(ActivityEntry e)
        {
            if (e.Category != Category.Dispositivo || (DateTime.Now - e.Time).TotalSeconds > 10) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (e.Message.StartsWith("Se conectó ") && !e.Message.StartsWith("Se conectó la pantalla"))
                    {
                        string name = e.Message.Substring("Se conectó ".Length);
                        Enqueue(new Ctx { Kind = "usb", Key = "usb" + name, Glyph = Glyphs.Usb, Title = name, Subtitle = "conectado", Height = 38, Seconds = 4 });
                    }
                    else if (e.Message.StartsWith("Unidad disponible: "))
                    {
                        string letter = e.Message.Substring("Unidad disponible: ".Length, 2);
                        string free = "";
                        try { var d = new DriveInfo(letter); if (d.IsReady) free = " " + Format.Bytes((ulong)d.AvailableFreeSpace) + " libres"; } catch { }
                        // Completar el aviso del USB recién conectado con su letra y espacio libre.
                        var usb = active != null && active.Kind == "usb" ? active : queue.FirstOrDefault(q => q.Kind == "usb");
                        if (usb != null) { usb.Subtitle = "conectado · " + letter + free; usb.Until = DateTime.Now.AddSeconds(4); StartResize(); Invalidate(); }
                        else Enqueue(new Ctx { Kind = "usb", Key = "vol" + letter, Glyph = Glyphs.HardDrive, Title = "Unidad " + letter, Subtitle = "disponible ·" + free, Height = 38, Seconds = 4 });
                    }
                }));
            }
            catch { }
        }

        // ================================================================== mouse

        void UpdatePolling()
        {
            if (!Settings.IslandAutoHide) SetAutoHidden(false);
            if (expanded || Settings.IslandHoverExpand || Settings.IslandAutoHide || fullScreen) poll.Start(); else poll.Stop();
        }

        /// <summary>Expandir al pasar el mouse y colapsar al hacer clic afuera (la isla nunca toma el foco).</summary>
        void OnPoll()
        {
            var p = Cursor.Position;
            if (fullScreen)
            {
                // Borde de la pantalla donde está la isla, a su altura.
                bool nearEdge = NearEdge(p);
                bool overIsland = Visible && Rectangle.Inflate(ShapeScreen, S(24), S(24)).Contains(p);
                if (nearEdge || overIsland || openMenu != null) revealUntil = DateTime.Now.AddMilliseconds(700);
                UpdateVisibility();
            }
            else if (Settings.IslandAutoHide && islandOn)
            {
                // Cerca de su lugar o contra el borde: aparece. Expandida, con un aviso o con el menú abierto: se queda.
                var home = HomeShape(S(cur.Width), S(cur.Height));
                bool near = Rectangle.Inflate(home, S(40), S(40)).Contains(p) || NearEdge(p);
                if (near || expanded || active != null || openMenu != null) nearUntil = DateTime.Now.AddMilliseconds(near ? 900 : 300);
                SetAutoHidden(DateTime.Now >= nearUntil);
            }
            if (!Visible) return;
            bool inside = ShapeScreen.Contains(p);
            if (inside && !wasInside) insideSince = DateTime.Now;
            if (!inside && wasInside) outsideSince = DateTime.Now;
            wasInside = inside;

            if (Settings.IslandHoverExpand)
            {
                if (inside && !expanded && (DateTime.Now - insideSince).TotalMilliseconds >= 250) SetExpanded(true);
                if (!inside && expanded && !pinned && openMenu == null && (DateTime.Now - outsideSince).TotalMilliseconds >= 400) SetExpanded(false);
            }
            if (expanded && !pinned && !inside && openMenu == null &&
                ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0))
                SetExpanded(false);
        }

        ContextMenuStrip openMenu;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var h = HitAt(e.Location);
            if (e.Button == MouseButtons.Left && h == null && ShapeClient.Contains(e.Location))
            {
                // Clic en la isla: un evento contextual se descarta; si no, alterna compacta ↔ expandida.
                if (active != null && !expanded) { DismissActive(); return; }
                SetExpanded(!expanded);
                return;
            }
            if (e.Button == MouseButtons.Right && (h == null || h.RightClick == null)) { ShowOptions(e.Location); return; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (expanded || active != null) return;
            int set = (Settings.IslandCompactSet + (e.Delta < 0 ? 1 : 2)) % 3;
            Settings.IslandCompactSet = set;
            StartResize();
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && expanded) { SetExpanded(false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowOptions(Point at)
        {
            var m = FluentMenu.Create(th, s);
            string[] posText = { "Posición: arriba al centro", "Posición: sobre la barra de tareas", "Posición: a la izquierda", "Posición: a la derecha" };
            for (int i = 0; i < Settings.IslandPositions.Length; i++)
            {
                string p = Settings.IslandPositions[i];
                var it = FluentMenu.Add(m, posText[i], null, true, () => { Settings.IslandPosition = p; ChangePosition(); });
                it.Checked = Pos == p;
            }
            var ah = FluentMenu.Add(m, "Ocultar automáticamente", null, true, () => { Settings.IslandAutoHide = !Settings.IslandAutoHide; UpdatePolling(); });
            ah.Checked = Settings.IslandAutoHide;
            FluentMenu.Separator(m);
            var hv = FluentMenu.Add(m, "Expandir al pasar el mouse", null, true, () => { Settings.IslandHoverExpand = !Settings.IslandHoverExpand; UpdatePolling(); });
            hv.Checked = Settings.IslandHoverExpand;
            var al = FluentMenu.Add(m, "Mostrar alertas en la isla", null, true, () => { Settings.IslandAlerts = !Settings.IslandAlerts; if (!Settings.IslandAlerts) { queue.Clear(); download = null; DismissActive(); } });
            al.Checked = Settings.IslandAlerts;
            var ac = FluentMenu.Add(m, "Todos los anillos con el color de acento", null, true, () => { Settings.IslandColorMode = Settings.IslandColorMode == "Accent" ? "PerMetric" : "Accent"; Invalidate(); });
            ac.Checked = Settings.IslandColorMode == "Accent";
            FluentMenu.Separator(m);
            FluentMenu.Add(m, "Abrir Panoptic", Glyphs.OpenExternal, true, () => host.OpenMain(null));
            FluentMenu.Add(m, "Configuración de la isla…", Glyphs.Settings, true, () => host.OpenMain("settings"));
            FluentMenu.Add(m, "Ocultar isla", Glyphs.Cancel, true, host.ToggleWidget);
            openMenu = m;
            m.Closed += (o, e) => { openMenu = null; BeginInvoke(new Action(m.Dispose)); };
            m.Show(this, at);
        }

        // ================================================================== pintado

        Color MetricColor(Color c) { return Settings.IslandColorMode == "Accent" ? th.Accent : c; }
        Color Track(Color c) { return Color.FromArgb((int)(255 * 0.22), c); }
        Color Secondary { get { return th.Dark ? Color.FromArgb(158, 255, 255, 255) : Color.FromArgb(153, 0, 0, 0); } }

        protected override void PaintSurface(Graphics g)
        {
            // Fondo: acrílico de Windows (no se pinta nada), vibrancia si Windows apagó el desenfoque, o sólido.
            if (th.Glass) g.Clear(Color.Transparent);
            else if (th.TransparencyEnabled && vibrancy.Ensure(ShapeScreen, th.Dark)) vibrancy.Draw(g, ShapeScreen, AreaRect);
            else g.Clear(th.Dark ? Theme.Hex(0x202020) : Theme.Hex(0xF3F3F3));
            if (active != null && active.Critical && !expanded)
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * 0.14), th.Critical))) g.FillRectangle(b, AreaRect);

            var snap = host.Latest;
            if (snap == null)
            {
                // Antes de la primera medición: nunca una isla vacía.
                DrawText(g, AppInfo.Name + " · Iniciando…", fonts.Body, AreaRect, Secondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            float alpha = ContentAlpha();
            if (alpha >= 0.999f) PaintContent(g, snap);
            else if (alpha > 0.01f)
            {
                // Fundido: el contenido se pinta en una capa y se compone con opacidad.
                using (var layer = new Bitmap(Area.Width, Area.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var lg = Graphics.FromImage(layer))
                    {
                        lg.SmoothingMode = SmoothingMode.AntiAlias;
                        lg.PixelOffsetMode = PixelOffsetMode.Half;
                        lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        lg.Clear(Color.Transparent);
                        bool glass = th.Glass;
                        th.Glass = true; // en la capa con alfa el texto se dibuja con GDI+
                        try { PaintContent(lg, snap); } finally { th.Glass = glass; }
                    }
                    using (var ia = new ImageAttributes())
                    {
                        var cm = new ColorMatrix(); cm.Matrix33 = alpha;
                        ia.SetColorMatrix(cm);
                        g.DrawImage(layer, new Rectangle(0, 0, layer.Width, layer.Height), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
        }

        void PaintContent(Graphics g, Snapshot snap)
        {
            if (expanded) { if (Top) PaintExpandedTop(g, snap); else PaintExpandedTaskbar(g, snap); return; }
            var ctx = active ?? download;
            if (ctx != null) { PaintContext(g, ctx); return; }
            if (Top) PaintCompactTop(g, snap, AreaRect);
            else if (Side) PaintCompactSide(g, snap, AreaRect);
            else PaintCompactTaskbar(g, snap, AreaRect);
        }

        // ---- Compacta (costados): vertical

        void PaintCompactSide(Graphics g, Snapshot snap, Rectangle r)
        {
            int cx = r.X + r.Width / 2, y = r.Y + S(12);
            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            AppIcon(g, new Rectangle(cx - S(11), y, S(22), S(22)));
            y += S(22) + S(12);
            var net = snap.Nets.OrderByDescending(n => n.RecvBps + n.SendBps).FirstOrDefault();

            if (Settings.IslandCompactSet == 2)
            {
                // Vista de red: tipo de conexión y velocidades, una debajo de la otra.
                Color nc = MetricColor(ResColors.Net(th));
                string name = net == null ? "Sin red" : net.Kind;
                DrawText(g, name, fCaption12, new Rectangle(r.X, y, r.Width, S(18)), nc, center);
                y += S(18) + S(8);
                if (net != null)
                    foreach (var line in new[] { "↓", Format.Bits(net.RecvBps), "↑", Format.Bits(net.SendBps) })
                    {
                        DrawText(g, line, fCaption12, new Rectangle(r.X, y, r.Width, S(16)), line.Length == 1 ? Secondary : th.Text, center);
                        y += S(16);
                    }
                AddHit("compact", r, null, "Isla: " + name + (net != null ? ", bajada " + Format.Bits(net.RecvBps) : "")).Focusable = false;
                return;
            }

            var ms = CompactMetrics(snap);
            foreach (var m in ms)
            {
                float v = Value("c-" + m.Id, m.Value);
                Ring(g, new RectangleF(cx - S(12), y, S(24), S(24)), 3f * s, v, MetricColor(m.Color));
                y += S(24) + S(4);
                DrawText(g, m.Short, fValue13, new Rectangle(r.X, y, r.Width, S(16)), th.Text, center);
                y += S(16) + S(10);
            }
            using (var b = new SolidBrush(th.Dark ? Color.FromArgb(31, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0)))
                g.FillRectangle(b, cx - S(10), y - S(5), S(20), Math.Max(1, S(1)));
            if (net != null)
            {
                // "↓ 11,2" y la unidad abajo: no entra en una sola línea.
                string bits = Format.Bits(net.RecvBps);
                int sp = bits.LastIndexOf(' ');
                DrawText(g, "↓ " + (sp > 0 ? bits.Substring(0, sp) : bits), fCaption12, new Rectangle(r.X, y, r.Width, S(16)), Secondary, center);
                if (sp > 0) DrawText(g, bits.Substring(sp + 1), fSmall11, new Rectangle(r.X, y + S(16), r.Width, S(16)), Secondary, center);
            }
            AddHit("compact", r, null, "Isla: " + string.Join(", ", ms.Select(m => m.Label + " " + m.Short))).Focusable = false;
        }

        /// <summary>Anillo de progreso: pista al 22 %, trazo con extremos redondeados, empieza a las 12 y gira en sentido horario.</summary>
        void Ring(Graphics g, RectangleF r, float stroke, float value, Color c)
        {
            var inner = RectangleF.Inflate(r, -stroke / 2f, -stroke / 2f);
            using (var t = new Pen(Track(c), stroke)) g.DrawEllipse(t, inner);
            if (value > 0.3f)
                using (var p = new Pen(c, stroke))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawArc(p, inner, -90f, 360f * Math.Min(100f, value) / 100f);
                }
        }

        void AppIcon(Graphics g, Rectangle r)
        {
            Color c = MetricColor(ResColors.Memory(th));
            Fill(g, r, Color.FromArgb((int)(255 * 0.18), c), S(4));
            Glyph(g, Glyphs.Performance, r.Width >= S(24) ? fonts.Glyph16 : fonts.Glyph12, r, c);
        }

        // ---- Compacta (arriba)

        sealed class Metric { public string Id, Label, Detail, Short; public float Value; public Color Color; public string Text; }

        List<Metric> CompactMetrics(Snapshot snap)
        {
            var all = Cells(snap).ToDictionary(m => m.Id);
            var ids = Settings.IslandCompactSet == 0 ? new[] { "cpu", "mem", all.ContainsKey("temp") ? "temp" : "disk" }
                    : new[] { "cpu", all.ContainsKey("gpu") ? "gpu" : "mem", "disk" };
            var list = ids.Where(all.ContainsKey).Select(i => all[i]).ToList();
            if (all.ContainsKey("bat")) list.Add(all["bat"]); // con batería, la compacta suma su anillo
            return list;
        }

        void PaintCompactTop(Graphics g, Snapshot snap, Rectangle r)
        {
            int x = r.X + S(12), cy = r.Y + r.Height / 2;
            AppIcon(g, new Rectangle(x, cy - S(11), S(22), S(22)));
            x += S(22) + S(14);
            var net = snap.Nets.OrderByDescending(n => n.RecvBps + n.SendBps).FirstOrDefault();

            if (Settings.IslandCompactSet == 2)
            {
                // Vista de red: nombre, gráfico y velocidades.
                Color nc = MetricColor(ResColors.Net(th));
                string name = net == null ? "Sin red" : net.Kind;
                DrawText(g, name, fValue13, new Rectangle(x, r.Y, S(60), r.Height), nc);
                x += MeasureText(name, fValue13) + S(10);
                if (net != null)
                {
                    MiniGraph(g, new Rectangle(x, cy - S(9), S(64), S(18)), net.RecvHistory.Skip(History.Size - 24).ToArray(), nc);
                    x += S(64) + S(12);
                    DrawText(g, "↓ " + Format.Bits(net.RecvBps) + "  ↑ " + Format.Bits(net.SendBps), fCaption12, new Rectangle(x, r.Y, r.Right - x - S(16), r.Height), Secondary);
                }
                AddHit("compact", r, null, "Isla: " + name + (net != null ? ", bajada " + Format.Bits(net.RecvBps) : "")).Focusable = false;
                return;
            }

            var ms = CompactMetrics(snap);
            int valW = MeasureText("88%", fValue13) + S(2); // ancho fijo: el texto no salta
            foreach (var m in ms)
            {
                float v = Value("c-" + m.Id, m.Value);
                Ring(g, new RectangleF(x, cy - S(8), S(16), S(16)), 2.8f * s, v, MetricColor(m.Color));
                x += S(16) + S(6);
                DrawText(g, m.Short, fValue13, new Rectangle(x, r.Y, valW + S(8), r.Height), th.Text);
                x += valW + S(14);
            }
            using (var b = new SolidBrush(th.Dark ? Color.FromArgb(31, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0)))
                g.FillRectangle(b, x - S(4), cy - S(8), Math.Max(1, S(1)), S(16));
            x += S(10);
            if (net != null) DrawText(g, "↓ " + Format.Bits(net.RecvBps), fCaption12, new Rectangle(x, r.Y, r.Right - x - S(12), r.Height), Secondary);
            AddHit("compact", r, null, "Isla: " + string.Join(", ", ms.Select(m => m.Label + " " + m.Short))).Focusable = false;
        }

        // ---- Compacta (sobre la barra de tareas)

        void PaintCompactTaskbar(Graphics g, Snapshot snap, Rectangle r)
        {
            int x = r.X + S(14), cy = r.Y + r.Height / 2;
            Color cc = MetricColor(ResColors.Cpu(th)), mc = MetricColor(ResColors.Memory(th));
            DrawText(g, "CPU", fCaption12, new Rectangle(x, r.Y, S(32), r.Height), Secondary);
            x += S(32);
            MiniGraph(g, new Rectangle(x, cy - S(9), S(64), S(18)), snap.CpuHistory.Skip(History.Size - 24).ToArray(), cc);
            x += S(64) + S(8);
            DrawText(g, snap.Cpu.ToString("0") + " %", fValue13, new Rectangle(x, r.Y, S(48), r.Height), th.Text);
            string mem = snap.MemPercent.ToString("0") + " %";
            int mw = MeasureText(mem, fValue13);
            DrawText(g, mem, fValue13, new Rectangle(r.Right - S(14) - mw - S(2), r.Y, mw + S(4), r.Height), mc);
            DrawText(g, "Mem", fCaption12, new Rectangle(r.Right - S(14) - mw - S(40), r.Y, S(34), r.Height), Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            AddHit("compact", r, null, "Isla: CPU " + snap.Cpu.ToString("0") + " %, memoria " + mem).Focusable = false;
        }

        void MiniGraph(Graphics g, Rectangle r, float[] values, Color c)
        {
            if (values == null || values.Length < 2) return;
            float max = Math.Max(1, values.Max()) * 1.15f;
            if (c == MetricColor(ResColors.Cpu(th))) max = 100;
            var pts = new PointF[values.Length];
            for (int i = 0; i < values.Length; i++)
                pts[i] = new PointF(r.X + (r.Width - 1f) * i / (values.Length - 1), r.Bottom - 1 - (r.Height - 2) * Math.Min(max, values[i]) / max);
            using (var path = new GraphicsPath())
            {
                path.AddLine(pts[0].X, r.Bottom, pts[0].X, pts[0].Y);
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, r.Bottom);
                path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(50, c))) g.FillPath(b, path);
            }
            using (var p = new Pen(c, 1.5f * s)) { p.LineJoin = LineJoin.Round; g.DrawLines(p, pts); }
        }

        // ---- Métricas (celdas de la vista expandida)

        List<Metric> Cells(Snapshot snap)
        {
            var list = new List<Metric>();
            if (snap == null) return list;
            list.Add(new Metric { Id = "cpu", Label = "CPU", Value = snap.Cpu, Short = snap.Cpu.ToString("0") + "%", Color = ResColors.Cpu(th),
                Detail = snap.CpuMhz > 0 ? Format.Ghz(snap.CpuMhz) : snap.Logical + " lógicos" });
            list.Add(new Metric { Id = "mem", Label = "Memoria", Value = snap.MemPercent, Short = snap.MemPercent.ToString("0") + "%", Color = ResColors.Memory(th),
                Detail = Format.Bytes(snap.MemUsed) });
            var d = snap.Disks.OrderByDescending(x => x.Active).FirstOrDefault();
            if (d != null)
                list.Add(new Metric { Id = "disk", Label = "Disco", Value = d.Active, Short = d.Active.ToString("0") + "%", Color = ResColors.Disk(th),
                    Detail = Format.Rate(d.ReadBps + d.WriteBps) });
            if (snap.GpuAvailable)
                list.Add(new Metric { Id = "gpu", Label = "GPU", Value = snap.Gpu, Short = snap.Gpu.ToString("0") + "%", Color = ResColors.Gpu(th),
                    Detail = Format.Bytes(snap.GpuDedicated + snap.GpuShared) });
            if (!double.IsNaN(snap.Temperature))
                list.Add(new Metric { Id = "temp", Label = "Temp.", Value = (float)Math.Min(100, snap.Temperature), Short = snap.Temperature.ToString("0") + "°", Color = ResColors.Temp(th),
                    Detail = snap.Temperature >= Settings.TempThreshold ? "Alta" : "Normal" });
            if (snap.HasBattery)
                list.Add(new Metric { Id = "bat", Label = "Batería", Value = snap.BatteryPercent, Short = snap.BatteryPercent.ToString("0") + "%", Color = ResColors.Battery(th),
                    Detail = snap.OnAc ? (snap.Charging ? "Cargando" : "Enchufada") : (snap.BatteryMinutes > 0 ? (snap.BatteryMinutes / 60) + " h " + (snap.BatteryMinutes % 60) + " min" : "Con batería") });
            else
            {
                // Sin batería: la red, como porcentaje del ancho de banda del adaptador.
                var n = snap.Nets.OrderByDescending(x => x.SendBps + x.RecvBps).FirstOrDefault();
                if (n != null && n.LinkSpeed > 0)
                {
                    float pct = (float)Math.Min(100, (n.SendBps + n.RecvBps) * 8 / n.LinkSpeed * 100);
                    list.Add(new Metric { Id = "net", Label = n.Kind, Value = pct, Short = pct.ToString(pct < 10 ? "0.0" : "0") + "%", Color = ResColors.Net(th),
                        Detail = Format.Bits(n.SendBps + n.RecvBps) });
                }
            }
            return list;
        }

        // ---- Expandida (arriba): encabezado, 6 anillos y red

        void PaintExpandedTop(Graphics g, Snapshot snap)
        {
            int W = Area.Width;
            int pad = S(16), y = S(14);
            Header(g, new Rectangle(pad, y, W - 2 * pad, S(28)));
            y += S(28) + S(14);

            var cells = Cells(snap);
            int n = Math.Max(1, cells.Count), gap = S(4);
            int cw = (W - 2 * pad - gap * (n - 1)) / n, ch = S(104);
            for (int i = 0; i < cells.Count; i++)
            {
                var m = cells[i];
                var r = new Rectangle(pad + i * (cw + gap), y, cw, ch);
                string key = "cell-" + m.Id;
                string page = m.Id == "net" ? "net" : m.Id;
                AddHit(key, r, () => host.OpenMain(page), m.Label + " " + m.Short + ", " + m.Detail).Tip = "Abrir " + m.Label + " en el Monitor";
                if (IsHover(key)) Fill(g, r, th.HoverOverlay, S(4));
                float v = Value("e-" + m.Id, m.Value);
                var rr = new RectangleF(r.X + (r.Width - S(64)) / 2f, r.Y + S(4), S(64), S(64));
                Color c = MetricColor(m.Color);
                Ring(g, rr, 3.5f * s, v, c);
                DrawText(g, m.Short, fDisplay16, Rectangle.Round(rr), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                DrawText(g, m.Label, fCaption12, new Rectangle(r.X, (int)rr.Bottom + S(4), r.Width, S(16)), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                DrawText(g, m.Detail, fSmall11, new Rectangle(r.X, (int)rr.Bottom + S(20), r.Width, S(14)), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            y += ch + S(14);
            NetRow(g, snap, new Rectangle(pad, y, W - 2 * pad, Area.Height - y - S(16)));
        }

        void Header(Graphics g, Rectangle r)
        {
            AppIcon(g, new Rectangle(r.X, r.Y + (r.Height - S(24)) / 2, S(24), S(24)));
            DrawText(g, AppInfo.Name, fonts.BodyStrong, new Rectangle(r.X + S(34), r.Y, S(80), r.Height), th.Text);
            int ago = (int)Math.Max(0, (DateTime.Now - lastData).TotalSeconds) + 1;
            DrawText(g, "Actualizado hace " + ago + " s", fCaption12, new Rectangle(r.X + S(120), r.Y, S(200), r.Height), Secondary);
            var open = SubtleButton(g, "open", new Rectangle(r.Right - S(32), r.Y, S(32), S(28)), Glyphs.OpenExternal, null, "Abrir ventana principal", () => host.OpenMain(null));
            var pin = SubtleButton(g, "pin", new Rectangle(r.Right - S(68), r.Y, S(32), S(28)), pinned ? Glyphs.Unpin : Glyphs.Pin, null,
                pinned ? "Dejar de fijar (se cierra al hacer clic afuera)" : "Fijar expandida", () => { pinned = !pinned; Invalidate(); });
            pin.Checked = pinned;
            pin.Role = AccessibleRole.CheckButton;
        }

        void NetRow(Graphics g, Snapshot snap, Rectangle r)
        {
            using (var p = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), S(4)))
            {
                using (var b = new SolidBrush(th.Dark ? Color.FromArgb(10, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255))) g.FillPath(b, p);
                using (var pen = new Pen(th.Dark ? Color.FromArgb(13, 255, 255, 255) : Color.FromArgb(13, 0, 0, 0))) g.DrawPath(pen, p);
            }
            var n = snap.Nets.OrderByDescending(x => x.SendBps + x.RecvBps).FirstOrDefault();
            var inner = new Rectangle(r.X + S(12), r.Y + S(8), r.Width - S(24), r.Height - S(16));
            if (n == null) { DrawText(g, "Sin conexión de red", fCaption12, inner, Secondary); return; }
            Color c = MetricColor(ResColors.Net(th));
            string name = n.Kind == "Wi-Fi" ? "Wi-Fi" : n.Name;
            int nameW = MeasureText(name, fValue13) + S(12);
            DrawText(g, name, fValue13, new Rectangle(inner.X, inner.Y, nameW, inner.Height), c);
            string down = "↓ " + Format.Bits(n.RecvBps), up = "↑ " + Format.Bits(n.SendBps);
            int dw = MeasureText(down, fValue13) + S(14), uw = MeasureText(up, fCaption12) + S(4);
            DrawText(g, up, fCaption12, new Rectangle(inner.Right - uw, inner.Y, uw, inner.Height), Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            DrawText(g, down, fValue13, new Rectangle(inner.Right - uw - dw, inner.Y, dw, inner.Height), th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var gr = new Rectangle(inner.X + nameW, inner.Y + S(2), inner.Width - nameW - uw - dw - S(12), inner.Height - S(4));
            if (gr.Width > S(20))
            {
                var vals = n.RecvHistory.Skip(History.Size - 40).ToArray();
                float max = Math.Max(1, vals.Max()) * 1.15f;
                var pts = new PointF[vals.Length];
                for (int i = 0; i < vals.Length; i++)
                    pts[i] = new PointF(gr.X + (gr.Width - 1f) * i / (vals.Length - 1), gr.Bottom - 1 - (gr.Height - 2) * vals[i] / max);
                using (var pen = new Pen(c, 1.5f * s)) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, pts); }
            }
            AddHit("netrow", r, () => host.OpenMain("net"), name + ": bajada " + Format.Bits(n.RecvBps) + ", subida " + Format.Bits(n.SendBps));
        }

        // ---- Expandida (sobre la barra de tareas): barras, procesos y la compacta abajo

        void PaintExpandedTaskbar(Graphics g, Snapshot snap)
        {
            int W = Area.Width, H = Area.Height, pad = S(16);
            // La compacta queda como última fila.
            var compact = new Rectangle(0, H - S(36), W, S(36));
            PaintCompactTaskbar(g, snap, compact);
            using (var b = new SolidBrush(th.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)))
                g.FillRectangle(b, pad, compact.Y - 1, W - 2 * pad, 1);

            // "Más consumo": los 3 procesos con más CPU.
            var top = snap.Procs.Where(p => p.Name != "Idle").OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory).Take(3).ToList();
            int y = compact.Y - S(8) - top.Count * S(32);
            DrawText(g, "Más consumo", fCaption12, new Rectangle(pad, y - S(24), S(200), S(20)), Secondary);
            foreach (var p in top)
            {
                var r = new Rectangle(pad - S(6), y, W - 2 * pad + S(12), S(32));
                string key = "proc" + p.Pid;
                var proc = p;
                var h = AddHit(key, r, () => host.OpenProcess(proc.Pid), p.Display + ", CPU " + Format.Percent(p.Cpu) + ", memoria " + Format.Bytes(p.Memory));
                h.RightClick = pt =>
                {
                    var m = FluentMenu.Create(th, s);
                    FluentMenu.Add(m, "Finalizar tarea", Glyphs.Cancel, true, () => host.EndTask(proc, null));
                    openMenu = m;
                    m.Closed += (o, e) => { openMenu = null; BeginInvoke(new Action(m.Dispose)); };
                    m.Show(this, pt);
                };
                if (IsHover(key)) Fill(g, r, th.HoverOverlay, S(4));
                var ir = new Rectangle(r.X + S(6), r.Y + S(8), S(16), S(16));
                if (p.Icon != null) DrawIcon(g, p.Icon, ir);
                else Glyph(g, Glyphs.App, fonts.Glyph12, ir, Secondary);
                DrawText(g, p.Display, fCaption12, new Rectangle(ir.Right + S(10), r.Y, r.Width - S(180), r.Height), th.Text);
                DrawText(g, Format.Percent(p.Cpu), fValue13, new Rectangle(r.Right - S(150), r.Y, S(64), r.Height), th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                DrawText(g, Format.Bytes(p.Memory), fCaption12, new Rectangle(r.Right - S(82), r.Y, S(74), r.Height), Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                y += S(32);
            }
            int lineY = compact.Y - S(8) - top.Count * S(32) - S(32);
            using (var b = new SolidBrush(th.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)))
                g.FillRectangle(b, pad, lineY, W - 2 * pad, 1);

            // Seis filas: etiqueta y subtítulo | barra de 4 px | valor.
            var cells = Cells(snap);
            int rowH = S(34);
            int ry = lineY - S(8) - cells.Count * rowH;
            Header(g, new Rectangle(pad, Math.Max(S(10), ry - S(42)), W - 2 * pad, S(28)));
            foreach (var m in cells)
            {
                var r = new Rectangle(pad, ry, W - 2 * pad, rowH);
                string key = "row-" + m.Id;
                string page = m.Id;
                AddHit(key, r, () => host.OpenMain(page), m.Label + " " + m.Short + ", " + m.Detail);
                if (IsHover(key)) Fill(g, Rectangle.Inflate(r, S(6), 0), th.HoverOverlay, S(4));
                Color c = MetricColor(m.Color);
                DrawText(g, m.Label, fCaption12, new Rectangle(r.X, r.Y + S(1), S(84), S(16)), th.Text);
                DrawText(g, m.Detail, fSmall11, new Rectangle(r.X, r.Y + S(17), S(84), S(14)), c);
                float v = Value("b-" + m.Id, m.Value);
                var bar = new Rectangle(r.X + S(92), r.Y + r.Height / 2 - S(2), r.Width - S(92) - S(52), S(4));
                Fill(g, bar, Track(c), S(2));
                if (v > 0.3f) Fill(g, new Rectangle(bar.X, bar.Y, Math.Max(S(4), (int)(bar.Width * Math.Min(100, v) / 100f)), bar.Height), c, S(2));
                DrawText(g, m.Short, fValue13, new Rectangle(r.Right - S(44), r.Y, S(44), r.Height), th.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                ry += rowH;
            }
        }

        // ---- Estados contextuales

        float ContextWidth(Ctx c)
        {
            float w = 16;
            switch (c.Kind)
            {
                case "download":
                    w += MeasureText(c.Title, fCaption12) / s + 10 + 90 + 12 + MeasureText(c.Value, fValue13) / s;
                    break;
                case "temp":
                    w += MeasureText(c.Value, fDisplay18) / s + 12 + Math.Max(MeasureText(c.Title, fValue13), MeasureText(c.Subtitle, fSmall11)) / s + 16 + MeasureText(c.Button, fonts.Body) / s + 28;
                    break;
                case "hog":
                    w += 20 + 12 + Math.Max(MeasureText(c.Title, fValue13), MeasureText(c.Subtitle, fSmall11)) / s + 16 + MeasureText(c.Button, fonts.Body) / s + 28;
                    break;
                case "charger":
                    w += 26 + 10 + MeasureText(c.Value, fValue13) / s + 10 + MeasureText(c.Title, fCaption12) / s;
                    break;
                default:
                    w += 18 + 10 + Math.Max(MeasureText(c.Title, fValue13), MeasureText(c.Subtitle ?? "", fCaption12)) / s;
                    break;
            }
            return Math.Max(200, w + 16);
        }

        void PaintContext(Graphics g, Ctx c)
        {
            var r = AreaRect;
            int x = r.X + S(16), cy = r.Y + r.Height / 2;
            Color sec = Secondary;
            switch (c.Kind)
            {
                case "download":
                {
                    Color nc = MetricColor(ResColors.Net(th));
                    DrawText(g, c.Title, fCaption12, new Rectangle(x, r.Y, S(80), r.Height), nc);
                    x += MeasureText(c.Title, fCaption12) + S(10);
                    var snap = host.Latest;
                    var n = snap == null ? null : snap.Nets.OrderByDescending(q => q.RecvBps).FirstOrDefault();
                    if (n != null) MiniGraph(g, new Rectangle(x, cy - S(10), S(90), S(20)), n.RecvHistory.Skip(History.Size - 30).ToArray(), nc);
                    x += S(90) + S(12);
                    DrawText(g, n != null ? "↓ " + Format.Rate(n.RecvBps) : c.Value, fValue13, new Rectangle(x, r.Y, r.Right - x - S(12), r.Height), th.Text);
                    AddHit("ctx", r, null, "Descarga activa: " + c.Value).Focusable = false;
                    break;
                }
                case "temp":
                {
                    int vw = MeasureText(c.Value, fDisplay18) + S(4);
                    DrawText(g, c.Value, fDisplay18, new Rectangle(x, r.Y, vw, r.Height), th.Critical);
                    x += vw + S(12);
                    TwoLines(g, c, x, r);
                    ContextButton(g, c, r);
                    break;
                }
                case "hog":
                {
                    var ir = new Rectangle(x, cy - S(10), S(20), S(20));
                    if (c.Icon != null) DrawIcon(g, c.Icon, ir);
                    else Glyph(g, c.Glyph, fonts.Glyph16, ir, th.Text);
                    x += S(20) + S(12);
                    TwoLines(g, c, x, r);
                    ContextButton(g, c, r);
                    break;
                }
                case "charger":
                {
                    Color bc = MetricColor(ResColors.Battery(th));
                    var body = new Rectangle(x, cy - S(6), S(22), S(12));
                    using (var p = Theme.Round(new RectangleF(body.X + .5f, body.Y + .5f, body.Width - 1, body.Height - 1), S(2)))
                    using (var pen = new Pen(bc, 1.2f * s)) g.DrawPath(pen, p);
                    Fill(g, new Rectangle(body.Right, cy - S(3), S(2), S(6)), bc, S(1));
                    Fill(g, new Rectangle(body.X + S(2), body.Y + S(2), Math.Max(S(2), (int)((body.Width - S(4)) * c.Level / 100f)), body.Height - S(4)), bc, S(1));
                    x += S(26) + S(10);
                    DrawText(g, c.Value, fValue13, new Rectangle(x, r.Y, S(60), r.Height), th.Text);
                    x += MeasureText(c.Value, fValue13) + S(10);
                    DrawText(g, c.Title, fCaption12, new Rectangle(x, r.Y, r.Right - x - S(12), r.Height), bc);
                    AddHit("ctx", r, null, "Cargador conectado: " + c.Value).Focusable = false;
                    break;
                }
                default:
                {
                    Glyph(g, c.Glyph ?? Glyphs.Info, fonts.Glyph16, new Rectangle(x, cy - S(9), S(18), S(18)), th.Text);
                    x += S(18) + S(10);
                    DrawText(g, c.Title, fValue13, new Rectangle(x, r.Y + S(3), r.Right - x - S(12), r.Height / 2), th.Text, TextFormatFlags.Left | TextFormatFlags.Bottom);
                    DrawText(g, c.Subtitle, fCaption12, new Rectangle(x, cy, r.Right - x - S(12), r.Height / 2 - S(3)), sec, TextFormatFlags.Left | TextFormatFlags.Top);
                    AddHit("ctx", r, null, c.Title + ", " + c.Subtitle).Focusable = false;
                    break;
                }
            }
        }

        void TwoLines(Graphics g, Ctx c, int x, Rectangle r)
        {
            int cy = r.Y + r.Height / 2;
            DrawText(g, c.Title, fValue13, new Rectangle(x, r.Y + S(2), S(260), cy - r.Y - S(2)), th.Text, TextFormatFlags.Left | TextFormatFlags.Bottom);
            DrawText(g, c.Subtitle, fSmall11, new Rectangle(x, cy + S(1), S(260), r.Bottom - cy - S(3)), Secondary, TextFormatFlags.Left | TextFormatFlags.Top);
        }

        /// <summary>Botón de Windows 11 (alto 32, radio 4): primario con acento o secundario.</summary>
        void ContextButton(Graphics g, Ctx c, Rectangle r)
        {
            if (c.Button == null) return;
            int bw = MeasureText(c.Button, fonts.Body) + S(28);
            var br = new Rectangle(r.Right - S(8) - bw, r.Y + (r.Height - S(32)) / 2, bw, S(32));
            StandardButton(g, "ctx-btn", br, null, c.Button, c.Primary, true, () =>
            {
                var act = c.ButtonAction;
                DismissActive();
                if (act != null) act();
            });
            AddHit("ctx", new Rectangle(r.X, r.Y, br.X - r.X, r.Height), null, c.Title + ", " + c.Subtitle).Focusable = false;
        }

        // ================================================================== vista previa

        /// <summary>Prepara un estado para exportar una captura (--render).</summary>
        internal void PreviewState(bool expand, string position, string ctxKind)
        {
            previewMode = true;
            Settings.IslandPosition = position;
            expanded = expand;
            active = null; download = null;
            var snap = host.Latest;
            switch (ctxKind)
            {
                case "download": download = new Ctx { Kind = "download", Key = "download", Title = "Wi-Fi", Value = "↓ 12,4 MB/s", Height = 38 }; break;
                case "temp": active = new Ctx { Kind = "temp", Key = "temp", Critical = true, Height = 44, Value = "92°", Title = "Temperatura alta", Subtitle = "Supera el umbral de 85°", Button = "Ver" }; break;
                case "hog": active = new Ctx { Kind = "hog", Key = "hog", Height = 44, Glyph = Glyphs.App, Title = "msedge.exe", Subtitle = "48 % CPU durante 2 min", Button = "Finalizar tarea", Primary = true }; break;
                case "charger": active = new Ctx { Kind = "charger", Key = "charger", Height = 38, Level = 72, Value = "72 %", Title = "Cargando · lleno en 48 min" }; break;
                case "usb": active = new Ctx { Kind = "usb", Key = "usb", Glyph = Glyphs.Usb, Title = "Kingston DataTraveler", Subtitle = "conectado · E: 28,6 GB libres", Height = 38 }; break;
            }
            cur = from = to = TargetSize();
            ClientSize = new Size(S(cur.Width), S(cur.Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                Activity.Added -= OnActivity;
                data.Dispose(); anim.Dispose(); poll.Dispose(); screenCheck.Dispose();
                if (surface != null) surface.Dispose();
                if (content != null) content.Dispose();
                vibrancy.Dispose();
                foreach (var f in new[] { fValue13, fSmall11, fDisplay16, fDisplay18, fCaption12 }) if (f != null) f.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
