using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.GraphicsInterface;

namespace ARAR.Civil3D.Services;

/// <summary>Construye la entidad Table estática para los puntos de una alineación.</summary>
public static class AlignmentPointTableBuilder
{
    private const int ColumnCount = 5;
    private const int HeaderRows = 4;
    private const double DataRowHeight = 0.65;
    private const double HeaderRowHeight = 0.8;
    private const double TextHeight = 0.25;

    private static readonly double[] ColumnWidths = { 4.2, 6.2, 6.2, 4.5, 6.8 };

    public static Table Create(
        Database database,
        Point3d insertionPoint,
        string alignmentName,
        string description,
        double firstStation,
        double lastStation,
        IReadOnlyList<AlignmentTablePoint> points)
    {
        int rowCount = HeaderRows + (points.Count * 2) - 1;
        var table = new Table
        {
            TableStyle = database.Tablestyle,
            Position = insertionPoint
        };
        table.SetSize(rowCount, ColumnCount);

        for (int column = 0; column < ColumnCount; column++)
            table.SetColumnWidth(column, ColumnWidths[column]);

        for (int row = 0; row < rowCount; row++)
        {
            table.SetRowHeight(row, row < HeaderRows ? HeaderRowHeight : DataRowHeight);
            for (int column = 0; column < ColumnCount; column++)
            {
                table.SetTextHeight(row, column, TextHeight);
                table.SetAlignment(row, column, CellAlignment.MiddleCenter);
            }
        }

        string[] headingLines =
        {
            $"Nombre de alineación: {alignmentName}",
            $"Descripción: {description}",
            $"Intervalo de P.K.: inicio: {AlignmentPointTableCalculator.FormatStation(firstStation)}, fin: {AlignmentPointTableCalculator.FormatStation(lastStation)}"
        };

        for (int row = 0; row < headingLines.Length; row++)
        {
            table.MergeCells(CellRange.Create(table, row, 0, row, ColumnCount - 1));
            table.SetTextString(row, 0, headingLines[row]);
            table.SetAlignment(row, 0, CellAlignment.MiddleLeft);
        }

        string[] headers = { "P.K. de PI", "Ordenada", "Abscisa", "Distancia", "Orientación" };
        ObjectId boldHeaderStyleId = GetOrCreateBoldHeaderStyle(
            database,
            table.Cells[3, 0].TextStyleId,
            table.TableStyle);
        for (int column = 0; column < headers.Length; column++)
        {
            table.Cells[3, column].TextStyleId = boldHeaderStyleId;
            table.SetTextString(3, column, headers[column]);
        }

        int outputRow = HeaderRows;
        for (int index = 0; index < points.Count; index++)
        {
            AlignmentTablePoint point = points[index];
            table.SetTextString(outputRow, 0, AlignmentPointTableCalculator.FormatStation(point.Station));
            table.SetTextString(outputRow, 1, AlignmentPointTableCalculator.FormatCoordinate(point.Northing));
            table.SetTextString(outputRow, 2, AlignmentPointTableCalculator.FormatCoordinate(point.Easting));
            outputRow++;

            if (index >= points.Count - 1)
                continue;

            AlignmentTablePoint next = points[index + 1];
            AlignmentPointTableCalculator.SegmentMeasurement measurement =
                AlignmentPointTableCalculator.Measure(point.Easting, point.Northing, next.Easting, next.Northing);
            double distanceAlongAlignment = AlignmentPointTableCalculator.DistanceAlongAlignment(point.Station, next.Station);
            table.SetTextString(outputRow, 3, AlignmentPointTableCalculator.FormatDistance(distanceAlongAlignment));
            table.SetTextString(outputRow, 4, measurement.Bearing);
            outputRow++;
        }

        ApplyBlackTextAndBorders(table, rowCount);
        table.GenerateLayout();
        return table;
    }

    private static void ApplyBlackTextAndBorders(Table table, int rowCount)
    {
        Color black = Color.FromRgb(0, 0, 0);

        // Las tres filas de identificación están combinadas en una única celda.
        for (int row = 0; row < HeaderRows - 1; row++)
        {
            ApplyBlackFormatting(table.Cells[row, 0], black);
        }

        for (int row = HeaderRows - 1; row < rowCount; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
                ApplyBlackFormatting(table.Cells[row, column], black);
        }
    }

    private static void ApplyBlackFormatting(CellRange cell, Color black)
    {
        cell.ContentColor = black;
        CellBorders borders = cell.Borders;
        SetBlackVisible(borders.Top, black);
        SetBlackVisible(borders.Bottom, black);
        SetBlackVisible(borders.Left, black);
        SetBlackVisible(borders.Right, black);
        SetBlackVisible(borders.Horizontal, black);
        SetBlackVisible(borders.Vertical, black);
    }

    private static void SetBlackVisible(CellBorder border, Color black)
    {
        border.Color = black;
        border.IsVisible = true;
    }

    private static ObjectId GetOrCreateBoldHeaderStyle(Database database, ObjectId? cellStyleId, ObjectId tableStyleId)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var tableStyle = (TableStyle)transaction.GetObject(tableStyleId, OpenMode.ForRead);
        ObjectId sourceStyleId = cellStyleId ?? tableStyle.TextStyle(RowType.DataRow);
        var sourceStyle = (TextStyleTableRecord)transaction.GetObject(sourceStyleId, OpenMode.ForRead);
        bool useArialFallback = sourceStyle.IsShapeFile;
        string sourceName = new string(sourceStyle.Name
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        string styleName = $"ARAR_TABLA_BOLD_{sourceName}{(useArialFallback ? "_TTF" : string.Empty)}";

        var styleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        if (styleTable.Has(styleName))
        {
            ObjectId existingStyleId = styleTable[styleName];
            transaction.Commit();
            return existingStyleId;
        }

        var boldStyle = (TextStyleTableRecord)sourceStyle.Clone();
        boldStyle.Name = styleName;
        FontDescriptor sourceFont = sourceStyle.Font;
        boldStyle.Font = new FontDescriptor(
            useArialFallback ? "Arial" : sourceFont.TypeFace,
            true,
            sourceFont.Italic,
            sourceFont.CharacterSet,
            sourceFont.PitchAndFamily);

        if (useArialFallback)
        {
            boldStyle.FileName = "arial.ttf";
            boldStyle.BigFontFileName = string.Empty;
        }

        styleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForWrite);
        ObjectId boldStyleId = styleTable.Add(boldStyle);
        transaction.AddNewlyCreatedDBObject(boldStyle, true);
        transaction.Commit();
        return boldStyleId;
    }
}

public readonly record struct AlignmentTablePoint(double Station, double Easting, double Northing);
