using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
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

    public long FramePosition => _framePosition;

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
    private TimeStretchProvider? _stretch;
    private readonly DispatcherQueueTimer _clock;
    private readonly DispatcherQueue _dispatcher;
    private double _pauseAtSeconds = double.NaN;
    private bool _disposed;
    private int _buildGen;
    private string _lastRenderDeviceId = "";
    private readonly MMDeviceEnumerator _devices;
    private readonly RenderDeviceWatcher _watcher;

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

    // Introspection for the --audio-test harness (and future diagnostics):
    // the device mix format seen at init, and the exact sample-provider
    // format handed to the output. "?" until the first BuildOutput.
    public string DeviceMixFormatDescription { get; private set; } = "?";
    public string LastOutputDescription { get; private set; } = "?";

    // Last device/output error that did not throw (background rebuilds).
    // Plain property on purpose: read any time, never blocks.
    public string LastOutputError { get; private set; } = "";

    public event Action? PositionChanged;
    public event Action? TrackEnded;

    public AudioEngine()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clock = _dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) => OnTick();
        _devices = new MMDeviceEnumerator();
        _watcher = new RenderDeviceWatcher(this);
        try
        {
            _lastRenderDeviceId = CurrentDefaultRenderId();
            _devices.RegisterEndpointNotificationCallback(_watcher);
        }
        catch { }
    }

    // Every public mutating call below runs its UI-touching body on the UI
    // thread (directly when already there). NAudio output calls are
    // thread-safe; XAML-bound observables and the DispatcherQueueTimer are
    // not. Touching them from a pool thread failed every audio open with an
    // empty-message RPC_E_WRONG_THREAD.
    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) action();
        else _ = _dispatcher.TryEnqueue(() => { try { action(); } catch { } });
    }

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        if (_disposed) { tcs.TrySetResult(); return tcs.Task; }
        if (_dispatcher.HasThreadAccess)
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            return tcs.Task;
        }
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("UI queue unavailable"));
        }
        return tcs.Task;
    }

    public bool IsAuditioning => !double.IsNaN(_pauseAtSeconds);

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        float[]? samples = null;
        TimeSpan duration = TimeSpan.Zero;
        Dictionary<string, string>? tags = null;
        await Task.Run(async () =>
        {
            StopInternal();
            var bins = FfmpegBins.Locate();
            if (bins is null)
                throw new InvalidOperationException(
                    $"Decode failed ({ext}): bundled FFmpeg is missing (expected ffmpeg.exe and " +
                    "ffprobe.exe in ffmpeg\\x64 or ffmpeg\\arm64 next to the app). " +
                    "No fallback decoder is available.");
            try
            {
                float[] decoded;
                (decoded, duration) = await FfmpegDecoder.DecodeAsync(bins, path, null, ct);
                samples = decoded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Decode failed ({ext}): {AudioErrorText.MessageOf(ex)}", ex);
            }
            try
            {
                var probe = await FfmpegDecoder.ProbeAsync(bins, path, ct);
                tags = new Dictionary<string, string>(probe.Tags, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            _pcm = new FfmpegPcmProvider(samples);
            _source = _pcm;
            try
            {
                BuildOutput(0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Output failed ({ext}): {AudioErrorText.MessageOf(ex)} " +
                    $"Device mix: {DeviceMixFormatDescription}. Tried: {LastOutputDescription}.", ex);
            }
        }, ct);
        // Observable state always lands on the UI thread (see RunOnUi).
        await RunOnUiAsync(() =>
        {
            DecodeBackend = "ffmpeg";
            if (tags is not null)
            {
                ProbeTags.Clear();
                foreach (var (k, v) in tags) ProbeTags[k] = v;
            }
            Duration = duration;
            Position = TimeSpan.Zero;
            HasAudio = true;
        });
    }

    public void Play()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(Play); return; }
        if (_output is null || _source is null || _disposed) return;
        ClearAudition();
        _output.Play();
        IsPlaying = true;
        _clock.Start();
    }

    public void Pause()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(Pause); return; }
        if (_output is null || _disposed) return;
        _output.Pause();
        IsPlaying = false;
        _clock.Stop();
        ClearAudition();
        RefreshPosition();
    }

    public void Seek(double seconds)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => Seek(seconds)); return; }
        if (_source is null || _disposed) return;
        ClearAudition();
        SeekWithoutClearing(seconds);
    }

    public void SetVolume(double volume)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetVolume(volume)); return; }
        Volume = Math.Max(0, Math.Min(1, volume));
        if (_output is not null) _output.Volume = (float)Volume;
    }

    public void SetPlaybackRate(double rate)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetPlaybackRate(rate)); return; }
        rate = Math.Max(0.1, Math.Min(4, rate));
        if (Math.Abs(rate - PlaybackRate) < 0.001) return;
        PlaybackRate = rate;
        // Live change straight into the stretch provider: no rebuild, no
        // gap, no click. The provider takes its own lock (device thread may
        // be pulling); tempo slews inside the engine.
        _stretch?.Tempo = rate;
    }

    public void AuditionRange(double startSeconds, double endSeconds)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => AuditionRange(startSeconds, endSeconds)); return; }
        if (_source is null || !HasAudio) return;
        if (endSeconds - startSeconds <= 0) return;
        _pauseAtSeconds = endSeconds;
        SeekWithoutClearing(startSeconds);
        Play();
        _pauseAtSeconds = endSeconds;
    }

    public void StopAndUnload()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(StopAndUnload); return; }
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
            if (_pcm is not null)
            {
                _pcm.CurrentTime = TimeSpan.FromSeconds(clamped);
                // Drop stretcher buffers and re-anchor its counter, or the
                // old tempo tail would smear across the seek point.
                _stretch?.Reset(_pcm.FramePosition);
            }
        }
        catch { }
        RefreshPosition();
    }

    private void ClearAudition() => _pauseAtSeconds = double.NaN;

    private sealed record MixTarget(int Rate, int Channels, int BitsPerSample, string Desc);

    private void BuildOutput(double positionSeconds)
    {
        if (_source is null) return;
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }
        var mix = QueryMixTarget();
        DeviceMixFormatDescription = mix.Desc;
        // Source (always stereo float from the FFmpeg path) runs through the
        // pitch-preserving time-stretch provider first (live tempo, bypassed
        // at 1x), then channel mapping and the device-rate resample. The
        // device never sees our internal format.
        ISampleProvider chain = _source;
        var stretch = new TimeStretchProvider(chain);
        stretch.Tempo = PlaybackRate;
        _stretch = stretch;
        chain = stretch;
        if (chain.WaveFormat.Channels != mix.Channels)
            chain = new ChannelMapSampleProvider(chain, mix.Channels);
        if (chain.WaveFormat.SampleRate != mix.Rate)
            chain = new WdlResamplingSampleProvider(chain, mix.Rate);
        var attempts = new List<string>();
        WasapiOut? output = null;
        if (mix.BitsPerSample == 32)
        {
            var target = WaveFormat.CreateIeeeFloatWaveFormat(mix.Rate, mix.Channels);
            try
            {
                output = InitOutput(new SampleToWaveProvider(chain), target);
                attempts.Add($"{DescribeWaveFormat(target)} OK");
            }
            catch (Exception ex)
            {
                attempts.Add($"{DescribeWaveFormat(target)} FAILED: {AudioErrorText.MessageOf(ex)}");
            }
        }
        if (output is null && mix.BitsPerSample >= 16)
        {
            var target = new WaveFormat(mix.Rate, mix.Channels);
            try
            {
                output = InitOutput(new SampleToWaveProvider16(chain), target);
                attempts.Add($"{DescribeWaveFormat(target)} OK");
            }
            catch (Exception ex)
            {
                attempts.Add($"{DescribeWaveFormat(target)} FAILED: {AudioErrorText.MessageOf(ex)}");
            }
        }
        if (output is null)
            throw new InvalidOperationException(
                $"audio device rejected every candidate format. Device mix: {mix.Desc}. " +
                $"Attempts: {string.Join(" | ", attempts)}");
        _output = output;
        _output.Volume = (float)Volume;
        _output.PlaybackStopped += OnPlaybackStopped;
        LastOutputDescription =
            $"in={DescribeSampleFormat(_source)} chain={DescribeSampleFormat(chain)} tried=[{string.Join(" | ", attempts)}]";
        SeekWithoutClearing(positionSeconds);
    }

    private static WasapiOut InitOutput(IWaveProvider provider, WaveFormat _)
    {
        WasapiOut output;
        try
        {
            output = new WasapiOut();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Output failed: no usable audio device (default render device missing or busy). " +
                AudioErrorText.MessageOf(ex), ex);
        }
        try
        {
            output.Init(provider);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static MixTarget QueryMixTarget()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            return new MixTarget(
                mix.SampleRate, mix.Channels, mix.BitsPerSample,
                $"\"{device.FriendlyName}\" {DescribeWaveFormat(mix)}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"could not read the default render device mix format: {AudioErrorText.MessageOf(ex)}", ex);
        }
    }

    private static string DescribeDefaultMixFormat()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return $"\"{device.FriendlyName}\" {DescribeWaveFormat(device.AudioClient.MixFormat)}";
        }
        catch (Exception ex)
        {
            return $"unreadable ({AudioErrorText.MessageOf(ex)})";
        }
    }

    private static string DescribeSampleFormat(ISampleProvider source) =>
        source is null ? "null" : DescribeWaveFormat(source.WaveFormat);

    private static string DescribeWaveFormat(NAudio.Wave.WaveFormat format) =>
        format is null
            ? "null"
            : $"{format.SampleRate}Hz/{format.Channels}ch/{format.Encoding}/{format.BitsPerSample}bit";

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio raises this on its own callback thread: never touch UI
        // state here directly.
        RunOnUi(() =>
        {
            if (_disposed) return;
            IsPlaying = false;
            _clock.Stop();
            ClearAudition();
            RefreshPosition();
        });
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
        TimeSpan pos;
        try
        {
            // Source-audio time, stretch-buffer latency accounted for inside
            // the provider (fed minus still-buffered frames). Falls back to
            // the raw PCM clock when no stretch stage exists yet.
            pos = _stretch is not null
                ? TimeSpan.FromSeconds(_stretch.ReportedSourceSeconds)
                : _pcm is not null ? _pcm.CurrentTime : Position;
        }
        catch
        {
            pos = Position;
        }
        // The read is thread-safe; the observable write and event are not.
        if (_dispatcher.HasThreadAccess)
        {
            Position = pos;
            PositionChanged?.Invoke();
        }
        else
        {
            RunOnUi(() =>
            {
                Position = pos;
                PositionChanged?.Invoke();
            });
        }
    }

    private void StopInternal()
    {
        // Output teardown is pool-safe; the clock and observable stay on UI.
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { }
            _output.Dispose();
            _output = null;
        }
        _pcm = null;
        _source = null;
        _stretch = null;
        ClearAudition();
        RunOnUi(() =>
        {
            _clock.Stop();
            IsPlaying = false;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _devices.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        try { _devices.Dispose(); } catch { }
        StopInternal();
    }

    private string CurrentDefaultRenderId()
    {
        using var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    // Default-device switches and unplug/replug arrive here (Bluetooth,
    // USB DACs). Rebuild the output against the NEW default at the current
    // position; never throw back into the OS callback.
    private void OnRenderDeviceMaybeChanged()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (_disposed || !HasAudio || _source is null) return;
                string id;
                try { id = CurrentDefaultRenderId(); }
                catch (Exception ex)
                {
                    LastOutputError = $"default render device unreadable: {AudioErrorText.MessageOf(ex)}";
                    return;
                }
                if (id == _lastRenderDeviceId) return;
                var gen = ++_buildGen;
                var resume = IsPlaying;
                var pos = Position.TotalSeconds;
                BuildOutput(pos);
                if (gen != _buildGen || _disposed) return;
                _lastRenderDeviceId = id;
                if (resume) RunOnUi(Play);
            }
            catch (Exception ex)
            {
                LastOutputError = AudioErrorText.MessageOf(ex);
            }
        });
    }

    private sealed class RenderDeviceWatcher : IMMNotificationClient
    {
        private readonly AudioEngine _owner;

        public RenderDeviceWatcher(AudioEngine owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render) _owner.OnRenderDeviceMaybeChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            _owner.OnRenderDeviceMaybeChanged();

        public void OnDeviceAdded(string deviceId) { }

        public void OnDeviceRemoved(string deviceId) => _owner.OnRenderDeviceMaybeChanged();

        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
