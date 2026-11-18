using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.Services;

public sealed record SectionLinkInput(string SubassemblyName, string Code, IReadOnlyList<SectionPoint> Points);
public sealed record SectionShapeInput(string Code, IReadOnlyList<SectionPoint> Points);

/// <summary>Convierte datos numéricos de links y shapes en geometría de sección independiente del host.</summary>
public static class SectionCalculator
{
    public static (SectionGeometry Geometry, IReadOnlyList<SubassemblySegmentInfo> Segments, double AxisElevation) Calculate(
        IEnumerable<SectionLinkInput> links, IEnumerable<SectionShapeInput> shapes)
    {
        var geometry = new SectionGeometry();
        var segments = new List<SubassemblySegmentInfo>();
        var elevationsAtAxis = new List<(double Distance, double Elevation)>();

        foreach (SectionLinkInput link in links)
        {
            if (link.Points.Count < 2) continue;
            geometry.Polylines.Add(new SectionPolyline("Link", link.Points.ToArray(), link.Code, link.SubassemblyName));
            foreach (SectionPoint point in link.Points)
                elevationsAtAxis.Add((Math.Abs(point.Offset), point.Elevation));

            for (int i = 1; i < link.Points.Count; i++)
            {
                SectionPoint first = link.Points[i - 1], second = link.Points[i];
                double deltaOffset = second.Offset - first.Offset;
                if (Math.Abs(deltaOffset) < 1e-9) continue;
                bool left = (first.Offset + second.Offset) / 2.0 < 0;
                double outwardDeltaOffset = left ? -deltaOffset : deltaOffset;
                double slope = (second.Elevation - first.Elevation) / outwardDeltaOffset * 100.0;
                segments.Add(new SubassemblySegmentInfo
                {
                    Side = left ? "Izquierda" : "Derecha",
                    Name = link.SubassemblyName,
                    StartOffset = first.Offset,
                    EndOffset = second.Offset,
                    SlopePercent = slope,
                    LinkCode = link.Code
                });
            }
        }

        foreach (SectionShapeInput shape in shapes)
        {
            if (shape.Points.Count < 3) continue;
            var points = shape.Points.ToList();
            if (points[0] != points[^1]) points.Add(points[0]);
            geometry.Polygons.Add(new SectionPolygon("Firme", points, shape.Code));
        }

        double axis = elevationsAtAxis.OrderBy(p => p.Distance).Select(p => p.Elevation).FirstOrDefault(double.NaN);
        return (geometry, segments, axis);
    }
}
