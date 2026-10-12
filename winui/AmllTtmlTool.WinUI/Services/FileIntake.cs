using AmllTtmlTool.Core.Audio;
using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Import;
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
    Action<string> notify,
    SmtcService? smtc = null,
    Action<bool>? setBusy = null,
    Action<EditDocument>? onImported = null)
{
    // Self-driving runs (Step-5 --lyrics-test) must never block on a modal
    // dialog: when set, errors are recorded here instead of shown, so a
    // failing import becomes a fast FAIL verdict, not a watchdog timeout.
    public Action<string, string, string?>? ErrorRecorder { get; set; }

    // Single import funnel: picker, drop, startup file, --load-lyrics hook
    // and paste all land here. Bytes -> decode -> parse OFF the UI thread ->
    // apply ON the UI thread (ObservableCollections). Never throws: every
    // failure becomes an error dialog and the previous document stays.
    public async Task ImportFileAsync(StorageFile file)
    {
        byte[] bytes;
        try
        {
            using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Import failed",
                $"Could not read {file.Name}.", ex.Message);
            return;
        }
        await ImportTextAsync(LyricEncoding.Decode(bytes), file.Name);
    }

    public async Task ImportTextAsync(string text, string fileName, string? displayName = null)
    {
        ILyricImporter importer;
        try
        {
            importer = LyricStore.ImporterFor(fileName);
        }
        catch (LyricImportException ex)
        {
            await ShowErrorAsync("Import failed",
                $"Unsupported lyric format: {fileName}.", ex.Message);
            return;
        }
        // Slow parses (>200ms) show the indeterminate indicator; fast ones
        // never flash it.
        var parseSw = System.Diagnostics.Stopwatch.StartNew();
        var parseTask = Task.Run(() => LyricStore.ParseLyricText(importer, text, fileName));
        var slow = await Task.WhenAny(parseTask, Task.Delay(200)) != parseTask;
        if (slow) SetBusy(true);
        try
        {
            LyricImportResult result;
            try
            {
                result = await parseTask;
            }
            catch (LyricImportException ex)
            {
                await ShowErrorAsync("Import failed",
                    $"Could not parse {fileName}.", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Import failed",
                    $"Could not parse {fileName}.", ex.Message);
                return;
            }
            var parseMs = parseSw.ElapsedMilliseconds;
            var applySw = System.Diagnostics.Stopwatch.StartNew();
            await OnUiAsync(() =>
            {
                store.ApplyImportResult(result, fileName, displayName);
                onImported?.Invoke(store.Document);
            });
            WriteTiming($"import-timing file={fileName} parseMs={parseMs} applyMs={applySw.ElapsedMilliseconds}");
        }
        finally
        {
            if (slow) SetBusy(false);
        }
    }

    // Phase/timing telemetry for the Step-5 perf tests. Test runs only
    // (HarnessHost.Report set); zero production impact otherwise.
    private static void WriteTiming(string line)
    {
        try
        {
            if (HarnessHost.IsTest && HarnessHost.Report is string report)
                System.IO.File.AppendAllText(report, line + "\n");
        }
        catch { }
    }

    private Task ShowErrorAsync(string title, string message, string? details)
    {
        if (ErrorRecorder is not null)
        {
            ErrorRecorder(title, message, details);
            return Task.CompletedTask;
        }
        return OnUiAsync(() => FileDialogs.ShowErrorAsync(anchor, title, message, details));
    }

    private void SetBusy(bool busy)
    {
        if (setBusy is null) return;
        if (window.DispatcherQueue.HasThreadAccess) setBusy(busy);
        else window.DispatcherQueue.TryEnqueue(() => setBusy(busy));
    }

    private Task OnUiAsync(Action work)
    {
        if (window.DispatcherQueue.HasThreadAccess)
        {
            work();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource();
        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task OnUiAsync(Func<Task> work)
    {
        if (window.DispatcherQueue.HasThreadAccess)
        {
            await work();
            return;
        }
        var tcs = new TaskCompletionSource();
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        await tcs.Task;
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
            var bins = FfmpegBins.Locate();
            if (bins is not null)
            {
                try
                {
                    var probe = await FfmpegDecoder.ProbeAsync(bins, file.Path);
                    foreach (var (k, v) in probe.Tags)
                    {
                        if (!raw.ContainsKey(k) || string.IsNullOrWhiteSpace(raw[k])) raw[k] = v;
                    }
                    meta = AudioMetadata.ParseAudioTrackMetadata(raw, file.Name);
                }
                catch { }
            }
            var cover = bins is not null ? await TryFfmpegCoverAsync(bins, file.Path) : null;
            audio.Cover = cover ?? await ReadCoverAsync(file);
            await audio.LoadAsync(file.Path);
            // The playback level belongs to the app, not to this track: opening
            // or importing a file must never reset it (a used-to reset it to a
            // fixed value here, which jumped the whole OS).
            audio.ReassertVolume();
            store.MergeAudioMetadata(AudioMetadata.ToTtmlMetadata(meta));
            // OS transport surface: title/artist/album (+ cover) for the
            // volume OSD and media keys. Best-effort, never blocks the open.
            if (smtc is not null)
            {
                var (smtcTitle, smtcArtist, smtcAlbum) =
                    AmllTtmlTool.Core.Audio.SmtcDisplay.ForTrack(meta);
                _ = smtc.RefreshMetadataAsync(smtcTitle, smtcArtist, smtcAlbum,
                    audio.Cover?.Bytes);
            }
        }
        catch (Exception ex)
        {
            audio.Cover = null;
            await FileDialogs.ShowErrorAsync(anchor, "Audio failed",
                $"Could not open {file.Name}. {AudioErrorText.MessageOf(ex)}",
                AudioErrorText.FullReport(ex));
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
            await ImportFileAsync(file);
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
            // Sniff: pasted text has no file, so the registry picks the
            // format (the old path forced TTML and rejected LRC pastes).
            var sniffed = LyricImporterRegistry.Default().Sniff(text);
            var pseudoName = "pasted" + sniffed.Extensions[0];
            await ImportTextAsync(text, pseudoName, "Untitled");
        }
    }

    public async Task CopyTtmlAsync(bool appleFlavor)
    {
        string text;
        if (appleFlavor)
        {
            text = Core.AppleTtml.ToAppleTtml(store.ToRecords());
            foreach (var w in Core.AppleTtml.GetAppleTtmlWarnings(store.ToRecords())) notify(w);
        }
        else
        {
            var prepared = Core.TtmlNormalize.PostProcess(Core.TtmlNormalize.NormalizeForExport(store.ToRecords()));
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
        await SaveToFileAsync(file, appleFlavor);
    }

    public async Task SaveToFileAsync(StorageFile file, bool appleFlavor)
    {
        var text = await BuildSaveTextAsync(appleFlavor, appleFlavor ? "Save failed" : "Save failed");
        if (text is null) return;
        await FileIO.WriteTextAsync(file, text);
        if (!appleFlavor) store.MarkSaved(file.Name);
        notify($"Saved {file.Name}.");
    }

    private async Task<string?> BuildSaveTextAsync(bool appleFlavor, string title)
    {
        if (appleFlavor)
        {
            foreach (var w in Core.AppleTtml.GetAppleTtmlWarnings(store.ToRecords())) notify(w);
            return Core.AppleTtml.ToAppleTtml(store.ToRecords());
        }
        var prepared = Core.TtmlNormalize.PostProcess(Core.TtmlNormalize.NormalizeForExport(store.ToRecords()));
        var gen = Core.TtmlGenerator.Generate(prepared, new(), new(false, false, Core.TtmlNormalize.IsLineTimingLyrics(prepared)));
        if (!gen.Success)
        {
            await FileDialogs.ShowErrorAsync(anchor, title, "Could not generate TTML.", gen.Error?.Message);
            return null;
        }
        return gen.Data!;
    }

    private static void Put(Dictionary<string, string> raw, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        raw[key] = raw.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing + ";" + value
            : value;
    }

    private static async Task<AudioCover?> TryFfmpegCoverAsync(FfmpegBins bins, string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await FfmpegDecoder.ExtractCoverAsync(bins, path, cts.Token);
            return bytes is null ? null : new AudioCover(bytes, "image/jpeg");
        }
        catch
        {
            return null;
        }
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
