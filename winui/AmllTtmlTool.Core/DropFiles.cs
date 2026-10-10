namespace AmllTtmlTool.Core;

// What a set of dropped (or picked) file names means. Pure logic over
// caller-supplied extension sets so the UI keeps exactly one list each
// (LyricStore.AudioExtensions / LyricStore.LyricExtensions) while this
// stays unit-testable without any WinUI dependency.
public enum DropKind
{
    None,
    Lyrics,
    Audio,
    Both,
    Unsupported,
}

public static class DropFiles
{
    public static DropKind Classify(
        IEnumerable<string> fileNames,
        ISet<string> audioExtensions,
        ISet<string> lyricExtensions)
    {
        var lyric = false;
        var audio = false;
        var other = false;
        var any = false;
        foreach (var name in fileNames)
        {
            any = true;
            var ext = ExtensionOf(name);
            if (lyricExtensions.Contains(ext)) lyric = true;
            else if (audioExtensions.Contains(ext)) audio = true;
            else other = true;
        }
        if (!any) return DropKind.None;
        if (!lyric && !audio) return DropKind.Unsupported;
        if (lyric && audio) return DropKind.Both;
        _ = other;
        return lyric ? DropKind.Lyrics : DropKind.Audio;
    }

    public static string Caption(DropKind kind) => kind switch
    {
        DropKind.Lyrics => "Drop to open lyrics",
        DropKind.Audio => "Drop to open audio",
        DropKind.Both => "Drop to open lyrics + audio",
        DropKind.Unsupported => "Unsupported file",
        _ => "Drop to open",
    };

    public static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : string.Empty;
    }
}
