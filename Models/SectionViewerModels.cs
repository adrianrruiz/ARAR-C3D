using Autodesk.AutoCAD.DatabaseServices;
using ARAR.Civil3D.Models;
using SectionGeometryModel = ARAR.Civil3D.Models.SectionGeometry;

namespace ARAR.Civil3D.Services;

public sealed record BaselineChoice(ObjectId CorridorId, int BaselineIndex, ObjectId AlignmentId, string CorridorName, string BaselineName, string AlignmentName);
public sealed record SurfaceChoice(ObjectId SurfaceId, string Name);
public sealed record CorridorSectionData(
    BaselineChoice Baseline,
    CorridorSectionCache Cache,
    LongitudinalProfileSnapshot? Profile,
    int AdjustedElevationFallbacks = 0,
    int SkippedAssemblies = 0);
public sealed record TerrainSectionResult(SectionGeometryModel Geometry, double? AxisElevation);

