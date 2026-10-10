// --clock-test <file> [reportPath] harness: volume-0 playback while
// sampling the same PositionClock.ExtrapolateNow() the UI renders, on
// every CompositionTarget.Rendering callback. Reports frame rate,
// monotonicity, frame-delta deviation, jitter, 30s drift against the
// engine sample counter (<5ms), text-change rate, and timer CPU cost.
// Self-closing. DEBUG-only.
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;

namespace AmllTtmlTool.WinUI.Services;

internal static class ClockTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clock-test-report.txt");

    private sealed record Sample(long Qpc, double Pos, string Text, long SnapQpc, double SnapSrc, double SnapRate, string SnapState);

    public static async Task RunAsync(Window window, AudioEngine audio, string file, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(15), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: clock-test exceeded 15 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== CLOCK TEST ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            if (NativeAudioClock.TryReadLatency(out var streamMs, out var bufFrames, out var mixRate) && mixRate > 0)
                Write(report, $"latency stream={streamMs:F2}ms endpoint-buffer={bufFrames * 1000.0 / mixRate:F1}ms(mix {mixRate}Hz)");
            else
                Write(report, $"latency NATIVE-CLOCK-UNAVAILABLE detail={NativeAudioClock.LastErrorDetail} (fallback Stopwatch correlation in use)");
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                Write(report, $"FILE-ERROR: not found: {file}");
                return;
            }
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            Write(report, $"output-latency-budget={audio.OutputLatencySeconds * 1000:F1}ms (stream+buffer+stretcher prime)");
            Write(report, await SnapshotWatchAsync(window, audio));
            foreach (var rate in new[] { 1.0, 0.5, 1.5 })
                Write(report, await SampleRateAsync(window, audio, rate, TimeSpan.FromSeconds(10)));
            Write(report, await DriftAsync(window, audio));
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    // Snapshot-watch: 6s of playing while recording the published
    // snapshot identity per frame. Proves the 250ms rebase is (or isn't)
    // landing, and shows exactly what surrounds any discontinuity.
    private static async Task<string> SnapshotWatchAsync(Window window, AudioEngine audio)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---- snapshot-watch 6s @1x ----");
        var rows = new List<(double Pos, long Qpc, double Src, string State)>(1024);
        void Handler(object? sender, object? e)
        {
            var snap = PositionClock.Latest;
            rows.Add((PositionClock.ExtrapolateNow().TotalSeconds,
                snap.QpcTicks, snap.SourceSeconds, snap.State.ToString()));
        }
        await OnUiAsync(window, () =>
        {
            audio.SetPlaybackRate(1.0);
            audio.SeekTo(0);
            audio.Play();
            CompositionTarget.Rendering += Handler;
        });
        await Task.Delay(TimeSpan.FromSeconds(6));
        await OnUiAsync(window, () =>
        {
            CompositionTarget.Rendering -= Handler;
            audio.Pause();
        });
        var distinctQpc = rows.Select(r => r.Qpc).Distinct().Count();
        sb.AppendLine($"  frames={rows.Count} distinct-snapshot-qpc={distinctQpc} (expect ~24 with 250ms rebase)");
        var worstBack = 0.0;
        var worstIdx = -1;
        for (var i = 1; i < rows.Count; i++)
        {
            var d = rows[i].Pos - rows[i - 1].Pos;
            if (d < worstBack) { worstBack = d; worstIdx = i; }
        }
        sb.AppendLine($"  worst-backwards-step={worstBack * 1000:F2}ms at frame {worstIdx}");
        if (worstIdx > 0)
        {
            var a = rows[worstIdx - 1];
            var b = rows[worstIdx];
            sb.AppendLine($"  before: pos={a.Pos:F3}s snapQpc={a.Qpc} snapSrc={a.Src:F3}s {a.State}");
            sb.AppendLine($"  after:  pos={b.Pos:F3}s snapQpc={b.Qpc} snapSrc={b.Src:F3}s {b.State}");
        }
        sb.Append($"snapshot-watch | distinct-qpc={distinctQpc} | worst-back={worstBack * 1000:F1}ms | " +
            $"{(distinctQpc >= 2 && worstBack > -0.002 ? "OK" : "FAIL")}");
        return sb.ToString();
    }

    private static async Task<string> SampleRateAsync(Window window, AudioEngine audio, double rate, TimeSpan span)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"---- rate={rate} ({span.TotalSeconds:F0}s) ----");
        var samples = new List<Sample>(2048);
        var textChanges = 0;
        string? lastText = null;
        var handlerTicks = 0L;
        long prevRawMs = -1;
        var rawBacksOver2ms = 0;
        double worstRawBackMs = 0;
        long shownMs = -1;
        var clampEngagements = 0;
        void Handler(object? sender, object? e)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var pos = PositionClock.ExtrapolateNow().TotalSeconds;
            var snap = PositionClock.Latest;
            var rawMs = (long)Math.Round(pos * 1000.0);
            // Mirror of the UI's monotonic display clamp.
            if (rawMs < prevRawMs)
            {
                var backMs = (prevRawMs - rawMs);
                if (backMs > worstRawBackMs) worstRawBackMs = backMs;
                if (backMs > 2) rawBacksOver2ms++;
            }
            prevRawMs = rawMs;
            // Mirror of the UI's monotonic display clamp: the painted value
            // never steps back; engagements count rebase corrections that
            // would otherwise have shown.
            var displayed = rawMs < shownMs ? shownMs : rawMs;
            if (rawMs < shownMs) clampEngagements++;
            shownMs = displayed;
            var text = Stamp(pos);
            if (text != lastText) { textChanges++; lastText = text; }
            samples.Add(new Sample(System.Diagnostics.Stopwatch.GetTimestamp(), pos, text,
                snap.QpcTicks, snap.SourceSeconds, snap.Rate, snap.State.ToString()));
            handlerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
        var proc0 = Process.GetCurrentProcess().TotalProcessorTime;
        var r0 = await ReadRenderStatsAsync(window);
        await OnUiAsync(window, () =>
        {
            audio.SetPlaybackRate(rate);
            audio.SeekTo(0);
            audio.Play();
            CompositionTarget.Rendering += Handler;
        });
        await Task.Delay(span);
        await OnUiAsync(window, () =>
        {
            CompositionTarget.Rendering -= Handler;
            audio.Pause();
        });
        var proc1 = Process.GetCurrentProcess().TotalProcessorTime;
        var r1 = await ReadRenderStatsAsync(window);
        var gate = await OnUiAsync(window, () => audio.LastStartGateReport);
        if (samples.Count < 10)
        {
            sb.AppendLine($"  FAIL too few frames ({samples.Count})");
            return sb.ToString().TrimEnd();
        }
        var fps = samples.Count / span.TotalSeconds;
        var backwards = 0;
        var deltas = new List<double>(samples.Count);
        var worstIdx = -1;
        var worstBack = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            var d = samples[i].Pos - samples[i - 1].Pos;
            if (d < 0)
            {
                backwards++;
                if (d < worstBack)
                {
                    worstBack = d;
                    worstIdx = i;
                }
            }
            deltas.Add(d);
        }
        if (worstIdx > 0)
        {
            var a = samples[worstIdx - 1];
            var b = samples[worstIdx];
            sb.AppendLine($"  worst-back frame {worstIdx}: {worstBack * 1000:F2}ms");
            sb.AppendLine($"    before: pos={a.Pos:F3}s snapSrc={a.SnapSrc:F3}s snapRate={a.SnapRate} {a.SnapState}");
            sb.AppendLine($"    after:  pos={b.Pos:F3}s snapSrc={b.SnapSrc:F3}s snapRate={b.SnapRate} {b.SnapState}");
        }
        var sorted = deltas.OrderBy(d => d).ToArray();
        var median = sorted[sorted.Length / 2];
        var maxDev = 0.0;
        var maxDevIdx = -1;
        for (var i = 0; i < deltas.Count; i++)
        {
            var dev = Math.Abs(deltas[i] - median);
            if (dev > maxDev) { maxDev = dev; maxDevIdx = i + 1; }
        }
        if (maxDevIdx > 0)
        {
            var a = samples[maxDevIdx - 1];
            var b = samples[maxDevIdx];
            sb.AppendLine($"  max-dev frame {maxDevIdx}: delta={(b.Pos - a.Pos) * 1000:F2}ms (median {median * 1000:F2}ms)");
            sb.AppendLine($"    before: pos={a.Pos:F3}s snapSrc={a.SnapSrc:F3}s snapRate={a.SnapRate} {a.SnapState}");
            sb.AppendLine($"    after:  pos={b.Pos:F3}s snapSrc={b.SnapSrc:F3}s snapRate={b.SnapRate} {b.SnapState}");
        }
        var mean = deltas.Average();
        var variance = deltas.Average(d => (d - mean) * (d - mean));
        var jitter = Math.Sqrt(variance);
        var handlerUs = handlerTicks / (double)samples.Count / System.Diagnostics.Stopwatch.Frequency * 1_000_000.0;
        var procPct = (proc1 - proc0).TotalMilliseconds / span.TotalMilliseconds / Environment.ProcessorCount * 100.0;
        var uiFrames = r1.Frames - r0.Frames;
        var uiText = r1.Text - r0.Text;
        var uiUs = uiFrames > 0
            ? (r1.Ticks - r0.Ticks) / (double)uiFrames / System.Diagnostics.Stopwatch.Frequency * 1_000_000.0
            : double.NaN;
        sb.AppendLine($"  frames={samples.Count} fps={fps:F1} raw-back-over-2ms={rawBacksOver2ms} (worst {worstRawBackMs:F2}ms) clamp-engagements={clampEngagements} {(rawBacksOver2ms == 0 ? "OK" : "FAIL")}");
        sb.AppendLine($"  frame-delta median={median * 1000:F2}ms max-dev={maxDev * 1000:F2}ms jitter-std={jitter * 1000:F2}ms");
        sb.AppendLine($"  text-changes={textChanges} ({textChanges / span.TotalSeconds:F1}/s)");
        sb.AppendLine($"  harness-handler={handlerUs:F1}us/frame ui-handler={uiUs:F1}us/frame process-cpu={procPct:F2}%");
        sb.AppendLine($"  {gate}");
        sb.Append($"rate={rate} | fps={fps:F1} | rawback2ms={rawBacksOver2ms} | maxdev={maxDev * 1000:F1}ms | " +
            $"tps={textChanges / span.TotalSeconds:F1}/s | ui={uiUs:F1}us | {(rawBacksOver2ms == 0 ? "OK" : "FAIL")}");
        return sb.ToString();
    }

    private static async Task<string> DriftAsync(Window window, AudioEngine audio)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---- drift 30s @1x ----");
        await OnUiAsync(window, () =>
        {
            audio.SetPlaybackRate(1.0);
            audio.SeekTo(0);
            audio.Play();
        });
        await Task.Delay(2000);
        var c0 = PositionClock.PublishCount;
        var (e0, p0) = await OnUiAsync(window, () => (PositionClock.ExtrapolateNow().TotalSeconds, (double)audio.DecodedFramePosition / 48000.0));
        await Task.Delay(TimeSpan.FromSeconds(30));
        var (e1, p1) = await OnUiAsync(window, () => (PositionClock.ExtrapolateNow().TotalSeconds, (double)audio.DecodedFramePosition / 48000.0));
        await OnUiAsync(window, () => audio.StopAndUnload());
        var c1 = PositionClock.PublishCount;
        var skewPpm = await OnUiAsync(window, () => PositionClock.SkewPpm);
        var drift = Math.Abs((e1 - e0) - (p1 - p0));
        sb.AppendLine($"  extrapolated-advance={e1 - e0:F3}s engine-advance={p1 - p0:F3}s drift={drift * 1000:F2}ms {(drift < 0.005 ? "OK" : "FAIL")}");
        sb.AppendLine($"  device-skew-estimate={skewPpm:F0}ppm");
        sb.Append($"drift30s={drift * 1000:F2}ms publishes-during-run={c1 - c0} | {(drift < 0.005 ? "OK" : "FAIL")}");
        return sb.ToString();
    }

    private static async Task<(long Frames, long Text, long Ticks)> ReadRenderStatsAsync(Window window)
    {
        var r = (Frames: 0L, Text: 0L, Ticks: 0L);
        await OnUiAsync(window, () => { r = (RenderStats.Frames, RenderStats.TextUpdates, RenderStats.HandlerTicks); });
        return r;
    }

    private static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
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

    private static Task<T> OnUiAsync<T>(Window window, Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }
}
