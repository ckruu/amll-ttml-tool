using AmllTtmlTool.Core;

namespace AmllTtmlTool.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amll-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string FilePath(string name = "settings.json") => Path.Combine(_dir, name);

    [Fact]
    public void MissingFile_ReturnsFallbacks()
    {
        var store = new SettingsStore(FilePath());
        Assert.Equal("ttml", store.GetString("lastSaveFormat", "ttml"));
        Assert.True(store.GetBool("showOutline", true));
        Assert.False(store.GetBool("showBpm", false));
    }

    [Fact]
    public void RoundTrip_PersistsAcrossInstances()
    {
        var path = FilePath();
        var first = new SettingsStore(path);
        first.SetString("lastSaveFormat", "apple");
        first.SetBool("followPlayback", true);
        var second = new SettingsStore(path);
        Assert.Equal("apple", second.GetString("lastSaveFormat", "ttml"));
        Assert.True(second.GetBool("followPlayback", false));
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        var path = FilePath();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{not json");
        var store = new SettingsStore(path);
        Assert.Equal("fallback", store.GetString("anything", "fallback"));
        store.SetString("ok", "yes");
        Assert.Equal("yes", new SettingsStore(path).GetString("ok", "no"));
    }

    [Fact]
    public void WrongType_FallsBack()
    {
        var path = FilePath();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{"flag": "yes", "name": true}""");
        var store = new SettingsStore(path);
        Assert.False(store.GetBool("flag", false));
        Assert.Equal("fallback", store.GetString("name", "fallback"));
    }
}
