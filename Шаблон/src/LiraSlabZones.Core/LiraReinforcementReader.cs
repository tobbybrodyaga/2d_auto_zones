using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LiraResAPI;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Быстрое чтение As плит: 1–2 запроса к API, без перебора имён и без чтения по одному КЭ.
    /// </summary>
    public sealed class LiraReinforcementReader : IDisposable
    {
        private readonly LiraResultsAccess _results = new LiraResultsAccess();

        // Кэш успешного имени задачи между запусками в одной сессии Revit/Exporter
        private static string? _cachedDocName;
        private static int _cachedDesignOption = 1;

        public string LastDiagnostics { get; private set; } = string.Empty;
        public bool LastSucceeded { get; private set; }

        public void FillPlateReinforcement(
            string documentName,
            IList<LiraPlateElement> plates,
            int designOption = 1)
        {
            var sw = Stopwatch.StartNew();
            LastSucceeded = false;

            if (plates.Count == 0)
            {
                LastDiagnostics = "Нет пластин.";
                return;
            }

            foreach (var p in plates)
                p.Rebar = new PlateReinforcement();

            var candidates = BuildFastCandidates(documentName);
            var options = BuildFastOptions(designOption);

            Exception? lastEx = null;

            // Сначала кэш прошлой успешной сессии
            if (!string.IsNullOrEmpty(_cachedDocName))
            {
                candidates.RemoveAll(c => string.Equals(c, _cachedDocName, StringComparison.OrdinalIgnoreCase));
                candidates.Insert(0, _cachedDocName!);
                options = new[] { _cachedDesignOption }.Concat(options.Where(o => o != _cachedDesignOption)).ToArray();
            }

            foreach (var doc in candidates)
            {
                foreach (var dopt in options)
                {
                    try
                    {
                        int ok = FillAllInOneOrFewBatches(doc, plates, dopt);
                        if (ok > 0)
                        {
                            _cachedDocName = doc;
                            _cachedDesignOption = dopt;
                            LastSucceeded = true;
                            LastDiagnostics =
                                $"OK As: doc='{doc}', opt={dopt}, plates={plates.Count}, withAs={ok}, {sw.ElapsedMilliseconds} ms";
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        // сброс частичных результатов перед следующей попыткой
                        foreach (var p in plates)
                            p.Rebar = new PlateReinforcement();
                    }
                }
            }

            LastSucceeded = false;
            LastDiagnostics =
                $"As пропущен ({sw.ElapsedMilliseconds} ms). " +
                (lastEx != null ? lastEx.Message : "нет данных") +
                ". Геометрия загружена.";
        }

        private int FillAllInOneOrFewBatches(string documentName, IList<LiraPlateElement> plates, int designOption)
        {
            // Один крупный запрос; при лимите API — куски по 800
            const int maxBatch = 800;
            int ok = 0;

            for (int offset = 0; offset < plates.Count; offset += maxBatch)
            {
                var batch = plates.Skip(offset).Take(maxBatch).ToList();
                ok += FillBatchFast(documentName, batch, designOption);
            }

            return ok;
        }

        private int FillBatchFast(string documentName, List<LiraPlateElement> batch, int designOption)
        {
            var request = (LiraSelectedReinforcementRequest)_results.CreateNewRequest(
                LiraRequestEnum.kLiraRequest_SelectedReinforcement);

            request.DocumentName = documentName;
            request.DesignOption = designOption;
            request.SuperElement = 0;
            request.Elements.AddFromString(string.Join(",", batch.Select(p => p.Id)));

            request.ReinforcementData.Count = 1;
            request.ReinforcementData.set_Item(0, (int)LiraReinforcementDataEnum.kLiraReinforcementData_Total);

            request.ReinforcementInPlates.Count = 4;
            request.ReinforcementInPlates.set_Item(0, (int)LiraReinforcementInPlatesEnum.kLiraReinforcementInPlates_AS1);
            request.ReinforcementInPlates.set_Item(1, (int)LiraReinforcementInPlatesEnum.kLiraReinforcementInPlates_AS2);
            request.ReinforcementInPlates.set_Item(2, (int)LiraReinforcementInPlatesEnum.kLiraReinforcementInPlates_AS3);
            request.ReinforcementInPlates.set_Item(3, (int)LiraReinforcementInPlatesEnum.kLiraReinforcementInPlates_AS4);

            var response = _results.SelectedReinforcement(request);

            int ok = 0;
            const int stage = 0;
            var rd = LiraReinforcementDataEnum.kLiraReinforcementData_Total;

            foreach (var plate in batch)
            {
                try
                {
                    int e1 = 0, e2 = 0, e3 = 0, e4 = 0;
                    float as1 = response.GetPlateAS1(plate.Id, rd, stage, out e1);
                    float as2 = response.GetPlateAS2(plate.Id, rd, stage, out e2);
                    float as3 = response.GetPlateAS3(plate.Id, rd, stage, out e3);
                    float as4 = response.GetPlateAS4(plate.Id, rd, stage, out e4);

                    if (e1 != 0 && e2 != 0 && e3 != 0 && e4 != 0)
                        continue;

                    plate.Rebar = new PlateReinforcement
                    {
                        As1 = as1 < 0 ? 0 : as1,
                        As2 = as2 < 0 ? 0 : as2,
                        As3 = as3 < 0 ? 0 : as3,
                        As4 = as4 < 0 ? 0 : as4,
                        Ok = true
                    };
                    ok++;
                }
                catch (ArgumentException)
                {
                    // КЭ без As — пропускаем быстро
                }
                catch (COMException)
                {
                }
            }

            return ok;
        }

        private static int[] BuildFastOptions(int designOption)
        {
            // Только заданный + 1 (типичный). Без 0/2/3 — они давали лишние долгие запросы.
            if (designOption <= 0) return new[] { 1 };
            if (designOption == 1) return new[] { 1 };
            return new[] { designOption, 1 };
        }

        private static List<string> BuildFastCandidates(string documentName)
        {
            var list = new List<string>();
            void Add(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                s = s.Trim().Trim('"');
                var dash = s.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0) s = s.Substring(0, dash).Trim();
                s = System.IO.Path.GetFileNameWithoutExtension(s);
                if (string.IsNullOrWhiteSpace(s)) return;
                if (!list.Contains(s, StringComparer.OrdinalIgnoreCase))
                    list.Add(s);
            }

            foreach (var part in documentName.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                Add(part);

            // максимум 2 кандидата
            if (list.Count > 2)
                list = list.Take(2).ToList();

            return list;
        }

        public void Dispose()
        {
            try { Marshal.FinalReleaseComObject(_results); } catch { /* ignore */ }
        }
    }
}
