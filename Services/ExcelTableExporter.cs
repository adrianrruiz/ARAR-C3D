using System.Globalization;
using ClosedXML.Excel;
using Autodesk.AutoCAD.DatabaseServices;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Services;

public sealed record ExportTableSnapshot(
    ObjectId Id,
    string[][] Cells,
    VolumeTableMetadata? VolumeMetadata,
    string LegacyPointAlignmentName);

/// <summary>Escribe tablas seleccionadas en un XLSX sin depender de Excel ni de COM.</summary>
public static class ExcelTableExporter
{
    private const string VolumeFont = "Times New Roman";

    public static List<ExportTableSnapshot> ReadTables(
        Database database,
        IEnumerable<ObjectId> ids,
        Action<string> warning)
    {
        var result = new List<ExportTableSnapshot>();
        foreach (ObjectId id in ids.Distinct())
        {
            try
            {
                using Transaction transaction = database.TransactionManager.StartTransaction();
                if (transaction.GetObject(id, OpenMode.ForRead) is not Table table)
                {
                    warning($"El objeto {id.Handle} ya no es una tabla y se omitió.");
                    continue;
                }

                VolumeTableMetadataStore.TryRead(transaction, table, out VolumeTableMetadata? metadata);
                var cells = new string[table.NumRows][];
                for (int row = 0; row < table.NumRows; row++)
                {
                    cells[row] = new string[table.NumColumns];
                    for (int column = 0; column < table.NumColumns; column++)
                        cells[row][column] = table.Cells[row, column].TextString ?? string.Empty;
                }

                string alignmentName = metadata?.AlignmentName ?? ReadLegacyAlignmentName(cells);
                result.Add(new ExportTableSnapshot(id, cells, metadata, alignmentName));
                transaction.Commit();
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                warning($"No se pudo leer la tabla {id.Handle}; se omitió. {ex.Message}");
            }
            catch (System.Exception ex)
            {
                warning($"No se pudo leer la tabla {id.Handle}; se omitió. {ex.Message}");
            }
        }
        return result;
    }

    public static int WriteWorkbook(string path, IReadOnlyList<ExportTableSnapshot> tables, Action<string> warning)
    {
        using var workbook = new XLWorkbook();
        var sheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumeRows = new List<FlatVolumeRow>();
        int tableSheetCount = 0;

        foreach (ExportTableSnapshot table in tables)
        {
            bool isVolume = table.VolumeMetadata is not null;
            string requestedName = isVolume
                ? $"{table.VolumeMetadata!.AlignmentName}_{table.VolumeMetadata.GroupName}"
                : table.LegacyPointAlignmentName;
            string sheetName = VolumeTableTextFormat.CreateUniqueWorksheetName(requestedName, sheetNames);
            IXLWorksheet sheet = workbook.Worksheets.Add(sheetName);
            WriteTableSheet(sheet, table, isVolume, volumeRows, warning);
            if (table.Cells.Length > 0 && table.Cells.Any(row => row.Length > 0))
                tableSheetCount++;
        }

        if (volumeRows.Count > 0)
        {
            string dataSheetName = VolumeTableTextFormat.CreateUniqueWorksheetName("Datos", sheetNames);
            WriteFlatVolumeSheet(workbook.Worksheets.Add(dataSheetName), volumeRows);
        }

        workbook.SaveAs(path);
        return tableSheetCount + (volumeRows.Count > 0 ? 1 : 0);
    }

    private static void WriteTableSheet(
        IXLWorksheet sheet,
        ExportTableSnapshot table,
        bool isVolume,
        List<FlatVolumeRow> flatRows,
        Action<string> warning)
    {
        string[][] source = table.Cells;
        int rowCount = source.Length;
        int columnCount = rowCount == 0 ? 0 : source.Max(row => row.Length);
        if (rowCount == 0 || columnCount == 0)
            return;

        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < source[row].Length; column++)
                sheet.Cell(row + 1, column + 1).SetValue(source[row][column]);
        }

        if (isVolume)
            WriteVolumeNumbers(sheet, source, table.VolumeMetadata!, flatRows, warning);
        else
            WritePointNumbers(sheet, source, table.LegacyPointAlignmentName, warning);

        if (isVolume)
        {
            for (int row = 1; row <= Math.Min(4, rowCount); row++)
                if (columnCount > 1)
                    sheet.Range(row, 1, row, columnCount).Merge();
            for (int row = 7; row <= rowCount; row++)
            {
                string pkCell = source[row - 1].Length > 0 ? source[row - 1][0] : string.Empty;
                if (pkCell.StartsWith("P.K.:", StringComparison.OrdinalIgnoreCase) && columnCount > 1)
                    sheet.Range(row, 1, row, columnCount).Merge();
            }
        }
        else
        {
            for (int row = 1; row <= Math.Min(3, rowCount); row++)
                if (columnCount > 1)
                    sheet.Range(row, 1, row, columnCount).Merge();
        }

        string fontName = isVolume ? VolumeFont : "Arial";
        IXLRange used = sheet.Range(1, 1, rowCount, columnCount);
        used.Style.Font.FontName = fontName;
        used.Style.Font.FontColor = XLColor.Black;
        used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.InsideBorderColor = XLColor.Black;
        used.Style.Border.OutsideBorderColor = XLColor.Black;

        int headerRow = isVolume ? 5 : 4;
        if (headerRow <= rowCount)
            sheet.Range(headerRow, 1, headerRow, columnCount).Style.Font.Bold = true;

        sheet.Columns(1, columnCount).AdjustToContents(8, 42);
        sheet.Rows(1, rowCount).AdjustToContents();
        sheet.SheetView.FreezeRows(isVolume ? Math.Min(6, rowCount) : Math.Min(4, rowCount));
    }

    private static void WriteVolumeNumbers(
        IXLWorksheet sheet,
        string[][] source,
        VolumeTableMetadata metadata,
        List<FlatVolumeRow> flatRows,
        Action<string> warning)
    {
        double? currentStation = null;
        string? currentStationText = null;
        for (int row = 6; row < source.Length; row++)
        {
            string[] cells = source[row];
            string first = cells.Length == 0 ? string.Empty : cells[0].Trim();
            if (first.StartsWith("P.K.:", StringComparison.OrdinalIgnoreCase))
            {
                currentStationText = first[(first.IndexOf(':') + 1)..].Trim();
                if (VolumeTableTextFormat.TryParseStation(currentStationText, out double station))
                    currentStation = station;
                else
                {
                    currentStation = null;
                    warning($"No se pudo leer el P.K. de la fila {row + 1} en '{metadata.AlignmentName} / {metadata.GroupName}'.");
                }
                continue;
            }

            string materialName = cells.Length > 1 ? cells[1].Trim() : string.Empty;
            var values = new double?[3];
            for (int index = 0; index < values.Length; index++)
            {
                int sourceColumn = index + 2;
                if (cells.Length > sourceColumn && !string.IsNullOrWhiteSpace(cells[sourceColumn]))
                {
                    if (VolumeTableTextFormat.TryParseNumber(cells[sourceColumn], out double parsed))
                    {
                        values[index] = parsed;
                        sheet.Cell(row + 1, sourceColumn + 1).SetValue(parsed);
                        sheet.Cell(row + 1, sourceColumn + 1).Style.NumberFormat.Format = "0.00";
                        sheet.Cell(row + 1, sourceColumn + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    else
                    {
                        warning($"La celda {row + 1}, {sourceColumn + 1} de '{metadata.AlignmentName} / {metadata.GroupName}' se exportó como texto.");
                    }
                }
            }

            if (currentStation.HasValue && materialName.Length > 0)
                flatRows.Add(new FlatVolumeRow(metadata.AlignmentName, metadata.GroupName, currentStation.Value,
                    currentStationText ?? VolumeTableTextFormat.FormatStation(currentStation.Value), materialName,
                    values[0], values[1], values[2]));
        }
    }

    private static void WritePointNumbers(IXLWorksheet sheet, string[][] source, string alignmentName, Action<string> warning)
    {
        for (int row = 4; row < source.Length; row++)
        {
            string[] cells = source[row];
            if (cells.Length == 0)
                continue;

            if (VolumeTableTextFormat.TryParseStation(cells[0], out _))
            {
                for (int column = 1; column <= 2 && column < cells.Length; column++)
                    SetNumericCell(sheet, row, column, cells[column], "#,##0.0000\"m\"", warning, alignmentName);
                continue;
            }

            if (cells.Length > 3 && !string.IsNullOrWhiteSpace(cells[3]))
                SetNumericCell(sheet, row, 3, cells[3], "#,##0.000\"m\"", warning, alignmentName);
        }
    }

    private static void SetNumericCell(
        IXLWorksheet sheet,
        int zeroBasedRow,
        int zeroBasedColumn,
        string text,
        string numberFormat,
        Action<string> warning,
        string alignmentName)
    {
        if (VolumeTableTextFormat.TryParseNumber(text, out double value))
        {
            IXLCell cell = sheet.Cell(zeroBasedRow + 1, zeroBasedColumn + 1);
            cell.SetValue(value);
            cell.Style.NumberFormat.Format = numberFormat;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            warning($"Una coordenada o distancia de '{alignmentName}' se exportó como texto: {text}");
        }
    }

    private static void WriteFlatVolumeSheet(IXLWorksheet sheet, IReadOnlyList<FlatVolumeRow> rows)
    {
        string[] headers = { "Alineación", "Grupo", "P.K. numérico", "Tipo de área", "Área", "Vol. incremental", "Vol. acumul." };
        for (int column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).SetValue(headers[column]);

        for (int index = 0; index < rows.Count; index++)
        {
            FlatVolumeRow item = rows[index];
            int row = index + 2;
            sheet.Cell(row, 1).SetValue(item.AlignmentName);
            sheet.Cell(row, 2).SetValue(item.GroupName);
            sheet.Cell(row, 3).SetValue(item.Station);
            sheet.Cell(row, 4).SetValue(item.MaterialName);
            if (item.Area.HasValue) sheet.Cell(row, 5).SetValue(item.Area.Value);
            if (item.IncrementalVolume.HasValue) sheet.Cell(row, 6).SetValue(item.IncrementalVolume.Value);
            if (item.CumulativeVolume.HasValue) sheet.Cell(row, 7).SetValue(item.CumulativeVolume.Value);
        }

        IXLRange used = sheet.Range(1, 1, rows.Count + 1, headers.Length);
        used.Style.Font.FontName = VolumeFont;
        used.Style.Font.FontColor = XLColor.Black;
        used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.InsideBorderColor = XLColor.Black;
        used.Style.Border.OutsideBorderColor = XLColor.Black;
        sheet.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
        sheet.Range(2, 3, rows.Count + 1, 3).Style.NumberFormat.Format = "0.000";
        sheet.Range(2, 5, rows.Count + 1, 7).Style.NumberFormat.Format = "0.00";
        sheet.Range(1, 1, rows.Count + 1, headers.Length).SetAutoFilter();
        sheet.Columns(1, headers.Length).AdjustToContents(8, 42);
        sheet.SheetView.FreezeRows(1);
    }

    private static string ReadLegacyAlignmentName(string[][] cells)
    {
        if (cells.Length > 0 && cells[0].Length > 0)
        {
            const string label = "Nombre de alineación:";
            string first = cells[0][0];
            if (first.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return first[label.Length..].Trim();
        }
        return "Tabla de puntos";
    }

    private sealed record FlatVolumeRow(
        string AlignmentName,
        string GroupName,
        double Station,
        string StationText,
        string MaterialName,
        double? Area,
        double? IncrementalVolume,
        double? CumulativeVolume);
}
