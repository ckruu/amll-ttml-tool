using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Lyrics;

// Adapters between the immutable interchange records (Models) and the
// mutable int-ms editing model. Total in both directions for everything
// the records hold; the record Agent (raw ttm:agent) rides the line
// Unmapped carry-bag under key "agent" (extension point for Phase 4
// formats) and round-trips untouched.
// Times: parser doubles are WHOLE ms up to float noise — anything beyond
// 1e-6 off an integer is reported (source + count + locations) instead of
// silently rounded. EndTimeLink has no record counterpart (the original
// never serializes it either) and is dropped on export.

public sealed class LyricAdapterException : Exception
{
    public LyricAdapterException(string message) : base(message)
    {
    }
}

public static class LyricAdapters
{
    private const double WholeMsTolerance = 1e-6;

    public static EditDocument FromRecords(TtmlLyric records, string sourceName)
    {
        var violations = new List<string>();
        var doc = new EditDocument();
        foreach (var meta in records.Metadata)
        {
            doc.Metadata.Add(new EditMetadata
            {
                Key = meta.Key,
                Values = new List<string>(meta.Value),
                Error = meta.Error,
            });
        }
        for (var li = 0; li < records.LyricLines.Count; li++)
        {
            var src = records.LyricLines[li];
            var line = new EditLine
            {
                Id = src.Id,
                TranslatedLyric = src.TranslatedLyric,
                RomanLyric = src.RomanLyric,
                IsBg = src.IsBg,
                IsDuet = src.IsDuet,
                StartTimeMs = WholeMs(src.StartTime, $"line[{li}].startTime", sourceName, violations),
                EndTimeMs = WholeMs(src.EndTime, $"line[{li}].endTime", sourceName, violations),
                IgnoreSync = src.IgnoreSync,
            };
            // Raw agent display hint (null/absent stays out of the bag).
            if (src.Agent is not null) line.Unmapped[EditLine.AgentKey] = src.Agent;
            for (var wi = 0; wi < src.Words.Count; wi++)
            {
                var w = src.Words[wi];
                var word = new EditWord
                {
                    Id = w.Id,
                    StartTimeMs = WholeMs(w.StartTime, $"line[{li}].words[{wi}].startTime", sourceName, violations),
                    EndTimeMs = WholeMs(w.EndTime, $"line[{li}].words[{wi}].endTime", sourceName, violations),
                    Text = w.Word,
                    RomanWord = w.RomanWord,
                    Obscene = w.Obscene,
                    EmptyBeat = WholeCount(w.EmptyBeat, $"line[{li}].words[{wi}].emptyBeat", sourceName, violations),
                    RomanWarning = w.RomanWarning,
                };
                if (w.Ruby is not null)
                {
                    for (var ri = 0; ri < w.Ruby.Count; ri++)
                    {
                        var r = w.Ruby[ri];
                        word.Ruby.Add(new EditRubySegment
                        {
                            Text = r.Word,
                            StartTimeMs = WholeMs(r.StartTime, $"line[{li}].words[{wi}].ruby[{ri}].startTime", sourceName, violations),
                            EndTimeMs = WholeMs(r.EndTime, $"line[{li}].words[{wi}].ruby[{ri}].endTime", sourceName, violations),
                            // Records carry no ruby emptyBeat (the WASM
                            // RubyTag has none either) — always 0 here.
                        });
                    }
                }
                line.Words.Add(word);
            }
            doc.Lines.Add(line);
        }
        if (violations.Count > 0)
        {
            var shown = string.Join("; ", violations.Take(5));
            throw new LyricAdapterException(
                $"{sourceName}: {violations.Count} non-whole-millisecond value(s), first: {shown}");
        }
        return doc;
    }

    public static TtmlLyric ToRecords(EditDocument document) => new(
        document.Metadata.Select(m => new TtmlMetadata(m.Key, new List<string>(m.Values), m.Error)).ToList(),
        document.Lines.Select(l => new LyricLine(
            l.Id,
            l.Words.Select(w => new LyricWord(
                w.Id, w.StartTimeMs, w.EndTimeMs, w.Text, w.RomanWord,
                w.Obscene, w.EmptyBeat, w.RomanWarning,
                w.Ruby.Count == 0 ? null : w.Ruby.Select(r =>
                    // Records (like WASM RubyTag) carry no ruby emptyBeat.
                    new LyricWordBase(r.StartTimeMs, r.EndTimeMs, r.Text)).ToList()
            )).ToList(),
            l.TranslatedLyric, l.RomanLyric, l.IsBg, l.IsDuet,
            l.StartTimeMs, l.EndTimeMs, l.IgnoreSync,
            Agent: l.Unmapped.TryGetValue(EditLine.AgentKey, out var agent) ? agent : null
        )).ToList());

    private static int WholeMs(double value, string where, string source, List<string> violations)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > WholeMsTolerance)
            violations.Add($"{where}={value} in {source}");
        return (int)rounded;
    }

    private static int WholeCount(double value, string where, string source, List<string> violations) =>
        WholeMs(value, where, source, violations);
}
