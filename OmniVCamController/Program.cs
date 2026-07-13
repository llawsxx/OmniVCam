using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace OmniVCamController
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string cultureName = ControllerLanguage.LoadSavedCultureName();
            if (!string.IsNullOrWhiteSpace(cultureName))
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
