using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Lyrics.Import;

// Import pipeline: text in, EditDocument out. Every importer funnels through
// BulkReplaceCommand semantics (one history step at the UI layer); these
// return documents, the UI executes the replace.

// Failure to import (parse errors, unknown formats). The UI catches this
// and shows the error WITHOUT touching the open document — mirroring the
// original's TtmlErrorDialog / toast paths.
public sealed class LyricImportException : Exception
{
    public LyricImportException(string message) : base(message)
    {
    }

    public LyricImportException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed record LyricImportResult(EditDocument Document, List<string> Notices);

public interface ILyricImporter
{
    string FormatId { get; }
    IReadOnlyList<string> Extensions { get; }
    bool CanSniff(string text);
    LyricImportResult Import(string text, string sourceName);
}

// TTML: Parse -> legacy repair -> wrapper renames -> xml:lang backfill,
// exactly useFileOpener.performOpenFile. Repair fixes ride along as notices.
public sealed class TtmlImporter : ILyricImporter
{
    public string FormatId => "ttml";
    public IReadOnlyList<string> Extensions => [".ttml"];

    public bool CanSniff(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("<", StringComparison.Ordinal) && t.Contains("<tt");
    }

    public LyricImportResult Import(string text, string sourceName)
    {
        var parsed = TtmlParser.Parse(text);
        if (!parsed.Success)
            throw new LyricImportException(
                $"TTML parse failed ({sourceName}): {parsed.Error!.Message}");
        var (repairedRecords, fixes) = LegacyTtml.RepairLegacyImport(parsed.Data!, text);
        var notices = new List<string>(fixes);
        // The legacy notice heads the list (useFileOpener behavior).
        var detection = LegacyTtml.DetectLegacyTtml(text);
        if (detection.IsLegacy)
            notices.Insert(0, LegacyTtml.GetLegacyFixNotice(detection.Reasons));
        var norm = TtmlNormalize.ApplyImportRenames(repairedRecords);
        var lang = LegacyTtml.ExtractXmlLang(text);
        if (lang is not null && !norm.Metadata.Any(m =>
                m.Key.Equals("language", StringComparison.OrdinalIgnoreCase)))
        {
            norm = norm with
            {
                Metadata = [.. norm.Metadata, new TtmlMetadata("language", [lang])],
            };
        }
        var doc = LyricAdapters.FromRecords(norm, sourceName);
        return new LyricImportResult(doc, notices);
    }
}

// LRC: the local parseLrc rules (no metadata/offset tags — ignored like the
// original), fresh parser Guids, no document metadata.
public sealed class LrcImporter : ILyricImporter
{
    public string FormatId => "lrc";
    public IReadOnlyList<string> Extensions => [".lrc"];

    public bool CanSniff(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\[[0-9]{1,2}:[0-9]{1,2}");

    public LyricImportResult Import(string text, string sourceName)
    {
        var lines = LrcParser.ParseLrc(text);
        var doc = LyricAdapters.FromRecords(new TtmlLyric([], lines), sourceName);
        return new LyricImportResult(doc, []);
    }
}

// Plain text: the ImportFromText dialog rules. The original only offers this
// via dialog (file open of .txt is "unsupported"); the registry additionally
// maps .txt here as a documented addition.
public sealed class PlainTextImporter : ILyricImporter
{
    private readonly TextImportOptions _options;

    public PlainTextImporter(TextImportOptions? options = null) =>
        _options = options ?? new TextImportOptions();

    public string FormatId => "txt";
    public IReadOnlyList<string> Extensions => [".txt", ".text"];

    public bool CanSniff(string text) => true; // fallback, registered last

    public LyricImportResult Import(string text, string sourceName)
    {
        var doc = new EditDocument();
        foreach (var line in TextImporter.Import(text, _options))
            doc.Lines.Add(line);
        return new LyricImportResult(doc, []);
    }
}

// Registry keyed by extension AND content sniffing. YRC/QRC/LYS/ESLRC plug
// in here in Phase 4 without touching existing importers.
public sealed class LyricImporterRegistry
{
    private readonly List<ILyricImporter> _importers = new();

    public LyricImporterRegistry Register(ILyricImporter importer)
    {
        _importers.Add(importer);
        return this;
    }

    public static LyricImporterRegistry Default() => new LyricImporterRegistry()
        .Register(new TtmlImporter())
        .Register(new LrcImporter())
        .Register(new PlainTextImporter());

    public ILyricImporter ByExtension(string pathOrExtension)
    {
        var ext = pathOrExtension.StartsWith('.')
            ? pathOrExtension
            : Path.GetExtension(pathOrExtension);
        var hit = _importers.FirstOrDefault(i => i.Extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
        if (hit is null)
            throw new LyricImportException($"Unsupported lyric format: '{ext}'.");
        return hit;
    }

    public ILyricImporter Sniff(string text)
    {
        foreach (var importer in _importers)
        {
            if (importer.CanSniff(text)) return importer;
        }
        throw new LyricImportException("No importer accepts this content.");
    }

    public LyricImportResult ImportFile(string path, string text) =>
        ByExtension(path).Import(text, Path.GetFileName(path));
}
