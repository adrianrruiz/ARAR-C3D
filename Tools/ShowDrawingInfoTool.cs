using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;

namespace ARAR.Civil3D.Tools;

public sealed class ShowDrawingInfoTool : ToolBase
{
    public override string Id => "ARAR.SHOW_DRAWING_INFO";
    public override string Title => "Información del dibujo";
    public override string Panel => "GENERACION DE MOVIMIENTOS DE TIERRA - ELEMENTO";
    public override string Description => "Muestra el nombre del dibujo activo.";
    public override string Tooltip => "Muestra el nombre del dibujo activo.";
    public override string CommandName => "ARAR_INFO_DIBUJO";
    public override string IconResourceKey => "DrawingInfoIcon";
    public override int Order => 20;

    protected override void ExecuteCore()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay ningún dibujo activo.");
            return;
        }

        string drawingName = string.IsNullOrWhiteSpace(document.Name) ? "Dibujo sin guardar" : document.Name;
        WriteMessage($"Dibujo activo: {drawingName}");
    }

    [CommandMethod("ARAR_INFO_DIBUJO", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();
}
