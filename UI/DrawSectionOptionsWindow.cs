using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.UI;

/// <summary>Modal drawing options dialog hosted by AutoCAD.</summary>
public sealed class DrawSectionOptionsWindow : Window
{
    private readonly CheckBox _corridor = new() { Content = "Links del corredor", IsChecked = true };
    private readonly CheckBox _terrain = new() { Content = "Terreno", IsChecked = true };
    private readonly CheckBox _axis = new() { Content = "Eje", IsChecked = true };
    private readonly CheckBox _shapes = new() { Content = "Shapes / firme", IsChecked = true };
    private readonly CheckBox _earthwork = new() { Content = "Desmonte y terraplén", IsChecked = true };
    private readonly CheckBox _grid = new() { Content = "Cuadrícula", IsChecked = true };
    private readonly CheckBox _labels = new() { Content = "Rótulos", IsChecked = true };
    private readonly TextBox _horizontalScale = new() { Text = "100", Width = 80 };
    private readonly TextBox _verticalFactor = new() { Text = "1", Width = 80 };
    private readonly TextBox _referenceElevation = new() { Width = 100 };
    private readonly RadioButton _singleMode = new() { Content = "Solo la sección actual", IsChecked = true, GroupName = "Mode" };
    private readonly RadioButton _rangeMode = new() { Content = "Rango de PK", GroupName = "Mode" };
    private readonly TextBox _rangeStart = new() { Width = 100 };
    private readonly TextBox _rangeEnd = new() { Width = 100 };
    private readonly TextBox _rangeInterval = new() { Text = "10", Width = 100 };
    private readonly TextBox _columns = new() { Text = "3", Width = 100 };
    private readonly TextBox _spacingX = new() { Text = "20", Width = 100 };
    private readonly TextBox _spacingY = new() { Text = "15", Width = 100 };
    public DrawOptions? Options { get; private set; }

    public DrawSectionOptionsWindow(SectionDrawRequest request)
    {
        Title = "Opciones de dibujo de sección"; Width = 370; Height = 560; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = System.Windows.Media.Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Elementos a dibujar", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (CheckBox check in new[] { _corridor, _terrain, _axis, _shapes, _earthwork, _grid, _labels }) panel.Children.Add(check);
        panel.Children.Add(MakeRow("Escala horizontal 1:", _horizontalScale));
        panel.Children.Add(MakeRow("Exageración vertical:", _verticalFactor));
        double minimum = request.Section.Geometry.AllPoints.Concat(request.Terrain.AllPoints).Select(p => p.Elevation).DefaultIfEmpty(0).Min();
        _referenceElevation.Text = (Math.Floor(minimum / 5.0) * 5.0).ToString("0.###", CultureInfo.InvariantCulture);
        panel.Children.Add(MakeRow("Cota de referencia:", _referenceElevation));
        _rangeStart.Text = request.Section.Station.ToString("0.###", CultureInfo.InvariantCulture);
        _rangeEnd.Text = _rangeStart.Text;
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 5) });
        panel.Children.Add(_singleMode); panel.Children.Add(_rangeMode);
        panel.Children.Add(MakeRow("PK inicial:", _rangeStart));
        panel.Children.Add(MakeRow("PK final:", _rangeEnd));
        panel.Children.Add(MakeRow("Intervalo:", _rangeInterval));
        panel.Children.Add(MakeRow("Columnas:", _columns));
        panel.Children.Add(MakeRow("Separación X:", _spacingX));
        panel.Children.Add(MakeRow("Separación Y:", _spacingY));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Dibujar", Padding = new Thickness(12, 5, 12, 5), IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }

    private static UIElement MakeRow(string label, Control control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(control);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static bool TryParseStation(string text, out double station)
    {
        station = 0; string value = text.Trim(); int plus = value.IndexOf('+');
        if (plus < 0) return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out station) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out station);
        if (!double.TryParse(value[..plus], NumberStyles.Float, CultureInfo.InvariantCulture, out double km) ||
            !(double.TryParse(value[(plus + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double remainder) || double.TryParse(value[(plus + 1)..], NumberStyles.Float, CultureInfo.CurrentCulture, out remainder)) || remainder < 0 || remainder >= 1000) return false;
        station = Math.Sign(km) * (Math.Abs(km) * 1000 + remainder); return double.IsFinite(station);
    }

    private void Accept()
    {
        if (!double.TryParse(_horizontalScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) || denominator <= 0 ||
            !double.TryParse(_verticalFactor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double vertical) || vertical <= 0 ||
            !double.TryParse(_referenceElevation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double reference) ||
            !TryParseStation(_rangeStart.Text, out double rangeStart) ||
            !TryParseStation(_rangeEnd.Text, out double rangeEnd) ||
            !double.TryParse(_rangeInterval.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double interval) ||
            !int.TryParse(_columns.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int columns) ||
            !double.TryParse(_spacingX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double spacingX) ||
            !double.TryParse(_spacingY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double spacingY) ||
            rangeStart > rangeEnd || interval <= 0 || columns <= 0 || spacingX <= 0 || spacingY <= 0)
        {
            MessageBox.Show(this, "Introduzca una escala, exageración y cota de referencia válidas.", "Datos no válidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Options = new DrawOptions(_corridor.IsChecked == true, _terrain.IsChecked == true, _axis.IsChecked == true,
            _shapes.IsChecked == true, _earthwork.IsChecked == true, _grid.IsChecked == true, _labels.IsChecked == true,
            1.0 / denominator, vertical, reference, _rangeMode.IsChecked == true, rangeStart, rangeEnd, interval, columns, spacingX, spacingY);
        DialogResult = true;
    }
}
