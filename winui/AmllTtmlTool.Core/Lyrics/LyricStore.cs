using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.Core.Lyrics.Import;
using AmllTtmlTool.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmllTtmlTool.Core.Lyrics;

// App-wide lyric session: thin facade over EditDocument + the Core history.
// Replaces the old records-based store (own undo stack deleted). Records
// exist only at the import/export boundary (adapters) and as read-only
// snapshots for export. Nothing in the app mutates a record.
//
// Preserved behaviors (from the old store): extension sets, FileName,
// Changed/Notice events, New/Open/paste flows, merge no-op rule, MarkSaved
// silence, failure-leaves-doc-untouched. Deliberate changes, all documented:
// - FileName default is "Untitled" (was "lyric.ttml").
// - IsDirty tracks a saved baseline depth: clean after New/Open/MarkSaved
//   even though history is kept (undo restores the previous doc); edits and
//   undo/redo away from the baseline mark dirty. The old store forced dirty
//   on every undo (even back to the saved state); the original web app uses
//   canUndo. Baseline-depth matches the original in every common flow.
// - OpenLyricText routes through the importer registry (.ttml/.lrc/.txt;
//   unknown extensions fail cleanly instead of falling into TTML parse).
// - Once 100+ steps bury the baseline (history cap drop), the doc stays
//   dirty (sticky) rather than risking a false clean.
public sealed record LyricFileResult(
    bool Success,
    string? ErrorKind = null,
    string? ErrorMessage = null,
    List<string>? FixNotes = null);

public partial class LyricStore : ObservableObject
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "m4a", "alac", "ape", "mac", "wv", "tta", "tak",
        "aiff", "aif", "aifc", "mp3", "aac", "mp4", "ogg", "oga", "opus",
        "wma", "asf", "mpc", "mpp", "mp+", "dsf", "ac3", "ec3", "eac3", "dts",
        "dtshd", "thd", "mlp", "mka", "amr", "rm", "ra", "au", "snd",
        "caf", "w64", "iff", "8svx",
    };

    public static readonly HashSet<string> LyricExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ttml", "lrc", "txt",
    };

    private static readonly LyricImporterRegistry Registry = LyricImporterRegistry.Default();

    [ObservableProperty]
    private EditDocument _document = new();

    [ObservableProperty]
    private string _fileName = "Untitled";

    [ObservableProperty]
    private bool _isDirty;

    private int _savedDepth;
    private bool _historyTruncated;

    public bool CanUndo => Document.CanUndo;
    public bool CanRedo => Document.CanRedo;

    public event Action? Changed;
    public event Action<string>? Notice;

    public static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : string.Empty;
    }

    public static bool IsAudioFile(string fileName) => AudioExtensions.Contains(ExtensionOf(fileName));
    public static bool IsLyricFile(string fileName) => LyricExtensions.Contains(ExtensionOf(fileName));

    public void Execute(ILyricCommand command)
    {
        RunCommand(command);
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (!Document.Undo()) return false;
        SyncDirty();
        SyncHistory();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!Document.Redo()) return false;
        SyncDirty();
        SyncHistory();
        Changed?.Invoke();
        return true;
    }

    public void NewDocument()
    {
        RunCommand(new BulkReplaceCommand(new List<EditLine>(), new List<EditMetadata>()));
        FileName = "Untitled";
        Rebaseline();
        Changed?.Invoke();
    }

    public LyricFileResult OpenLyricText(string text, string fileName, string? displayName = null)
    {
        ILyricImporter importer;
        try
        {
            importer = Registry.ByExtension(fileName);
        }
        catch (LyricImportException ex)
        {
            return new LyricFileResult(false, "UnsupportedFormat", ex.Message);
        }
        LyricImportResult imported;
        try
        {
            imported = ParseLyricText(importer, text, fileName);
        }
        catch (LyricImportException ex)
        {
            return new LyricFileResult(false, "ImportFailed", ex.Message);
        }
        catch (Exception ex)
        {
            return new LyricFileResult(false, "ImportFailed", ex.Message);
        }
        ApplyImportResult(imported, fileName, displayName);
        return new LyricFileResult(true, null, null, imported.Notices);
    }

    // Pure parse phase: no document touched, safe on any thread. The UI
    // applies the result via ApplyImportResult on the UI thread.
    public static LyricImportResult ParseLyricText(ILyricImporter importer, string text, string fileName) =>
        importer.Import(text, fileName);

    public static ILyricImporter ImporterFor(string fileName) => Registry.ByExtension(fileName);

    // Apply phase: must run on the UI thread (ObservableCollections).
    public void ApplyImportResult(LyricImportResult imported, string fileName, string? displayName = null)
    {
        RunCommand(new BulkReplaceCommand(
            imported.Document.Lines.ToList(), imported.Document.Metadata.ToList()));
        FileName = displayName ?? fileName;
        Rebaseline();
        Changed?.Invoke();
        if (imported.Notices.Count > 0)
        {
            foreach (var n in imported.Notices) Notice?.Invoke(n);
        }
    }

    public void MergeAudioMetadata(List<TtmlMetadata> extracted)
    {
        var current = Document.Metadata
            .Select(m => new TtmlMetadata(m.Key, new List<string>(m.Values), m.Error))
            .ToList();
        if (!Audio.AudioMetadata.MergeExtractedMetadata(current, extracted)) return;
        RunCommand(new SetMetadataCommand(current
            .Select(m => new EditMetadata
            {
                Key = m.Key,
                Values = new List<string>(m.Value),
                Error = m.Error,
            }).ToList()));
        SyncDirty();
        SyncHistory();
        Changed?.Invoke();
    }

    public void MarkSaved(string fileName)
    {
        FileName = fileName;
        Rebaseline();
    }

    // Read-only records snapshot for export/preview boundaries.
    public TtmlLyric ToRecords() => LyricAdapters.ToRecords(Document);

    private void RunCommand(ILyricCommand command)
    {
        var before = Document.UndoDepth;
        Document.Execute(command);
        if (before == EditDocument.MaxHistoryDepth
            && Document.UndoDepth == EditDocument.MaxHistoryDepth)
            _historyTruncated = true;
        SyncDirty();
        SyncHistory();
    }

    private void Rebaseline()
    {
        _savedDepth = Document.UndoDepth;
        _historyTruncated = false;
        SyncDirty();
        SyncHistory();
    }

    private void SyncDirty() =>
        IsDirty = _historyTruncated || Document.UndoDepth != _savedDepth;

    private void SyncHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}
