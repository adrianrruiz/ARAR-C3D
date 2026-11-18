using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Tools;

/// <summary>Genera franjas cebreadas 2D entre los bordes de carril y berma.</summary>
public sealed class CebreadoTool : ToolBase
{
    private readonly CebreadoService _service = new();

    public override string Id => "ARAR.CEBREADO";
    public override string Title => "Cebreado";
    public override string Panel => "SEÑALIZACIÓN VIAL";
    public override string Description => "Crea franjas diagonales con sombreado entre dos polilíneas límite.";
    public override string Tooltip => "Genera cebreado entre el borde de carril y el borde de berma.";
    public override string CommandName => "ARAR_CEBREADO";
    public override string IconResourceKey => "CebreadoIcon";
    public override int Order => 40;

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

        PromptEntityResult laneResult = PromptPolyline(document, "\nSeleccione la polilínea del borde de carril: ");
        if (laneResult.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        PromptEntityResult bermResult = PromptPolyline(document, "\nSeleccione la polilínea del borde de berma: ");
        if (bermResult.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }
        if (laneResult.ObjectId == bermResult.ObjectId)
        {
            WriteMessage("Seleccione dos polilíneas distintas.");
            return;
        }

        if (!TryGetPositiveDistance(document.Editor, "\nSeparación libre entre franjas, medida perpendicularmente (m) <2>: ", 2.0, out double stripeGapMeters) ||
            !TryGetPositiveDistance(document.Editor, "\nSeparación entre el borde de carril y el inicio del cebreado (m) <0.225>: ", 0.225, out double laneClearanceMeters) ||
            !TryGetPositiveDistance(document.Editor, "\nLongitud total del cebreado, medida sobre el borde (m): ", null, out double sectionLengthMeters))
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        PromptPointResult startResult = document.Editor.GetPoint(new PromptPointOptions(
            "\nIndique el punto inicial sobre el borde de carril: "));
        if (startResult.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        PromptPointResult directionResult = document.Editor.GetPoint(new PromptPointOptions(
            "\nIndique otro punto sobre el borde para fijar el sentido del cebreado: "));
        if (directionResult.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }

        var advanceOptions = new PromptKeywordOptions(
            "\nIndique si se trata de la entrada o de la salida [Entrada/Salida]: ");
        advanceOptions.Keywords.Add("Entrada");
        advanceOptions.Keywords.Add("Salida");
        PromptResult advanceResult = document.Editor.GetKeywords(advanceOptions);
        if (advanceResult.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se modificó el dibujo.");
            return;
        }
        bool mirrorStripes = string.Equals(
            advanceResult.StringResult,
            "Salida",
            StringComparison.OrdinalIgnoreCase);

        try
        {
            CebreadoResult result = _service.Create(
                document,
                laneResult.ObjectId,
                bermResult.ObjectId,
                startResult.Value,
                directionResult.Value,
                stripeGapMeters,
                laneClearanceMeters,
                mirrorStripes,
                sectionLengthMeters);

            WriteMessage($"Cebreado terminado: {result.StripesCreated} franjas creadas; {result.StripesSkipped} omitidas por falta de intersección entre los límites.");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            AddinLogger.Error("Error de AutoCAD al generar el cebreado.", ex);
            WriteMessage("No se pudo generar el cebreado; se revirtió la operación: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Error inesperado al generar el cebreado.", ex);
            WriteMessage("No se pudo generar el cebreado; se revirtió la operación: " + ex.Message);
        }
    }

    [CommandMethod("ARAR_CEBREADO", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    private static PromptEntityResult PromptPolyline(Document document, string message)
    {
        var options = new PromptEntityOptions(message);
        options.SetRejectMessage("\nDebe seleccionar una polilínea abierta.");
        options.AddAllowedClass(typeof(Polyline), exactMatch: true);
        options.AddAllowedClass(typeof(Polyline2d), exactMatch: true);
        options.AddAllowedClass(typeof(Polyline3d), exactMatch: true);
        return document.Editor.GetEntity(options);
    }

    private static bool TryGetPositiveDistance(Editor editor, string message, double? defaultValue, out double value)
    {
        var options = new PromptDoubleOptions(message)
        {
            AllowNegative = false,
            AllowZero = false,
            AllowNone = defaultValue.HasValue
        };
        if (defaultValue is double fallback)
        {
            options.DefaultValue = fallback;
            options.UseDefaultValue = true;
        }

        PromptDoubleResult result = editor.GetDouble(options);
        value = result.Value;
        return result.Status == PromptStatus.OK;
    }
}
