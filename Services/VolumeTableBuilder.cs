using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.Services;

/// <summary>Construye las tablas de cantidades a partir de valores ya calculados por Civil 3D.</summary>
public static class VolumeTableBuilder
{
    private const int ColumnCount = 5;
    private const int HeaderRowCount = 6;
    private const double HeaderRowHeight = 0.8;
    private const double DataRowHeight = 0.65;
    private const double TextHeight = 0.22;
    private static readonly double[] ColumnWidths = { 14.0, 24.0, 18.0, 21.0, 20.0 };

    public static int GetRowCount(VolumeGroupData data) =>
        HeaderRowCount + data.Stations.Count * (1 + data.MaterialNames.Count);

    public static double GetEstimatedHeight(VolumeGroupData data) =>
        (HeaderRowCount * HeaderRowHeight) + ((GetRowCount(data) - HeaderRowCount) * DataRowHeight);

    public static Table Create(Database database, Transaction transaction, Point3d insertionPoint, VolumeGroupData data)
    {
        int rowCount = GetRowCount(data);
        var table = new Table
        {
            TableStyle = database.Tablestyle,
            Position = insertionPoint
        };
        table.SetSize(rowCount, ColumnCount);

        ObjectId regularStyle = GetOrCreateFontStyle(database, transaction, bold: false);
        ObjectId boldStyle = GetOrCreateFontStyle(database, transaction, bold: true);

        for (int column = 0; column < ColumnCount; column++)
            table.SetColumnWidth(column, ColumnWidths[column]);

        for (int row = 0; row < rowCount; row++)
        {
            table.SetRowHeight(row, row < HeaderRowCount ? HeaderRowHeight : DataRowHeight);
            for (int column = 0; column < ColumnCount; column++)
            {
                table.SetTextHeight(row, column, TextHeight);
                table.SetAlignment(row, column, CellAlignment.MiddleCenter);
                table.Cells[row, column].TextStyleId = regularStyle;
            }
        }

        string[] heading =
        {
            $"Alineación: {data.AlignmentName}",
            $"Grupo de líneas de muestreo: {data.GroupName}",
            $"P.K. inicial: {VolumeTableTextFormat.FormatStation(data.StartStation)}",
            $"P.K. final: {VolumeTableTextFormat.FormatStation(data.EndStation)}"
        };
        for (int row = 0; row < heading.Length; row++)
        {
            table.MergeCells(CellRange.Create(table, row, 0, row, ColumnCount - 1));
            table.SetTextString(row, 0, heading[row]);
            table.SetAlignment(row, 0, CellAlignment.MiddleCenter);
        }

        string[] titles = { string.Empty, "Tipo de área", "Área", "Vol. incremental", "Vol. acumul." };
        string[] units = { string.Empty, string.Empty, "Metros cuadrados", "Metros cúbicos", "Metros cúbicos" };
        for (int column = 0; column < ColumnCount; column++)
        {
            table.SetTextString(4, column, titles[column]);
            table.SetTextString(5, column, units[column]);
            table.Cells[4, column].TextStyleId = boldStyle;
        }

        int outputRow = HeaderRowCount;
        foreach (VolumeStationData station in data.Stations)
        {
            table.MergeCells(CellRange.Create(table, outputRow, 0, outputRow, ColumnCount - 1));
            table.SetTextString(outputRow, 0, $"P.K.: {VolumeTableTextFormat.FormatStation(station.Station)}");
            table.SetAlignment(outputRow, 0, CellAlignment.MiddleLeft);
            outputRow++;

            foreach (string materialName in data.MaterialNames)
            {
                VolumeMaterialValue? value = station.Materials.FirstOrDefault(
                    item => string.Equals(item.Name, materialName, StringComparison.OrdinalIgnoreCase));
                table.SetTextString(outputRow, 0, string.Empty);
                table.SetTextString(outputRow, 1, materialName);
                table.SetTextString(outputRow, 2, value is null ? string.Empty : FormatValue(value.Area));
                table.SetTextString(outputRow, 3, value is null ? string.Empty : FormatValue(value.IncrementalVolume));
                table.SetTextString(outputRow, 4, value is null ? string.Empty : FormatValue(value.CumulativeVolume));
                outputRow++;
            }
        }

        ApplyBlackTextAndBorders(table, rowCount);
        table.GenerateLayout();
        return table;
    }

    private static string FormatValue(double value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static ObjectId GetOrCreateFontStyle(Database database, Transaction transaction, bool bold)
    {
        string baseName = bold ? "ARAR_VOL_TNR_BOLD" : "ARAR_VOL_TNR";
        var styleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        string styleName = baseName;
        int suffix = 2;
        while (styleTable.Has(styleName))
        {
            var existing = (TextStyleTableRecord)transaction.GetObject(styleTable[styleName], OpenMode.ForRead);
            if (string.Equals(existing.Font.TypeFace, "Times New Roman", StringComparison.OrdinalIgnoreCase) &&
                existing.Font.Bold == bold)
                return existing.ObjectId;
            styleName = $"{baseName}_{suffix++}";
        }

        var record = new TextStyleTableRecord
        {
            Name = styleName,
            Font = new FontDescriptor("Times New Roman", bold, false, 0, 0),
            FileName = "times.ttf"
        };
        styleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForWrite);
        ObjectId styleId = styleTable.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        return styleId;
    }

    private static void ApplyBlackTextAndBorders(Table table, int rowCount)
    {
        Color black = Color.FromRgb(0, 0, 0);
        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
            {
                CellRange cell = table.Cells[row, column];
                cell.ContentColor = black;
                CellBorders borders = cell.Borders;
                SetBorder(borders.Top, black);
                SetBorder(borders.Bottom, black);
                SetBorder(borders.Left, black);
                SetBorder(borders.Right, black);
                SetBorder(borders.Horizontal, black);
                SetBorder(borders.Vertical, black);
            }
        }
    }

    private static void SetBorder(CellBorder border, Color black)
    {
        border.Color = black;
        border.IsVisible = true;
    }
}
