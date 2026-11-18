using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Services;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilAlignmentCurve = Autodesk.Civil.DatabaseServices.AlignmentCurve;
using CivilAlignmentEntity = Autodesk.Civil.DatabaseServices.AlignmentEntity;

namespace ARAR.Civil3D.Tools;

/// <summary>Crea una tabla estática de coordenadas y rumbos de una alineación.</summary>
public sealed class AlignmentPointsTableTool : ToolBase
{
    private enum PointDefinitionMode
    {
        Interval,
        List,
        Vertices
    }

    private const double DefaultInterval = 20.0;
    private const double StationTolerance = 1e-8;

    public override string Id => "ARAR.ALIGNMENT_POINTS_TABLE";
    public override string Title => "Tabla de puntos de alineación";
    public override string Panel => "Alineaciones";
    public override string Description => "Genera una tabla estática con coordenadas, distancias y orientaciones de una alineación.";
    public override string Tooltip => "Crea una tabla de puntos, distancias rectas y rumbos por PK.";
    public override string CommandName => "ARARTABLAPUNTOS";
    public override string IconResourceKey => "DefaultIcon";
    public override int Order => 10;

    protected override void ExecuteCore()
    {
        Autodesk.AutoCAD.ApplicationServices.Document? document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }

        PromptEntityResult selection = PromptAlignment(document.Editor);
        if (selection.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }

        AlignmentMetadata metadata = ReadMetadata(document.Database, selection.ObjectId);
        IReadOnlyList<double> stations = PromptStations(document.Editor, metadata);
        if (stations.Count == 0)
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }

        PromptPointResult insertion = document.Editor.GetPoint(new PromptPointOptions("\nIndique el punto de inserción de la tabla: "));
        if (insertion.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }

        List<AlignmentTablePoint> points = ReadPoints(document.Database, selection.ObjectId, stations);
        using (document.LockDocument())
        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                document.Database.CurrentSpaceId,
                OpenMode.ForWrite);

            using Table table = AlignmentPointTableBuilder.Create(
                document.Database,
                insertion.Value,
                metadata.Name,
                metadata.Description,
                stations[0],
                stations[^1],
                points);
            currentSpace.AppendEntity(table);
            transaction.AddNewlyCreatedDBObject(table, true);
            transaction.Commit();
        }

        WriteMessage($"Tabla creada: {points.Count} puntos y {Math.Max(0, points.Count - 1)} tramos. Es una tabla estática, no vinculada a la alineación.");
    }

    [CommandMethod("ARARTABLAPUNTOS", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    private static PromptEntityResult PromptAlignment(Editor editor)
    {
        var options = new PromptEntityOptions("\nSeleccione una alineación: ");
        options.SetRejectMessage("\nDebe seleccionar una alineación de Civil 3D.");
        options.AddAllowedClass(typeof(CivilAlignment), exactMatch: false);
        return editor.GetEntity(options);
    }

    private static AlignmentMetadata ReadMetadata(Database database, ObjectId alignmentId)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        CivilAlignment alignment = (CivilAlignment)transaction.GetObject(alignmentId, OpenMode.ForRead);
        var entityStations = new List<double> { alignment.StartingStation, alignment.EndingStation };
        foreach (CivilAlignmentEntity entity in alignment.Entities)
        {
            if (entity is CivilAlignmentCurve curve)
            {
                entityStations.Add(curve.StartStation);
                entityStations.Add(curve.EndStation);
            }
        }

        double[] vertices = NormalizeStations(entityStations, alignment.StartingStation, alignment.EndingStation);
        var metadata = new AlignmentMetadata(
            alignment.Name,
            alignment.Description ?? string.Empty,
            alignment.StartingStation,
            alignment.EndingStation,
            vertices);
        transaction.Commit();
        return metadata;
    }

    private static IReadOnlyList<double> PromptStations(Editor editor, AlignmentMetadata alignment)
    {
        var modeOptions = new PromptKeywordOptions("\n¿Cómo desea definir los puntos? [Intervalo/Lista/Vertices] <Intervalo>: ")
        {
            AllowNone = true
        };
        modeOptions.Keywords.Add("Intervalo");
        modeOptions.Keywords.Add("Lista");
        modeOptions.Keywords.Add("Vertices");
        PromptResult modeResult = editor.GetKeywords(modeOptions);
        if (modeResult.Status == PromptStatus.None ||
            (modeResult.Status == PromptStatus.OK && string.IsNullOrEmpty(modeResult.StringResult)))
            return BuildIntervalStations(editor, alignment);
        if (modeResult.Status != PromptStatus.OK)
            return Array.Empty<double>();

        PointDefinitionMode mode = modeResult.StringResult switch
        {
            "Lista" => PointDefinitionMode.List,
            "Vertices" => PointDefinitionMode.Vertices,
            _ => PointDefinitionMode.Interval
        };

        return mode switch
        {
            PointDefinitionMode.List => BuildListStations(editor, alignment),
            PointDefinitionMode.Vertices => alignment.VertexStations,
            _ => BuildIntervalStations(editor, alignment)
        };
    }

    private static IReadOnlyList<double> BuildIntervalStations(Editor editor, AlignmentMetadata alignment)
    {
        if (!TryGetDouble(editor, $"\nPK inicial <{AlignmentPointTableCalculator.FormatStation(alignment.StartStation)}>: ", alignment.StartStation, out double start) ||
            !TryGetDouble(editor, $"\nPK final <{AlignmentPointTableCalculator.FormatStation(alignment.EndStation)}>: ", alignment.EndStation, out double end) ||
            !TryGetDouble(editor, $"\nPaso entre puntos (m) <{DefaultInterval:0.###}>: ", DefaultInterval, out double step))
            return Array.Empty<double>();

        ValidateRange(start, alignment);
        ValidateRange(end, alignment);
        if (end <= start)
            throw new ArgumentException("El PK final debe ser mayor que el PK inicial.");
        if (step <= 0)
            throw new ArgumentException("El paso debe ser mayor que cero.");

        var stations = new List<double> { start };
        double next = start + step;
        int safetyCounter = 0;
        while (next < end - StationTolerance)
        {
            stations.Add(next);
            next += step;
            if (++safetyCounter > 1_000_000)
                throw new InvalidOperationException("El intervalo genera demasiados puntos; aumente el paso.");
        }
        if (Math.Abs(stations[^1] - end) > StationTolerance)
            stations.Add(end);
        else
            stations[^1] = end;
        return stations;
    }

    private static IReadOnlyList<double> BuildListStations(Editor editor, AlignmentMetadata alignment)
    {
        var options = new PromptStringOptions("\nEscriba los PK separados por comas o espacios (ej.: 0+000, 0+039.868, 1250): ")
        {
            AllowSpaces = true
        };
        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return Array.Empty<double>();

        string[] tokens = result.StringResult.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            throw new ArgumentException("Introduzca al menos un PK.");

        double[] stations = tokens.Select(AlignmentPointTableCalculator.ParseStation).ToArray();
        foreach (double station in stations)
            ValidateRange(station, alignment);
        return NormalizeStations(stations, alignment.StartStation, alignment.EndStation);
    }

    private static List<AlignmentTablePoint> ReadPoints(Database database, ObjectId alignmentId, IReadOnlyList<double> stations)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        CivilAlignment alignment = (CivilAlignment)transaction.GetObject(alignmentId, OpenMode.ForRead);
        var points = new List<AlignmentTablePoint>(stations.Count);
        foreach (double station in stations)
        {
            double easting = 0;
            double northing = 0;
            alignment.PointLocation(station, 0, ref easting, ref northing);
            points.Add(new AlignmentTablePoint(station, easting, northing));
        }
        transaction.Commit();
        return points;
    }

    private static bool TryGetDouble(Editor editor, string message, double defaultValue, out double value)
    {
        var options = new PromptDoubleOptions(message)
        {
            AllowNone = true,
            AllowZero = true,
            AllowNegative = true,
            UseDefaultValue = true,
            DefaultValue = defaultValue
        };
        PromptDoubleResult result = editor.GetDouble(options);
        value = result.Value;
        return result.Status == PromptStatus.OK || result.Status == PromptStatus.None;
    }

    private static void ValidateRange(double station, AlignmentMetadata alignment)
    {
        if (station < alignment.StartStation - StationTolerance || station > alignment.EndStation + StationTolerance)
            throw new ArgumentOutOfRangeException(nameof(station),
                $"El PK {AlignmentPointTableCalculator.FormatStation(station)} está fuera del rango de la alineación ({AlignmentPointTableCalculator.FormatStation(alignment.StartStation)} a {AlignmentPointTableCalculator.FormatStation(alignment.EndStation)}).");
    }

    private static double[] NormalizeStations(IEnumerable<double> stations, double start, double end)
    {
        var result = stations
            .Where(s => s >= start - StationTolerance && s <= end + StationTolerance)
            .OrderBy(s => s)
            .Aggregate(new List<double>(), (list, station) =>
            {
                if (list.Count == 0 || Math.Abs(list[^1] - station) > StationTolerance)
                    list.Add(station);
                return list;
            });
        return result.ToArray();
    }

    private sealed record AlignmentMetadata(
        string Name,
        string Description,
        double StartStation,
        double EndStation,
        double[] VertexStations);
}
