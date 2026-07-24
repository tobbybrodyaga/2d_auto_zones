using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using LiraSapr;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Чтение геометрии схемы через COM LiraSapr (LiraAPI таблиц ввода).
    /// </summary>
    public sealed class LiraGeometryReader : IDisposable
    {
        private LiraApplication? _app;
        private LiraDocument? _doc;
        private bool _ownedApp;

        // Типы стержней (исключаем). Пластины: любые КЭ с 3–4 узлами.
        private static readonly HashSet<int> BarTypeCodes = new HashSet<int>
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 16
        };

        private static bool IsPlateElement(int typeCode, int nodeCount) =>
            nodeCount >= 3 && nodeCount <= 4 && !BarTypeCodes.Contains(typeCode);

        public string DocumentName => _doc?.Title ?? string.Empty;
        public string DocumentPath => _doc?.PathName ?? string.Empty;

        public void AttachToRunningOrOpen(string? lirPath)
        {
            _app = TryGetRunningApplication();
            if (_app == null)
            {
                _app = new LiraApplication();
                _ownedApp = true;
            }

            if (!string.IsNullOrWhiteSpace(lirPath))
            {
                string msgs = string.Empty;
                _doc = _app.OpenDocument(lirPath, true, true, ref msgs);
                if (_doc == null)
                    throw new InvalidOperationException("Не удалось открыть файл ЛИРА: " + lirPath + Environment.NewLine + msgs);
                return;
            }

            _doc = _app.ActiveDocument;
            if (_doc == null && _app.DocumentCount > 0)
                _doc = _app.get_Document(0);

            if (_doc == null)
                throw new InvalidOperationException(
                    "Нет активной схемы в ЛИРА-САПР. Откройте .lir или укажите путь к файлу.");
        }

        private static LiraApplication? TryGetRunningApplication()
        {
            foreach (var progId in new[] { "LiraSapr.Application.2024", "LiraSapr.Application" })
            {
                try
                {
                    var obj = Marshal.GetActiveObject(progId);
                    if (obj is LiraApplication app)
                        return app;
                    return (LiraApplication)obj;
                }
                catch (COMException)
                {
                    // нет запущенного экземпляра
                }
            }

            return null;
        }

        public LiraModelPartEnum ModelPart { get; set; } = LiraModelPartEnum.kLiraModelPart_Visible;

        public static LiraModelPartEnum ParseModelPart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return LiraModelPartEnum.kLiraModelPart_Visible;
            switch (value.Trim().ToLowerInvariant())
            {
                case "all":
                    return LiraModelPartEnum.kLiraModelPart_All;
                case "selected":
                    return LiraModelPartEnum.kLiraModelPart_Selected;
                default:
                    return LiraModelPartEnum.kLiraModelPart_Visible;
            }
        }

        /// <summary>
        /// Для All — снять фрагментацию. Для Visible — оставить текущий видимый фрагмент как есть.
        /// </summary>
        private void PrepareModelPart()
        {
            if (_app == null) return;
            if (ModelPart != LiraModelPartEnum.kLiraModelPart_All)
                return;

            try
            {
                _app.StartCmd(LiraCmdIdEnum.kLiraCmdId_RestoreModel);
            }
            catch
            {
                // ignore
            }
        }

        private bool _modelPrepared;

        private void PrepareModelPartOnce()
        {
            if (_modelPrepared) return;
            PrepareModelPart();
            _modelPrepared = true;
        }

        /// <summary>
        /// Узлы + пластины видимого фрагмента за один проход подготовки модели.
        /// </summary>
        public (Dictionary<int, LiraNode> Nodes, List<LiraPlateElement> Plates) ReadNodesAndPlates()
        {
            EnsureDoc();
            PrepareModelPartOnce();

            var nodes = ReadNodesCore();
            var plates = ReadPlateElementsCore(nodes);
            return (nodes, plates);
        }

        public string LastAxesDiagnostics { get; private set; } = string.Empty;

        public List<ConstructionAxis> ReadConstructionAxes()
        {
            EnsureDoc();
            PrepareModelPartOnce();
            var axes = new List<ConstructionAxis>();
            int rowsTotal = 0, skipped = 0;
            try
            {
                var table = _doc!.AllTables.CreateNewItem(LiraTableEnum.kLiraTable_ConstructionAxes) as LiraTable;
                if (table == null)
                {
                    LastAxesDiagnostics = "таблица ConstructionAxes недоступна";
                    return axes;
                }
                table.RefillFromModel();
                object data = null!;
                table.GetContents(ref data);

                void AddRow(IList<object> row)
                {
                    rowsTotal++;
                    if (row.Count < 2) { skipped++; return; }

                    string name = Convert.ToString(row[0])?.Trim() ?? "";
                    // заголовок
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Equals("Имя", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("№") ||
                        name.Equals("N", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        return;
                    }

                    var nums = new List<(int Col, double V)>();
                    string? typeTok = null;
                    for (int c = 1; c < row.Count; c++)
                    {
                        var s = Convert.ToString(row[c])?.Trim() ?? "";
                        if (string.IsNullOrEmpty(s)) continue;
                        if (IsAxisTypeToken(s))
                        {
                            typeTok = s;
                            continue;
                        }
                        if (TryDouble(row[c], out var v))
                            nums.Add((c, v));
                    }

                    // Формат отрезка: X1 Y1 X2 Y2 (± доп. числа)
                    if (nums.Count >= 4)
                    {
                        double x1 = nums[0].V, y1 = nums[1].V, x2 = nums[2].V, y2 = nums[3].V;
                        double dx = x2 - x1, dy = y2 - y1;
                        bool vertical = Math.Abs(dx) <= Math.Abs(dy); // ближе к вертикали (x≈const)
                        // уточнение: если явно задан тип — уважаем его
                        if (typeTok != null)
                            vertical = IsVerticalType(typeTok);

                        axes.Add(new ConstructionAxis
                        {
                            Name = name,
                            IsSegment = true,
                            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                            Vertical = vertical,
                            Position = vertical ? (x1 + x2) * 0.5 : (y1 + y2) * 0.5
                        });
                        return;
                    }

                    // Формат: тип + координата (или только координата)
                    if (nums.Count >= 1)
                    {
                        bool vertical = true;
                        if (typeTok != null)
                            vertical = IsVerticalType(typeTok);
                        else if (nums.Count >= 2 && (Math.Abs(nums[0].V - 1) < 1e-9 || Math.Abs(nums[0].V - 2) < 1e-9))
                        {
                            // первый «number» на самом деле код типа 1/2
                            vertical = Math.Abs(nums[0].V - 1) < 1e-9;
                            axes.Add(new ConstructionAxis
                            {
                                Name = name,
                                Vertical = vertical,
                                Position = nums[1].V
                            });
                            return;
                        }
                        else
                        {
                            // эвристика по имени: цифры → обычно вертикальные (поперечные)
                            vertical = name.Any(char.IsDigit) && !name.Any(ch => ch >= 'А' && ch <= 'Я' || ch >= 'A' && ch <= 'Z');
                            if (name.Length == 1 && char.IsLetter(name[0]))
                                vertical = false; // А, Б, В — продольные (Y=const)
                        }

                        axes.Add(new ConstructionAxis
                        {
                            Name = name,
                            Vertical = vertical,
                            Position = nums[0].V
                        });
                        return;
                    }

                    skipped++;
                }

                if (data is object[,] arr)
                {
                    int rows = arr.GetLength(0);
                    int cols = arr.GetLength(1);
                    for (int r = 0; r < rows; r++)
                    {
                        var row = new List<object>(cols);
                        for (int c = 0; c < cols; c++) row.Add(arr[r, c]);
                        AddRow(row);
                    }
                }
                else
                {
                    foreach (var row in NormalizeRows(data))
                        AddRow(row);
                }

                // нормализация единиц: если координаты осей в мм, а узлы в м
                NormalizeAxisUnits(axes);
                // убрать дубликаты (имя+позиция)
                axes = DedupAxes(axes);

                LastAxesDiagnostics = $"axes rows={rowsTotal} ok={axes.Count} skip={skipped}";
            }
            catch (Exception ex)
            {
                LastAxesDiagnostics = "axes error: " + ex.Message;
            }

            return axes;
        }

        private static bool IsAxisTypeToken(string s)
        {
            s = s.Trim();
            if (s.Equals("X", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                s == "1" || s == "2" || s == "0" ||
                s.Equals("V", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("H", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("верт", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("гор", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("попер", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("прод", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool IsVerticalType(string t)
        {
            t = t.Trim();
            if (t.Equals("Y", StringComparison.OrdinalIgnoreCase) || t == "2" ||
                t.Equals("H", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("гор", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("прод", StringComparison.OrdinalIgnoreCase))
                return false;
            // X, 1, 0, V, верт, попер → вертикальная (x=const)
            return true;
        }

        private static void NormalizeAxisUnits(List<ConstructionAxis> axes)
        {
            if (axes.Count == 0) return;
            double maxAbs = 0;
            foreach (var a in axes)
            {
                if (a.IsSegment)
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(a.X1));
                    maxAbs = Math.Max(maxAbs, Math.Abs(a.Y1));
                    maxAbs = Math.Max(maxAbs, Math.Abs(a.X2));
                    maxAbs = Math.Max(maxAbs, Math.Abs(a.Y2));
                }
                else
                    maxAbs = Math.Max(maxAbs, Math.Abs(a.Position));
            }
            // узлы обычно в метрах (десятки–сотни); мм → тысячи–десятки тысяч
            if (maxAbs < 500) return;
            const double k = 0.001;
            foreach (var a in axes)
            {
                a.Position *= k;
                a.X1 *= k; a.Y1 *= k; a.X2 *= k; a.Y2 *= k;
            }
        }

        private static List<ConstructionAxis> DedupAxes(List<ConstructionAxis> axes)
        {
            var result = new List<ConstructionAxis>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in axes)
            {
                string key = a.IsSegment
                    ? $"{a.Name}|s|{a.X1:F3}|{a.Y1:F3}|{a.X2:F3}|{a.Y2:F3}"
                    : $"{a.Name}|{(a.Vertical ? "V" : "H")}|{a.Position:F3}";
                if (!seen.Add(key)) continue;
                result.Add(a);
            }
            return result;
        }

        public List<(string Name, double Z)> ReadElevationMarks()
        {
            EnsureDoc();
            PrepareModelPartOnce();
            var list = new List<(string, double)>();
            try
            {
                var table = _doc!.AllTables.CreateNewItem(LiraTableEnum.kLiraTable_ElevationMarks) as LiraTable;
                if (table == null) return list;
                table.RefillFromModel();
                object data = null!;
                table.GetContents(ref data);

                void AddRow(IList<object> row)
                {
                    if (row.Count < 2) return;
                    string name = Convert.ToString(row[0])?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Equals("Имя", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("№"))
                        return;

                    // собираем все числа: часто Z + возможно X,Y — берём значение, похожее на отметку
                    var nums = new List<double>();
                    for (int c = 1; c < row.Count; c++)
                    {
                        if (TryDouble(row[c], out var z)) nums.Add(z);
                    }
                    if (nums.Count == 0) return;

                    // если есть число в «разумном» диапазоне отметок (−50…500 м) — предпочитаем его
                    double chosen = nums[0];
                    foreach (var n in nums)
                    {
                        double m = Math.Abs(n) > 500 ? n * 0.001 : n;
                        if (m > -50 && m < 500) { chosen = Math.Abs(n) > 500 ? n * 0.001 : n; break; }
                    }
                    if (Math.Abs(chosen) > 500) chosen *= 0.001;
                    list.Add((name, chosen));
                }

                if (data is object[,] arr)
                {
                    int rows = arr.GetLength(0);
                    int cols = arr.GetLength(1);
                    for (int r = 0; r < rows; r++)
                    {
                        var row = new List<object>(cols);
                        for (int c = 0; c < cols; c++) row.Add(arr[r, c]);
                        AddRow(row);
                    }
                }
                else
                {
                    foreach (var row in NormalizeRows(data))
                        AddRow(row);
                }
            }
            catch { /* optional */ }

            return list;
        }

        public static string FormatElevationLabel(double zM, IList<(string Name, double Z)>? marks, double tol = 0.05)
        {
            if (marks != null)
            {
                foreach (var (name, z) in marks)
                {
                    if (Math.Abs(z - zM) <= tol && !string.IsNullOrWhiteSpace(name))
                        return $"{name} (Z={zM:F3} м)";
                }
            }
            return $"Z = {zM:F3} м";
        }

        /// <summary>Только отметки (для кнопки «уровни из ЛИРЫ» без полного анализа).</summary>
        public List<(string Name, double Z)> PeekElevationMarks(string? lirPath = null)
        {
            AttachToRunningOrOpen(lirPath);
            return ReadElevationMarks();
        }

        public Dictionary<int, LiraNode> ReadNodes()
        {
            EnsureDoc();
            PrepareModelPartOnce();
            return ReadNodesCore();
        }

        public List<LiraPlateElement> ReadPlateElements(Dictionary<int, LiraNode> nodes)
        {
            EnsureDoc();
            PrepareModelPartOnce();
            return ReadPlateElementsCore(nodes);
        }

        private Dictionary<int, LiraNode> ReadNodesCore()
        {
            var table = _doc!.AllTables.CreateNewItem(LiraTableEnum.kLiraTable_Nodes_Coordinates) as LiraTable
                        ?? throw new InvalidOperationException("Не удалось создать таблицу узлов.");
            table.RefillFromModel();

            object data = null!;
            table.GetContents(ref data);

            var nodes = new Dictionary<int, LiraNode>(4096);
            if (data is object[,] arr)
            {
                int rows = arr.GetLength(0);
                int cols = arr.GetLength(1);
                for (int r = 0; r < rows; r++)
                {
                    if (cols < 4) continue;
                    if (!TryInt(arr[r, 0], out var id)) continue;
                    if (!TryDouble(arr[r, 1], out var x)) continue;
                    if (!TryDouble(arr[r, 2], out var y)) continue;
                    if (!TryDouble(arr[r, 3], out var z)) continue;
                    nodes[id] = new LiraNode { Id = id, Coord = new Point3(x, y, z) };
                }
                return nodes;
            }

            foreach (var row in NormalizeRows(data))
            {
                if (row.Count < 4) continue;
                if (!TryInt(row[0], out var id)) continue;
                if (!TryDouble(row[1], out var x)) continue;
                if (!TryDouble(row[2], out var y)) continue;
                if (!TryDouble(row[3], out var z)) continue;
                nodes[id] = new LiraNode { Id = id, Coord = new Point3(x, y, z) };
            }
            return nodes;
        }

        private List<LiraPlateElement> ReadPlateElementsCore(Dictionary<int, LiraNode> nodes)
        {
            var table = _doc!.AllTables.CreateNewItem(LiraTableEnum.kLiraTable_Elements_TypeAndNodes) as LiraTable
                        ?? throw new InvalidOperationException("Не удалось создать таблицу элементов.");
            table.RefillFromModel();

            object data = null!;
            table.GetContents(ref data);

            var plates = new List<LiraPlateElement>(2048);

            void ConsumeRow(object c0, object c1, object c2)
            {
                if (!TryInt(c0, out var id)) return;
                if (!TryInt(c1, out var typeCode)) return;
                var nodeIds = ParseNodeList(c2);
                if (!IsPlateElement(typeCode, nodeIds.Count)) return;

                var contour = new List<Point3>(nodeIds.Count);
                foreach (var nid in nodeIds)
                {
                    if (!nodes.TryGetValue(nid, out var n)) continue;
                    contour.Add(n.Coord);
                }
                if (contour.Count < 3) return;

                contour = ContourFix.OrderAsSimplePolygon(contour);
                ContourFix.EdgeAlignedSize(contour, out var w, out var len);

                double sx2 = 0, sy2 = 0, sz2 = 0;
                foreach (var p in contour) { sx2 += p.X; sy2 += p.Y; sz2 += p.Z; }
                double inv = 1.0 / contour.Count;
                plates.Add(new LiraPlateElement
                {
                    Id = id,
                    TypeCode = typeCode,
                    NodeIds = nodeIds,
                    Contour = contour,
                    Centroid = new Point3(sx2 * inv, sy2 * inv, sz2 * inv),
                    WidthM = w,
                    LengthM = len
                });
            }

            if (data is object[,] arr)
            {
                int rows = arr.GetLength(0);
                int cols = arr.GetLength(1);
                if (cols >= 3)
                {
                    for (int r = 0; r < rows; r++)
                        ConsumeRow(arr[r, 0], arr[r, 1], arr[r, 2]);
                }
                return plates;
            }

            foreach (var row in NormalizeRows(data))
            {
                if (row.Count < 3) continue;
                ConsumeRow(row[0], row[1], row[2]);
            }

            return plates;
        }

        private void EnsureDoc()
        {
            if (_doc == null)
                throw new InvalidOperationException("Документ ЛИРА не подключен.");
        }

        private static List<List<object>> NormalizeRows(object data)
        {
            var result = new List<List<object>>();
            if (data == null) return result;

            if (data is object[,] arr2)
            {
                int rows = arr2.GetLength(0);
                int cols = arr2.GetLength(1);
                for (int r = 0; r < rows; r++)
                {
                    var row = new List<object>(cols);
                    for (int c = 0; c < cols; c++)
                        row.Add(arr2[r, c]);
                    result.Add(row);
                }
                return result;
            }

            if (data is Array arr1 && arr1.Rank == 1)
            {
                foreach (var item in arr1)
                {
                    if (item is Array nested)
                    {
                        var row = new List<object>();
                        foreach (var v in nested) row.Add(v);
                        result.Add(row);
                    }
                    else if (item != null)
                    {
                        var parts = item.ToString()!
                            .Split(new[] { '\t', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                        result.Add(parts.Cast<object>().ToList());
                    }
                }
                return result;
            }

            var text = Convert.ToString(data, CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { '\t', ';' }, StringSplitOptions.None);
                result.Add(parts.Cast<object>().ToList());
            }

            return result;
        }

        private static List<int> ParseNodeList(object raw)
        {
            var s = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            var ids = new List<int>();
            foreach (var part in s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryInt(part, out var id))
                    ids.Add(id);
            }
            return ids;
        }

        private static bool TryInt(object? v, out int value)
        {
            value = 0;
            if (v == null) return false;
            if (v is int i) { value = i; return true; }
            if (v is short sh) { value = sh; return true; }
            if (v is long l) { value = (int)l; return true; }
            if (v is double d) { value = (int)Math.Round(d); return true; }
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryDouble(object? v, out double value)
        {
            value = 0;
            if (v == null) return false;
            if (v is double d) { value = d; return true; }
            if (v is float f) { value = f; return true; }
            if (v is int i) { value = i; return true; }
            var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        public void Dispose()
        {
            if (_doc != null)
            {
                try { Marshal.FinalReleaseComObject(_doc); } catch { /* ignore */ }
                _doc = null;
            }

            if (_ownedApp && _app != null)
            {
                try { Marshal.FinalReleaseComObject(_app); } catch { /* ignore */ }
                _app = null;
            }
        }
    }
}
