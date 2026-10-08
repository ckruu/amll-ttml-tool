using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AmllTtmlTool.WinUI.Services;

public sealed record AudioCover(byte[] Bytes, string Mime);

public sealed class FfmpegPcmProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _framePosition;

    public FfmpegPcmProvider(float[] interleavedStereo)
    {
        _samples = interleavedStereo;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(FfmpegDecoder.OutSampleRate, FfmpegDecoder.OutChannels);
    }

    public WaveFormat WaveFormat { get; }

    public long TotalFrames => _samples.Length / FfmpegDecoder.OutChannels;

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds((double)_framePosition / FfmpegDecoder.OutSampleRate);
        set => _framePosition = (int)Math.Max(0, Math.Min(
            value.TotalSeconds * FfmpegDecoder.OutSampleRate, TotalFrames));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var available = Math.Max(0, _samples.Length - _framePosition * FfmpegDecoder.OutChannels);
        var take = Math.Min(count, available);
        Array.Copy(_samples, _framePosition * FfmpegDecoder.OutChannels, buffer, offset, take);
        _framePosition += take / FfmpegDecoder.OutChannels;
        return take;
    }
}

public partial class AudioEngine : ObservableObject, IDisposable
{
    private IWavePlayer? _output;
    private ISampleProvider? _source;
    private FfmpegPcmProvider? _pcm;
    private MediaFoundationReader? _reader;
    private readonly DispatcherQueueTimer _clock;
    private double _pauseAtSeconds = double.NaN;
    private bool _disposed;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _hasAudio;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private double _volume = 0.5;

    [ObservableProperty]
    private double _playbackRate = 1.0;

    [ObservableProperty]
    private AudioCover? _cover;

    public Dictionary<string, string> ProbeTags { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string DecodeBackend { get; private set; } = "none";

    public event Action? PositionChanged;
    public event Action? TrackEnded;

    public AudioEngine()
    {
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) => OnTick();
    }

    public bool IsAuditioning => !double.IsNaN(_pauseAtSeconds);

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            StopInternal();
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var bins = FfmpegBins.Locate();
            Exception? ffmpegError = null;
            if (bins is not null)
            {
                try
                {
                    await LoadViaFfmpegAsync(bins, path, ct);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ffmpegError = ex;
                }
            }
            try
            {
                LoadViaMediaFoundation(path);
            }
            catch (Exception mfEx)
            {
                var detail = bins is null
                    ? "Bundled FFmpeg is missing and Media Foundation also failed. "
                    : $"FFmpeg failed ({ffmpegError?.Message}). Media Foundation fallback also failed. ";
                throw new InvalidOperationException(
                    $"Decode failed ({ext}): {detail}{mfEx.Message}", mfEx);
            }
        }, ct);
    }

    private async Task LoadViaFfmpegAsync(FfmpegBins bins, string path, CancellationToken ct)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        float[] samples;
        TimeSpan duration;
        try
        {
            (samples, duration) = await FfmpegDecoder.DecodeAsync(bins, path, null, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Decode failed ({ext}): {ex.Message}", ex);
        }
        try
        {
            var probe = await FfmpegDecoder.ProbeAsync(bins, path, ct);
            ProbeTags.Clear();
            foreach (var (k, v) in probe.Tags) ProbeTags[k] = v;
        }
        catch { }
        _pcm = new FfmpegPcmProvider(samples);
        _source = _pcm;
        DecodeBackend = "ffmpeg";
        BuildOutput(0);
        Duration = duration;
        Position = TimeSpan.Zero;
        HasAudio = true;
    }

    private void LoadViaMediaFoundation(string path)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        MediaFoundationReader reader;
        try
        {
            reader = new MediaFoundationReader(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Decode failed ({ext}): Media Foundation cannot decode this file " +
                $"(missing codec or Media Feature Pack). {ex.Message}", ex);
        }
        _reader = reader;
        _source = _reader.ToSampleProvider();
        DecodeBackend = "mediafoundation";
        try
        {
            BuildOutput(0);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Output failed ({ext}): audio device rejected the format. {ex.Message}", ex);
        }
        Duration = reader.TotalTime;
        Position = TimeSpan.Zero;
        HasAudio = true;
    }

    public void Play()
    {
        if (_output is null || _source is null || _disposed) return;
        ClearAudition();
        _output.Play();
        IsPlaying = true;
        _clock.Start();
    }

    public void Pause()
    {
        if (_output is null || _disposed) return;
        _output.Pause();
        IsPlaying = false;
        _clock.Stop();
        ClearAudition();
        RefreshPosition();
    }

    public void Seek(double seconds)
    {
        if (_source is null || _disposed) return;
        ClearAudition();
        SeekWithoutClearing(seconds);
    }

    public void SetVolume(double volume)
    {
        Volume = Math.Max(0, Math.Min(1, volume));
        if (_output is not null) _output.Volume = (float)Volume;
    }

    public void SetPlaybackRate(double rate)
    {
        rate = Math.Max(0.1, Math.Min(4, rate));
        if (Math.Abs(rate - PlaybackRate) < 0.001) return;
        var resume = IsPlaying;
        var pos = Position.TotalSeconds;
        _output?.Stop();
        PlaybackRate = rate;
        if (_source is not null)
        {
            BuildOutput(pos);
            if (resume) Play();
        }
    }

    public void AuditionRange(double startSeconds, double endSeconds)
    {
        if (_source is null || !HasAudio) return;
        if (endSeconds - startSeconds <= 0) return;
        _pauseAtSeconds = endSeconds;
        SeekWithoutClearing(startSeconds);
        Play();
        _pauseAtSeconds = endSeconds;
    }

    public void StopAndUnload()
    {
        StopInternal();
        HasAudio = false;
        IsPlaying = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        Cover = null;
        ProbeTags.Clear();
        DecodeBackend = "none";
        _clock.Stop();
    }

    private void SeekWithoutClearing(double seconds)
    {
        var clamped = Math.Max(0, Math.Min(seconds, Duration.TotalSeconds));
        try
        {
            if (_pcm is not null) _pcm.CurrentTime = TimeSpan.FromSeconds(clamped);
            else if (_reader is not null) _reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        }
        catch { }
        RefreshPosition();
    }

    private void ClearAudition() => _pauseAtSeconds = double.NaN;

    private void BuildOutput(double positionSeconds)
    {
        if (_source is null) return;
        _output?.Dispose();
        ISampleProvider source = _source;
        if (Math.Abs(PlaybackRate - 1.0) > 0.001)
        {
            var targetRate = Math.Max(8000, (int)(source.WaveFormat.SampleRate * PlaybackRate));
            source = new WdlResamplingSampleProvider(source, targetRate);
        }
        WasapiOut output;
        try
        {
            output = new WasapiOut();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Output failed: no usable audio device (default render device missing or busy). " + ex.Message, ex);
        }
        _output = output;
        _output.Volume = (float)Volume;
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(new SampleToWaveProvider(source));
        SeekWithoutClearing(positionSeconds);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        IsPlaying = false;
        _clock.Stop();
        ClearAudition();
        RefreshPosition();
    }

    private void OnTick()
    {
        if (_source is null) return;
        RefreshPosition();
        if (!double.IsNaN(_pauseAtSeconds) && Position.TotalSeconds >= _pauseAtSeconds)
        {
            Pause();
            TrackEnded?.Invoke();
            return;
        }
        if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(200) && IsPlaying)
        {
            Pause();
            TrackEnded?.Invoke();
            return;
        }
        PositionChanged?.Invoke();
    }

    private void RefreshPosition()
    {
        try
        {
            if (_pcm is not null) Position = _pcm.CurrentTime;
            else if (_reader is not null) Position = _reader.CurrentTime;
        }
        catch { }
        PositionChanged?.Invoke();
    }

    private void StopInternal()
    {
        _clock.Stop();
        IsPlaying = false;
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { }
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _pcm = null;
        _source = null;
        ClearAudition();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
    }
}
