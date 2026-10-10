namespace AmllTtmlTool.Core.Audio;

// Pure display-title resolution for SMTC (and anywhere else a track
// label is needed): tag title wins, file name (without extension) is the
// fallback, "Unknown track" is the last resort. Artist joins multiples;
// album takes the first. Fully unit-testable, no OS.
public static class SmtcDisplay
{
    public static (string Title, string Artist, string Album) ForTrack(AudioTrackMetadata meta)
    {
        var title = meta.Titles.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        title = string.IsNullOrWhiteSpace(title) ? meta.FileName : title.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Unknown track";
        var artist = string.Join(", ",
            meta.Artists.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        var album = meta.Albums.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim() ?? "";
        return (title, artist, album);
    }
}
