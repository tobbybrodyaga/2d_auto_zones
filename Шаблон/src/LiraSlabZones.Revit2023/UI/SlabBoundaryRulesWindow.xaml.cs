using System.Globalization;
using System.Windows;

namespace LiraSlabZones.Revit2023.UI
{
    public partial class SlabBoundaryRulesWindow : Window
    {
        public SlabBoundaryRulesWindow(double thicknessMm, double coverBottomMm, double coverTopMm)
        {
            InitializeComponent();
            TbThickness.Text = thicknessMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverBottom.Text = coverBottomMm.ToString("0.##", CultureInfo.InvariantCulture);
            TbCoverTop.Text = coverTopMm.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public double ThicknessMm { get; private set; }
        public double CoverBottomMm { get; private set; }
        public double CoverTopMm { get; private set; }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(TbThickness.Text, out var thickness) || thickness <= 0 ||
                !TryRead(TbCoverBottom.Text, out var coverBottom) || coverBottom < 0 ||
                !TryRead(TbCoverTop.Text, out var coverTop) || coverTop < 0)
            {
                TxtValidation.Text = "Введите толщину больше нуля и неотрицательные защитные слои.";
                return;
            }
            if (coverBottom + coverTop >= thickness)
            {
                TxtValidation.Text = "Сумма защитных слоёв должна быть меньше толщины плиты.";
                return;
            }

            ThicknessMm = thickness;
            CoverBottomMm = coverBottom;
            CoverTopMm = coverTop;
            DialogResult = true;
        }

        private static bool TryRead(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }
}
