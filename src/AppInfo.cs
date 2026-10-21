using System.Reflection;

[assembly: AssemblyTitle("Panoptic")]
[assembly: AssemblyDescription("Rendimiento en vivo, procesos y registro de actividad de la PC. Licencia PolyForm Noncommercial 1.0.0.")]
[assembly: AssemblyProduct("Panoptic")]
[assembly: AssemblyCompany("@villargonza")]
[assembly: AssemblyCopyright("© 2026 @villargonza")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: AssemblyInformationalVersion("1.6.0")]

namespace Panoptic
{
    internal static class AppInfo
    {
        public const string Name = "Panoptic";
        public const string Version = "1.6.0";
        public const string Author = "@villargonza";
        public const string Repo = "https://github.com/Ludens-ux/Panoptic";
        public const string Description = "Rendimiento en vivo, procesos y registro de actividad de la PC.";
        public const string LicenseName = "PolyForm Noncommercial 1.0.0";
        public const string LicenseSummary = "Uso gratuito, personal y no comercial; no se puede vender ni usar con fines comerciales.";
        public const string PrivacySummary = "No envía datos a ningún servidor. Todo lo que registra queda en tu PC (%LOCALAPPDATA%\\" + Name + ").";
        public const string LicenseUrl = Repo + "/blob/main/LICENSE.md";
        public const string PrivacyUrl = Repo + "/blob/main/PRIVACY.md";

        public static void ShowAbout(System.Windows.Forms.IWin32Window owner)
        {
            string text = Name + "\nVersión " + Version + "\n© 2026 " + Author + "\n\n" + Description +
                "\n\nLicencia: " + LicenseName + ". " + LicenseSummary + "\n" + LicenseUrl +
                "\n\nPrivacidad: " + PrivacySummary + "\n" + PrivacyUrl;
            System.Windows.Forms.MessageBox.Show(owner, text, "Acerca de " + Name,
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
        }

        public static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch { }
        }
    }
}
