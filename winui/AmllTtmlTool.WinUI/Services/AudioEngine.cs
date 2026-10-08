using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AmllTtmlTool.WinUI.Services;

public sealed record AudioCover(byte[] Bytes, string Mime);

public partial class AudioEngine : ObservableObject, IDisposable
{
    private IWavePlayer? _output;
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
        await Task.Run(() =>
        {
            StopInternal();
            try
            {
                _reader = new MediaFoundationReader(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Decode failed ({System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant()}): {ex.Message}", ex);
            }
            BuildOutput(0);
            Duration = _reader.TotalTime;
            Position = TimeSpan.Zero;
            HasAudio = true;
        }, ct);
    }

    public void Play()
    {
        if (_output is null || _reader is null || _disposed) return;
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
        if (_reader is null || _disposed) return;
        ClearAudition();
        var clamped = Math.Max(0, Math.Min(seconds, _reader.TotalTime.TotalSeconds));
        _reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        RefreshPosition();
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
        if (_reader is not null)
        {
            BuildOutput(pos);
            if (resume) Play();
        }
    }

    public void AuditionRange(double startSeconds, double endSeconds)
    {
        if (_reader is null || !HasAudio) return;
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
        _clock.Stop();
    }

    private void SeekWithoutClearing(double seconds)
    {
        if (_reader is null) return;
        var clamped = Math.Max(0, Math.Min(seconds, _reader.TotalTime.TotalSeconds));
        _reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        RefreshPosition();
    }

    private void ClearAudition() => _pauseAtSeconds = double.NaN;

    private void BuildOutput(double positionSeconds)
    {
        _output?.Dispose();
        if (_reader is null) return;
        ISampleProvider source = _reader.ToSampleProvider();
        if (Math.Abs(PlaybackRate - 1.0) > 0.001)
        {
            var targetRate = Math.Max(8000, (int)(source.WaveFormat.SampleRate * PlaybackRate));
            source = new WdlResamplingSampleProvider(source, targetRate);
        }
        _output = new WasapiOut();
        _output.Volume = (float)Volume;
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(new SampleToWaveProvider(source));
        _reader.CurrentTime = TimeSpan.FromSeconds(
            Math.Max(0, Math.Min(positionSeconds, _reader.TotalTime.TotalSeconds)));
        RefreshPosition();
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
        if (_reader is null) return;
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
        if (_reader is not null)
        {
            try { Position = _reader.CurrentTime; } catch { }
        }
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
        ClearAudition();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
    }
}
