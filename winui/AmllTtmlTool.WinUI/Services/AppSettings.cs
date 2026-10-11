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

    private static double GetDouble(string key, double fallback) =>
        Store.GetDouble(key, fallback);

    private static void SetDouble(string key, double value) => Store.SetDouble(key, value);

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

    /// <summary>
    /// Playback level as a 0-100 slider position (amplitude = (v/100)^2).
    /// Remembered across launches and across opening or importing new audio
    /// files, because it belongs to the app rather than to a track.
    /// </summary>
    public static double Volume
    {
        get => GetDouble("volume", 50.0);
        set => SetDouble("volume", Math.Clamp(double.IsNaN(value) ? 50 : value, 0, 100));
    }

    /// <summary>
    /// Muted state. Unmute restores the level that was set before the mute.
    /// </summary>
    public static bool Mute
    {
        get => GetBool("mute", false);
        set => SetBool("mute", value);
    }

    /// <summary>
    /// Last non-zero playback level (0-100 slider position). Mute/unmute and
    /// slider-to-0 restore exactly this level; a missing or non-positive
    /// stored value falls back to 50.
    /// </summary>
    public static double LastNonZeroVolume
    {
        get
        {
            var v = GetDouble("lastNonZeroVolume", 50.0);
            return v > 0 ? v : 50.0;
        }
        set => SetDouble("lastNonZeroVolume",
            double.IsNaN(value) || value <= 0 ? 50.0 : Math.Clamp(value, 0, 100));
    }

    /// <summary>
    /// True while a test harness owns the app: the harness forces output
    /// silence through the in-app gain instead of the OS volume, and no
    /// setting is written, so a harness run can never persist a 0 or a mute
    /// over the user's own preferences.
    /// </summary>
    public static bool SilentMode
    {
        get => _silentMode;
        set => _silentMode = value;
    }

    private static bool _silentMode =
        Environment.GetEnvironmentVariable("AMLL_SILENT") is "1" or "true";

    public static void NotifyDisplayChanged() => RaiseDisplayChanged();
}
