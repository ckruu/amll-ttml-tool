namespace AmllTtmlTool.Core.Audio;

// Read-only contract for audio input files (test-data corruption guard:
// a harness once appended its report INTO an .m4a, destroying it).
// Every engine entry point opens inputs through OpenReadShared, so a
// write handle on test data fails fast here instead of corrupting it.
public static class AudioInput
{
    private static readonly string[] AudioExtensions =
    {
        ".m4a", ".m4b", ".mp3", ".flac", ".aac", ".ogg",
        ".opus", ".wav", ".wma", ".aiff", ".aif", ".alac",
    };

    public static bool IsAudioPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && AudioExtensions.Contains(
            Path.GetExtension(path).ToLowerInvariant());

    // Opens an audio input strictly read-only and shared-read. Throws
    // FileNotFoundException / IOException with the path attached. The
    // caller owns the stream; ffmpeg entry points open-check-close.
    public static FileStream OpenReadShared(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Audio path is empty.", nameof(path));
        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // A harness report must never be an audio file (the corruption we
    // observed). Returns a temp default when the candidate is one.
    public static string GuardReportPath(string? candidate, string mode) =>
        IsAudioPath(candidate)
            ? Path.Combine(Path.GetTempPath(), $"{mode}-report.txt")
            : candidate!;
}
