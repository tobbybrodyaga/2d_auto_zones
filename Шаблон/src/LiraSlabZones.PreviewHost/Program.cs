using System;
using System.Windows;
using LiraSlabZones.Revit2023.UI;

namespace LiraSlabZones.PreviewHost
{
    public partial class App : Application
    {
        [STAThread]
        public static void Main()
        {
            // Быстрый старт: без демо при запуске (демо — по кнопке)
            var app = new App();
            var win = new ZonePreviewWindow();
            app.Run(win);
        }
    }
}
