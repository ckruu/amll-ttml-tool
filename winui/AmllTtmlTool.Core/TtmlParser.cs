using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public static partial class TtmlParser
{
    private static readonly HashSet<string> PlatformIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ncmMusicId", "qqMusicId", "spotifyId", "appleMusicId",
    };

    public static Result<TtmlLyric> Parse(string xml, TtmlParseOptions? options = null)
    {
        options ??= new TtmlParseOptions();
        // The original accepts a leading BOM (verified WASM behavior); strip
        // it before anything else (deliberate: no other normalization).
        xml = xml.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains('<'))
        {
            return Result<TtmlLyric>.Ok(new TtmlLyric([], []));
        }
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            if (OpenTagsOnly().IsMatch(xml))
            {
                return Result<TtmlLyric>.Ok(new TtmlLyric([], []));
            }
            var offset = ByteOffsetFor(xml, ex.LineNumber, ex.LinePosition);
            return Result<TtmlLyric>.Fail(new TtmlError(
                "XmlError",
                $"XML parsing error: {ex.Message} (at byte offset {offset})",
                offset,
                null,
                []));
        }

        var cursor = new Cursor(xml);
        var root = doc.Root;
        if (root is null || !Is("tt", root))
        {
            return Result<TtmlLyric>.Ok(new TtmlLyric([], []));
        }

        var lang = Attr(root, "lang")?.Trim();
        string? title = null;
        var artists = new List<string>();
        string? album = null;
        var isrc = new List<string>();
        var platformIds = new List<TtmlMetadata>();
        var songwriters = new List<string>();
        var rawRest = new List<TtmlMetadata>();
        var lineTranslations = new Dictionary<string, List<(string? Lang, string Text)>>();
        var lineRomans = new Dictionary<string, List<(double Begin, double End, string Text)>>();

        var head = root.Elements().FirstOrDefault(e => Is("head", e));
        var metadataEl = head?.Elements().FirstOrDefault(e => Is("metadata", e));
        if (metadataEl is not null)
        {
            foreach (var child in metadataEl.Elements())
            {
                if (Is("meta", child))
                {
                    var key = child.Attribute("key")?.Value ?? string.Empty;
                    var value = child.Attribute("value")?.Value ?? string.Empty;
                    switch (key)
                    {
                        case "musicName": title = value; break;
                        case "artists": artists.Add(value); break;
                        case "album": album = value; break;
                        case "isrc": isrc.Add(value); break;
                        case "songwriters": songwriters.Add(value); break;
                        case "language":
                        case "timingMode": break;
                        default:
                            if (PlatformIdKeys.Contains(key)) platformIds.Add(new TtmlMetadata(key, [value]));
                            else rawRest.Add(new TtmlMetadata(key, [value]));
                            break;
                    }
                }
            }
            var itunesMeta = metadataEl.Elements().FirstOrDefault(e => Is("iTunesMetadata", e));
            if (itunesMeta is not null)
            {
                var sw = itunesMeta.Elements().FirstOrDefault(e => Is("songwriters", e));
                if (sw is not null)
                {
                    foreach (var w in sw.Elements().Where(e => Is("songwriter", e)))
                        songwriters.Add(w.Value);
                }
                var translationsEl = itunesMeta.Elements().FirstOrDefault(e => Is("translations", e));
                if (translationsEl is not null)
                {
                    foreach (var tr in translationsEl.Elements().Where(e => Is("translation", e)))
                    {
                        var trLang = Attr(tr, "lang");
                        foreach (var text in tr.Elements().Where(e => Is("text", e)))
                        {
                            var forKey = text.Attribute("for")?.Value ?? string.Empty;
                            if (!lineTranslations.TryGetValue(forKey, out var list))
                            {
                                list = [];
                                lineTranslations[forKey] = list;
                            }
                            list.Add((trLang, text.Value));
                        }
                    }
                }
                // Per-word romanization: <transliteration><text for="L1">
                // spans attach to the word in the keyed line whose span
                // matches exactly (verified WASM behavior; first match wins,
                // non-matching spans and unknown keys are dropped).
                var transliterationsEl = itunesMeta.Elements().FirstOrDefault(e => Is("transliterations", e));
                if (transliterationsEl is not null)
                {
                    foreach (var tr in transliterationsEl.Elements().Where(e => Is("transliteration", e)))
                    {
                        foreach (var text in tr.Elements().Where(e => Is("text", e)))
                        {
                            var forKey = text.Attribute("for")?.Value ?? string.Empty;
                            if (!lineRomans.TryGetValue(forKey, out var list))
                            {
                                list = [];
                                lineRomans[forKey] = list;
                            }
                            foreach (var span in text.Elements().Where(e => Is("span", e)))
                            {
                                var bAttr = Attr(span, "begin");
                                var eAttr = Attr(span, "end");
                                if (bAttr is null || eAttr is null) continue;
                                if (!TryParseTimestamp(bAttr, out var b)) continue;
                                if (!TryParseTimestamp(eAttr, out var e)) continue;
                                list.Add((b, e, span.Value));
                            }
                        }
                    }
                }
            }
        }

        var lines = new List<LyricLine>();
        var body = root.Elements().FirstOrDefault(e => Is("body", e));
        // Duet baseline (verified WASM behavior): a line is duet iff its
        // agent differs from the FIRST <p>'s agent in document order —
        // not "== v2". A MISSING agent defaults to "v1" (an explicitly
        // empty agent stays distinct). Missing == missing, "" == "".
        string? baselineAgent = null;
        var baselineFound = false;
        if (body is not null)
        {
            foreach (var div in body.Elements().Where(e => Is("div", e)))
            {
                foreach (var p in div.Elements().Where(e => Is("p", e)))
                {
                    baselineAgent = Attr(p, "agent") ?? "v1";
                    baselineFound = true;
                    break;
                }
                if (baselineFound) break;
            }
        }
        if (body is not null)
        {
            foreach (var div in body.Elements().Where(e => Is("div", e)))
            {
                foreach (var p in div.Elements().Where(e => Is("p", e)))
                {
                    var parsed = ParseParagraph(p, options, lineTranslations, lineRomans, baselineAgent, cursor);
                    if (!parsed.Success) return Result<TtmlLyric>.Fail(parsed.Error!);
                    lines.AddRange(parsed.Data!);
                }
            }
        }

        var metas = new List<TtmlMetadata>();
        if (title is not null) metas.Add(new TtmlMetadata("title", [title]));
        if (artists.Count > 0) metas.Add(new TtmlMetadata("artists", artists));
        if (album is not null) metas.Add(new TtmlMetadata("album", [album]));
        if (isrc.Count > 0) metas.Add(new TtmlMetadata("isrc", isrc));
        if (!string.IsNullOrWhiteSpace(lang)) metas.Add(new TtmlMetadata("language", [lang!.Trim()]));
        metas.AddRange(platformIds);
        if (songwriters.Count > 0) metas.Add(new TtmlMetadata("songwriters", songwriters));
        metas.AddRange(rawRest);

        var lyric = new TtmlLyric(metas, lines);
        return Result<TtmlLyric>.Ok(TtmlNormalize.ApplyImportRenames(lyric));
    }

    private static Result<List<LyricLine>> ParseParagraph(
        XElement p,
        TtmlParseOptions options,
        Dictionary<string, List<(string? Lang, string Text)>> lineTranslations,
        Dictionary<string, List<(double Begin, double End, string Text)>> lineRomans,
        string? baselineAgent,
        Cursor cursor)
    {
        var stack = new List<string> { "tt", "body", "div", "p" };
        var pOffset = cursor.Take("p");
        var beginAttr = Attr(p, "begin");
        if (beginAttr is null)
            return Fail<List<LyricLine>>("MissingAttribute", "Missing required attribute: begin", pOffset, stack);
        var endAttr = Attr(p, "end");
        if (endAttr is null)
            return Fail<List<LyricLine>>("MissingAttribute", "Missing required attribute: end", pOffset, stack);
        if (!TryParseTimestamp(beginAttr, out var pBegin))
            return Fail<List<LyricLine>>("InvalidTimestamp", $"Invalid timestamp format: {beginAttr}", pOffset, stack, "begin", beginAttr);
        if (!TryParseTimestamp(endAttr, out var pEnd))
            return Fail<List<LyricLine>>("InvalidTimestamp", $"Invalid timestamp format: {endAttr}", pOffset, stack, "end", endAttr);

        var agent = Attr(p, "agent") ?? "v1";
        var pDuet = agent != baselineAgent;
        var key = Attr(p, "key");

        var words = new List<LyricWord>();
        var translations = new List<(string? Lang, string Text)>();
        string? roman = null;
        var bgGroups = new List<(XElement Wrapper, string? Agent)>();
        var lastWasTimedWord = false;
        var hasTimedWord = false;
        var pendingSpace = false;

        foreach (var node in p.Nodes())
        {
            if (node is XText text)
            {
                if (text.Value.Contains('\n')) pendingSpace = false;
                else if (lastWasTimedWord && words.Count > 0) pendingSpace = true;
                continue;
            }
            if (node is not XElement el || !Is("span", el))
            {
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            var role = Attr(el, "role");
            if (role == "x-bg")
            {
                bgGroups.Add((el, Attr(el, "agent")));
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            if (role == "x-translation")
            {
                translations.Add((Attr(el, "lang"), el.Value));
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            if (role == "x-roman")
            {
                roman ??= el.Value;
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            if (Attr(el, "ruby") == "container")
            {
                var ruby = ParseRuby(el, pBegin, pEnd);
                if (!ruby.Success) return Result<List<LyricLine>>.Fail(ruby.Error!);
                if (pendingSpace && words.Count > 0)
                    words[^1] = words[^1] with { Word = words[^1].Word + " " };
                pendingSpace = false;
                lastWasTimedWord = ruby.Success && ruby.Data is not null;
                if (ruby.Data is not null)
                {
                    words.Add(ruby.Data);
                    hasTimedWord = true;
                }
                continue;
            }
            var w = ParseWordSpan(el, cursor, stack);
            if (!w.Success) return Result<List<LyricLine>>.Fail(w.Error!);
            if (pendingSpace && words.Count > 0)
                words[^1] = words[^1] with { Word = words[^1].Word + " " };
            pendingSpace = false;
            words.Add(w.Data!);
            hasTimedWord = true;
            lastWasTimedWord = true;
        }

        if (key is not null && lineTranslations.TryGetValue(key, out var metaTrs) && metaTrs.Count > 0)
        {
            var chosen = SelectByLang(metaTrs, options.TranslationLanguage) ?? metaTrs[0].Text;
            translations.Insert(0, (options.TranslationLanguage, chosen));
        }
        var translated = SelectByLang(translations, options.TranslationLanguage)
            ?? (translations.Count > 0 ? translations[0].Text : string.Empty);
        roman ??= string.Empty;

        var result = new List<LyricLine>();
        // A main line ALWAYS carries at least one word: timed words when
        // present, else the direct text (trimmed, or "" when blank) as one
        // [pBegin, pEnd] word — verified WASM behavior for empty, x-bg-only,
        // translation-only, and mixed text+BG paragraphs.
        List<LyricWord> mainWords;
        if (hasTimedWord)
        {
            mainWords = words;
        }
        else
        {
            var direct = string.Concat(p.Nodes().OfType<XText>().Select(t => t.Value));
            var text = string.IsNullOrWhiteSpace(direct) ? string.Empty : direct.Trim();
            mainWords = [NewWord(pBegin, pEnd, text)];
        }
        AttachRoman(key, mainWords, lineRomans);

        result.Add(NewLine(pBegin, pEnd, false, pDuet, mainWords, translated, roman));

        foreach (var (wrapper, wAgent) in bgGroups)
        {
            var bg = ParseBgWrapper(wrapper, pBegin, pEnd, wAgent ?? agent, baselineAgent, cursor, stack);
            if (!bg.Success) return Result<List<LyricLine>>.Fail(bg.Error!);
            result.Add(bg.Data!);
        }

        return Result<List<LyricLine>>.Ok(result);
    }

    private static void AttachRoman(
        string? key,
        List<LyricWord> words,
        Dictionary<string, List<(double Begin, double End, string Text)>> lineRomans)
    {
        if (key is null || !lineRomans.TryGetValue(key, out var spans)) return;
        var unused = spans.ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var at = unused.FindIndex(s => s.Begin == words[i].StartTime && s.End == words[i].EndTime);
            if (at < 0) continue;
            words[i] = words[i] with { RomanWord = unused[at].Text };
            unused.RemoveAt(at);
        }
    }

    // BG paren convention: the parser strips exactly one paren layer per end
    // (ASCII or fullwidth, each end independent; main lines untouched) —
    // verified WASM behavior. The generator and legacy repair re-add them.
    private static string StripBgParens(string s)
    {
        if (s.Length > 0 && (s[0] == '(' || s[0] == '（')) s = s[1..];
        if (s.Length > 0 && (s[^1] == ')' || s[^1] == '）')) s = s[..^1];
        return s;
    }
    private static string? SelectByLang(List<(string? Lang, string Text)> items, string? want)
    {
        if (!string.IsNullOrEmpty(want))
        {
            var hit = items.FirstOrDefault(t => string.Equals(t.Lang, want, StringComparison.OrdinalIgnoreCase));
            if (hit.Text is not null) return hit.Text;
        }
        return null;
    }

    private static Result<LyricWord> ParseWordSpan(XElement span, Cursor cursor, List<string> stack)
    {
        var offset = cursor.Take("span");
        var withStack = new List<string>(stack) { "span" };
        var beginAttr = Attr(span, "begin");
        if (beginAttr is null)
            return Fail<LyricWord>("MissingAttribute", "Missing required attribute: begin", offset, withStack);
        var endAttr = Attr(span, "end");
        if (endAttr is null)
            return Fail<LyricWord>("MissingAttribute", "Missing required attribute: end", offset, withStack);
        if (!TryParseTimestamp(beginAttr, out var begin))
            return Fail<LyricWord>("InvalidTimestamp", $"Invalid timestamp format: {beginAttr}", offset, withStack, "begin", beginAttr);
        if (!TryParseTimestamp(endAttr, out var end))
            return Fail<LyricWord>("InvalidTimestamp", $"Invalid timestamp format: {endAttr}", offset, withStack, "end", endAttr);
        var obscene = string.Equals(Attr(span, "obscene"), "true", StringComparison.OrdinalIgnoreCase);
        var emptyBeat = 0.0;
        var eb = Attr(span, "empty-beat");
        if (eb is not null)
            double.TryParse(eb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out emptyBeat);
        return Result<LyricWord>.Ok(NewWord(begin, end, span.Value, obscene, emptyBeat));
    }

    private static Result<LyricWord?> ParseRuby(XElement container, double pBegin, double pEnd)
    {
        var baseText = string.Empty;
        var rubies = new List<LyricWordBase>();
        foreach (var child in container.Descendants())
        {
            if (!Is("span", child)) continue;
            if (Attr(child, "ruby") == "base" && baseText == string.Empty)
            {
                baseText = child.Value;
            }
            else if (Attr(child, "ruby") == "text")
            {
                var beginAttr = Attr(child, "begin");
                var endAttr = Attr(child, "end");
                if (beginAttr is null || endAttr is null) continue;
                if (!TryParseTimestamp(beginAttr, out var b) || !TryParseTimestamp(endAttr, out var e)) continue;
                rubies.Add(new LyricWordBase(b, e, child.Value));
            }
        }
        if (baseText == string.Empty && rubies.Count == 0) return Result<LyricWord?>.Ok(null);
        var start = rubies.Count > 0 ? rubies.Min(r => r.StartTime) : pBegin;
        var end = rubies.Count > 0 ? rubies.Max(r => r.EndTime) : pEnd;
        return Result<LyricWord?>.Ok(NewWord(start, end, baseText) with { Ruby = rubies });
    }

    private static Result<LyricLine> ParseBgWrapper(
        XElement wrapper, double pBegin, double pEnd, string? agent, string? baselineAgent, Cursor cursor, List<string> stack)
    {
        var wBeginAttr = Attr(wrapper, "begin");
        var wEndAttr = Attr(wrapper, "end");
        double? wBegin = wBeginAttr is not null && TryParseTimestamp(wBeginAttr, out var wb) ? wb : null;
        double? wEnd = wEndAttr is not null && TryParseTimestamp(wEndAttr, out var we) ? we : null;

        var words = new List<LyricWord>();
        var lastWasTimedWord = false;
        var pendingSpace = false;
        foreach (var node in wrapper.Nodes())
        {
            if (node is XText text)
            {
                if (text.Value.Contains('\n')) pendingSpace = false;
                else if (lastWasTimedWord && words.Count > 0) pendingSpace = true;
                continue;
            }
            if (node is not XElement el || !Is("span", el))
            {
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            var role = Attr(el, "role");
            if (role is "x-translation" or "x-roman")
            {
                lastWasTimedWord = false;
                pendingSpace = false;
                continue;
            }
            if (Attr(el, "ruby") == "container")
            {
                var ruby = ParseRuby(el, pBegin, pEnd);
                if (!ruby.Success) return Result<LyricLine>.Fail(ruby.Error!);
                if (pendingSpace && words.Count > 0)
                    words[^1] = words[^1] with { Word = words[^1].Word + " " };
                pendingSpace = false;
                lastWasTimedWord = ruby.Success && ruby.Data is not null;
                if (ruby.Data is not null) words.Add(ruby.Data);
                continue;
            }
            if (pendingSpace && words.Count > 0)
                words[^1] = words[^1] with { Word = words[^1].Word + " " };
            pendingSpace = false;
            var bAttr = Attr(el, "begin");
            var eAttr = Attr(el, "end");
            var b = bAttr is not null && TryParseTimestamp(bAttr, out var tb) ? tb : 0.0;
            var e = eAttr is not null && TryParseTimestamp(eAttr, out var te) ? te : 0.0;
            var obscene = string.Equals(Attr(el, "obscene"), "true", StringComparison.OrdinalIgnoreCase);
            words.Add(NewWord(b, e, StripBgParens(el.Value), obscene));
            lastWasTimedWord = true;
        }

        var timing = words.Where(w => !string.IsNullOrWhiteSpace(w.Word)).ToList();
        var begin = wBegin ?? (timing.Count > 0 ? timing.Min(w => w.StartTime) : pBegin);
        var end = wEnd ?? (timing.Count > 0 ? timing.Max(w => w.EndTime) : pEnd);

        if (timing.Count > 0)
        {
            var first = timing[0];
            var last = timing[^1];
            if (StartsWithOpenParen(first.Word))
                ReplaceWord(words, first, first.Word[1..]);
            var currentLast = words.First(w => w.Id == last.Id);
            if (EndsWithCloseParen(currentLast.Word))
                ReplaceWord(words, currentLast, currentLast.Word[..^1]);
        }

        return Result<LyricLine>.Ok(NewLine(begin, end, true, (agent ?? "v1") != baselineAgent, words));
    }

    private static void ReplaceWord(List<LyricWord> words, LyricWord target, string word)
    {
        var idx = words.FindIndex(w => w.Id == target.Id);
        if (idx >= 0) words[idx] = target with { Word = word };
    }

    private static bool StartsWithOpenParen(string s) =>
        s.StartsWith('(') || s.StartsWith('（');

    private static bool EndsWithCloseParen(string s) =>
        s.EndsWith(')') || s.EndsWith('）');

    private static LyricWord NewWord(double start, double end, string word, bool obscene = false, double emptyBeat = 0.0) =>
        new(Guid.NewGuid().ToString("N"), start, end, word, string.Empty, obscene, emptyBeat);

    private static LyricLine NewLine(double start, double end, bool isBg, bool isDuet, List<LyricWord> words, string translated = "", string roman = "") =>
        new(Guid.NewGuid().ToString("N"), words, translated, roman, isBg, isDuet, start, end, false);

    private static bool Is(string localName, XElement el) =>
        el.Name.LocalName == localName;

    private static string? Attr(XElement el, string localName) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    // Verified WASM behavior: timestamps truncate toward zero to whole ms
    // (0.0019s -> 1ms, 0.0009s -> 0ms). Integer/decimal arithmetic throughout
    // so float noise can never straddle the truncation (41.138s -> 41138,
    // never 41137.999...). Colon form allows at most 3 frac digits.
    internal static bool TryParseTimestamp(string s, out double ms)
    {
        ms = 0;
        s = s.Trim();
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            if (!TryParseSecMs(parts[^1], out var secMs)) return false;
            if (!int.TryParse(parts[^2], out var min)) return false;
            var hour = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], out hour)) return false;
            ms = ((hour * 60L + min) * 60 * 1000) + secMs;
            return true;
        }
        if (!decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return false;
        ms = (double)decimal.Truncate(seconds * 1000);
        return true;
    }

    private static bool TryParseSecMs(string s, out long ms)
    {
        ms = 0;
        var dot = s.IndexOf('.');
        if (dot < 0)
        {
            if (!int.TryParse(s, out var whole)) return false;
            ms = whole * 1000L;
            return true;
        }
        if (!int.TryParse(s[..dot], out var w)) return false;
        var fracStr = s[(dot + 1)..];
        if (fracStr.Length == 0 || fracStr.Length > 3 || !fracStr.All(char.IsDigit)) return false;
        ms = w * 1000L + long.Parse(fracStr.PadRight(3, '0'));
        return true;
    }

    internal static long ByteOffsetFor(string xml, int lineNumber, int linePosition)
    {
        var lines = xml.Split('\n');
        long bytes = 0;
        for (var i = 0; i < lineNumber - 1 && i < lines.Length; i++)
            bytes += Encoding.UTF8.GetByteCount(lines[i] + "\n");
        if (lineNumber - 1 < lines.Length)
            bytes += Encoding.UTF8.GetByteCount(lines[lineNumber - 1][..Math.Min(linePosition - 1, lines[lineNumber - 1].Length)]);
        return bytes;
    }

    private static Result<T> Fail<T>(string kind, string message, long byteOffset, List<string> stack, string? attr = null, string? offending = null) =>
        Result<T>.Fail(new TtmlError(kind, $"{message} (at byte offset {byteOffset})", byteOffset, null, stack, attr, offending));

    [GeneratedRegex(@"^(\s*<[A-Za-z_][^<>]*>)*\s*$", RegexOptions.Singleline)]
    private static partial Regex OpenTagsOnly();

    private sealed class Cursor(string xml)
    {
        private int _index;
        private int _lastFound;
        private long _bytes;

        public long Take(string tag)
        {
            var found = xml.IndexOf("<" + tag, _index, StringComparison.Ordinal);
            if (found < 0) found = _index;
            // Incremental byte counting: searches only move forward, so
            // encode just the delta (was O(n) per word -> O(n^2) total).
            if (found > _lastFound)
            {
                _bytes += Encoding.UTF8.GetByteCount(xml[_lastFound..found]);
                _lastFound = found;
            }
            _index = found + 1;
            return _bytes;
        }
    }
}
