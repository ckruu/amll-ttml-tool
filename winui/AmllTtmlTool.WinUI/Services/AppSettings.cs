using AmllTtmlTool.Core;

namespace AmllTtmlTool.WinUI.Services;

public static class AppSettings
{
    public static event Action? DisplayChanged;

    private static void RaiseDisplayChanged() => DisplayChanged?.Invoke();

    private static SettingsStore? _store;

    internal static SettingsStore Store =>
        _store ??= new SettingsStore(DefaultPath());

    internal static void ResetForTests(SettingsStore store) => _store = store;

    private static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmllTtmlTool",
        "settings.json");

    private static string Get(string key, string fallback) =>
        Store.GetString(key, fallback);

    private static void Set(string key, string value) => Store.SetString(key, value);

    private static bool GetBool(string key, bool fallback) =>
        Store.GetBool(key, fallback);

    private static void SetBool(string key, bool value) => Store.SetBool(key, value);

    public static string LastSaveFormat
    {
        get => Get("lastSaveFormat", "ttml");
        set => Set("lastSaveFormat", value);
    }

    public static string LastCopyFormat
    {
        get => Get("lastCopyFormat", "ttml");
        set => Set("lastCopyFormat", value);
    }

    public static bool ShowRomanization
    {
        get => GetBool("showRomanization", false);
        set => SetBool("showRomanization", value);
    }

    public static bool ShowTranslation
    {
        get => GetBool("showTranslation", false);
        set => SetBool("showTranslation", value);
    }

    public static bool ShowWordRomanization
    {
        get => GetBool("showWordRomanization", false);
        set => SetBool("showWordRomanization", value);
    }

    public static bool ShowOutline
    {
        get => GetBool("showOutline", true);
        set => SetBool("showOutline", value);
    }

    public static bool ShowBpm
    {
        get => GetBool("showBpm", false);
        set => SetBool("showBpm", value);
    }

    public static bool ShowPreview
    {
        get => GetBool("showPreview", true);
        set => SetBool("showPreview", value);
    }

    public static bool FollowPlayback
    {
        get => GetBool("followPlayback", false);
        set => SetBool("followPlayback", value);
    }

    public static void NotifyDisplayChanged() => RaiseDisplayChanged();
}
