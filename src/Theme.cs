using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace SysMonitor
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
            if (t.Dark)
            {
                t.Bg = Hex(0x202020); t.Card = Hex(0x2B2B2B); t.CardStroke = Hex(0x1D1D1D);
                t.Divider = Hex(0x1D1D1D); t.Footer = Hex(0x1C1C1C); t.FooterStroke = Hex(0x2E2E2E);
                t.WindowStroke = Hex(0x3A3A3A);
                t.Text = Hex(0xFFFFFF); t.TextSecondary = Hex(0xCFCFCF); t.TextTertiary = Hex(0x9E9E9E);
                t.TextDisabled = Hex(0x6E6E6E);
                t.Accent = palette != null ? palette[1] : Hex(0x60CDFF);
                t.OnAccent = Hex(0x000000);
                t.Caution = Hex(0xFCE100); t.Success = Hex(0x6CCB5F); t.Critical = Hex(0xFF99A4);
                t.HoverOverlay = Color.FromArgb(15, 255, 255, 255);
                t.PressedOverlay = Color.FromArgb(8, 255, 255, 255);
                t.ToggleOffFill = Color.FromArgb(25, 0, 0, 0);
                t.ToggleOffStroke = Hex(0xCFCFCF);
                t.MenuBg = Hex(0x2C2C2C); t.MenuHover = Hex(0x383838);
            }
            else
            {
                t.Bg = Hex(0xF3F3F3); t.Card = Hex(0xFBFBFB); t.CardStroke = Hex(0xE5E5E5);
                t.Divider = Hex(0xEAEAEA); t.Footer = Hex(0xEEEEEE); t.FooterStroke = Hex(0xE0E0E0);
                t.WindowStroke = Hex(0xD6D6D6);
                t.Text = Hex(0x1A1A1A); t.TextSecondary = Hex(0x5C5C5C); t.TextTertiary = Hex(0x8A8A8A);
                t.TextDisabled = Hex(0xA6A6A6);
                t.Accent = palette != null ? palette[4] : Hex(0x005FB8);
                t.OnAccent = Hex(0xFFFFFF);
                t.Caution = Hex(0x9D5D00); t.Success = Hex(0x0F7B0F); t.Critical = Hex(0xC42B1C);
                t.HoverOverlay = Color.FromArgb(10, 0, 0, 0);
                t.PressedOverlay = Color.FromArgb(5, 0, 0, 0);
                t.ToggleOffFill = Color.FromArgb(6, 0, 0, 0);
                t.ToggleOffStroke = Hex(0x868686);
                t.MenuBg = Hex(0xF9F9F9); t.MenuHover = Hex(0xEDEDED);
            }
            t.AccentHover = Mix(t.Accent, t.Bg, 0.10f);
            t.AccentPressed = Mix(t.Accent, t.Bg, 0.20f);
            t.AccentDisabled = t.TextDisabled;

            t.Clear = t.Bg;
            t.Layer = t.Dark ? Hex(0x272727) : Hex(0xF9F9F9);
            t.LayerStroke = t.Dark ? Hex(0x1D1D1D) : Hex(0xE5E5E5);
            t.GraphBg = t.Dark ? Hex(0x1C1C1C) : Hex(0xFFFFFF);
            t.ControlFill = t.Dark ? Hex(0x2D2D2D) : Hex(0xFBFBFB);

            t.TransparencyEnabled = ForceGlass.HasValue ? ForceGlass.Value
                : ReadDword(@"SoftwareMicrosoftWindowsCurrentVersionThemesPersonalize", "EnableTransparency", 1) == 1;

            // Efectos de transparencia de Windows (Configuración > Personalización > Colores).
            // Mica y Acrílico existen desde Windows 11 22H2 (compilación 22621).
            t.Glass = ForceGlass.HasValue ? ForceGlass.Value
                : ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 1
                  && Environment.OSVersion.Version.Build >= 22621 && !OverlappedContentDisabled();
            if (t.Glass)
            {
                // Tokens de WinUI para superficies sobre Mica/Acrílico.
                t.Clear = Color.Transparent;
                if (t.Dark)
                {
                    t.Card = Color.FromArgb(13, 255, 255, 255);
                    t.CardStroke = Color.FromArgb(26, 0, 0, 0);
                    t.Footer = Color.FromArgb(40, 0, 0, 0);
                    t.FooterStroke = Color.FromArgb(20, 255, 255, 255);
                    t.Layer = Color.FromArgb(76, 58, 58, 58);
                    t.LayerStroke = Color.FromArgb(26, 0, 0, 0);
                    t.GraphBg = Color.FromArgb(70, 0, 0, 0);
                    t.ControlFill = Color.FromArgb(15, 255, 255, 255);
                }
                else
                {
                    t.Card = Color.FromArgb(179, 255, 255, 255);
                    t.CardStroke = Color.FromArgb(15, 0, 0, 0);
                    t.Footer = Color.FromArgb(10, 0, 0, 0);
                    t.FooterStroke = Color.FromArgb(15, 0, 0, 0);
                    t.Layer = Color.FromArgb(128, 255, 255, 255);
                    t.LayerStroke = Color.FromArgb(15, 0, 0, 0);
                    t.GraphBg = Color.FromArgb(150, 255, 255, 255);
                    t.ControlFill = Color.FromArgb(179, 255, 255, 255);
                }
            }
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
        public const string Firewall = "";
    }

    /// <summary>Rampa tipográfica de Windows 11, escalada al DPI del monitor.</summary>
    internal sealed class Fonts : IDisposable
    {
        public Font Caption, CaptionStrong, Body, BodyStrong, Subtitle, Title, Glyph12, Glyph16, Glyph20;

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
        }
    }
}
