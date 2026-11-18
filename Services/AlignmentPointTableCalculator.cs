using System.Globalization;

namespace ARAR.Civil3D.Services;

/// <summary>Geometría y formatos independientes de AutoCAD para la tabla de puntos.</summary>
public static class AlignmentPointTableCalculator
{
    private const double ZeroTolerance = 1e-10;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static double ParseStation(string text)
    {
        string value = text.Trim();
        int plusIndex = value.IndexOf('+');

        if (plusIndex >= 0)
        {
            string kilometerText = value.Substring(0, plusIndex).Trim();
            string meterText = value.Substring(plusIndex + 1).Trim();
            if (!double.TryParse(kilometerText, NumberStyles.Float, Invariant, out double kilometers) ||
                !double.TryParse(meterText, NumberStyles.Float, Invariant, out double meters) ||
                kilometers < 0 || meters < 0 || meters >= 1000)
            {
                throw new FormatException($"PK no válido: «{text}». Use, por ejemplo, 0+039.868 o 39.868.");
            }

            double sign = value.StartsWith("-", StringComparison.Ordinal) ? -1 : 1;
            return sign * (kilometers * 1000 + meters);
        }

        if (double.TryParse(value, NumberStyles.Float, Invariant, out double station) && double.IsFinite(station))
            return station;

        throw new FormatException($"PK no válido: «{text}». Use, por ejemplo, 0+039.868 o 39.868.");
    }

    public static string FormatStation(double station)
    {
        // Determina el signo después de redondear para evitar formatos como -0+000.000.
        double roundedStation = Math.Round(station, 3, MidpointRounding.AwayFromZero);
        long totalThousandths = checked((long)Math.Round(Math.Abs(roundedStation) * 1000, MidpointRounding.AwayFromZero));
        long wholeMeters = totalThousandths / 1000;
        long kilometers = wholeMeters / 1000;
        long remainingThousandths = totalThousandths % 1_000_000;
        double remainingMeters = remainingThousandths / 1000.0;
        string sign = roundedStation < 0 ? "-" : string.Empty;
        return string.Format(Invariant, "{0}{1}+{2:000.000}", sign, kilometers, remainingMeters);
    }

    public static string FormatCoordinate(double value) => value.ToString("#,##0.0000", Invariant) + "m";

    public static string FormatDistance(double value) => value.ToString("#,##0.000", Invariant) + "m";

    public static double DistanceAlongAlignment(double currentStation, double nextStation) => nextStation - currentStation;

    public static SegmentMeasurement Measure(double easting1, double northing1, double easting2, double northing2)
    {
        double deltaEasting = easting2 - easting1;
        double deltaNorthing = northing2 - northing1;
        double distance = Math.Sqrt(deltaEasting * deltaEasting + deltaNorthing * deltaNorthing);

        if (distance <= ZeroTolerance)
            return new SegmentMeasurement(0, "Sin orientación (distancia cero).");

        if (Math.Abs(deltaNorthing) <= ZeroTolerance)
            return new SegmentMeasurement(distance, deltaEasting > 0 ? "E" : "O");
        if (Math.Abs(deltaEasting) <= ZeroTolerance)
            return new SegmentMeasurement(distance, deltaNorthing > 0 ? "N" : "S");

        double angleDegrees = Math.Atan2(Math.Abs(deltaEasting), Math.Abs(deltaNorthing)) * 180.0 / Math.PI;
        long hundredthsOfArcSecond = (long)Math.Round(angleDegrees * 360_000.0, MidpointRounding.AwayFromZero);
        const long HundredthsPerDegree = 360_000;
        const long HundredthsPerMinute = 6_000;
        int degrees = (int)(hundredthsOfArcSecond / HundredthsPerDegree);
        long remainder = hundredthsOfArcSecond % HundredthsPerDegree;
        int minutes = (int)(remainder / HundredthsPerMinute);
        double seconds = (remainder % HundredthsPerMinute) / 100.0;

        // El redondeo de una dirección muy próxima a un eje puede llevarla a 90° o 0°.
        if (degrees >= 90)
            return new SegmentMeasurement(distance, deltaEasting > 0 ? "E" : "O");
        if (degrees == 0 && minutes == 0 && seconds == 0)
            return new SegmentMeasurement(distance, deltaNorthing > 0 ? "N" : "S");

        string northSouth = deltaNorthing > 0 ? "N" : "S";
        string eastWest = deltaEasting > 0 ? "E" : "O";
        string bearing = string.Format(Invariant, "{0}{1}° {2:00}' {3:00.00}\"{4}", northSouth, degrees, minutes, seconds, eastWest);
        return new SegmentMeasurement(distance, bearing);
    }

    public readonly record struct SegmentMeasurement(double Distance, string Bearing);
}
