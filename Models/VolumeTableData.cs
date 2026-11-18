namespace ARAR.Civil3D.Models;

/// <summary>Datos de cantidades ya calculados por Civil 3D para una estación.</summary>
public sealed record VolumeMaterialValue(string Name, double Area, double IncrementalVolume, double CumulativeVolume);

public sealed record VolumeStationData(double Station, IReadOnlyList<VolumeMaterialValue> Materials);

public sealed record VolumeGroupData(
    string AlignmentName,
    string GroupName,
    string MaterialListName,
    double StartStation,
    double EndStation,
    IReadOnlyList<string> MaterialNames,
    IReadOnlyList<VolumeStationData> Stations);
