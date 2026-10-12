using System.Text.RegularExpressions;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public static partial class LrcParser
{
    private sealed record ParsedEvent(double Time, string Text, int Index);

    public static List<LyricLine> ParseLrc(string content)
    {
        var events = new List<ParsedEvent>();
        var globalIndex = 0;
        foreach (var rawLine in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            // JS trim() strips U+FEFF too (.NET Trim does not) — mirror it so
            // a BOM-ish line end filters as empty exactly like the original.
            var text = TimeTag().Replace(rawLine, string.Empty).Trim().Trim('﻿').Trim();
            foreach (Match match in TimeTag().Matches(rawLine))
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success
                    ? double.Parse("0." + match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0.0;
                var totalMs = Math.Round((minutes * 60 + seconds + fraction) * 1000, MidpointRounding.AwayFromZero);
                events.Add(new ParsedEvent(totalMs, text, globalIndex++));
            }
        }

        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Index.CompareTo(b.Index));

        var lines = new List<LyricLine>();
        var i = 0;
        while (i < events.Count)
        {
            var currentTime = events[i].Time;
            var group = new List<ParsedEvent>();
            while (i < events.Count && events[i].Time == currentTime)
            {
                group.Add(events[i]);
                i++;
            }

            var next = i < events.Count ? events[i] : null;
            var endTime = next is not null ? next.Time : currentTime + 10000;
            var textEvents = group.Where(e => e.Text.Length > 0).ToList();
            if (textEvents.Count == 0) continue;

            lines.Add(NewLyricLine(textEvents[0].Text,
                textEvents.Count > 1 ? textEvents[1].Text : string.Empty,
                textEvents.Count > 2 ? textEvents[2].Text : string.Empty,
                currentTime, endTime));

            for (var k = 3; k < textEvents.Count; k++)
                lines.Add(NewLyricLine(textEvents[k].Text, string.Empty, string.Empty, currentTime, endTime));
        }

        return lines;
    }

    private static LyricLine NewLyricLine(string word, string translated, string roman, double start, double end)
    {
        var w = new LyricWord(Guid.NewGuid().ToString("N"), start, end, word, string.Empty, false, 0);
        return new LyricLine(Guid.NewGuid().ToString("N"), [w], translated, roman, false, false, start, end, false);
    }

    // [0-9], not \d: .NET \d matches Unicode digits (int.Parse would then
    // throw where the original's ASCII-only regex simply finds no tag).
    [GeneratedRegex(@"\[([0-9]{1,2}):([0-9]{1,2})(?:[.:]([0-9]{1,3}))?\]")]
    private static partial Regex TimeTag();
}
