using System.Text.Json;
using System.IO;

namespace ARAR.Civil3D.Models;

/// <summary>Parámetros en metros y color de una marca vial.</summary>
public sealed record RoadMarkingSettings
{
    public bool Dashed { get; init; }
    public double WidthMeters { get; init; } = 0.10;
    public double DashMeters { get; init; } = 4.0;
    public double GapMeters { get; init; } = 3.0;
    public short ColorIndex { get; init; } = 7;
    public byte Red { get; init; } = 255;
    public byte Green { get; init; } = 255;
    public byte Blue { get; init; } = 255;
    public bool IsTrueColor { get; init; }
}

/// <summary>Almacena las últimas opciones fuera del dibujo, en el perfil del usuario.</summary>
public static class RoadMarkingPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ARAR-C3D", "settings", "road-marking.json");

    public static RoadMarkingSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<RoadMarkingSettings>(File.ReadAllText(FilePath)) ?? new RoadMarkingSettings();
        }
        catch (Exception ex)
        {
            Services.AddinLogger.Error("No se pudieron leer las últimas opciones de marca vial.", ex);
        }

        return new RoadMarkingSettings();
    }

    public static void Save(RoadMarkingSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Services.AddinLogger.Error("No se pudieron guardar las últimas opciones de marca vial.", ex);
        }
    }
}
