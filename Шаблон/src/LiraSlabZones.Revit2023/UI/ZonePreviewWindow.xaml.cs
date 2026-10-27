using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using LiraSlabZones.Core;
using Microsoft.Win32;

namespace LiraSlabZones.Revit2023.UI
{
    public partial class ZonePreviewWindow : Window
    {
        private AnalysisResult? _result;
        private List<LiraPlateElement> _plates = new List<LiraPlateElement>();
        private Action<AnalysisResult>? _placeCallback;
        private Action<AnalysisResult>? _linkCallback;
        /// <summary>Связь с Revit живёт дольше одного результата: пересчёт создаёт новый AnalysisResult.</summary>
        private RevitLinkInfo? _revitLink;
        private bool _suppressUiEvents;
        private bool _updatingBoundaryToggle;
        private bool _busy;
        private bool _suppressZoneFamilySelection;
        private readonly HashSet<int> _excludedZoneDiameters = new HashSet<int>();
        private readonly DispatcherTimer _rebuildTimer;

        public ZonePreviewWindow()
        {
            InitializeComponent();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control ||
                    Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
                e.Handled = Viewport.UndoLastEdit();
            };
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
                    SelectComboValue(CmbSelectedZoneDiameter, z.DiameterMm);
                    SelectComboValue(CmbSelectedZoneStep, z.BarStepMm);
                    TbSelectedLengthMm.Text = z.LengthMm.ToString("0", CultureInfo.InvariantCulture);
                    TbSelectedWidthMm.Text = z.WidthMm.ToString("0", CultureInfo.InvariantCulture);
                    _suppressZoneFamilySelection = true;
                    try
                    {
                        for (var i = 0; i < CmbSelectedZoneFamilyKind.Items.Count; i++)
                            if ((CmbSelectedZoneFamilyKind.Items[i] as ComboBoxItem)?.Tag?.ToString() == z.FamilyKind.ToString())
                                CmbSelectedZoneFamilyKind.SelectedIndex = i;
                        CmbSelectedZoneFamilyName.Text = z.FamilyFileName;
                    }
                    finally { _suppressZoneFamilySelection = false; }
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
                Viewport.PatchSelected += selection =>
                {
                    if (selection == null || selection.Patches.Count == 0)
                    {
                        TxtZoneInfo.Text = "Кликните пунктирную рамку на холсте";
                        return;
                    }

                    var patches = selection.Patches;
                    var layers = string.Join(", ", patches.Select(patch => patch.Layer).Distinct());
                    var elementCount = patches.SelectMany(patch => patch.ElementIds).Distinct().Count();
                    var patchIds = string.Join(", ", patches.Take(8).Select(patch => $"#{patch.PatchId}"));
                    if (patches.Count > 8) patchIds += $" … +{patches.Count - 8}";
                    var zoneLines = selection.Zones.Count == 0
                        ? "Зоны не сформированы: выберите класс бетона и эталонные Ø/шаг."
                        : string.Join("\n", selection.Zones.Select(zone =>
                            $"{zone.Layer}: {zone.DiameterMm}-{zone.LengthMm:0} ×{zone.BarCount}, шаг {zone.BarStepMm}" +
                            $"\nAs треб.: {zone.AsRequired:0.##}; As доп.: {zone.AsAdditional:0.##}" +
                            (zone.StatusColor == "warn" ? $"\nПредупреждение: {zone.Comment}" : "")));

                    TxtZoneInfo.Text =
                        $"{(patches.Count == 1 ? $"Пятно {patchIds}" : $"Объединённая рамка: пятен {patches.Count} ({patchIds})")}\n" +
                        $"Слои: {layers}\nКЭ (уник.): {elementCount}\n" +
                        $"Ячейки мозаики: {patches.Sum(patch => patch.Cells.Count)}\n" +
                        $"Габарит рамки: {(selection.MaxXM - selection.MinXM) * 1000:0} × {(selection.MaxYM - selection.MinYM) * 1000:0} мм\n" +
                        "Зоны в рамке:\n" + zoneLines;
                };
                Viewport.StatusChanged += s => TxtStatus.Text = s;
                Viewport.ZonesEdited += () =>
                {
                    if (_result == null) return;
                    TxtTitle.Text = $"{_result.DocumentName} · Z = {_result.ElevationZM:F3} м · зон: {Viewport.EditableZoneCount}";
                    RefreshStats();
                    Log($"Зоны отредактированы: {Viewport.EditableZoneCount}", "ok");
                };
            };
        }

        private void SetZoneEditMode(ZoneEditMode mode)
        {
            Viewport.SetEditMode(mode);
            TxtStatus.Text = $"Режим редактирования: {mode}";
        }

        private void BtnEditSelect_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Select);
        private void BtnEditMove_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Move);
        private void BtnEditResize_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Resize);
        private void BtnEditCreate_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Create);
        private void BtnEditSplit_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Split);
        private void BtnEditMerge_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Merge);
        private void BtnEditDelete_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.Delete);
        private void BtnEditGap_Click(object sender, RoutedEventArgs e) => SetZoneEditMode(ZoneEditMode.CreateGap);
        private void BtnEditPerpendicular_Click(object sender, RoutedEventArgs e) =>
            SetZoneEditMode(ZoneEditMode.PerpendicularToEdge);

        private void CmbDiagnostics_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Viewport == null || !(CmbDiagnostics?.SelectedItem is ComboBoxItem item)) return;
            if (Enum.TryParse(item.Tag?.ToString(), out DiagnosticFilter filter))
                Viewport.SetDiagnosticFilter(filter);
        }

        private void BtnApplyZoneDiameter_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbSelectedZoneDiameter.SelectedItem is ComboBoxItem item) ||
                !int.TryParse(item.Content?.ToString(), out var diameter)) return;
            if (!Viewport.SetSelectedDiameter(diameter))
                TxtStatus.Text = "Сначала выберите зону; диаметр не может быть меньше фонового";
        }

        private void BtnApplyZoneStep_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbSelectedZoneStep.SelectedItem is ComboBoxItem item) ||
                !int.TryParse(item.Content?.ToString(), out var step) ||
                !Viewport.SetSelectedStep(step))
                TxtStatus.Text = "Сначала выберите зону";
        }

        private void BtnZoneDiameters_Click(object sender, RoutedEventArgs e)
        {
            PanelZoneDiameters.Children.Clear();
            foreach (var diameter in RebarTables.AllowedDiametersMm)
            {
                var value = diameter;
                var check = new CheckBox
                {
                    Content = $"Ø{diameter} мм",
                    IsChecked = !_excludedZoneDiameters.Contains(diameter),
                    Margin = new Thickness(2)
                };
                check.Checked += (_, __) => { _excludedZoneDiameters.Remove(value); ScheduleZoneRebuild(); };
                check.Unchecked += (_, __) => { _excludedZoneDiameters.Add(value); ScheduleZoneRebuild(); };
                PanelZoneDiameters.Children.Add(check);
            }
            PopupZoneDiameters.IsOpen = true;
        }

        private void ScheduleZoneRebuild()
        {
            if (_busy || _suppressUiEvents) return;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void BtnApplyZoneDimensions_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TbSelectedLengthMm.Text.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var length) ||
                !double.TryParse(TbSelectedWidthMm.Text.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var width) ||
                !Viewport.ResizeSelectedZone(length, width))
                TxtStatus.Text = "Выберите зону и задайте L и B больше 50 мм";
        }

        private void SelectedZoneFamilyKindChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressZoneFamilySelection || _result == null ||
                !(CmbSelectedZoneFamilyKind.SelectedItem is ComboBoxItem item) ||
                !Enum.TryParse(item.Tag?.ToString(), out ZoneFamilyKind kind)) return;
            CmbSelectedZoneFamilyName.Text = _result.Settings.GetFamilyName(kind);
        }

        private void BtnApplyZoneFamily_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbSelectedZoneFamilyKind.SelectedItem is ComboBoxItem item) ||
                !Enum.TryParse(item.Tag?.ToString(), out ZoneFamilyKind kind) ||
                !Viewport.SetSelectedFamily(kind, CmbSelectedZoneFamilyName.Text))
                TxtStatus.Text = "Выберите зону и укажите имя загруженного семейства";
        }

        private static void SelectComboValue(ComboBox combo, int value)
        {
            foreach (var entry in combo.Items)
            {
                if (entry is ComboBoxItem item && item.Content?.ToString() == value.ToString(CultureInfo.InvariantCulture))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        public void SetPlaceCallback(Action<AnalysisResult> callback)
        {
            _placeCallback = callback;
            UpdatePreviewModeControls(_result);
        }

        public void SetLinkCallback(Action<AnalysisResult> callback)
        {
            _linkCallback = callback;
            UpdatePreviewModeControls(_result);
        }

        /// <summary>Принять связь с Revit (уровень, планы, оси) и показать оси на холсте.</summary>
        public void ApplyRevitLink(RevitLinkInfo link)
        {
            _revitLink = link;
            var registration = link.Registration;
            if (registration != null && registration.Reliable)
            {
                // Поворот и сдвиг — с точностью до долей мм: 0,01° на 50 м — это уже 9 мм.
                _suppressUiEvents = true;
                try { SetTransformText(registration.RotationDeg, registration.OffsetXM, registration.OffsetYM); }
                finally { _suppressUiEvents = false; }
            }
            PushToViewport(fitView: false);

            Log($"Связь с Revit: уровень «{link.LevelName}», планы на отметке " +
                $"{RevitLinkRules.FormatElevation(link.PlanElevationM)}, планов {link.Plans.Count}, осей {link.Grids.Count}" +
                (link.SkippedGrids > 0 ? $" (пропущено дуговых/составных: {link.SkippedGrids})" : ""), "ok");
            if (registration == null)
                Log(link.Floors.Count == 0
                    ? "Совмещение с Revit: на уровне нет перекрытий — поворот и сдвиг оставлены как были"
                    : "Совмещение с Revit: контур плиты не удалось сопоставить с перекрытиями", "warn");
            else if (registration.Reliable)
                Log("Совмещение с Revit по перекрытиям: " + registration.Summary, "ok");
            else
                Log("Совмещение с Revit ненадёжно, поворот и сдвиг оставлены как были: " + registration.Summary, "warn");

            var main = link.Floors.OrderByDescending(floor => floor.AreaM2).FirstOrDefault();
            var thickness = ReadSettingsFromUi().SlabThicknessMm;
            if (main != null && main.ThicknessMm > 0 && Math.Abs(main.ThicknessMm - thickness) > 0.5)
                Log($"Толщина перекрытия в Revit {main.ThicknessMm:0} мм, в программе {thickness:0.##} мм — проверьте", "warn");

            var alignment = Viewport.LastAlignment;
            if (alignment != null)
                Log("Привязка зон к осям Revit (кратно 10 мм): " + alignment.Summary,
                    alignment.UntiedZones > 0 ? "warn" : "ok");
            RefreshStats();
        }

        private void SetTransformText(double rotationDeg, double offsetXM, double offsetYM)
        {
            TbOffX.Text = offsetXM.ToString("0.####", CultureInfo.InvariantCulture);
            TbOffY.Text = offsetYM.ToString("0.####", CultureInfo.InvariantCulture);
            TbRot.Text = rotationDeg.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>Подставить связь в текущий результат, если отметка та же; иначе связь не действует.</summary>
        private void AttachRevitLink()
        {
            if (_result == null) return;
            if (_result.RevitLink != null && !ReferenceEquals(_result.RevitLink, _revitLink) &&
                _result.RevitLink.Matches(_result.ElevationZM))
                _revitLink = _result.RevitLink; // пришла из загруженного JSON
            _result.RevitLink = _revitLink != null && _revitLink.Matches(_result.ElevationZM) ? _revitLink : null;

            var link = _result.RevitLink;
            TxtRevitLink.Text = link == null
                ? _revitLink == null
                    ? "Связь с Revit: нет"
                    : $"Связь с Revit: для отметки {RevitLinkRules.FormatElevation(_revitLink.LiraElevationZM)}, не для текущей"
                : $"Связь с Revit: «{link.LevelName}», планов {link.Plans.Count}, осей {link.Grids.Count}";
        }

        private void BtnLinkRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _linkCallback == null) return;
            _result.Settings = ReadSettingsFromUi();
            _linkCallback(_result);
        }

        private void UpdatePreviewModeControls(AnalysisResult? result)
        {
            var patchMode = result?.PatchPreviewOnly == true;
            var editable = result != null && (patchMode
                ? Viewport.EditableZoneCount > 0 || result.Patches.Count > 0
                : result.Zones.Count > 0);
            // Зоны пересчитываются холстом при любой смене настроек, поэтому наличие зон
            // проверяется в момент нажатия (BtnPlace_Click), а не здесь.
            BtnPlace.IsEnabled = _placeCallback != null && result?.Plates?.Count > 0;
            BtnLinkRevit.IsEnabled = _linkCallback != null && result?.Plates?.Count > 0;
            BtnBoundaryEditor.IsEnabled = result?.Plates?.Count > 0;
            BtnApplyBoundaryRules.IsEnabled = result?.Plates?.Count > 0;
            BtnSaveJson.Content = result?.PatchPreviewOnly == true
                ? "Сохранить JSON пятен…"
                : "Сохранить JSON зон…";
            GrpSelectedZone.IsEnabled = editable || patchMode;
            GrpSelectedZone.Header = patchMode ? "Выбранная зона / пятно" : "Выбранная зона";
            PanelZoneEditing.Visibility = Visibility.Visible;
            PanelZoneEditing.IsEnabled = editable;
            TxtZoneInfo.Text = patchMode ? "Выберите зону или пунктирную рамку" : "Кликните зону на холсте";
            BtnEditMove.IsEnabled = editable;
            BtnEditResize.IsEnabled = editable;
            BtnEditCreate.IsEnabled = editable;
            BtnEditSplit.IsEnabled = editable;
            BtnEditPerpendicular.IsEnabled = editable;
            BtnEditMerge.IsEnabled = editable;
            BtnEditGap.IsEnabled = editable;
            BtnEditDelete.IsEnabled = editable;
            CmbDiagnostics.IsEnabled = result != null;
            ChkShowIso.Visibility = result?.PatchPreviewOnly == true ? Visibility.Collapsed : Visibility.Visible;
            ChkShowPatchBoundaries.Visibility = patchMode ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Имя прямой зоны из ComboBox (из проекта или вручную).</summary>
        public string? SelectedFamilyName
        {
            get
            {
                if (CmbZoneFamily.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
                var text = CmbZoneFamily.Text?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }

        /// <summary>Заполнить список семейств текущего документа Revit.</summary>
        public void SetProjectFamilies(IReadOnlyList<string> familyNames, string? preferred = null)
        {
            _suppressUiEvents = true;
            try
            {
                var prev = SelectedFamilyName;
                CmbZoneFamily.Items.Clear();
                if (familyNames == null || familyNames.Count == 0)
                {
                    TxtZoneFamilyHint.Text = "В проекте нет загруженных семейств. Имена можно ввести вручную.";
                }
                else
                {
                    foreach (var n in familyNames)
                    {
                        CmbZoneFamily.Items.Add(n);
                        CmbSelectedZoneFamilyName.Items.Add(n);
                    }
                    TxtZoneFamilyHint.Text = $"Семейств в проекте: {familyNames.Count}. User cfg: {AppConfig.UserConfigPath}";
                }

                var want = !string.IsNullOrWhiteSpace(preferred) ? preferred!.Trim()
                    : !string.IsNullOrWhiteSpace(prev) ? prev!
                    : _result?.Settings.FamilyStraight;

                SelectFamilyInCombo(want);
                if (CmbZoneFamily.SelectedIndex < 0 && !string.IsNullOrWhiteSpace(want))
                    CmbZoneFamily.Text = want;
            }
            finally { _suppressUiEvents = false; }
        }

        private void BtnSaveUserCfg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var s = ReadSettingsFromUi();
                AppConfig.SaveUserSettings(s);
                if (_result != null) _result.Settings = s;
                Log($"Сохранено: {AppConfig.UserConfigPath}", "ok");
                TxtZoneFamilyHint.Text = $"Сохранено → {AppConfig.UserConfigPath}";
            }
            catch (Exception ex)
            {
                Log(ex.Message, "error");
                MessageBox.Show(ex.Message, "LiraSlabZones", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            var resultKind = result.PatchPreviewOnly
                ? $"пластин: {result.PlateCount}, пятен: {result.Patches.Count}"
                : $"пластин: {result.PlateCount}, зон: {result.Zones.Count}";
            TxtSource.Text = $"{result.DocumentName}\n{resultKind}{elev}";
            TxtTitle.Text = string.IsNullOrWhiteSpace(result.ElevationLabel)
                ? result.DocumentName
                : $"{result.DocumentName}  ·  {result.ElevationLabel}";
            PushToViewport(fitView);
            RefreshStats();
            UpdatePreviewModeControls(result);
            UpdateLayoutGate(result.Settings);
            Log($"Загружено: {result.DocumentName}, {(result.PatchPreviewOnly ? $"пятен {result.Patches.Count}" : $"зон {result.Zones.Count}")}, оси {result.Axes.Count}, контур {result.Outline.Count} т.", "ok");
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
            AttachRevitLink();
            Viewport.SetData(
                _result,
                ReadSettingsFromUi(),
                ChkShowMesh.IsChecked == true,
                ChkShowIso.IsChecked == true,
                ChkShowAxes.IsChecked == true,
                fitView: fitView,
                showPatchBoundaries: ChkShowPatchBoundaries.IsChecked == true);
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
            try
            {
                LoadResult(SlabZoneAnalyzer.LoadJson(dlg.FileName, ReadSettingsFromUi()));
            }
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

        private void BtnBoundaryEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _result.Plates.Count == 0) return;
            try
            {
                var editor = new BoundaryEditorWindow(_result) { Owner = this };
                if (editor.ShowDialog() != true) return;

                _result.Openings = editor.EditedOpenings;
                _result.BoundaryConditions = editor.EditedBoundaries;
                _result.UseCustomOpenings = true;
                RebuildZonesPreservingView(fromUi: false);
                Log($"Геометрия обновлена: отверстий {_result.Openings.Count}, границ {_result.BoundaryConditions.Count}", "ok");
            }
            catch (Exception ex)
            {
                Log($"Ошибка редактора границ и отверстий: {ex}", "error");
                MessageBox.Show(this,
                    $"Не удалось открыть или применить редактор границ и отверстий.\n{ex.Message}",
                    "Ошибка редактора", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnApplyBoundaryRules_Checked(object sender, RoutedEventArgs e)
        {
            UpdateBoundaryRulesToggleLabel();
            if (_suppressUiEvents || _updatingBoundaryToggle) return;
            if (_result == null || _result.Plates.Count == 0) return;
            try
            {
                var current = ReadSettingsFromUi();
                var dialog = new SlabBoundaryRulesWindow(
                    current.SlabThicknessMm, current.CoverBottomMm, current.CoverTopMm)
                { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    SetBoundaryRulesToggle(false);
                    return;
                }

                _suppressUiEvents = true;
                TbThick.Text = dialog.ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture);
                TbCoverBot.Text = dialog.CoverBottomMm.ToString("0.##", CultureInfo.InvariantCulture);
                TbCoverTop.Text = dialog.CoverTopMm.ToString("0.##", CultureInfo.InvariantCulture);
                _suppressUiEvents = false;

                current = ReadSettingsFromUi();
                current.HoleIgnorePerpMm = 0;
                current.ApplySlabBoundaryAndOpeningRules = true;
                current.ApplyHoleRules = true;
                current.ApplyBentRules = true;
                RebuildZonesPreservingView(fromUi: true);
                Log($"Учтены отверстия и контур плиты; толщина {current.SlabThicknessMm:0.##} мм, " +
                    $"защитные слои {current.CoverBottomMm:0.##}/{current.CoverTopMm:0.##} мм", "ok");
            }
            catch (Exception ex)
            {
                _suppressUiEvents = false;
                SetBoundaryRulesToggle(false);
                Log($"Ошибка применения границы плиты и отверстий: {ex}", "error");
                MessageBox.Show(this,
                    $"Не удалось учесть границу плиты и отверстия.\n{ex.Message}",
                    "Ошибка расчёта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnApplyBoundaryRules_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateBoundaryRulesToggleLabel();
            if (_suppressUiEvents || _updatingBoundaryToggle || _result == null) return;
            var settings = ReadSettingsFromUi();
            settings.ApplySlabBoundaryAndOpeningRules = false;
            settings.ApplyHoleRules = false;
            settings.ApplyBentRules = false;
            RebuildZonesPreservingView(fromUi: true);
            Log("Обработка границы плиты и отверстий отключена; зоны пересобраны без подрезки и загибов", "ok");
        }

        private void SetBoundaryRulesToggle(bool enabled)
        {
            _updatingBoundaryToggle = true;
            try { BtnApplyBoundaryRules.IsChecked = enabled; }
            finally { _updatingBoundaryToggle = false; }
            UpdateBoundaryRulesToggleLabel();
        }

        private void UpdateBoundaryRulesToggleLabel()
        {
            if (TxtBoundaryRulesToggle == null) return;
            TxtBoundaryRulesToggle.Text = BtnApplyBoundaryRules.IsChecked == true
                ? "Граница и отверстия: включены"
                : "Учитывать границу и отверстия";
        }

        private static bool TryGetLayerOnlyChanges(
            AnalysisSettings? oldSettings, AnalysisSettings newSettings,
            out HashSet<RebarLayer> changed)
        {
            changed = new HashSet<RebarLayer>();
            if (oldSettings == null) return false;
            if (oldSettings.ShowAs1 != newSettings.ShowAs1 ||
                Math.Abs(oldSettings.AsMainAs1 - newSettings.AsMainAs1) > 1e-9)
                changed.Add(RebarLayer.As1);
            if (oldSettings.ShowAs2 != newSettings.ShowAs2 ||
                Math.Abs(oldSettings.AsMainAs2 - newSettings.AsMainAs2) > 1e-9)
                changed.Add(RebarLayer.As2);
            if (oldSettings.ShowAs3 != newSettings.ShowAs3 ||
                Math.Abs(oldSettings.AsMainAs3 - newSettings.AsMainAs3) > 1e-9)
                changed.Add(RebarLayer.As3);
            if (oldSettings.ShowAs4 != newSettings.ShowAs4 ||
                Math.Abs(oldSettings.AsMainAs4 - newSettings.AsMainAs4) > 1e-9)
                changed.Add(RebarLayer.As4);
            if (oldSettings.BgBottomDiameterMm != newSettings.BgBottomDiameterMm ||
                oldSettings.BgBottomStepMm != newSettings.BgBottomStepMm)
            { changed.Add(RebarLayer.As1); changed.Add(RebarLayer.As2); }
            if (oldSettings.BgTopDiameterMm != newSettings.BgTopDiameterMm ||
                oldSettings.BgTopStepMm != newSettings.BgTopStepMm)
            { changed.Add(RebarLayer.As3); changed.Add(RebarLayer.As4); }
            if (changed.Count == 0) return false;

            var oldJson = JObject.FromObject(oldSettings);
            var newJson = JObject.FromObject(newSettings);
            foreach (var name in new[] { "ShowAs1", "ShowAs2", "ShowAs3", "ShowAs4",
                "AsMainCm2PerM", "AsMainAs1", "AsMainAs2", "AsMainAs3", "AsMainAs4",
                "BgBottomDiameterMm", "BgBottomStepMm", "BgTopDiameterMm", "BgTopStepMm" })
            { oldJson.Remove(name); newJson.Remove(name); }
            return JToken.DeepEquals(oldJson, newJson);
        }

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
            var useCustomOpenings = _result.UseCustomOpenings;
            var boundaries = _result.BoundaryConditions;
            var units = _result.UnitsNote;

            AnalysisResult rebuilt;
            if (TryGetLayerOnlyChanges(_result.Settings, settings, out var changedLayers))
                rebuilt = SlabZoneAnalyzer.RebuildLayers(_result, settings, changedLayers);
            else if (!double.IsNaN(settings.TargetElevationZM))
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
                    openings: openings,
                    useCustomOpenings: useCustomOpenings);
                rebuilt.AllPlates = all;
                rebuilt.AvailableLevels = levels;
            }

            rebuilt.BoundaryConditions = boundaries;
            rebuilt.UnitsNote = units;
            rebuilt.Settings = settings;
            _result = rebuilt;
            _plates = rebuilt.Plates ?? _plates;
            UpdatePreviewModeControls(rebuilt);
            RefreshStats();
            UpdateBackgroundAsLabels(settings);
            UpdateLayoutGate(settings);
            var gate = rebuilt.PatchPreviewOnly || settings.CanLayoutAdditionalZones(out var why)
                ? ""
                : $" | допки: {why}";
            var resultKind = rebuilt.PatchPreviewOnly
                ? $"пластин: {rebuilt.PlateCount}, пятен: {rebuilt.Patches.Count}"
                : $"пластин: {rebuilt.PlateCount}, зон: {rebuilt.Zones.Count}";
            TxtSource.Text = $"{rebuilt.DocumentName}\n{resultKind}" +
                             (string.IsNullOrWhiteSpace(rebuilt.ElevationLabel) ? "" : $"\n{rebuilt.ElevationLabel}");
            PushToViewport(fitView: false);
            var summary = rebuilt.PatchPreviewOnly ? $"пятен {rebuilt.Patches.Count}" : $"зон {rebuilt.Zones.Count}{gate}";
            Log($"Пересчёт: {summary} (вид сохранён)",
                rebuilt.PatchPreviewOnly || settings.CanLayoutAdditionalZones(out _) ? "ok" : "warn");
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
            var boundariesForLevel = rebuilt.BoundaryConditions;
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
                skipLevelFilter: true,
                openings: rebuilt.Openings,
                useCustomOpenings: rebuilt.UseCustomOpenings);
            rebuilt.BoundaryConditions = boundariesForLevel;
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
            var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = _result.PatchPreviewOnly ? "slab_patches.json" : "slab_zones.json"
            };
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
            var zones = Viewport.PlacementZones;
            if (zones.Count == 0)
            {
                Log("Нет зон для размещения в Revit.", "warn");
                return;
            }
            _result.Zones = zones;
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
                ChkShowAxes.IsChecked == true,
                ChkShowPatchBoundaries.IsChecked == true);
        }

        private AnalysisSettings ReadSettingsFromUi()
        {
            double D(string s, double def) =>
                double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : def;
            int I(string s, int def) => int.TryParse(s, out var v) ? v : def;

            var allowedBarSteps = ReadAllowedAdditionalBarSteps();
            var barStep = allowedBarSteps.Contains(200) ? 200 : 100;
            var concrete = ComboIntOrText(CmbConcrete);
            if (concrete == "—" || concrete == "-") concrete = "";

            const bool auto = true;
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

            var baseCfg = _result?.Settings ?? AppConfig.LoadEffectiveSettings();
            var straight = ResolveSelectedFamilyName();
            var gridCellMm = Math.Max(1, I(TbGridCell.Text, 300));
            var minimumZoneWidthM = Math.Max(
                ParseZoneSizeMmToM(TbMinW.Text), gridCellMm / 1000.0);
            TbMinW.Text = FormatZoneSizeMm(minimumZoneWidthM);

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
                MinZoneWidthM = minimumZoneWidthM,
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
                FamilyStraight = straight,
                FamilyL = string.IsNullOrWhiteSpace(TbFamilyL.Text) ? baseCfg.FamilyL : TbFamilyL.Text.Trim(),
                FamilyPEqual = string.IsNullOrWhiteSpace(TbFamilyPEqual.Text) ? baseCfg.FamilyPEqual : TbFamilyPEqual.Text.Trim(),
                FamilyPDiff = string.IsNullOrWhiteSpace(TbFamilyPDiff.Text) ? baseCfg.FamilyPDiff : TbFamilyPDiff.Text.Trim(),
                FamilyBentStick = string.IsNullOrWhiteSpace(TbFamilyBent.Text) ? baseCfg.FamilyBentStick : TbFamilyBent.Text.Trim(),
                AutoLayout = auto,
                PlacementMode = "AutoLayout",
                DetailLevel = detail,
                DetailSlider = detailSlider,
                BarStepMm = barStep,
                ExcludedZoneDiametersMm = _excludedZoneDiameters.OrderBy(d => d).ToList(),
                AllowedAdditionalBarStepsMm = allowedBarSteps.ToList(),
                UseBarStep100 = allowedBarSteps.Contains(100),
                ReverseZoneDirections = ChkReverseDirections.IsChecked == true,
                AveragePatchPeaks = ChkAveragePeaks.IsChecked == true,
                ConcreteClass = concrete,
                GridCellMm = gridCellMm,
                SlabThicknessMm = D(TbThick.Text, 200),
                CoverBottomMm = D(TbCoverBot.Text, 25),
                CoverTopMm = D(TbCoverTop.Text, 25),
                HoleIgnorePerpMm = BtnApplyBoundaryRules.IsChecked == true
                    ? 0
                    : _result?.Settings.HoleIgnorePerpMm ?? 200,
                ApplySlabBoundaryAndOpeningRules = BtnApplyBoundaryRules.IsChecked == true,
                ApplyHoleRules = BtnApplyBoundaryRules.IsChecked == true,
                ApplyBentRules = BtnApplyBoundaryRules.IsChecked == true,
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
            _excludedZoneDiameters.Clear();
            foreach (var diameter in s.ExcludedZoneDiametersMm ?? new List<int>())
                _excludedZoneDiameters.Add(diameter);
            ChkAs1.IsChecked = s.ShowAs1;
            ChkAs2.IsChecked = s.ShowAs2;
            ChkAs3.IsChecked = s.ShowAs3;
            ChkAs4.IsChecked = s.ShowAs4;
            SelectCombo(CmbBgBotD, s.BgBottomDiameterMm > 0 ? s.BgBottomDiameterMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgBotStep, s.BgBottomStepMm > 0 ? s.BgBottomStepMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgTopD, s.BgTopDiameterMm > 0 ? s.BgTopDiameterMm.ToString(CultureInfo.InvariantCulture) : "—");
            SelectCombo(CmbBgTopStep, s.BgTopStepMm > 0 ? s.BgTopStepMm.ToString(CultureInfo.InvariantCulture) : "—");
            UpdateBackgroundAsLabels(s);
            var gridCellMm = Math.Max(1, s.GridCellMm > 0 ? s.GridCellMm : 300);
            TbMinW.Text = FormatZoneSizeMm(Math.Max(s.MinZoneWidthM, gridCellMm / 1000.0));
            TbMaxW.Text = FormatZoneSizeMm(s.MaxZoneWidthM);
            TbMinL.Text = FormatZoneSizeMm(s.MinZoneLengthM);
            TbMinFe.Text = s.MinActiveElements.ToString(CultureInfo.InvariantCulture);
            SldVis.Value = Math.Max(0, Math.Min(25, s.VisualizationScale));
            TxtVis.Text = SldVis.Value.ToString("0", CultureInfo.InvariantCulture);
            SetTransformText(s.RotationDeg, s.OffsetXM, s.OffsetYM);

            ChkAutoLayout.IsChecked = true;
            SldDetail.Value = DetailOptimizer.SliderFromStepIndex(
                DetailOptimizer.StepIndexFromSlider(Math.Max(0, Math.Min(1, s.DetailSlider))));
            TxtDetail.Text = DetailOptimizer.LabelWithMass(SldDetail.Value, 0);
            TbGridCell.Text = gridCellMm.ToString(CultureInfo.InvariantCulture);
            TbThick.Text = s.SlabThicknessMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverBot.Text = s.CoverBottomMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverTop.Text = s.CoverTopMm.ToString("0.##", CultureInfo.InvariantCulture);
            BtnApplyBoundaryRules.IsChecked = s.ApplySlabBoundaryAndOpeningRules;
            UpdateBoundaryRulesToggleLabel();

            var allowedBarSteps = (s.AllowedAdditionalBarStepsMm ?? new List<int>())
                .Where(step => step == 100 || step == 200)
                .Distinct()
                .ToHashSet();
            if (allowedBarSteps.Count == 0)
            {
                allowedBarSteps.Add(200);
                if (s.UseBarStep100) allowedBarSteps.Add(100);
            }
            foreach (var item in BtnAllowedBarSteps.ContextMenu.Items.OfType<MenuItem>())
                item.IsChecked = int.TryParse(item.Tag?.ToString(), out var step) && allowedBarSteps.Contains(step);
            UpdateAllowedBarStepsButtonText();
            ChkReverseDirections.IsChecked = s.ReverseZoneDirections;
            ChkAveragePeaks.IsChecked = s.AveragePatchPeaks;
            SelectCombo(CmbConcrete, string.IsNullOrWhiteSpace(s.ConcreteClass) ? "—" : s.ConcreteClass);
            SelectFamilyInCombo(s.FamilyStraight);
            if (CmbZoneFamily.SelectedIndex < 0)
                CmbZoneFamily.Text = s.FamilyStraight ?? "";
            TbFamilyL.Text = s.FamilyL ?? "";
            TbFamilyPEqual.Text = s.FamilyPEqual ?? "";
            TbFamilyPDiff.Text = s.FamilyPDiff ?? "";
            TbFamilyBent.Text = s.FamilyBentStick ?? "";
            UpdateLayoutGate(s);
        }

        private void BtnAllowedBarSteps_Click(object sender, RoutedEventArgs e)
        {
            var menu = BtnAllowedBarSteps.ContextMenu;
            menu.PlacementTarget = BtnAllowedBarSteps;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void AllowedBarStepsChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            var menuItems = BtnAllowedBarSteps.ContextMenu.Items.OfType<MenuItem>().ToList();
            if (!menuItems.Any(item => item.IsChecked))
            {
                _suppressUiEvents = true;
                ((MenuItem)sender).IsChecked = true;
                _suppressUiEvents = false;
            }
            UpdateAllowedBarStepsButtonText();
            SettingsChanged(sender, e);
        }

        private int[] ReadAllowedAdditionalBarSteps() => BtnAllowedBarSteps.ContextMenu.Items
            .OfType<MenuItem>()
            .Where(item => item.IsChecked && int.TryParse(item.Tag?.ToString(), out _))
            .Select(item => int.Parse(item.Tag!.ToString()!, CultureInfo.InvariantCulture))
            .Where(step => step == 100 || step == 200)
            .Distinct()
            .OrderBy(step => step)
            .DefaultIfEmpty(200)
            .ToArray();

        private void UpdateAllowedBarStepsButtonText()
        {
            var steps = ReadAllowedAdditionalBarSteps();
            BtnAllowedBarSteps.Content = $"Шаги доп. армирования: {string.Join(" / ", steps)} мм";
        }

        private string ResolveSelectedFamilyName()
        {
            var fromUi = SelectedFamilyName;
            if (!string.IsNullOrWhiteSpace(fromUi))
                return AppConfig.StripRfa(fromUi!);
            return _result?.Settings.FamilyStraight
                   ?? AppConfig.LoadEffectiveSettings().FamilyStraight;
        }

        private void SelectFamilyInCombo(string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return;
            for (int i = 0; i < CmbZoneFamily.Items.Count; i++)
            {
                var item = CmbZoneFamily.Items[i]?.ToString() ?? "";
                if (item.Equals(familyName, StringComparison.OrdinalIgnoreCase))
                {
                    CmbZoneFamily.SelectedIndex = i;
                    return;
                }
            }
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
            if (_result?.PatchPreviewOnly == true)
            {
                var missingBottom = (s.ShowAs1 || s.ShowAs2) &&
                                    (s.BgBottomDiameterMm <= 0 || s.BgBottomStepMm <= 0);
                var missingTop = (s.ShowAs3 || s.ShowAs4) &&
                                 (s.BgTopDiameterMm <= 0 || s.BgTopStepMm <= 0);
                if (string.IsNullOrWhiteSpace(s.ConcreteClass))
                {
                    TxtLayoutGate.Text = "Выберите класс бетона для расчёта анкеровки и нахлёстов.";
                    TxtLayoutGate.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09));
                }
                else if (missingBottom || missingTop)
                {
                    TxtLayoutGate.Text = "Задайте эталонные стержни (Ø и шаг) для включённых слоёв.";
                    TxtLayoutGate.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09));
                }
                else
                {
                    TxtLayoutGate.Text = $"Прямые стержни · анкеровка и нахлёст по {s.ConcreteClass} · максимум 11700 мм.";
                    TxtLayoutGate.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D));
                }
                return;
            }
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
            if (_result.PatchPreviewOnly)
            {
                var patches = _result.Patches ?? new List<ZonePatch>();
                var uniqueElements = patches.SelectMany(patch => patch.ElementIds).Distinct().Count();
                var cellCount = patches.Sum(patch => patch.Cells.Count);
                TxtStats.Text =
                    $"Отметка: {elev}\n" +
                    $"Режим: связные пятна / {DetailOptimizer.StepLabel(DetailOptimizer.StepIndexFromSlider(_result.Settings.DetailSlider))}\n" +
                    $"КЭ уровня: {_result.PlateCount}\n" +
                    $"Мозаика: {_result.Settings.GridCellMm} × {_result.Settings.GridCellMm} мм\n" +
                    "Подписи КЭ: требуемая добавка As, см²/м\n" +
                    $"Пятна: {patches.Count}\n" +
                    $"Зоны: {Viewport.EditableZoneCount}\n" +
                    $"Ячейки в пятнах: {cellCount}\n" +
                    $"КЭ в пятнах (уник.): {uniqueElements}\n" +
                    $"As1: {patches.Count(patch => patch.Layer == RebarLayer.As1)}\n" +
                    $"As2: {patches.Count(patch => patch.Layer == RebarLayer.As2)}\n" +
                    $"As3: {patches.Count(patch => patch.Layer == RebarLayer.As3)}\n" +
                    $"As4: {patches.Count(patch => patch.Layer == RebarLayer.As4)}\n" +
                    $"Контур: {_result.Outline.Count} вершин\n" +
                    $"Отверстия: {_result.Openings.Count}" +
                    (_result.RevitLink?.Grids.Count > 0
                        ? $"\nПривязка к осям Revit не кратна 10 мм: {_result.Diagnostics?.AxisTieCount ?? 0}"
                        : "");
                TxtDetail.Text = DetailOptimizer.StepLabel(
                    DetailOptimizer.StepIndexFromSlider(_result.Settings.DetailSlider));
                return;
            }

            var mode = _result.Settings.AutoLayout ? "Автораскладка" : "По КЭ";
            var overlong = _result.Zones.Count(RebarTables.ExceedsMaxBarLength);
            var diagnostics = _result.Diagnostics ?? new ZoneDiagnostics();
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
                $"Непокрытые КЭ: {diagnostics.UncoveredCount}\n" +
                $"Пустые зоны: {diagnostics.EmptyZoneCount}\n" +
                $"Конфликты зон: {diagnostics.ConflictCount}\n" +
                (_result.RevitLink?.Grids.Count > 0
                    ? $"Привязка к осям Revit не кратна 10 мм: {diagnostics.AxisTieCount}\n"
                    : "") +
                (overlong > 0 ? $"Превышение 11700 мм: {overlong} (размещение заблокировано)\n" : "") +
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
