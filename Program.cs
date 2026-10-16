using System;
using System.Windows.Forms;
using YaMus.Core.Diagnostics;
using YaMus.UI;

namespace YaMus
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // процесс должен явно разрешить тёмный режим до создания окон —
            // от этого зависят скроллбары (Win11) и DWM-атрибутика
            NativeTheme.PreferDark();

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Diag.LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) =>
                Diag.LogException("Application.ThreadException", e.Exception);

            Diag.StartSession();

            Application.Run(new MainForm());
        }
    }
}