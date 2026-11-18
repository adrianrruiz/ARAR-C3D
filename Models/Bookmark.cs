namespace ARAR.Civil3D.Models;

/// <summary>Persistent marker for a noteworthy corridor section.</summary>
public sealed record SectionBookmark(double Station, string Note, string Label, DateTime CreatedUtc);
