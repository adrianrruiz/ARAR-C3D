using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Models;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Services;

public sealed record RoadMarkingProcessResult(int Modified, int Replaced, IReadOnlyList<string> Omitted, bool SplineApproximated);

/// <summary>Crea, estiliza y etiqueta marcas viales en una única transacción del dibujo.</summary>
public sealed class RoadMarkingDrawingService
{
    public const string RegAppName = "ARAR_MARCAVIAL";
    private const double SplineToleranceMeters = 0.005;

    public RoadMarkingProcessResult ApplyToExisting(Document document, IReadOnlyList<ObjectId> ids, RoadMarkingSettings settings)
    {
        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        EnsureRegApp(document.Database, transaction, RegAppName);
        ObjectId linetypeId = GetLinetype(document.Database, transaction, settings);
        ObjectId spaceId = document.Database.CurrentSpaceId;
        var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
        int modified = 0, replaced = 0;
        bool splineApproximated = false;
        var omitted = new List<string>();

        foreach (ObjectId id in ids)
        {
            Entity? entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (entity is null)
            {
                omitted.Add($"{id}: no es una entidad gráfica.");
                continue;
            }

            if (entity is Polyline lightPolyline)
            {
                lightPolyline.UpgradeOpen();
                ApplyAppearance(lightPolyline, settings, linetypeId, document.Database);
                SetXData(lightPolyline, settings, transaction);
                modified++;
                continue;
            }

            if (entity is Polyline3d || entity.GetType().FullName == "Autodesk.Civil.DatabaseServices.FeatureLine")
            {
                entity.UpgradeOpen();
                ApplyAppearance(entity, settings, linetypeId, document.Database, applyWidth: false);
                SetXData(entity, settings, transaction);
                omitted.Add($"{entity.Handle}: el ancho se guardó en XData pero no se representa en polilíneas 3D/líneas características.");
                modified++;
                continue;
            }

            if (entity is Circle circle)
            {
                Polyline replacement = CreateCirclePolyline(circle, settings, linetypeId, document.Database);
                CopyProperties(circle, replacement);
                ApplyAppearance(replacement, settings, linetypeId, document.Database);
                space.AppendEntity(replacement);
                transaction.AddNewlyCreatedDBObject(replacement, true);
                SetXData(replacement, settings, transaction);
                circle.UpgradeOpen(); circle.Erase();
                replaced++;
                continue;
            }

            if (entity is Curve curve && (entity is Line || entity is Arc || entity is Polyline2d || entity is Spline))
            {
                Vector3d normal = GetCurveNormal(entity);
                double tolerance = DrawingUnitService.MetersToDrawingUnits(document.Database, SplineToleranceMeters);
                List<Point3d> points = SampleCurve(curve, tolerance);
                if (points.Count < 2)
                {
                    omitted.Add($"{entity.Handle}: no fue posible obtener dos puntos de geometría.");
                    continue;
                }
                if (entity is Spline) splineApproximated = true;

                IReadOnlyList<double>? bulges = entity is Arc arc
                    ? new[] { Math.Tan(arc.TotalAngle / 4.0), 0.0 }
                    : null;
                if (entity is Arc arcEntity)
                    points = new List<Point3d> { arcEntity.StartPoint, arcEntity.EndPoint };
                Polyline replacement = CreatePolyline(points, normal, settings, linetypeId, document.Database, bulges);
                CopyProperties(entity, replacement);
                ApplyAppearance(replacement, settings, linetypeId, document.Database);
                space.AppendEntity(replacement);
                transaction.AddNewlyCreatedDBObject(replacement, true);
                SetXData(replacement, settings, transaction);
                entity.UpgradeOpen(); entity.Erase();
                replaced++;
                continue;
            }

            omitted.Add($"{entity.Handle}: tipo de objeto no admitido ({entity.GetType().Name}).");
        }

        transaction.Commit();
        return new RoadMarkingProcessResult(modified, replaced, omitted, splineApproximated);
    }

    public ObjectId CreateNew(Document document, IReadOnlyList<Point3d> vertices, IReadOnlyList<double> bulges,
        RoadMarkingSettings settings, bool closed = false)
    {
        if (vertices.Count < 2 || bulges.Count != vertices.Count)
            throw new ArgumentException("La marca nueva debe contener al menos dos vértices y un valor de curva por vértice.");

        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        EnsureRegApp(document.Database, transaction, RegAppName);
        ObjectId linetypeId = GetLinetype(document.Database, transaction, settings);
        var polyline = CreatePolyline(vertices, Vector3d.ZAxis, settings, linetypeId, document.Database, bulges, closed);
        polyline.Elevation = vertices[0].Z;
        BlockTableRecord space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
        space.AppendEntity(polyline);
        transaction.AddNewlyCreatedDBObject(polyline, true);
        SetXData(polyline, settings, transaction);
        ObjectId result = polyline.ObjectId;
        transaction.Commit();
        return result;
    }

    private static Polyline CreateCirclePolyline(Circle circle, RoadMarkingSettings settings, ObjectId linetypeId, Database database)
    {
        var vertices = new List<Point3d>(4);
        for (int index = 0; index < 4; index++)
            vertices.Add(circle.GetPointAtParameter(index * Math.PI / 2));
        return CreatePolyline(vertices, circle.Normal, settings, linetypeId, database, new[] { 1.0, 1.0, 1.0, 1.0 }, closed: true);
    }

    private static Polyline CreatePolyline(IReadOnlyList<Point3d> points, Vector3d normal, RoadMarkingSettings settings,
        ObjectId linetypeId, Database database, IReadOnlyList<double>? bulges = null, bool closed = false)
    {
        if (normal.Length < 1e-9) normal = Vector3d.ZAxis;
        normal = normal.GetNormal();
        Matrix3d worldToOcs = Matrix3d.WorldToPlane(normal);
        Point3d firstOcs = points[0].TransformBy(worldToOcs);
        var polyline = new Polyline
        {
            Normal = normal,
            Elevation = firstOcs.Z,
            Closed = closed,
            Plinegen = true,
            LinetypeId = linetypeId,
            LinetypeScale = GetLinetypeScale()
        };

        for (int index = 0; index < points.Count; index++)
        {
            Point3d point = points[index].TransformBy(worldToOcs);
            polyline.AddVertexAt(index, new Point2d(point.X, point.Y), bulges is not null ? bulges[index] : 0, 0, 0);
        }
        double width = DrawingUnitService.MetersToDrawingUnits(database, settings.WidthMeters);
        int segmentCount = closed ? points.Count : points.Count - 1;
        for (int index = 0; index < segmentCount; index++)
        {
            polyline.SetStartWidthAt(index, width);
            polyline.SetEndWidthAt(index, width);
        }
        polyline.ConstantWidth = width;
        ApplyColor(polyline, settings);
        return polyline;
    }

    private static List<Point3d> SampleCurve(Curve curve, double chordTolerance)
    {
        double start = curve.StartParam, end = curve.EndParam;
        Point3d first = curve.GetPointAtParameter(start), last = curve.GetPointAtParameter(end);
        var points = new List<Point3d> { first };
        Subdivide(start, first, end, last, 0);
        return points;

        void Subdivide(double p0, Point3d a, double p1, Point3d b, int depth)
        {
            if (depth >= 20 || Math.Abs(p1 - p0) < 1e-10)
            {
                points.Add(b);
                return;
            }
            double middleParameter = (p0 + p1) / 2;
            Point3d middle = curve.GetPointAtParameter(middleParameter);
            if (DistanceToSegment(middle, a, b) <= chordTolerance)
            {
                points.Add(b);
                return;
            }
            Subdivide(p0, a, middleParameter, middle, depth + 1);
            Subdivide(middleParameter, middle, p1, b, depth + 1);
        }
    }

    private static double DistanceToSegment(Point3d point, Point3d a, Point3d b)
    {
        Vector3d ab = b - a;
        double denominator = ab.DotProduct(ab);
        double t = denominator < 1e-20 ? 0 : Math.Clamp((point - a).DotProduct(ab) / denominator, 0, 1);
        return point.DistanceTo(a + ab * t);
    }

    private static Vector3d GetCurveNormal(Entity entity) => entity switch
    {
        Arc arc => arc.Normal,
        Circle circle => circle.Normal,
        Polyline2d polyline => polyline.Normal,
        _ => Vector3d.ZAxis
    };

    private static void CopyProperties(Entity source, Entity target)
    {
        target.Layer = source.Layer;
        target.Linetype = source.Linetype;
        target.LineWeight = source.LineWeight;
        target.Transparency = source.Transparency;
        target.Visible = source.Visible;
    }

    private static void ApplyAppearance(Entity entity, RoadMarkingSettings settings, ObjectId linetypeId, Database database, bool applyWidth = true)
    {
        entity.LinetypeId = linetypeId;
        entity.LinetypeScale = GetLinetypeScale();
        ApplyColor(entity, settings);
        if (applyWidth && entity is Polyline polyline)
        {
            polyline.Plinegen = true;
            polyline.ConstantWidth = DrawingUnitService.MetersToDrawingUnits(database, settings.WidthMeters);
        }
    }

    private static void ApplyColor(Entity entity, RoadMarkingSettings settings)
    {
        using Color color = settings.IsTrueColor
            ? Color.FromRgb(settings.Red, settings.Green, settings.Blue)
            : Color.FromColorIndex(ColorMethod.ByAci, settings.ColorIndex);
        entity.Color = color;
    }

    private static double GetLinetypeScale()
    {
        try
        {
            object? rawScale = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("LTSCALE");
            double globalScale = Convert.ToDouble(rawScale, CultureInfo.InvariantCulture);
            // La entidad compensa únicamente LTSCALE, igual que la implementación
            // anterior que mostraba correctamente el patrón de rayas en el dibujo.
            return double.IsFinite(globalScale) && Math.Abs(globalScale) > 1e-12 ? 1.0 / globalScale : 1.0;
        }
        catch (AcException ex)
        {
            AddinLogger.Error("No se pudo consultar LTSCALE; se usará escala de tipo de línea 1.", ex);
            return 1.0;
        }
    }

    private static ObjectId GetLinetype(Database database, Transaction transaction, RoadMarkingSettings settings)
    {
        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (!settings.Dashed)
        {
            foreach (ObjectId continuousId in table)
                if (transaction.GetObject(continuousId, OpenMode.ForRead) is LinetypeTableRecord record &&
                    record.Name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return continuousId;
            throw new InvalidOperationException("No se encontró el tipo de línea estándar Continuous en el dibujo.");
        }

        double dash = DrawingUnitService.MetersToDrawingUnits(database, settings.DashMeters);
        double gap = DrawingUnitService.MetersToDrawingUnits(database, settings.GapMeters);
        string rootName = $"ARAR_MV_D{Math.Round(settings.DashMeters * 1000, MidpointRounding.AwayFromZero):0}_E{Math.Round(settings.GapMeters * 1000, MidpointRounding.AwayFromZero):0}";
        string name = rootName;
        int revision = 1;
        while (table.Has(name))
        {
            ObjectId existingId = table[name];
            if (transaction.GetObject(existingId, OpenMode.ForRead) is LinetypeTableRecord existing && IsExpectedPattern(existing, dash, gap))
                return existingId;
            name = $"{rootName}_R{++revision}";
        }
        table.UpgradeOpen();
        var lineType = new LinetypeTableRecord { Name = name, PatternLength = dash + gap, NumDashes = 2, Comments = $"ARAR marca vial: raya {settings.DashMeters:0.###} m; espacio {settings.GapMeters:0.###} m" };
        lineType.SetDashLengthAt(0, dash);
        lineType.SetDashLengthAt(1, -gap);
        ObjectId id = table.Add(lineType);
        transaction.AddNewlyCreatedDBObject(lineType, true);
        return id;
    }

    private static bool IsExpectedPattern(LinetypeTableRecord record, double dash, double gap)
    {
        if (record.NumDashes != 2) return false;
        double tolerance = Math.Max(1e-9, Math.Max(dash, gap) * 1e-9);
        return Math.Abs(record.DashLengthAt(0) - dash) <= tolerance &&
               Math.Abs(record.DashLengthAt(1) + gap) <= tolerance &&
               Math.Abs(record.PatternLength - (dash + gap)) <= tolerance;
    }

    private static void EnsureRegApp(Database database, Transaction transaction, string appName)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(appName)) return;
        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = appName };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static void SetXData(Entity entity, RoadMarkingSettings settings, Transaction transaction)
    {
        var values = new List<TypedValue>();
        using (ResultBuffer? existing = entity.XData)
        {
            TypedValue[]? current = existing?.AsArray();
            if (current is not null)
            {
                bool skip = false;
                foreach (TypedValue value in current)
                {
                    if (value.TypeCode == 1001)
                        skip = string.Equals(value.Value?.ToString(), RegAppName, StringComparison.OrdinalIgnoreCase);
                    if (!skip) values.Add(value);
                }
            }
        }

        values.Add(new TypedValue(1001, RegAppName));
        values.Add(new TypedValue(1000, settings.Dashed ? "D" : "C"));
        values.Add(new TypedValue(1040, settings.WidthMeters));
        values.Add(new TypedValue(1040, settings.DashMeters));
        values.Add(new TypedValue(1040, settings.GapMeters));
        values.Add(new TypedValue(1000, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        using var buffer = new ResultBuffer(values.ToArray());
        entity.XData = buffer;
    }
}
