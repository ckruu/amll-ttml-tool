namespace AmllTtmlTool.Core.Lyrics.Import;

// Plain-text importer: exact port of ImportFromText.tsx (addLine loop,
// modes, separators, prefixes, empty beats, swap). Differences from the
// original, all deliberate and tested:
// - Interleaved tail overrun / missing SameLine parts assign "" instead of
//   undefined (output-equal on every serializer).
// - Empty prefixes are skipped (the original would loop forever on
//   startsWith("")), as is an empty SameLine separator only insofar as JS
//   split("") yields chars (replicated exactly).
// - \r is retained (\n-only split), blank lines yield blank one-word lines.

public enum TextImportMode { Lyric, LyricTrans, LyricRoman, LyricTransRoman }

public enum TextLineSeparatorMode { Interleaved, SameLineSeparator }

public sealed record TextImportOptions(
    TextImportMode Mode = TextImportMode.Lyric,
    TextLineSeparatorMode SeparatorMode = TextLineSeparatorMode.Interleaved,
    string LineSeparator = "|",
    bool SwapTransAndRoman = false,
    string WordSeparator = "\\",
    bool EnableSpecialPrefix = false,
    string BgPrefix = "<",
    string DuetPrefix = ">",
    bool EnableEmptyBeat = false,
    string EmptyBeatSymbol = "^");

public static class TextImporter
{
    public static List<EditLine> Import(string text, TextImportOptions? options = null)
    {
        options ??= new TextImportOptions();
        var rawLines = text.Split('\n');
        var result = new List<EditLine>();

        void AddLine(string orig, string trans, string roman)
        {
            var finalOrig = orig;
            var isBg = false;
            var isDuet = false;
            if (options.EnableSpecialPrefix)
            {
                while (true)
                {
                    if (options.BgPrefix.Length > 0 && finalOrig.StartsWith(options.BgPrefix, StringComparison.Ordinal))
                    {
                        isBg = true;
                        finalOrig = finalOrig.Substring(options.BgPrefix.Length);
                    }
                    else if (options.DuetPrefix.Length > 0 && finalOrig.StartsWith(options.DuetPrefix, StringComparison.Ordinal))
                    {
                        isDuet = true;
                        finalOrig = finalOrig.Substring(options.DuetPrefix.Length);
                    }
                    else break;
                }
            }
            var line = EditLine.New();
            var word = EditWord.New();
            word.Text = finalOrig;
            line.Words.Add(word);
            line.TranslatedLyric = trans;
            line.RomanLyric = roman;
            line.IsBg = isBg;
            line.IsDuet = isDuet;
            result.Add(line);
        }

        switch (options.Mode)
        {
            case TextImportMode.Lyric:
                foreach (var line in rawLines) AddLine(line, "", "");
                break;
            case TextImportMode.LyricTrans:
                AddWithSubs("translatedLyric", null, rawLines, options);
                break;
            case TextImportMode.LyricRoman:
                AddWithSubs("romanLyric", null, rawLines, options);
                break;
            case TextImportMode.LyricTransRoman:
                AddWithSubs("translatedLyric", "romanLyric", rawLines, options);
                break;
        }
        void AddWithSubs(string sub1, string? sub2, string[] lines, TextImportOptions o)
        {
            if (o.SeparatorMode == TextLineSeparatorMode.Interleaved)
            {
                var skip = sub2 is not null ? 3 : 2; // 1 + #subs
                for (var i = 0; i < lines.Length; i += skip)
                {
                    var ii = 0;
                    var subText1 = i + ++ii < lines.Length ? lines[i + ii] : "";
                    var subText2 = sub2 is not null
                        ? (i + ++ii < lines.Length ? lines[i + ii] : "")
                        : "";
                    var line = NewBare(lines[i]);
                    SetSub(line, sub1, subText1);
                    if (sub2 is not null) SetSub(line, sub2, subText2);
                }
            }
            else
            {
                foreach (var lineText in lines)
                {
                    string[] parts = o.LineSeparator.Length == 0
                        ? lineText.Select(c => c.ToString()).ToArray() // JS split("") chars
                        : lineText.Split([o.LineSeparator], StringSplitOptions.None);
                    var subText1 = parts.Length > 1 ? parts[1] : "";
                    var subText2 = sub2 is not null && parts.Length > 2 ? parts[2] : "";
                    var line = NewBare(parts[0]);
                    SetSub(line, sub1, subText1);
                    if (sub2 is not null) SetSub(line, sub2, subText2);
                }
            }
        }
        EditLine NewBare(string orig)
        {
            var before = result.Count;
            AddLine(orig, "", "");
            return result[before];
        }
        static void SetSub(EditLine line, string which, string value)
        {
            if (which == "translatedLyric") line.TranslatedLyric = value;
            else line.RomanLyric = value;
        }

        if (options.SwapTransAndRoman)
        {
            foreach (var line in result)
                (line.RomanLyric, line.TranslatedLyric) = (line.TranslatedLyric, line.RomanLyric);
        }

        if (options.WordSeparator.Length > 0)
        {
            foreach (var line in result)
            {
                var whole = string.Concat(line.Words.Select(w => w.Text));
                line.Words.Clear();
                foreach (var part in whole.Split([options.WordSeparator], StringSplitOptions.None))
                {
                    var w = EditWord.New();
                    w.Text = part;
                    line.Words.Add(w);
                }
            }
        }

        if (options.EnableEmptyBeat && options.EmptyBeatSymbol.Length > 0)
        {
            foreach (var line in result)
            {
                foreach (var word in line.Words)
                {
                    while (word.Text.EndsWith(options.EmptyBeatSymbol, StringComparison.Ordinal))
                    {
                        word.Text = word.Text.Substring(0, word.Text.Length - options.EmptyBeatSymbol.Length);
                        word.EmptyBeat += 1;
                    }
                }
            }
        }

        return result;
    }
}
