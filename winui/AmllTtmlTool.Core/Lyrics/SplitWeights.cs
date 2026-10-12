using System.Globalization;

namespace AmllTtmlTool.Core.Lyrics;

// Word-split time distribution, ported from
// segmentation/utils/segmentation.ts (calculateWeight/distributeTime) +
// utils/charUtils.ts (getCharType). Manual split only (no postProcess
// punctuation merging — recalculateWordTime weights raw segments).
// Matches JS semantics: string lengths are UTF-16 code units on both sides.

public enum LyricCharType { Cjk, Latin, Numeric, Whitespace, Other }

public static class SplitWeights
{
    public const double DefaultPunctuationWeight = 0.2;

    // .NET regex has no \p{Script=...}; the original tests
    // Latin/Cyrillic/Greek scripts + \p{M} + "'". These block ranges cover
    // the three scripts (JS would call anything else Other — kept so).
    // Known edge: astral-plane Latin-script chars (e.g. Deseret) test as
    // Latin in JS but Other here (first UTF-16 unit only) — no lyric impact.
    private static bool IsLatinScript(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
        || (c >= '\u00C0' && c <= '\u00D6') || (c >= '\u00D8' && c <= '\u00F6')
        || (c >= '\u00F8' && c <= '\u02AF')
        || (c >= '\u1E00' && c <= '\u1EFF')
        || (c >= '\u2C60' && c <= '\u2C7F') || (c >= '\uA720' && c <= '\uA7FF')
        || c == '\'';

    private static bool IsCyrillicScript(char c) =>
        (c >= '\u0400' && c <= '\u04FF') || (c >= '\u0500' && c <= '\u052F')
        || (c >= '\u2DE0' && c <= '\u2DFF') || (c >= '\uA640' && c <= '\uA69F')
        || (c >= '\u1C80' && c <= '\u1C88');

    private static bool IsGreekScript(char c) =>
        (c >= '\u0370' && c <= '\u03FF') || (c >= '\u1F00' && c <= '\u1FFF');

    public static LyricCharType GetCharType(string token)
    {
        if (string.IsNullOrEmpty(token)) return LyricCharType.Whitespace;
        // JS: c.trim() === "" -> Whitespace (trims U+FEFF too, unlike
        // char.IsWhiteSpace — checked explicitly).
        if (token.All(c => char.IsWhiteSpace(c) || c == '\uFEFF'))
            return LyricCharType.Whitespace;
        var first = token[0];
        var category = CharUnicodeInfo.GetUnicodeCategory(first);
        if (IsLatinScript(first)
            || category == UnicodeCategory.NonSpacingMark
            || category == UnicodeCategory.SpacingCombiningMark
            || category == UnicodeCategory.EnclosingMark)
            return LyricCharType.Latin;
        if (IsCyrillicScript(first) || IsGreekScript(first))
        {
            // JS RE_LATIN includes Cyrillic + Greek scripts as Latin.
            return LyricCharType.Latin;
        }
        if (first >= '0' && first <= '9') return LyricCharType.Numeric;
        if ((first >= 0x4E00 && first <= 0x9FFF)
            || (first >= 0x3040 && first <= 0x309F)
            || (first >= 0x30A0 && first <= 0x30FF)
            || (first >= 0xAC00 && first <= 0xD7AF))
            return LyricCharType.Cjk;
        return LyricCharType.Other;
    }

    public static double CalculateWeight(string token, double punctuationWeight) =>
        GetCharType(token) switch
        {
            LyricCharType.Latin => token.Length,
            LyricCharType.Numeric => token.Length,
            LyricCharType.Cjk => token.Length,
            LyricCharType.Other => punctuationWeight,
            _ => 0.0,
        };

    // Exact port of distributeTime: single segment keeps the original word
    // (same id, all fields); zero duration -> clones share the span with
    // fresh ids; zero total weight -> no words at all.
    public static List<EditWord> DistributeTime(
        EditWord original, IReadOnlyList<string> segments, double punctuationWeight)
    {
        if (segments.Count == 0) return new List<EditWord>();
        if (segments.Count == 1)
        {
            var single = original.Clone();
            single.Text = segments[0];
            return new List<EditWord> { single };
        }
        var totalDuration = original.EndTimeMs - original.StartTimeMs;
        if (totalDuration <= 0)
        {
            return segments.Select(token =>
            {
                var w = EditWord.New();
                w.Text = token;
                w.StartTimeMs = original.StartTimeMs;
                w.EndTimeMs = original.EndTimeMs;
                return w;
            }).ToList();
        }
        var weights = segments.Select(s => CalculateWeight(s, punctuationWeight)).ToList();
        var totalWeight = weights.Sum();
        if (totalWeight <= 0) return new List<EditWord>();
        var perWeight = totalDuration / totalWeight;
        var result = new List<EditWord>();
        var cursor = original.StartTimeMs;
        for (var i = 0; i < segments.Count; i++)
        {
            // JS Math.round rounds half UP (.NET default is banker's) —
            // AwayFromZero matches for the non-negative values here.
            var tokenEnd = cursor + (int)Math.Round(weights[i] * perWeight, MidpointRounding.AwayFromZero);
            if (i == segments.Count - 1) tokenEnd = original.EndTimeMs;
            var w = EditWord.New();
            w.Text = segments[i];
            w.StartTimeMs = cursor;
            w.EndTimeMs = tokenEnd;
            result.Add(w);
            if (tokenEnd > cursor) cursor = tokenEnd;
        }
        return result;
    }
}
