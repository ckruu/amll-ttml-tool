using System.Text.RegularExpressions;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Audio;

public sealed record AudioTrackMetadata(
    List<string> Titles,
    List<string> Artists,
    List<string> Albums,
    List<string> Composers,
    List<string> Isrcs,
    string FileName);

public static partial class AudioMetadata
{
    public static List<string> SplitMetadataValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return Splitter()
            .Split(raw)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
    }

    public static List<string> CollectAndSplit(
        IReadOnlyDictionary<string, string> raw,
        params string[] targetKeys)
    {
        var wanted = new HashSet<string>(targetKeys.Select(k => k.ToLowerInvariant()));
        var collected = new List<string>();
        foreach (var (rawKey, rawValue) in raw)
        {
            if (!wanted.Contains(rawKey.ToLowerInvariant())) continue;
            if (string.IsNullOrWhiteSpace(rawValue)) continue;
            foreach (var val in SplitMetadataValue(rawValue))
            {
                if (!collected.Contains(val)) collected.Add(val);
            }
        }
        return collected;
    }

    public static AudioTrackMetadata ParseAudioTrackMetadata(
        IReadOnlyDictionary<string, string> raw,
        string? fileNameWithExtension = null)
    {
        var fileName = string.Empty;
        if (!string.IsNullOrEmpty(fileNameWithExtension))
        {
            var dot = fileNameWithExtension.LastIndexOf('.');
            fileName = (dot >= 0 ? fileNameWithExtension[..dot] : fileNameWithExtension).Trim();
        }
        return new AudioTrackMetadata(
            CollectAndSplit(raw, "title", "musicName", "track"),
            CollectAndSplit(raw, "artist", "artists", "author", "singer"),
            CollectAndSplit(raw, "album", "albumTitle"),
            CollectAndSplit(raw, "composer", "songwriter", "lyricist"),
            CollectAndSplit(raw, "isrc"),
            fileName);
    }

    public static List<TtmlMetadata> ToTtmlMetadata(AudioTrackMetadata meta)
    {
        var result = new List<TtmlMetadata>();
        if (meta.Titles.Count > 0) result.Add(new TtmlMetadata("musicName", [.. meta.Titles]));
        if (meta.Artists.Count > 0) result.Add(new TtmlMetadata("artists", [.. meta.Artists]));
        if (meta.Albums.Count > 0) result.Add(new TtmlMetadata("album", [.. meta.Albums]));
        if (meta.Composers.Count > 0) result.Add(new TtmlMetadata("songwriter", [.. meta.Composers]));
        if (meta.Isrcs.Count > 0) result.Add(new TtmlMetadata("isrc", [.. meta.Isrcs]));
        return result;
    }

    public static bool MergeExtractedMetadata(
        List<TtmlMetadata> current,
        List<TtmlMetadata> extracted)
    {
        var changed = false;
        foreach (var item in extracted)
        {
            var values = item.Value.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (values.Count == 0) continue;
            var existing = current.Find(m => m.Key == item.Key);
            if (existing is null)
            {
                current.Add(new TtmlMetadata(item.Key, values));
                changed = true;
                continue;
            }
            if (existing.Value.Any(v => v.Trim().Length > 0)) continue;
            var idx = current.IndexOf(existing);
            current[idx] = existing with { Value = values };
            changed = true;
        }
        return changed;
    }

    [GeneratedRegex(@"[\n,;/，；、|\\]")]
    private static partial Regex Splitter();
}
