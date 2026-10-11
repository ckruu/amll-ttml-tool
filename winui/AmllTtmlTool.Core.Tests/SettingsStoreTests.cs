using AmllTtmlTool.Core;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class SettingsStoreTests
{
    private static SettingsStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "amll-settings-" + Guid.NewGuid().ToString("N") + ".json");
        return new SettingsStore(path);
    }

    [Fact]
    public void Volume_RoundTrips()
    {
        var s = NewStore(out _);
        s.SetDouble("volume", 37);
        s.Reload();
        Assert.Equal(37.0, s.GetDouble("volume", 50.0), 9);
    }

    [Fact]
    public void Volume_Fractional_RoundTrips_And_CultureIndependent()
    {
        var s = NewStore(out _);
        s.SetDouble("volume", 12.5);
        s.Reload();
        // A user on a comma-decimal locale must not read 12,5 back as 12.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(12.5, s.GetDouble("volume", 0.0), 9);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Volume_Falls_Back_When_Missing_Or_Corrupt()
    {
        var s = NewStore(out _);
        Assert.Equal(50.0, s.GetDouble("volume", 50.0));
        s.SetString("volume", "not-a-number");
        Assert.Equal(50.0, s.GetDouble("volume", 50.0));
    }

    [Fact]
    public void Mute_RoundTrips()
    {
        var s = NewStore(out _);
        s.SetBool("mute", true);
        s.Reload();
        Assert.True(s.GetBool("mute", false));
    }

    [Fact]
    public void Volume_Survives_A_Whole_New_Store_Object()
    {
        var s = NewStore(out var path);
        s.SetDouble("volume", 83);
        s.SetBool("mute", true);

        // A second process reading the same file is the "remembered across
        // launches" case.
        var reopened = new SettingsStore(path);
        Assert.Equal(83.0, reopened.GetDouble("volume", 50.0), 9);
        Assert.True(reopened.GetBool("mute", false));
    }

    [Fact]
    public void Missing_File_Yields_Fallbacks_Not_An_Exception()
    {
        var missing = Path.Combine(Path.GetTempPath(), "amll-absent-" + Guid.NewGuid().ToString("N") + ".json");
        var s = new SettingsStore(missing);
        Assert.Equal(50.0, s.GetDouble("volume", 50.0));
        Assert.False(s.GetBool("mute", false));
    }

    [Fact]
    public void Corrupt_File_Yields_Fallbacks_Not_An_Exception()
    {
        var s = NewStore(out var path);
        File.WriteAllText(path, "{ this is not json at all");
        s.Reload();
        Assert.Equal(50.0, s.GetDouble("volume", 50.0));
        Assert.False(s.GetBool("mute", false));
    }
}
