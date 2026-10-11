namespace AmllTtmlTool.Core.Audio;

// Pure track-tip label resolution (no OS): title/artist fallbacks plus the
// friendly codec name shown on the tip's second line. Fully unit-testable.
public static class TrackDisplay
{
    public static string TitleFor(AudioTrackMetadata meta) =>
        SmtcDisplay.ForTrack(meta).Title;

    public static string ArtistOrUnknown(AudioTrackMetadata meta)
    {
        var (_, artist, _) = SmtcDisplay.ForTrack(meta);
        return string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist;
    }

    // Friendly codec names for the tip. Probe codec_name values are matched
    // case-insensitively; anything unlisted shows uppercased, never blank.
    public static string CodecLabel(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName)) return "?";
        return codecName.Trim().ToLowerInvariant() switch
        {
            "alac" => "ALAC",
            "flac" => "FLAC",
            "eac3" or "ec3" => "E-AC-3",
            "ac3" => "AC-3",
            "truehd" => "TrueHD",
            "aac" => "AAC",
            "mp3" => "MP3",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            var pcm when pcm.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            var wma when wma.StartsWith("wma", StringComparison.Ordinal) => "WMA",
            var other => other.ToUpperInvariant(),
        };
    }

    // Second line: "artist - CONTAINER CODEC" (e.g. "Petit Rabbit's - M4A
    // ALAC"). The artist side ellipsizes in the UI; the codec side is always
    // a short fixed token, so it stays visible.
    public static string Line2(string artistOrUnknown, string containerExt, string? codecName)
    {
        var container = string.IsNullOrWhiteSpace(containerExt)
            ? "?"
            : containerExt.Trim().TrimStart('.').ToUpperInvariant();
        return $"{artistOrUnknown} - {container} {CodecLabel(codecName)}";
    }
}
