using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using SectionGeometryModel = ARAR.Civil3D.Models.SectionGeometry;

namespace ARAR.Civil3D.Services;

public sealed record SectionDrawingResult(int EntityCount, IReadOnlyList<string> GroupNames)
{
    public string GroupName => string.Join(", ", GroupNames);
}

/// <summary>Creates a single section as grouped 2D entities in one AutoCAD transaction.</summary>
public sealed class SectionDrawingService
{
    private const string RegAppName = "ARAR_SECCIONES";
    private static readonly (string Name, short Color)[] Layers =
    {
        ("ARAR_SEC_TERRENO", 30), ("ARAR_SEC_RASANTE", 5), ("ARAR_SEC_TALUD", 3),
        ("ARAR_SEC_FIRME", 4), ("ARAR_SEC_DESMONTE", 1), ("ARAR_SEC_TERRAPLEN", 3),
        ("ARAR_SEC_EJE", 2), ("ARAR_SEC_CUADRICULA", 8), ("ARAR_SEC_TEXTO", 7)
    };

    public bool HasExisting(Document document, BaselineChoice baseline, double station)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        BlockTableRecord space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in space)
            if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && Matches(entity, baseline, station)) return true;
        transaction.Commit(); return false;
    }

    public SectionDrawingResult Draw(Document document, SectionDrawRequest request, DrawOptions options, Point3d insertion, bool replaceExisting) =>
        DrawMany(document, new[] { request }, options, insertion, replaceExisting);

    public SectionDrawingResult DrawMany(Document document, IReadOnlyList<SectionDrawRequest> requests, DrawOptions options, Point3d insertion, bool replaceExisting)
    {
        if (requests.Count == 0) throw new ArgumentException("No hay secciones que dibujar.", nameof(requests));
        ValidateDrawInput(requests, options, insertion);
        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        Database database = document.Database;
        EnsureLayers(database, transaction);
        EnsureRegApp(database, transaction);
        BlockTableRecord space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        if (replaceExisting)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToArray())
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && requests.Any(r => Matches(entity, r.Baseline, r.Section.Station)))
                { entity.UpgradeOpen(); entity.Erase(); }
        }

        var groups = (DBDictionary)transaction.GetObject(database.GroupDictionaryId, OpenMode.ForWrite);
        var groupNames = new List<string>();
        int entityCount = 0;
        for (int index = 0; index < requests.Count; index++)
        {
            SectionDrawRequest request = requests[index];
            int column = index % options.RangeColumns, row = index / options.RangeColumns;
            Point3d sectionInsertion = new(insertion.X + column * options.SpacingX, insertion.Y - row * options.SpacingY, 0);
            var created = new ObjectIdCollection();
            SectionGeometryModel corridor = request.Section.Geometry;
            if (options.DrawCorridor) AddPolylines(corridor.Polylines, false, space, transaction, created, sectionInsertion, options, request);
            if (options.DrawTerrain) AddPolylines(request.Terrain.Polylines, true, space, transaction, created, sectionInsertion, options, request);
            if (options.DrawShapes) AddPolygons(corridor.Polygons, database, space, transaction, created, sectionInsertion, options, request,
                message => document.Editor.WriteMessage("\n" + message));
            if (options.DrawEarthwork) AddPolygons(request.Earthwork.Polygons, database, space, transaction, created, sectionInsertion, options, request,
                message => document.Editor.WriteMessage("\n" + message));
            if (options.DrawAxis) AddAxis(corridor, request.Terrain, space, transaction, created, sectionInsertion, options, request);
            if (options.DrawGrid) AddGrid(corridor, request.Terrain, space, transaction, created, sectionInsertion, options, request);
            if (options.DrawLabels) AddLabel(request, space, transaction, created, sectionInsertion, options);
            if (created.Count == 0) continue;

            string baseName = "ARAR_SEC_" + FormatStation(request.Section.Station).Replace('+', '_').Replace('.', '_').Replace('-', 'N');
            string groupName = baseName; int suffix = 1;
            while (groups.Contains(groupName)) groupName = baseName + "_" + suffix++;
            var group = new Group("Sección " + FormatStation(request.Section.Station), selectable: true);
            groups.SetAt(groupName, group); transaction.AddNewlyCreatedDBObject(group, true); group.Append(created);
            groupNames.Add(groupName); entityCount += created.Count;
        }
        if (entityCount == 0) throw new InvalidOperationException("Las opciones seleccionadas no generan entidades.");
        transaction.Commit();
        return new SectionDrawingResult(entityCount, groupNames);
    }

    private static void AddPolylines(IEnumerable<SectionPolyline> lines, bool terrain, BlockTableRecord space, Transaction transaction,
        ObjectIdCollection created, Point3d insertion, DrawOptions options, SectionDrawRequest request)
    {
        foreach (SectionPolyline line in lines)
        {
            if (line.Points.Count < 2) continue;
            string layer = terrain ? "ARAR_SEC_TERRENO" : GetLinkLayer(line.Code);
            var polyline = new Polyline { Layer = layer };
            for (int i = 0; i < line.Points.Count; i++) polyline.AddVertexAt(i, Transform(line.Points[i], insertion, options), 0, 0, 0);
            AddEntity(polyline, space, transaction, created, request);
        }
    }

    private static void AddPolygons(IEnumerable<SectionPolygon> polygons, Database database, BlockTableRecord space, Transaction transaction,
        ObjectIdCollection created, Point3d insertion, DrawOptions options, SectionDrawRequest request, Action<string> reportHatchFailure)
    {
        foreach (SectionPolygon polygon in polygons)
        {
            if (polygon.Points.Count < 3) continue;
            string layer = polygon.Style switch { "Terraplen" => "ARAR_SEC_TERRAPLEN", "Desmonte" => "ARAR_SEC_DESMONTE", _ => "ARAR_SEC_FIRME" };
            var polyline = new Polyline { Layer = layer, Closed = true };
            foreach (SectionPoint point in polygon.Points)
                polyline.AddVertexAt(polyline.NumberOfVertices, Transform(point, insertion, options), 0, 0, 0);
            AddEntity(polyline, space, transaction, created, request);
            // Un Hatch inválido no debe cancelar los contornos ni el resto de la sección.
            // Se aísla en una transacción anidada; ante fallo se revierte solo el Hatch.
            try
            {
                using Transaction hatchTransaction = database.TransactionManager.StartTransaction();
                var boundaryIds = new ObjectIdCollection { polyline.ObjectId };
                var hatch = new Hatch { Layer = layer, Associative = false };
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                space.AppendEntity(hatch);
                hatchTransaction.AddNewlyCreatedDBObject(hatch, true);
                hatch.AppendLoop(HatchLoopTypes.Outermost, boundaryIds);
                hatch.EvaluateHatch(true);
                ApplyMetadata(hatch, request);
                ObjectId hatchId = hatch.ObjectId;
                hatchTransaction.Commit();
                created.Add(hatchId);
            }
            catch (System.Exception ex)
            {
                reportHatchFailure("Se creó el contorno, pero se omitió su sombreado Hatch: " + ex.Message);
            }
        }
    }

    private static void AddAxis(SectionGeometryModel corridor, SectionGeometryModel terrain, BlockTableRecord space, Transaction transaction,
        ObjectIdCollection created, Point3d insertion, DrawOptions options, SectionDrawRequest request)
    {
        var points = corridor.AllPoints.Concat(terrain.AllPoints).ToArray(); if (points.Length == 0) return;
        var start = Transform(new SectionPoint(0, points.Min(p => p.Elevation)), insertion, options);
        var end = Transform(new SectionPoint(0, points.Max(p => p.Elevation)), insertion, options);
        AddEntity(new Line(new Point3d(start.X, start.Y, 0), new Point3d(end.X, end.Y, 0)) { Layer = "ARAR_SEC_EJE" }, space, transaction, created, request);
    }

    private static void AddGrid(SectionGeometryModel corridor, SectionGeometryModel terrain, BlockTableRecord space, Transaction transaction,
        ObjectIdCollection created, Point3d insertion, DrawOptions options, SectionDrawRequest request)
    {
        var points = corridor.AllPoints.Concat(terrain.AllPoints).ToArray(); if (points.Length == 0) return;
        double minX = points.Min(p => p.Offset), maxX = points.Max(p => p.Offset), minY = points.Min(p => p.Elevation), maxY = points.Max(p => p.Elevation);
        for (double x = Math.Ceiling(minX / 5) * 5; x <= maxX; x += 5)
            AddEntity(new Line(ToWcs(new SectionPoint(x, minY), insertion, options), ToWcs(new SectionPoint(x, maxY), insertion, options)) { Layer = "ARAR_SEC_CUADRICULA" }, space, transaction, created, request);
        for (double y = Math.Ceiling(minY / 5) * 5; y <= maxY; y += 5)
            AddEntity(new Line(ToWcs(new SectionPoint(minX, y), insertion, options), ToWcs(new SectionPoint(maxX, y), insertion, options)) { Layer = "ARAR_SEC_CUADRICULA" }, space, transaction, created, request);
    }

    private static void AddLabel(SectionDrawRequest request, BlockTableRecord space, Transaction transaction,
        ObjectIdCollection created, Point3d insertion, DrawOptions options)
    {
        string label = $"{request.Baseline.CorridorName} | {request.Baseline.BaselineName} | PK {FormatStation(request.Section.Station)}";
        var text = new DBText { Position = insertion, Height = Math.Max(options.HorizontalScale * 2.5, 0.15), TextString = label, Layer = "ARAR_SEC_TEXTO" };
        AddEntity(text, space, transaction, created, request);
    }

    private static void AddEntity(Entity entity, BlockTableRecord space, Transaction transaction, ObjectIdCollection created, SectionDrawRequest request)
    {
        space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
        ApplyMetadata(entity, request);
        created.Add(entity.ObjectId);
    }

    private static void ApplyMetadata(Entity entity, SectionDrawRequest request)
    {
        using var data = new ResultBuffer(new TypedValue(1001, RegAppName), new TypedValue(1000, request.Baseline.CorridorId.Handle.ToString()),
            new TypedValue(1000, Limit(request.Baseline.CorridorName, 240)), new TypedValue(1000, Limit(request.Baseline.BaselineName, 240)), new TypedValue(1040, request.Section.Station),
            new TypedValue(1000, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        entity.XData = data;
    }

    private static void EnsureLayers(Database database, Transaction transaction)
    {
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach ((string name, short color) in Layers)
        {
            if (table.Has(name)) continue;
            table.UpgradeOpen(); var layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
            table.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true);
        }
    }

    private static void EnsureRegApp(Database database, Transaction transaction)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(RegAppName)) return;
        table.UpgradeOpen(); var record = new RegAppTableRecord { Name = RegAppName }; table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static bool Matches(Entity entity, BaselineChoice baseline, double station)
    {
        ResultBuffer? data = entity.GetXDataForApplication(RegAppName); if (data is null) return false;
        using (data)
        {
            TypedValue[] values = data.AsArray();
            return values.Length >= 6 && string.Equals(values[1].Value?.ToString(), baseline.CorridorId.Handle.ToString(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(values[3].Value?.ToString(), baseline.BaselineName, StringComparison.Ordinal) &&
                values[4].Value is double value && Math.Abs(value - station) <= 1e-6;
        }
    }

    private static string GetLinkLayer(string code)
    {
        string value = code.ToUpperInvariant();
        if (value.Contains("TOP") || value.Contains("PAVE") || value.Contains("CROWN")) return "ARAR_SEC_RASANTE";
        return value.Contains("DAYLIGHT") || value.Contains("SLOPE") || value.Contains("TALUD") ? "ARAR_SEC_TALUD" : "ARAR_SEC_RASANTE";
    }

    private static Point2d Transform(SectionPoint point, Point3d insertion, DrawOptions options) =>
        new(insertion.X + point.Offset * options.HorizontalScale, insertion.Y + (point.Elevation - options.ReferenceElevation) * options.HorizontalScale * options.VerticalExaggeration);
    private static Point3d ToWcs(SectionPoint point, Point3d insertion, DrawOptions options)
    {
        Point2d p = Transform(point, insertion, options); return new Point3d(p.X, p.Y, 0);
    }
    private static string FormatStation(double station) { double value = Math.Abs(station); long km = (long)Math.Floor(value / 1000); return $"{(station < 0 ? "-" : "")}{km}+{value - km * 1000:000.000}"; }
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static void ValidateDrawInput(IReadOnlyList<SectionDrawRequest> requests, DrawOptions options, Point3d insertion)
    {
        if (!double.IsFinite(insertion.X) || !double.IsFinite(insertion.Y) || !double.IsFinite(insertion.Z))
            throw new InvalidOperationException("El punto de inserción contiene coordenadas no válidas.");
        if (!double.IsFinite(options.HorizontalScale) || options.HorizontalScale <= 0 ||
            !double.IsFinite(options.VerticalExaggeration) || options.VerticalExaggeration <= 0 ||
            !double.IsFinite(options.ReferenceElevation) || options.RangeColumns < 1 ||
            !double.IsFinite(options.SpacingX) || options.SpacingX <= 0 || !double.IsFinite(options.SpacingY) || options.SpacingY <= 0)
            throw new InvalidOperationException("Las opciones de escala o disposición no son válidas.");

        foreach (SectionDrawRequest request in requests)
        {
            if (!double.IsFinite(request.Section.Station)) throw new InvalidOperationException("Una sección tiene un PK no válido.");
            IEnumerable<SectionPoint> allPoints = request.Section.Geometry.AllPoints
                .Concat(request.Terrain.AllPoints).Concat(request.Earthwork.AllPoints);
            if (allPoints.Any(point => !double.IsFinite(point.Offset) || !double.IsFinite(point.Elevation)))
                throw new InvalidOperationException("Una sección contiene coordenadas no válidas; no se modificó el dibujo.");
        }
    }
}
