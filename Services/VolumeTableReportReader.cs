using System.IO;
using System.Globalization;
using System.Xml.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.Services;

/// <summary>Obtiene cantidades mediante el informe QTO generado por Civil 3D.</summary>
public static class VolumeTableReportReader
{
    public static VolumeGroupData Read(
        Database database,
        ObjectId sampleLineGroupId,
        string alignmentName,
        string groupName,
        double? startStation,
        double? endStation)
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"ARAR_QTO_{Guid.NewGuid():N}.xml");
        string materialListName;

        try
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var group = (SampleLineGroup)transaction.GetObject(sampleLineGroupId, OpenMode.ForRead);
                QTOMaterialListCollection materialLists = group.MaterialLists;
                if (materialLists.Count == 0)
                    throw new InvalidOperationException("El grupo no tiene una lista de materiales asignada.");

                using QTOMaterialList materialList = materialLists[0];
                string[] mappingNames = group.GetQTOMappingNames();
                if (mappingNames.Length == 0)
                    throw new InvalidOperationException("No hay un cálculo QTO disponible para el grupo de líneas de muestreo.");

                // Civil 3D genera el XML con los valores de su propio cálculo de cantidades.
                materialListName = materialList.Name;
                SampleLineGroup.ReportQuantities(sampleLineGroupId, mappingNames[0], reportPath, string.Empty);
                transaction.Commit();
            }

            return ParseReport(reportPath, alignmentName, groupName, materialListName, startStation, endStation);
        }
        finally
        {
            try
            {
                if (File.Exists(reportPath))
                    File.Delete(reportPath);
            }
            catch (IOException)
            {
                // El informe es temporal; no se deja que un fallo de limpieza oculte el resultado QTO.
            }
        }
    }

    private static VolumeGroupData ParseReport(
        string reportPath,
        string alignmentName,
        string groupName,
        string materialListName,
        double? startStation,
        double? endStation)
    {
        XDocument document = XDocument.Load(reportPath, LoadOptions.None);
        var stationValues = new List<(double Station, List<VolumeMaterialValue> Materials)>();

        foreach (XElement crossSection in document.Descendants()
                     .Where(element => IsName(element, "CrossSect") && TryReadStation(element, out _)))
        {
            if (!TryReadStation(crossSection, out double station))
                continue;
            if (startStation.HasValue && station < startStation.Value - 0.0005)
                continue;
            if (endStation.HasValue && station > endStation.Value + 0.0005)
                continue;

            var materials = new List<VolumeMaterialValue>();
            foreach (XElement material in crossSection.Descendants().Where(element => IsName(element, "MaterialCrossSect")))
            {
                string? name = GetAttribute(material, "name");
                if (string.IsNullOrWhiteSpace(name) ||
                    !TryReadNumber(material, "area", out double area) ||
                    !TryReadNumber(material, "volume", out double incremental) ||
                    !TryReadNumber(material, "cumVolume", out double cumulative, "cumulativeVolume", "cumUsableVolume"))
                    continue;

                materials.Add(new VolumeMaterialValue(name, area, incremental, cumulative));
            }

            if (materials.Count > 0)
                stationValues.Add((station, materials));
        }

        stationValues.Sort((left, right) => left.Station.CompareTo(right.Station));
        if (stationValues.Count == 0)
            throw new InvalidDataException("El informe QTO no contiene secciones con áreas y volúmenes. Compruebe que los materiales estén calculados.");

        string[] materialNames = stationValues
            .SelectMany(item => item.Materials)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var stations = stationValues
            .Select(item => new VolumeStationData(item.Station, item.Materials))
            .ToArray();

        return new VolumeGroupData(
            alignmentName,
            groupName,
            materialListName,
            stations[0].Station,
            stations[^1].Station,
            materialNames,
            stations);
    }

    private static bool TryReadStation(XElement element, out double station) =>
        TryReadNumber(element, "sta", out station, "station", "stationValue");

    private static bool TryReadNumber(XElement element, string primaryName, out double value, params string[] alternateNames)
    {
        value = 0;
        string? raw = GetAttribute(element, primaryName);
        if (raw is null)
        {
            foreach (string alternateName in alternateNames)
            {
                raw = GetAttribute(element, alternateName);
                if (raw is not null)
                    break;
            }
        }

        return raw is not null && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static string? GetAttribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool IsName(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
}
