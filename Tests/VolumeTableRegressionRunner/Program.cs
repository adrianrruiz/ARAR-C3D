using ARAR.Civil3D.Services;

var cases = new (double Input, string Expected)[]
{
    (-0.0001, "0+000.000"),
    (-2.5, "-0+002.500"),
    (999.9996, "1+000.000")
};

int failures = 0;
foreach ((double input, string expected) in cases)
{
    string actual = VolumeTableTextFormat.FormatStation(input);
    bool passed = string.Equals(actual, expected, StringComparison.Ordinal);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {input.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} -> {actual}; esperado {expected}");
    if (!passed)
        failures++;
}

Check(VolumeTableTextFormat.TryParseStation("0+013.002", out double positive) && positive == 13.002,
    "PK 0+013.002 -> 13.002");
Check(VolumeTableTextFormat.TryParseStation("-0+002.500", out double negative) && negative == -2.5,
    "PK -0+002.500 -> -2.5");
Check(VolumeTableTextFormat.TryParseNumber("3,994,931.2456m", out double coordinate) && coordinate == 3994931.2456,
    "Coordenada con separadores y sufijo m");
Check(VolumeTableTextFormat.TryParseNumber("39.868m", out double distance) && distance == 39.868,
    "Distancia con sufijo m");

var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
string firstName = VolumeTableTextFormat.CreateUniqueWorksheetName("Alineación/ACC3?*", names);
string duplicateName = VolumeTableTextFormat.CreateUniqueWorksheetName("Alineación/ACC3?*", names);
Check(firstName.Length <= 31 && !firstName.Any(character => ":\\/?*[]".Contains(character)),
    "Nombre de hoja: longitud y caracteres válidos");
Check(!string.Equals(firstName, duplicateName, StringComparison.OrdinalIgnoreCase) && duplicateName.EndsWith("_2", StringComparison.Ordinal),
    "Nombre de hoja duplicado: sufijo numérico");

return failures == 0 ? 0 : 1;

void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
    if (!condition)
        failures++;
}
