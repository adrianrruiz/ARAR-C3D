using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.Services;

/// <summary>Genera cartera y áreas de pavimento a partir de la geometría ya cacheada.</summary>
public static class SectionReportCalculator
{
    public static IReadOnlyList<SectionStakePoint> BuildStakePoints(
        SectionGeometry design, IReadOnlyList<SubassemblySegmentInfo> segments, double axisElevation,
        SectionGeometry terrain, double? terrainAtAxis)
    {
        SectionPolyline[] topLinks = design.Polylines.Where(IsCarriagewayTop).ToArray();
        SectionPolyline[] daylightLinks = design.Polylines.Where(IsDaylight).ToArray();
        SectionPoint? leftEdge = ExtremePoint(topLinks, left: true);
        SectionPoint? rightEdge = ExtremePoint(topLinks, left: false);
        SectionPoint? leftDaylight = FindTerrainIntersection(daylightLinks, terrain, left: true);
        SectionPoint? rightDaylight = FindTerrainIntersection(daylightLinks, terrain, left: false);

        return new[]
        {
            MakePoint("P1", "Intersección talud-terreno izquierda", leftDaylight, terrain),
            MakePoint("P2", "Extremo calzada izquierda", leftEdge, terrain),
            MakePoint("P3", "Eje", new SectionPoint(0, axisElevation), terrain, terrainAtAxis),
            MakePoint("P4", "Extremo calzada derecha", rightEdge, terrain),
            MakePoint("P5", "Intersección talud-terreno derecha", rightDaylight, terrain)
        };
    }

    public static IReadOnlyList<PavementArea> CalculatePavementAreas(SectionGeometry design)
    {
        var areas = new Dictionary<string, (double Left, double Right)>(StringComparer.OrdinalIgnoreCase);
        foreach (SectionPolygon polygon in design.Polygons)
        {
            if (polygon.Points.Count < 3 || string.IsNullOrWhiteSpace(polygon.Code)) continue;
            double leftArea = ClippedArea(polygon.Points, keepLeft: true);
            double rightArea = ClippedArea(polygon.Points, keepLeft: false);
            if (leftArea + rightArea <= 1e-10) continue;
            areas.TryGetValue(polygon.Code, out var current);
            areas[polygon.Code] = (current.Left + leftArea, current.Right + rightArea);
        }
        return areas.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new PavementArea(pair.Key, pair.Value.Left, pair.Value.Right)).ToArray();
    }

    private static bool IsCarriagewayTop(SectionPolyline line) =>
        line.Owner.Contains("CALZADA", StringComparison.OrdinalIgnoreCase) && IsTopCode(line.Code);

    private static bool IsTopCode(string code) =>
        code.Contains("TOP", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("PAVE", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("CROWN", StringComparison.OrdinalIgnoreCase);

    private static bool IsDaylight(SectionPolyline line) =>
        line.Owner.Contains("TALUD", StringComparison.OrdinalIgnoreCase) ||
        line.Code.Contains("DAYLIGHT", StringComparison.OrdinalIgnoreCase) ||
        line.Code.Contains("SLOPE", StringComparison.OrdinalIgnoreCase) ||
        line.Code.Contains("TALUD", StringComparison.OrdinalIgnoreCase);

    private static SectionPoint? ExtremePoint(IEnumerable<SectionPolyline> lines, bool left)
    {
        SectionPoint[] points = lines.SelectMany(line => line.Points)
            .Where(point => left ? point.Offset < -1e-6 : point.Offset > 1e-6).ToArray();
        if (points.Length == 0) return null;
        return left ? points.MinBy(point => point.Offset) : points.MaxBy(point => point.Offset);
    }

    private static SectionPoint? FindTerrainIntersection(IEnumerable<SectionPolyline> links, SectionGeometry terrain, bool left)
    {
        var intersections = new List<SectionPoint>();
        foreach (SectionPolyline link in links)
        {
            for (int i = 1; i < link.Points.Count; i++)
            {
                SectionPoint a = link.Points[i - 1], b = link.Points[i];
                if ((left && Math.Max(a.Offset, b.Offset) > 1e-6) || (!left && Math.Min(a.Offset, b.Offset) < -1e-6)) continue;
                if (TryGroundAt(terrain, a.Offset, out double ga) && Math.Abs(ga - a.Elevation) < 0.02)
                    intersections.Add(new SectionPoint(a.Offset, (ga + a.Elevation) / 2));
                if (TryGroundAt(terrain, b.Offset, out double gb) && Math.Abs(gb - b.Elevation) < 0.02)
                    intersections.Add(new SectionPoint(b.Offset, (gb + b.Elevation) / 2));

                foreach (SectionPolyline groundLine in terrain.Polylines)
                for (int j = 1; j < groundLine.Points.Count; j++)
                    if (TryIntersect(a, b, groundLine.Points[j - 1], groundLine.Points[j], out SectionPoint point) &&
                        (left ? point.Offset <= 1e-6 : point.Offset >= -1e-6)) intersections.Add(point);
            }
        }
        if (intersections.Count == 0) return null;
        return left ? intersections.MinBy(point => point.Offset) : intersections.MaxBy(point => point.Offset);
    }

    private static SectionStakePoint MakePoint(string number, string description, SectionPoint? design, SectionGeometry terrain, double? knownGround = null)
    {
        if (design is not SectionPoint point) return new SectionStakePoint(number, description, null, null, null);
        double? ground = knownGround;
        if (ground is null && TryGroundAt(terrain, point.Offset, out double value)) ground = value;
        return new SectionStakePoint(number, description, point.Offset, point.Elevation, ground);
    }

    private static bool TryGroundAt(SectionGeometry terrain, double offset, out double elevation)
    {
        foreach (SectionPolyline line in terrain.Polylines)
        for (int i = 1; i < line.Points.Count; i++)
        {
            SectionPoint a = line.Points[i - 1], b = line.Points[i];
            double min = Math.Min(a.Offset, b.Offset) - 1e-9, max = Math.Max(a.Offset, b.Offset) + 1e-9;
            if (offset < min || offset > max || Math.Abs(b.Offset - a.Offset) < 1e-12) continue;
            double t = (offset - a.Offset) / (b.Offset - a.Offset);
            elevation = a.Elevation + t * (b.Elevation - a.Elevation);
            return double.IsFinite(elevation);
        }
        elevation = 0;
        return false;
    }

    private static bool TryIntersect(SectionPoint a, SectionPoint b, SectionPoint c, SectionPoint d, out SectionPoint point)
    {
        double rx = b.Offset - a.Offset, ry = b.Elevation - a.Elevation;
        double sx = d.Offset - c.Offset, sy = d.Elevation - c.Elevation;
        double denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-12) { point = default; return false; }
        double qx = c.Offset - a.Offset, qy = c.Elevation - a.Elevation;
        double t = (qx * sy - qy * sx) / denominator;
        double u = (qx * ry - qy * rx) / denominator;
        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) { point = default; return false; }
        point = new SectionPoint(a.Offset + t * rx, a.Elevation + t * ry);
        return double.IsFinite(point.Offset) && double.IsFinite(point.Elevation);
    }

    private static double ClippedArea(IReadOnlyList<SectionPoint> points, bool keepLeft)
    {
        var input = points.ToList();
        if (input.Count > 1 && input[0] == input[^1]) input.RemoveAt(input.Count - 1);
        var output = new List<SectionPoint>();
        for (int i = 0; i < input.Count; i++)
        {
            SectionPoint current = input[i], previous = input[(i + input.Count - 1) % input.Count];
            bool currentInside = keepLeft ? current.Offset <= 0 : current.Offset >= 0;
            bool previousInside = keepLeft ? previous.Offset <= 0 : previous.Offset >= 0;
            if (currentInside != previousInside)
            {
                double t = (0 - previous.Offset) / (current.Offset - previous.Offset);
                output.Add(new SectionPoint(0, previous.Elevation + t * (current.Elevation - previous.Elevation)));
            }
            if (currentInside) output.Add(current);
        }
        if (output.Count < 3) return 0;
        double twiceArea = 0;
        for (int i = 0; i < output.Count; i++)
        {
            SectionPoint a = output[i], b = output[(i + 1) % output.Count];
            twiceArea += a.Offset * b.Elevation - b.Offset * a.Elevation;
        }
        return Math.Abs(twiceArea) / 2.0;
    }
}
