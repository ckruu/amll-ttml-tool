using System.Text.RegularExpressions;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public sealed record LegacyDetection(bool IsLegacy, List<string> Reasons);

public static partial class LegacyTtml
{
    public static LegacyDetection DetectLegacyTtml(string text)
    {
        var reasons = new List<string>();
        if (text.Contains("xmlns:amll")) reasons.Add("xmlns:amll namespace");
        if (text.Contains("xmlns:tts")) reasons.Add("xmlns:tts namespace");
        if (text.Contains("amll:meta")) reasons.Add("amll:meta entries");
        if (text.Contains("itunes:key")) reasons.Add("itunes:key attributes");
        if (text.Contains("iTunesMetadata") || text.Contains("songwriters"))
            reasons.Add("iTunesMetadata/songwriters");
        if (BodyDur().IsMatch(text)) reasons.Add("body dur attribute");
        if (DivBeginEnd().IsMatch(text)) reasons.Add("begin/end on <div>");
        var bgWrappers = BgWrapper().Matches(text);
        if (bgWrappers.Count > 0)
        {
            if (bgWrappers.Any(m => !BeginAttr().IsMatch(m.Value) || !EndAttr().IsMatch(m.Value)))
                reasons.Add("untimed x-bg wrapper");
            if (GluedBg().IsMatch(text)) reasons.Add("x-bg glued without preceding space");
        }
        if (ShortTimestamp().IsMatch(text)) reasons.Add("short timestamp format");
        return new LegacyDetection(reasons.Count > 0, reasons);
    }

    public static string? ExtractXmlLang(string text)
    {
        var m = XmlLangDouble().Match(text);
        if (!m.Success) m = XmlLangSingle().Match(text);
        var v = m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string GetLegacyFixNotice(List<string> reasons) =>
        $"Legacy TTML detected ({string.Join(", ", reasons)}). Opening is fine — use Copy/Save Apple TTML to re-encode with fixed attributes for Cider.";

    public static (TtmlLyric Result, List<string> Fixes) RepairLegacyImport(
        TtmlLyric lyric,
        string rawText)
    {
        var fixes = new List<string>();
        var lines = lyric.LyricLines.Select(l => l with
        {
            Words = l.Words.Select(w => w with { }).ToList(),
        }).ToList();
        var metadata = lyric.Metadata.Select(m => m with { Value = m.Value.ToList() }).ToList();
        var result = lyric with { LyricLines = lines, Metadata = metadata };

        // Agent per <p> in document order (null when absent), mirroring the
        // original's per-line agentIds from rawParseTtml. A flat agent-attr
        // regex would SHIFT every later line when one <p> lacks the attr.
        var agentIds = PTag().Matches(rawText).Select(m =>
        {
            var a = AgentAttr().Match(m.Value);
            return a.Success ? a.Groups[1].Value : null;
        }).ToList();
        var pIndex = 0;
        string? currentAgent = agentIds.Count > 0 ? agentIds[0] : null;
        var duetFixed = 0;
        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            if (!line.IsBg)
            {
                currentAgent = pIndex < agentIds.Count ? agentIds[pIndex] : null;
                pIndex++;
            }
            var shouldBeDuet = currentAgent is not null && currentAgent != "v1" && currentAgent != string.Empty;
            if (shouldBeDuet && !line.IsBg && !line.IsDuet)
            {
                lines[li] = line with { IsDuet = true };
                duetFixed++;
            }
        }
        if (duetFixed > 0) fixes.Add($"restored duet flag on {duetFixed} line(s)");

        var parenFixed = 0;
        foreach (var (bgLine, bgIdx) in lines.Select((l, idx) => (l, idx)).ToList())
        {
            if (!bgLine.IsBg) continue;
            var timing = bgLine.Words.Where(w => !string.IsNullOrWhiteSpace(w.Word)).ToList();
            if (timing.Count == 0) continue;
            var first = timing[0];
            var last = timing[^1];
            if (!OpenParen().IsMatch(first.Word))
            {
                ReplaceWord(bgIdx, first, $"({first.Word}");
                parenFixed++;
            }
            if (!CloseParen().IsMatch(last.Word))
            {
                var current = lines[bgIdx].Words.First(w => w.Id == last.Id);
                ReplaceWord(bgIdx, current, $"{current.Word})");
                parenFixed++;
            }
        }
        if (parenFixed > 0) fixes.Add("restored BG parentheses");

        var emptied = 0;
        foreach (var idx in Enumerable.Range(0, lines.Count))
        {
            var line = lines[idx];
            if (line.Words.Count == 0) continue;
            if (line.Words.All(w => w.Word == string.Empty))
            {
                lines[idx] = line with { Words = [] };
                emptied++;
            }
        }
        if (emptied > 0) fixes.Add($"preserved {emptied} empty interlude(s)");

        return (result, fixes);

        void ReplaceWord(int lineIdx, LyricWord target, string word)
        {
            var line = lines[lineIdx];
            lines[lineIdx] = line with
            {
                Words = line.Words.Select(w => w.Id == target.Id ? w with { Word = word } : w).ToList(),
            };
        }
    }

    [GeneratedRegex("<body[^>]*\\bdur\\s*=", RegexOptions.Singleline)]
    private static partial Regex BodyDur();

    [GeneratedRegex("<div[^>]*\\b(begin|end)\\s*=", RegexOptions.Singleline)]
    private static partial Regex DivBeginEnd();

    [GeneratedRegex("<span[^>]*ttm:role\\s*=\\s*[\"']x-bg[\"'][^>]*>")]
    private static partial Regex BgWrapper();

    [GeneratedRegex("\\bbegin\\s*=")]
    private static partial Regex BeginAttr();

    [GeneratedRegex("\\bend\\s*=")]
    private static partial Regex EndAttr();

    [GeneratedRegex("</span><span[^>]*ttm:role\\s*=\\s*[\"']x-bg[\"']")]
    private static partial Regex GluedBg();

    [GeneratedRegex("(begin|end)\\s*=\\s*\"(\\d+\\.\\d+|\\d+:\\d{2}\\.\\d+)\"")]
    private static partial Regex ShortTimestamp();

    [GeneratedRegex("<tt\\b[^>]*\\bxml:lang\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex XmlLangDouble();

    [GeneratedRegex("<tt\\b[^>]*\\bxml:lang\\s*=\\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex XmlLangSingle();

    [GeneratedRegex("^[\\(\\（]")]
    private static partial Regex OpenParen();

    [GeneratedRegex("[\\)\\）]$")]
    private static partial Regex CloseParen();

    [GeneratedRegex("ttm:agent\\s*=\\s*[\"']([^\"']+)[\"']")]
    private static partial Regex AgentAttr();

    [GeneratedRegex("<p\\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex PTag();
}
