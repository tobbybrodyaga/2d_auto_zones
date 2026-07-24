using System;
using System.Collections.Generic;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Демо-плита для предварительного UI (без запуска ЛИРА), по логике SmartRebar:
    /// сетка КЭ + изополя As + зоны доп.армирования у опор.
    /// </summary>
    public static class DemoSlabFactory
    {
        public static AnalysisResult Create(AnalysisSettings? settings = null)
        {
            settings ??= new AnalysisSettings
            {
                ShowAs1 = true,
                ShowAs2 = true,
                ShowAs3 = false,
                ShowAs4 = false,
                VisualizationScale = 1,
                SlabSelected = true,
                BgBottomDiameterMm = 12,
                BgBottomStepMm = 200,
                BgTopDiameterMm = 12,
                BgTopStepMm = 200,
                ConcreteClass = "B25"
            };
            settings.SlabSelected = true;
            settings.SyncBackgroundAsFromBars();
            if (string.IsNullOrWhiteSpace(settings.ConcreteClass))
                settings.ConcreteClass = "B25";
            if (settings.BgBottomDiameterMm <= 0)
            {
                settings.BgBottomDiameterMm = 12;
                settings.BgBottomStepMm = 200;
                settings.BgTopDiameterMm = 12;
                settings.BgTopStepMm = 200;
                settings.SyncBackgroundAsFromBars();
            }

            const int nx = 12;
            const int ny = 8;
            const double dx = 1.0;
            const double dy = 1.0;
            const double z = 3.0;

            var nodes = new Dictionary<int, LiraNode>();
            int nid = 1;
            for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                nodes[nid] = new LiraNode
                {
                    Id = nid,
                    Coord = new Point3(i * dx, j * dy, z)
                };
                nid++;
            }

            int NodeAt(int i, int j) => j * (nx + 1) + i + 1;

            var plates = new List<LiraPlateElement>();
            int eid = 1;
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var n1 = NodeAt(i, j);
                var n2 = NodeAt(i + 1, j);
                var n3 = NodeAt(i + 1, j + 1);
                var n4 = NodeAt(i, j + 1);
                var contour = new List<Point3>
                {
                    nodes[n1].Coord,
                    nodes[n2].Coord,
                    nodes[n3].Coord,
                    nodes[n4].Coord
                };

                double cx = (i + 0.5) * dx;
                double cy = (j + 0.5) * dy;

                // Пики у колонн (углы и сетка 4 м) — как «огибающие» у опор
                double as1 = BackgroundAs(cx, cy, alongX: true);
                double as2 = BackgroundAs(cx, cy, alongX: false);
                double as3 = as1 * 0.35;
                double as4 = as2 * 0.35;

                plates.Add(new LiraPlateElement
                {
                    Id = eid++,
                    TypeCode = 42,
                    NodeIds = new List<int> { n1, n2, n3, n4 },
                    Contour = contour,
                    Centroid = new Point3(cx, cy, z),
                    WidthM = dx,
                    LengthM = dy,
                    Rebar = new PlateReinforcement
                    {
                        As1 = as1,
                        As2 = as2,
                        As3 = as3,
                        As4 = as4,
                        Ok = true
                    }
                });
            }

            var axes = new List<ConstructionAxis>
            {
                new ConstructionAxis { Name = "А", Vertical = false, Position = 0 },
                new ConstructionAxis { Name = "Б", Vertical = false, Position = 4 },
                new ConstructionAxis { Name = "В", Vertical = false, Position = 8 },
                new ConstructionAxis { Name = "1", Vertical = true, Position = 0 },
                new ConstructionAxis { Name = "2", Vertical = true, Position = 4 },
                new ConstructionAxis { Name = "3", Vertical = true, Position = 8 },
                new ConstructionAxis { Name = "4", Vertical = true, Position = 12 },
            };

            return SlabZoneAnalyzer.BuildResult(
                "DEMO_плита_12x8",
                "(demo)",
                nodes.Count,
                plates,
                settings,
                axes,
                z,
                $"Этаж +{z:F3} (Z={z:F3} м)",
                skipLevelFilter: true);
        }

        private static double BackgroundAs(double x, double y, bool alongX)
        {
            // фон ~4.5 + пики у опор
            double peak = 0;
            for (int px = 0; px <= 12; px += 4)
            for (int py = 0; py <= 8; py += 4)
            {
                double d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                peak = Math.Max(peak, Math.Max(0, 14.0 - d * 5.5));
            }

            // полосовые зоны вдоль пролёта
            double strip = alongX
                ? (y < 1.2 || y > 6.8 ? 3.5 : 0)
                : (x < 1.2 || x > 10.8 ? 3.5 : 0);

            return 4.5 + peak + strip;
        }
    }
}
