using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace SysMonitor
{
    /// <summary>Colores de cada recurso, como en el Administrador de tareas de Windows 11.</summary>
    internal static class ResColors
    {
        public static Color Cpu(Theme th) { return th.Accent; }
        public static Color Memory(Theme th) { return th.Dark ? Theme.Hex(0xC08CF0) : Theme.Hex(0x8764B8); }
        public static Color Disk(Theme th) { return th.Dark ? Theme.Hex(0x6CCB5F) : Theme.Hex(0x0F7B0F); }
        public static Color Net(Theme th) { return th.Dark ? Theme.Hex(0xF7A146) : Theme.Hex(0xC25E00); }
        public static Color Gpu(Theme th) { return th.Dark ? Theme.Hex(0x4CC2FF) : Theme.Hex(0x0E6FAE); }
        public static Color Temp(Theme th) { return th.Dark ? Theme.Hex(0xFF8A80) : Theme.Hex(0xC42B1C); }
        public static Color Battery(Theme th) { return th.Dark ? Theme.Hex(0x9EE493) : Theme.Hex(0x3B7D23); }
    }

    /// <summary>Zona interactiva registrada durante el pintado (interfaz de modo inmediato).</summary>
    internal sealed class Hit
    {
        public string Key;
        public Rectangle R;
        public Action Click;
        public Action<Point> RightClick;
        public string Name, Tip;
        public bool Focusable = true;
        public AccessibleRole Role = AccessibleRole.PushButton;
        public bool Checked;
    }

    /// <summary>
    /// Ventana dibujada a mano con el estilo de Windows 11. Cada pintado registra sus zonas
    /// interactivas; el mouse y el teclado trabajan sobre las del último pintado.
    /// </summary>
    internal abstract class Surface : Form
    {
        protected Theme th;
        protected Fonts fonts;
        protected float s = 1f;
        protected List<Hit> hits = new List<Hit>();
        List<Hit> building = new List<Hit>();
        protected string hoverKey, pressKey, focusKey;
        protected bool focusVisible;
        /// <summary>Texto siempre con GDI+ (necesario al pintar en una capa con transparencia por píxel).</summary>
        protected bool gdiPlusText;
        /// <summary>Desplazamiento del contenido dentro de la ventana (la isla dibuja su forma dentro de un lienzo más grande).</summary>
        protected Point contentOffset;
        readonly ToolTip tip = new ToolTip();
        string tipKey;

        protected Surface()
        {
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            th = Theme.Load();
            fonts = new Fonts(s);
            BackColor = th.Bg;
            tip.InitialDelay = 600;
            UpdatePaintMode();
        }

        protected int S(float v) { return (int)Math.Round(v * s); }

        /// <summary>Fondo del sistema que usa esta ventana cuando hay transparencia (Mica o Acrílico).</summary>
        protected virtual int Backdrop { get { return Native.DWMSBT_ACRYLIC; } }

        protected virtual void ApplyDwm()
        {
            if (!IsHandleCreated) return;
            int v = th.Dark ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
            Native.SetBackdrop(Handle, th.Glass ? Backdrop : Native.DWMSBT_NONE);
        }

        // Con transparencia se pinta en un bitmap con canal alfa propio (el doble búfer de WinForms no lo conserva).
        void UpdatePaintMode()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer, !th.Glass);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetScale(Native.ScaleForWindow(Handle));
            ApplyDwm();
        }

        public void ReloadTheme()
        {
            th = Theme.Load();
            BackColor = th.Bg;
            UpdatePaintMode();
            ApplyDwm();
            OnThemeReloaded();
            Invalidate(true);
        }

        protected void SetScale(float ns)
        {
            if (Math.Abs(ns - s) < 0.01f && fonts != null) return;
            s = ns;
            if (fonts != null) fonts.Dispose();
            fonts = new Fonts(s);
            OnScaleChanged();
        }

        protected virtual void OnScaleChanged() { }

        /// <summary>Después de recargar el tema (cambio de modo, acento, transparencia o fondo de pantalla).</summary>
        protected virtual void OnThemeReloaded() { }

        // ------------------------------------------------------------------ pintado

        protected abstract void PaintSurface(Graphics g);

        protected override void OnPaintBackground(PaintEventArgs e) { }

        Bitmap glassBuffer;

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!th.Glass) { PaintAll(e.Graphics); return; }
            var size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            if (glassBuffer == null || glassBuffer.Size != size)
            {
                if (glassBuffer != null) glassBuffer.Dispose();
                glassBuffer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            }
            using (var g = Graphics.FromImage(glassBuffer))
            {
                g.Clear(Color.Transparent);
                PaintAll(g);
            }
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.DrawImageUnscaled(glassBuffer, 0, 0);
        }

        /// <summary>Pinta la ventana en un bitmap (para las capturas de vista previa).</summary>
        public Bitmap RenderToBitmap()
        {
            var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); PaintAll(g); }
            return bmp;
        }

        // Estadísticas de pintado (solo en el modo de medición SYSMON_PROFILE).
        public static readonly Dictionary<string, double[]> PaintStats = new Dictionary<string, double[]>();
        public static bool ProfilePaint;

        /// <summary>Pinta todo el contenido (y registra las zonas de clic) en el Graphics dado.</summary>
        protected void PaintAll(Graphics g)
        {
            var swp = ProfilePaint ? System.Diagnostics.Stopwatch.StartNew() : null;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            building = new List<Hit>();
            PaintSurface(g);
            hits = building;
            if (focusVisible && focusKey != null)
            {
                var f = hits.FirstOrDefault(h => h.Key == focusKey);
                if (f != null)
                    using (var p = Theme.Round(new RectangleF(f.R.X + s, f.R.Y + s, f.R.Width - 2 * s, f.R.Height - 2 * s), S(4)))
                    using (var pen = new Pen(th.Text, 2 * s)) g.DrawPath(pen, p);
            }
            if (swp != null)
            {
                double[] st;
                if (!PaintStats.TryGetValue(GetType().Name, out st)) PaintStats[GetType().Name] = st = new double[2];
                st[0]++; st[1] += swp.Elapsed.TotalMilliseconds;
            }
        }

        protected Hit AddHit(string key, Rectangle r, Action click, string name)
        {
            var h = new Hit { Key = key, R = r, Click = click, Name = name };
            building.Add(h);
            return h;
        }

        protected bool IsHover(string key) { return hoverKey == key; }
        protected bool IsPressed(string key) { return pressKey == key && hoverKey == key; }

        // ------------------------------------------------------------------ controles dibujados

        protected void DrawText(Graphics g, string text, Font f, Rectangle r, Color c)
        {
            DrawText(g, text, f, r, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        protected void DrawText(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags align)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (th.Glass || gdiPlusText) { GlassText(g, text, f, r, c, align, true); return; }
            TextRenderer.DrawText(g, text, f, r, c, align | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        protected void Glyph(Graphics g, string glyph, Font f, Rectangle r, Color c)
        {
            if (th.Glass || gdiPlusText) { GlassText(g, glyph, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter, false); return; }
            TextRenderer.DrawText(g, glyph, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        /// <summary>
        /// Texto con GDI+ (suavizado en escala de grises): el texto de GDI no tiene canal alfa y sobre
        /// Mica/Acrílico se vería transparente. Windows también usa grises para el texto sobre Mica.
        /// </summary>
        // Formatos de texto reutilizados (uno por combinación de alineación): se crean una sola vez.
        static readonly Dictionary<int, StringFormat> formats = new Dictionary<int, StringFormat>();

        static StringFormat FormatFor(TextFormatFlags align, bool ellipsis)
        {
            var h = (align & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : (align & TextFormatFlags.Right) != 0 ? StringAlignment.Far : StringAlignment.Near;
            var v = (align & TextFormatFlags.VerticalCenter) != 0 ? StringAlignment.Center : (align & TextFormatFlags.Bottom) != 0 ? StringAlignment.Far : StringAlignment.Near;
            int key = (int)h * 10 + (int)v + (ellipsis ? 100 : 0);
            StringFormat sf;
            if (formats.TryGetValue(key, out sf)) return sf;
            sf = new StringFormat(StringFormat.GenericTypographic);
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            sf.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            sf.Alignment = h;
            sf.LineAlignment = v;
            sf.HotkeyPrefix = HotkeyPrefix.None;
            formats[key] = sf;
            return sf;
        }

        static void GlassText(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags align, bool ellipsis)
        {
            using (var b = new SolidBrush(c))
                g.DrawString(text, f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), FormatFor(align, ellipsis));
        }

        static readonly Bitmap measureBmp = new Bitmap(1, 1);

        protected int MeasureText(string text, Font f)
        {
            if (th.Glass || gdiPlusText)
                using (var g = Graphics.FromImage(measureBmp))
                using (var sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    return (int)Math.Ceiling(g.MeasureString(text, f, int.MaxValue, sf).Width) + 1;
                }
            return TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        protected Color ControlStroke
        {
            get
            {
                if (th.Glass) return th.Dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
                return th.Dark ? Theme.Hex(0x3A3A3A) : Theme.Hex(0xE5E5E5);
            }
        }

        // Íconos ya escalados a su tamaño final: escalar con calidad alta en cada cuadro es caro.
        static readonly Dictionary<Bitmap, Dictionary<int, Bitmap>> scaledIcons = new Dictionary<Bitmap, Dictionary<int, Bitmap>>();

        protected void DrawIcon(Graphics g, Bitmap src, Rectangle r)
        {
            if (src == null) return;
            Dictionary<int, Bitmap> sizes;
            if (!scaledIcons.TryGetValue(src, out sizes)) scaledIcons[src] = sizes = new Dictionary<int, Bitmap>();
            Bitmap b;
            if (!sizes.TryGetValue(r.Width, out b))
            {
                b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppPArgb);
                using (var sg = Graphics.FromImage(b))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    sg.DrawImage(src, new Rectangle(0, 0, b.Width, b.Height));
                }
                sizes[r.Width] = b;
            }
            g.DrawImageUnscaled(b, r.X, r.Y);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);

        /// <summary>
        /// Relleno rápido de rectángulos grandes: con colores opacos usa GDI (mucho más rápido que GDI+ sobre el
        /// búfer de la ventana); con transparencia, GDI+ en modo copia para conservar el canal alfa.
        /// </summary>
        protected void FastFill(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (!th.Glass && c.A == 255)
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    IntPtr br = CreateSolidBrush(Native.ColorRef(c));
                    var rc = new RECT { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom };
                    FillRect(hdc, ref rc, br);
                    DeleteObject(br);
                }
                finally { g.ReleaseHdc(hdc); }
                return;
            }
            var mode = g.CompositingMode; var sm = g.SmoothingMode;
            g.CompositingMode = CompositingMode.SourceCopy; g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
            g.CompositingMode = mode; g.SmoothingMode = sm;
        }

        protected void Card(Graphics g, Rectangle r)
        {
            Card(g, r, th.Card);
        }

        protected void Card(Graphics g, Rectangle r, Color fill)
        {
            using (var p = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), S(6)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(th.CardStroke)) g.DrawPath(pen, p);
            }
        }

        protected void Fill(Graphics g, Rectangle r, Color c, float radius)
        {
            using (var p = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        /// <summary>Barra de progreso de Windows 11 (pista fina y barra redondeada).</summary>
        protected void Bar(Graphics g, Rectangle r, float frac, Color c)
        {
            frac = Math.Max(0, Math.Min(1, frac));
            float h = Math.Max(2 * s, r.Height);
            var track = new RectangleF(r.X, r.Y + (r.Height - h) / 2f, r.Width, h);
            using (var p = Theme.Round(track, h / 2))
            using (var b = new SolidBrush(th.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0))) g.FillPath(b, p);
            if (frac <= 0) return;
            var fill = new RectangleF(track.X, track.Y, Math.Max(h, track.Width * frac), h);
            using (var p = Theme.Round(fill, h / 2))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        /// <summary>Gráfico de área con línea, como los del Administrador de tareas.</summary>
        protected void Graph(Graphics g, Rectangle r, float[] values, float max, Color c, bool grid, bool frame)
        {
            if (frame)
                using (var pen = new Pen(Color.FromArgb(th.Dark ? 70 : 90, c), Math.Max(1, s)))
                    g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            if (grid)
            {
                using (var pen = new Pen(Color.FromArgb(th.Dark ? 28 : 36, c), 1))
                {
                    for (int i = 1; i < 10; i++) { int x = r.X + r.Width * i / 10; g.DrawLine(pen, x, r.Y + 1, x, r.Bottom - 2); }
                    for (int i = 1; i < 10; i++) { int y = r.Y + r.Height * i / 10; g.DrawLine(pen, r.X + 1, y, r.Right - 2, y); }
                }
            }
            if (values == null || values.Length < 2) return;
            if (max <= 0) max = 1;
            var pts = new PointF[values.Length];
            float inset = frame ? 1 : 0;
            float w = r.Width - 2 * inset, h = r.Height - 2 * inset;
            for (int i = 0; i < values.Length; i++)
            {
                float v = Math.Max(0, Math.Min(max, values[i]));
                pts[i] = new PointF(r.X + inset + w * i / (values.Length - 1), r.Y + inset + h - h * v / max);
            }
            var state = g.Save();
            g.SetClip(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));
            using (var path = new GraphicsPath())
            {
                path.AddLine(pts[0].X, r.Bottom, pts[0].X, pts[0].Y);
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, r.Bottom);
                path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(th.Dark ? 60 : 45, c))) g.FillPath(b, path);
            }
            using (var pen = new Pen(c, Math.Max(1.2f, 1.5f * s)))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }
            g.Restore(state);
        }

        /// <summary>Botón sutil (fondo solo al pasar el mouse) con glifo y texto opcional.</summary>
        protected Hit SubtleButton(Graphics g, string key, Rectangle r, string glyph, string text, string name, Action click)
        {
            var h = AddHit(key, r, click, name ?? text);
            if (IsHover(key))
                Fill(g, r, IsPressed(key) ? th.PressedOverlay : th.HoverOverlay, S(4));
            if (text == null) Glyph(g, glyph, fonts.Glyph16, r, th.Text);
            else
            {
                int x = r.X + S(12);
                if (glyph != null)
                {
                    Glyph(g, glyph, fonts.Glyph16, new Rectangle(x, r.Y, S(16), r.Height), th.Text);
                    x += S(16) + S(8);
                }
                DrawText(g, text, fonts.Body, new Rectangle(x, r.Y, r.Right - x - S(8), r.Height), th.Text);
            }
            if (!string.IsNullOrEmpty(name) && text == null) h.Tip = name;
            return h;
        }

        /// <summary>Botón estándar de Windows 11 (fondo de control con borde) o de acento.</summary>
        protected Hit StandardButton(Graphics g, string key, Rectangle r, string glyph, string text, bool accent, bool enabled, Action click)
        {
            Hit h = null;
            if (enabled) h = AddHit(key, r, click, text);
            Color fill, fg;
            if (accent)
            {
                fill = !enabled ? th.AccentDisabled : IsPressed(key) ? th.AccentPressed : IsHover(key) ? th.AccentHover : th.Accent;
                fg = enabled ? th.OnAccent : th.Bg;
            }
            else
            {
                fill = th.ControlFill;
                fg = enabled ? th.Text : th.TextDisabled;
            }
            using (var p = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (!accent && enabled && IsHover(key))
                    using (var b = new SolidBrush(IsPressed(key) ? th.PressedOverlay : th.HoverOverlay)) g.FillPath(b, p);
                if (!accent) using (var pen = new Pen(ControlStroke)) g.DrawPath(pen, p);
            }
            int tw = MeasureText(text, fonts.Body) + (glyph != null ? S(24) : 0);
            int x = r.X + (r.Width - tw) / 2;
            if (glyph != null) { Glyph(g, glyph, fonts.Glyph16, new Rectangle(x, r.Y, S(16), r.Height), fg); x += S(24); }
            DrawText(g, text, fonts.Body, new Rectangle(x, r.Y, r.Right - x, r.Height), fg);
            return h;
        }

        /// <summary>Interruptor de Windows 11 (40 × 20) centrado verticalmente en <paramref name="r"/>, alineado a la derecha.</summary>
        protected Hit ToggleSwitch(Graphics g, string key, Rectangle r, bool on, string name, Action click)
        {
            var tr = new Rectangle(r.Right - S(40), r.Y + (r.Height - S(20)) / 2, S(40), S(20));
            var h = AddHit(key, Rectangle.Inflate(tr, S(6), S(8)), click, name);
            h.Role = AccessibleRole.CheckButton;
            h.Checked = on;
            bool hot = IsHover(key), down = IsPressed(key);
            var rf = new RectangleF(tr.X + .5f, tr.Y + .5f, tr.Width - 1, tr.Height - 1);
            using (var p = Theme.Round(rf, rf.Height / 2))
            {
                if (on)
                    using (var b = new SolidBrush(down ? th.AccentPressed : hot ? th.AccentHover : th.Accent)) g.FillPath(b, p);
                else
                {
                    if (hot) using (var b = new SolidBrush(th.HoverOverlay)) g.FillPath(b, p);
                    using (var pen = new Pen(th.ToggleOffStroke, Math.Max(1f, s))) g.DrawPath(pen, p);
                }
            }
            float kd = (hot ? 14 : 12) * s;
            float cx = on ? rf.Right - rf.Height / 2 : rf.X + rf.Height / 2, cy = rf.Y + rf.Height / 2;
            using (var b = new SolidBrush(on ? th.OnAccent : th.TextSecondary))
                g.FillEllipse(b, cx - kd / 2, cy - kd / 2, kd, kd);
            if (focusVisible && focusKey == key)
                using (var p = Theme.Round(RectangleF.Inflate(rf, 3 * s, 3 * s), rf.Height / 2 + 3 * s))
                using (var pen = new Pen(th.Text, 2 * s)) g.DrawPath(pen, p);
            return h;
        }

        /// <summary>Selector numérico: [ − ] valor [ + ].</summary>
        protected void Stepper(Graphics g, string key, Rectangle r, string value, string name, Action minus, Action plus)
        {
            using (var p = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), S(4)))
            {
                using (var b = new SolidBrush(th.ControlFill)) g.FillPath(b, p);
                using (var pen = new Pen(ControlStroke)) g.DrawPath(pen, p);
            }
            var mr = new Rectangle(r.X + S(2), r.Y + S(2), S(28), r.Height - S(4));
            var pr = new Rectangle(r.Right - S(30), r.Y + S(2), S(28), r.Height - S(4));
            AddHit(key + "-", mr, minus, "Bajar " + name);
            AddHit(key + "+", pr, plus, "Subir " + name);
            if (IsHover(key + "-")) Fill(g, mr, th.HoverOverlay, S(3));
            if (IsHover(key + "+")) Fill(g, pr, th.HoverOverlay, S(3));
            Glyph(g, "", fonts.Glyph12, mr, th.Text);   // menos
            Glyph(g, "", fonts.Glyph12, pr, th.Text);   // más
            DrawText(g, value, fonts.Body, new Rectangle(mr.Right, r.Y, pr.X - mr.Right, r.Height), th.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>
        /// Tarjeta de ajuste (SettingsCard de Windows 11): glifo, título, descripción y el control a la derecha.
        /// Devuelve el rectángulo disponible para el control.
        /// </summary>
        protected Rectangle SettingCard(Graphics g, Rectangle r, string glyph, string title, string desc, int controlWidth)
        {
            Card(g, r);
            int x = r.X + S(16);
            if (glyph != null)
            {
                Glyph(g, glyph, fonts.Glyph16, new Rectangle(x, r.Y, S(20), r.Height), th.Text);
                x += S(36);
            }
            int textW = r.Right - S(16) - controlWidth - S(16) - x;
            if (string.IsNullOrEmpty(desc))
                DrawText(g, title, fonts.Body, new Rectangle(x, r.Y, textW, r.Height), th.Text);
            else
            {
                DrawText(g, title, fonts.Body, new Rectangle(x, r.Y + r.Height / 2 - S(19), textW, S(20)), th.Text);
                DrawText(g, desc, fonts.Caption, new Rectangle(x, r.Y + r.Height / 2 + S(1), textW, S(18)), th.TextSecondary);
            }
            return new Rectangle(r.Right - S(16) - controlWidth, r.Y, controlWidth, r.Height);
        }

        // ------------------------------------------------------------------ mouse y teclado

        protected Hit HitAt(Point p)
        {
            p.Offset(-contentOffset.X, -contentOffset.Y);
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitAt(e.Location);
            string k = h != null ? h.Key : null;
            if (k != hoverKey) { hoverKey = k; Invalidate(); }
            if (h != null && h.Tip != null)
            {
                if (tipKey != h.Key) { tipKey = h.Key; tip.SetToolTip(this, h.Tip); }
            }
            else if (tipKey != null) { tipKey = null; tip.SetToolTip(this, null); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverKey != null) { hoverKey = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var h = HitAt(e.Location);
            if (e.Button == MouseButtons.Left && h != null) { pressKey = h.Key; focusVisible = false; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var h = HitAt(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                string p = pressKey;
                pressKey = null;
                Invalidate();
                if (h != null && h.Key == p && h.Click != null) { focusKey = h.Key; h.Click(); }
            }
            else if (e.Button == MouseButtons.Right && h != null && h.RightClick != null)
                h.RightClick(e.Location);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Tab: MoveFocus(1); return true;
                case Keys.Shift | Keys.Tab: MoveFocus(-1); return true;
                case Keys.Space:
                case Keys.Enter:
                    if (ActiveControl is TextBox) break;
                    var f = hits.FirstOrDefault(x => x.Key == focusKey);
                    if (f != null && f.Click != null) { focusVisible = true; f.Click(); return true; }
                    break;
                case Keys.Apps:
                case Keys.Shift | Keys.F10:
                    var c = hits.FirstOrDefault(x => x.Key == focusKey);
                    if (c != null && c.RightClick != null) { c.RightClick(new Point(c.R.X + c.R.Width / 2, c.R.Bottom)); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected void MoveFocus(int dir)
        {
            var list = hits.Where(h => h.Focusable && h.Click != null).ToList();
            if (list.Count == 0) return;
            int i = list.FindIndex(h => h.Key == focusKey);
            i = i < 0 ? (dir > 0 ? 0 : list.Count - 1) : (i + dir + list.Count) % list.Count;
            focusKey = list[i].Key;
            focusVisible = true;
            OnFocusMoved(list[i]);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, hits.IndexOf(list[i]));
        }

        protected virtual void OnFocusMoved(Hit h) { }

        // ------------------------------------------------------------------ accesibilidad

        protected override AccessibleObject CreateAccessibilityInstance() { return new SurfaceAccessible(this); }

        sealed class SurfaceAccessible : ControlAccessibleObject
        {
            readonly Surface f;
            public SurfaceAccessible(Surface f) : base(f) { this.f = f; }
            public override int GetChildCount() { return f.hits.Count; }
            public override AccessibleObject GetChild(int i) { return i >= 0 && i < f.hits.Count ? new HitAccessible(f, this, f.hits[i]) : null; }
        }

        sealed class HitAccessible : AccessibleObject
        {
            readonly Surface f; readonly AccessibleObject parent; readonly Hit h;
            public HitAccessible(Surface f, AccessibleObject parent, Hit h) { this.f = f; this.parent = parent; this.h = h; }
            public override string Name { get { return h.Name ?? h.Tip; } }
            public override AccessibleRole Role { get { return h.Role; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { var r = h.R; r.Offset(f.contentOffset); return f.RectangleToScreen(r); } }
            public override AccessibleStates State
            {
                get
                {
                    var st = AccessibleStates.Focusable;
                    if (h.Checked) st |= AccessibleStates.Checked;
                    if (f.focusKey == h.Key) st |= AccessibleStates.Focused;
                    return st;
                }
            }
            public override string DefaultAction { get { return "Presionar"; } }
            public override void DoDefaultAction() { if (h.Click != null) f.BeginInvoke(h.Click); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tip.Dispose(); if (fonts != null) fonts.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
