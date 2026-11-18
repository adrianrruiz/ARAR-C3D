using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ARAR.Civil3D.Services;

public sealed record CebreadoResult(int StripesCreated, int StripesSkipped);

/// <summary>Construye la geometría 2D del cebreado entre dos límites polilineales.</summary>
public sealed class CebreadoService
{
    private const double StripeWidthMeters = 1.0;
    private const double AngleAdjacentMeters = 2.0;
    private const double AngleOppositeMeters = 1.0;
    private const double SampleStepMeters = 0.05;
    private const double GeometryTolerance = 1e-8;
    private const int MaximumStripeCount = 2000;

    private readonly record struct Vec2(double X, double Y)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y));
        public Vec2 Left => new(-Y, X);
        public Vec2 Right => new(Y, -X);
        public double Dot(Vec2 other) => (X * other.X) + (Y * other.Y);
        public double Cross(Vec2 other) => (X * other.Y) - (Y * other.X);
        public Vec2 Unit()
        {
            double length = Length;
            if (length <= GeometryTolerance)
                throw new InvalidOperationException("No se pudo determinar la dirección local de la polilínea.");
            return new Vec2(X / length, Y / length);
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 vector, double scale) => new(vector.X * scale, vector.Y * scale);
    }

    private readonly record struct P2(double X, double Y)
    {
        public static P2 From(Point3d point) => new(point.X, point.Y);
        public Vec2 To(P2 other) => new(other.X - X, other.Y - Y);
        public P2 Offset(Vec2 vector) => new(X + vector.X, Y + vector.Y);
        public double DistanceTo(P2 other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
    }

    private sealed record StripePolygon(P2 LaneStart, P2 BermStart, P2 BermEnd, P2 LaneEnd);

    public CebreadoResult Create(
        Document document,
        ObjectId laneId,
        ObjectId bermId,
        Point3d startPoint,
        Point3d directionPoint,
        double stripeGapMeters,
        double laneClearanceMeters,
        bool mirrorStripes,
        double sectionLengthMeters)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (stripeGapMeters <= 0 || laneClearanceMeters <= 0 || sectionLengthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectionLengthMeters), "Las distancias deben ser mayores que cero.");

        Database database = document.Database;
        double unitsPerMeter = DrawingUnitService.MetersToDrawingUnits(database, 1.0);
        double stripeWidth = DrawingUnitService.MetersToDrawingUnits(database, StripeWidthMeters);
        double stripeGap = DrawingUnitService.MetersToDrawingUnits(database, stripeGapMeters);
        double laneClearance = DrawingUnitService.MetersToDrawingUnits(database, laneClearanceMeters);
        double sectionLength = DrawingUnitService.MetersToDrawingUnits(database, sectionLengthMeters);
        double sampleStep = DrawingUnitService.MetersToDrawingUnits(database, SampleStepMeters);
        double sineAngle = AngleOppositeMeters / Math.Sqrt(
            (AngleAdjacentMeters * AngleAdjacentMeters) + (AngleOppositeMeters * AngleOppositeMeters));
        double stationStep = (stripeWidth + stripeGap) / sineAngle;

        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = database.TransactionManager.StartTransaction();

        Entity laneEntity = transaction.GetObject(laneId, OpenMode.ForRead, false) as Entity
            ?? throw new InvalidOperationException("No se pudo abrir la polilínea del borde de carril.");
        Entity bermEntity = transaction.GetObject(bermId, OpenMode.ForRead, false) as Entity
            ?? throw new InvalidOperationException("No se pudo abrir la polilínea del borde de berma.");

        if (laneEntity is not Curve lane || bermEntity is not Curve berm ||
            !IsSupportedPolyline(laneEntity) || !IsSupportedPolyline(bermEntity))
            throw new InvalidOperationException("Los dos límites deben ser polilíneas 2D o 3D.");
        if (IsClosedPolyline(laneEntity) || IsClosedPolyline(bermEntity))
            throw new InvalidOperationException("Los límites deben ser polilíneas abiertas.");

        double laneLength = GetCurveLength(lane);
        double bermLength = GetCurveLength(berm);
        if (laneLength <= GeometryTolerance || bermLength <= GeometryTolerance)
            throw new InvalidOperationException("Una de las polilíneas no tiene longitud suficiente.");

        Point3d projectedStart = lane.GetClosestPointTo(startPoint, extend: false);
        Point3d projectedDirection = lane.GetClosestPointTo(directionPoint, extend: false);
        double startStation = lane.GetDistAtPoint(projectedStart);
        double directionStation = lane.GetDistAtPoint(projectedDirection);
        if (Math.Abs(directionStation - startStation) <= GeometryTolerance)
            throw new InvalidOperationException("El punto de dirección debe ser distinto del punto inicial sobre el borde.");
        int directionSign = directionStation > startStation ? 1 : -1;

        IReadOnlyList<P2> laneOffsetSamples = SampleOffsetLane(lane, berm, laneLength, laneClearance, sampleStep);
        IReadOnlyList<P2> bermSamples = SampleCurve(berm, bermLength, sampleStep);
        var polygons = new List<StripePolygon>();
        int skipped = 0;
        int stripeCount = 0;

        while (stripeCount < MaximumStripeCount)
        {
            double station = startStation + (directionSign * stripeCount * stationStep);
            double traveled = Math.Abs(station - startStation);
            if (traveled > sectionLength + GeometryTolerance || station < 0 || station > laneLength)
                break;

            Point3d lanePoint3d = lane.GetPointAtDist(station);
            P2 lanePoint = P2.From(lanePoint3d);
            Vec2 tangent = GetTangent(lane, station, laneLength, sampleStep) * directionSign;
            Point3d nearestBermPoint = berm.GetClosestPointTo(lanePoint3d, extend: false);
            Vec2 toBerm = lanePoint.To(P2.From(nearestBermPoint));
            Vec2 leftNormal = tangent.Left;
            double sideValue = leftNormal.Dot(toBerm);
            if (Math.Abs(sideValue) <= GeometryTolerance)
            {
                skipped++;
                stripeCount++;
                continue;
            }

            Vec2 inward = leftNormal * Math.Sign(sideValue);
            Vec2 stripeDirection = ((tangent * AngleAdjacentMeters) + (inward * AngleOppositeMeters)).Unit();
            Vec2 patternNormal = stripeDirection.Right;
            if (patternNormal.Dot(tangent) < 0)
                patternNormal = patternNormal * -1;

            P2 secondLineAnchor = lanePoint.Offset(patternNormal * stripeWidth);
            if (TryGetClosestIntersection(laneOffsetSamples, lanePoint, stripeDirection, out P2 laneStart) &&
                TryGetClosestIntersection(laneOffsetSamples, secondLineAnchor, stripeDirection, out P2 laneEnd) &&
                TryGetForwardIntersection(bermSamples, lanePoint, stripeDirection, laneStart, out P2 bermStart) &&
                TryGetForwardIntersection(bermSamples, secondLineAnchor, stripeDirection, laneEnd, out P2 bermEnd) &&
                HasUsableArea(laneStart, bermStart, bermEnd, laneEnd))
            {
                var stripe = new StripePolygon(laneStart, bermStart, bermEnd, laneEnd);
                polygons.Add(mirrorStripes ? MirrorStripeAcrossRoadNormal(stripe, tangent, berm) : stripe);
            }
            else
            {
                skipped++;
            }

            stripeCount++;
        }

        if (stripeCount == MaximumStripeCount)
            document.Editor.WriteMessage("\nAviso: se alcanzó el máximo de 2000 franjas en una operación.");

        if (polygons.Count == 0)
        {
            transaction.Commit();
            return new CebreadoResult(0, skipped);
        }

        var currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        ObjectId currentLayerId = database.Clayer;
        foreach (StripePolygon polygon in polygons)
        {
            var boundary = new Polyline(4)
            {
                Closed = true,
                Elevation = 0.0,
                LayerId = currentLayerId
            };
            boundary.SetDatabaseDefaults(database);
            boundary.AddVertexAt(0, new Point2d(polygon.LaneStart.X, polygon.LaneStart.Y), 0.0, 0.0, 0.0);
            boundary.AddVertexAt(1, new Point2d(polygon.BermStart.X, polygon.BermStart.Y), 0.0, 0.0, 0.0);
            boundary.AddVertexAt(2, new Point2d(polygon.BermEnd.X, polygon.BermEnd.Y), 0.0, 0.0, 0.0);
            boundary.AddVertexAt(3, new Point2d(polygon.LaneEnd.X, polygon.LaneEnd.Y), 0.0, 0.0, 0.0);
            currentSpace.AppendEntity(boundary);
            transaction.AddNewlyCreatedDBObject(boundary, true);

            var hatch = new Hatch
            {
                Associative = false,
                LayerId = currentLayerId
            };
            hatch.SetDatabaseDefaults(database);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            currentSpace.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            hatch.EvaluateHatch(true);
        }

        transaction.Commit();
        return new CebreadoResult(polygons.Count, skipped);
    }

    private static bool IsSupportedPolyline(Entity entity) => entity is Polyline or Polyline2d or Polyline3d;

    private static bool IsClosedPolyline(Entity entity) => entity switch
    {
        Polyline polyline => polyline.Closed,
        Polyline2d polyline2d => polyline2d.Closed,
        Polyline3d polyline3d => polyline3d.Closed,
        _ => true
    };

    private static double GetCurveLength(Curve curve) => curve.GetDistanceAtParameter(curve.EndParam);

    private static IReadOnlyList<P2> SampleCurve(Curve curve, double length, double sampleStep)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(length / sampleStep));
        var points = new List<P2>(steps + 1);
        for (int index = 0; index <= steps; index++)
        {
            double distance = length * index / steps;
            P2 point = P2.From(curve.GetPointAtDist(distance));
            if (points.Count == 0 || point.DistanceTo(points[^1]) > GeometryTolerance)
                points.Add(point);
        }
        return points;
    }

    private static IReadOnlyList<P2> SampleOffsetLane(Curve lane, Curve berm, double laneLength, double offset, double sampleStep)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(laneLength / sampleStep));
        var points = new List<P2>(steps + 1);
        for (int index = 0; index <= steps; index++)
        {
            double station = laneLength * index / steps;
            Point3d lanePoint3d = lane.GetPointAtDist(station);
            P2 lanePoint = P2.From(lanePoint3d);
            Vec2 tangent = GetTangent(lane, station, laneLength, sampleStep);
            Point3d nearestBermPoint = berm.GetClosestPointTo(lanePoint3d, extend: false);
            Vec2 toBerm = lanePoint.To(P2.From(nearestBermPoint));
            Vec2 leftNormal = tangent.Left;
            double sideValue = leftNormal.Dot(toBerm);
            if (Math.Abs(sideValue) <= GeometryTolerance)
                throw new InvalidOperationException("No se pudo determinar qué lado de la línea de carril mira hacia la berma.");

            P2 offsetPoint = lanePoint.Offset(leftNormal * (Math.Sign(sideValue) * offset));
            if (points.Count == 0 || offsetPoint.DistanceTo(points[^1]) > GeometryTolerance)
                points.Add(offsetPoint);
        }
        return points;
    }

    private static Vec2 GetTangent(Curve curve, double station, double curveLength, double sampleStep)
    {
        double delta = Math.Max(sampleStep, curveLength * 1e-8);
        double before = Math.Max(0, station - delta);
        double after = Math.Min(curveLength, station + delta);
        P2 a = P2.From(curve.GetPointAtDist(before));
        P2 b = P2.From(curve.GetPointAtDist(after));
        if (b.DistanceTo(a) <= GeometryTolerance)
            throw new InvalidOperationException("No se pudo calcular la dirección de la polilínea en el punto indicado.");
        return a.To(b).Unit();
    }

    private static StripePolygon MirrorStripeAcrossRoadNormal(StripePolygon stripe, Vec2 tangent, Curve berm)
    {
        // Refleja la franja terminada sobre el eje transversal que pasa por su centro.
        P2 center = new(
            (stripe.LaneStart.X + stripe.BermStart.X + stripe.BermEnd.X + stripe.LaneEnd.X) / 4.0,
            (stripe.LaneStart.Y + stripe.BermStart.Y + stripe.BermEnd.Y + stripe.LaneEnd.Y) / 4.0);
        Vec2 axisNormal = tangent.Unit();

        StripePolygon mirrored = new(
            MirrorPointAcrossRoadNormal(stripe.LaneStart, center, axisNormal),
            MirrorPointAcrossRoadNormal(stripe.BermStart, center, axisNormal),
            MirrorPointAcrossRoadNormal(stripe.BermEnd, center, axisNormal),
            MirrorPointAcrossRoadNormal(stripe.LaneEnd, center, axisNormal));

        // El espejo invierte la inclinación, pero en curvas puede dejar las puntas
        // exteriores ligeramente más allá de la berma. Proyecta solo esos vértices
        // sobre el límite real para que el sombreado no lo sobrepase.
        Point3d bermStart = berm.GetClosestPointTo(
            new Point3d(mirrored.BermStart.X, mirrored.BermStart.Y, 0.0),
            extend: false);
        Point3d bermEnd = berm.GetClosestPointTo(
            new Point3d(mirrored.BermEnd.X, mirrored.BermEnd.Y, 0.0),
            extend: false);

        return new StripePolygon(
            mirrored.LaneStart,
            P2.From(bermStart),
            P2.From(bermEnd),
            mirrored.LaneEnd);
    }

    private static P2 MirrorPointAcrossRoadNormal(P2 point, P2 center, Vec2 tangent)
    {
        Vec2 fromCenter = center.To(point);
        Vec2 mirrored = fromCenter - (tangent * (2.0 * fromCenter.Dot(tangent)));
        return center.Offset(mirrored);
    }

    private static bool TryGetClosestIntersection(IReadOnlyList<P2> boundary, P2 anchor, Vec2 direction, out P2 intersection)
    {
        List<(P2 Point, double T)> hits = FindIntersections(boundary, anchor, direction);
        if (hits.Count == 0)
        {
            intersection = default;
            return false;
        }

        intersection = hits.OrderBy(hit => hit.Point.DistanceTo(anchor)).First().Point;
        return true;
    }

    private static bool TryGetForwardIntersection(
        IReadOnlyList<P2> boundary,
        P2 anchor,
        Vec2 direction,
        P2 afterPoint,
        out P2 intersection)
    {
        double minimumT = anchor.To(afterPoint).Dot(direction) + GeometryTolerance;
        List<(P2 Point, double T)> hits = FindIntersections(boundary, anchor, direction)
            .Where(hit => hit.T > minimumT)
            .OrderBy(hit => hit.T)
            .ToList();
        if (hits.Count == 0)
        {
            intersection = default;
            return false;
        }

        intersection = hits[0].Point;
        return true;
    }

    private static List<(P2 Point, double T)> FindIntersections(IReadOnlyList<P2> points, P2 origin, Vec2 direction)
    {
        var hits = new List<(P2 Point, double T)>();
        for (int index = 0; index + 1 < points.Count; index++)
        {
            P2 a = points[index];
            P2 b = points[index + 1];
            Vec2 segment = a.To(b);
            double denominator = direction.Cross(segment);
            if (Math.Abs(denominator) <= GeometryTolerance)
                continue;

            Vec2 toA = origin.To(a);
            double t = toA.Cross(segment) / denominator;
            double u = toA.Cross(direction) / denominator;
            if (u < -GeometryTolerance || u > 1 + GeometryTolerance)
                continue;

            var hit = new P2(origin.X + direction.X * t, origin.Y + direction.Y * t);
            if (!hits.Any(existing => Math.Abs(existing.T - t) <= GeometryTolerance))
                hits.Add((hit, t));
        }
        return hits;
    }

    private static bool HasUsableArea(P2 a, P2 b, P2 c, P2 d)
    {
        double twiceArea =
            (a.X * b.Y - b.X * a.Y) +
            (b.X * c.Y - c.X * b.Y) +
            (c.X * d.Y - d.X * c.Y) +
            (d.X * a.Y - a.X * d.Y);
        return Math.Abs(twiceArea) > GeometryTolerance;
    }
}
