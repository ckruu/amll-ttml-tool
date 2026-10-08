using AmllTtmlTool.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmllTtmlTool.WinUI.Services;

public sealed record LyricFileResult(bool Success, string? ErrorKind = null, string? ErrorMessage = null, List<string>? FixNotes = null);

public partial class LyricStore : ObservableObject
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "m4a", "alac", "ape", "mac", "wv", "tta", "tak",
        "aiff", "aif", "aifc", "mp3", "aac", "mp4", "ogg", "oga", "opus",
        "wma", "asf", "mpc", "mpp", "mp+", "dsf", "ac3", "eac3", "dts",
        "dtshd", "thd", "mlp", "mka", "amr", "rm", "ra", "au", "snd",
        "caf", "w64", "iff", "8svx",
    };

    public static readonly HashSet<string> LyricExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ttml", "lrc",
    };

    private const int MaxUndo = 100;

    [ObservableProperty]
    private TtmlLyric _document = new([], []);

    [ObservableProperty]
    private string _fileName = "lyric.ttml";

    [ObservableProperty]
    private bool _isDirty;

    private readonly List<TtmlLyric> _undo = [];
    private readonly List<TtmlLyric> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;
    public event Action<string>? Notice;

    public static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : string.Empty;
    }

    public static bool IsAudioFile(string fileName) => AudioExtensions.Contains(ExtensionOf(fileName));
    public static bool IsLyricFile(string fileName) => LyricExtensions.Contains(ExtensionOf(fileName));

    public void NewDocument()
    {
        PushUndo();
        Document = new TtmlLyric([], []);
        FileName = "lyric.ttml";
        IsDirty = false;
        Changed?.Invoke();
    }

    public LyricFileResult OpenLyricText(string text, string fileName)
    {
        var ext = ExtensionOf(fileName);
        TtmlLyric lyric;
        List<string>? notes = null;
        if (ext.Equals("lrc", StringComparison.OrdinalIgnoreCase))
        {
            lyric = new TtmlLyric([], LrcToLines(text));
        }
        else
        {
            var detection = Core.LegacyTtml.DetectLegacyTtml(text);
            var parsed = Core.TtmlParser.Parse(text);
            if (!parsed.Success)
            {
                return new LyricFileResult(false, parsed.Error!.Kind, parsed.Error.Message);
            }
            lyric = parsed.Data!;
            if (detection.IsLegacy)
            {
                var repaired = Core.LegacyTtml.RepairLegacyImport(lyric, text);
                lyric = repaired.Result;
                notes = repaired.Fixes;
                notes.Insert(0, Core.LegacyTtml.GetLegacyFixNotice(detection.Reasons));
            }
        }
        PushUndo();
        Document = lyric;
        FileName = fileName;
        IsDirty = false;
        Changed?.Invoke();
        if (notes is not null)
        {
            foreach (var n in notes) Notice?.Invoke(n);
        }
        return new LyricFileResult(true, null, null, notes);
    }

    private static List<LyricLine> LrcToLines(string text)
    {
        var parsed = Core.LrcParser.ParseLrc(text);
        return parsed.Select(l => l with
        {
            Id = Guid.NewGuid().ToString("N"),
            IgnoreSync = false,
            Words = l.Words.Select(w => w with { Id = Guid.NewGuid().ToString("N") }).ToList(),
        }).ToList();
    }

    public void Mutate(Func<TtmlLyric, TtmlLyric> change)
    {
        PushUndo();
        Document = change(Document);
        IsDirty = true;
        Changed?.Invoke();
    }

    public void MergeAudioMetadata(List<TtmlMetadata> extracted)
    {
        var current = Document.Metadata.ToList();
        if (!Core.Audio.AudioMetadata.MergeExtractedMetadata(current, extracted)) return;
        PushUndo();
        Document = Document with { Metadata = current };
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Document);
        Document = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsDirty = true;
        Changed?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Document);
        Document = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        IsDirty = true;
        Changed?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void MarkSaved(string fileName)
    {
        FileName = fileName;
        IsDirty = false;
    }

    private void PushUndo()
    {
        _undo.Add(Clone(Document));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    internal static TtmlLyric Clone(TtmlLyric source) => source with
    {
        Metadata = source.Metadata.Select(m => m with { Value = m.Value.ToList() }).ToList(),
        LyricLines = source.LyricLines.Select(l => l with
        {
            Words = l.Words.Select(w => w with
            {
                Ruby = w.Ruby?.Select(r => r with { }).ToList(),
            }).ToList(),
        }).ToList(),
    };
}
