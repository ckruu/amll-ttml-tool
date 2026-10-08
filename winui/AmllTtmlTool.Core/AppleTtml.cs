using System.Text;
using System.Text.RegularExpressions;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public sealed partial class AppleTtmlOptions
{
    public string? Lang { get; init; }
}

public static partial class AppleTtml
{
    public static string FormatTime(double ms)
    {
        var totalMs = (long)Math.Round(ms, MidpointRounding.AwayFromZero);
        var mmm = ((totalMs % 1000) + 1000) % 1000;
        var totalSec = (long)Math.Floor(totalMs / 1000.0);
        var ss = ((totalSec % 60) + 60) % 60;
        var totalMin = (long)Math.Floor(totalSec / 60.0);
        var mm = ((totalMin % 60) + 60) % 60;
        var hh = totalMin / 60;
        return $"{hh:D2}:{mm:D2}:{ss:D2}.{mmm:D3}";
    }

    public static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static bool IsWhitespaceWord(LyricWord w) =>
        string.IsNullOrWhiteSpace(w.Word);

    private static string ResolveLang(TtmlLyric lyric, AppleTtmlOptions? opts)
    {
        if (!string.IsNullOrWhiteSpace(opts?.Lang)) return opts.Lang.Trim();
        var langMeta = lyric.Metadata.FirstOrDefault(m =>
            m.Key.Equals("language", StringComparison.OrdinalIgnoreCase));
        if (langMeta?.Value.Count > 0 && !string.IsNullOrWhiteSpace(langMeta.Value[0]))
            return langMeta.Value[0].Trim();
        var lang2 = lyric.Metadata.FirstOrDefault(m =>
            m.Key.Equals("lang", StringComparison.OrdinalIgnoreCase));
        if (lang2?.Value.Count > 0 && !string.IsNullOrWhiteSpace(lang2.Value[0]))
            return lang2.Value[0].Trim();
        return "en";
    }

    private sealed record Group(LyricLine? Main, List<LyricLine> BgLines, bool IsEmpty);

    private static bool IsEmptyLine(LyricLine line) =>
        line.Words.Count == 0 || line.Words.All(w => w.Word == string.Empty);

    private static List<Group> GroupLines(List<LyricLine> lines)
    {
        var groups = new List<Group>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.IsBg)
            {
                var batch = new List<LyricLine>();
                while (i < lines.Count && lines[i].IsBg)
                {
                    batch.Add(lines[i]);
                    i++;
                }
                groups.Add(new Group(null, batch, false));
                continue;
            }
            if (IsEmptyLine(line))
            {
                groups.Add(new Group(line, [], true));
                i++;
                continue;
            }
            var following = new List<LyricLine>();
            var j = i + 1;
            while (j < lines.Count && lines[j].IsBg)
            {
                following.Add(lines[j]);
                j++;
            }
            groups.Add(new Group(line, following, false));
            i = j;
        }
        return groups;
    }

    private static List<LyricWord> GetTimingWords(List<LyricWord> words) =>
        words.Where(w => !IsWhitespaceWord(w)).ToList();

    private static string BuildSpansContent(List<LyricWord> words)
    {
        var out_ = new StringBuilder();
        foreach (var w in words)
        {
            if (IsWhitespaceWord(w))
            {
                out_.Append(w.Word);
            }
            else
            {
                out_.Append($"<span begin=\"{FormatTime(w.StartTime)}\" end=\"{FormatTime(w.EndTime)}\">{EscapeXml(w.Word)}</span>");
            }
        }
        return out_.ToString();
    }

    private static string BuildBgInnerContent(List<LyricWord> bgWords)
    {
        var timingIdx = new List<int>();
        for (var i = 0; i < bgWords.Count; i++)
        {
            if (!IsWhitespaceWord(bgWords[i])) timingIdx.Add(i);
        }
        if (timingIdx.Count > 0)
        {
            var firstIdx = timingIdx[0];
            var lastIdx = timingIdx[^1];
            var next = bgWords.ToList();
            if (firstIdx == lastIdx)
            {
                var w = next[firstIdx].Word;
                if (!OpenParen().IsMatch(w)) w = $"({w}";
                if (!CloseParen().IsMatch(w)) w = $"{w})";
                next[firstIdx] = next[firstIdx] with { Word = w };
            }
            else
            {
                if (!OpenParen().IsMatch(next[firstIdx].Word))
                    next[firstIdx] = next[firstIdx] with { Word = $"({next[firstIdx].Word}" };
                if (!CloseParen().IsMatch(next[lastIdx].Word))
                    next[lastIdx] = next[lastIdx] with { Word = $"{next[lastIdx].Word})" };
            }
            return BuildSpansContent(next);
        }
        return BuildSpansContent(bgWords);
    }

    public static List<string> GetAppleTtmlWarnings(TtmlLyric lyric)
    {
        var warnings = new List<string>();
        for (var idx = 0; idx < lyric.LyricLines.Count; idx++)
        {
            var line = lyric.LyricLines[idx];
            if (line.IsBg) continue;
            var nonWs = GetTimingWords(line.Words);
            if (nonWs.Count == 1 && line.Words.Count == 1)
            {
                var w = nonWs[0];
                if (w.StartTime == line.StartTime && w.EndTime == line.EndTime)
                {
                    warnings.Add($"Line {idx + 1} is a single span covering the whole line — Cider cannot animate per syllable");
                }
                else if (line.StartTime == 0 && line.EndTime == 0)
                {
                    warnings.Add($"Line {idx + 1} has only one word — consider splitting for per-syllable animation");
                }
            }
        }
        return warnings;
    }

    public static string ToAppleTtml(TtmlLyric lyric, AppleTtmlOptions? opts = null)
    {
        var lang = ResolveLang(lyric, opts);
        var groups = GroupLines(lyric.LyricLines);

        static string AgentOf(Group g)
        {
            if (g.Main is not null) return g.Main.IsDuet ? "v2" : "v1";
            if (g.BgLines.Count > 0) return g.BgLines[0].IsDuet ? "v2" : "v1";
            return "v1";
        }

        var usedAgents = new HashSet<string>(groups.Select(AgentOf));
        var distinctAgents = new List<string>();
        if (usedAgents.Contains("v1")) distinctAgents.Add("v1");
        if (usedAgents.Contains("v2")) distinctAgents.Add("v2");
        var isDuet = distinctAgents.Count > 1;

        var head = string.Empty;
        if (isDuet)
        {
            var agentsXml = string.Concat(distinctAgents.Select(id => $"<ttm:agent type=\"person\" xml:id=\"{id}\"/>"));
            head = $"<head><metadata>{agentsXml}</metadata></head>";
        }

        var divParts = new List<string>();
        foreach (var g in groups)
        {
            string pBegin;
            string pEnd;
            string inner;

            if (g.IsEmpty && g.Main is not null)
            {
                pBegin = FormatTime(g.Main.StartTime);
                pEnd = FormatTime(g.Main.EndTime);
                inner = string.Empty;
            }
            else if (g.Main is null)
            {
                var flatBg = g.BgLines.SelectMany(l => l.Words).ToList();
                var timingWords = GetTimingWords(flatBg);
                if (timingWords.Count == 0)
                {
                    var first = g.BgLines[0];
                    pBegin = FormatTime(first.StartTime);
                    pEnd = FormatTime(first.EndTime);
                }
                else
                {
                    pBegin = FormatTime(timingWords.Min(w => w.StartTime));
                    pEnd = FormatTime(timingWords.Max(w => w.EndTime));
                }
                var innerBg = BuildBgInnerContent(flatBg);
                inner = $"<span ttm:role=\"x-bg\" begin=\"{pBegin}\" end=\"{pEnd}\">{innerBg}</span>";
            }
            else
            {
                var mainWords = g.Main.Words;
                var flatBg = g.BgLines.SelectMany(l => l.Words).ToList();
                var allTiming = GetTimingWords(mainWords).Concat(GetTimingWords(flatBg)).ToList();
                if (allTiming.Count == 0)
                {
                    pBegin = FormatTime(g.Main.StartTime);
                    pEnd = FormatTime(g.Main.EndTime);
                }
                else
                {
                    pBegin = FormatTime(allTiming.Min(w => w.StartTime));
                    pEnd = FormatTime(allTiming.Max(w => w.EndTime));
                }
                var mainContent = BuildSpansContent(mainWords);
                if (flatBg.Count > 0)
                {
                    if (mainContent != string.Empty)
                    {
                        var trailing = TrailingSpaces().Match(mainContent);
                        if (!trailing.Success)
                        {
                            mainContent += " ";
                        }
                        else if (trailing.Value.Length != 1)
                        {
                            mainContent = TrailingSpaces().Replace(mainContent, " ");
                        }
                    }
                    var innerBg = BuildBgInnerContent(flatBg);
                    inner = mainContent + $"<span ttm:role=\"x-bg\" begin=\"{pBegin}\" end=\"{pEnd}\">{innerBg}</span>";
                }
                else
                {
                    inner = mainContent;
                }
            }

            var agentAttr = isDuet && !g.IsEmpty ? $" ttm:agent=\"{AgentOf(g)}\"" : string.Empty;
            divParts.Add($"<p begin=\"{pBegin}\" end=\"{pEnd}\"{agentAttr}>{inner}</p>");
        }

        var body = $"<body><div>{string.Concat(divParts)}</div></body>";
        var ttAttrs = $"xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" itunes:timing=\"Word\" xml:lang=\"{EscapeXml(lang)}\"";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><tt {ttAttrs}>{head}{body}</tt>";
    }

    [GeneratedRegex("^[\\(\\（]")]
    private static partial Regex OpenParen();

    [GeneratedRegex("[\\)\\）]$")]
    private static partial Regex CloseParen();

    [GeneratedRegex(" +$")]
    private static partial Regex TrailingSpaces();
}
