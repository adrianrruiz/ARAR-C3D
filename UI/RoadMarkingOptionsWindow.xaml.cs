using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ARAR.Civil3D.Models;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;

namespace ARAR.Civil3D.UI;

public partial class RoadMarkingOptionsWindow : Window
{
    private bool _loading;
    private short _colorIndex = 7;
    private byte _red = 255, _green = 255, _blue = 255;
    private bool _isTrueColor;

    public RoadMarkingOptionsWindow(RoadMarkingSettings initial)
    {
        // XAML can raise SelectionChanged while it is still creating named controls.
        _loading = true;
        InitializeComponent();
        WidthText.Text = initial.WidthMeters.ToString("0.###", CultureInfo.CurrentCulture);
        DashText.Text = initial.DashMeters.ToString("0.###", CultureInfo.CurrentCulture);
        GapText.Text = initial.GapMeters.ToString("0.###", CultureInfo.CurrentCulture);
        _colorIndex = initial.ColorIndex;
        _red = initial.Red; _green = initial.Green; _blue = initial.Blue;
        _isTrueColor = initial.IsTrueColor;
        if (initial.IsTrueColor)
            ColorCombo.SelectedIndex = -1;
        else
            ColorCombo.SelectedIndex = initial.ColorIndex switch { 2 => 1, 5 => 2, 1 => 3, 3 => 4, _ => 0 };
        LinetypeCombo.SelectedIndex = initial.Dashed ? 1 : 0;
        _loading = false;
        UpdateVisibility();
    }

    public bool UseExisting => ExistingOriginRadio.IsChecked == true;
    public RoadMarkingSettings? Settings { get; private set; }

    private void ColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ColorCombo.SelectedItem is not ComboBoxItem item || !short.TryParse(item.Tag?.ToString(), out short aci))
            return;
        _colorIndex = aci;
        _isTrueColor = false;
    }

    private void LinetypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        // Keep this safe if WPF raises the event before the remaining controls exist.
        if (LinetypeCombo is null || DashText is null || GapText is null)
            return;

        bool dashed = (LinetypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Dashed";
        DashText.IsEnabled = dashed;
        GapText.IsEnabled = dashed;
    }

    private void OtherColorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AcColorDialog { IncludeByBlockByLayer = false };
            if (!_isTrueColor)
                dialog.Color = AcadColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, _colorIndex);
            object? dialogResult = typeof(AcColorDialog).GetMethod("ShowDialog", Type.EmptyTypes)?.Invoke(dialog, null);
            if (!string.Equals(dialogResult?.ToString(), "OK", StringComparison.OrdinalIgnoreCase))
                return;

            using AcadColor selected = dialog.Color;
            if (selected.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci)
            {
                _colorIndex = selected.ColorIndex;
                _isTrueColor = false;
                ColorCombo.SelectedIndex = selected.ColorIndex switch { 2 => 1, 5 => 2, 1 => 3, 3 => 4, 7 => 0, _ => -1 };
            }
            else
            {
                _red = selected.Red; _green = selected.Green; _blue = selected.Blue;
                _isTrueColor = true;
                ColorCombo.SelectedIndex = -1;
            }
            OtherColorButton.Content = $"RGB {_red},{_green},{_blue}";
        }
        catch (Exception ex)
        {
            ValidationText.Text = "No se pudo abrir el selector de color de AutoCAD: " + ex.Message;
        }
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPositive(WidthText, "El ancho de línea debe ser mayor que cero.", out double width)) return;
        bool dashed = (LinetypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Dashed";
        double dash = 4, gap = 3;
        if (dashed && !TryReadPositive(DashText, "La longitud de raya debe ser mayor que cero.", out dash)) return;
        if (dashed && !TryReadPositive(GapText, "El espacio entre rayas debe ser mayor que cero.", out gap)) return;

        Settings = new RoadMarkingSettings
        {
            Dashed = dashed, WidthMeters = width, DashMeters = dash, GapMeters = gap,
            ColorIndex = _colorIndex, Red = _red, Green = _green, Blue = _blue, IsTrueColor = _isTrueColor
        };
        DialogResult = true;
    }

    private bool TryReadPositive(TextBox box, string error, out double value)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value > 0)
        {
            ValidationText.Text = string.Empty;
            return true;
        }
        ValidationText.Text = error;
        box.Focus(); box.SelectAll();
        return false;
    }
}
