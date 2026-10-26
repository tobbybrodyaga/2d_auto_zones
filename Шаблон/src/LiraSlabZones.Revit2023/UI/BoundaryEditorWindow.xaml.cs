using System.Linq;
using System.Windows;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023.UI
{
    public partial class BoundaryEditorWindow : Window
    {
        public BoundaryEditorWindow(AnalysisResult source)
        {
            InitializeComponent();
            EditedOpenings = source.Openings.Select(Clone).ToList();
            EditedBoundaries = source.BoundaryConditions.Select(Clone).ToList();
            Viewport.SetData(source.Plates, source.Outline, EditedOpenings, EditedBoundaries);
            Viewport.SelectionChanged += RefreshSelection;
            Viewport.StatusChanged += message => TxtStatus.Text = message;
            Viewport.GeometryChanged += RefreshSelection;
            RefreshSelection();
        }

        public System.Collections.Generic.List<OpeningInfo> EditedOpenings { get; }
        public System.Collections.Generic.List<BoundaryConditionInfo> EditedBoundaries { get; }

        private static OpeningInfo Clone(OpeningInfo source) => new OpeningInfo
        {
            OpeningId = source.OpeningId,
            MinXM = source.MinXM,
            MaxXM = source.MaxXM,
            MinYM = source.MinYM,
            MaxYM = source.MaxYM,
            ElementIds = source.ElementIds?.Distinct().ToList() ?? new System.Collections.Generic.List<int>(),
            MeshAnchors = source.MeshAnchors?.Select(anchor => new MeshVertexAnchor
            {
                ElementId = anchor.ElementId,
                VertexIndex = anchor.VertexIndex,
                Position = anchor.Position == null ? new Point3() :
                    new Point3(anchor.Position.X, anchor.Position.Y, anchor.Position.Z)
            }).ToList() ?? new System.Collections.Generic.List<MeshVertexAnchor>()
        };

        private static BoundaryConditionInfo Clone(BoundaryConditionInfo source) => new BoundaryConditionInfo
        {
            BoundaryId = source.BoundaryId,
            Name = source.Name,
            Edges = source.Edges?.Select(edge => new MeshEdgeAnchor
            {
                ElementId = edge.ElementId,
                EdgeIndex = edge.EdgeIndex,
                Start = edge.Start == null ? new Point3() : new Point3(edge.Start.X, edge.Start.Y, edge.Start.Z),
                End = edge.End == null ? new Point3() : new Point3(edge.End.X, edge.End.Y, edge.End.Z)
            }).ToList() ?? new System.Collections.Generic.List<MeshEdgeAnchor>()
        };

        private void BtnSelect_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.Select);
        private void BtnAddOpening_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.AddOpening);
        private void BtnMoveOpening_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.MoveOpening);
        private void BtnResizeOpening_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.ResizeOpening);
        private void BtnAddBoundary_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.AddBoundary);
        private void BtnAddOuterBoundary_Click(object sender, RoutedEventArgs e) => Viewport.SetMode(BoundaryEditMode.AddOuterBoundary);
        private void BtnDelete_Click(object sender, RoutedEventArgs e) => Viewport.DeleteSelected();
        private void BtnFit_Click(object sender, RoutedEventArgs e) => Viewport.FitToView();
        private void MeshChanged(object sender, RoutedEventArgs e)
        {
            if (Viewport != null)
                Viewport.ShowMesh = ChkMesh.IsChecked == true;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RefreshSelection()
        {
            var opening = Viewport.SelectedOpening;
            var boundary = Viewport.SelectedBoundary;
            if (opening != null)
            {
                TxtSelection.Text = $"Отверстие #{opening.OpeningId}\n" +
                    $"Размер: {opening.WidthM * 1000:0} × {opening.HeightM * 1000:0} мм\n" +
                    $"Привязано КЭ: {opening.ElementIds.Count}";
            }
            else if (boundary != null)
            {
                TxtSelection.Text = $"Граница #{boundary.BoundaryId}\n" +
                    $"Рёбер КЭ: {boundary.Edges.Count}\n" +
                    string.Join(", ", boundary.Edges.Select(edge => edge.ElementId).Distinct().Take(8).Select(id => $"КЭ {id}"));
            }
            else
                TxtSelection.Text = "Выберите отверстие или границу";

            TxtCounts.Text = $"Отверстий: {EditedOpenings.Count}\nДобавленных границ: {EditedBoundaries.Count}\n" +
                $"КЭ схемы: {Viewport.PlateCount}";
        }
    }
}
