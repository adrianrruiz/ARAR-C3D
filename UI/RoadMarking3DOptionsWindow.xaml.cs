using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.UI;

public partial class RoadMarking3DOptionsWindow : Window
{
    public RoadMarking3DOptionsWindow(IReadOnlyList<RoadSurfaceChoice> surfaces, RoadMarkingSettings? firstSettings,
        double? firstPolylineWidth, bool requiresSurface, string widthDisplayText)
    {
        InitializeComponent();
        _displayWidthMeters = firstPolylineWidth ?? firstSettings?.WidthMeters ?? 0.10;
        SurfaceCombo.ItemsSource = surfaces;
        SurfaceCombo.SelectedIndex = requiresSurface ? 0 : -1;
        SurfaceCombo.IsEnabled = requiresSurface;
        WidthText.Text = widthDisplayText;
        WidthText.IsReadOnly = true;
        if (firstSettings is not null)
        {
            LinetypeCombo.SelectedIndex = firstSettings.Dashed ? 1 : 0;
            DashText.Text = firstSettings.DashMeters.ToString("0.###", CultureInfo.CurrentCulture);
            GapText.Text = firstSettings.GapMeters.ToString("0.###", CultureInfo.CurrentCulture);
        }
        else LinetypeCombo.SelectedIndex = 0;
        UpdateVisibility();
    }

    private readonly double _displayWidthMeters;

    public Autodesk.AutoCAD.DatabaseServices.ObjectId? SurfaceId => (SurfaceCombo.SelectedItem as RoadSurfaceChoice)?.Id;
    public RoadMarkingSettings? Settings { get; private set; }
    public bool KeepIntermediate => KeepIntermediateBox.IsChecked == true;
    public bool ReverseDirection => ReverseBox.IsChecked == true;

    private void LinetypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void UpdateVisibility()
    {
        bool dashed = (LinetypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Dashed";
        DashLabel.Visibility = DashText.Visibility = dashed ? Visibility.Visible : Visibility.Collapsed;
        GapLabel.Visibility = GapText.Visibility = dashed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        bool dashed = (LinetypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Dashed";
        double dash = 4, gap = 3;
        if (dashed && !TryPositive(DashText, "La longitud de raya debe ser mayor que cero.", out dash)) return;
        if (dashed && !TryPositive(GapText, "El espacio entre rayas debe ser mayor que cero.", out gap)) return;
        if (SurfaceCombo.IsEnabled && SurfaceId is null)
        {
            ValidationText.Text = "Seleccione una superficie TIN para elevar las polilíneas 2D.";
            return;
        }
        Settings = new RoadMarkingSettings { WidthMeters = _displayWidthMeters, Dashed = dashed, DashMeters = dash, GapMeters = gap };
        DialogResult = true;
    }

    private bool TryPositive(TextBox textBox, string message, out double value)
    {
        if (double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value > 0)
        {
            ValidationText.Text = string.Empty;
            return true;
        }
        ValidationText.Text = message;
        textBox.Focus(); textBox.SelectAll();
        return false;
    }
}
