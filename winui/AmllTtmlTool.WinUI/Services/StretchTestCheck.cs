// --stretch-test [reportPath] harness: offline stretch verification, no
// audio device needed. Processes a 440 Hz sine through the same
// TimeStretchProvider the playback graph uses, in both modes: pitch
// preserved (output length must match the rate within 2%, dominant
// frequency must stay 440 Hz within 2%) and varispeed (pitch must follow
// the rate: 440*rate within 3%, length 1/rate within 2%). Reported source
// position must equal the processed source samples in both. Plus a
// 60-second CPU benchmark per rate. Self-closing.
using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

internal static class StretchTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stretch-test-report.txt");

    private const int Rate = 48000;
    private const int Channels = 2;
    private const double ToneHz = 440.0;

    public static async Task RunAsync(Window window, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: stretch-test exceeded 10 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== STRETCH TEST (SoundTouch.Net managed port) ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            await Task.Run(() => RunAll(report));
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static void RunAll(string report)
    {
        var sine3 = Sine(3);
        var table = new List<string>();
        foreach (var keepPitch in new[] { true, false })
        {
            foreach (var rate in new[] { 0.5, 0.75, 1.5, 2.0 })
            {
                string row;
                try { row = CheckRate(sine3, rate, keepPitch); }
                catch (Exception ex) { row = $"keepPitch={keepPitch} rate={rate} HARNESS-ERROR {AudioErrorText.MessageOf(ex)}"; }
                Write(report, row);
                table.Add(row.Split('\n').Last());
            }
        }
        try
        {
            var sine60 = Sine(60);
            foreach (var rate in new[] { 0.5, 1.0, 2.0 })
                Write(report, Benchmark(sine60, rate));
        }
        catch (Exception ex) { Write(report, $"benchmark HARNESS-ERROR {AudioErrorText.MessageOf(ex)}"); }
        Write(report, "=== TABLE (mode | rate | length | pitch | position) ===");
        foreach (var r in table) Write(report, r);
    }

    private static string CheckRate(float[] sine, double rate, bool keepPitch)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"---- keepPitch={keepPitch} rate={rate} ----");
        var frames = sine.Length / Channels;
        var provider = new TimeStretchProvider(new ArraySample(sine, Rate, Channels));
        provider.PreservePitch = keepPitch;
        provider.Tempo = rate;
        var output = new List<float>(sine.Length);
        var chunk = new float[4096 * Channels];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            output.AddRange(new ArraySegment<float>(chunk, 0, read));
        var outFrames = output.Count / Channels;
        var expectedRatio = 1.0 / rate;
        var gotRatio = (double)outFrames / frames;
        var lenErr = Math.Abs(gotRatio / expectedRatio - 1.0);
        sb.AppendLine($"  length in={frames} out={outFrames} ratio-err={lenErr:P2} {(lenErr <= 0.02 ? "OK" : "FAIL")}");
        var peak = PeakFreq(output.ToArray(), outFrames);
        // Varispeed moves pitch with the rate (tape-style); the preserved
        // path must hold 440 Hz. Varispeed gets a wider band (3%): linear
        // interpolation + DFT bin width at low frequencies.
        var wantHz = keepPitch ? ToneHz : ToneHz * rate;
        var pitchTol = keepPitch ? 0.02 : 0.03;
        var freqErr = Math.Abs(peak - wantHz) / wantHz;
        sb.AppendLine($"  pitch peak={peak:F1}Hz want={wantHz:F1}Hz err={freqErr:P2} {(freqErr <= pitchTol ? "OK" : "FAIL")}");
        var fedOk = keepPitch
            ? provider.SourceFramesFed == frames
            : Math.Abs(provider.SourceFramesFed - frames) <= 1; // fractional tail
        sb.AppendLine($"  fed={provider.SourceFramesFed} expected={frames} {(fedOk ? "OK" : "FAIL")}");
        var reported = provider.ReportedSourceSeconds;
        var posErr = Math.Abs(reported - (double)frames / Rate);
        sb.AppendLine($"  position reported={reported:F3}s expected={frames / (double)Rate:F3}s err={posErr * 1000:F1}ms {(posErr <= 0.001 ? "OK" : "FAIL")}");
        // Seek-reset: re-anchor mid-stream, position must follow exactly.
        var provider2 = new TimeStretchProvider(new ArraySample(sine, Rate, Channels));
        provider2.PreservePitch = keepPitch;
        provider2.Tempo = rate;
        var tmp = new float[4096 * Channels];
        var want = frames / 2;
        var pulled = 0;
        while (pulled < want)
            pulled += provider2.Read(tmp, 0, tmp.Length) / Channels;
        provider2.Reset(frames / 4);
        var resetOk = Math.Abs(provider2.ReportedSourceSeconds - (frames / 4.0) / Rate) <= 0.001;
        sb.AppendLine($"  seek-reset reported={provider2.ReportedSourceSeconds:F3}s {(resetOk ? "OK" : "FAIL")}");
        var pass = lenErr <= 0.02 && freqErr <= pitchTol && fedOk && posErr <= 0.001 && resetOk;
        sb.Append($"keepPitch={keepPitch} rate={rate} | length-err={lenErr:P2} | pitch-err={freqErr:P2} | position | {(pass ? "OK" : "FAIL")}");
        return sb.ToString();
    }

    private static string Benchmark(float[] sine, double rate)
    {
        var frames = sine.Length / Channels;
        var provider = new TimeStretchProvider(new ArraySample(sine, Rate, Channels));
        provider.Tempo = rate;
        var chunk = new float[4096 * Channels];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long outFrames = 0;
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            outFrames += read / Channels;
        sw.Stop();
        var audioMs = outFrames / (double)Rate * 1000.0;
        var pct = sw.Elapsed.TotalMilliseconds / audioMs * 100.0;
        return $"bench rate={rate} cpu-ms={sw.ElapsedMilliseconds} audio-ms={audioMs:F0} realtime={pct:F2}%";
    }

    private static float[] Sine(int seconds)
    {
        var n = Rate * seconds;
        var s = new float[n * Channels];
        for (var i = 0; i < n; i++)
        {
            var v = (float)Math.Sin(2 * Math.PI * ToneHz * i / Rate);
            s[i * Channels] = v;
            s[i * Channels + 1] = v;
        }
        return s;
    }

    private static double PeakFreq(float[] stereo, int frames)
    {
        const int N = 8192;
        var start = Math.Max(0, frames / 2 - N / 2) * Channels;
        var avail = Math.Min(N, frames - (frames / 2 - N / 2));
        if (avail < N / 2) return double.NaN;
        double best = 0;
        var bestBin = 0;
        for (var k = 1; k < N / 2; k++)
        {
            double re = 0, im = 0;
            for (var n = 0; n < avail; n++)
            {
                var v = stereo[start + n * Channels];
                var a = 2 * Math.PI * k * n / N;
                re += v * Math.Cos(a);
                im -= v * Math.Sin(a);
            }
            var mag = re * re + im * im;
            if (mag > best) { best = mag; bestBin = k; }
        }
        return bestBin * (double)Rate / N;
    }

    private sealed class ArraySample : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;

        public WaveFormat WaveFormat { get; }

        public ArraySample(float[] data, int rate, int channels)
        {
            _data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var take = Math.Min(count, _data.Length - _pos);
            if (take <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
    }

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }
}
