using AmllTtmlTool.Core.Lyrics.Import;

namespace AmllTtmlTool.Core.Tests;

public sealed class EncodingTests
{
    [Fact]
    public void Utf8_Bom_Stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("[00:01.00]hi")).ToArray();
        Assert.Equal("[00:01.00]hi", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Utf8_Without_Bom()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("日本語 lyrics ✓");
        Assert.Equal("日本語 lyrics ✓", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Utf16Le_With_Bom()
    {
        var bytes = new byte[] { 0xFF, 0xFE }
            .Concat(System.Text.Encoding.Unicode.GetBytes("abc 日本語")).ToArray();
        Assert.Equal("abc 日本語", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Utf16Be_With_Bom()
    {
        var bytes = new byte[] { 0xFE, 0xFF }
            .Concat(System.Text.Encoding.BigEndianUnicode.GetBytes("abc 日本語")).ToArray();
        Assert.Equal("abc 日本語", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Utf16Le_Without_Bom_Detected()
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes("[00:01.00]hello world test data 1234");
        Assert.Equal("[00:01.00]hello world test data 1234", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Utf16Be_Without_Bom_Detected()
    {
        var bytes = System.Text.Encoding.BigEndianUnicode.GetBytes("[00:01.00]hello world test data 1234");
        Assert.Equal("[00:01.00]hello world test data 1234", LyricEncoding.Decode(bytes));
    }

    [Fact]
    public void Invalid_Utf8_Yields_Replacement_Chars()
    {
        // Mirrors file.text(): undecodable bytes become U+FFFD, no throw.
        var bytes = new byte[] { (byte)'a', 0xFF, 0xFE, (byte)'b' };
        var text = LyricEncoding.Decode(bytes);
        Assert.Contains("a", text);
        Assert.Contains("b", text);
        Assert.Contains("\uFFFD", text);
    }
}
