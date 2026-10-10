// --audio-test <folder> [reportPath] harness: drives the real AudioEngine
// path for every audio file (probe, full decode, LoadAsync incl. device
// init, silent volume-0 playback, seeks) and records per-stage results with
// full error detail. Self-closing with a hard watchdog. DEBUG-only entry.
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Text;

namespace AmllTtmlTool.WinUI.Services;

internal static class AudioTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audio-test-report.txt");

    private const int First2SecondsBytes = 2 * FfmpegDecoder.OutSampleRate * FfmpegDecoder.OutChannels * 4;

    public static async Task RunAsync(Window window, AudioEngine audio, string folder, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(20), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: audio-test exceeded 20 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== AUDIO TEST ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            await WriteEnvironmentAsync(report);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Write(report, $"FOLDER-ERROR: not found: {folder}");
                return;
            }
            var files = Directory.GetFiles(folder)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Write(report, $"files={files.Length}");
            var table = new List<string>();
            foreach (var file in files)
            {
                string row;
                try { row = await TestFileAsync(window, audio, file); }
                catch (Exception ex) { row = $"file={Name(file)} | HARNESS-ERROR {AudioErrorText.MessageOf(ex)}"; }
                Write(report, row);
                table.Add(SummaryRow(file, row));
            }
            Write(report, "=== TABLE (file | decoder | format | result) ===");
            foreach (var r in table) Write(report, r);
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static async Task WriteEnvironmentAsync(string report)
    {
        var bins = FfmpegBins.Locate();
        if (bins is null)
        {
            Write(report, "ffmpeg=bins MISSING (ffmpeg/ + arch folder with ffmpeg.exe+ffprobe.exe not found)");
        }
        else
        {
            Write(report, $"ffmpeg={bins.Ffmpeg}");
            Write(report, $"ffprobe={bins.Ffprobe}");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = bins.Ffmpeg,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) { Write(report, "ffmpeg-version=COULD-NOT-START"); }
                else
                {
                    var first = await p.StandardOutput.ReadLineAsync() ?? "";
                    await p.WaitForExitAsync();
                    Write(report, $"ffmpeg-version={first.Trim()}");
                    var cfg = await ConfigLineAsync(bins.Ffmpeg);
                    var gpl = cfg.Contains("--enable-gpl") ? "YES" : "no";
                    var nonfree = cfg.Contains("--enable-nonfree") ? "YES" : "no";
                    Write(report, $"ffmpeg-gpl={gpl} ffmpeg-nonfree={nonfree}");
                }
            }
            catch (Exception ex) { Write(report, $"ffmpeg-version=ERROR {AudioErrorText.MessageOf(ex)}"); }
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    using (dev)
                    {
                        var mix = dev.AudioClient.MixFormat;
                        Write(report, $"device=\"{dev.FriendlyName}\" state={dev.State} mix={mix.SampleRate}Hz/{mix.Channels}ch/{mix.Encoding}/{mix.BitsPerSample}bit");
                    }
                }
                catch (Exception ex) { Write(report, $"device=UNREADABLE {AudioErrorText.MessageOf(ex)}"); }
            }
        }
        catch (Exception ex) { Write(report, $"device-enumeration=ERROR {AudioErrorText.MessageOf(ex)}"); }
    }

    private static async Task<string> ConfigLineAsync(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var full = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in full.Split('\n'))
                if (line.TrimStart().StartsWith("configuration:")) return line.Trim();
        }
        catch { }
        return "";
    }

    private static async Task<string> TestFileAsync(Window window, AudioEngine audio, string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"---- file={Name(file)} ({new FileInfo(file).Length} bytes) ----");
        var bins = FfmpegBins.Locate();
        sb.AppendLine($"  bins={(bins is null ? "MISSING" : bins.Ffmpeg)}");
        FfmpegProbeResult? probe = null;
        if (bins is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                probe = await FfmpegDecoder.ProbeAsync(bins, file, cts.Token);
                sb.AppendLine($"  probe codec={probe.CodecName ?? "?"} rate={probe.SampleRate} ch={probe.Channels} bits={probe.BitsPerSample} dur={probe.DurationSeconds:F3}s");
            }
            catch (Exception ex) { sb.AppendLine("  probe FAILED:\n" + Indent(AudioErrorText.FullReport(ex))); }
        }
        else sb.AppendLine("  probe SKIPPED (no bins)");

        long totalBytes = -1;
        if (bins is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                var (samples, dur) = await FfmpegDecoder.DecodeAsync(bins, file, null, cts.Token);
                totalBytes = (long)samples.Length * 4;
                var first2 = Math.Min(totalBytes, (long)First2SecondsBytes);
                var probeDur = probe is null ? "?" : probe.DurationSeconds.ToString("F3");
                sb.AppendLine($"  decode bytes={totalBytes} first2s-bytes={first2} dur={dur.TotalSeconds:F3}s probe-dur={probeDur}s");
                if (probe is not null && Math.Abs(dur.TotalSeconds - probe.DurationSeconds) > 1.0)
                    sb.AppendLine("  decode NOTE: decoded duration differs from probe by >1s");
            }
            catch (Exception ex) { sb.AppendLine("  decode FAILED:\n" + Indent(AudioErrorText.FullReport(ex))); }
        }

        string backend = "?";
        var loaded = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await audio.LoadAsync(file, cts.Token);
            backend = audio.DecodeBackend;
            loaded = true;
            sb.AppendLine($"  load OK backend={backend} duration={audio.Duration.TotalSeconds:F3}s mix=({audio.DeviceMixFormatDescription}) out=({audio.LastOutputDescription})");
        }
        catch (Exception ex) { sb.AppendLine("  load FAILED:\n" + Indent(AudioErrorText.FullReport(ex))); }

        if (loaded)
        {
            try
            {
                // Silent playback on the UI thread: the real output path,
                // volume 0, position must advance.
                await OnUiAsync(window, () => audio.SetVolume(0));
                var pos0 = await OnUiAsync(window, () => { audio.Play(); return audio.Position; });
                await Task.Delay(2000);
                var pos1 = await OnUiAsync(window, () => audio.Position);
                await OnUiAsync(window, () => audio.Pause());
                var advance = (pos1 - pos0).TotalSeconds;
                sb.AppendLine($"  play2s advance={advance:F3}s {(advance >= 0.8 && advance <= 3.0 ? "OK" : "FAIL")}");
                var dur = audio.Duration.TotalSeconds;
                if (dur > 0.5)
                {
                    foreach (var frac in new[] { 0.25, 0.5, 0.75 })
                    {
                        var want = dur * frac;
                        var got = await OnUiAsync(window, () => { audio.Seek(want); return audio.Position.TotalSeconds; });
                        var err = Math.Abs(got - want) * 1000;
                        sb.AppendLine($"  seek {frac:P0} want={want:F3}s got={got:F3}s err={err:F0}ms {(err <= 50 ? "OK" : "FAIL")}");
                    }
                }
                else sb.AppendLine("  seek SKIPPED (duration too short)");
            }
            catch (Exception ex) { sb.AppendLine("  play/seek FAILED:\n" + Indent(AudioErrorText.FullReport(ex))); }
            finally
            {
                try { await OnUiAsync(window, () => audio.StopAndUnload()); } catch { }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string SummaryRow(string file, string row)
    {
        string Pick(string marker)
        {
            foreach (var line in row.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith(marker)) return t[(marker.Length)..].Trim();
            }
            return "?";
        }
        var probe = Pick("probe codec=");
        var backendLine = row.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("load OK"))?.Trim() ?? "?";
        var result = row.Contains("FAILED") || row.Contains("HARNESS-ERROR") || row.Contains("FAIL")
            ? "FAIL" : "OK";
        return $"file={Name(file)} | {backendLine} | probe[{probe}] | {result}";
    }

    private static string Name(string path) => Path.GetFileName(path);
    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "    " + l));

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
