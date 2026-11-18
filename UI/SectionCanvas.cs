using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.UI;

/// <summary>Sección 2D independiente del dibujo de AutoCAD.</summary>
public sealed class SectionCanvas : FrameworkElement
{
    public SectionCanvas()
    {
        Focusable = true;
        MouseWheel += Canvas_MouseWheel;
        MouseDown += Canvas_MouseDown; MouseMove += Canvas_MouseMove; MouseUp += Canvas_MouseUp;
    }

    public void FitToWindow() { _zoom = 1; _panX = 0; _panY = 0; InvalidateVisual(); }

    private void Canvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        Point p = e.GetPosition(this); double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double oldZoom = _zoom; _zoom = Math.Clamp(_zoom * factor, 0.2, 20); factor = _zoom / oldZoom;
        _panX = p.X - (p.X - _panX) * factor; _panY = p.Y - (p.Y - _panY) * factor; InvalidateVisual(); e.Handled = true;
    }
    private void Canvas_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        _panning = true; _panStart = e.GetPosition(this); CaptureMouse(); e.Handled = true;
    }
    private void Canvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_panning) return; Point p = e.GetPosition(this); _panX += p.X - _panStart.X; _panY += p.Y - _panStart.Y; _panStart = p; InvalidateVisual();
    }
    private void Canvas_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return; _panning = false; ReleaseMouseCapture(); e.Handled = true;
    }
    private SectionGeometry _section = new();
    private SectionGeometry _terrain = new();
    private SectionGeometry _earthwork = new();
    private IReadOnlyList<SubassemblySegmentInfo> _segments = Array.Empty<SubassemblySegmentInfo>();
    private IReadOnlyList<SectionStakePoint> _stakePoints = Array.Empty<SectionStakePoint>();
    private IReadOnlyList<NeighborSectionGeometry> _neighbors = Array.Empty<NeighborSectionGeometry>();
    private bool _showPrevious;
    private bool _showNext;
    private string _stationLabel = "";
    private bool _showEarthwork = true;
    private bool _showShapes = true;
    private double _verticalExaggeration = 1;
    private bool _darkBackground;
    private double _zoom = 1, _panX, _panY;
    private Point _panStart;
    private bool _panning;

    public void SetData(SectionGeometry section, SectionGeometry? terrain, string stationLabel, SectionGeometry? earthwork = null,
        IReadOnlyList<SubassemblySegmentInfo>? segments = null, IReadOnlyList<SectionStakePoint>? stakePoints = null)
    {
        _section = section;
        _terrain = terrain ?? new SectionGeometry();
        _earthwork = earthwork ?? new SectionGeometry();
        _segments = segments ?? Array.Empty<SubassemblySegmentInfo>();
        _stakePoints = stakePoints ?? Array.Empty<SectionStakePoint>();
        _stationLabel = stationLabel;
        InvalidateVisual();
    }

    public void SetBackground(bool dark) { _darkBackground = dark; InvalidateVisual(); }

    public void SetDisplayOptions(bool showEarthwork, bool showShapes, double verticalExaggeration)
    {
        _showEarthwork = showEarthwork;
        _showShapes = showShapes;
        _verticalExaggeration = double.IsFinite(verticalExaggeration) && verticalExaggeration > 0 ? verticalExaggeration : 1;
        InvalidateVisual();
    }

    public void SetNeighborSections(IReadOnlyList<NeighborSectionGeometry> neighbors, bool showPrevious, bool showNext)
    {
        _neighbors = neighbors; _showPrevious = showPrevious; _showNext = showNext; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect bounds = new(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(_darkBackground ? new SolidColorBrush(Color.FromRgb(32, 36, 42)) : Brushes.White, null, bounds);
        var all = _section.AllPoints.Concat(_terrain.AllPoints).Concat(_showEarthwork ? _earthwork.AllPoints : Array.Empty<SectionPoint>())
            .Concat(_neighbors.Where(n => n.IsPrevious ? _showPrevious : _showNext).SelectMany(n => n.Geometry.AllPoints)).ToArray();
        if (ActualWidth < 5 || ActualHeight < 5 || all.Length == 0)
        {
            DrawCenteredText(drawingContext, "Cargue una baseline para visualizar la sección.");
            return;
        }

        double minX = Math.Min(all.Min(p => p.Offset), -2), maxX = Math.Max(all.Max(p => p.Offset), 2);
        double minY = all.Min(p => p.Elevation), maxY = all.Max(p => p.Elevation);
        // Mantiene al menos una cota entera a 1 m por debajo y por encima de la geometría.
        // Además de facilitar la lectura, evita que la sección quede pegada al borde vertical.
        minY = Math.Floor(minY) - 1;
        maxY = Math.Ceiling(maxY) + 1;
        double margin = 48;
        double verticalRange = Math.Max((maxY - minY) * _verticalExaggeration, 1);
        double scale = Math.Min((ActualWidth - 2 * margin) / Math.Max(maxX - minX, 1), (ActualHeight - 2 * margin) / verticalRange);
        double xPad = (ActualWidth - (maxX - minX) * scale) / 2;
        double yPad = (ActualHeight - (maxY - minY) * scale * _verticalExaggeration) / 2;
        Point Map(SectionPoint p)
        {
            double x = xPad + (p.Offset - minX) * scale;
            double y = ActualHeight - yPad - (p.Elevation - minY) * scale * _verticalExaggeration;
            return new Point(x, y);
        }

        var viewTransform = new TransformGroup();
        viewTransform.Children.Add(new ScaleTransform(_zoom, _zoom, ActualWidth / 2, ActualHeight / 2));
        viewTransform.Children.Add(new TranslateTransform(_panX, _panY));
        drawingContext.PushTransform(viewTransform);
        DrawGrid(drawingContext, minX, maxX, minY, maxY, xPad, yPad, scale, _verticalExaggeration);
        if (_showEarthwork) DrawPolygons(drawingContext, _earthwork.Polygons, Map);
        foreach (NeighborSectionGeometry neighbor in _neighbors.Where(n => n.IsPrevious ? _showPrevious : _showNext))
            DrawNeighborLines(drawingContext, neighbor.Geometry.Polylines, Map, neighbor.IsPrevious ? Color.FromArgb(100, 35, 115, 210) : Color.FromArgb(105, 235, 135, 35));
        if (_showShapes) DrawPolygons(drawingContext, _section.Polygons, Map);
        DrawPolylines(drawingContext, _section.Polylines, Map, false);
        DrawPolylines(drawingContext, _terrain.Polylines, Map, true);
        DrawCarriagewaySlopeLabels(drawingContext, Map);
        DrawStakeMarkers(drawingContext, Map);
        double zeroX = xPad + (0 - minX) * scale;
        var axisPen = new Pen(Brushes.DimGray, 1) { DashStyle = DashStyles.Dash };
        drawingContext.DrawLine(axisPen, new Point(zeroX, margin / 2), new Point(zeroX, ActualHeight - margin / 2));
        DrawText(drawingContext, "0", new Point(zeroX + 4, ActualHeight - 24), _darkBackground ? Brushes.Gainsboro : Brushes.DimGray, 11);
        DrawText(drawingContext, _stationLabel, new Point(12, 10), _darkBackground ? Brushes.White : Brushes.Black, 14);
        DrawLegend(drawingContext);
        drawingContext.Pop();
    }

    private void DrawGrid(DrawingContext dc, double minX, double maxX, double minY, double maxY, double xPad, double yPad, double scale, double verticalExaggeration)
    {
        double xPixelsPerMeter = scale * _zoom;
        double yPixelsPerMeter = scale * verticalExaggeration * _zoom;
        double maxOffsetLabelWidth = Math.Max(
            CreateText(Math.Ceiling(minX).ToString("0", CultureInfo.InvariantCulture), Brushes.DimGray, 10).Width,
            CreateText(Math.Floor(maxX).ToString("0", CultureInfo.InvariantCulture), Brushes.DimGray, 10).Width);
        double labelXSpacing = Math.Max(18, maxOffsetLabelWidth + 5);
        double labelYSpacing = Math.Max(18, 14);
        double labelXStep = LabelStep(xPixelsPerMeter, labelXSpacing);
        double labelYStep = LabelStep(yPixelsPerMeter, labelYSpacing);
        Brush gridLabelBrush = _darkBackground ? Brushes.Gainsboro : Brushes.DimGray;
        Color baseColor = _darkBackground ? Color.FromRgb(70, 76, 84) : Color.FromRgb(225, 230, 235);
        for (double x = Math.Ceiling(minX); x <= maxX + 1e-9; x += 1.0)
        {
            double px = xPad + (x - minX) * scale;
            double spacing = xPixelsPerMeter;
            byte alpha = (byte)(spacing < 6 ? 55 : 125);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)), 1 / _zoom);
            dc.DrawLine(pen, new Point(px, 20), new Point(px, ActualHeight - 35));
            if (IsLabelStation(x, labelXStep) && spacing * labelXStep >= labelXSpacing)
                DrawText(dc, x.ToString("0", CultureInfo.InvariantCulture), new Point(px + 2 / _zoom, ActualHeight - 30 / _zoom), gridLabelBrush, 10 / _zoom);
        }
        for (double y = Math.Ceiling(minY); y <= maxY + 1e-9; y += 1.0)
        {
            double py = ActualHeight - yPad - (y - minY) * scale * verticalExaggeration;
            double spacing = yPixelsPerMeter;
            byte alpha = (byte)(spacing < 6 ? 55 : 125);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)), 1 / _zoom);
            dc.DrawLine(pen, new Point(42 / _zoom, py), new Point(ActualWidth - 10, py));
            if (IsLabelStation(y, labelYStep) && spacing * labelYStep >= labelYSpacing)
            {
                // Fija el rótulo al margen izquierdo incluso al aplicar zoom/pan al gráfico.
                double labelX = ActualWidth / 2 + (4 - _panX - ActualWidth / 2) / _zoom;
                DrawText(dc, y.ToString("0", CultureInfo.InvariantCulture), new Point(labelX, py - 13 / _zoom), gridLabelBrush, 9 / _zoom);
            }
        }
    }

    private static void DrawPolygons(DrawingContext dc, IEnumerable<SectionPolygon> polygons, Func<SectionPoint, Point> map)
    {
        foreach (SectionPolygon polygon in polygons)
        {
            if (polygon.Points.Count < 3) continue;
            Color color = polygon.Style switch
            {
                "Terraplen" => Color.FromArgb(85, 55, 160, 80),
                "Desmonte" => Color.FromArgb(85, 220, 80, 65),
                "Firme" => ToMediaColor(PavementShapeCatalog.GetColor(polygon.Code)),
                _ => Color.FromArgb(70, 120, 120, 120)
            };
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(map(polygon.Points[0]), true, true);
                context.PolyLineTo(polygon.Points.Skip(1).Select(map).ToArray(), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(130, color.R, color.G, color.B)), 0.6), geometry);
        }
    }

    private void DrawCarriagewaySlopeLabels(DrawingContext dc, Func<SectionPoint, Point> map)
    {
        foreach (SubassemblySegmentInfo segment in _segments)
        {
            if (!segment.Name.Contains("CALZADA", StringComparison.OrdinalIgnoreCase) ||
                !(segment.LinkCode.Contains("TOP", StringComparison.OrdinalIgnoreCase) ||
                  segment.LinkCode.Contains("PAVE", StringComparison.OrdinalIgnoreCase) ||
                  segment.LinkCode.Contains("CROWN", StringComparison.OrdinalIgnoreCase))) continue;

            SectionPolyline? ownerLine = _section.Polylines.FirstOrDefault(line =>
                string.Equals(line.Owner, segment.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line.Code, segment.LinkCode, StringComparison.OrdinalIgnoreCase));
            if (ownerLine is null) continue;
            for (int i = 1; i < ownerLine.Points.Count; i++)
            {
                SectionPoint a = ownerLine.Points[i - 1], b = ownerLine.Points[i];
                if (!MatchesPair(a.Offset, b.Offset, segment.StartOffset, segment.EndOffset)) continue;
                Point pa = map(a), pb = map(b);
                if ((pb - pa).Length * _zoom < 40) break;
                Point middle = new((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2 - 8 / _zoom);
                DrawText(dc, $"{segment.SlopePercent:+0.00;-0.00;0.00}%", middle,
                    new SolidColorBrush(Color.FromRgb(35, 165, 75)), 12 / _zoom);
                break;
            }
        }
    }

    private void DrawStakeMarkers(DrawingContext dc, Func<SectionPoint, Point> map)
    {
        Brush orange = new SolidColorBrush(Color.FromRgb(255, 132, 34));
        Brush labelBrush = _darkBackground ? Brushes.White : new SolidColorBrush(Color.FromRgb(160, 70, 10));
        foreach (SectionStakePoint stake in _stakePoints)
        {
            if (stake.Offset is not double offset || stake.DesignElevation is not double designElevation) continue;
            Point design = map(new SectionPoint(offset, designElevation));
            double radius = 5 / _zoom;
            dc.DrawEllipse(orange, null, design, radius, radius);
            DrawText(dc, stake.Number, new Point(design.X + radius + 2 / _zoom, design.Y - 11 / _zoom), labelBrush, 10 / _zoom);
            if (stake.GroundElevation is double groundElevation && Math.Abs(groundElevation - designElevation) >= 0.02)
                dc.DrawEllipse(orange, null, map(new SectionPoint(offset, groundElevation)), radius, radius);
        }
    }

    private static bool MatchesPair(double a, double b, double c, double d) =>
        (Math.Abs(a - c) < 1e-6 && Math.Abs(b - d) < 1e-6) || (Math.Abs(a - d) < 1e-6 && Math.Abs(b - c) < 1e-6);

    private static Color ToMediaColor(RgbaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static bool IsLabelStation(double value, double step) =>
        Math.Abs(value / step - Math.Round(value / step)) < 1e-7;

    private static double LabelStep(double pixelsPerMeter, double minimumLabelSpacing)
    {
        if (pixelsPerMeter >= minimumLabelSpacing) return 1;
        double step = 1;
        foreach (double factor in new[] { 2.0, 5.0, 10.0 })
        {
            step = factor;
            if (pixelsPerMeter * step >= minimumLabelSpacing) return step;
        }
        while (pixelsPerMeter * step < minimumLabelSpacing) step *= 10;
        return step;
    }

    private static void DrawPolylines(DrawingContext dc, IEnumerable<SectionPolyline> lines, Func<SectionPoint, Point> map, bool terrain)
    {
        foreach (SectionPolyline line in lines)
        {
            if (line.Points.Count < 2) continue;
            Color color = terrain ? Color.FromRgb(170, 88, 35) : ColorFor(line.Code);
            var pen = new Pen(new SolidColorBrush(color), terrain ? 2.3 : 1.8) { LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(map(line.Points[0]), false, false);
                context.PolyLineTo(line.Points.Skip(1).Select(map).ToArray(), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawNeighborLines(DrawingContext dc, IEnumerable<SectionPolyline> lines, Func<SectionPoint, Point> map, Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.2);
        foreach (SectionPolyline line in lines)
        {
            if (line.Points.Count < 2) continue;
            var path = new StreamGeometry();
            using (StreamGeometryContext context = path.Open())
            {
                context.BeginFigure(map(line.Points[0]), false, false);
                context.PolyLineTo(line.Points.Skip(1).Select(map).ToArray(), true, false);
            }
            path.Freeze();
            dc.DrawGeometry(null, pen, path);
        }
    }

    private void DrawLegend(DrawingContext dc)
    {
        double y = 30;
        if (_section.Polylines.Count > 0) { DrawLegendEntry(dc, "Corredor", Color.FromRgb(34, 95, 150), y); y += 19; }
        if (_terrain.Polylines.Count > 0) { DrawLegendEntry(dc, "Terreno", Color.FromRgb(170, 88, 35), y); y += 19; }
        foreach (NeighborSectionGeometry neighbor in _neighbors.Where(n => n.IsPrevious ? _showPrevious : _showNext))
        {
            string label = (neighbor.IsPrevious ? "Anterior " : "Siguiente ") + FormatStation(neighbor.Station);
            DrawLegendEntry(dc, label, neighbor.IsPrevious ? Color.FromRgb(35, 115, 210) : Color.FromRgb(235, 135, 35), y); y += 19;
        }
        if (_showEarthwork && _earthwork.Polygons.Count > 0)
        {
            DrawLegendEntry(dc, "Terraplén", Color.FromRgb(55, 160, 80), y); y += 19;
            DrawLegendEntry(dc, "Desmonte", Color.FromRgb(220, 80, 65), y);
        }
    }

    private void DrawLegendEntry(DrawingContext dc, string text, Color color, double y)
    {
        dc.DrawLine(new Pen(new SolidColorBrush(color), 2.5), new Point(ActualWidth - 135, y + 7), new Point(ActualWidth - 115, y + 7));
        DrawText(dc, text, new Point(ActualWidth - 110, y), _darkBackground ? Brushes.White : Brushes.Black, 11);
    }

    private void DrawCenteredText(DrawingContext dc, string value)
    {
        var text = CreateText(value, Brushes.Gray, 14);
        dc.DrawText(text, new Point(Math.Max(0, (ActualWidth - text.Width) / 2), Math.Max(0, (ActualHeight - text.Height) / 2)));
    }

    private static string FormatStation(double station)
    {
        double value = Math.Abs(station); long km = (long)Math.Floor(value / 1000);
        return $"{(station < 0 ? "-" : "")}{km}+{value - km * 1000:000.000}";
    }

    private static Color ColorFor(string code)
    {
        string value = code.ToUpperInvariant();
        if (value.Contains("TOP") || value.Contains("PAVE") || value.Contains("CROWN")) return Color.FromRgb(30, 100, 165);
        if (value.Contains("DATUM") || value.Contains("SUBBASE")) return Color.FromRgb(120, 75, 145);
        if (value.Contains("DAYLIGHT") || value.Contains("SLOPE") || value.Contains("TALUD")) return Color.FromRgb(65, 135, 80);
        return Color.FromRgb(50, 75, 95);
    }

    private static void DrawText(DrawingContext dc, string value, Point position, Brush brush, double size) => dc.DrawText(CreateText(value, brush, size), position);
    private static FormattedText CreateText(string value, Brush brush, double size) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0);
}
