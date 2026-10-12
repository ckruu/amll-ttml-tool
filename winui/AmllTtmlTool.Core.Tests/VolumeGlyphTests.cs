using AmllTtmlTool.Core.Audio;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class VolumeGlyphTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(100)]
    public void Muted_Always_MuteGlyph(double percent) =>
        Assert.Equal("\uE74F", VolumeGlyph.ForLevel(percent, muted: true));

    [Fact]
    public void Zero_Shows_MuteGlyph()
    {
        Assert.Equal("\uE74F", VolumeGlyph.ForLevel(0, muted: false));
        Assert.Equal("\uE74F", VolumeGlyph.ForLevel(-5, muted: false));
        Assert.Equal("\uE74F", VolumeGlyph.ForLevel(double.NaN, muted: false));
    }

    [Fact]
    public void Whole_Range_Maps_To_Bands()
    {
        for (var v = 0; v <= 100; v++)
        {
            var want = v <= 0 ? "\uE74F"
                : v < 34 ? "\uE993"
                : v < 67 ? "\uE994"
                : "\uE995";
            Assert.Equal(want, VolumeGlyph.ForLevel(v, muted: false));
        }
    }

    [Fact]
    public void Above_Range_Clamps_To_Top_Band() =>
        Assert.Equal("\uE995", VolumeGlyph.ForLevel(250, muted: false));
}
