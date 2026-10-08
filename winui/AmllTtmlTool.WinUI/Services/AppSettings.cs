using Windows.Storage;

namespace AmllTtmlTool.WinUI.Services;

public static class AppSettings
{
    public static event Action? DisplayChanged;

    private static void RaiseDisplayChanged() => DisplayChanged?.Invoke();
    private static ApplicationDataContainer Local => ApplicationData.Current.LocalSettings;

    private static string Get(string key, string fallback) =>
        Local.Values[key] as string ?? fallback;

    private static void Set(string key, string value) => Local.Values[key] = value;

    private static bool GetBool(string key, bool fallback) =>
        Local.Values[key] is bool b ? b : fallback;

    private static void SetBool(string key, bool value) => Local.Values[key] = value;

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

    public static bool FollowPlayback
    {
        get => GetBool("followPlayback", false);
        set => SetBool("followPlayback", value);
    }

    public static void NotifyDisplayChanged() => RaiseDisplayChanged();
}
