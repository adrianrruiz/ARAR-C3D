using Autodesk.AutoCAD.ApplicationServices;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Services;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Core;

/// <summary>Gestión común de errores de comandos para las herramientas.</summary>
public abstract class ToolBase : ITool
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Panel { get; }
    public abstract string Description { get; }
    public abstract string Tooltip { get; }
    public abstract string CommandName { get; }
    public abstract string IconResourceKey { get; }
    public virtual ToolSize Size => ToolSize.Large;
    public virtual int Order => 0;

    public void Execute() => ExecuteSafely(ExecuteCore);

    protected abstract void ExecuteCore();

    protected static Document? ActiveDocument => AcadApplication.DocumentManager.MdiActiveDocument;

    protected static void WriteMessage(string message)
    {
        Editor? editor = ActiveDocument?.Editor;
        editor?.WriteMessage("\n" + message);
    }

    private void ExecuteSafely(Action action)
    {
        try
        {
            action();
        }
        catch (AcException ex)
        {
            AddinLogger.Error($"Error de AutoCAD en {CommandName}.", ex);
            WriteMessage($"Error de AutoCAD: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error($"Error inesperado en {CommandName}.", ex);
            WriteMessage($"Error inesperado: {ex.Message}");
        }
    }
}
