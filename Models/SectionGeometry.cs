namespace ARAR.Civil3D.Models;

/// <summary>Geometría 2D de sección en coordenadas (offset, cota), independiente de Autodesk y WPF.</summary>
public sealed class SectionGeometry
{
    public List<SectionPolyline> Polylines { get; } = new();
    public List<SectionPolygon> Polygons { get; } = new();
    public List<SectionText> Texts { get; } = new();
    public IEnumerable<SectionPoint> AllPoints => Polylines.SelectMany(p => p.Points).Concat(Polygons.SelectMany(p => p.Points));
}

public readonly record struct SectionPoint(double Offset, double Elevation);
public sealed record SectionPolyline(string Style, IReadOnlyList<SectionPoint> Points, string Code = "", string Owner = "");
public sealed record SectionPolygon(string Style, IReadOnlyList<SectionPoint> Points, string Code = "");
public sealed record SectionText(string Style, SectionPoint Position, string Text);

/// <summary>Punto de control de la cartera de rasante; null indica que la geometría no lo define.</summary>
public sealed record SectionStakePoint(string Number, string Description, double? Offset, double? DesignElevation, double? GroundElevation);

/// <summary>Área de las shapes que forman cada capa del pavimento.</summary>
public sealed record PavementArea(string Code, double LeftArea, double RightArea)
{
    public double TotalArea => LeftArea + RightArea;
}

/// <summary>Color de estilo como RGBA independiente de WPF.</summary>
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B);
public sealed record LongitudinalProfileSnapshot(string Name, double StartStation, double EndStation, IReadOnlyList<ProfileSample> Samples);
public readonly record struct ProfileSample(double Station, double Elevation);

/// <summary>Mapa único de código de shape a nombre y color de visualización.</summary>
public static class PavementShapeCatalog
{
    private static readonly (string Code, string Name, RgbaColor Color)[] Entries =
    {
        ("PAVE1", "PAV 1", new(115, 32, 173, 75)),
        ("PAVE2", "PAV 2", new(115, 35, 145, 205)),
        ("BASE", "BASE", new(115, 135, 75, 165)),
        ("SUBBASE", "SUBBASE", new(115, 55, 120, 205))
    };
    private static readonly RgbaColor[] Palette =
    {
        new(115, 32, 173, 75), new(115, 35, 145, 205), new(115, 135, 75, 165),
        new(115, 220, 145, 35), new(115, 35, 175, 170), new(115, 180, 80, 95)
    };

    public static string GetDisplayName(string code)
    {
        string key = GetKey(code);
        return Entries.FirstOrDefault(entry => key.Contains(entry.Code, StringComparison.OrdinalIgnoreCase)).Name ?? code;
    }

    public static RgbaColor GetColor(string code)
    {
        string key = GetKey(code);
        foreach (var entry in Entries)
            if (key.Contains(entry.Code, StringComparison.OrdinalIgnoreCase)) return entry.Color;
        int hash = 17;
        foreach (char character in key) hash = unchecked(hash * 31 + char.ToUpperInvariant(character));
        return Palette[(uint)hash % (uint)Palette.Length];
    }

    private static string GetKey(string code) => (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

public sealed record NeighborSectionGeometry(SectionGeometry Geometry, double Station, bool IsPrevious);
