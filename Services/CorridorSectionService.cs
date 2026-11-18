using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Models;
using SectionPointModel = ARAR.Civil3D.Models.SectionPoint;

namespace ARAR.Civil3D.Services;

/// <summary>Lee el dibujo dentro de transacciones y materializa una caché de datos sin wrappers de Civil 3D.</summary>
public sealed class CorridorSectionService
{
    public (IReadOnlyList<BaselineChoice> Baselines, IReadOnlyList<SurfaceChoice> Surfaces) Discover(Document document, ObjectId selectedId)
    {
        var baselines = new List<BaselineChoice>();
        var surfaces = new List<SurfaceChoice>();
        Database database = document.Database;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var selected = transaction.GetObject(selectedId, OpenMode.ForRead);
        ObjectId? selectedAlignmentId = null;
        if (selected is Corridor selectedCorridor)
            AddCorridorBaselines(selectedCorridor, transaction, baselines);
        else if (selected is Alignment alignment)
            selectedAlignmentId = alignment.ObjectId;
        else
            throw new InvalidOperationException("El objeto seleccionado no es un corredor ni una alineación.");

        if (selectedAlignmentId.HasValue)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            foreach (ObjectId corridorId in civilDocument.CorridorCollection)
            {
                var corridor = transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
                if (corridor is null) continue;
                foreach (Baseline baseline in corridor.Baselines)
                {
                    if (baseline.AlignmentId == selectedAlignmentId.Value)
                    {
                        int index = FindBaselineIndex(corridor, baseline.Name);
                        string alignmentName = ((Alignment)transaction.GetObject(selectedAlignmentId.Value, OpenMode.ForRead)).Name;
                        baselines.Add(new BaselineChoice(corridorId, index, selectedAlignmentId.Value, corridor.Name, baseline.Name, alignmentName));
                    }
                }
            }
        }

        CivilDocument activeCivilDocument = CivilApplication.ActiveDocument;
        foreach (ObjectId surfaceId in activeCivilDocument.GetSurfaceIds())
        {
            if (transaction.GetObject(surfaceId, OpenMode.ForRead) is TinSurface surface)
                surfaces.Add(new SurfaceChoice(surfaceId, surface.Name));
        }
        transaction.Commit();
        return (baselines, surfaces.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public CorridorSectionData Load(Document document, BaselineChoice choice)
    {
        var snapshots = new List<CorridorSectionSnapshot>();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        var corridor = transaction.GetObject(choice.CorridorId, OpenMode.ForRead) as Corridor
            ?? throw new InvalidOperationException("No se pudo abrir el corredor seleccionado.");
        Alignment alignment = transaction.GetObject(choice.AlignmentId, OpenMode.ForRead) as Alignment
            ?? throw new InvalidOperationException("No se pudo abrir la alineación de la baseline.");
        if (choice.BaselineIndex < 0 || choice.BaselineIndex >= corridor.Baselines.Count)
            throw new InvalidOperationException("La baseline ya no está disponible en el corredor.");

        Baseline baseline = corridor.Baselines[choice.BaselineIndex];
        LongitudinalProfileSnapshot? profileSnapshot = ReadLongitudinalProfile(transaction, baseline);
        int adjustedElevationFallbacks = 0;
        int skippedAssemblies = 0;
        System.Exception? firstAdjustedElevationError = null;
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
            foreach (AppliedAssembly appliedAssembly in region.AppliedAssemblies)
            {
                var linkInputs = new List<SectionLinkInput>();
                var shapeInputs = new List<SectionShapeInput>();
                var stationCandidates = new List<double>();
                string assemblyName = GetAssemblyName(transaction, appliedAssembly.AssemblyId);
                foreach (CalculatedPoint point in appliedAssembly.Points)
                    stationCandidates.Add(point.StationOffsetElevationToBaseline.X);

                // AdjustedElevation falla en algunas secciones calculadas. Recuperamos
                // su cota absoluta del perfil de la baseline para no abortar la caché.
                double baselineElevation;
                try
                {
                    baselineElevation = appliedAssembly.AdjustedElevation;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    firstAdjustedElevationError ??= ex;
                    if (!TryReadProfileElevation(transaction, baseline, stationCandidates, out baselineElevation))
                    {
                        skippedAssemblies++;
                        continue;
                    }
                    adjustedElevationFallbacks++;
                }
                catch (System.ArgumentException ex)
                {
                    firstAdjustedElevationError ??= ex;
                    if (!TryReadProfileElevation(transaction, baseline, stationCandidates, out baselineElevation))
                    {
                        skippedAssemblies++;
                        continue;
                    }
                    adjustedElevationFallbacks++;
                }

                foreach (AppliedSubassembly appliedSubassembly in appliedAssembly.GetAppliedSubassemblies())
                {
                    string subassemblyName = GetSubassemblyName(transaction, appliedSubassembly.SubassemblyId);
                    foreach (CalculatedLink link in appliedSubassembly.Links)
                    {
                        string code = string.Join("/", link.CorridorCodes.Cast<string>());
                        var points = new List<SectionPointModel>();
                        foreach (CalculatedPoint point in link.CalculatedPoints)
                        {
                            var location = point.StationOffsetElevationToBaseline;
                            stationCandidates.Add(location.X);
                            points.Add(new SectionPointModel(location.Y, baselineElevation + location.Z));
                        }
                        if (points.Count >= 2) linkInputs.Add(new SectionLinkInput(subassemblyName, code, points));
                    }
                    foreach (CalculatedShape shape in appliedSubassembly.Shapes)
                    {
                        var points = new List<SectionPointModel>();
                        foreach (CalculatedLink link in shape.CalculatedLinks)
                        {
                            foreach (CalculatedPoint point in link.CalculatedPoints)
                            {
                                var location = point.StationOffsetElevationToBaseline;
                                stationCandidates.Add(location.X);
                                var sectionPoint = new SectionPointModel(location.Y, baselineElevation + location.Z);
                                if (points.Count == 0 || points[^1] != sectionPoint) points.Add(sectionPoint);
                            }
                        }
                        string code = string.Join("/", shape.CorridorCodes.Cast<string>());
                        if (points.Count >= 3) shapeInputs.Add(new SectionShapeInput(code, points));
                    }
                }

                if (stationCandidates.Count == 0) continue;
                var calculated = SectionCalculator.Calculate(linkInputs, shapeInputs);
                double station = stationCandidates.Average();
                snapshots.Add(new CorridorSectionSnapshot
                {
                    Station = station,
                    AssemblyName = assemblyName,
                    RegionName = region.Name,
                    AxisElevation = calculated.AxisElevation,
                    Geometry = calculated.Geometry,
                    Segments = calculated.Segments
                });
            }
        }
        if (firstAdjustedElevationError is not null)
            AddinLogger.Error(
                $"AppliedAssembly.AdjustedElevation falló en esta baseline; se recuperaron {adjustedElevationFallbacks} secciones desde el perfil y se omitieron {skippedAssemblies} sin cota de perfil.",
                firstAdjustedElevationError);
        transaction.Commit();
        var cache = new CorridorSectionCache(snapshots);
        if (cache.Sections.Count == 0)
            throw new InvalidOperationException("La baseline no contiene secciones calculadas. Reconstruya el corredor y vuelva a intentarlo.");
        return new CorridorSectionData(choice with { AlignmentName = alignment.Name }, cache, profileSnapshot,
            adjustedElevationFallbacks, skippedAssemblies);
    }

    private static bool TryReadProfileElevation(Transaction transaction, Baseline baseline,
        IReadOnlyList<double> stationCandidates, out double elevation)
    {
        elevation = 0;
        if (stationCandidates.Count == 0) return false;

        try
        {
            ObjectId profileId = baseline.ProfileId;
            if (profileId.IsNull || !profileId.IsValid ||
                transaction.GetObject(profileId, OpenMode.ForRead, false) is not Profile profile)
                return false;

            elevation = profile.ElevationAt(stationCandidates.Average());
            return double.IsFinite(elevation);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (System.ArgumentException)
        {
            return false;
        }
    }

    private static LongitudinalProfileSnapshot? ReadLongitudinalProfile(Transaction transaction, Baseline baseline)
    {
        ObjectId profileId;
        try { profileId = baseline.ProfileId; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        if (profileId.IsNull || !profileId.IsValid) return null;

        Profile? profile = transaction.GetObject(profileId, OpenMode.ForRead) as Profile;
        if (profile is null || profile.Entities.Count == 0 || profile.PVIs.Count < 2) return null;

        double start = profile.StartingStation;
        double end = profile.EndingStation;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start) return null;

        var stations = new SortedSet<double> { start, end };
        foreach (ProfilePVI pvi in profile.PVIs)
            if (pvi.Station >= start && pvi.Station <= end) stations.Add(pvi.Station);

        foreach (ProfileEntity entity in profile.Entities)
        {
            double entityStart = Math.Clamp(entity.StartStation, start, end);
            double entityEnd = Math.Clamp(entity.EndStation, start, end);
            if (entityEnd < entityStart) continue;
            stations.Add(entityStart);
            stations.Add(entityEnd);
            // Muestreo máximo de 2 m: también representa con suficiente resolución
            // curvas verticales y conserva los extremos y PVIs exactos.
            int intervals = Math.Max(1, (int)Math.Ceiling((entityEnd - entityStart) / 2.0));
            for (int i = 1; i < intervals; i++) stations.Add(entityStart + (entityEnd - entityStart) * i / intervals);
        }

        var samples = new List<ProfileSample>(stations.Count);
        foreach (double station in stations)
        {
            try
            {
                double elevation = profile.ElevationAt(station);
                if (double.IsFinite(elevation)) samples.Add(new ProfileSample(station, elevation));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Se omite una estación que Civil 3D no pueda evaluar; el perfil solo
                // se presenta si quedan al menos dos puntos válidos.
            }
        }
        return samples.Count >= 2 ? new LongitudinalProfileSnapshot(profile.Name, start, end, samples) : null;
    }

    /// <summary>Projects a WCS point to an alignment and returns its station.</summary>
    public double ProjectStation(Document document, ObjectId alignmentId, Point3d point)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Alignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment
            ?? throw new InvalidOperationException("No se pudo abrir la alineación activa.");
        double station = 0, offset = 0;
        alignment.StationOffset(point.X, point.Y, ref station, ref offset);
        transaction.Commit();
        return station;
    }

    /// <summary>Returns the alignment point and local tangent at a station in WCS.</summary>
    public (Point3d Point, double TangentAngle) GetPlanFrame(Document document, ObjectId alignmentId, double station)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Alignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment
            ?? throw new InvalidOperationException("No se pudo abrir la alineación activa.");
        Point3d center = GetAlignmentPoint(alignment, station);
        const double delta = 0.1;
        Point3d nearby;
        bool useForward;
        try
        {
            nearby = GetAlignmentPoint(alignment, station + delta);
            useForward = true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            nearby = GetAlignmentPoint(alignment, station - delta);
            useForward = false;
        }
        Vector2d tangent = useForward
            ? new Vector2d(nearby.X - center.X, nearby.Y - center.Y)
            : new Vector2d(center.X - nearby.X, center.Y - nearby.Y);
        if (tangent.Length < 1e-9)
            throw new InvalidOperationException("No se pudo calcular la dirección local de la alineación.");
        transaction.Commit();
        return (center, Math.Atan2(tangent.Y, tangent.X));
    }

    private static Point3d GetAlignmentPoint(Alignment alignment, double station)
    {
        double easting = 0, northing = 0;
        alignment.PointLocation(station, 0, ref easting, ref northing);
        return new Point3d(easting, northing, 0);
    }

    private static void AddCorridorBaselines(Corridor corridor, Transaction transaction, List<BaselineChoice> output)
    {
        int index = 0;
        foreach (Baseline baseline in corridor.Baselines)
        {
            if (transaction.GetObject(baseline.AlignmentId, OpenMode.ForRead) is Alignment alignment)
                output.Add(new BaselineChoice(corridor.ObjectId, index, baseline.AlignmentId, corridor.Name, baseline.Name, alignment.Name));
            index++;
        }
    }

    private static int FindBaselineIndex(Corridor corridor, string baselineName)
    {
        for (int i = 0; i < corridor.Baselines.Count; i++)
            if (string.Equals(corridor.Baselines[i].Name, baselineName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string GetAssemblyName(Transaction transaction, ObjectId assemblyId)
    {
        if (assemblyId.IsNull || !assemblyId.IsValid) return "(Ensamblaje)";
        return (transaction.GetObject(assemblyId, OpenMode.ForRead) as Assembly)?.Name ?? "(Ensamblaje)";
    }

    private static string GetSubassemblyName(Transaction transaction, ObjectId subassemblyId)
    {
        if (subassemblyId.IsNull || !subassemblyId.IsValid) return "(Subensamblaje)";
        return (transaction.GetObject(subassemblyId, OpenMode.ForRead) as Subassembly)?.Name ?? "(Subensamblaje)";
    }
}
