using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using ARAR.Civil3D.UI;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Tools;

/// <summary>Configura y crea o adapta marcas viales 2D.</summary>
public sealed class DrawRoadMarkingTool : ToolBase
{
    private const string SelectionDxfTypes = "LINE,ARC,CIRCLE,LWPOLYLINE,POLYLINE,SPLINE,AECC_FEATURE_LINE";
    private readonly RoadMarkingDrawingService _drawingService = new();

    public override string Id => "ARAR.DRAW_ROAD_MARKING";
    public override string Title => "Dibujar Marca vial";
    public override string Panel => "SEÑALIZACIÓN VIAL";
    public override string Description => "Crea marcas viales con ancho, color y tipo de línea configurables.";
    public override string Tooltip => "Dibujar Marca vial";
    public override string CommandName => "ARAR_MARCA_VIAL";
    public override string IconResourceKey => "DrawRoadMarkingIcon";
    public override int Order => 10;

    protected override void ExecuteCore()
    {
        Document? document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }
        if (!DrawingUnitService.ConfirmMetricWorkflow(document))
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        var dialog = new RoadMarkingOptionsWindow(RoadMarkingPreferences.Load());
        if (AcadApplication.ShowModalWindow(dialog) != true || dialog.Settings is not RoadMarkingSettings settings)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }
        RoadMarkingPreferences.Save(settings);
        ReportLinetypeScaleModes(document);

        if (dialog.UseExisting)
            ApplyToSelected(document, settings);
        else
            DrawNew(document, settings);
    }

    [CommandMethod("ARAR_MARCA_VIAL", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    private static void ReportLinetypeScaleModes(Document document)
    {
        try
        {
            object? msltscaleValue = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("MSLTSCALE");
            object? psltscaleValue = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("PSLTSCALE");
            int msltscale = Convert.ToInt32(msltscaleValue, System.Globalization.CultureInfo.InvariantCulture);
            int psltscale = Convert.ToInt32(psltscaleValue, System.Globalization.CultureInfo.InvariantCulture);
            if (msltscale != 0 || psltscale != 0)
                document.Editor.WriteMessage($"\nAviso: la marca compensa LTSCALE con 1/LTSCALE; MSLTSCALE={msltscale} y PSLTSCALE={psltscale} pueden cambiar la representación en escalas anotativas o ventanas gráficas. Compruebe la apariencia en presentación.");
        }
        catch (AcException ex)
        {
            AddinLogger.Error("No se pudieron consultar MSLTSCALE y PSLTSCALE.", ex);
            document.Editor.WriteMessage("\nAviso: no se pudo consultar MSLTSCALE/PSLTSCALE; se compensará LTSCALE con 1/LTSCALE.");
        }
    }

    private void ApplyToSelected(Document document, RoadMarkingSettings settings)
    {
        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\nSeleccione los objetos de marca vial: ",
            MessageForRemoval = "\nRetire los objetos que no desee procesar: "
        };
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, SelectionDxfTypes) });
        PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK || selection.Value is null)
        {
            WriteMessage("No se seleccionaron objetos; no se modificó el dibujo.");
            return;
        }

        try
        {
            RoadMarkingProcessResult result = _drawingService.ApplyToExisting(document, selection.Value.GetObjectIds(), settings);
            WriteMessage($"Marca vial: {result.Modified} objetos modificados, {result.Replaced} sustituidos y {result.Omitted.Count} omitidos.");
            if (result.SplineApproximated)
                WriteMessage("Aviso: las SPLINE se aproximaron con tolerancia de 0,005 m.");
            foreach (string reason in result.Omitted)
                WriteMessage("  · " + reason);
        }
        catch (AcException ex)
        {
            AddinLogger.Error("Falló el paso de modificación de objetos de marca vial.", ex);
            WriteMessage("No se modificaron objetos porque falló la transacción de marca vial: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Falló el paso de modificación de objetos de marca vial.", ex);
            WriteMessage("No se modificaron objetos porque falló la transacción de marca vial: " + ex.Message);
        }
    }

    private void DrawNew(Document document, RoadMarkingSettings settings)
    {
        object? fillMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("FILLMODE");
        if (Convert.ToInt32(fillMode, System.Globalization.CultureInfo.InvariantCulture) == 0)
            document.Editor.WriteMessage("\nAviso: FILLMODE=0; el ancho de las polilíneas se verá hueco. No se ha cambiado la variable.");
        var vertices = new List<Point3d>();
        var bulges = new List<double>();
        bool closed = false;
        Matrix3d ucs = document.Editor.CurrentUserCoordinateSystem;
        PromptPointResult first = document.Editor.GetPoint(new PromptPointOptions("\nIndique el primer punto de la marca vial: ") { AllowNone = false });
        if (first.Status != PromptStatus.OK)
        {
            WriteMessage("Dibujo cancelado; no se modificó el dibujo.");
            return;
        }
        vertices.Add(first.Value.TransformBy(ucs));
        bulges.Add(0);

        bool canceled = false;
        while (true)
        {
            if (vertices.Count < 2)
            {
                PreviewPointResult result = GetPreviewPoint(document, vertices, bulges, settings,
                    "\nSiguiente punto o [Arco/Deshacer/Terminar]: ", "Arco Deshacer Terminar");
                if (result.Status == PromptStatus.Cancel) { canceled = true; break; }
                if (result.Status == PromptStatus.Keyword)
                {
                    if (result.Keyword.Equals("Deshacer", StringComparison.OrdinalIgnoreCase)) UndoLastSegment(vertices, bulges);
                    else if (result.Keyword.Equals("Arco", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ReadThreePointArc(document, vertices, bulges, ucs, settings)) { canceled = true; break; }
                    }
                    else if (result.Keyword.Equals("Terminar", StringComparison.OrdinalIgnoreCase)) break;
                    continue;
                }
                if (result.Status == PromptStatus.None) break;
                if (result.Status != PromptStatus.OK) { canceled = true; break; }
                AddLineVertex(vertices, bulges, result.Point);
                continue;
            }

            PreviewPointResult next = GetPreviewPoint(document, vertices, bulges, settings,
                "\nSiguiente punto o [Arco/Línea/Deshacer/Cerrar/Terminar]: ", "Arco Línea Deshacer Cerrar Terminar");
            if (next.Status == PromptStatus.Cancel) { canceled = true; break; }
            if (next.Status == PromptStatus.None) break;
            if (next.Status == PromptStatus.Keyword)
            {
                string keyword = next.Keyword;
                if (keyword.Equals("Deshacer", StringComparison.OrdinalIgnoreCase)) UndoLastSegment(vertices, bulges);
                else if (keyword.Equals("Cerrar", StringComparison.OrdinalIgnoreCase)) { closed = true; break; }
                else if (keyword.Equals("Terminar", StringComparison.OrdinalIgnoreCase)) break;
                else if (keyword.Equals("Arco", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReadArc(document, vertices, bulges, ucs, settings)) { canceled = true; break; }
                }
                continue;
            }
            if (next.Status != PromptStatus.OK) { canceled = true; break; }
            AddLineVertex(vertices, bulges, next.Point);
        }

        if (canceled)
        {
            WriteMessage("Dibujo cancelado; no se modificó el dibujo.");
            return;
        }
        if (vertices.Count < 2)
        {
            WriteMessage("No se creó la marca: se necesitan al menos dos puntos.");
            return;
        }

        if (closed)
        {
            Vector2d start = new(vertices[0].X, vertices[0].Y), end = new(vertices[^1].X, vertices[^1].Y);
            if ((start - end).Length > 1e-9)
                AddLineVertex(vertices, bulges, vertices[0]);
            vertices.RemoveAt(vertices.Count - 1);
            bulges.RemoveAt(bulges.Count - 1);
            closed = true;
        }

        try
        {
            ObjectId created = _drawingService.CreateNew(document, vertices, bulges, settings, closed);
            WriteMessage($"Marca vial creada correctamente (handle {created.Handle}).");
        }
        catch (AcException ex)
        {
            AddinLogger.Error("Falló el paso de creación de la polilínea de marca vial.", ex);
            WriteMessage("No se creó la marca vial; se revirtió la transacción: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Falló el paso de creación de la polilínea de marca vial.", ex);
            WriteMessage("No se creó la marca vial; se revirtió la transacción: " + ex.Message);
        }
    }

    private static PreviewPointResult GetPreviewPoint(Document document, IReadOnlyList<Point3d> vertices,
        IReadOnlyList<double> bulges, RoadMarkingSettings settings, string message, string keywords,
        PreviewCurveMode curveMode = PreviewCurveMode.Line, Point3d? throughPoint = null)
    {
        Editor editor = document.Editor;
        Point3d basePoint = vertices.Count > 0 ? vertices[^1] : Point3d.Origin;
        PromptPointResult result = editor.GetPoint(CreatePointOptions(basePoint, message, keywords));
        if (result.Status == PromptStatus.Keyword)
            return new PreviewPointResult(result.Status, Point3d.Origin, result.StringResult);
        if (result.Status != PromptStatus.OK)
            return new PreviewPointResult(result.Status, Point3d.Origin, string.Empty);

        // Se conserva el flujo de captura tradicional de GetPoint. La marca se crea
        // al terminar, tal como en la versión anterior; Enter vuelve a finalizar.
        return new PreviewPointResult(result.Status, result.Value.TransformBy(editor.CurrentUserCoordinateSystem), string.Empty);
    }

    private readonly record struct PreviewPointResult(PromptStatus Status, Point3d Point, string Keyword);

    private enum PreviewCurveMode
    {
        Line,
        TangentArc,
        ThroughPointArc
    }

    private static PromptPointOptions CreatePointOptions(Point3d basePoint, string message, string keywords)
    {
        var options = new PromptPointOptions("\n" + message)
        {
            AllowNone = true,
            UseBasePoint = true,
            BasePoint = basePoint,
            UseDashedLine = true
        };
        foreach (string keyword in keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Keywords.Add(keyword);
        return options;
    }

    private static void AddLineVertex(List<Point3d> vertices, List<double> bulges, Point3d point)
    {
        if (vertices[^1].DistanceTo(point) <= 1e-9) return;
        bulges[^1] = 0;
        vertices.Add(point);
        bulges.Add(0);
    }

    private static void UndoLastSegment(List<Point3d> vertices, List<double> bulges)
    {
        if (vertices.Count <= 1) return;
        vertices.RemoveAt(vertices.Count - 1);
        bulges.RemoveAt(bulges.Count - 1);
        bulges[^1] = 0;
    }

    private static bool ReadArc(Document document, List<Point3d> vertices, List<double> bulges, Matrix3d ucs, RoadMarkingSettings settings)
    {
        if (vertices.Count < 2)
            return ReadThreePointArc(document, vertices, bulges, ucs, settings);

        PreviewPointResult end = GetPreviewPoint(document, vertices, bulges, settings,
            "\nPunto final del arco o [Línea/SegundoPunto/Deshacer]: ", "Linea SegundoPunto Deshacer", PreviewCurveMode.TangentArc);
        if (end.Status == PromptStatus.Keyword)
        {
            if (end.Keyword.Equals("Linea", StringComparison.OrdinalIgnoreCase)) return true;
            if (end.Keyword.Equals("Deshacer", StringComparison.OrdinalIgnoreCase)) { UndoLastSegment(vertices, bulges); return true; }
            var middleOptions = new PromptPointOptions("\nIndique el segundo punto del arco: ") { AllowNone = false, UseBasePoint = true, BasePoint = vertices[^1] };
            PromptPointResult middle = document.Editor.GetPoint(middleOptions);
            if (middle.Status != PromptStatus.OK) return false;
            PreviewPointResult final = GetPreviewPoint(document, vertices, bulges, settings,
                "\nIndique el punto final del arco: ", string.Empty, PreviewCurveMode.ThroughPointArc, middle.Value.TransformBy(ucs));
            return final.Status == PromptStatus.OK && AddThreePointArc(vertices, bulges, vertices[^1], middle.Value.TransformBy(ucs), final.Point);
        }
        if (end.Status != PromptStatus.OK) return false;
        Point3d endpoint = end.Point;
        if (!TryTangentBulge(vertices, bulges, endpoint, out double bulge))
        {
            document.Editor.WriteMessage("\nNo se pudo calcular un arco tangente con esos puntos. Use «Segundo punto» para definirlo por tres puntos.");
            return true;
        }
        bulges[^1] = bulge;
        vertices.Add(endpoint); bulges.Add(0);
        return true;
    }

    private static bool ReadThreePointArc(Document document, List<Point3d> vertices, List<double> bulges, Matrix3d ucs, RoadMarkingSettings settings)
    {
        var midOptions = new PromptPointOptions("\nIndique un punto por el que pasa el arco: ") { AllowNone = false, UseBasePoint = true, BasePoint = vertices[^1], UseDashedLine = true };
        PromptPointResult middle = document.Editor.GetPoint(midOptions);
        if (middle.Status != PromptStatus.OK) return false;
        PreviewPointResult end = GetPreviewPoint(document, vertices, bulges, settings,
            "\nIndique el punto final del arco: ", string.Empty, PreviewCurveMode.ThroughPointArc, middle.Value.TransformBy(ucs));
        return end.Status == PromptStatus.OK && AddThreePointArc(vertices, bulges, vertices[^1], middle.Value.TransformBy(ucs), end.Point);
    }

    private static bool AddThreePointArc(List<Point3d> vertices, List<double> bulges, Point3d start, Point3d middle, Point3d end)
    {
        Vector2d a = new(start.X, start.Y), b = new(middle.X, middle.Y), c = new(end.X, end.Y);
        double cross = (b - a).X * (c - a).Y - (b - a).Y * (c - a).X;
        if (Math.Abs(cross) < 1e-10) return false;
        double bulge = ComputeThreePointBulge(a, b, c, Math.Sign(cross));
        if (!double.IsFinite(bulge)) return false;
        bulges[^1] = bulge;
        vertices.Add(end); bulges.Add(0);
        return true;
    }

    private static double ComputeThreePointBulge(Vector2d a, Vector2d b, Vector2d c, int direction)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return double.NaN;
        double aa = a.X * a.X + a.Y * a.Y, bb = b.X * b.X + b.Y * b.Y, cc = c.X * c.X + c.Y * c.Y;
        var center = new Point2d((aa * (b.Y - c.Y) + bb * (c.Y - a.Y) + cc * (a.Y - b.Y)) / d,
            (aa * (c.X - b.X) + bb * (a.X - c.X) + cc * (b.X - a.X)) / d);
        double start = Math.Atan2(a.Y - center.Y, a.X - center.X);
        double mid = Math.Atan2(b.Y - center.Y, b.X - center.X);
        double end = Math.Atan2(c.Y - center.Y, c.X - center.X);
        double sweep = direction > 0 ? PositiveAngle(end - start) : -PositiveAngle(start - end);
        double midSweep = direction > 0 ? PositiveAngle(mid - start) : -PositiveAngle(start - mid);
        if (Math.Abs(midSweep) > Math.Abs(sweep) + 1e-9)
            sweep += direction > 0 ? -2 * Math.PI : 2 * Math.PI;
        return Math.Tan(sweep / 4);
    }

    private static bool TryTangentBulge(IReadOnlyList<Point3d> vertices, IReadOnlyList<double> bulges, Point3d endpoint, out double bulge)
    {
        bulge = 0;
        Vector2d start = new(vertices[^1].X, vertices[^1].Y);
        Vector2d finish = new(endpoint.X, endpoint.Y);
        Vector2d chord = finish - start;
        if (chord.Length < 1e-9) return false;
        Vector2d tangent;
        if (Math.Abs(bulges[^2]) < 1e-12)
            tangent = start - new Vector2d(vertices[^2].X, vertices[^2].Y);
        else
        {
            Vector2d previous = start - new Vector2d(vertices[^2].X, vertices[^2].Y);
            double tangentAngle = Math.Atan2(previous.Y, previous.X) + 2 * Math.Atan(bulges[^2]);
            tangent = new Vector2d(Math.Cos(tangentAngle), Math.Sin(tangentAngle));
        }
        if (tangent.Length < 1e-9) return false;
        tangent = tangent.GetNormal();
        double cross = tangent.X * chord.Y - tangent.Y * chord.X;
        if (Math.Abs(cross) < 1e-9) return false;
        Vector2d left = new(-tangent.Y, tangent.X);
        double radiusSigned = chord.DotProduct(chord) / (2 * cross);
        Point2d center = new(start.X + left.X * radiusSigned, start.Y + left.Y * radiusSigned);
        Vector2d r0 = start - new Vector2d(center.X, center.Y), r1 = finish - new Vector2d(center.X, center.Y);
        double sweep = Math.Atan2(r0.X * r1.Y - r0.Y * r1.X, r0.DotProduct(r1));
        if (Math.Sign(sweep) != Math.Sign(radiusSigned)) sweep += Math.Sign(radiusSigned) * 2 * Math.PI;
        bulge = Math.Tan(sweep / 4);
        return double.IsFinite(bulge) && Math.Abs(bulge) < 1e6;
    }

    private static double PositiveAngle(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

}
