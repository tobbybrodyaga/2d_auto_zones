using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LiraSlabZones.Core;
using Microsoft.Win32;

namespace LiraSlabZones.Revit2023.UI
{
    public partial class ZonePreviewWindow : Window
    {
        private AnalysisResult? _result;
        private List<LiraPlateElement> _plates = new List<LiraPlateElement>();
        private Action<AnalysisResult>? _placeCallback;
        private bool _suppressUiEvents;
        private bool _busy;
        private readonly DispatcherTimer _rebuildTimer;

        public ZonePreviewWindow()
        {
            InitializeComponent();
            TxtStatus.Text = "Выберите: Демо / JSON / Анализ ЛИРА";
            _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _rebuildTimer.Tick += (_, __) =>
            {
                _rebuildTimer.Stop();
                RebuildZonesPreservingView(fromUi: true);
            };
            Loaded += (_, __) =>
            {
                Viewport.ZoneSelected += z =>
                {
                    var bars = z.DiameterMm > 0
                        ? $"Ø{z.DiameterMm}/{z.BarStepMm}×{z.BarCount}\n{z.FamilyKind} {z.Direction}\n"
                        : "";
                    var size = z.LengthMm > 0
                        ? $"L×B: {z.LengthMm:0}×{z.WidthMm:0} мм\n"
                        : $"Габарит: {z.WidthM:F2}×{z.LengthM:F2} м\n";
                    var tie = string.IsNullOrWhiteSpace(z.AxisTieLabel) ? "" : $"Привязка: {z.AxisTieLabel}\n";
                    TxtZoneInfo.Text =
                        $"Зона #{z.ZoneId}\nСлой: {z.Layer}\nКЭ: {z.ElementId}\n" +
                        bars +
                        $"As треб.: {z.AsRequired:F2}\nAs доп.: {z.AsAdditional:F2}\n" +
                        size +
                        tie +
                        z.Comment;
                };
                Viewport.StatusChanged += s => TxtStatus.Text = s;
            };
        }

        public void SetPlaceCallback(Action<AnalysisResult> callback)
        {
            _placeCallback = callback;
            BtnPlace.IsEnabled = callback != null;
        }

        public void LoadResult(AnalysisResult result, bool fitView = true, bool syncUiSettings = true)
        {
            _result = result;
            _plates = result.Plates ?? new List<LiraPlateElement>();
            _suppressUiEvents = true;
            try
            {
                if (syncUiSettings)
                    ApplySettingsToUi(result.Settings);
                FillLevelsCombo(result);
            }
            finally { _suppressUiEvents = false; }

            var elev = string.IsNullOrWhiteSpace(result.ElevationLabel) ? "" : $"\n{result.ElevationLabel}";
            TxtSource.Text = $"{result.DocumentName}\nпластин: {result.PlateCount}, зон: {result.Zones.Count}{elev}";
            TxtTitle.Text = string.IsNullOrWhiteSpace(result.ElevationLabel)
                ? result.DocumentName
                : $"{result.DocumentName}  ·  {result.ElevationLabel}";
            RefreshStats();
            PushToViewport(fitView);
            Log($"Загружено: {result.DocumentName}, зон {result.Zones.Count}, оси {result.Axes.Count}, контур {result.Outline.Count} т.", "ok");
        }

        private void FillLevelsCombo(AnalysisResult result)
        {
            CmbLevels.Items.Clear();
            var levels = result.AvailableLevels;
            if (levels == null || levels.Count == 0)
            {
                var all = result.AllPlates != null && result.AllPlates.Count > 0 ? result.AllPlates : result.Plates;
                all = MeshBoundary.FilterHorizontalPlates(all);
                levels = MeshBoundary.CollectLevels(all, null);
                result.AvailableLevels = levels;
            }

            int select = -1;
            for (int i = 0; i < levels.Count; i++)
            {
                CmbLevels.Items.Add(levels[i]);
                if (Math.Abs(levels[i].ZM - result.ElevationZM) < 0.08)
                    select = i;
            }
            if (select >= 0) CmbLevels.SelectedIndex = select;
            else if (CmbLevels.Items.Count > 0) CmbLevels.SelectedIndex = 0;
        }

        private void PushToViewport(bool fitView = true)
        {
            if (_result == null) return;
            Viewport.SetData(
                _result,
                ReadSettingsFromUi(),
                ChkShowMesh.IsChecked == true,
                ChkShowIso.IsChecked == true,
                ChkShowAxes.IsChecked == true,
                fitView: fitView);
            TxtZoom.Text = $"{Viewport.Zoom * 100:0}%";
        }

        private void BtnDemo_Click(object sender, RoutedEventArgs e)
        {
            var s = ReadSettingsFromUi();
            s.SlabSelected = true;
            LoadResult(DemoSlabFactory.Create(s));
            if (_result != null) _result.Settings.SlabSelected = true;
            UpdateLayoutGate();
        }

        private void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "JSON зон" };
            if (dlg.ShowDialog() != true) return;
            try { LoadResult(SlabZoneAnalyzer.LoadJson(dlg.FileName)); }
            catch (Exception ex)
            {
                Log(ex.Message, "error");
                MessageBox.Show(ex.Message);
            }
        }

        private async void BtnAnalyzeLira_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            Cursor = Cursors.Wait;
            TxtStatus.Text = "Чтение ЛИРА…";
            var settings = ReadSettingsFromUi();
            try
            {
                var result = await Task.Run(() => new SlabZoneAnalyzer().Analyze(null, settings));
                LoadResult(result);
                try
                {
                    var outPath = System.IO.Path.Combine(SolutionPaths.FindRoot(), "output", "slab_zones.json");
                    SlabZoneAnalyzer.SaveJson(result, outPath);
                    Log("JSON: " + outPath, "ok");
                }
                catch { /* ignore */ }

                if (!string.IsNullOrWhiteSpace(result.UnitsNote))
                    Log(result.UnitsNote, "ok");
            }
            catch (Exception ex)
            {
                Log(ex.Message, "error");
                MessageBox.Show("Не удалось проанализировать схему ЛИРА.\n\n" + ex.Message, "ЛИРА");
            }
            finally
            {
                _busy = false;
                Cursor = Cursors.Arrow;
            }
        }

        private void BtnRebuild_Click(object sender, RoutedEventArgs e) =>
            RebuildZonesPreservingView(fromUi: true);

        /// <summary>Пересчёт зон без сброса зума/пана и полей UI.</summary>
        private void RebuildZonesPreservingView(bool fromUi)
        {
            if (_result == null) return;
            if (_busy) return;

            var settings = fromUi ? ReadSettingsFromUi() : (_result.Settings ?? new AnalysisSettings());
            var all = _result.AllPlates != null && _result.AllPlates.Count > 0 ? _result.AllPlates : _plates;
            if (all.Count == 0 && _plates.Count == 0) return;

            var levels = _result.AvailableLevels;
            var axes = _result.Axes;
            var openings = _result.Openings;
            var units = _result.UnitsNote;

            AnalysisResult rebuilt;
            if (!double.IsNaN(settings.TargetElevationZM))
                rebuilt = SlabZoneAnalyzer.RebuildForElevation(_result, settings.TargetElevationZM, settings);
            else
            {
                rebuilt = SlabZoneAnalyzer.BuildResult(
                    _result.DocumentName,
                    _result.DocumentPath,
                    _result.NodeCount,
                    _plates.Count > 0 ? _plates : all,
                    settings,
                    axes,
                    _result.ElevationZM,
                    _result.ElevationLabel,
                    skipLevelFilter: true,
                    openings: openings);
                rebuilt.AllPlates = all;
                rebuilt.AvailableLevels = levels;
            }

            rebuilt.UnitsNote = units;
            rebuilt.Settings = settings;
            _result = rebuilt;
            _plates = rebuilt.Plates ?? _plates;
            RefreshStats();
            UpdateBackgroundAsLabels(settings);
            UpdateLayoutGate(settings);
            var gate = settings.CanLayoutAdditionalZones(out var why)
                ? ""
                : $" | допки: {why}";
            TxtSource.Text = $"{rebuilt.DocumentName}\nпластин: {rebuilt.PlateCount}, зон: {rebuilt.Zones.Count}" +
                             (string.IsNullOrWhiteSpace(rebuilt.ElevationLabel) ? "" : $"\n{rebuilt.ElevationLabel}");
            PushToViewport(fitView: false);
            Log($"Пересчёт: зон {rebuilt.Zones.Count}{gate} (вид сохранён)",
                settings.CanLayoutAdditionalZones(out _) ? "ok" : "warn");
        }

        private void ScheduleAutoRebuild()
        {
            if (!IsLoaded || _suppressUiEvents || _result == null) return;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private async void BtnLoadLevels_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            Cursor = Cursors.Wait;
            try
            {
                List<(string Name, double Z)> marks = new();
                await Task.Run(() =>
                {
                    using var geo = new LiraGeometryReader();
                    geo.AttachToRunningOrOpen(null);
                    marks = geo.ReadElevationMarks();
                });

                if (_result == null)
                {
                    CmbLevels.Items.Clear();
                    foreach (var (name, z) in marks.OrderByDescending(m => m.Z))
                    {
                        CmbLevels.Items.Add(new ElevationLevelInfo
                        {
                            Label = $"{name}  (Z={z:F3} м)",
                            ZM = z,
                            PlateCount = 0,
                            FromMarks = true
                        });
                    }
                    if (CmbLevels.Items.Count > 0) CmbLevels.SelectedIndex = 0;
                    Log($"Отметок из ЛИРА: {marks.Count}. Выберите уровень и нажмите «Анализ ЛИРА».", "ok");
                    return;
                }

                var all = _result.AllPlates != null && _result.AllPlates.Count > 0
                    ? _result.AllPlates
                    : _result.Plates;
                all = MeshBoundary.FilterHorizontalPlates(all);
                _result.AllPlates = all;
                _result.AvailableLevels = MeshBoundary.CollectLevels(all, marks);
                FillLevelsCombo(_result);
                Log($"Уровней: {_result.AvailableLevels.Count} (гориз. плит: {all.Count}, отметок: {marks.Count})", "ok");
            }
            catch (Exception ex)
            {
                Log(ex.Message, "error");
                MessageBox.Show("Не удалось прочитать уровни из ЛИРА.\n\n" + ex.Message, "ЛИРА");
            }
            finally
            {
                _busy = false;
                Cursor = Cursors.Arrow;
            }
        }

        private void BtnApplyLevel_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null)
            {
                MessageBox.Show("Сначала выполните анализ ЛИРА.");
                return;
            }
            if (CmbLevels.SelectedItem is not ElevationLevelInfo level)
            {
                MessageBox.Show("Выберите уровень в списке.");
                return;
            }

            var settings = ReadSettingsFromUi();
            settings.TargetElevationZM = level.ZM;
            settings.SlabSelected = true;
            var all = _result.AllPlates != null && _result.AllPlates.Count > 0
                ? _result.AllPlates
                : _result.Plates;
            var levels = _result.AvailableLevels;

            var rebuilt = SlabZoneAnalyzer.RebuildForElevation(_result, level.ZM, settings);
            // перенести As с AllPlates (уже прочитаны при анализе)
            var byId = new Dictionary<int, PlateReinforcement>();
            foreach (var p in all)
                if (p.Rebar.Ok) byId[p.Id] = p.Rebar;
            foreach (var p in rebuilt.Plates)
                if (byId.TryGetValue(p.Id, out var rb)) p.Rebar = rb;

            rebuilt = SlabZoneAnalyzer.BuildResult(
                rebuilt.DocumentName,
                rebuilt.DocumentPath,
                rebuilt.NodeCount,
                rebuilt.Plates,
                settings,
                rebuilt.Axes,
                rebuilt.ElevationZM,
                level.Label,
                skipLevelFilter: true);
            rebuilt.AllPlates = all;
            rebuilt.AvailableLevels = levels;

            LoadResult(rebuilt, fitView: false, syncUiSettings: false);
            if (_result != null) _result.Settings.SlabSelected = true;
            UpdateLayoutGate();
            Log($"Выбран уровень {level.Label}, КЭ={rebuilt.PlateCount}", "ok");
        }

        private void BtnRotateCw_Click(object sender, RoutedEventArgs e) => RotateBy(90);

        private void BtnRotateCcw_Click(object sender, RoutedEventArgs e) => RotateBy(-90);

        private void RotateBy(double delta)
        {
            double cur = 0;
            double.TryParse((TbRot.Text ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out cur);
            cur = Math.Round(cur + delta, 2);
            while (cur > 180) cur -= 360;
            while (cur <= -180) cur += 360;
            TbRot.Text = cur.ToString("0.##", CultureInfo.InvariantCulture);
            ApplyTransformToViewport();
        }

        private void BtnAlignAxes_Click(object sender, RoutedEventArgs e)
        {
            if (_result?.Axes == null || _result.Axes.Count == 0)
            {
                MessageBox.Show("Нет осей из ЛИРА.");
                return;
            }
            double ang = ContourFix.EstimateGridAngleDeg(_result.Axes);
            TbRot.Text = (-ang).ToString("0.##", CultureInfo.InvariantCulture);
            ApplyTransformToViewport();
            Log($"Выравнивание по осям: поворот {-ang:F1}°", "ok");
        }

        private void BtnResetTransform_Click(object sender, RoutedEventArgs e)
        {
            TbOffX.Text = "0"; TbOffY.Text = "0"; TbRot.Text = "0";
            ApplyTransformToViewport();
        }

        private void TransformChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressUiEvents) return;
            ApplyTransformToViewport();
        }

        private void ApplyTransformToViewport()
        {
            if (_result == null) return;
            Viewport.RefreshTransform(ReadSettingsFromUi(), fit: true);
            TxtZoom.Text = $"{Viewport.Zoom * 100:0}%";
        }

        private void BtnSaveJson_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "slab_zones.json" };
            if (dlg.ShowDialog() != true) return;
            _result.Settings = ReadSettingsFromUi();
            SlabZoneAnalyzer.SaveJson(_result, dlg.FileName);
            Log("Сохранено: " + dlg.FileName, "ok");
        }

        private void BtnSaveAllPlatesJson_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null)
            {
                MessageBox.Show("Сначала выполните анализ ЛИРА.");
                return;
            }

            var plates = _result.AllPlates != null && _result.AllPlates.Count > 0
                ? _result.AllPlates
                : _result.Plates;
            plates = MeshBoundary.FilterHorizontalPlates(plates);
            if (plates.Count == 0)
            {
                MessageBox.Show("Нет горизонтальных плит для выгрузки.");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "lira_all_plates.json",
                Title = "Сохранить JSON всех плит"
            };
            if (dlg.ShowDialog() != true) return;

            // временно подставить отфильтрованный список
            var prev = _result.AllPlates;
            _result.AllPlates = plates;
            try
            {
                SlabZoneAnalyzer.SaveAllPlatesJson(_result, dlg.FileName);
                Log($"Все плиты: {plates.Count} → {dlg.FileName}", "ok");
            }
            finally
            {
                _result.AllPlates = prev ?? new List<LiraPlateElement>();
            }
        }

        private void BtnPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _placeCallback == null) return;
            _result.Settings = ReadSettingsFromUi();
            _placeCallback(_result);
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            Viewport.ZoomBy(1.25);
            TxtZoom.Text = $"{Viewport.Zoom * 100:0}%";
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            Viewport.ZoomBy(1 / 1.25);
            TxtZoom.Text = $"{Viewport.Zoom * 100:0}%";
        }

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
        {
            Viewport.ResetZoom();
            TxtZoom.Text = "100%";
        }

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
        {
            Viewport.FitToView();
            Viewport.InvalidateVisual();
            TxtZoom.Text = $"{Viewport.Zoom * 100:0}%";
        }

        private void SettingsChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressUiEvents) return;
            TxtVis.Text = SldVis.Value.ToString("0", CultureInfo.InvariantCulture);
            UpdateBackgroundAsLabels();
            UpdateLayoutGate();

            // Смещение / поворот / шаг изополей — только перерисовка
            if (ReferenceEquals(sender, TbOffX) || ReferenceEquals(sender, TbOffY) ||
                ReferenceEquals(sender, TbRot) || ReferenceEquals(sender, SldVis))
            {
                if (_result != null)
                    Viewport.RefreshTransform(ReadSettingsFromUi(), fit: false);
                return;
            }

            ScheduleAutoRebuild();
        }

        private void DetailChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressUiEvents) return;
            // 5 ступеней: Max / 4 / 3 / 2 / Min
            var snapped = DetailOptimizer.SliderFromStepIndex(
                DetailOptimizer.StepIndexFromSlider(SldDetail.Value));
            if (Math.Abs(SldDetail.Value - snapped) > 1e-9)
            {
                _suppressUiEvents = true;
                SldDetail.Value = snapped;
                _suppressUiEvents = false;
            }
            var kg = _result?.Stats?.SteelKgPerM3 ?? 0;
            TxtDetail.Text = DetailOptimizer.LabelWithMass(snapped, kg);
            ScheduleAutoRebuild();
        }

        private void Redraw(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressUiEvents) return;
            Viewport.RefreshDisplayFlags(
                ChkShowMesh.IsChecked == true,
                ChkShowIso.IsChecked == true,
                ChkShowAxes.IsChecked == true);
        }

        private AnalysisSettings ReadSettingsFromUi()
        {
            double D(string s, double def) =>
                double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : def;
            int I(string s, int def) => int.TryParse(s, out var v) ? v : def;

            var barStep = ChkBarStep100.IsChecked == true ? 100 : 200;
            var concrete = ComboIntOrText(CmbConcrete);
            if (concrete == "—" || concrete == "-") concrete = "";

            var auto = ChkAutoLayout.IsChecked == true;
            var detailSlider = DetailOptimizer.SliderFromStepIndex(
                DetailOptimizer.StepIndexFromSlider(SldDetail.Value));
            var detail = DetailOptimizer.FromSlider(detailSlider);

            var bgBotD = ComboIntOrText(CmbBgBotD);
            var bgBotStep = ComboIntOrText(CmbBgBotStep);
            var bgTopD = ComboIntOrText(CmbBgTopD);
            var bgTopStep = ComboIntOrText(CmbBgTopStep);
            int.TryParse(bgBotD, out var botD);
            int.TryParse(bgBotStep, out var botStep);
            int.TryParse(bgTopD, out var topD);
            int.TryParse(bgTopStep, out var topStep);

            var settings = new AnalysisSettings
            {
                ShowAs1 = ChkAs1.IsChecked == true,
                ShowAs2 = ChkAs2.IsChecked == true,
                ShowAs3 = ChkAs3.IsChecked == true,
                ShowAs4 = ChkAs4.IsChecked == true,
                BgBottomDiameterMm = botD,
                BgBottomStepMm = botStep,
                BgTopDiameterMm = topD,
                BgTopStepMm = topStep,
                MinZoneWidthM = ParseZoneSizeMmToM(TbMinW.Text),
                MaxZoneWidthM = ParseZoneSizeMmToM(TbMaxW.Text),
                MinZoneLengthM = ParseZoneSizeMmToM(TbMinL.Text),
                MinActiveElements = I(TbMinFe.Text, 0),
                VisualizationScale = SldVis.Value,
                OffsetXM = D(TbOffX.Text, 0),
                OffsetYM = D(TbOffY.Text, 0),
                RotationDeg = D(TbRot.Text, 0),
                TargetElevationZM = CmbLevels.SelectedItem is ElevationLevelInfo lv ? lv.ZM : double.NaN,
                LoadReinforcement = true,
                ModelPart = "Visible",
                FamilyName = _result?.Settings.FamilyName ?? new AnalysisSettings().FamilyName,
                FamilyFileName = _result?.Settings.FamilyFileName ?? new AnalysisSettings().FamilyFileName,
                AutoLayout = auto,
                PlacementMode = auto ? "AutoLayout" : "ElementCenter",
                DetailLevel = detail,
                DetailSlider = detailSlider,
                BarStepMm = barStep,
                UseBarStep100 = ChkBarStep100.IsChecked == true,
                ConcreteClass = concrete,
                GridCellMm = I(TbGridCell.Text, 300),
                SlabThicknessMm = D(TbThick.Text, 200),
                CoverBottomMm = D(TbCoverBot.Text, 25),
                CoverTopMm = D(TbCoverTop.Text, 25),
                ApplyHoleRules = true,
                ApplyBentRules = true,
                AlphaCoef = 1.0,
                SlabEdgeInsetMm = 30,
                SlabSelected = _result?.Settings.SlabSelected == true
            };
            settings.SyncBackgroundAsFromBars();
            return settings;
        }

        private static string ComboIntOrText(ComboBox cmb)
        {
            if (cmb.SelectedItem is ComboBoxItem ci)
                return (ci.Content?.ToString() ?? "").Trim();
            return (cmb.Text ?? "").Trim();
        }

        private void ApplySettingsToUi(AnalysisSettings s)
        {
            ChkAs1.IsChecked = s.ShowAs1;
            ChkAs2.IsChecked = s.ShowAs2;
            ChkAs3.IsChecked = s.ShowAs3;
            ChkAs4.IsChecked = s.ShowAs4;
            SelectCombo(CmbBgBotD, s.BgBottomDiameterMm > 0 ? s.BgBottomDiameterMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgBotStep, s.BgBottomStepMm > 0 ? s.BgBottomStepMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgTopD, s.BgTopDiameterMm > 0 ? s.BgTopDiameterMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgTopStep, s.BgTopStepMm > 0 ? s.BgTopStepMm.ToString(CultureInfo.InvariantCulture) : "—");
            UpdateBackgroundAsLabels(s);
            TbMinW.Text = FormatZoneSizeMm(s.MinZoneWidthM);
            TbMaxW.Text = FormatZoneSizeMm(s.MaxZoneWidthM);
            TbMinL.Text = FormatZoneSizeMm(s.MinZoneLengthM);
            TbMinFe.Text = s.MinActiveElements.ToString(CultureInfo.InvariantCulture);
            SldVis.Value = Math.Max(0, Math.Min(25, s.VisualizationScale));
            TxtVis.Text = SldVis.Value.ToString("0", CultureInfo.InvariantCulture);
            TbOffX.Text = s.OffsetXM.ToString("0.###", CultureInfo.InvariantCulture);
            TbOffY.Text = s.OffsetYM.ToString("0.###", CultureInfo.InvariantCulture);
            TbRot.Text = s.RotationDeg.ToString("0.##", CultureInfo.InvariantCulture);

            ChkAutoLayout.IsChecked = s.AutoLayout;
            SldDetail.Value = DetailOptimizer.SliderFromStepIndex(
                DetailOptimizer.StepIndexFromSlider(Math.Max(0, Math.Min(1, s.DetailSlider))));
            TxtDetail.Text = DetailOptimizer.LabelWithMass(SldDetail.Value, 0);
            TbGridCell.Text = (s.GridCellMm > 0 ? s.GridCellMm : 300).ToString(CultureInfo.InvariantCulture);
            TbThick.Text = s.SlabThicknessMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverBot.Text = s.CoverBottomMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverTop.Text = s.CoverTopMm.ToString("0.##", CultureInfo.InvariantCulture);

            ChkBarStep100.IsChecked = s.UseBarStep100 || s.BarStepMm == 100;
            SelectCombo(CmbConcrete, string.IsNullOrWhiteSpace(s.ConcreteClass) ? "—" : s.ConcreteClass);
            UpdateLayoutGate(s);
        }

        private void UpdateBackgroundAsLabels(AnalysisSettings? s = null)
        {
            s ??= ReadSettingsFromUi();
            if (s.BgBottomDiameterMm > 0 && s.BgBottomStepMm > 0)
            {
                var a = BarCapacity.AsCm2PerM(s.BgBottomDiameterMm, s.BgBottomStepMm);
                TxtBgBotAs.Text = $"As низа = {a:0.##} см²/м (Ø{s.BgBottomDiameterMm}/{s.BgBottomStepMm})";
            }
            else TxtBgBotAs.Text = "As низа = —";

            if (s.BgTopDiameterMm > 0 && s.BgTopStepMm > 0)
            {
                var a = BarCapacity.AsCm2PerM(s.BgTopDiameterMm, s.BgTopStepMm);
                TxtBgTopAs.Text = $"As верха = {a:0.##} см²/м (Ø{s.BgTopDiameterMm}/{s.BgTopStepMm})";
            }
            else TxtBgTopAs.Text = "As верха = —";
        }

        private void UpdateLayoutGate(AnalysisSettings? s = null)
        {
            if (TxtLayoutGate == null) return;
            s ??= (_result?.Settings != null ? ReadSettingsFromUi() : new AnalysisSettings());
            if (s.CanLayoutAdditionalZones(out var reason))
            {
                TxtLayoutGate.Text = "Условия выполнены — можно раскладывать допки.";
                TxtLayoutGate.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D));
            }
            else
            {
                TxtLayoutGate.Text = reason;
                TxtLayoutGate.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09));
            }
        }

        /// <summary>
        /// UI в мм → в настройках храним метры.
        /// Если раньше сохранили «800» как метры (баг подписи) — считаем это мм.
        /// </summary>
        private static double ParseZoneSizeMmToM(string text)
        {
            if (!double.TryParse((text ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || v <= 0)
                return 0;
            if (v >= 50) return v / 1000.0; // ввели мм
            return v; // уже метры (редко)
        }

        private static string FormatZoneSizeMm(double storedM)
        {
            if (storedM <= 0) return "0";
            // stored мог быть ошибочно 800 (мм как «метры»)
            var mm = storedM >= 50 ? storedM : storedM * 1000.0;
            return mm.ToString("0", CultureInfo.InvariantCulture);
        }

        private static void SelectCombo(ComboBox cmb, string content)
        {
            foreach (var item in cmb.Items)
            {
                if (item is ComboBoxItem ci &&
                    string.Equals(ci.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = ci;
                    return;
                }
            }
        }

        private void RefreshStats()
        {
            if (_result == null) { TxtStats.Text = "—"; return; }
            var st = _result.Stats;
            var elev = string.IsNullOrWhiteSpace(_result.ElevationLabel)
                ? $"Z = {_result.ElevationZM:F3} м"
                : _result.ElevationLabel;
            var mode = _result.Settings.AutoLayout ? "Автораскладка" : "По КЭ";
            TxtStats.Text =
                $"Отметка: {elev}\n" +
                $"Режим: {mode} / {st.DetailLevelLabel}\n" +
                $"КЭ: {_result.PlateCount}\n" +
                $"Оси: {_result.Axes.Count}\n" +
                $"Зоны As1: {st.ZonesAs1}  ({st.AreaAs1M2:F1} м²)\n" +
                $"Зоны As2: {st.ZonesAs2}  ({st.AreaAs2M2:F1} м²)\n" +
                $"Зоны As3: {st.ZonesAs3}  ({st.AreaAs3M2:F1} м²)\n" +
                $"Зоны As4: {st.ZonesAs4}  ({st.AreaAs4M2:F1} м²)\n" +
                $"As max: {st.MaxAs:F2} см²/м\n" +
                $"Масса стали ≈ {st.TotalSteelMassKg:F1} кг\n" +
                $"Расход ≈ {st.SteelKgPerM3:F1} кг/м³\n" +
                $"Контур: {_result.Outline.Count} вершин\n" +
                $"⚠ {st.WarnCount}   ✕ {st.ErrorCount}";
            TxtDetail.Text = string.IsNullOrWhiteSpace(st.DetailLevelLabel)
                ? DetailOptimizer.LabelWithMass(_result.Settings.DetailSlider, st.SteelKgPerM3)
                : st.DetailLevelLabel;
        }

        private void Log(string msg, string level)
        {
            var prefix = level == "error" ? "[ERR] " : level == "warn" ? "[WRN] " : "[OK] ";
            LstLog.Items.Insert(0, prefix + msg);
            while (LstLog.Items.Count > 60) LstLog.Items.RemoveAt(LstLog.Items.Count - 1);
        }
    }
}
