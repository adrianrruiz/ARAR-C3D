using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Colors;

namespace ARAR.Civil3D.Services;

/// <summary>Planta indicator drawn only with AutoCAD transient graphics.</summary>
public sealed class PlanIndicator : IDisposable
{
    private readonly TransientManager _manager = TransientManager.CurrentTransientManager;
    private readonly IntegerCollection _viewports = new();
    private readonly List<Entity> _drawables = new();
    private bool _disposed;

    /// <summary>Replaces the transient marker at the supplied alignment frame.</summary>
    public void Update(Point3d center, double tangentAngle, double station)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearDrawables();
        const double halfLength = 10.0;
        const double markerRadius = 0.45;
        double normalAngle = tangentAngle + Math.PI / 2.0;
        Vector3d normal = new(Math.Cos(normalAngle), Math.Sin(normalAngle), 0);
        Point3d start = center - normal * halfLength;
        Point3d end = center + normal * halfLength;

        var sectionLine = new Line(start, end) { ColorIndex = 1, LineWeight = LineWeight.LineWeight050 };
        var marker = new Circle(center, Vector3d.ZAxis, markerRadius) { ColorIndex = 2 };
        var labelPoint = center + normal * (halfLength + 2.0);
        var label = new DBText
        {
            Position = labelPoint,
            Height = 1.5,
            Rotation = tangentAngle,
            TextString = "PK " + FormatStation(station),
            ColorIndex = 2
        };

        Add(sectionLine);
        Add(marker);
        Add(label);
    }

    private void Add(Entity drawable)
    {
        _drawables.Add(drawable);
        if (!_manager.AddTransient(drawable, TransientDrawingMode.DirectTopmost, 128, _viewports))
        {
            _drawables.Remove(drawable);
            drawable.Dispose();
            throw new InvalidOperationException("AutoCAD no pudo mostrar el indicador transitorio en planta.");
        }
    }

    private void ClearDrawables()
    {
        foreach (Entity drawable in _drawables)
        {
            try { _manager.EraseTransient(drawable, _viewports); }
            catch { /* El viewport o el documento pueden haberse cerrado. */ }
            drawable.Dispose();
        }
        _drawables.Clear();
    }

    private static string FormatStation(double station)
    {
        bool negative = station < 0;
        double value = Math.Abs(station);
        long km = (long)Math.Floor(value / 1000.0);
        return $"{(negative ? "-" : "")}{km}+{value - km * 1000.0:000.000}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearDrawables();
        _disposed = true;
    }
}
