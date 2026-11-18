using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Diagnostics;
using System.Threading.Tasks;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using ARAR.Civil3D.UI;
using DrawDialog = ARAR.Civil3D.UI.DrawSectionOptionsWindow;
using AcadUiApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ARAR.Civil3D.Tools;

/// <summary>Abre el visor modeless de secciones de un corredor Civil 3D.</summary>
public sealed class CorridorSectionViewerTool : ToolBase
{
    private static SectionViewerWindow? _window;
    private static PlanIndicator? _planIndicator;
    private static ObjectId _indicatorAlignmentId;
    private static Document? _requestedDocument;
    private static ObjectId _requestedAlignmentId;
    private static double _requestedStation;
    private static IReadOnlyList<SectionDrawRequest>? _pendingDrawRequests;
    private static DrawOptions? _pendingDrawOptions;
    private readonly CorridorSectionService _sectionService = new();

    public override string Id => "ARAR.CORRIDOR_SECTION_VIEWER";
    public override string Title => "Visor de secciones";
    public override string Panel => "Corredores";
    public override string Description => "Visualiza secciones calculadas del corredor y una superficie TIN.";
    public override string Tooltip => "Selecciona un corredor o alineación y abre un visor de secciones.";
    public override string CommandName => "MIE_SECCIONES";
    public override string IconResourceKey => "SectionViewerIcon";
    public override int Order => 10;

    public static void RegisterDocumentEvents()
    {
        var documents = AcadUiApplication.DocumentManager;
        documents.DocumentActivated -= OnDocumentActivated;
        documents.DocumentActivated += OnDocumentActivated;
        documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
    }

    public static void UnregisterDocumentEvents()
    {
        var documents = AcadUiApplication.DocumentManager;
        documents.DocumentActivated -= OnDocumentActivated;
        documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        CloseWindowAndClearState();
    }

    private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
    {
        if (_window is not null && !_window.IsForDocument(e.Document)) CloseWindowAndClearState();
    }

    private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
    {
        if (_window is not null && _window.IsForDocument(e.Document)) CloseWindowAndClearState();
    }

    private static void CloseWindowAndClearState()
    {
        SectionViewerWindow? oldWindow = _window;
        _window = null;
        try { oldWindow?.Close(); } catch (System.Exception ex) { AddinLogger.Error("No se pudo cerrar el visor al cambiar de documento.", ex); }
        try { _planIndicator?.Dispose(); } catch (System.Exception ex) { AddinLogger.Error("No se pudo limpiar el indicador transitorio.", ex); }
        _planIndicator = null; _indicatorAlignmentId = ObjectId.Null;
        _pendingDrawRequests = null; _pendingDrawOptions = null; _requestedDocument = null; _requestedAlignmentId = ObjectId.Null;
    }

    protected override void ExecuteCore()
    {
        Document? document = ActiveDocument;
        if (document is null || CivilApplication.ActiveDocument is null)
        {
            WriteMessage("No hay un dibujo activo de Civil 3D.");
            return;
        }

        if (_window is not null)
        {
            if (_window.IsForDocument(document)) { _window.Activate(); return; }
            _window.Close();
            _window = null;
            _planIndicator?.Dispose();
            _planIndicator = null;
        }

        var options = new PromptEntityOptions("\nSeleccione un corredor o una alineación: ")
        {
            AllowNone = false
        };
        options.SetRejectMessage("\nEl objeto debe ser un corredor o una alineación de Civil 3D.");
        options.AddAllowedClass(typeof(Autodesk.Civil.DatabaseServices.Corridor), exactMatch: false);
        options.AddAllowedClass(typeof(Autodesk.Civil.DatabaseServices.Alignment), exactMatch: false);
        PromptEntityResult prompt = document.Editor.GetEntity(options);
        if (prompt.Status != PromptStatus.OK) return;

        var (baselines, surfaces) = _sectionService.Discover(document, prompt.ObjectId);
        if (baselines.Count == 0)
        {
            WriteMessage("No se encontraron baselines de corredor asociadas al objeto seleccionado.");
            return;
        }

        var window = new SectionViewerWindow(document, baselines, surfaces);
        window.SectionPositionChanged += (alignmentId, station) =>
        {
            try
            {
                if (_planIndicator is null || _indicatorAlignmentId != alignmentId)
                {
                    _planIndicator?.Dispose();
                    _planIndicator = new PlanIndicator();
                    _indicatorAlignmentId = alignmentId;
                }
                var frame = _sectionService.GetPlanFrame(document, alignmentId, station);
                _planIndicator.Update(frame.Point, frame.TangentAngle, station);
            }
            catch (System.Exception ex)
            {
                AddinLogger.Error("No se pudo actualizar el indicador en planta.", ex);
                document.Editor.WriteMessage("\nNo se pudo actualizar el indicador en planta: " + ex.Message);
            }
        };
        window.DrawSectionRequested += request =>
        {
            var dialog = new DrawDialog(request);
            if (AcadUiApplication.ShowModalWindow(dialog) != true || dialog.Options is null) return;
            try { _pendingDrawRequests = window.PrepareDrawRequests(dialog.Options); }
            catch (System.Exception ex) { document.Editor.WriteMessage("\nNo se pudo preparar el rango: " + ex.Message); return; }
            _pendingDrawOptions = dialog.Options;
            _ = ExecuteDrawInCommandContextAsync(document);
        };
        window.CursorModeRequested += (_, _) => document.SendStringToExecute("MIE_SECCIONES_CURSOR\n", true, false, false);
        window.CenterRequested += (_, _) =>
        {
            if (window.CurrentAlignmentId is not ObjectId alignmentId || window.CurrentStation is not double station) return;
            _requestedDocument = document;
            _requestedAlignmentId = alignmentId;
            _requestedStation = station;
            document.SendStringToExecute("MIE_SECCIONES_CENTRAR\n", true, false, false);
        };
        window.ViewerClosed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
            _planIndicator?.Dispose();
            _planIndicator = null;
        };
        _window = window;
        AcadUiApplication.ShowModelessWindow(window);
        WriteMessage("Visor de secciones abierto. Seleccione la baseline y superficie, y pulse «Cargar secciones».");
    }

    [CommandMethod("MIE_SECCIONES", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    [CommandMethod("MIE_SECCIONES_CURSOR", CommandFlags.Modal)]
    public void CursorNavigationCommand()
    {
        Document? document = ActiveDocument;
        SectionViewerWindow? window = _window;
        if (document is null || window is null || !window.IsForDocument(document) || window.CurrentAlignmentId is not ObjectId alignmentId)
        {
            WriteMessage("Abra el visor y cargue una baseline antes de iniciar el modo cursor.");
            return;
        }
        Editor editor = document.Editor;
        long lastUpdate = 0;
        PointMonitorEventHandler monitor = (_, args) =>
        {
            if (!args.Context.PointComputed) return;
            long now = Stopwatch.GetTimestamp();
            if (now - lastUpdate < Stopwatch.Frequency / 12) return;
            lastUpdate = now;
            try
            {
                double station = _sectionService.ProjectStation(document, alignmentId, args.Context.RawPoint);
                window.SetSectionByStation(station);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            catch (System.Exception ex) { AddinLogger.Error("Error proyectando el cursor sobre la alineación.", ex); }
        };
        editor.PointMonitor += monitor;
        try
        {
            WriteMessage("Modo cursor activo. Mueva el cursor en planta, haga clic para fijar la posición y pulse Esc para salir.");
            var options = new PromptPointOptions("\nMueva el cursor en planta y haga clic para fijar la sección; Esc para salir: ") { AllowNone = false };
            PromptPointResult result = editor.GetPoint(options);
            if (result.Status == PromptStatus.OK)
            {
                double station = _sectionService.ProjectStation(document, alignmentId, result.Value);
                window.SetSectionByStation(station);
            }
        }
        finally
        {
            editor.PointMonitor -= monitor;
            WriteMessage("Modo cursor finalizado.");
        }
    }

    [CommandMethod("MIE_SECCION_DIBUJAR", CommandFlags.Modal)]
    public void DrawCurrentSectionCommand()
    {
        DrawCurrentSectionCore(ActiveDocument);
    }

    private static async Task ExecuteDrawInCommandContextAsync(Document document)
    {
        try
        {
            await AcadUiApplication.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                try
                {
                    if (!ReferenceEquals(AcadUiApplication.DocumentManager.MdiActiveDocument, document))
                    {
                        document.Editor.WriteMessage("\nNo se dibujó la sección: el dibujo asociado al visor ya no está activo.");
                        _pendingDrawRequests = null;
                        _pendingDrawOptions = null;
                    }
                    else document.Editor.Command("MIE_SECCION_DIBUJAR");
                }
                catch (System.Exception ex)
                {
                    document.Editor.WriteMessage("\nFalló el paso de dibujo en contexto de comando: " + ex.Message);
                    AddinLogger.Error("Falló la ejecución de dibujo desde la ventana modeless.", ex);
                }
                return Task.CompletedTask;
            }, null);
        }
        catch (System.Exception ex)
        {
            _pendingDrawRequests = null;
            _pendingDrawOptions = null;
            try { document.Editor.WriteMessage("\nNo se pudo iniciar el dibujo de la sección: " + ex.Message); }
            catch (System.Exception logException) { AddinLogger.Error("No se pudo informar del fallo de inicio del comando de dibujo.", logException); }
            AddinLogger.Error("No se pudo entrar en contexto de comando para dibujar la sección.", ex);
        }
    }

    private static void DrawCurrentSectionCore(Document? document)
    {
        IReadOnlyList<SectionDrawRequest>? requests = _pendingDrawRequests;
        DrawOptions? options = _pendingDrawOptions;
        _pendingDrawRequests = null; _pendingDrawOptions = null;
        if (document is null || requests is null || requests.Count == 0 || options is null)
        {
            document?.Editor.WriteMessage("\nNo hay secciones preparadas para dibujar. Vuelva a pulsar «Dibujar DWG».");
            return;
        }
        var pointOptions = new PromptPointOptions("\nIndique el punto de inserción de la sección en el UCS actual: ") { AllowNone = false };
        PromptPointResult pointResult = document.Editor.GetPoint(pointOptions);
        if (pointResult.Status != PromptStatus.OK) { document.Editor.WriteMessage("\nDibujo cancelado; no se modificó el DWG."); return; }
        Point3d insertion = pointResult.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
        if (!IsFinite(insertion)) { document.Editor.WriteMessage("\nNo se dibujó la sección: el punto de inserción no contiene coordenadas válidas."); return; }
        bool replace = false;
        var drawingService = new SectionDrawingService();
        if (requests.Any(r => drawingService.HasExisting(document, r.Baseline, r.Section.Station)))
        {
            var duplicateOptions = new PromptKeywordOptions("\nYa existe una sección en ese PK. [Reemplazar/Duplicar/Cancelar] <Duplicar>: ") { AllowNone = true };
            duplicateOptions.Keywords.Add("Reemplazar"); duplicateOptions.Keywords.Add("Duplicar"); duplicateOptions.Keywords.Add("Cancelar"); duplicateOptions.Keywords.Default = "Duplicar";
            PromptResult duplicateResult = document.Editor.GetKeywords(duplicateOptions);
            if (duplicateResult.Status == PromptStatus.Cancel || string.Equals(duplicateResult.StringResult, "Cancelar", StringComparison.OrdinalIgnoreCase))
            { document.Editor.WriteMessage("\nDibujo cancelado; no se modificó el DWG."); return; }
            replace = string.Equals(duplicateResult.StringResult, "Reemplazar", StringComparison.OrdinalIgnoreCase);
        }
        try
        {
            SectionDrawingResult result = drawingService.DrawMany(document, requests, options, insertion, replace);
            WriteMessage($"Sección dibujada: {result.EntityCount} entidades; grupo {result.GroupName}.");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) { AddinLogger.Error("Error de AutoCAD al dibujar la sección.", ex); WriteMessage("No se pudo dibujar la sección: " + ex.Message); }
        catch (System.Exception ex) { AddinLogger.Error("Error al dibujar la sección.", ex); WriteMessage("No se pudo dibujar la sección: " + ex.Message); }
    }

    private static bool IsFinite(Point3d point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    [CommandMethod("MIE_SECCIONES_CENTRAR", CommandFlags.Modal)]
    public void CenterAtStationCommand()
    {
        Document? document = ActiveDocument;
        if (document is null || _requestedDocument is null || !ReferenceEquals(document, _requestedDocument) || !_requestedAlignmentId.IsValid)
        {
            WriteMessage("No hay una posición válida del visor para centrar.");
            return;
        }
        try
        {
            var frame = _sectionService.GetPlanFrame(document, _requestedAlignmentId, _requestedStation);
            document.Editor.Command("_.ZOOM", "_C", frame.Point, 50.0);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            AddinLogger.Error("No se pudo centrar la vista en planta.", ex);
            WriteMessage("No se pudo centrar la vista: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Error centrando la vista en planta.", ex);
            WriteMessage("No se pudo centrar la vista: " + ex.Message);
        }
    }

}
