using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using ARAR.Civil3D.UI;
using AcadUiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Tools;

/// <summary>Eleva sólidos 3D para apoyar su cota inferior sobre una superficie TIN.</summary>
public sealed class LiftSolidsToSurfaceTool : ToolBase
{
    private const string SolidDxfType = "3DSOLID";
    private readonly SolidSurfaceService _service = new();

    public override string Id => "ARAR.LIFT_SOLIDS_TO_SURFACE";
    public override string Title => "Subir sólidos a superficie";
    public override string Panel => "SEÑALIZACIÓN VIAL";
    public override string Description => "Eleva sólidos 3D hasta apoyar su punto inferior central sobre una superficie TIN.";
    public override string Tooltip => "Eleva los sólidos seleccionados desde su punto inferior central hasta una superficie TIN.";
    public override string CommandName => "ARAR_SUBIR_SOLIDOS_SUPERFICIE";
    public override string IconResourceKey => "LiftSolidsIcon";
    public override int Order => 30;

    protected override void ExecuteCore()
    {
        Document? document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }

        var selectionOptions = new PromptSelectionOptions
        {
            MessageForAdding = "\nSeleccione los sólidos 3D que desea subir a la superficie: "
        };
        var selectionFilter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, SolidDxfType)
        });

        PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions, selectionFilter);
        if (selection.Status != PromptStatus.OK || selection.Value is null)
        {
            WriteMessage("No se seleccionaron sólidos; no se modificó el dibujo.");
            return;
        }

        IReadOnlyList<RoadSurfaceChoice> surfaces = _service.GetTinSurfaces(document);
        var surfaceDialog = new SelectSurfaceWindow(surfaces);
        if (AcadUiApplication.ShowModalWindow(surfaceDialog) != true)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        ObjectId targetSurfaceId;
        if (surfaceDialog.SelectManually)
        {
            var surfaceOptions = new PromptEntityOptions("\nSeleccione la superficie TIN de destino: ");
            surfaceOptions.SetRejectMessage("\nEl objeto seleccionado no es una superficie TIN.");
            surfaceOptions.AddAllowedClass(typeof(TinSurface), exactMatch: false);
            PromptEntityResult surfaceResult = document.Editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK)
            {
                WriteMessage("No se seleccionó una superficie; no se modificó el dibujo.");
                return;
            }
            targetSurfaceId = surfaceResult.ObjectId;
        }
        else if (surfaceDialog.SelectedSurface is RoadSurfaceChoice selectedSurface)
        {
            targetSurfaceId = selectedSurface.Id;
        }
        else
        {
            WriteMessage("No se seleccionó una superficie; no se modificó el dibujo.");
            return;
        }

        try
        {
            SolidSurfaceMoveResult result = _service.LiftToSurface(
                document,
                selection.Value.GetObjectIds(),
                targetSurfaceId);

            WriteMessage($"Sólidos elevados: {result.Lifted}; ya estaban a esa cota o por encima: {result.AlreadyAtOrAbove}; omitidos: {result.Skipped}.");
            foreach (string message in result.Messages)
                WriteMessage("  · " + message);
        }
        catch (AcException ex)
        {
            AddinLogger.Error("Error de AutoCAD al subir sólidos a la superficie.", ex);
            WriteMessage("No se pudo completar la operación; se revirtieron los cambios: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Error al subir sólidos a la superficie.", ex);
            WriteMessage("No se pudo completar la operación; se revirtieron los cambios: " + ex.Message);
        }
    }

    [CommandMethod("ARAR_SUBIR_SOLIDOS_SUPERFICIE", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();
}
