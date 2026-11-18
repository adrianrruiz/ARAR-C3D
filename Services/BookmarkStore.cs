using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Services;

/// <summary>Stores viewer bookmarks in a private named-object dictionary in the current DWG.</summary>
public sealed class BookmarkStore
{
    private const string RootKey = "ARAR_C3D_SECTION_BOOKMARKS";
    private const int MaxNoteLength = 230;

    public IReadOnlyList<SectionBookmark> Load(Document document, BaselineChoice choice)
    {
        var items = new List<SectionBookmark>();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        DBDictionary nod = (DBDictionary)transaction.GetObject(document.Database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(RootKey)) return items;
        var root = (DBDictionary)transaction.GetObject(nod.GetAt(RootKey), OpenMode.ForRead);
        string key = GetBaselineKey(choice);
        if (!root.Contains(key)) return items;
        var baseline = (DBDictionary)transaction.GetObject(root.GetAt(key), OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in baseline)
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Xrecord record) continue;
            ResultBuffer? data = record.Data;
            if (data is null) continue;
            using ResultBuffer buffer = data;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 4 || values[0].TypeCode != 1040 || values[1].TypeCode != 1000 || values[2].TypeCode != 1000 || values[3].TypeCode != 1040) continue;
            double station = Convert.ToDouble(values[0].Value, System.Globalization.CultureInfo.InvariantCulture);
            string note = (string)values[1].Value;
            string label = (string)values[2].Value;
            DateTime created = DateTime.FromOADate(Convert.ToDouble(values[3].Value, System.Globalization.CultureInfo.InvariantCulture));
            if (double.IsFinite(station)) items.Add(new SectionBookmark(station, note, label, DateTime.SpecifyKind(created, DateTimeKind.Utc)));
        }
        transaction.Commit();
        return items.OrderBy(x => x.Station).ToArray();
    }

    public void Save(Document document, BaselineChoice choice, IReadOnlyList<SectionBookmark> bookmarks)
    {
        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        DBDictionary nod = (DBDictionary)transaction.GetObject(document.Database.NamedObjectsDictionaryId, OpenMode.ForWrite);
        DBDictionary root;
        if (nod.Contains(RootKey)) root = (DBDictionary)transaction.GetObject(nod.GetAt(RootKey), OpenMode.ForWrite);
        else
        {
            root = new DBDictionary();
            nod.SetAt(RootKey, root);
            transaction.AddNewlyCreatedDBObject(root, true);
        }
        string key = GetBaselineKey(choice);
        DBDictionary baseline;
        if (root.Contains(key)) baseline = (DBDictionary)transaction.GetObject(root.GetAt(key), OpenMode.ForWrite);
        else
        {
            baseline = new DBDictionary();
            root.SetAt(key, baseline);
            transaction.AddNewlyCreatedDBObject(baseline, true);
        }
        foreach (DBDictionaryEntry entry in baseline.Cast<DBDictionaryEntry>().ToArray())
        {
            baseline.Remove(entry.Key);
            transaction.GetObject(entry.Value, OpenMode.ForWrite).Erase();
        }
        int index = 0;
        foreach (SectionBookmark bookmark in bookmarks.OrderBy(b => b.Station))
        {
            var record = new Xrecord();
            using (var data = new ResultBuffer(
                new TypedValue(1040, bookmark.Station),
                new TypedValue(1000, Limit(bookmark.Note, MaxNoteLength)),
                new TypedValue(1000, Limit(bookmark.Label, 60)),
                new TypedValue(1040, bookmark.CreatedUtc.ToOADate())))
                record.Data = data;
            baseline.SetAt($"F{index++:D6}", record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        transaction.Commit();
    }

    private static string GetBaselineKey(BaselineChoice choice)
    {
        string identity = choice.CorridorId.Handle.ToString() + "|" + choice.BaselineName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "B_" + Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static string Limit(string value, int maxLength) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maxLength)];
}
