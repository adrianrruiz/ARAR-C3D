using ARAR.Civil3D.Services;
using Xunit;

namespace ARAR.Civil3D.Tests;

public sealed class VolumeTableTextFormatTests
{
    [Theory]
    [InlineData(-0.0001, "0+000.000")]
    [InlineData(-2.5, "-0+002.500")]
    [InlineData(999.9996, "1+000.000")]
    public void FormatStation_RoundsBeforeChoosingSignAndCarriesThousands(double input, string expected)
    {
        Assert.Equal(expected, VolumeTableTextFormat.FormatStation(input));
    }

    [Theory]
    [InlineData("0+013.002", 13.002)]
    [InlineData("-0+002.500", -2.5)]
    public void TryParseStation_ParsesStationNotation(string input, double expected)
    {
        Assert.True(VolumeTableTextFormat.TryParseStation(input, out double actual));
        Assert.Equal(expected, actual, 3);
    }

    [Theory]
    [InlineData("3,994,931.2456m", 3994931.2456)]
    [InlineData("39.868m", 39.868)]
    public void TryParseNumber_ParsesInvariantValuesAndMetricSuffix(string input, double expected)
    {
        Assert.True(VolumeTableTextFormat.TryParseNumber(input, out double actual));
        Assert.Equal(expected, actual, 4);
    }

    [Fact]
    public void CreateUniqueWorksheetName_RemovesInvalidCharactersAndAddsUniqueSuffix()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string first = VolumeTableTextFormat.CreateUniqueWorksheetName("Alineación/G:Grupo?", names);
        string duplicate = VolumeTableTextFormat.CreateUniqueWorksheetName("Alineación/G:Grupo?", names);
        string longName = VolumeTableTextFormat.CreateUniqueWorksheetName(new string('X', 40), names);

        Assert.Equal("Alineación_G_Grupo_", first);
        Assert.Equal("Alineación_G_Grupo__2", duplicate);
        Assert.Equal(31, longName.Length);
        Assert.DoesNotContain(longName, ":\\/?*[]");
    }
}
