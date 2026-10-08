using AmllTtmlTool.Core.Audio;
using AmllTtmlTool.Core.Models;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AmllTtmlTool.WinUI.Services;

public sealed class FileIntake(
    LyricStore store,
    AudioEngine audio,
    Window window,
    UIElement anchor,
    Action<string> notify)
{
    public async Task OpenLyricFileAsync(StorageFile file)
    {
        var text = await FileIO.ReadTextAsync(file);
        var result = store.OpenLyricText(text, file.Name);
        if (!result.Success)
        {
            await FileDialogs.ShowErrorAsync(anchor, "Open failed",
                $"Could not parse {file.Name}.",
                result.ErrorKind + (result.ErrorMessage is null ? string.Empty : ": " + result.ErrorMessage));
        }
    }

    public async Task OpenAudioFileAsync(StorageFile file)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var props = await file.Properties.GetMusicPropertiesAsync();
            Put(raw, "title", props.Title);
            Put(raw, "artist", props.Artist);
            Put(raw, "album", props.Album);
            foreach (var c in props.Composers) Put(raw, "composer", c);
        }
        catch { }
        try
        {
            var meta = AudioMetadata.ParseAudioTrackMetadata(raw, file.Name);
            var cover = await ReadCoverAsync(file);
            if (cover is not null) audio.Cover = cover;
            await audio.LoadAsync(file.Path);
            store.MergeAudioMetadata(AudioMetadata.ToTtmlMetadata(meta));
        }
        catch (Exception ex)
        {
            audio.Cover = null;
            await FileDialogs.ShowErrorAsync(anchor, "Audio failed",
                $"Could not open {file.Name}.",
                ex.Message);
        }
    }

    public async Task OpenAnyAsync(StorageFile file)
    {
        var name = file.Name;
        if (LyricStore.IsAudioFile(name))
        {
            await OpenAudioFileAsync(file);
        }
        else if (LyricStore.IsLyricFile(name))
        {
            await OpenLyricFileAsync(file);
        }
        else
        {
            notify($"Unsupported file: {name}");
        }
    }

    public async Task PasteAsync()
    {
        var text = await FileDialogs.GetClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            notify("Clipboard has no text.");
            return;
        }
        if (!store.IsDirty || await FileDialogs.ConfirmAsync(anchor, "Paste lyrics",
                "Replace the current document with clipboard contents?", "Replace"))
        {
            var result = store.OpenLyricText(text, "lyric.ttml");
            if (!result.Success)
            {
                await FileDialogs.ShowErrorAsync(anchor, "Paste failed",
                    "Clipboard text is not valid TTML or LRC.",
                    result.ErrorKind + (result.ErrorMessage is null ? string.Empty : ": " + result.ErrorMessage));
            }
        }
    }

    public async Task CopyTtmlAsync(bool appleFlavor)
    {
        string text;
        if (appleFlavor)
        {
            text = Core.AppleTtml.ToAppleTtml(store.Document);
            foreach (var w in Core.AppleTtml.GetAppleTtmlWarnings(store.Document)) notify(w);
        }
        else
        {
            var prepared = Core.TtmlNormalize.PostProcess(Core.TtmlNormalize.NormalizeForExport(store.Document));
            var gen = Core.TtmlGenerator.Generate(prepared, new(), new(false, false, Core.TtmlNormalize.IsLineTimingLyrics(prepared)));
            if (!gen.Success)
            {
                await FileDialogs.ShowErrorAsync(anchor, "Copy failed", "Could not generate TTML.", gen.Error?.Message);
                return;
            }
            text = gen.Data!;
        }
        FileDialogs.CopyText(text);
        notify(appleFlavor ? "Apple TTML copied." : "TTML copied.");
    }

    public async Task SaveAsync(bool appleFlavor)
    {
        var file = await FileDialogs.PickSaveTtmlAsync(window, store.FileName, appleFlavor);
        if (file is null) return;
        string text;
        if (appleFlavor)
        {
            text = Core.AppleTtml.ToAppleTtml(store.Document);
            foreach (var w in Core.AppleTtml.GetAppleTtmlWarnings(store.Document)) notify(w);
        }
        else
        {
            var prepared = Core.TtmlNormalize.PostProcess(Core.TtmlNormalize.NormalizeForExport(store.Document));
            var gen = Core.TtmlGenerator.Generate(prepared, new(), new(false, false, Core.TtmlNormalize.IsLineTimingLyrics(prepared)));
            if (!gen.Success)
            {
                await FileDialogs.ShowErrorAsync(anchor, "Save failed", "Could not generate TTML.", gen.Error?.Message);
                return;
            }
            text = gen.Data!;
        }
        await FileIO.WriteTextAsync(file, text);
        if (!appleFlavor) store.MarkSaved(file.Name);
        notify($"Saved {file.Name}.");
    }

    private static void Put(Dictionary<string, string> raw, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        raw[key] = raw.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing + ";" + value
            : value;
    }

    private static async Task<AudioCover?> ReadCoverAsync(StorageFile file)
    {
        try
        {
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 512);
            if (thumb is null || thumb.Size == 0) return null;
            using var stream = thumb.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var mime = thumb.ContentType == "image/png" ? "image/png" : "image/jpeg";
            return new AudioCover(ms.ToArray(), mime);
        }
        catch
        {
            return null;
        }
    }
}
