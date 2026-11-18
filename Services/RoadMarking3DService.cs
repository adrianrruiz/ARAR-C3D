using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Models;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CadDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Services;

public sealed record RoadMarking3DResult(int Solids, int Objects, int FailedSweeps, double OmittedMeters, IReadOnlyList<string> Errors);
public sealed record RoadMarking3DInputData(
    IReadOnlyList<RoadSurfaceChoice> Surfaces,
    RoadMarkingSettings? FirstSettings,
    double? FirstWidthMeters,
    bool RequiresSurface,
    IReadOnlyList<ObjectId> ValidSourceIds,
    IReadOnlyList<string> OmittedHandles,
    string WidthDisplayText);

/// <summary>Convierte trayectorias seleccionadas en sólidos barridos conservando los objetos fuente.</summary>
public sealed class RoadMarking3DService
{
    public const string RegAppName = "ARAR_MARCAVIAL3D";
    public const double SolidHeightMeters = 0.010;
    public const double SurfaceSamplingStepMeters = 0.25;
    public const double ArcChordToleranceMeters = 0.01;
    private const double MinimumDashMeters = DashPattern.DefaultMinimumDashMeters;
    private sealed record TrajectoryRun(List<Point3d> Points, double StartDistanceMeters);
    private sealed record PendingSolid(Solid3d Solid, int DashIndex);

    public RoadMarking3DInputData ReadInputData(Document document, IReadOnlyList<ObjectId> ids)
    {
        var surfaces = new List<RoadSurfaceChoice>();
        RoadMarkingSettings? settings = null;
        double? width = null;
        bool requiresSurface = false;
        var validIds = new List<ObjectId>();
        var omittedHandles = new List<string>();
        var widthsMeters = new List<double>();
        double unitsPerMeter = DrawingUnitService.MetersToDrawingUnits(document.Database, 1.0);
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in ids)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (entity is null)
            {
                omittedHandles.Add(id.ToString());
                continue;
            }
            if (entity is Polyline or Polyline2d) requiresSurface = true;
            RoadMarkingSettings? entitySettings = ReadMarkingSettings(entity);
            double? entityWidth = ReadEntityWidth(entity, transaction, document.Database, out bool usedFirstSegmentWidth);
            double? widthMeters = entityWidth / unitsPerMeter;
            if (widthMeters is not double validWidth || !double.IsFinite(validWidth) || validWidth <= 0)
            {
                omittedHandles.Add(entity.Handle.ToString());
                continue;
            }
            if (usedFirstSegmentWidth)
                document.Editor.WriteMessage($"\nAviso: el objeto {entity.Handle} tiene ancho variable; se usará el ancho medio del primer segmento ({validWidth:0.###} m).");
            validIds.Add(id);
            widthsMeters.Add(validWidth);
            settings ??= entitySettings;
            width ??= validWidth;
        }

        CivilDocument civilDocument = CivilApplication.ActiveDocument;
        foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
            if (transaction.GetObject(surfaceId, OpenMode.ForRead, false) is TinSurface surface)
                surfaces.Add(new RoadSurfaceChoice(surfaceId, surface.Name));
        transaction.Commit();
        string widthDisplay = widthsMeters.Count == 0
            ? string.Empty
            : widthsMeters.Max() - widthsMeters.Min() > 1e-9
                ? "Varios"
                : widthsMeters[0].ToString("0.###", CultureInfo.CurrentCulture);
        return new RoadMarking3DInputData(surfaces.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            settings, width, requiresSurface, validIds, omittedHandles, widthDisplay);
    }

    public RoadMarking3DResult Create(Document document, IReadOnlyList<ObjectId> sourceIds, ObjectId? surfaceId,
        RoadMarkingSettings settings, bool keepIntermediate, bool reverseDirection)
    {
        if (sourceIds.Count == 0) throw new ArgumentException("No hay objetos seleccionados.", nameof(sourceIds));

        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Database database = document.Database;
        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        TinSurface? surface = surfaceId is ObjectId terrainId
            ? transaction.GetObject(terrainId, OpenMode.ForRead, false) as TinSurface
            : null;
        if (sourceIds.Any(id => IsTwoDimensional(transaction.GetObject(id, OpenMode.ForRead, false) as Entity)) && surface is null)
            throw new InvalidOperationException("La selección contiene polilíneas 2D y requiere una superficie TIN.");

        int solidCount = 0, failedSweeps = 0, objectCount = 0;
        double omittedMeters = 0;
        var errors = new List<string>();
        for (int sourceIndex = 0; sourceIndex < sourceIds.Count; sourceIndex++)
        {
            ObjectId sourceId = sourceIds[sourceIndex];
            Entity? source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity;
            if (source is null) { errors.Add($"{sourceId}: el objeto ya no está disponible."); continue; }

            double? sourceDrawingWidth = ReadEntityWidth(source, transaction, database, out bool usedFirstSegmentWidth);
            if (sourceDrawingWidth is not double validSourceWidth || !double.IsFinite(validSourceWidth) || validSourceWidth <= 0)
            {
                errors.Add($"Asigne grosor a la línea\nObjeto omitido: {source.Handle}");
                continue;
            }
            double sourceWidthMeters = sourceDrawingWidth.Value / DrawingUnitService.MetersToDrawingUnits(database, 1.0);
            if (usedFirstSegmentWidth)
                errors.Add($"Aviso: {source.Handle} tiene ancho variable; se usó el ancho medio del primer segmento ({sourceWidthMeters:0.###} m).");
            RoadMarkingSettings sourceSettings = settings with { WidthMeters = sourceWidthMeters };

            var candidateSolids = new List<PendingSolid>();
            try
            {
                List<TrajectoryRun> validRuns = GetTrajectories(document, transaction, source, surface, space,
                    out double sourceOmittedMeters, out double totalPlanLengthMeters);
                omittedMeters = omittedMeters + sourceOmittedMeters;
                var sourceSolidIds = new ObjectIdCollection();

                if (reverseDirection)
                {
                    validRuns = validRuns.Select(run =>
                    {
                        var reversedPoints = new List<Point3d>(run.Points);
                        double length = CalculatePlanLengthMeters(reversedPoints, database);
                        reversedPoints.Reverse();
                        return new TrajectoryRun(reversedPoints, totalPlanLengthMeters - run.StartDistanceMeters - length);
                    }).OrderBy(run => run.StartDistanceMeters).ToList();
                }

                IReadOnlyList<DistanceInterval> intervals = GetVisibleIntervals(
                    source, transaction, database, totalPlanLengthMeters, settings);
                if (reverseDirection && settings.Dashed)
                    intervals = intervals.Select(interval => new DistanceInterval(
                        totalPlanLengthMeters - interval.End, totalPlanLengthMeters - interval.Start))
                        .OrderBy(interval => interval.Start).ToArray();

                foreach (TrajectoryRun trajectory in validRuns)
                {
                    List<Point3d> run = trajectory.Points;
                    if (run.Count < 2) continue;
                    PlanMeasure measure = new(run, DrawingUnitService.MetersToDrawingUnits(database, 1));
                    if (measure.TotalMeters <= 1e-9) { errors.Add($"{source.Handle}: tramo sin longitud horizontal; se omitió."); continue; }
                    double runStartMeters = trajectory.StartDistanceMeters;
                    double runEndMeters = runStartMeters + measure.TotalMeters;
                    for (int intervalIndex = 0; intervalIndex < intervals.Count; intervalIndex++)
                    {
                        DistanceInterval interval = intervals[intervalIndex];
                        double visibleStart = Math.Max(interval.Start, runStartMeters);
                        double visibleEnd = Math.Min(interval.End, runEndMeters);
                        if (visibleEnd - visibleStart + 1e-9 < (settings.Dashed ? MinimumDashMeters : 1e-9))
                            continue;
                        try
                        {
                            List<Point3d> piece = measure.Extract(visibleStart - runStartMeters, visibleEnd - runStartMeters);
                            if (piece.Count < 2) continue;
                            Solid3d solid = CreateSweptRoadMarking(piece, sourceWidthMeters, database);
                            CopySourceColor(source, solid);
                            candidateSolids.Add(new PendingSolid(solid, intervalIndex));
                        }
                        catch (AcException ex)
                        {
                            failedSweeps++;
                            errors.Add($"{source.Handle}, tramo {intervalIndex + 1}: error de barrido ({ex.Message}).");
                            AddinLogger.Error($"Falló el barrido del tramo {intervalIndex + 1} de la marca {source.Handle}.", ex);
                        }
                        catch (System.Exception ex)
                        {
                            failedSweeps++;
                            errors.Add($"{source.Handle}, tramo {intervalIndex + 1}: {ex.Message}");
                            AddinLogger.Error($"Falló el barrido del tramo {intervalIndex + 1} de la marca {source.Handle}.", ex);
                        }
                    }
                }

                if (candidateSolids.Count > 0)
                {
                    ObjectId outputLayer = EnsureOutputLayer(database, transaction, source.Layer, out _);
                    EnsureRegApp(database, transaction);
                    for (int index = 0; index < candidateSolids.Count; index++)
                    {
                        PendingSolid candidate = candidateSolids[index];
                        Solid3d solid = candidate.Solid;
                        solid.LayerId = outputLayer;
                        SetSolidXData(solid, sourceSettings, candidate.DashIndex, source.Handle);
                        space.AppendEntity(solid);
                        transaction.AddNewlyCreatedDBObject(solid, true);
                        sourceSolidIds.Add(solid.ObjectId);
                        solidCount++;
                    }
                    AddGroup(transaction, database, sourceSolidIds, source.Handle);
                    objectCount++;
                }
                if (keepIntermediate && validRuns.Count > 0)
                    foreach (TrajectoryRun run in validRuns)
                        AppendIntermediate(space, transaction, run.Points, source.Layer);

                if ((sourceIndex + 1) % 20 == 0 || sourceIndex == sourceIds.Count - 1)
                    document.Editor.WriteMessage($"\nMarca vial 3D: procesados {sourceIndex + 1}/{sourceIds.Count} objetos; {solidCount} sólidos.");
            }
            catch (AcException ex)
            {
                failedSweeps++;
                AddinLogger.Error($"Falló la preparación de la geometría de marca {source.Handle}.", ex);
                transaction.Abort();
                throw;
            }
            catch (System.Exception ex)
            {
                failedSweeps++;
                AddinLogger.Error($"Falló la preparación de la geometría de marca {source.Handle}.", ex);
                transaction.Abort();
                throw;
            }
            finally
            {
                foreach (PendingSolid candidate in candidateSolids)
                    candidate.Solid.Dispose();
            }
        }

        transaction.Commit();
        return new RoadMarking3DResult(solidCount, objectCount, failedSweeps, omittedMeters, errors);
    }

    private static List<TrajectoryRun> GetTrajectories(Document document, Transaction transaction, Entity source,
        TinSurface? surface, BlockTableRecord space, out double omittedMeters, out double totalPlanLengthMeters)
    {
        omittedMeters = 0;
        totalPlanLengthMeters = 0;
        if (source is Polyline3d polyline3d)
        {
            var points = new List<Point3d>();
            foreach (ObjectId vertexId in polyline3d)
                if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is PolylineVertex3d vertex)
                    points.Add(vertex.Position);
            if (points.Count < 2) return new();
            totalPlanLengthMeters = CalculatePlanLengthMeters(points, document.Database);
            return new() { new TrajectoryRun(points, 0) };
        }

        if (source.GetType().FullName == "Autodesk.Civil.DatabaseServices.FeatureLine")
        {
            var featureLine = (FeatureLine)source;
            List<Point3d> points = SampleCivilCurve(featureLine, featureLine.GetPoints(FeatureLinePointType.AllPoints), document.Database);
            totalPlanLengthMeters = CalculatePlanLengthMeters(points, document.Database);
            return points.Count >= 2 ? new() { new TrajectoryRun(points, 0) } : new();
        }

        if (source is Polyline or Polyline2d)
        {
            if (surface is null) throw new InvalidOperationException("Falta seleccionar la superficie para elevar una polilínea 2D.");
            return DrapePolyline2d(document, transaction, source, surface, space, out omittedMeters, out totalPlanLengthMeters);
        }

        throw new InvalidOperationException($"Tipo de trayectoria no admitido: {source.GetType().Name}.");
    }

    private static List<TrajectoryRun> DrapePolyline2d(Document document, Transaction transaction, Entity source,
        TinSurface surface, BlockTableRecord space, out double omittedMeters, out double totalPlanLengthMeters)
    {
        omittedMeters = 0;
        totalPlanLengthMeters = 0;
        if (source is not Curve sourceCurve)
            throw new InvalidOperationException("La polilínea 2D no proporciona geometría de curva para muestrear.");

        // Preflight the plan geometry at <= 0.25 m. This isolates gaps before Civil 3D
        // assigns surface elevations, which can otherwise fail the complete feature line.
        double drawingUnitsPerMeter = DrawingUnitService.MetersToDrawingUnits(document.Database, 1.0);
        double sampleStep = DrawingUnitService.MetersToDrawingUnits(document.Database, SurfaceSamplingStepMeters);
        List<Point3d> arcSamples = SampleCivilCurve(sourceCurve, new Point3dCollection(), document.Database);
        var sampledPlan = new List<Point3d>();
        if (arcSamples.Count > 0) sampledPlan.Add(arcSamples[0]);
        for (int index = 1; index < arcSamples.Count; index++)
        {
            Point3d start = arcSamples[index - 1];
            Point3d end = arcSamples[index];
            int subdivisions = Math.Max(1, (int)Math.Ceiling(start.DistanceTo(end) / sampleStep));
            for (int subdivision = 1; subdivision <= subdivisions; subdivision++)
            {
                double parameter = (double)subdivision / subdivisions;
                sampledPlan.Add(new Point3d(
                    start.X + (end.X - start.X) * parameter,
                    start.Y + (end.Y - start.Y) * parameter,
                    start.Z + (end.Z - start.Z) * parameter));
            }
        }
        var cumulativeDistances = new double[sampledPlan.Count];
        for (int index = 1; index < sampledPlan.Count; index++)
            cumulativeDistances[index] = cumulativeDistances[index - 1] +
                sampledPlan[index - 1].DistanceTo(sampledPlan[index]) / drawingUnitsPerMeter;
        if (cumulativeDistances.Length > 0)
            totalPlanLengthMeters = cumulativeDistances[^1];
        var elevations = new List<double?>(sampledPlan.Count);
        foreach (Point3d point in sampledPlan)
        {
            try
            {
                elevations.Add(surface.FindElevationAtXY(point.X, point.Y));
            }
            catch (AcException)
            {
                elevations.Add(null);
            }
        }

        var planRuns = new List<TrajectoryRun>();
        List<Point3d>? activeRun = null;
        double activeRunStartDistance = 0;
        for (int index = 0; index < sampledPlan.Count; index++)
        {
            if (elevations[index] is double elevation)
            {
                if (activeRun is null)
                {
                    activeRun = new List<Point3d>();
                    activeRunStartDistance = cumulativeDistances[index];
                }
                Point3d plan = sampledPlan[index];
                activeRun.Add(new Point3d(plan.X, plan.Y, elevation));
            }
            else if (activeRun is not null)
            {
                if (activeRun.Count >= 2) planRuns.Add(new TrajectoryRun(activeRun, activeRunStartDistance));
                activeRun = null;
            }

            if (index > 0 && (!elevations[index].HasValue || !elevations[index - 1].HasValue))
                omittedMeters = omittedMeters + cumulativeDistances[index] - cumulativeDistances[index - 1];
        }
        if (activeRun is { Count: >= 2 }) planRuns.Add(new TrajectoryRun(activeRun, activeRunStartDistance));
        if (planRuns.Count == 0)
            return new();

        var output = new List<TrajectoryRun>(planRuns.Count);
        foreach (TrajectoryRun planRun in planRuns)
            output.Add(new TrajectoryRun(CreateSurfaceDrapedFeatureLine(document, transaction, planRun.Points, surface, space),
                planRun.StartDistanceMeters));
        return output;
    }

    private static List<Point3d> CreateSurfaceDrapedFeatureLine(Document document, Transaction transaction,
        IReadOnlyList<Point3d> planRun, TinSurface surface, BlockTableRecord space)
    {
        Entity? sourceCopy = null;
        ObjectId featureLineId = ObjectId.Null;
        using DBObjectCollection explodedParts = new();
        try
        {
            var planPolyline = new Polyline(planRun.Count);
            for (int index = 0; index < planRun.Count; index++)
                planPolyline.AddVertexAt(index, new Point2d(planRun[index].X, planRun[index].Y), 0, 0, 0);
            planPolyline.Elevation = planRun[0].Z;
            sourceCopy = planPolyline;
            space.AppendEntity(sourceCopy);
            transaction.AddNewlyCreatedDBObject(sourceCopy, true);
            featureLineId = FeatureLine.Create("", sourceCopy.ObjectId);
            FeatureLine.MoveToNoneSite(featureLineId);
            var featureLine = transaction.GetObject(featureLineId, OpenMode.ForWrite, false) as FeatureLine
                ?? throw new InvalidOperationException("Civil 3D no devolvió la línea característica temporal.");
            featureLine.AssignElevationsFromSurface(surface.ObjectId, true);
            featureLine.Explode(explodedParts);

            // Explode returns transient entities. Consume their curve geometry and
            // dispose every returned object; neither source geometry nor the TIN is edited.
            var components = new List<List<Point3d>>();
            foreach (CadDbObject part in explodedParts)
            {
                if (part is Curve curve)
                    components.Add(SampleCivilCurve(curve, new Point3dCollection { curve.StartPoint, curve.EndPoint }, document.Database));
            }
            if (components.Count == 0)
                throw new InvalidOperationException("La explosión de la línea característica temporal no devolvió curvas 3D.");
            return JoinCurveComponents(components, featureLine.StartPoint, featureLine.EndPoint);
        }
        finally
        {
            foreach (CadDbObject item in explodedParts)
                item.Dispose();
            if (featureLineId.IsValid && !featureLineId.IsErased && transaction.GetObject(featureLineId, OpenMode.ForWrite, false) is CadDbObject tempFeatureLine)
                tempFeatureLine.Erase();
            if (sourceCopy is not null)
            {
                if (!sourceCopy.IsErased)
                    sourceCopy.Erase();
                sourceCopy.Dispose();
            }
        }
    }

    private static List<Point3d> JoinCurveComponents(List<List<Point3d>> components, Point3d expectedStart, Point3d expectedEnd)
    {
        var remaining = new List<List<Point3d>>(components.Where(points => points.Count >= 2));
        if (remaining.Count == 0) throw new InvalidOperationException("La línea característica no produjo tramos geométricos utilizables.");
        var ordered = new List<Point3d>();
        Point3d current = expectedStart;
        while (remaining.Count > 0)
        {
            int bestIndex = -1;
            bool reverse = false;
            double bestDistance = double.PositiveInfinity;
            for (int index = 0; index < remaining.Count; index++)
            {
                double toStart = current.DistanceTo(remaining[index][0]);
                double toEnd = current.DistanceTo(remaining[index][^1]);
                if (toStart < bestDistance) { bestDistance = toStart; bestIndex = index; reverse = false; }
                if (toEnd < bestDistance) { bestDistance = toEnd; bestIndex = index; reverse = true; }
            }
            List<Point3d> next = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);
            if (reverse) next.Reverse();
            int startIndex = ordered.Count > 0 && ordered[^1].DistanceTo(next[0]) < 1e-6 ? 1 : 0;
            for (int index = startIndex; index < next.Count; index++) ordered.Add(next[index]);
            current = ordered[^1];
        }

        if (ordered.Count >= 2 && ordered[0].DistanceTo(expectedEnd) < ordered[^1].DistanceTo(expectedEnd))
            ordered.Reverse();
        return ordered;
    }

    private static List<Point3d> SampleCivilCurve(Curve curve, Point3dCollection controlPoints, Database database)
    {
        var breakParameters = new SortedSet<double> { curve.StartParam, curve.EndParam };
        foreach (Point3d point in controlPoints)
        {
            try
            {
                double parameter = curve.GetParameterAtPoint(point);
                if (parameter > curve.StartParam + 1e-10 && parameter < curve.EndParam - 1e-10)
                    breakParameters.Add(parameter);
            }
            catch (AcException ex)
            {
                AddinLogger.Error("Se omitió un punto de control de la línea característica al discretizar la trayectoria.", ex);
            }
        }

        double tolerance = DrawingUnitService.MetersToDrawingUnits(database, ArcChordToleranceMeters);
        var result = new List<Point3d>();
        double[] parameters = breakParameters.ToArray();
        for (int index = 1; index < parameters.Length; index++)
        {
            Point3d a = curve.GetPointAtParameter(parameters[index - 1]);
            Point3d b = curve.GetPointAtParameter(parameters[index]);
            if (result.Count == 0) result.Add(a);
            Subdivide(parameters[index - 1], a, parameters[index], b, 0);
        }
        return result;

        void Subdivide(double p0, Point3d a, double p1, Point3d b, int depth)
        {
            if (depth >= 20 || Math.Abs(p1 - p0) < 1e-10) { result.Add(b); return; }
            double pm = (p0 + p1) / 2;
            Point3d middle = curve.GetPointAtParameter(pm);
            if (DistanceToSegment(middle, a, b) <= tolerance) { result.Add(b); return; }
            Subdivide(p0, a, pm, middle, depth + 1);
            Subdivide(pm, middle, p1, b, depth + 1);
        }
    }

    private static Solid3d CreateSweptRoadMarking(IReadOnlyList<Point3d> pathPoints, double widthMeters, Database database)
    {
        try
        {
            return CreateSweptRoadMarkingPiece(pathPoints, widthMeters, database);
        }
        catch (AcException originalError)
        {
            List<List<Point3d>> pieces = SplitAtSharpDeflections(pathPoints);
            if (pieces.Count <= 1)
                throw;

            AddinLogger.Error("Falló el barrido completo; se reintentará dividido en los vértices con deflexión superior a 5 grados.", originalError);
            var solids = new List<Solid3d>(pieces.Count);
            try
            {
                foreach (List<Point3d> piece in pieces)
                    solids.Add(CreateSweptRoadMarkingPiece(piece, widthMeters, database));
                Solid3d combined = solids[0];
                for (int index = 1; index < solids.Count; index++)
                {
                    Solid3d next = solids[index];
                    combined.BooleanOperation(BooleanOperationType.BoolUnite, next);
                    next.Dispose();
                    solids[index] = null!;
                }
                solids.Clear();
                return combined;
            }
            catch
            {
                foreach (Solid3d? solid in solids)
                    solid?.Dispose();
                throw;
            }
        }
    }

    private static List<List<Point3d>> SplitAtSharpDeflections(IReadOnlyList<Point3d> points)
    {
        var pieces = new List<List<Point3d>>();
        var current = new List<Point3d> { points[0] };
        for (int index = 1; index < points.Count - 1; index++)
        {
            current.Add(points[index]);
            Vector3d incoming = points[index] - points[index - 1];
            Vector3d outgoing = points[index + 1] - points[index];
            incoming = new Vector3d(incoming.X, incoming.Y, 0);
            outgoing = new Vector3d(outgoing.X, outgoing.Y, 0);
            if (incoming.Length < 1e-9 || outgoing.Length < 1e-9) continue;
            double cosine = Math.Clamp(incoming.GetNormal().DotProduct(outgoing.GetNormal()), -1.0, 1.0);
            double deflectionDegrees = Math.Acos(cosine) * 180.0 / Math.PI;
            if (deflectionDegrees <= 5.0) continue;
            pieces.Add(current);
            current = new List<Point3d> { points[index] };
        }
        current.Add(points[^1]);
        if (current.Count >= 2) pieces.Add(current);
        return pieces;
    }

    private static Solid3d CreateSweptRoadMarkingPiece(IReadOnlyList<Point3d> pathPoints, double widthMeters, Database database)
    {
        Vector3d horizontalTangent = FindHorizontalTangent(pathPoints);
        if (horizontalTangent.Length < 1e-9) throw new InvalidOperationException("La trayectoria no tiene tangente horizontal.");
        horizontalTangent = horizontalTangent.GetNormal();
        Vector3d side = new(-horizontalTangent.Y, horizontalTangent.X, 0);
        double halfWidth = DrawingUnitService.MetersToDrawingUnits(database, widthMeters) / 2;
        double height = DrawingUnitService.MetersToDrawingUnits(database, SolidHeightMeters);
        Point3d origin = pathPoints[0];
        Point3d leftBottom = origin - side * halfWidth;
        Point3d rightBottom = origin + side * halfWidth;
        Point3d rightTop = rightBottom + Vector3d.ZAxis * height;
        Point3d leftTop = leftBottom + Vector3d.ZAxis * height;
        Vector3d profileNormal = side.CrossProduct(Vector3d.ZAxis).GetNormal();
        Matrix3d worldToOcs = Matrix3d.WorldToPlane(profileNormal);
        Point3d[] profilePoints = { leftBottom, rightBottom, rightTop, leftTop };
        Point3d firstOcs = profilePoints[0].TransformBy(worldToOcs);
        using var profile = new Polyline(4)
        {
            Normal = profileNormal,
            Elevation = firstOcs.Z,
            Closed = true
        };
        for (int index = 0; index < profilePoints.Length; index++)
        {
            Point3d p = profilePoints[index].TransformBy(worldToOcs);
            profile.AddVertexAt(index, new Point2d(p.X, p.Y), 0, 0, 0);
        }
        using var boundary = new DBObjectCollection { profile };
        DBObjectCollection regions = Region.CreateFromCurves(boundary);
        Region? region = regions.Cast<CadDbObject>().OfType<Region>().FirstOrDefault();
        if (region is null)
        {
            DisposeAll(regions);
            throw new InvalidOperationException("No se pudo crear el perfil rectangular para el barrido.");
        }

        using (region)
        using (var path = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(pathPoints.ToArray()), false))
        {
            var optionsBuilder = new SweepOptionsBuilder();
            // El perfil ya está colocado en el inicio del camino. No pedir a SWEEP
            // que lo alinee o traslade evita que desplace el primer extremo.
            optionsBuilder.Align = SweepOptionsAlignOption.NoAlignment;
            using SweepOptions sweepOptions = optionsBuilder.ToSweepOptions();
            var solid = new Solid3d();
            try
            {
                solid.CreateSweptSolid(region, path, sweepOptions);
                return solid;
            }
            catch
            {
                solid.Dispose();
                throw;
            }
        }
    }

    private static Vector3d FindHorizontalTangent(IReadOnlyList<Point3d> points)
    {
        for (int index = 1; index < points.Count; index++)
        {
            Vector3d delta = points[index] - points[index - 1];
            var horizontal = new Vector3d(delta.X, delta.Y, 0);
            if (horizontal.Length > 1e-9) return horizontal;
        }
        return new Vector3d(0, 0, 0);
    }

    private static double CalculatePlanLengthMeters(IReadOnlyList<Point3d> points, Database database)
    {
        double drawingUnitsPerMeter = DrawingUnitService.MetersToDrawingUnits(database, 1.0);
        double total = 0;
        for (int index = 1; index < points.Count; index++)
        {
            double dx = points[index].X - points[index - 1].X;
            double dy = points[index].Y - points[index - 1].Y;
            total = total + Math.Sqrt(dx * dx + dy * dy) / drawingUnitsPerMeter;
        }
        return total;
    }

    private static IReadOnlyList<DistanceInterval> GetVisibleIntervals(Entity source, Transaction transaction,
        Database database, double totalLengthMeters, RoadMarkingSettings settings)
    {
        if (!settings.Dashed)
            return DashPattern.ComputeContinuous(totalLengthMeters);

        ObjectId lineTypeId = source.LinetypeId;
        if (lineTypeId.IsNull || lineTypeId.IsErased)
            return DashPattern.Compute(totalLengthMeters, settings.DashMeters, settings.GapMeters, MinimumDashMeters);

        if (transaction.GetObject(lineTypeId, OpenMode.ForRead, false) is not LinetypeTableRecord selectedLineType)
            return DashPattern.Compute(totalLengthMeters, settings.DashMeters, settings.GapMeters, MinimumDashMeters);
        if (selectedLineType.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) &&
            transaction.GetObject(source.LayerId, OpenMode.ForRead, false) is LayerTableRecord layer)
            lineTypeId = layer.LinetypeObjectId;

        if (transaction.GetObject(lineTypeId, OpenMode.ForRead, false) is not LinetypeTableRecord lineType ||
            lineType.NumDashes != 2 || lineType.DashLengthAt(0) <= 0 || lineType.DashLengthAt(1) >= 0)
            return DashPattern.Compute(totalLengthMeters, settings.DashMeters, settings.GapMeters, MinimumDashMeters);

        // Los sólidos siguen el patrón efectivo de la entidad seleccionada, incluyendo
        // LTSCALE y, en la pestaña Modelo, el factor que aplica MSLTSCALE.
        double globalScale = Convert.ToDouble(
            Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("LTSCALE"),
            CultureInfo.InvariantCulture);
        double displayScale = source.LinetypeScale * globalScale;
        int modelTab = Convert.ToInt32(
            Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("TILEMODE"),
            CultureInfo.InvariantCulture);
        int annotationScaling = Convert.ToInt32(
            Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("MSLTSCALE"),
            CultureInfo.InvariantCulture);
        if (modelTab == 1 && annotationScaling == 1)
        {
            double annotationScale = Convert.ToDouble(
                Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CANNOSCALEVALUE"),
                CultureInfo.InvariantCulture);
            displayScale *= annotationScale;
        }

        double drawingUnitsPerMeter = DrawingUnitService.MetersToDrawingUnits(database, 1.0);
        if (!double.IsFinite(displayScale) || displayScale <= 0 || drawingUnitsPerMeter <= 0)
            return DashPattern.Compute(totalLengthMeters, settings.DashMeters, settings.GapMeters, MinimumDashMeters);

        double dashMeters = lineType.DashLengthAt(0) * displayScale / drawingUnitsPerMeter;
        double gapMeters = -lineType.DashLengthAt(1) * displayScale / drawingUnitsPerMeter;
        if (!double.IsFinite(dashMeters) || !double.IsFinite(gapMeters) || dashMeters <= 0 || gapMeters < 0)
            return DashPattern.Compute(totalLengthMeters, settings.DashMeters, settings.GapMeters, MinimumDashMeters);

        return DashPattern.Compute(totalLengthMeters, dashMeters, gapMeters, MinimumDashMeters);
    }

    private static double DistanceToSegment(Point3d point, Point3d a, Point3d b)
    {
        Vector3d ab = b - a;
        double denominator = ab.DotProduct(ab);
        double parameter = denominator < 1e-20 ? 0 : Math.Clamp((point - a).DotProduct(ab) / denominator, 0, 1);
        return point.DistanceTo(a + ab * parameter);
    }

    private static void AppendIntermediate(BlockTableRecord space, Transaction transaction, IReadOnlyList<Point3d> points, string layerName)
    {
        if (points.Count < 2) return;
        using var intermediate = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(points.ToArray()), false)
        {
            Layer = layerName
        };
        space.AppendEntity(intermediate);
        transaction.AddNewlyCreatedDBObject(intermediate, true);
    }

    private static void CopySourceColor(Entity source, Solid3d solid)
    {
        using Color sourceColor = source.Color;
        if (sourceColor.ColorMethod == ColorMethod.ByLayer)
            solid.ColorIndex = 256;
        else
            solid.Color = sourceColor;
    }

    private static bool IsTwoDimensional(Entity? entity) => entity is Polyline or Polyline2d;

    private static double? ReadEntityWidth(Entity entity, Transaction transaction, Database database, out bool usedFirstSegmentWidth)
    {
        usedFirstSegmentWidth = false;
        if (entity is Polyline polyline)
        {
            if (polyline.ConstantWidth > 0) return polyline.ConstantWidth;
            if (polyline.NumberOfVertices < 2) return null;
            usedFirstSegmentWidth = true;
            double width = (polyline.GetStartWidthAt(0) + polyline.GetEndWidthAt(0)) / 2.0;
            return width > 0 ? width : null;
        }

        if (entity is Polyline2d legacyPolyline)
        {
            if (legacyPolyline.ConstantWidth > 0) return legacyPolyline.ConstantWidth;
            foreach (ObjectId vertexId in legacyPolyline)
            {
                if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is not Vertex2d firstVertex)
                    continue;
                usedFirstSegmentWidth = true;
                double width = (firstVertex.StartWidth + firstVertex.EndWidth) / 2.0;
                return width > 0 ? width : null;
            }
            return null;
        }

        RoadMarkingSettings? xdataSettings = ReadMarkingSettings(entity);
        return xdataSettings is null
            ? null
            : DrawingUnitService.MetersToDrawingUnits(database, xdataSettings.WidthMeters);
    }

    private static RoadMarkingSettings? ReadMarkingSettings(Entity entity)
    {
        using ResultBuffer? buffer = entity.GetXDataForApplication(RoadMarkingDrawingService.RegAppName);
        if (buffer is null) return null;
        TypedValue[] values = buffer.AsArray();
        string? kind = values.FirstOrDefault(v => v.TypeCode == 1000).Value?.ToString();
        double[] numbers = values.Where(v => v.TypeCode == 1040).Select(v => Convert.ToDouble(v.Value, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length < 3 || !double.IsFinite(numbers[0]) || numbers[0] <= 0) return null;
        return new RoadMarkingSettings
        {
            Dashed = kind == "D", WidthMeters = numbers[0], DashMeters = numbers[1], GapMeters = numbers[2]
        };
    }

    private static void EnsureRegApp(Database database, Transaction transaction)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(RegAppName)) return;
        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = RegAppName };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static ObjectId EnsureOutputLayer(Database database, Transaction transaction, string sourceLayerName, out string outputName)
    {
        outputName = sourceLayerName.EndsWith("_3D", StringComparison.OrdinalIgnoreCase) ? sourceLayerName : sourceLayerName + "_3D";
        if (outputName.Length > 255 || outputName.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', ',' }) >= 0)
            throw new InvalidOperationException($"El nombre de capa resultante no es válido: {outputName}.");
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(outputName)) return layers[outputName];

        ObjectId sourceLayerId = layers[sourceLayerName];
        var sourceLayer = (LayerTableRecord)transaction.GetObject(sourceLayerId, OpenMode.ForRead);
        layers.UpgradeOpen();
        var created = new LayerTableRecord
        {
            Name = outputName,
            Color = sourceLayer.Color,
            LinetypeObjectId = sourceLayer.LinetypeObjectId,
            LineWeight = sourceLayer.LineWeight,
            IsPlottable = sourceLayer.IsPlottable
        };
        ObjectId id = layers.Add(created);
        transaction.AddNewlyCreatedDBObject(created, true);
        return id;
    }

    private static void SetSolidXData(Solid3d solid, RoadMarkingSettings settings, int dashIndex, Handle sourceHandle)
    {
        using var data = new ResultBuffer(
            new TypedValue(1001, RegAppName),
            new TypedValue(1000, settings.Dashed ? "D" : "C"),
            new TypedValue(1040, settings.WidthMeters),
            new TypedValue(1040, SolidHeightMeters),
            new TypedValue(1070, (short)Math.Clamp(dashIndex, 0, short.MaxValue)),
            new TypedValue(1005, sourceHandle.ToString()));
        solid.XData = data;
    }

    private static void AddGroup(Transaction transaction, Database database, ObjectIdCollection solidIds, Handle sourceHandle)
    {
        var dictionary = (DBDictionary)transaction.GetObject(database.GroupDictionaryId, OpenMode.ForWrite);
        string baseName = "ARAR_MV3D_" + sourceHandle.ToString();
        string name = baseName; int suffix = 1;
        while (dictionary.Contains(name)) name = baseName + "_" + suffix++;
        var group = new Group("Marca vial 3D " + sourceHandle, selectable: true);
        dictionary.SetAt(name, group);
        transaction.AddNewlyCreatedDBObject(group, true);
        group.Append(solidIds);
    }

    private static void DisposeAll(DBObjectCollection objects)
    {
        foreach (CadDbObject item in objects)
            item.Dispose();
        objects.Dispose();
    }

    private sealed class PlanMeasure
    {
        private readonly IReadOnlyList<Point3d> _points;
        private readonly double[] _cumulativeMeters;
        public double TotalMeters => _cumulativeMeters[^1];

        public PlanMeasure(IReadOnlyList<Point3d> points, double drawingUnitsPerMeter)
        {
            _points = points;
            _cumulativeMeters = new double[points.Count];
            for (int index = 1; index < points.Count; index++)
            {
                double planLength = Math.Sqrt(Math.Pow(points[index].X - points[index - 1].X, 2) + Math.Pow(points[index].Y - points[index - 1].Y, 2));
                _cumulativeMeters[index] = _cumulativeMeters[index - 1] + planLength / drawingUnitsPerMeter;
            }
        }

        public List<Point3d> Extract(double startMeters, double endMeters)
        {
            var output = new List<Point3d> { At(startMeters) };
            for (int index = 1; index < _points.Count - 1; index++)
                if (_cumulativeMeters[index] > startMeters + 1e-9 && _cumulativeMeters[index] < endMeters - 1e-9)
                    output.Add(_points[index]);
            output.Add(At(endMeters));
            return output;
        }

        private Point3d At(double distanceMeters)
        {
            distanceMeters = Math.Clamp(distanceMeters, 0, TotalMeters);
            int index = Array.BinarySearch(_cumulativeMeters, distanceMeters);
            if (index >= 0) return _points[index];
            index = ~index - 1;
            index = Math.Clamp(index, 0, _points.Count - 2);
            double span = _cumulativeMeters[index + 1] - _cumulativeMeters[index];
            if (span <= 1e-12) return _points[index + 1];
            double t = (distanceMeters - _cumulativeMeters[index]) / span;
            return new Point3d(
                _points[index].X + (_points[index + 1].X - _points[index].X) * t,
                _points[index].Y + (_points[index + 1].Y - _points[index].Y) * t,
                _points[index].Z + (_points[index + 1].Z - _points[index].Z) * t);
        }
    }
}
