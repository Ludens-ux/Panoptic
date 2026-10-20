using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>
    /// Efecto "vibrancy" al estilo macOS: el fondo de pantalla que queda detrás de la ventana, desenfocado y
    /// con la saturación aumentada, y colores de primer plano que toman el tono de ese fondo.
    /// </summary>
    internal sealed class Vibrancy : IDisposable
    {
        const int Downscale = 24;          // cuanto más chico el mapa intermedio, mayor el desenfoque
        const float Saturation = 1.8f;

        Bitmap small;                      // fondo del monitor reducido, desenfocado y saturado
        Rectangle monitor;
        string key;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);

        // Cachés: la comprobación del fondo de pantalla (registro y archivo) se hace como mucho cada 5 s,
        // y el recorte ampliado y el color promedio se reutilizan mientras la ventana no se mueva.
        DateTime lastCheck = DateTime.MinValue;
        bool lastDark;
        Bitmap drawn;
        Rectangle drawnScreen;
        string drawnKey;
        Color avg;
        Rectangle avgScreen;
        string avgKey;

        /// <summary>Prepara el fondo del monitor que contiene <paramref name="screenRect"/>. False si no hay imagen.</summary>
        public bool Ensure(Rectangle screenRect, bool dark)
        {
            var center = new Point(screenRect.X + screenRect.Width / 2, screenRect.Y + screenRect.Height / 2);
            if (small != null && dark == lastDark && monitor.Contains(center) && (DateTime.UtcNow - lastCheck).TotalSeconds < 5)
                return true;
            lastCheck = DateTime.UtcNow;
            lastDark = dark;

            var sc = Screen.FromRectangle(screenRect);
            string path = WallpaperPath();
            int style, tile;
            ReadStyle(out style, out tile);
            Rectangle area = style == 22 ? SystemInformation.VirtualScreen : sc.Bounds; // 22 = Expandir
            string k = path + "|" + (path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0) + "|" + area + "|" + style + "|" + tile + "|" + dark;
            if (k == key && small != null) return true;
            key = k;
            if (small != null) { small.Dispose(); small = null; }
            monitor = area;

            Image img = null;
            try
            {
                if (path != null && File.Exists(path))
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var tmp = Image.FromStream(fs))
                        img = new Bitmap(tmp);
            }
            catch { img = null; }

            int w = Math.Max(1, area.Width / Downscale), h = Math.Max(1, area.Height / Downscale);
            small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.Clear(DesktopColor());
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(SaturationMatrix(Saturation));
                        ia.SetWrapMode(WrapMode.TileFlipXY); // sin bordes oscuros al reducir
                        float f = 1f / Downscale;
                        if (style == 0 && tile == 1)
                        {
                            for (int y = 0; y < area.Height; y += img.Height)
                                for (int x = 0; x < area.Width; x += img.Width)
                                    g.DrawImage(img, Scale(new Rectangle(x, y, img.Width, img.Height), f), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                        }
                        else
                            g.DrawImage(img, Scale(Placement(style, img.Size, area.Size), f), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                    }
                    img.Dispose();
                }
                // Tinte de luminosidad, como el material "vibrant" de macOS.
                using (var b = new SolidBrush(dark ? Color.FromArgb(95, 18, 18, 18) : Color.FromArgb(110, 250, 250, 250)))
                    g.FillRectangle(b, 0, 0, w, h);
            }
            return true;
        }

        /// <summary>Recorte del fondo detrás de <paramref name="screenRect"/>, ampliado (el aumento produce el desenfoque).</summary>
        public void Draw(Graphics g, Rectangle screenRect, Rectangle dest)
        {
            var img = Region(screenRect, dest.Size);
            if (img == null) return;
            var state = g.Save();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(img, dest.X, dest.Y);
            g.Restore(state);
        }

        /// <summary>
        /// La imagen del fondo detrás de <paramref name="screenRect"/> ya ampliada al tamaño pedido. Es la misma
        /// instancia mientras no cambie (sirve para guardarla en la placa de video).
        /// </summary>
        public Bitmap Region(Rectangle screenRect, Size size)
        {
            if (small == null) return null;
            var dest = new Rectangle(Point.Empty, size);
            if (drawn == null || drawn.Width != dest.Width || drawn.Height != dest.Height || drawnScreen != screenRect || drawnKey != key)
            {
                if (drawn != null) drawn.Dispose();
                drawn = new Bitmap(Math.Max(1, dest.Width), Math.Max(1, dest.Height), PixelFormat.Format32bppPArgb);
                drawnScreen = screenRect;
                drawnKey = key;
                var src = SourceRect(screenRect);
                using (var dg = Graphics.FromImage(drawn))
                using (var ia = new ImageAttributes())
                {
                    dg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    dg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    dg.CompositingMode = CompositingMode.SourceCopy;
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    dg.DrawImage(small, new Rectangle(0, 0, drawn.Width, drawn.Height), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return drawn;
        }

        /// <summary>Color promedio del fondo detrás de una zona de la pantalla.</summary>
        public Color Average(Rectangle screenRect)
        {
            if (small == null) return Color.Gray;
            if (avgScreen == screenRect && avgKey == key) return avg;
            avgScreen = screenRect;
            avgKey = key;
            var src = Rectangle.Round(SourceRect(screenRect));
            src.Intersect(new Rectangle(0, 0, small.Width, small.Height));
            if (src.Width <= 0 || src.Height <= 0) return avg = Color.Gray;
            long r = 0, gg = 0, b = 0, n = 0;
            for (int y = src.Top; y < src.Bottom; y++)
                for (int x = src.Left; x < src.Right; x++)
                {
                    var c = small.GetPixel(x, y);
                    r += c.R; gg += c.G; b += c.B; n++;
                }
            return avg = n == 0 ? Color.Gray : Color.FromArgb((int)(r / n), (int)(gg / n), (int)(b / n));
        }

        RectangleF SourceRect(Rectangle screenRect)
        {
            return new RectangleF((screenRect.X - monitor.X) / (float)Downscale, (screenRect.Y - monitor.Y) / (float)Downscale,
                screenRect.Width / (float)Downscale, screenRect.Height / (float)Downscale);
        }

        /// <summary>
        /// Color "vibrante": conserva el tono del fondo y ajusta la luminosidad para que se lea
        /// (claro sobre fondos oscuros, oscuro sobre claros), con más saturación.
        /// </summary>
        public static Color Vibrant(Color bg, bool dark, float lightness, int alpha)
        {
            double h, s, l;
            ToHsl(bg, out h, out s, out l);
            s = Math.Min(1, s * 1.6 + 0.08);
            l = dark ? lightness : 1 - lightness;
            return FromHsl(h, s, l, alpha);
        }

        // ------------------------------------------------------------------ fondo de pantalla

        static string WallpaperPath()
        {
            var sb = new StringBuilder(520);
            try { if (SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0) && sb.Length > 0 && File.Exists(sb.ToString())) return sb.ToString(); } catch { }
            string t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            return File.Exists(t) ? t : null;
        }

        static void ReadStyle(out int style, out int tile)
        {
            style = 10; tile = 0;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    int.TryParse(Convert.ToString(k.GetValue("WallpaperStyle", "10")), out style);
                    int.TryParse(Convert.ToString(k.GetValue("TileWallpaper", "0")), out tile);
                }
            }
            catch { }
        }

        static Color DesktopColor()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                {
                    var p = Convert.ToString(k.GetValue("Background", "0 0 0")).Split(' ');
                    return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
                }
            }
            catch { return Color.Black; }
        }

        /// <summary>Dónde queda la imagen dentro del monitor según el ajuste del fondo.</summary>
        static Rectangle Placement(int style, Size img, Size area)
        {
            double sx = area.Width / (double)img.Width, sy = area.Height / (double)img.Height;
            switch (style)
            {
                case 2: return new Rectangle(0, 0, area.Width, area.Height);                 // Expandir (estirar)
                case 6: return Fit(img, area, Math.Min(sx, sy));                            // Ajustar
                case 0: return new Rectangle((area.Width - img.Width) / 2, (area.Height - img.Height) / 2, img.Width, img.Height); // Centrar
                default: return Fit(img, area, Math.Max(sx, sy));                           // Rellenar (10) y Expandir a varias pantallas (22)
            }
        }

        static Rectangle Fit(Size img, Size area, double scale)
        {
            int w = (int)Math.Round(img.Width * scale), h = (int)Math.Round(img.Height * scale);
            return new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
        }

        static Rectangle Scale(Rectangle r, float f)
        {
            return Rectangle.FromLTRB((int)Math.Floor(r.Left * f), (int)Math.Floor(r.Top * f), (int)Math.Ceiling(r.Right * f), (int)Math.Ceiling(r.Bottom * f));
        }

        static ColorMatrix SaturationMatrix(float s)
        {
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float sr = (1 - s) * lr, sg = (1 - s) * lg, sb = (1 - s) * lb;
            return new ColorMatrix(new[]
            {
                new[] { sr + s, sr, sr, 0f, 0f },
                new[] { sg, sg + s, sg, 0f, 0f },
                new[] { sb, sb, sb + s, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
        }

        // ------------------------------------------------------------------ HSL

        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            if (max == min) { h = s = 0; return; }
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }

        static Color FromHsl(double h, double s, double l, int a)
        {
            double r, g, b;
            if (s == 0) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
            }
            return Color.FromArgb(a, (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
        }

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        public void Invalidate() { key = null; lastCheck = DateTime.MinValue; }

        public void Dispose() { if (small != null) small.Dispose(); if (drawn != null) drawn.Dispose(); }
    }
}
