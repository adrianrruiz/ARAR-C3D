namespace ARAR.Civil3D.Models;

public sealed class SubassemblySegmentInfo
{
    public required string Side { get; init; }
    public required string Name { get; init; }
    public required double StartOffset { get; init; }
    public required double EndOffset { get; init; }
    public required double SlopePercent { get; init; }
    public required string LinkCode { get; init; }
}

public sealed class CorridorSectionSnapshot
{
    public required double Station { get; init; }
    public required string AssemblyName { get; init; }
    public required string RegionName { get; init; }
    public required double AxisElevation { get; init; }
    public required SectionGeometry Geometry { get; init; }
    public required IReadOnlyList<SubassemblySegmentInfo> Segments { get; init; }
    public double MinimumOffset => Geometry.AllPoints.Select(p => p.Offset).DefaultIfEmpty(-10).Min();
    public double MaximumOffset => Geometry.AllPoints.Select(p => p.Offset).DefaultIfEmpty(10).Max();
}

/// <summary>Caché ordenada de secciones y búsqueda binaria por PK.</summary>
public sealed class CorridorSectionCache
{
    private readonly CorridorSectionSnapshot[] _sections;
    public CorridorSectionCache(IEnumerable<CorridorSectionSnapshot> sections) => _sections = sections.OrderBy(s => s.Station).ToArray();
    public IReadOnlyList<CorridorSectionSnapshot> Sections => _sections;
    public int FindNearestIndex(double station)
    {
        if (_sections.Length == 0) return -1;
        int lo = 0, hi = _sections.Length - 1;
        while (lo <= hi) { int mid = lo + ((hi - lo) >> 1); double value = _sections[mid].Station; if (value < station) lo = mid + 1; else if (value > station) hi = mid - 1; else return mid; }
        if (lo <= 0) return 0; if (lo >= _sections.Length) return _sections.Length - 1;
        return station - _sections[lo - 1].Station <= _sections[lo].Station - station ? lo - 1 : lo;
    }
}
