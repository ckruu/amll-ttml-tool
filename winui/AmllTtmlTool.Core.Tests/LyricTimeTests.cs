using AmllTtmlTool.Core.Lyrics;

namespace AmllTtmlTool.Core.Tests;

public sealed class LyricTimeTests
{
    [Theory]
    [InlineData("00:00.000", 0)]
    [InlineData("00:01.000", 1000)]
    [InlineData("01:00.000", 60000)]
    [InlineData("03:09.213", 189213)]
    // Fraction digits are DECIMAL (mirrors parseTimespan): .1 = 100ms.
    [InlineData("00:01.1", 1100)]
    [InlineData("00:02.12", 2120)]
    [InlineData("00:03.123", 3123)]
    [InlineData("00:03:09.213", 189213)]
    [InlineData("1:02:03.004", 3723004)]
    [InlineData("5", 5000)]
    [InlineData("5.25", 5250)]
    [InlineData("0", 0)]
    public void Parse_Known_Inputs(string text, int want) =>
        Assert.Equal(want, LyricTime.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00:01.1234")]
    [InlineData("1:2:3:4:5")]
    [InlineData("-01:00")]
    [InlineData("  01:00")]
    public void Parse_Invalid_Throws(string text) =>
        Assert.Throws<FormatException>(() => LyricTime.Parse(text));

    [Fact]
    public void Parse_Overflow_Throws() =>
        Assert.Throws<FormatException>(() => LyricTime.Parse("999999:00.000"));

    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(1000, "00:01.000")]
    [InlineData(189213, "03:09.213")]
    [InlineData(3723004, "01:02:03.004")]
    [InlineData(-5, "00:00.000")]
    public void Format_Known_Values(int ms, string want) =>
        Assert.Equal(want, LyricTime.Format(ms));

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(189213, "03:09")]
    [InlineData(3723004, "01:02:03")]
    public void Format_Without_Milliseconds(int ms, string want) =>
        Assert.Equal(want, LyricTime.Format(ms, withMilliseconds: false));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(189213)]
    [InlineData(3723004)]
    [InlineData(86399999)]
    public void Format_Parse_Round_Trip(int ms) =>
        Assert.Equal(ms, LyricTime.Parse(LyricTime.Format(ms)));
}
