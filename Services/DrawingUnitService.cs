using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace ARAR.Civil3D.Services;

/// <summary>Convierte magnitudes métricas a unidades de dibujo y avisa ante INSUNITS no métrico.</summary>
public static class DrawingUnitService
{
    public static bool ConfirmMetricWorkflow(Document document)
    {
        if (document.Database.Insunits == UnitsValue.Meters)
            return true;

        string name = (int)document.Database.Insunits == 0
            ? "sin unidades (INSUNITS=0)"
            : document.Database.Insunits.ToString();
        document.Editor.WriteMessage($"\nAviso: INSUNITS está configurado como {name}. Las dimensiones introducidas se expresan en metros y se convertirán a las unidades del dibujo.");
        var options = new PromptKeywordOptions("\n¿Desea continuar? [Continuar/Cancelar] <Cancelar>: ") { AllowNone = true };
        options.Keywords.Add("Continuar");
        options.Keywords.Add("Cancelar");
        options.Keywords.Default = "Cancelar";
        PromptResult result = document.Editor.GetKeywords(options);
        return result.Status == PromptStatus.OK && result.StringResult.Equals("Continuar", StringComparison.OrdinalIgnoreCase);
    }

    public static double MetersToDrawingUnits(Database database, double meters)
    {
        double metersPerUnit = database.Insunits switch
        {
            UnitsValue.Meters => 1.0,
            UnitsValue.Kilometers => 1000.0,
            UnitsValue.Centimeters => 0.01,
            UnitsValue.Millimeters => 0.001,
            UnitsValue.Mils => 0.0000254,
            UnitsValue.Inches => 0.0254,
            UnitsValue.Feet => 0.3048,
            UnitsValue.Yards => 0.9144,
            UnitsValue.Miles => 1609.344,
            _ => 1.0
        };
        return meters / metersPerUnit;
    }
}
