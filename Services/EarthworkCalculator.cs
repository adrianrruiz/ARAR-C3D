using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.Services;

public sealed record EarthworkResult(SectionGeometry Geometry, double CutAreaSquareMeters, double FillAreaSquareMeters);

/// <summary>Calculates cut/fill strips from section geometry only; no Autodesk objects are required.</summary>
public static class EarthworkCalculator
{
    public static EarthworkResult Calculate(SectionGeometry corridor, SectionGeometry terrain)
    {
        var result = new SectionGeometry();
        IReadOnlyList<SectionPoint> project = BuildProjectProfile(corridor);
        double cut = 0, fill = 0;
        if (project.Count < 2) return new EarthworkResult(result, 0, 0);

        foreach (SectionPolyline groundRun in terrain.Polylines)
        {
            IReadOnlyList<SectionPoint> ground = groundRun.Points.OrderBy(p => p.Offset).ToArray();
            if (ground.Count < 2) continue;
            double min = Math.Max(project[0].Offset, ground[0].Offset);
            double max = Math.Min(project[^1].Offset, ground[^1].Offset);
            if (max <= min) continue;
            var stations = project.Select(p => p.Offset).Concat(ground.Select(p => p.Offset))
                .Where(x => x >= min && x <= max).Append(min).Append(max).Distinct().OrderBy(x => x).ToArray();
            for (int i = 1; i < stations.Length; i++)
            {
                double x0 = stations[i - 1], x1 = stations[i];
                if (x1 - x0 <= 1e-9) continue;
                double p0 = Interpolate(project, x0), p1 = Interpolate(project, x1);
                double g0 = Interpolate(ground, x0), g1 = Interpolate(ground, x1);
                double d0 = p0 - g0, d1 = p1 - g1;
                if (Math.Abs(d0) < 1e-10) d0 = 0;
                if (Math.Abs(d1) < 1e-10) d1 = 0;
                if (d0 == 0 && d1 == 0) continue;
                if (d0 * d1 < 0)
                {
                    double ratio = Math.Abs(d0) / (Math.Abs(d0) + Math.Abs(d1));
                    double crossX = x0 + (x1 - x0) * ratio;
                    double crossY = p0 + (p1 - p0) * ratio;
                    AddCell(result, ref cut, ref fill, x0, crossX, p0, crossY, g0, crossY);
                    AddCell(result, ref cut, ref fill, crossX, x1, crossY, p1, crossY, g1);
                }
                else AddCell(result, ref cut, ref fill, x0, x1, p0, p1, g0, g1);
            }
        }
        return new EarthworkResult(result, cut, fill);
    }

    private static IReadOnlyList<SectionPoint> BuildProjectProfile(SectionGeometry geometry)
    {
        SectionPolyline[] candidates = geometry.Polylines.Where(line =>
        {
            string code = line.Code.ToUpperInvariant();
            return code.Contains("TOP") || code.Contains("PAVE") || code.Contains("CROWN") || code.Contains("RASANTE");
        }).ToArray();
        if (candidates.Length == 0) candidates = geometry.Polylines.ToArray();
        return candidates.SelectMany(line => line.Points)
            .Where(p => double.IsFinite(p.Offset) && double.IsFinite(p.Elevation))
            .GroupBy(p => Math.Round(p.Offset, 6))
            .Select(g => new SectionPoint(g.Average(p => p.Offset), g.Max(p => p.Elevation)))
            .OrderBy(p => p.Offset).ToArray();
    }

    private static double Interpolate(IReadOnlyList<SectionPoint> points, double x)
    {
        int low = 0, high = points.Count - 1;
        while (low + 1 < high)
        {
            int mid = low + ((high - low) >> 1);
            if (points[mid].Offset <= x) low = mid; else high = mid;
        }
        SectionPoint a = points[low], b = points[high];
        if (Math.Abs(b.Offset - a.Offset) < 1e-12) return Math.Max(a.Elevation, b.Elevation);
        double t = Math.Clamp((x - a.Offset) / (b.Offset - a.Offset), 0, 1);
        return a.Elevation + (b.Elevation - a.Elevation) * t;
    }

    private static void AddCell(SectionGeometry output, ref double cut, ref double fill,
        double x0, double x1, double p0, double p1, double g0, double g1)
    {
        double dx = x1 - x0;
        double d0 = p0 - g0, d1 = p1 - g1;
        double area = (Math.Abs(d0) + Math.Abs(d1)) * dx / 2.0;
        if (area <= 1e-10) return;
        bool isFill = (d0 + d1) >= 0;
        if (isFill) fill += area; else cut += area;
        output.Polygons.Add(new SectionPolygon(isFill ? "Terraplen" : "Desmonte", new[]
        {
            new SectionPoint(x0, p0), new SectionPoint(x1, p1),
            new SectionPoint(x1, g1), new SectionPoint(x0, g0), new SectionPoint(x0, p0)
        }));
    }
}
