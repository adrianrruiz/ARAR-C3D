using ARAR.Civil3D.Models;
using Xunit;

namespace ARAR.Civil3D.Tests;

public sealed class DashPatternTests
{
    [Theory]
    [InlineData(12, 2, 7, 11, 4)]
    [InlineData(15, 3, 14, 15, 1)]
    [InlineData(30, 5, 28, 30, 2)]
    [InlineData(7, 1, 0, 4, 4)]
    [InlineData(3, 1, 0, 3, 3)]
    [InlineData(14.05, 2, 7, 11, 4)]
    [InlineData(14.10, 3, 14, 14.10, 0.10)]
    public void Compute_ReturnsExpectedDashIntervals(double total, int expectedCount, double finalStart, double finalEnd, double finalLength)
    {
        IReadOnlyList<DistanceInterval> actual = DashPattern.Compute(total, 4, 3, 0.10);

        Assert.Equal(expectedCount, actual.Count);
        Assert.Equal(finalStart, actual[^1].Start, 8);
        Assert.Equal(finalEnd, actual[^1].End, 8);
        Assert.Equal(finalLength, actual[^1].Length, 8);
    }

    [Fact]
    public void Compute_ZeroLengthReturnsNoIntervals() =>
        Assert.Empty(DashPattern.Compute(0, 4, 3, 0.10));

    [Fact]
    public void ComputeContinuous_ReturnsOneFullInterval() =>
        Assert.Equal(new DistanceInterval(0, 14.1), Assert.Single(DashPattern.ComputeContinuous(14.1)));
}
