using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public static class TtmlGenerator
{
    private const string TtmlNs = "http://www.w3.org/ns/ttml";
    private const string ItunesNs = "http://music.apple.com/lyric-ttml-internal";
    private const string TtmNs = "http://www.w3.org/ns/ttml#metadata";
    private const string TtsNs = "http://www.w3.org/ns/ttml#styling";
    private const string AmllNs = "http://www.example.com/ns/amll";

    public static Result<string> Generate(
        TtmlLyric lyric,
        TtmlGenerateOptions? options = null,
        GeneratorConfig? config = null)
    {
        options ??= new TtmlGenerateOptions();
        config ??= new GeneratorConfig();
        var normalized = TtmlNormalize.PostProcess(TtmlNormalize.NormalizeForExport(lyric));

        var language = normalized.Metadata
            .FirstOrDefault(m => m.Key.Equals("language", StringComparison.OrdinalIgnoreCase))
            ?.Value.FirstOrDefault();
        var timingMode = normalized.Metadata
            .FirstOrDefault(m => m.Key == "timingMode")
            ?.Value.FirstOrDefault();

        var groups = GroupLines(normalized.LyricLines);
        var blocks = new List<Block>();
        var keyCounter = 0;
        foreach (var g in groups)
        {
            if (g.Main is not null)
            {
                keyCounter++;
                blocks.Add(new MainBlock($"L{keyCounter}", g.Main, g.BgLines));
            }
            else if (g.BgLines.Count > 0)
            {
                keyCounter++;
                blocks.Add(new DirectBgBlock($"L{keyCounter}", g.BgLines));
            }
        }

        var anyV2 = blocks.Any(b => b.AgentIsV2);
        var translations = new List<(string Lang, string Key, string Text)>();
        var transliterations = new List<(string Key, List<(double Begin, double End, string Text)> Spans)>();
        foreach (var b in blocks)
        {
            var line = b.TranslationSource;
            if (line is not null && !string.IsNullOrEmpty(line.TranslatedLyric) && config.UseAppleFormatRules)
                translations.Add((options.TranslationLanguage ?? "zh-Hans", b.Key, line.TranslatedLyric));
            var romanSpans = b.Lines
                .SelectMany(l => l.Words)
                .Where(w => !string.IsNullOrWhiteSpace(w.RomanWord))
                .Select(w => (w.StartTime, w.EndTime, w.RomanWord))
                .ToList();
            if (romanSpans.Count > 0)
                transliterations.Add((b.Key, romanSpans));
        }
        var songwriters = normalized.Metadata
            .Where(m => m.Key == "songwriters")
            .SelectMany(m => m.Value)
            .ToList();

        var w = new Writer(config.Format);
        var ttAttrs = $"xmlns=\"{TtmlNs}\" xmlns:itunes=\"{ItunesNs}\" xmlns:ttm=\"{TtmNs}\" xmlns:tts=\"{TtsNs}\" xmlns:amll=\"{AmllNs}\"";
        if (config.LineTiming) ttAttrs += " itunes:timing=\"Line\"";
        else if (!string.IsNullOrEmpty(timingMode)) ttAttrs += $" itunes:timing=\"{Escape(timingMode!)}\"";
        if (!string.IsNullOrEmpty(language)) ttAttrs += $" xml:lang=\"{Escape(language!)}\"";
        w.Open($"<tt {ttAttrs}>", 0);
        w.Open("<head>", 1);
        w.Open("<metadata>", 2);
        w.Element("<ttm:agent type=\"person\" xml:id=\"v1\"/>", 3);
        if (anyV2) w.Element("<ttm:agent type=\"person\" xml:id=\"v2\"/>", 3);
        foreach (var meta in normalized.Metadata)
        {
            if (meta.Key is "language" or "timingMode" or "songwriters") continue;
            if (meta.Key == "title")
            {
                foreach (var v in meta.Value) w.Element($"<amll:meta key=\"musicName\" value=\"{Escape(v)}\"/>", 3);
                continue;
            }
            foreach (var v in meta.Value) w.Element($"<amll:meta key=\"{Escape(meta.Key)}\" value=\"{Escape(v)}\"/>", 3);
        }
        if (translations.Count > 0 || transliterations.Count > 0 || songwriters.Count > 0)
        {
            w.Open($"<iTunesMetadata xmlns=\"{ItunesNs}\">", 3);
            if (translations.Count > 0)
            {
                w.Open("<translations>", 4);
                foreach (var (lang, key, text) in translations)
                {
                    w.Open($"<translation xml:lang=\"{Escape(lang)}\">", 5);
                    w.Element($"<text for=\"{key}\">{Escape(text)}</text>", 6);
                    w.Close("</translation>", 5);
                }
                w.Close("</translations>", 4);
            }
            if (transliterations.Count > 0)
            {
                w.Open("<transliterations>", 4);
                var romanLang = options.RomanizationLanguage;
                foreach (var (key, spans) in transliterations)
                {
                    var langAttr = string.IsNullOrEmpty(romanLang) ? string.Empty : $" xml:lang=\"{Escape(romanLang!)}\"";
                    w.Open($"<transliteration{langAttr}>", 5);
                    w.Open($"<text for=\"{key}\">", 6);
                    foreach (var (b, e, t) in spans)
                        w.Element($"<span begin=\"{Fmt(b)}\" end=\"{Fmt(e)}\">{Escape(t)}</span>", 7);
                    w.Close("</text>", 6);
                    w.Close("</transliteration>", 5);
                }
                w.Close("</transliterations>", 4);
            }
            if (songwriters.Count > 0)
            {
                w.Open("<songwriters>", 4);
                foreach (var s in songwriters) w.Element($"<songwriter>{Escape(s)}</songwriter>", 5);
                w.Close("</songwriters>", 4);
            }
            w.Close("</iTunesMetadata>", 3);
        }
        w.Close("</metadata>", 2);
        w.Close("</head>", 1);

        if (blocks.Count == 0)
        {
            w.Break(1);
            w.Inline("<body></body>");
        }
        else
        {
            var divBegin = normalized.LyricLines.Min(l => l.StartTime);
            var divEnd = normalized.LyricLines.Max(l => l.EndTime);
            var dur = normalized.LyricLines.Max(l => l.EndTime);
            w.Open($"<body dur=\"{Fmt(dur)}\">", 1);
            w.Open($"<div begin=\"{Fmt(divBegin)}\" end=\"{Fmt(divEnd)}\">", 2);
            foreach (var b in blocks) WriteBlock(w, b, options, config);
            w.Close("</div>", 2);
            w.Close("</body>", 1);
        }
        w.Close("</tt>", 0);
        return Result<string>.Ok(w.ToString());
    }

    private sealed record Group(LyricLine? Main, List<LyricLine> BgLines);

    private abstract record Block(string Key)
    {
        public abstract List<LyricLine> Lines { get; }
        public abstract bool AgentIsV2 { get; }
        public abstract LyricLine? TranslationSource { get; }
    }

    private sealed record MainBlock(string Key, LyricLine MainLine, List<LyricLine> BgLines) : Block(Key)
    {
        public override List<LyricLine> Lines => [MainLine];
        public override bool AgentIsV2 => MainLine.IsDuet;
        public override LyricLine? TranslationSource => MainLine;
    }

    private sealed record DirectBgBlock(string Key, List<LyricLine> Batch) : Block(Key)
    {
        public override List<LyricLine> Lines => Batch;
        public override bool AgentIsV2 => Batch.Any(l => l.IsDuet);
        public override LyricLine? TranslationSource => Batch.Count > 0 ? Batch[0] : null;
    }

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
                groups.Add(new Group(null, batch));
                continue;
            }
            var following = new List<LyricLine>();
            var j = i + 1;
            while (j < lines.Count && lines[j].IsBg)
            {
                following.Add(lines[j]);
                j++;
            }
            groups.Add(new Group(line, following));
            i = j;
        }
        return groups;
    }

    private static void WriteBlock(Writer w, Block block, TtmlGenerateOptions options, GeneratorConfig config)
    {
        if (block is DirectBgBlock direct)
        {
            var first = direct.Batch.First();
            var last = direct.Batch.Last();
            var agent = direct.AgentIsV2 ? "v2" : "v1";
            w.Open($"<p begin=\"{Fmt(first.StartTime)}\" end=\"{Fmt(last.EndTime)}\" itunes:key=\"{block.Key}\" ttm:agent=\"{agent}\">", 3);
            foreach (var bgLine in direct.Batch)
                foreach (var word in bgLine.Words)
                    WriteWord(w, word, 4);
            WriteTranslationAndRoman(w, direct.TranslationSource, options, config);
            w.Close("</p>", 3);
            return;
        }

        var main = (MainBlock)block;
        var line = main.MainLine;
        var mainAgent = line.IsDuet ? "v2" : "v1";
        var openTag = $"<p begin=\"{Fmt(line.StartTime)}\" end=\"{Fmt(line.EndTime)}\" itunes:key=\"{block.Key}\" ttm:agent=\"{mainAgent}\">";
        var hasMainContent = config.LineTiming
            ? line.Words.Count > 0
            : line.Words.Any(x => !string.IsNullOrWhiteSpace(x.Word));
        var hasTranslation = !string.IsNullOrEmpty(line.TranslatedLyric) && !config.UseAppleFormatRules;
        var hasRoman = !string.IsNullOrEmpty(line.RomanLyric)
            && !config.UseAppleFormatRules
            && !line.Words.Any(x => !string.IsNullOrWhiteSpace(x.RomanWord));
        if (!hasMainContent && !hasTranslation && !hasRoman && main.BgLines.Count == 0)
        {
            w.Break(3);
            w.Inline(openTag + "</p>");
            return;
        }
        w.Open(openTag, 3);
        if (config.LineTiming)
        {
            if (line.Words.Count > 0)
                w.Text(string.Concat(line.Words.Select(x => Escape(x.Word))), 4);
        }
        else
        {
            foreach (var word in line.Words)
                WriteWord(w, word, 4);
        }
        WriteTranslationAndRoman(w, line, options, config);
        if (main.BgLines.Count > 0)
        {
            var flat = main.BgLines.SelectMany(l => l.Words).ToList();
            var (begin, end) = BgBounds(flat, line.StartTime, line.EndTime);
            WriteBgWrapper(w, flat, begin, end, 4);
        }
        w.Close("</p>", 3);
    }

    private static void WriteTranslationAndRoman(Writer w, LyricLine? line, TtmlGenerateOptions options, GeneratorConfig config)
    {
        if (line is null) return;
        if (!string.IsNullOrEmpty(line.TranslatedLyric) && !config.UseAppleFormatRules)
            w.Element($"<span ttm:role=\"x-translation\" xml:lang=\"{Escape(options.TranslationLanguage ?? "zh-Hans")}\">{Escape(line.TranslatedLyric)}</span>", 4);
        if (!string.IsNullOrEmpty(line.RomanLyric)
            && !config.UseAppleFormatRules
            && !line.Words.Any(x => !string.IsNullOrWhiteSpace(x.RomanWord)))
        {
            var langAttr = string.IsNullOrEmpty(options.RomanizationLanguage)
                ? string.Empty
                : $" xml:lang=\"{Escape(options.RomanizationLanguage!)}\"";
            w.Element($"<span ttm:role=\"x-roman\"{langAttr}>{Escape(line.RomanLyric)}</span>", 4);
        }
    }

    private static (string Begin, string End) BgBounds(List<LyricWord> flat, double fallbackBegin, double fallbackEnd)
    {
        var timing = flat.Where(x => !string.IsNullOrWhiteSpace(x.Word)).ToList();
        if (timing.Count > 0)
            return (Fmt(timing.Min(x => x.StartTime)), Fmt(timing.Max(x => x.EndTime)));
        if (flat.Count > 0)
            return (Fmt(flat.First().StartTime), Fmt(flat.Last().EndTime));
        return (Fmt(fallbackBegin), Fmt(fallbackEnd));
    }

    private static void WriteWord(Writer w, LyricWord word, int level)
    {
        if (string.IsNullOrWhiteSpace(word.Word))
        {
            w.Inline(word.Word);
            return;
        }
        if (word.Ruby is { Count: > 0 })
        {
            w.Open("<span tts:ruby=\"container\">", level);
            w.Element($"<span tts:ruby=\"base\">{Escape(word.Word)}</span>", level + 1);
            w.Open("<span tts:ruby=\"textContainer\">", level + 1);
            foreach (var r in word.Ruby)
                w.Element($"<span tts:ruby=\"text\" begin=\"{Fmt(r.StartTime)}\" end=\"{Fmt(r.EndTime)}\">{Escape(r.Word)}</span>", level + 2);
            w.Close("</span>", level + 1);
            w.Close("</span>", level);
            return;
        }
        var attrs = string.Empty;
        if (word.Obscene) attrs += " amll:obscene=\"true\"";
        if (word.EmptyBeat != 0) attrs += $" amll:empty-beat=\"{FormatNumber(word.EmptyBeat)}\"";
        w.Element($"<span begin=\"{Fmt(word.StartTime)}\" end=\"{Fmt(word.EndTime)}\"{attrs}>{Escape(word.Word)}</span>", level);
    }

    private static void WriteBgWrapper(Writer w, List<LyricWord> words, string begin, string end, int level)
    {
        var timingIdx = new List<int>();
        for (var i = 0; i < words.Count; i++)
            if (!string.IsNullOrWhiteSpace(words[i].Word)) timingIdx.Add(i);
        var next = words.ToList();
        if (timingIdx.Count > 0)
        {
            var first = timingIdx[0];
            var last = timingIdx[^1];
            if (first == last)
            {
                var word = next[first].Word;
                if (!StartsWithOpenParen(word)) word = "(" + word;
                if (!EndsWithCloseParen(word)) word += ")";
                next[first] = next[first] with { Word = word };
            }
            else
            {
                if (!StartsWithOpenParen(next[first].Word))
                    next[first] = next[first] with { Word = "(" + next[first].Word };
                if (!EndsWithCloseParen(next[last].Word))
                    next[last] = next[last] with { Word = next[last].Word + ")" };
            }
        }
        w.Open($"<span ttm:role=\"x-bg\" begin=\"{begin}\" end=\"{end}\">", level);
        foreach (var word in next) WriteWord(w, word, level + 1);
        w.Close("</span>", level);
    }

    private static bool StartsWithOpenParen(string s) =>
        s.StartsWith('(') || s.StartsWith('（');

    private static bool EndsWithCloseParen(string s) =>
        s.EndsWith(')') || s.EndsWith('）');

    public static string Fmt(double ms)
    {
        var totalMs = (long)Math.Round(ms, MidpointRounding.AwayFromZero);
        var mmm = ((totalMs % 1000) + 1000) % 1000;
        var totalSec = (long)Math.Floor(totalMs / 1000.0);
        var ss = ((totalSec % 60) + 60) % 60;
        var totalMin = (long)Math.Floor(totalSec / 60.0);
        if (totalMin == 0) return $"{totalSec}.{(int)mmm:D3}";
        var mm = ((totalMin % 60) + 60) % 60;
        var hh = (long)Math.Floor(totalMin / 60.0);
        if (hh == 0) return $"{totalMin}:{(int)ss:D2}.{(int)mmm:D3}";
        return $"{hh}:{(int)mm:D2}:{(int)ss:D2}.{(int)mmm:D3}";
    }

    public static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string FormatNumber(double v) =>
        v % 1 == 0 ? ((long)v).ToString() : v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Writer(bool pretty)
    {
        private readonly System.Text.StringBuilder _sb = new();
        private bool _lastWasElement;
        private static string Indent(int level) => new(' ', level * 4);

        public void Break(int level)
        {
            if (pretty && level > 0) _sb.Append('\n').Append(Indent(level));
        }

        public void Open(string tag, int level)
        {
            Break(level);
            _sb.Append(tag);
            _lastWasElement = true;
        }

        public void Close(string tag, int level)
        {
            if (pretty && _lastWasElement) _sb.Append('\n').Append(Indent(level));
            _sb.Append(tag);
            _lastWasElement = true;
        }

        public void Element(string xml, int level)
        {
            if (_lastWasElement) Break(level);
            _sb.Append(xml);
            _lastWasElement = true;
        }

        public void Inline(string xml)
        {
            _sb.Append(xml);
            _lastWasElement = false;
        }

        public void Text(string text, int level)
        {
            Break(level);
            _sb.Append(text);
            _lastWasElement = false;
        }
        public override string ToString() => _sb.ToString();
    }
}
