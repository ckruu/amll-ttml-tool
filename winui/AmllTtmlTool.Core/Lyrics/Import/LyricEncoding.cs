using System.Text;

namespace AmllTtmlTool.Core.Lyrics.Import;

// Byte decoding for lyric text. The original has NO detection (File API
// file.text() = UTF-8, BOM kept as data); this layer is stricter per the
// port brief: BOM sniffing (UTF-8/16/32 LE+BE), a NUL heuristic for BOM-less
// UTF-16, UTF-8 otherwise with replacement fallback (like file.text()).
// Exactly one leading U+FEFF is stripped after decoding.
public static class LyricEncoding
{
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00
            && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return StripBom(Encoding.BigEndianUnicode.GetString(bytes, 4, bytes.Length - 4));
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0x00 && bytes[3] == 0x00)
            return StripBom(Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StripBom(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return StripBom(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return StripBom(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        if (LooksLikeUtf16Le(bytes)) return StripBom(Encoding.Unicode.GetString(bytes));
        if (LooksLikeUtf16Be(bytes)) return StripBom(Encoding.BigEndianUnicode.GetString(bytes));
        return StripBom(Encoding.UTF8.GetString(bytes));
    }

    // ASCII-range UTF-16 has NULs at every other byte (odd positions for LE,
    // even for BE). Heuristic over the first 1KB, even length required.
    private static bool LooksLikeUtf16Le(byte[] bytes) =>
        bytes.Length >= 2 && bytes.Length % 2 == 0 && NulFraction(bytes, odd: true) > 0.25;

    private static bool LooksLikeUtf16Be(byte[] bytes) =>
        bytes.Length >= 2 && bytes.Length % 2 == 0 && NulFraction(bytes, odd: false) > 0.25;

    private static double NulFraction(byte[] bytes, bool odd)
    {
        var sample = Math.Min(bytes.Length, 1024);
        var nuls = 0;
        var total = 0;
        for (var i = odd ? 1 : 0; i < sample; i += 2)
        {
            total++;
            if (bytes[i] == 0) nuls++;
        }
        return total == 0 ? 0 : (double)nuls / total;
    }

    private static string StripBom(string s) =>
        s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
}
