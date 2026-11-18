using System.Globalization;
using System.Text;

namespace ARAR.Civil3D.Services;

/// <summary>Funciones independientes de Autodesk para los formatos de la tabla de volúmenes.</summary>
public static class VolumeTableTextFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string FormatStation(double station)
    {
        double roundedStation = Math.Round(station, 3, MidpointRounding.AwayFromZero);
        bool negative = roundedStation < 0;
        double rounded = Math.Abs(roundedStation);
        long thousands = (long)Math.Floor(rounded / 1000.0);
        double remainder = rounded - (thousands * 1000.0);
        return $"{(negative ? "-" : string.Empty)}{thousands.ToString(Invariant)}+{remainder.ToString("000.000", Invariant)}";
    }

    public static bool TryParseStation(string? text, out double station)
    {
        station = 0;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        int plusIndex = value.IndexOf('+');
        if (plusIndex < 0)
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, Invariant, out station);

        bool negative = value.StartsWith("-", StringComparison.Ordinal);
        string thousandsText = value[..plusIndex].TrimStart('-', '+').Replace(",", string.Empty, StringComparison.Ordinal);
        string remainderText = value[(plusIndex + 1)..].Replace(",", string.Empty, StringComparison.Ordinal);
        if (!long.TryParse(thousandsText, NumberStyles.Integer, Invariant, out long thousands) ||
            !double.TryParse(remainderText, NumberStyles.Float, Invariant, out double remainder))
            return false;

        station = (thousands * 1000.0) + remainder;
        if (negative)
            station = -station;
        return true;
    }

    public static bool TryParseNumber(string? text, out double number)
    {
        number = 0;
        string value = (text ?? string.Empty).Trim();
        if (value.EndsWith('m') || value.EndsWith('M'))
            value = value[..^1].TrimEnd();

        value = value.Replace(",", string.Empty, StringComparison.Ordinal);
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, Invariant, out number);
    }

    public static string CreateUniqueWorksheetName(string? sourceName, ISet<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        string name = string.IsNullOrWhiteSpace(sourceName) ? "Tabla" : sourceName.Trim();
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
            builder.Append(character is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : character);

        name = builder.ToString().Trim('\'', ' ');
        if (name.Length == 0)
            name = "Tabla";
        if (name.Length > 31)
            name = name[..31];

        string candidate = name;
        int suffix = 2;
        while (existingNames.Contains(candidate))
        {
            string ending = $"_{suffix++}";
            candidate = name[..Math.Min(name.Length, 31 - ending.Length)] + ending;
        }

        existingNames.Add(candidate);
        return candidate;
    }
}
