using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Panoptic
{
    /// <summary>Colores de Windows 11 (tokens de WinUI) para el modo del sistema actual.</summary>
    internal sealed class Theme
    {
        public bool Dark;
        public bool TaskbarLight;
        public Color Bg, Card, CardStroke, Divider, Footer, FooterStroke, WindowStroke;
        public Color Text, TextSecondary, TextTertiary, TextDisabled;
        public Color Accent, AccentHover, AccentPressed, AccentDisabled, OnAccent;
        public Color Caution, Success, Critical;
        public Color HoverOverlay, PressedOverlay, ToggleOffFill, ToggleOffStroke, MenuBg, MenuHover;

        /// <summary>
        /// True cuando Windows tiene activados los efectos de transparencia: las ventanas usan Mica o
        /// Acrílico y las superficies se pintan semitransparentes encima.
        /// </summary>
        public bool Glass;
        /// <summary>El interruptor "Efectos de transparencia" de Configuración (sin mirar otras opciones del sistema).</summary>
        public bool TransparencyEnabled;
        /// <summary>Color con el que se limpia la ventana (transparente con Mica/Acrílico).</summary>
        public Color Clear;
        /// <summary>Capa de contenido de la ventana principal y fondo de los gráficos.</summary>
        public Color Layer, LayerStroke, GraphBg, ControlFill;

        /// <summary>Solo para las capturas de vista previa.</summary>
        public static bool? ForceDark, ForceGlass;

        public static Theme Load()
        {
            var t = new Theme();
            t.Dark = ForceDark.HasValue ? ForceDark.Value
                : ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 0;
            t.TaskbarLight = !t.Dark;

            Color[] palette = ReadAccentPalette();
            bool sysAccent = Settings.UseWindowsAccent && palette != null;
            // Lenguaje "isla": superficies casi negras, tarjetas que se separan por un borde fino y un velo mínimo,
            // y el violeta del iris de la mascota como acento (o el color de Windows, si se elige en Configuración).
            Color ink = t.Dark ? Color.White : Color.Black;
            Func<double, Color> veil = a => Color.FromArgb((int)Math.Round(255 * a), ink);
            if (t.Dark)
            {
                t.Bg = Hex(0x0A0A0A);
                t.Text = Hex(0xFFFFFF);
                t.Accent = sysAccent ? palette[1] : Hex(0xA98BF0);
                t.OnAccent = Hex(0x000000);
                t.Caution = Hex(0xFCE100); t.Success = Hex(0x6CCB5F); t.Critical = Hex(0xFF8A80);
                t.MenuBg = Hex(0x141414);
            }
            else
            {
                t.Bg = Hex(0xF3F3F3);
                t.Text = Hex(0x1A1A1A);
                t.Accent = sysAccent ? palette[4] : Hex(0x7A57D1);
                t.OnAccent = Hex(0xFFFFFF);
                t.Caution = Hex(0x9D5D00); t.Success = Hex(0x0F7B0F); t.Critical = Hex(0xC42B1C);
                t.MenuBg = Hex(0xFAFAFA);
            }
            // Textos secundarios opacos (el texto de GDI ignora el canal alfa): blanco o negro al 60, 50 y 35 % sobre el fondo.
            t.TextSecondary = Mix(t.Bg, ink, 0.60f);
            t.TextTertiary = Mix(t.Bg, ink, 0.50f);
            t.TextDisabled = Mix(t.Bg, ink, 0.35f);
            t.Card = veil(0.035);
            t.CardStroke = veil(0.06);
            t.Divider = t.Footer = t.FooterStroke = veil(0.07);
            t.WindowStroke = veil(0.09);
            t.ControlFill = veil(0.05);
            t.HoverOverlay = veil(0.07);
            t.PressedOverlay = veil(0.04);
            t.ToggleOffFill = Color.Transparent;
            t.ToggleOffStroke = Mix(t.Bg, ink, 0.55f);
            t.MenuHover = Mix(t.MenuBg, ink, 0.07f);
            t.GraphBg = t.Dark ? Color.FromArgb(115, 0, 0, 0) : Color.FromArgb(10, 0, 0, 0);
            t.AccentHover = Mix(t.Accent, ink, 0.12f);
            t.AccentPressed = Mix(t.Accent, t.Bg, 0.20f);
            t.AccentDisabled = t.TextDisabled;

            t.TransparencyEnabled = ForceGlass.HasValue ? ForceGlass.Value
                : ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 1;

            // Efectos de transparencia de Windows (Configuración > Personalización > Colores).
            // Mica y Acrílico existen desde Windows 11 22H2 (compilación 22621).
            t.Glass = ForceGlass.HasValue ? ForceGlass.Value
                : ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 1
                  && Environment.OSVersion.Version.Build >= 22621 && !OverlappedContentDisabled();
            // Con transparencia, el fondo es un tinte al 92 % sobre el material del sistema; sin ella, sólido.
            t.Clear = t.Glass ? Color.FromArgb(235, t.Bg) : t.Bg;
            t.Layer = t.Clear;
            t.LayerStroke = Color.Transparent;
            return t;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref bool value, int winIni);

        /// <summary>
        /// Accesibilidad > "Quitar imágenes de fondo (donde estén disponibles)": Windows deja de dibujar
        /// transparencias y desenfoques en todo el sistema, aunque los efectos de transparencia estén activados.
        /// </summary>
        static bool OverlappedContentDisabled()
        {
            bool v = false;
            try { SystemParametersInfo(0x1048 /* SPI_GETDISABLEOVERLAPPEDCONTENT */, 0, ref v, 0); } catch { }
            return v;
        }

        static int ReadDword(string key, string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }

        /// <summary>AccentPalette: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, (reservado).</summary>
        static Color[] ReadAccentPalette()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (k == null) return null;
                    var b = k.GetValue("AccentPalette") as byte[];
                    if (b == null || b.Length < 28) return null;
                    var c = new Color[7];
                    for (int i = 0; i < 7; i++) c[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                    return c;
                }
            }
            catch { return null; }
        }

        /// <summary>Borde de ventana opaco (DWM no admite transparencia): blanco o negro al 9 % sobre el fondo.</summary>
        public Color WindowBorder { get { return Mix(Bg, Dark ? Color.White : Color.Black, 0.09f); } }

        public static Color Hex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            return Round(r, radius, true, true);
        }

        public static GraphicsPath Round(RectangleF r, float radius, bool top, bool bottom)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            if (top) p.AddArc(r.X, r.Y, d, d, 180, 90); else p.AddLine(r.X, r.Y, r.X, r.Y);
            if (top) p.AddArc(r.Right - d, r.Y, d, d, 270, 90); else p.AddLine(r.Right, r.Y, r.Right, r.Y);
            if (bottom) p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); else p.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (bottom) p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Glifos de Segoe Fluent Icons (con respaldo en Segoe MDL2 Assets).</summary>
    internal static class Glyphs
    {
        public const string Cpu = "";
        public const string Memory = "";
        public const string Performance = "";
        public const string Processes = "";
        public const string Log = "";
        public const string HardDrive = "";
        public const string Ethernet = "";
        public const string Wifi = "";
        public const string Pin = "";
        public const string Unpin = "";
        public const string Menu = "";
        public const string Search = "";
        public const string Cancel = "";
        public const string Warning = "";
        public const string Power = "";
        public const string Battery = "";
        public const string Lock = "";
        public const string Usb = "";
        public const string Error = "";
        public const string Info = "";
        public const string ChevronUp = "";
        public const string ChevronDown = "";
        public const string Settings = "";
        public const string OpenExternal = "";
        public const string Widget = "";
        public const string Filter = "";
        public const string Startup = "";
        public const string Folder = "";
        public const string CheckMark = "";
        public const string ArrowDown = "";
        public const string ArrowUp = "";
        public const string App = "";
        public const string More = "";
        public const string Copy = "";
        public const string History = "";
        public const string Globe = "";
        public const string Services = "";
        public const string Gpu = "";
        public const string Temperature = "";
        public const string Export = "";
        public const string Play = "";
        public const string Stop = "";
        public const string Restart = "";
        public const string Shield = "";
        public const string Camera = "\uE722", Microphone = "\uE720", Volume = "\uE767", Mute = "\uE74F", Brightness = "\uE706", Photo = "\uE91B", Vpn = "\uE705";
        public const string Firewall = "";
    }

    /// <summary>Rampa tipográfica de Windows 11, escalada al DPI del monitor.</summary>
    internal sealed class Fonts : IDisposable
    {
        public Font Caption, CaptionStrong, Body, BodyStrong, Subtitle, Title, Glyph12, Glyph16, Glyph20;
        /// <summary>Rampa del lenguaje "isla": notas de 11 px, cuerpo de 13 px y números Display de 16, 18 y 22 px.</summary>
        public Font Small, Body13, Body13Strong, Display16, Display18, Display22;

        static string textFamily, strongFamily, displayFamily, iconFamily;

        public Fonts(float scale)
        {
            if (textFamily == null)
            {
                textFamily = Pick("Segoe UI Variable Text", "Segoe UI");
                strongFamily = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
                displayFamily = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
                iconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
            }
            Caption = new Font(textFamily, 12f * scale, GraphicsUnit.Pixel);
            CaptionStrong = new Font(strongFamily, 12f * scale, GraphicsUnit.Pixel);
            Body = new Font(textFamily, 14f * scale, GraphicsUnit.Pixel);
            BodyStrong = new Font(strongFamily, 14f * scale, GraphicsUnit.Pixel);
            Subtitle = new Font(displayFamily, 20f * scale, GraphicsUnit.Pixel);
            Title = new Font(displayFamily, 28f * scale, GraphicsUnit.Pixel);
            Glyph12 = new Font(iconFamily, 12f * scale, GraphicsUnit.Pixel);
            Glyph16 = new Font(iconFamily, 16f * scale, GraphicsUnit.Pixel);
            Glyph20 = new Font(iconFamily, 20f * scale, GraphicsUnit.Pixel);
            Small = new Font(textFamily, 11f * scale, GraphicsUnit.Pixel);
            Body13 = new Font(textFamily, 13f * scale, GraphicsUnit.Pixel);
            Body13Strong = new Font(strongFamily, 13f * scale, GraphicsUnit.Pixel);
            Display16 = new Font(displayFamily, 16f * scale, GraphicsUnit.Pixel);
            Display18 = new Font(displayFamily, 18f * scale, GraphicsUnit.Pixel);
            Display22 = new Font(displayFamily, 22f * scale, GraphicsUnit.Pixel);
        }

        public static string IconFamily
        {
            get { if (iconFamily == null) iconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets"); return iconFamily; }
        }

        public static string TextFamily
        {
            get { if (textFamily == null) textFamily = Pick("Segoe UI Variable Text", "Segoe UI"); return textFamily; }
        }

        static string Pick(string preferred, string fallback)
        {
            try { using (new FontFamily(preferred)) return preferred; }
            catch (ArgumentException) { return fallback; }
        }

        public void Dispose()
        {
            Caption.Dispose(); CaptionStrong.Dispose(); Body.Dispose(); BodyStrong.Dispose();
            Subtitle.Dispose(); Title.Dispose();
            Glyph12.Dispose(); Glyph16.Dispose(); Glyph20.Dispose();
            Small.Dispose(); Body13.Dispose(); Body13Strong.Dispose(); Display16.Dispose(); Display18.Dispose(); Display22.Dispose();
        }
    }
}
