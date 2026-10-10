// --smtc-test <file> [reportPath] harness: end-to-end SMTC verification
// through the OS stack (GlobalSystemMediaTransportControlsSessionManager,
// i.e. what the volume OSD and media keys see), not just our in-process
// objects. Loads one file, pushes metadata the same way FileIntake does,
// plays volume-0, and reads our session back: title/artist, Playing
// status, timeline end ~= duration, position near the ms clock. Then
// pauses and expects Paused. Self-closing. DEBUG-only.
using AmllTtmlTool.Core.Audio;
using Windows.Media.Control;

namespace AmllTtmlTool.WinUI.Services;

internal static class SmtcTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smtc-test-report.txt");

    public static async Task RunAsync(Window window, AudioEngine audio, SmtcService smtc, string file, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(5), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: smtc-test exceeded 5 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== SMTC TEST (GSMTC read-back) ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            if (!smtc.IsAttached)
            {
                Write(report, $"SMTC-UNAVAILABLE: {smtc.AttachDetail}");
                Write(report, "smtc | FAIL");
                return;
            }
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                Write(report, $"FILE-ERROR: not found: {file}");
                return;
            }
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            // Same metadata path as FileIntake: probe tags + file name,
            // cover via FFmpeg when present.
            var raw = new Dictionary<string, string>(audio.ProbeTags, StringComparer.OrdinalIgnoreCase);
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            var meta = AudioMetadata.ParseAudioTrackMetadata(raw, fileName);
            var (wantTitle, wantArtist, _) = SmtcDisplay.ForTrack(meta);
            byte[]? cover = null;
            try
            {
                var bins = FfmpegBins.Locate();
                if (bins is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    cover = await FfmpegDecoder.ExtractCoverAsync(bins, file, cts.Token);
                }
            }
            catch { }
            await OnUiAsync(window, async () => await smtc.RefreshMetadataAsync(wantTitle, wantArtist,
                meta.Albums.FirstOrDefault() ?? "", cover));
            await OnUiAsync(window, () =>
            {
                audio.SeekTo(0);
                audio.Play();
            });
            var play = await ExpectSessionAsync("playing", wantTitle, wantArtist, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                audio.Duration, () => PositionClock.ExtrapolateNow());
            Write(report, play);
            await OnUiAsync(window, () => audio.Pause());
            var pause = await ExpectSessionAsync("paused", wantTitle, wantArtist, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                audio.Duration, () => PositionClock.ExtrapolateNow());
            Write(report, pause);
            var ok = play.EndsWith("OK") && pause.EndsWith("OK");
            Write(report, $"smtc | {(ok ? "OK" : "FAIL")}");
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    // Polls the system session list for our title until the expected
    // status shows (OS propagation takes ~1s), then checks metadata and
    // timeline against the engine.
    private static async Task<string> ExpectSessionAsync(string phase, string wantTitle, string wantArtist,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus wantStatus, TimeSpan duration, Func<TimeSpan> clockNow)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"---- expect {phase} ----");
        GlobalSystemMediaTransportControlsSession? ours = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
        var deadline = Environment.TickCount64 + 15000;
        string? lastDetail = null;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var s in mgr.GetSessions())
                {
                    GlobalSystemMediaTransportControlsSessionMediaProperties? p = null;
                    try { p = await s.TryGetMediaPropertiesAsync(); } catch { }
                    if (p is null || p.Title != wantTitle) continue;
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo? pi = null;
                    try { pi = s.GetPlaybackInfo(); } catch { }
                    if (pi?.PlaybackStatus == wantStatus)
                    {
                        ours = s; props = p; info = pi;
                        break;
                    }
                    lastDetail = $"title-match status={pi?.PlaybackStatus}";
                }
            }
            catch (Exception ex) { lastDetail = "manager: " + ex.GetType().Name; }
            if (ours is not null) break;
            await Task.Delay(500);
        }
        if (ours is null)
        {
            sb.AppendLine($"  session '{wantTitle}' with status {wantStatus} not found ({lastDetail ?? "no sessions"})");
            sb.Append($"smtc-{phase} | FAIL");
            return sb.ToString();
        }
        var artistOk = props!.Artist == wantArtist;
        sb.AppendLine($"  title='{props.Title}' artist='{props.Artist}' (want '{wantArtist}') album='{props.AlbumTitle}' status={info!.PlaybackStatus} {(artistOk ? "OK" : "FAIL")}");
        GlobalSystemMediaTransportControlsSessionTimelineProperties? tl = null;
        try { tl = ours.GetTimelineProperties(); } catch (Exception ex) { sb.AppendLine($"  timeline-error: {ex.GetType().Name}"); }
        var tlOk = true;
        if (tl is not null)
        {
            var endErr = Math.Abs((tl.EndTime - duration).TotalSeconds);
            var posErr = Math.Abs((tl.Position - clockNow()).TotalSeconds);
            // EndTime exact-ish; position loose (OS propagation + throttle).
            tlOk = endErr <= 1.0 && posErr <= 5.0;
            sb.AppendLine($"  timeline start={tl.StartTime} end={tl.EndTime} (want {duration}, err {endErr:F2}s) pos={tl.Position} (clock-err {posErr:F2}s) {(tlOk ? "OK" : "FAIL")}");
        }
        else
        {
            tlOk = false;
        }
        var pass = tlOk && artistOk;
        sb.Append($"smtc-{phase} | {(pass ? "OK" : "FAIL")}");
        return sb.ToString();
    }

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    private static Task OnUiAsync(Window window, Func<Task> func)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await func(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }
}
