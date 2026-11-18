using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Core;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;
using AutoCADTable = Autodesk.AutoCAD.DatabaseServices.Table;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace ARAR.Civil3D.Tools;

/// <summary>Genera y actualiza tablas con las cantidades QTO de Civil 3D.</summary>
public sealed class VolumeTableTool : ToolBase
{
    private const double TableVerticalGap = 2.0;

    public override string Id => "ARAR.VOLUME_TABLE";
    public override string Title => "Generación de tabla de volúmenes";
    public override string Panel => "GENERACIÓN DE MOVIMIENTO DE TIERRAS";
    public override string Description => "Genera tablas por P.K. con cantidades calculadas por Civil 3D.";
    public override string Tooltip => "Crea y actualiza tablas de áreas y volúmenes QTO por grupo de líneas de muestreo.";
    public override string CommandName => "ARARTABLAVOLUMENES";
    public override string IconResourceKey => "VolumeTableIcon";

    protected override void ExecuteCore()
    {
        var document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }

        Editor editor = document.Editor;
        IReadOnlyList<SampleGroupInfo> availableGroups = SelectGroups(editor, document.Database);
        if (availableGroups.Count == 0)
        {
            WriteMessage("No se encontraron grupos de líneas de muestreo para procesar.");
            return;
        }

        IReadOnlyList<SampleGroupInfo> chosenGroups = PromptGroupsByAlignment(editor, availableGroups);
        if (chosenGroups.Count == 0)
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }

        if (!TryPromptStationRange(editor, out double? start, out double? end))
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }
        var dataToCreate = new List<(SampleGroupInfo Group, VolumeGroupData Data)>();
        var omissions = new List<string>();
        foreach (SampleGroupInfo group in chosenGroups)
        {
            try
            {
                VolumeGroupData data = VolumeTableReportReader.Read(
                    document.Database, group.GroupId, group.AlignmentName, group.GroupName, start, end);
                dataToCreate.Add((group, data));
            }
            catch (AcadException ex)
            {
                omissions.Add($"{group.AlignmentName} / {group.GroupName}: error de Civil 3D ({ex.Message})");
            }
            catch (System.Exception ex)
            {
                omissions.Add($"{group.AlignmentName} / {group.GroupName}: {ex.Message}");
            }
        }

        if (dataToCreate.Count == 0)
        {
            WriteSummary(0, omissions);
            return;
        }

        PromptPointResult insertion = editor.GetPoint(
            new PromptPointOptions("\nIndique el punto de inserción de las tablas: "));
        if (insertion.Status != PromptStatus.OK)
        {
            WriteMessage("Operación cancelada; no se creó ninguna tabla.");
            return;
        }

        int created = 0;
        Point3d nextPosition = insertion.Value;
        using (document.LockDocument())
        {
            foreach ((SampleGroupInfo group, VolumeGroupData data) in dataToCreate)
            {
                try
                {
                    using Transaction transaction = document.Database.TransactionManager.StartTransaction();
                    var currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId, OpenMode.ForWrite);
                    using AutoCADTable table = VolumeTableBuilder.Create(document.Database, transaction, nextPosition, data);
                    currentSpace.AppendEntity(table);
                    transaction.AddNewlyCreatedDBObject(table, true);

                    VolumeTableMetadataStore.Write(transaction, table, new VolumeTableMetadata(
                        group.GroupId.Handle.ToString(),
                        data.AlignmentName,
                        data.GroupName,
                        data.MaterialListName,
                        start,
                        end));
                    transaction.Commit();
                    created++;
                    nextPosition = new Point3d(
                        insertion.Value.X,
                        nextPosition.Y - VolumeTableBuilder.GetEstimatedHeight(data) - TableVerticalGap,
                        insertion.Value.Z);
                }
                catch (AcadException ex)
                {
                    omissions.Add($"{group.AlignmentName} / {group.GroupName}: no se pudo escribir la tabla ({ex.Message})");
                }
                catch (System.Exception ex)
                {
                    omissions.Add($"{group.AlignmentName} / {group.GroupName}: no se pudo escribir la tabla ({ex.Message})");
                }
            }
        }

        WriteSummary(created, omissions);
    }

    [CommandMethod("ARARTABLAVOLUMENES", CommandFlags.Modal)]
    public void ExecuteCommand() => Execute();

    [CommandMethod("ARARACTUALIZARTABLAVOLUMENES", CommandFlags.Modal)]
    public void UpdateCommand()
    {
        try
        {
            UpdateTables();
        }
        catch (AcadException ex)
        {
            AddinLogger.Error("Error de AutoCAD al actualizar tablas de volúmenes.", ex);
            WriteMessage($"Error de AutoCAD: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("Error inesperado al actualizar tablas de volúmenes.", ex);
            WriteMessage($"Error inesperado: {ex.Message}");
        }
    }

    private static IReadOnlyList<SampleGroupInfo> SelectGroups(Editor editor, Database database)
    {
        var sourceOptions = new PromptKeywordOptions("\nSeleccione el origen de alineaciones [Seleccionar/Todas] <Seleccionar>: ")
        {
            AllowNone = true
        };
        sourceOptions.Keywords.Add("Seleccionar");
        sourceOptions.Keywords.Add("Todas");
        PromptResult source = editor.GetKeywords(sourceOptions);
        if (source.Status is PromptStatus.Cancel or PromptStatus.Error)
            return Array.Empty<SampleGroupInfo>();

        List<(ObjectId Id, string Name)> alignments;
        if (source.StringResult == "Todas")
        {
            var civilDocument = CivilApplication.ActiveDocument;
            alignments = ReadAlignmentNames(database, civilDocument.GetAlignmentIds());
        }
        else
        {
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSeleccione una o varias alineaciones: ",
                AllowDuplicates = false
            };
            string alignmentDxfName = RXClass.GetClass(typeof(CivilAlignment)).DxfName;
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, alignmentDxfName)
            });
            PromptSelectionResult selection = editor.GetSelection(selectionOptions, filter);
            if (selection.Status != PromptStatus.OK || selection.Value is null)
                return Array.Empty<SampleGroupInfo>();

            alignments = selection.Value.GetObjectIds()
                .Distinct()
                .Select(id => (id, Name: string.Empty))
                .ToList();
            using Transaction transaction = database.TransactionManager.StartTransaction();
            for (int index = 0; index < alignments.Count; index++)
            {
                var alignment = transaction.GetObject(alignments[index].Id, OpenMode.ForRead) as CivilAlignment;
                if (alignment is not null)
                    alignments[index] = (alignments[index].Id, alignment.Name);
            }
            transaction.Commit();
        }

        return ReadSampleGroups(database, alignments);
    }

    private static List<(ObjectId Id, string Name)> ReadAlignmentNames(Database database, ObjectIdCollection alignmentIds)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var result = new List<(ObjectId, string)>(alignmentIds.Count);
        foreach (ObjectId alignmentId in alignmentIds)
        {
            if (transaction.GetObject(alignmentId, OpenMode.ForRead) is CivilAlignment alignment)
                result.Add((alignmentId, alignment.Name));
        }
        transaction.Commit();
        return result;
    }

    private static IReadOnlyList<SampleGroupInfo> ReadSampleGroups(Database database, IReadOnlyList<(ObjectId Id, string Name)> alignments)
    {
        var result = new List<SampleGroupInfo>();
        var warnings = new List<string>();
        using Transaction transaction = database.TransactionManager.StartTransaction();
        foreach ((ObjectId alignmentId, string alignmentName) in alignments)
        {
            var alignment = transaction.GetObject(alignmentId, OpenMode.ForRead) as CivilAlignment;
            if (alignment is null)
                continue;

            ObjectIdCollection groupIds = alignment.GetSampleLineGroupIds();
            if (groupIds.Count == 0)
            {
                warnings.Add(alignment.Name);
                continue;
            }

            foreach (ObjectId groupId in groupIds)
            {
                if (transaction.GetObject(groupId, OpenMode.ForRead) is SampleLineGroup group)
                    result.Add(new SampleGroupInfo(alignmentId, groupId, alignment.Name, group.Name));
            }
        }
        transaction.Commit();

        foreach (string alignmentName in warnings)
            AcadApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\nSe omite la alineación '{alignmentName}': no tiene grupos de líneas de muestreo.");
        return result;
    }

    private static IReadOnlyList<SampleGroupInfo> PromptGroupsByAlignment(Editor editor, IReadOnlyList<SampleGroupInfo> groups)
    {
        var selected = new List<SampleGroupInfo>();
        foreach (IGrouping<ObjectId, SampleGroupInfo> alignmentGroups in groups.GroupBy(group => group.AlignmentId))
        {
            SampleGroupInfo[] items = alignmentGroups.ToArray();
            if (items.Length == 1)
            {
                selected.Add(items[0]);
                continue;
            }

            editor.WriteMessage($"\nAlineación: {items[0].AlignmentName}");
            for (int index = 0; index < items.Length; index++)
                editor.WriteMessage($"\n  {index + 1}. {items[index].GroupName}");

            var options = new PromptStringOptions("\nGrupos a incluir, separados por comas; Enter = todos: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.None || string.IsNullOrWhiteSpace(result.StringResult))
            {
                selected.AddRange(items);
                continue;
            }
            if (result.Status != PromptStatus.OK)
                return Array.Empty<SampleGroupInfo>();

            int[] indexes = result.StringResult.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => int.TryParse(token, out int value) ? value : -1)
                .Distinct()
                .ToArray();
            if (indexes.Length == 0 || indexes.Any(index => index < 1 || index > items.Length))
                throw new ArgumentException("La selección de grupos no es válida. Use los números mostrados, separados por comas.");
            selected.AddRange(indexes.Select(index => items[index - 1]));
        }
        return selected;
    }

    private static bool TryPromptStationRange(Editor editor, out double? startStation, out double? endStation)
    {
        startStation = null;
        endStation = null;
        var options = new PromptKeywordOptions("\nRango de P.K. [Todo/Rango] <Todo>: ") { AllowNone = true };
        options.Keywords.Add("Todo");
        options.Keywords.Add("Rango");
        PromptResult result = editor.GetKeywords(options);
        if (result.Status == PromptStatus.None)
            return true;
        if (result.Status != PromptStatus.OK)
            return false;
        if (result.StringResult != "Rango")
            return true;

        if (!TryGetDouble(editor, "\nP.K. inicial del rango: ", out double start) ||
            !TryGetDouble(editor, "\nP.K. final del rango: ", out double end))
            return false;
        if (end <= start)
            throw new ArgumentException("El P.K. final debe ser mayor que el inicial.");
        startStation = start;
        endStation = end;
        return true;
    }

    private static bool TryGetDouble(Editor editor, string message, out double value)
    {
        var options = new PromptDoubleOptions(message)
        {
            AllowNegative = true,
            AllowZero = true,
            AllowNone = false
        };
        PromptDoubleResult result = editor.GetDouble(options);
        value = result.Value;
        return result.Status == PromptStatus.OK && double.IsFinite(value);
    }

    private static void WriteSummary(int created, IReadOnlyList<string> omissions)
    {
        WriteMessage($"Tablas de volúmenes creadas: {created}. Separación vertical entre tablas: {TableVerticalGap:0.##} m.");
        if (omissions.Count == 0)
            return;

        WriteMessage("Elementos omitidos:");
        foreach (string omission in omissions)
            WriteMessage($"  - {omission}");
    }

    private static void UpdateTables()
    {
        var document = ActiveDocument;
        if (document is null)
        {
            WriteMessage("No hay un dibujo activo.");
            return;
        }

        Editor editor = document.Editor;
        var modeOptions = new PromptKeywordOptions("\nActualizar tablas [Seleccionar/Todas] <Seleccionar>: ") { AllowNone = true };
        modeOptions.Keywords.Add("Seleccionar");
        modeOptions.Keywords.Add("Todas");
        PromptResult mode = editor.GetKeywords(modeOptions);
        if (mode.Status is PromptStatus.Cancel or PromptStatus.Error)
            return;

        List<ObjectId> tableIds;
        if (mode.StringResult == "Todas")
        {
            tableIds = FindAllMarkedTables(document.Database);
        }
        else
        {
            ObjectId[] selectedIds = PromptTables(editor);
            if (selectedIds.Length == 0)
                return;
            tableIds = selectedIds.ToList();
        }

        var failures = new List<string>();
        int updated = 0;
        using (document.LockDocument())
        {
            foreach (ObjectId tableId in tableIds)
            {
                VolumeTableMetadata? metadata;
                using (Transaction readTransaction = document.Database.TransactionManager.StartTransaction())
                {
                    if (readTransaction.GetObject(tableId, OpenMode.ForRead) is not AutoCADTable currentTable ||
                        !VolumeTableMetadataStore.TryRead(readTransaction, currentTable, out metadata) || metadata is null)
                    {
                        failures.Add($"{tableId.Handle}: no es una tabla de volúmenes reconocida.");
                        readTransaction.Commit();
                        continue;
                    }
                    readTransaction.Commit();
                }

                if (!VolumeTableMetadataStore.TryResolveGroup(document.Database, metadata.GroupHandle, out ObjectId groupId))
                {
                    failures.Add($"{metadata.AlignmentName} / {metadata.GroupName}: el grupo ya no existe; la tabla se conserva.");
                    continue;
                }

                try
                {
                    VolumeGroupData refreshedData = VolumeTableReportReader.Read(
                        document.Database, groupId, metadata.AlignmentName, metadata.GroupName,
                        metadata.StartStation, metadata.EndStation);
                    using Transaction writeTransaction = document.Database.TransactionManager.StartTransaction();
                    var oldTable = (AutoCADTable)writeTransaction.GetObject(tableId, OpenMode.ForWrite);
                    var owner = (BlockTableRecord)writeTransaction.GetObject(oldTable.OwnerId, OpenMode.ForWrite);

                    using AutoCADTable replacement = VolumeTableBuilder.Create(
                        document.Database, writeTransaction, oldTable.Position, refreshedData);
                    replacement.TableStyle = oldTable.TableStyle;
                    replacement.LayerId = oldTable.LayerId;
                    replacement.Color = oldTable.Color;
                    replacement.Rotation = oldTable.Rotation;
                    owner.AppendEntity(replacement);
                    writeTransaction.AddNewlyCreatedDBObject(replacement, true);
                    VolumeTableMetadataStore.Write(writeTransaction, replacement, metadata);
                    oldTable.Erase();
                    writeTransaction.Commit();
                    updated++;
                }
                catch (AcadException ex)
                {
                    failures.Add($"{metadata.AlignmentName} / {metadata.GroupName}: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    failures.Add($"{metadata.AlignmentName} / {metadata.GroupName}: {ex.Message}");
                }
            }
        }

        WriteMessage($"Tablas de volúmenes actualizadas: {updated}.");
        foreach (string failure in failures)
            WriteMessage($"  - {failure}");
    }

    private static ObjectId[] PromptTables(Editor editor)
    {
        var options = new PromptSelectionOptions { MessageForAdding = "\nSeleccione las tablas ARAR de volúmenes: ", AllowDuplicates = false };
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "ACAD_TABLE") });
        PromptSelectionResult result = editor.GetSelection(options, filter);
        return result.Status == PromptStatus.OK && result.Value is not null
            ? result.Value.GetObjectIds()
            : Array.Empty<ObjectId>();
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
                if (transaction.GetObject(entityId, OpenMode.ForRead) is AutoCADTable table &&
                    VolumeTableMetadataStore.TryRead(transaction, table, out _))
                    result.Add(entityId);
            }
        }
        transaction.Commit();
        return result;
    }

    private sealed record SampleGroupInfo(ObjectId AlignmentId, ObjectId GroupId, string AlignmentName, string GroupName);
}
