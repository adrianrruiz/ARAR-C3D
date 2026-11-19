namespace ARAR.Civil3D.Models;

/// <summary>Intervalo sólido en una trayectoria, medido en distancia horizontal.</summary>
public readonly record struct DistanceInterval(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>Lógica pura para repartir rayas sobre una trayectoria.</summary>
public static class DashPattern
{
    public const double DefaultMinimumDashMeters = 0.10;
    private const double Tolerance = 1e-9;

    public static IReadOnlyList<DistanceInterval> Compute(double totalLength, double dashLength, double gapLength, double minDash)
    {
        if (!double.IsFinite(totalLength) || totalLength < 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength), "La longitud total debe ser finita y no negativa.");
        if (!double.IsFinite(dashLength) || dashLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(dashLength), "La longitud de raya debe ser mayor que cero.");
        if (!double.IsFinite(gapLength) || gapLength < 0)
            throw new ArgumentOutOfRangeException(nameof(gapLength), "El espacio debe ser finito y no negativo.");
        if (!double.IsFinite(minDash) || minDash < 0)
            throw new ArgumentOutOfRangeException(nameof(minDash), "La raya mínima debe ser finita y no negativa.");
        if (totalLength <= Tolerance)
            return Array.Empty<DistanceInterval>();

        double period = dashLength + gapLength;
        int count = checked((int)Math.Ceiling(totalLength / period));
        var intervals = new List<DistanceInterval>(count);
        for (int i = 0; i < count; i++)
        {
            double start = i * period;
            if (start >= totalLength - Tolerance)
                break;

            double end = Math.Min(start + dashLength, totalLength);
            if (end - start + Tolerance >= minDash)
                intervals.Add(new DistanceInterval(start, end));
        }

        return intervals;
    }

    public static IReadOnlyList<DistanceInterval> ComputeAutoCadCentered(double totalLength, double dashLength, double gapLength, double minDash)
    {
        if (!double.IsFinite(totalLength) || totalLength < 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength), "La longitud total debe ser finita y no negativa.");
        if (!double.IsFinite(dashLength) || dashLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(dashLength), "La longitud de raya debe ser mayor que cero.");
        if (!double.IsFinite(gapLength) || gapLength < 0)
            throw new ArgumentOutOfRangeException(nameof(gapLength), "El espacio debe ser finito y no negativo.");
        if (!double.IsFinite(minDash) || minDash < 0)
            throw new ArgumentOutOfRangeException(nameof(minDash), "La raya mínima debe ser finita y no negativa.");
        if (totalLength <= Tolerance)
            return Array.Empty<DistanceInterval>();
        if (gapLength <= Tolerance)
            return totalLength + Tolerance >= minDash
                ? new[] { new DistanceInterval(0, totalLength) }
                : Array.Empty<DistanceInterval>();

        double period = dashLength + gapLength;
        int gaps = (int)Math.Floor((totalLength + Tolerance) / period);
        if (gaps <= 0)
            return totalLength + Tolerance >= minDash
                ? new[] { new DistanceInterval(0, totalLength) }
                : Array.Empty<DistanceInterval>();

        double remainder = Math.Max(0.0, totalLength - gaps * period);
        double endDash = (dashLength + remainder) / 2.0;
        var intervals = new List<DistanceInterval>(gaps + 1);

        if (endDash + Tolerance >= minDash)
            intervals.Add(new DistanceInterval(0, endDash));

        for (int i = 0; i < gaps - 1; i++)
        {
            double start = endDash + gapLength + i * period;
            double end = start + dashLength;
            if (end - start + Tolerance >= minDash)
                intervals.Add(new DistanceInterval(start, end));
        }

        double lastStart = Math.Max(0.0, totalLength - endDash);
        if (totalLength - lastStart + Tolerance >= minDash)
            intervals.Add(new DistanceInterval(lastStart, totalLength));

        return intervals;
    }

    public static IReadOnlyList<DistanceInterval> ComputeContinuous(double totalLength) =>
        !double.IsFinite(totalLength) || totalLength < 0
            ? throw new ArgumentOutOfRangeException(nameof(totalLength))
            : totalLength <= Tolerance
                ? Array.Empty<DistanceInterval>()
                : new[] { new DistanceInterval(0, totalLength) };
}
