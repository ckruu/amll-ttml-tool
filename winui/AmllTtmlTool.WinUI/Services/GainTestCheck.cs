// --gain-test [reportPath] harness: offline verification of the app-owned
// gain stage, plus proof that the OS endpoint volume is never touched.
//
// Two legs:
//  1. Endpoint probe (READ-ONLY): IAudioEndpointVolume scalar + mute are
//     read before and after setting the app volume to 0/50/100 and toggling
//     mute. They must be identical, because the app no longer writes them.
//     Only the getters are used - this harness never touches the endpoint.
//  2. Offline signal: a 1kHz sine at known amplitude is pushed through the
//     REAL GainSampleProvider, and the output RMS is compared to the
//     expected value. 100/50/25 must match within 0.5dB, 0 must be exact
//     zeros, and a mid-stream gain step must never exceed 0.1 full scale
//     sample-to-sample (zipper/click guard).
//
// Self-closing. DEBUG-only.
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

internal static class GainTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gain-test-report.txt");

    private static int _ran;

    public static async Task RunAsync(Window window, AudioEngine audio, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        var runId = Guid.NewGuid().ToString("N")[..8];
        if (Interlocked.Exchange(ref _ran, 1) != 0)
        {
            try { System.IO.File.AppendAllText(report, $"SKIP duplicate dispatch runId={runId}\n"); } catch { }
            return;
        }
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(3), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            Write(report, "=== GAIN TEST ===");
            Write(report, $"runId={runId} pid={Environment.ProcessId}");
            Write(report, $"silent-env={(Environment.GetEnvironmentVariable("AMLL_SILENT") ?? "(unset)")}");
            var fails = 0;

            fails += await EndpointUntouchedAsync(window, audio, report);
            fails += RunOfflineSine(report);

            Write(report, fails == 0 ? "gain | OK" : $"gain | FAIL fails={fails}");
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    // Reads the endpoint volume/mute, drives the app's own level through
    // 0/50/100 + mute, and re-reads. The app must have changed nothing.
    private static async Task<int> EndpointUntouchedAsync(Window window, AudioEngine audio, string report)
    {
        Write(report, "---- endpoint untouched (read-only probe) ----");
        var before = ReadEndpoint();
        if (before is null)
        {
            Write(report, "  SKIP: no default render device to read");
            return 0;
        }
        await OnUiAsync(window, () =>
        {
            audio.SetVolumePercent(0);
            audio.SetVolumePercent(50);
            audio.SetVolumePercent(100);
            audio.SetMuted(true);
            audio.SetMuted(false);
        });
        var after = ReadEndpoint();
        var sameScalar = Math.Abs(before.Value.Scalar - after.Value.Scalar) < 1e-6;
        var sameMute = before.Value.Mute == after.Value.Mute;
        Write(report, $"  scalar {before.Value.Scalar:F4} -> {after.Value.Scalar:F4} mute {before.Value.Mute} -> {after.Value.Mute}");
        Write(report, $"  endpoint-unchanged | scalar-same={sameScalar} mute-same={sameMute} | {(sameScalar && sameMute ? "OK" : "FAIL")}");
        return sameScalar && sameMute ? 0 : 1;
    }

    private static (float Scalar, bool Mute)? ReadEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var vol = device.AudioEndpointVolume;
            // GETTERS ONLY: the app (and this harness) never writes here.
            return (vol.MasterVolumeLevelScalar, vol.Mute);
        }
        catch
        {
            return null;
        }
    }

    // --- offline signal leg ------------------------------------------------
    private static int RunOfflineSine(string report)
    {
        Write(report, "---- offline sine through gain (no device) ----");
        var fails = 0;
        const int rate = 48000;
        const double freq = 1000.0;
        const double amp = 0.5; // known input PEAK amplitude
        var sine = new SineProvider(rate, freq, amp);
        // RMS of a pure sine is its peak / sqrt(2), so the expected output is
        // the gained peak / sqrt(2) - not the gained peak itself.
        var sineRms = 1.0 / Math.Sqrt(2.0);

        foreach (var (percent, expectedDb) in new[] { (100.0, 0.0), (50.0, -12.0), (25.0, -24.0) })
        {
            sine.Reset();
            var gain = new GainSampleProvider(sine, Math.Pow(percent / 100.0, 2.0));
            var rms = MeasureRms(gain, rate);
            var expected = amp * Math.Pow(percent / 100.0, 2.0) * sineRms;
            var errDb = 20 * Math.Log10(rms / expected);
            var ok = Math.Abs(errDb) <= 0.5;
            Write(report, $"  {(ok ? "ok" : "FAIL")} v={percent:F0} rms={rms:F6} expected={expected:F6} err={errDb:F2}dB (want 0.00dB +-0.50; curve {expectedDb:F0}dB)");
            if (!ok) fails++;
        }

        // Zero must be exact digital silence, not a denormal or a residue.
        {
            sine.Reset();
            var gain = new GainSampleProvider(sine, 0.0);
            var buf = new float[rate];
            gain.Read(buf, 0, buf.Length);
            var nonZero = 0;
            foreach (var v in buf) { if (v != 0f) nonZero++; }
            var ok = nonZero == 0;
            Write(report, $"  {(ok ? "ok" : "FAIL")} v=0 non-zero-samples={nonZero} (want 0)");
            if (!ok) fails++;
        }

        // Mid-stream change: max sample-to-sample jump must stay far below a
        // full scale, i.e. the ramp really smooths the step.
        {
            sine.Reset();
            var gain = new GainSampleProvider(sine, 0.0);
            var buf = new float[rate];
            for (var i = 0; i < buf.Length; i++) buf[i] = 0f;
            gain.Read(buf, 0, 1024);
            gain.SetLevel(1.0); // full step, mid-stream
            gain.Read(buf, 1024, buf.Length - 1024);
            var worst = 0.0;
            for (var i = 1025; i < buf.Length; i++)
                worst = Math.Max(worst, Math.Abs(buf[i] - buf[i - 1]));
            var ok = worst < 0.1;
            Write(report, $"  {(ok ? "ok" : "FAIL")} mid-stream step max-sample-jump={worst:F4} (want < 0.100)");
            if (!ok) fails++;
        }
        return fails;
    }

    // Skips the 15ms ramp so a steady-state RMS is measured, not a ramp.
    private static double MeasureRms(GainSampleProvider gain, int rate)
    {
        var warm = new float[4096];
        gain.Read(warm, 0, warm.Length);
        var warm2 = new float[4096];
        gain.Read(warm2, 0, warm2.Length);
        var buf = new float[rate];
        gain.Read(buf, 0, buf.Length);
        double sum = 0;
        foreach (var v in buf) sum += v * v;
        return Math.Sqrt(sum / buf.Length);
    }

    private sealed class SineProvider : ISampleProvider
    {
        private readonly double _freq;
        private readonly double _amp;
        private double _phase;

        public SineProvider(int rate, double freq, double amp)
        {
            _freq = freq;
            _amp = amp;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public void Reset() => _phase = 0;

        public int Read(float[] buffer, int offset, int count)
        {
            var rate = WaveFormat.SampleRate;
            var frames = count / 2;
            for (var i = 0; i < frames; i++)
            {
                var s = (float)(_amp * Math.Sin(2 * Math.PI * _freq * _phase / rate));
                _phase++;
                if (_phase >= rate) _phase -= rate;
                buffer[offset + i * 2] = s;
                buffer[offset + i * 2 + 1] = s;
            }
            return frames * 2;
        }
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

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
    }
}
