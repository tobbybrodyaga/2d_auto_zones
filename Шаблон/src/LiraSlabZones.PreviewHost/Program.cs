using System;
using System.IO;
using System.Windows;
using LiraSlabZones.Core;
using LiraSlabZones.Revit2023.UI;

namespace LiraSlabZones.PreviewHost
{
    public partial class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Быстрый старт: без демо при запуске (демо — по кнопке)
            var app = new App();
            var win = new ZonePreviewWindow();
            if (args.Length > 0 && File.Exists(args[0]))
            {
                win.Loaded += (_, __) =>
                {
                    try { win.LoadResult(SlabZoneAnalyzer.LoadJson(args[0])); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Не удалось открыть предпросмотр JSON",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
            }
            app.Run(win);
        }
    }
}
