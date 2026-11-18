using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace ARAR.Civil3D.Services;

public sealed record VolumeTableMetadata(
    string GroupHandle,
    string AlignmentName,
    string GroupName,
    string MaterialListName,
    double? StartStation,
    double? EndStation);

/// <summary>Identifica y permite regenerar las tablas ARAR sin inferirlo por su apariencia.</summary>
public static class VolumeTableMetadataStore
{
    public const string RecordKey = "ARAR_VOLUME_TABLE";
    public const string Marker = "ARAR_VOLUME_TABLE_V1";

    public static void Write(Transaction transaction, Table table, VolumeTableMetadata metadata)
    {
        if (table.ExtensionDictionary.IsNull)
            table.CreateExtensionDictionary();

        var dictionary = (DBDictionary)transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite);
        if (dictionary.Contains(RecordKey))
        {
            var previous = (Xrecord)transaction.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForWrite);
            previous.Erase();
        }

        using var record = new Xrecord
        {
            Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, Marker),
                new TypedValue((int)DxfCode.Text, metadata.GroupHandle),
                new TypedValue((int)DxfCode.Text, metadata.AlignmentName),
                new TypedValue((int)DxfCode.Text, metadata.GroupName),
                new TypedValue((int)DxfCode.Text, metadata.MaterialListName),
                new TypedValue((int)DxfCode.Real, metadata.StartStation ?? double.NaN),
                new TypedValue((int)DxfCode.Real, metadata.EndStation ?? double.NaN))
        };

        dictionary.SetAt(RecordKey, record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    public static bool TryRead(Transaction transaction, Table table, out VolumeTableMetadata? metadata)
    {
        metadata = null;
        ObjectId extensionId = table.ExtensionDictionary;
        if (extensionId.IsNull)
            return false;

        var dictionary = (DBDictionary)transaction.GetObject(extensionId, OpenMode.ForRead);
        if (!dictionary.Contains(RecordKey))
            return false;

        var record = (Xrecord)transaction.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForRead);
        TypedValue[]? values = record.Data?.AsArray();
        if (values is null || values.Length < 7 || !string.Equals(values[0].Value as string, Marker, StringComparison.Ordinal))
            return false;

        double start = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture);
        double end = Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture);
        metadata = new VolumeTableMetadata(
            values[1].Value as string ?? string.Empty,
            values[2].Value as string ?? string.Empty,
            values[3].Value as string ?? string.Empty,
            values[4].Value as string ?? string.Empty,
            double.IsFinite(start) ? start : null,
            double.IsFinite(end) ? end : null);
        return !string.IsNullOrWhiteSpace(metadata.GroupHandle);
    }

    public static bool TryResolveGroup(Database database, string groupHandle, out ObjectId groupId)
    {
        groupId = ObjectId.Null;
        if (!long.TryParse(groupHandle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handleValue))
            return false;

        try
        {
            groupId = database.GetObjectId(false, new Handle(handleValue), 0);
            return !groupId.IsNull && groupId.IsValid && !groupId.IsErased;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            groupId = ObjectId.Null;
            return false;
        }
    }
}
