using AmllTtmlTool.Core.Audio;

namespace AmllTtmlTool.Core.Tests;

public sealed class SpeedDisplayTests
{
    [Theory]
    [InlineData(0.25, ".25x")]
    [InlineData(0.5, ".5x")]
    [InlineData(0.85, ".85x")]
    [InlineData(1.0, "1x")]
    [InlineData(1.25, "1.25x")]
    [InlineData(1.5, "1.5x")]
    [InlineData(2.0, "2x")]
    [InlineData(1.20, "1.2x")]
    public void Format_Simplifies(double rate, string want) =>
        Assert.Equal(want, SpeedDisplay.Format(rate));

    [Theory]
    [InlineData(0.25, 0)]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    [InlineData(1.25, 4)]
    [InlineData(0.85, -1)]
    [InlineData(1.26, -1)]
    public void PresetIndex_Matches_With_Tolerance(double rate, int want) =>
        Assert.Equal(want, SpeedDisplay.PresetIndex(rate));

    [Fact]
    public void Cycle_Follows_Preset_Row_Order_From_1x()
    {
        var seen = new List<double>();
        var rate = 1.0;
        for (var i = 0; i < 6; i++)
        {
            rate = SpeedDisplay.CycleFrom(rate);
            seen.Add(rate);
        }
        Assert.Equal([1.5, 1.25, 0.25, 0.5, 1.0, 1.5], seen);
    }

    [Fact]
    public void Cycle_From_Non_Preset_Goes_To_1x() =>
        Assert.Equal(1.0, SpeedDisplay.CycleFrom(0.85));
}
