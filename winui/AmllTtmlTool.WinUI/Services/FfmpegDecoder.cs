using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AmllTtmlTool.WinUI.Services;

public sealed record FfmpegProbeResult(
    double DurationSeconds,
    Dictionary<string, string> Tags,
    string? CodecName,
    int SampleRate,
    int Channels);

public sealed record FfmpegBins(string Ffmpeg, string Ffprobe)
{
    public static FfmpegBins? Locate()
    {
        try
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            if (arch is null) return null;
            var dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg", arch);
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) return null;
            return new FfmpegBins(ffmpeg, ffprobe);
        }
        catch
        {
            return null;
        }
    }
}

public static class FfmpegDecoder
{
    public const int OutSampleRate = 48000;
    public const int OutChannels = 2;
    public const long MaxPcmBytes = 500L * 1024 * 1024;

    public static async Task<FfmpegProbeResult> ProbeAsync(
        FfmpegBins bins, string path, CancellationToken ct = default)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{path}\"";
        var (exit, stdout, stderr) = await RunAsync(bins.Ffprobe, args, null, ct);
        if (exit != 0)
            throw new InvalidOperationException($"Probe failed ({Path.GetExtension(path).TrimStart('.').ToLowerInvariant()}): {FirstLine(stderr)}");
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : (JsonElement?)null;
        var duration = 0.0;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format is not null)
        {
            if (format.Value.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dur))
            {
                duration = dur;
            }
            if (format.Value.TryGetProperty("tags", out var ft))
            {
                foreach (var prop in ft.EnumerateObject())
                    tags[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        string? codec = null;
        var rate = 0;
        var channels = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio")
                {
                    if (s.TryGetProperty("codec_name", out var c)) codec = c.GetString();
                    if (s.TryGetProperty("sample_rate", out var sr) &&
                        int.TryParse(sr.GetString(), out var r)) rate = r;
                    if (s.TryGetProperty("channels", out var ch)) channels = ch.GetInt32();
                    if (s.TryGetProperty("tags", out var st))
                    {
                        foreach (var prop in st.EnumerateObject())
                            tags.TryAdd(prop.Name, prop.Value.GetString() ?? string.Empty);
                    }
                    break;
                }
            }
        }
        return new FfmpegProbeResult(duration, tags, codec, rate, channels);
    }

    public static async Task<(float[] Samples, TimeSpan Duration)> DecodeAsync(
        FfmpegBins bins,
        string path,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var args = $"-v error -i \"{path}\" -map 0:a -f f32le -ac {OutChannels} -ar {OutSampleRate} -";
        using var process = Start(bins.Ffmpeg, args);
        var pcm = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Decode failed ({Path.GetExtension(path).TrimStart('.').ToLowerInvariant()}): {FirstLine(stderr)}");
        }
        var bytes = pcm.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("Decode produced no audio (no audio stream?).");
        if (bytes.Length > MaxPcmBytes)
            throw new InvalidOperationException("Decoded audio exceeds 500 MB; file too long.");
        if (bytes.Length % 4 != 0)
            throw new InvalidOperationException("Decoded audio has incomplete samples.");
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        var frames = samples.Length / OutChannels;
        progress?.Report(1.0);
        return (samples, TimeSpan.FromSeconds((double)frames / OutSampleRate));
    }

    public static async Task<byte[]?> ExtractCoverAsync(FfmpegBins bins, string path, CancellationToken ct = default)
    {
        var args = $"-v error -i \"{path}\" -map 0:v -frames:v 1 -f image2pipe -vcodec mjpeg -";
        var (exit, _, _) = (0, string.Empty, string.Empty);
        using var process = Start(bins.Ffmpeg, args);
        var ms = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        exit = process.ExitCode;
        if (exit != 0 || ms.Length < 128) return null;
        var bytes = ms.ToArray();
        if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        return bytes;
    }

    private static Process Start(string exe, string args) => Process.Start(new ProcessStartInfo
    {
        FileName = exe,
        Arguments = args,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
    }) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string exe, string args, byte[]? stdin, CancellationToken ct)
    {
        using var process = Start(exe, args);
        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, ct);
            process.StandardInput.Close();
        }
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout, stderr);
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t.Length <= 300 ? t : t[..300];
        }
        return "unknown error";
    }
}
