using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Models;
using SectionGeometryModel = ARAR.Civil3D.Models.SectionGeometry;
using SectionPointModel = ARAR.Civil3D.Models.SectionPoint;

namespace ARAR.Civil3D.Services;

/// <summary>Muestrea una superficie existente a lo largo de la perpendicular al eje.</summary>
public sealed class TerrainSampler
{
    public TerrainSectionResult Sample(Document document, CorridorSectionData corridorData, SurfaceChoice surfaceChoice, CorridorSectionSnapshot section, double sampleStep = 0.5)
    {
        if (!double.IsFinite(sampleStep) || sampleStep <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStep));
        var result = new SectionGeometryModel();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Alignment alignment = transaction.GetObject(corridorData.Baseline.AlignmentId, OpenMode.ForRead) as Alignment
            ?? throw new InvalidOperationException("No se pudo abrir la alineación.");
        TinSurface surface = transaction.GetObject(surfaceChoice.SurfaceId, OpenMode.ForRead) as TinSurface
            ?? throw new InvalidOperationException("La superficie seleccionada ya no está disponible.");

        double minOffset = Math.Min(section.MinimumOffset - 2.0, -2.0);
        double maxOffset = Math.Max(section.MaximumOffset + 2.0, 2.0);
        int count = Math.Clamp((int)Math.Ceiling((maxOffset - minOffset) / sampleStep) + 1, 2, 5000);
        sampleStep = (maxOffset - minOffset) / (count - 1);
        var run = new List<SectionPointModel>();
        double? axisElevation = null;
        for (int i = 0; i < count; i++)
        {
            double offset = minOffset + i * sampleStep;
            double easting = 0, northing = 0, bearing = 0;
            try
            {
                alignment.PointLocation(section.Station, offset, 0.001, ref easting, ref northing, ref bearing);
                double elevation = surface.FindElevationAtXY(easting, northing);
                if (!double.IsFinite(elevation)) throw new InvalidOperationException("Cota no válida.");
                run.Add(new SectionPointModel(offset, elevation));
                if (Math.Abs(offset) <= sampleStep / 2.0) axisElevation = elevation;
            }
            catch (System.Exception)
            {
                AddRun(result, run);
                run.Clear();
            }
        }
        AddRun(result, run);
        transaction.Commit();
        return new TerrainSectionResult(result, axisElevation);
    }

    private static void AddRun(SectionGeometryModel geometry, List<SectionPointModel> points)
    {
        if (points.Count >= 2) geometry.Polylines.Add(new SectionPolyline("Terreno", points.ToArray(), "EG"));
    }
}
