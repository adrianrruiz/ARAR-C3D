using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Models;

public sealed record DrawOptions(bool DrawCorridor = true, bool DrawTerrain = true, bool DrawAxis = true, bool DrawShapes = true, bool DrawEarthwork = true, bool DrawGrid = true, bool DrawLabels = true, double HorizontalScale = 0.01, double VerticalExaggeration = 1, double ReferenceElevation = 0, bool DrawRange = false, double RangeStart = 0, double RangeEnd = 0, double RangeInterval = 10, int RangeColumns = 3, double SpacingX = 20, double SpacingY = 15);

public sealed record SectionDrawRequest(BaselineChoice Baseline, CorridorSectionSnapshot Section, SectionGeometry Terrain, SectionGeometry Earthwork);
