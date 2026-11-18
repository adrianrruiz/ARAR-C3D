using System.IO;
using Microsoft.Win32;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Services;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Tools;

/// <summary>Exporta tablas reconocidas de ARAR-C3D a un libro XLSX.</summary>
public sealed class ExportTablesToExcelTool : ToolBase
{
    public override string Id => "ARAR.EXPORT_TABLES_EXCEL";
    public override string Title => "Exportar tablas a Excel";
    public override string Panel => "GENERACIÓN DE MOVIMIENTO DE TIERRAS";
    public override string Description => "Exporta tablas ARAR-C3D del dibujo a un libro de Excel.";
    public override string Tooltip => "Crea un XLSX con una hoja por tabla seleccionada.";
    public override string CommandName => "ARAREXPORTAREXCEL";
    public override string IconResourceKey => "ExportTablesIcon";
    public override int Order => 30;

    protected override void ExecuteCore()
    {
        var document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }

        Editor editor = document.Editor;
        var options = new PromptKeywordOptions("\nExportar tablas [Seleccionar/Todas] <Seleccionar>: ") { AllowNone = true };
        options.Keywords.Add("Seleccionar");
        options.Keywords.Add("Todas");
        PromptResult mode = editor.GetKeywords(options);
        if (mode.Status is PromptStatus.Cancel or PromptStatus.Error)
            return;

        bool allRecognized = mode.StringResult == "Todas";
        ObjectId[] selectedIds;
        if (allRecognized)
        {
            selectedIds = FindAllMarkedTables(document.Database).ToArray();
        }
        else
        {
            selectedIds = PromptTableSelection(editor);
            if (selectedIds.Length == 0)
            {
                WriteMessage("No se seleccionaron tablas.");
                return;
            }
        }

        if (selectedIds.Length == 0)
        {
            WriteMessage("No se encontraron tablas ARAR-C3D reconocidas en el dibujo.");
            return;
        }

        var omitted = new List<string>();
        List<ExportTableSnapshot> snapshots;
        try
        {
            snapshots = ExcelTableExporter.ReadTables(document.Database, selectedIds, omitted.Add);
        }
        catch (AcadException ex)
        {
            AddinLogger.Error("No se pudieron leer las tablas seleccionadas para Excel.", ex);
            WriteMessage($"Error de AutoCAD al leer las tablas: {ex.Message}");
            return;
        }

        var exportable = snapshots.Where(snapshot => snapshot.VolumeMetadata is not null).ToList();
        ExportTableSnapshot[] unmarked = snapshots.Where(snapshot => snapshot.VolumeMetadata is null).ToArray();
        if (unmarked.Length > 0 && !allRecognized)
        {
            bool treatAsLegacyPointTables = ConfirmLegacyTables(editor, unmarked.Length);
            if (treatAsLegacyPointTables)
                exportable.AddRange(unmarked);
            else
                omitted.AddRange(unmarked.Select(snapshot => $"{snapshot.Id.Handle}: tabla sin marca ARAR-C3D."));
        }
        else if (unmarked.Length > 0)
        {
            omitted.AddRange(unmarked.Select(snapshot => $"{snapshot.Id.Handle}: tabla sin marca ARAR-C3D."));
        }

        if (exportable.Count == 0)
        {
            WriteSummary(0, string.Empty, omitted);
            return;
        }

        string drawingName = Path.GetFileNameWithoutExtension(document.Name);
        if (string.IsNullOrWhiteSpace(drawingName))
            drawingName = "Dibujo";
        string initialDirectory = Path.GetDirectoryName(document.Name) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new SaveFileDialog
        {
            Title = "Guardar tablas ARAR-C3D en Excel",
            Filter = "Libro de Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initialDirectory,
            FileName = $"{drawingName}_tablas.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            WriteMessage("Exportación cancelada; no se modificó el dibujo.");
            return;
        }

        var conversionWarnings = new List<string>();
        int exportedSheets;
        try
        {
            exportedSheets = ExcelTableExporter.WriteWorkbook(dialog.FileName, exportable,
                warning => conversionWarnings.Add(warning));
        }
        catch (IOException ex)
        {
            WriteMessage($"No se pudo guardar el archivo. Si está abierto en Excel, ciérrelo e inténtelo de nuevo. Detalle: {ex.Message}");
            return;
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("No se pudo generar el libro Excel de tablas ARAR-C3D.", ex);
            WriteMessage($"No se pudo generar el archivo Excel: {ex.Message}");
            return;
        }

        omitted.AddRange(conversionWarnings);
        WriteSummary(exportedSheets, dialog.FileName, omitted);
    }

    [CommandMethod("ARAREXPORTAREXCEL", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    private static ObjectId[] PromptTableSelection(Editor editor)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nSeleccione una o varias tablas: ",
            AllowDuplicates = false
        };
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "ACAD_TABLE") });
        PromptSelectionResult result = editor.GetSelection(options, filter);
        return result.Status == PromptStatus.OK && result.Value is not null
            ? result.Value.GetObjectIds()
            : Array.Empty<ObjectId>();
    }

    private static bool ConfirmLegacyTables(Editor editor, int count)
    {
        var options = new PromptKeywordOptions(
            $"\nHay {count} tabla(s) seleccionada(s) sin marca ARAR. ¿Son tablas antiguas de puntos de alineación? [Si/No] <No>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("Si");
        options.Keywords.Add("No");
        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK && result.StringResult == "Si";
    }

    private static List<ObjectId> FindAllMarkedTables(Database database)
    {
        var result = new List<ObjectId>();
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout)
                continue;

            foreach (ObjectId entityId in block)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead) is Table table &&
                    VolumeTableMetadataStore.TryRead(transaction, table, out _))
                    result.Add(entityId);
            }
        }
        transaction.Commit();
        return result;
    }

    private static void WriteSummary(int sheetCount, string path, IReadOnlyList<string> omitted)
    {
        WriteMessage($"Hojas exportadas: {sheetCount}. Archivo: {(string.IsNullOrWhiteSpace(path) ? "no creado" : path)}.");
        if (omitted.Count == 0)
            return;

        WriteMessage("Tablas o celdas omitidas/avisos:");
        foreach (string item in omitted)
            WriteMessage($"  - {item}");
    }
}
