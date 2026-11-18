using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.UI;

/// <summary>Dibuja la rasante cacheada y el PK activo sin acceder al dibujo.</summary>
public sealed class LongitudinalProfileCanvas : FrameworkElement
{
    private LongitudinalProfileSnapshot? _profile;
    private double _station = double.NaN;
    private Func<double, string> _formatStation = value => value.ToString("0.000", CultureInfo.CurrentCulture);
    private double _zoom = 1, _panX, _panY;
    private Point _panStart;
    private bool _panning;

    public LongitudinalProfileCanvas()
    {
        Focusable = true;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    public void SetProfile(LongitudinalProfileSnapshot? profile, double station, Func<double, string> formatStation)
    {
        bool newProfile = !ReferenceEquals(profile, _profile);
        _profile = profile;
        _station = station;
        _formatStation = formatStation;
        if (newProfile) FitToWindow();
        else EnsureStationVisible();
        InvalidateVisual();
    }

    public void FitToWindow()
    {
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_profile is null || _profile.Samples.Count < 2 || ActualWidth < 80 || ActualHeight < 60) return;

        double minX = _profile.StartStation, maxX = _profile.EndStation;
        double minY = _profile.Samples.Min(sample => sample.Elevation);
        double maxY = _profile.Samples.Max(sample => sample.Elevation);
        if (maxY - minY < 1) { minY -= 0.5; maxY += 0.5; }
        double marginLeft = 58, marginRight = 20, marginTop = 36, marginBottom = 34;
        double plotWidth = Math.Max(1, ActualWidth - marginLeft - marginRight);
        double plotHeight = Math.Max(1, ActualHeight - marginTop - marginBottom);
        double baseScaleX = plotWidth / Math.Max(maxX - minX, 1e-6);
        double baseScaleY = plotHeight / (maxY - minY);
        double scaleX = baseScaleX * _zoom;
        double scaleY = baseScaleY;
        double centerX = marginLeft + plotWidth / 2;
        Point Map(ProfileSample sample) => new(
            centerX + (sample.Station - (minX + maxX) / 2) * scaleX + _panX,
            marginTop + plotHeight - (sample.Elevation - minY) * scaleY + _panY);

        var minorPen = new Pen(new SolidColorBrush(Color.FromRgb(229, 233, 237)), 1);
        var axisBrush = Brushes.DimGray;
        double elevationSpacing = CreateText("-000.00", axisBrush, 10).Width + 5;
        double elevationStep = LabelStep(scaleY, Math.Max(20, elevationSpacing));
        for (double elevation = Math.Ceiling(minY / elevationStep) * elevationStep; elevation <= maxY; elevation += elevationStep)
        {
            double y = marginTop + plotHeight - (elevation - minY) * scaleY + _panY;
            dc.DrawLine(minorPen, new Point(marginLeft, y), new Point(ActualWidth - marginRight, y));
            DrawText(dc, elevation.ToString("0.00", CultureInfo.CurrentCulture), new Point(3, y - 7), axisBrush, 10);
        }

        double visibleStart = (minX + maxX) / 2 + (marginLeft - centerX - _panX) / scaleX;
        double visibleEnd = (minX + maxX) / 2 + (ActualWidth - marginRight - centerX - _panX) / scaleX;
        double stationSpacing = CreateText(_formatStation((minX + maxX) / 2), axisBrush, 9).Width + 8;
        double stationStep = LabelStep(scaleX, Math.Max(24, stationSpacing));
        for (double station = Math.Ceiling(visibleStart / stationStep) * stationStep; station <= visibleEnd; station += stationStep)
        {
            double x = centerX + (station - (minX + maxX) / 2) * scaleX + _panX;
            dc.DrawLine(minorPen, new Point(x, marginTop), new Point(x, marginTop + plotHeight));
            DrawText(dc, _formatStation(station), new Point(x - 24, ActualHeight - 24), axisBrush, 9);
        }

        var path = new StreamGeometry();
        using (StreamGeometryContext context = path.Open())
        {
            context.BeginFigure(Map(_profile.Samples[0]), false, false);
            context.PolyLineTo(_profile.Samples.Skip(1).Select(Map).ToArray(), true, false);
        }
        path.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(35, 105, 170)), 2), path);
        DrawText(dc, "Perfil longitudinal: " + _profile.Name, new Point(10, 8), Brushes.Black, 13);

        if (_station < _profile.StartStation || _station > _profile.EndStation)
        {
            DrawText(dc, "PK fuera del perfil", new Point(ActualWidth - 155, 9), Brushes.DarkRed, 12);
            return;
        }
        double elevationAtStation = Interpolate(_profile.Samples, _station);
        double markerX = centerX + (_station - (minX + maxX) / 2) * scaleX + _panX;
        double markerY = marginTop + plotHeight - (elevationAtStation - minY) * scaleY + _panY;
        var markerPen = new Pen(new SolidColorBrush(Color.FromRgb(235, 115, 30)), 1.5) { DashStyle = DashStyles.Dash };
        dc.DrawLine(markerPen, new Point(markerX, marginTop), new Point(markerX, marginTop + plotHeight));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 132, 34)), null, new Point(markerX, markerY), 4.5, 4.5);
        DrawText(dc, $"PK {_formatStation(_station)} · Cota {elevationAtStation:0.000} m",
            new Point(Math.Clamp(markerX + 6, 8, Math.Max(8, ActualWidth - 225)), Math.Max(marginTop + 2, markerY - 19)), Brushes.Black, 11);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_profile is null) return;
        Point cursor = e.GetPosition(this);
        double oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 100);
        double factor = _zoom / oldZoom;
        double plotCenter = Math.Max(1, (ActualWidth - 78) / 2.0 + 58);
        _panX = cursor.X - (cursor.X - _panX - plotCenter) * factor - plotCenter;
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (e.ClickCount >= 2) { FitToWindow(); e.Handled = true; return; }
        _panning = true;
        _panStart = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        Point point = e.GetPosition(this);
        _panX += point.X - _panStart.X;
        _panY += point.Y - _panStart.Y;
        _panStart = point;
        InvalidateVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = false;
        ReleaseMouseCapture();
    }

    private void EnsureStationVisible()
    {
        if (_profile is null || _station < _profile.StartStation || _station > _profile.EndStation || _zoom <= 1 || ActualWidth <= 0) return;
        // La repintada conserva el zoom horizontal y recentra únicamente cuando el
        // indicador abandona el área gráfica.
        double plotWidth = Math.Max(1, ActualWidth - 78);
        double center = (ActualWidth - 78) / 2.0 + 58;
        double baseScale = plotWidth / Math.Max(_profile.EndStation - _profile.StartStation, 1e-6);
        double x = center + (_station - (_profile.StartStation + _profile.EndStation) / 2) * baseScale * _zoom + _panX;
        if (x < 58 || x > ActualWidth - 20) _panX += center - x;
    }

    private static double Interpolate(IReadOnlyList<ProfileSample> samples, double station)
    {
        int low = 0, high = samples.Count - 1;
        while (low + 1 < high)
        {
            int mid = low + (high - low) / 2;
            if (samples[mid].Station < station) low = mid; else high = mid;
        }
        ProfileSample a = samples[low], b = samples[high];
        double width = b.Station - a.Station;
        if (width <= 1e-12) return a.Elevation;
        double ratio = Math.Clamp((station - a.Station) / width, 0, 1);
        return a.Elevation + (b.Elevation - a.Elevation) * ratio;
    }

    private static double LabelStep(double pixelsPerUnit, double requiredPixels)
    {
        double step = 1;
        while (step * pixelsPerUnit < requiredPixels)
        {
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
            double normalized = step / magnitude;
            step = normalized < 2 ? 2 * magnitude : normalized < 5 ? 5 * magnitude : 10 * magnitude;
        }
        return step;
    }

    private static FormattedText CreateText(string text, Brush brush, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0);

    private static void DrawText(DrawingContext dc, string text, Point point, Brush brush, double size)
    {
        dc.DrawText(CreateText(text, brush, size), point);
    }
}
