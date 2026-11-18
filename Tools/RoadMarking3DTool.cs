using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using ARAR.Civil3D.UI;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Tools;

/// <summary>Genera marcas viales como sólidos barridos siguiendo trayectorias 3D.</summary>
public sealed class RoadMarking3DTool : ToolBase
{
    private const string SelectionDxfTypes = "LWPOLYLINE,POLYLINE,AECC_FEATURE_LINE";
    private readonly RoadMarking3DService _service = new();

    public override string Id => "ARAR.ROAD_MARKING_3D";
    public override string Title => "Marca vial 3D";
    public override string Panel => "SEÑALIZACIÓN VIAL";
    public override string Description => "Crea sólidos de marca vial siguiendo polilíneas y líneas características.";
    public override string Tooltip => "Marca vial 3D";
    public override string CommandName => "ARAR_MARCA_VIAL_3D";
    public override string IconResourceKey => "RoadMarking3DIcon";
    public override int Order => 20;

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

        var selectionOptions = new PromptSelectionOptions { MessageForAdding = "\nSeleccione polilíneas 2D/3D o líneas características: " };
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, SelectionDxfTypes) });
        PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions, filter);
        if (selection.Status != PromptStatus.OK || selection.Value is null)
        {
            WriteMessage("No se seleccionaron objetos; no se modificó el dibujo.");
            return;
        }

        try
        {
            ObjectId[] selectedIds = selection.Value.GetObjectIds();
            RoadMarking3DInputData input = _service.ReadInputData(document, selectedIds);
            foreach (string handle in input.OmittedHandles)
            {
                WriteMessage("Asigne grosor a la línea");
                WriteMessage("Objeto omitido: " + handle);
            }
            if (input.ValidSourceIds.Count == 0)
            {
                WriteMessage("No hay objetos seleccionados con un grosor válido; no se abrió el diálogo ni se creó geometría.");
                return;
            }
            var dialog = new RoadMarking3DOptionsWindow(input.Surfaces, input.FirstSettings,
                input.FirstWidthMeters, input.RequiresSurface, input.WidthDisplayText);
            if (AcadApplication.ShowModalWindow(dialog) != true || dialog.Settings is not RoadMarkingSettings settings)
            {
                WriteMessage("Operación cancelada; no se modificó el dibujo.");
                return;
            }

            RoadMarking3DResult result = _service.Create(document, input.ValidSourceIds, dialog.SurfaceId, settings,
                dialog.KeepIntermediate, dialog.ReverseDirection);
            string type = settings.Dashed ? "discontinua" : "continua";
            WriteMessage($"Marca vial 3D: {result.Solids} sólidos creados ({type}) en {result.Objects} objetos; {result.FailedSweeps} tramos con error; {result.OmittedMeters:0.00} m fuera de la superficie.");
            foreach (string error in result.Errors)
                WriteMessage("  · " + error);
        }
        catch (AcException ex)
        {
            AddinLogger.Error("Falló la ejecución del comando Marca vial 3D.", ex);
            WriteMessage("No se pudo generar la marca vial 3D; la transacción se revirtió: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Falló la ejecución del comando Marca vial 3D.", ex);
            WriteMessage("No se pudo generar la marca vial 3D; la transacción se revirtió: " + ex.Message);
        }
    }

    [CommandMethod("ARAR_MARCA_VIAL_3D", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();
}
