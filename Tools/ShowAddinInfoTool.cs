using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.UI;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ARAR.Civil3D.Tools;

public sealed class ShowAddinInfoTool : ToolBase
{
    public override string Id => "ARAR.SHOW_ADDIN_INFO";
    public override string Title => "Acerca de ARAR-C3D";
    public override string Panel => "INFORMACIÓN";
    public override string Description => "Muestra información de carga del complemento.";
    public override string Tooltip => "Comprueba que ARAR-C3D está cargado.";
    public override string CommandName => "ARAR_ACERCA";
    public override string IconResourceKey => "AboutIcon";
    public override int Order => 10;

    protected override void ExecuteCore()
    {
        WriteMessage("ARAR-C3D está cargado y la cinta está operativa.");
        AcadApplication.ShowModalWindow(new AddinInfoWindow());
    }

    [CommandMethod("ARAR_ACERCA", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();
}
