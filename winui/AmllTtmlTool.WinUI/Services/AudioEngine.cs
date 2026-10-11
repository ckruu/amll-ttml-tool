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
    private readonly object _posGate = new();

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
        get { lock (_posGate) return TimeSpan.FromSeconds((double)_framePosition / FfmpegDecoder.OutSampleRate); }
        set { lock (_posGate) _framePosition = (int)Math.Max(0, Math.Min(value.TotalSeconds * FfmpegDecoder.OutSampleRate, TotalFrames)); }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Position advance is locked: the device thread pulls here while the
        // UI thread sets CurrentTime on seeks. The sample copy itself runs
        // outside the lock (immutable array).
        // NOTE: never Array.Copy here. NAudio's SampleToWaveProvider hands us
        // a WaveBuffer-punned array: a byte[] laundered through overlapping
        // reference fields and viewed as float[]. Indexing reinterprets the
        // bytes correctly, and Buffer.BlockCopy (byte-level, primitive types
        // only) works — but Array.Copy's strict type check throws
        // ArrayTypeMismatchException on it. This only detonates when no
        // resampler/channel-map sits between us and the converter (i.e.
        // exactly when the device mix matches our format), which is why it
        // hid behind passing tests on a 192 kHz device.
        int start;
        int take;
        lock (_posGate)
        {
            var available = Math.Max(0, _samples.Length - _framePosition * FfmpegDecoder.OutChannels);
            take = Math.Min(count, available);
            start = _framePosition * FfmpegDecoder.OutChannels;
            _framePosition += take / FfmpegDecoder.OutChannels;
        }
        Buffer.BlockCopy(_samples, start * 4, buffer, offset * 4, take * 4);
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
    // Dedicated snapshot publisher. A System.Threading.Timer fires on the
    // thread pool, NOT the UI dispatcher, so a busy UI cannot delay the
    // anchors the display extrapolates from.
    private readonly System.Threading.Timer _publishTimer;
    private readonly DispatcherQueue _dispatcher;
    private double _pauseAtSeconds = double.NaN;
    private bool _disposed;
    private int _buildGen;
    private long _seekGen;
    private bool _userPaused;
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

    // Playback level 0..1 as a linear amplitude, derived from the 0-100
    // slider position with the perceptual curve amplitude=(v/100)^2 (see
    // GainSampleProvider). Never touches the OS endpoint volume.
    private double _gainLevel = 0.5;
    private GainSampleProvider? _gain;

    // 0-100 slider position the engine keeps for round-tripping (the curve is
    // applied on the way in, and the UI reads this back for its own slider).
    private double _volumePercent = 50;
    private bool _muted;
    private double _preMutePercent = 50;

    /// <summary>Current slider position 0-100 (survives mute).</summary>
    public double VolumePercent => _volumePercent;

    /// <summary>True while output is silent by user choice.</summary>
    public bool IsMuted => _muted;

    // Forced silence for test harnesses: an IN-APP gain override, so a
    // harness never writes the OS volume and never persists a setting.
    public bool SilentMode
    {
        get => _silentMode;
        set
        {
            if (_silentMode == value) return;
            _silentMode = value;
            if (_dispatcher.HasThreadAccess) RecomputeGain();
            else RunOnUi(RecomputeGain);
        }
    }

    private bool _silentMode;

    // Single place the amplitude is derived. Muted/harness-silent are a
    // target of exactly 0 (true digital silence); everything else follows the
    // slider curve amplitude=(percent/100)^2. `_gainLevel` is what a freshly
    // built output starts at, so a rebuild mid-ramp cannot pop.
    private void RecomputeGain()
    {
        var amplitude = _muted || _silentMode
            ? 0.0
            : Math.Pow(Math.Max(0, _volumePercent) / 100.0, 2.0);
        _gainLevel = amplitude;
        _gain?.SetLevel(amplitude);
    }

    /// <summary>
    /// Sets playback level from a 0-100 slider position. App-only: the OS
    /// endpoint volume is never read or written.
    /// </summary>
    public void SetVolumePercent(double percent)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetVolumePercent(percent)); return; }
        _volumePercent = Math.Clamp(percent, 0, 100);
        if (_volumePercent > 0) _muted = false;
        RecomputeGain();
    }

    // Re-applies the remembered level to the current output. Called after
    // every load and import: the level belongs to the app, not to a track,
    // and a new file must never reset it.
    public void ReassertVolume()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(RecomputeGain); return; }
        RecomputeGain();
    }

    public void SetMuted(bool muted)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetMuted(muted)); return; }
        if (muted)
        {
            if (_volumePercent > 0) _preMutePercent = _volumePercent;
            _muted = true;
        }
        else
        {
            _muted = false;
            if (_volumePercent == 0 && _preMutePercent > 0) _volumePercent = _preMutePercent;
        }
        RecomputeGain();
    }

    [ObservableProperty]
    private double _playbackRate = 1.0;

    // Tape-style varispeed (pitch follows rate) when false; SoundTouch
    // pitch-preserving stretch when true. Live toggle, no rebuild.
    [ObservableProperty]
    private bool _keepPitch = true;

    [ObservableProperty]
    private AudioCover? _cover;

    /// <summary>Probe codec_name of the loaded track's audio stream ("" when unknown).</summary>
    public string CodecName { get; private set; } = "";

    /// <summary>Full path of the loaded audio file ("" when none).</summary>
    public string AudioPath { get; private set; } = "";

    public Dictionary<string, string> ProbeTags { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string DecodeBackend { get; private set; } = "none";

    // Introspection for the --audio-test harness (and future diagnostics):
    // the device mix format seen at init, and the exact sample-provider
    // format handed to the output. "?" until the first BuildOutput.
    public string DeviceMixFormatDescription { get; private set; } = "?";
    public string LastOutputDescription { get; private set; } = "?";

    // Total output latency in wall seconds at the last BuildOutput:
    // measured stream latency + endpoint buffer + stretcher priming (0 at
    // 1x bypass, 0 in varispeed). The displayed/recorded time subtracts
    // this. The stream+buffer base is kept separately so a live KeepPitch
    // toggle can re-budget the priming without rebuilding the output.
    public double OutputLatencySeconds { get; private set; }
    private double _outputBaseLatencyMs;

    // Last device/output error that did not throw (background rebuilds).
    // Plain property on purpose: read any time, never blocks.
    public string LastOutputError { get; private set; } = "";

    // Device-startup gate: after Play()/restarting-seek the WASAPI stream
    // takes a device-dependent while (USB DAC <2ms, onboard 30-50ms)
    // before the first pull. The position line must not run on wall time
    // during that window, or the first rebase steps back by the lag. While
    // armed, ticks run fast (20ms) and publish flat snapshots, watching
    // fed for the preroll-burst -> flat -> gradual signature of a started
    // device; the playing line anchors at device start. Falls back to
    // normal after 500ms. LastStartGateReport documents the measured
    // startup lag and preroll burst per start (harness-visible).
    public string LastStartGateReport { get; private set; } = "start-gate: no playback yet";
    private bool _startGate;
    private double _startGateFed;
    private long _startGateSinceQpc;
    private double _startGateBurstWall;

    // Test/diagnostic introspection: raw decoded-frame position. Frame
    // advances prove the device is actually pulling.
    public long DecodedFramePosition
    {
        get
        {
            try { return _pcm is not null ? _pcm.FramePosition : -1; }
            catch { return -1; }
        }
    }

    public event Action? PositionChanged;
    public event Action? TrackEnded;

    public AudioEngine()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clock = _dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) => OnTick();
        // Same cadence as the dispatcher tick above, but off it. This is the
        // clock's own heartbeat; the dispatcher tick keeps doing the UI-bound
        // bookkeeping (Position, end-of-track, the startup-gate decision).
        _publishTimer = new System.Threading.Timer(
            OnPublishTimer, null, Timeout.Infinite, Timeout.Infinite);
        // Harness-mode silence, read once at construction: an environment
        // signal (not a command line a launcher might drop or invent) forces
        // the IN-APP gain to zero. No OS volume is involved, and no setting is
        // persisted while it is set, so a test run cannot move the machine or
        // overwrite the user's remembered level.
        _silentMode = Environment.GetEnvironmentVariable("AMLL_SILENT") is "1" or "true";
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
        string? codecName = null;
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
                codecName = probe.CodecName;
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
            _trackEndFired = false;
            if (tags is not null)
            {
                ProbeTags.Clear();
                foreach (var (k, v) in tags) ProbeTags[k] = v;
            }
            Duration = duration;
            Position = TimeSpan.Zero;
            HasAudio = true;
            CodecName = codecName ?? "";
            AudioPath = path;
            PublishClock(PlaybackClockState.Paused);
        });
    }

    public void Play()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(Play); return; }
        if (_output is null || _source is null || _disposed) return;
        ClearAudition();
        _trackEndFired = false;
        _userPaused = false;
        _output.Play();
        IsPlaying = true;
        _clock.Start();
        StartPublishTimer(20);
        // Arm the startup gate BEFORE the first snapshot: the playing line
        // must not run until the device provably pulls. The flat snapshot
        // below is synchronously superseded by gated ticks; no frame ever
        // sees an optimistic line.
        ArmStartGate();
        PublishFlatClock();
    }

    // The dedicated publisher is re-perioded with the startup gate (fast)
    // and back to the 250ms rebase (normal), exactly like the dispatcher
    // timer, so anchor cadence matches the UI bookkeeping cadence.
    private void StartPublishTimer(int intervalMs)
    {
        _publishTimer.Change(
            TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(intervalMs));
    }

    private void StopPublishTimer() =>
        _publishTimer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Pause()
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(Pause); return; }
        if (_output is null || _disposed) return;
        _output.Pause();
        _userPaused = true;
        IsPlaying = false;
        _clock.Stop();
        StopPublishTimer();
        DisarmStartGate();
        ClearAudition();
        RefreshPosition();
        PublishClock(PlaybackClockState.Paused);
    }

    // The one seek path. Atomically: bumps the generation (stale async
    // work loses), flushes stretcher buffers, seeks the (already decoded,
    // hence sample-exact) PCM and re-anchors its counter, and rebases the
    // clock so the UI shows the target immediately. Seeking while paused
    // never starts playback. Seeking while playing restarts the output so
    // already-queued pre-seek audio cannot keep sounding (~200ms stale
    // tail); the cost is a short gap instead.
    public void SeekTo(double seconds)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SeekTo(seconds)); return; }
        if (_source is null || _disposed) return;
        ClearAudition();
        var wasPlaying = IsPlaying;
        var gen = ++_seekGen;
        ++_buildGen;
        _trackEndFired = false;
        if (wasPlaying && _output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { }
        }
        SeekWithoutClearing(seconds);
        PublishClock(IsPlaying ? PlaybackClockState.Playing : PlaybackClockState.Paused);
        if (wasPlaying && _output is not null && gen == _seekGen && !_disposed)
        {
            try { _output.Play(); } catch (Exception ex) { LastOutputError = AudioErrorText.MessageOf(ex); }
            finally { _output.PlaybackStopped += OnPlaybackStopped; }
            // The restart re-prerolls and re-lags like a fresh Play: arm
            // the gate and synchronously replace the optimistic snapshot
            // above with a flat one before any frame renders.
            ArmStartGate();
            PublishFlatClock();
        }
        else if (wasPlaying && _output is not null)
        {
            _output.PlaybackStopped += OnPlaybackStopped;
        }
    }

    public void Seek(double seconds) => SeekTo(seconds);

    // Legacy 0..1 amplitude entry point, kept for the harnesses that silence
    // playback. In-app gain only - no OS volume is read or written. New code
    // uses SetVolumePercent / SetMuted / SilentMode.
    public void SetVolume(double amplitude)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetVolume(amplitude)); return; }
        // Inverted from the slider curve so SetVolume(0) really is silence.
        var percent = amplitude <= 0 ? 0 : Math.Sqrt(Math.Clamp(amplitude, 0, 1)) * 100.0;
        SetVolumePercent(percent);
    }

    public void SetPlaybackRate(double rate)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetPlaybackRate(rate)); return; }
        rate = Math.Max(0.1, Math.Min(4, rate));
        if (Math.Abs(rate - PlaybackRate) < 0.001) return;
        PlaybackRate = rate;
        // Live change straight into the stretch provider: no rebuild, no
        // gap, no click. The provider takes its own lock (device thread may
        // be pulling); tempo slews inside the engine. Slope changes, so the
        // snapshot must be re-anchored immediately or extrapolation runs on
        // the old rate.
        _stretch?.Tempo = rate;
        PublishClock(IsPlaying ? PlaybackClockState.Playing : PlaybackClockState.Paused);
    }

    public void SetKeepPitch(bool keep)
    {
        if (!_dispatcher.HasThreadAccess) { RunOnUi(() => SetKeepPitch(keep)); return; }
        if (keep == KeepPitch) return;
        KeepPitch = keep;
        // Live mode switch inside the provider (buffers drop, clock is
        // preserved): no rebuild, no gap. Priming changes (0 in varispeed),
        // so re-budget latency and re-anchor the snapshot.
        _stretch?.PreservePitch = keep;
        RebudgetPrime();
        PublishClock(IsPlaying ? PlaybackClockState.Playing : PlaybackClockState.Paused);
    }

    private void RebudgetPrime()
    {
        var primeMs = 0.0;
        if (Math.Abs(PlaybackRate - 1.0) > 0.001 && _stretch is not null)
        {
            var prime = _stretch.GetPrimeLatencyFrames();
            if (prime >= 0) primeMs = prime * 1000.0 / FfmpegDecoder.OutSampleRate;
        }
        OutputLatencySeconds = (_outputBaseLatencyMs + primeMs) / 1000.0;
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
        CodecName = "";
        AudioPath = "";
        ProbeTags.Clear();
        DecodeBackend = "none";
        _clock.Stop();
        PositionClock.Publish(PlaybackSnapshot.Empty);
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
        stretch.PreservePitch = KeepPitch;
        _stretch = stretch;
        chain = stretch;
        if (chain.WaveFormat.Channels != mix.Channels)
            chain = new ChannelMapSampleProvider(chain, mix.Channels);
        if (chain.WaveFormat.SampleRate != mix.Rate)
            chain = new WdlResamplingSampleProvider(chain, mix.Rate);
        // App-owned gain, the LAST float stage: after decode, stretch and
        // resample, before the device sees anything. The OS endpoint volume
        // is never touched by this app - the session volume stays whatever
        // the user set, and a harness forces silence through this stage.
        var gain = new GainSampleProvider(chain, _gainLevel);
        _gain = gain;
        chain = gain;
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
        // NB: no IWavePlayer/endpoint volume assignment exists in this app on
        // purpose. The endpoint (OS) volume and mute belong to the user; the
        // app scales its own samples in the gain stage above.
        _output.PlaybackStopped += OnPlaybackStopped;
        LastOutputDescription =
            $"in={DescribeSampleFormat(_source)} chain={DescribeSampleFormat(chain)} tried=[{string.Join(" | ", attempts)}]";
        // Output latency budget, all measured, all wall time: the audio
        // client's stream latency + its endpoint buffer (native audio
        // clock) + the stretcher priming (0 at 1x bypass).
        var streamMs = 0.0;
        var bufferMs = 0.0;
        if (NativeAudioClock.TryReadLatency(out var sl, out var bufFrames, out var bufRate) && bufRate > 0)
        {
            streamMs = sl;
            bufferMs = bufFrames * 1000.0 / bufRate;
        }
        var primeMs = 0.0;
        if (Math.Abs(PlaybackRate - 1.0) > 0.001)
        {
            var prime = stretch.GetPrimeLatencyFrames();
            if (prime >= 0) primeMs = prime * 1000.0 / FfmpegDecoder.OutSampleRate;
        }
        _outputBaseLatencyMs = streamMs + bufferMs;
        OutputLatencySeconds = (_outputBaseLatencyMs + primeMs) / 1000.0;
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
            // A user Pause() can surface here too: expected stop, sync the
            // state, but never mistake it for track end and never log it.
            if (_userPaused)
            {
                IsPlaying = false;
                _clock.Stop();
                DisarmStartGate();
                ClearAudition();
                RefreshPosition();
                return;
            }
            var wasPlaying = IsPlaying;
            IsPlaying = false;
            _clock.Stop();
            DisarmStartGate();
            ClearAudition();
            RefreshPosition();
            // Natural stream exhaustion (provider returned 0 at EOF) lands
            // here, often BEFORE the next 250ms tick can notice: this is the
            // primary end-of-track path, not the tick. Anything else is an
            // unexpected device stop worth recording.
            if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(500))
                FireTrackEndedOnce();
            else
                LastOutputError =
                    $"device stopped at {Position}/{Duration} (wasPlaying={wasPlaying})" +
                    (e.Exception is null
                        ? " (no exception)"
                        : ":\n" + AudioErrorText.FullReport(e.Exception));
        });
    }

    // TrackEnded fires exactly once per playthrough, from whichever path
    // notices the end first (device callback or clock tick). Rearmed by
    // Play, SeekTo, and Load.
    private bool _trackEndFired;

    private void FireTrackEndedOnce()
    {
        if (_trackEndFired) return;
        _trackEndFired = true;
        TrackEnded?.Invoke();
    }

    private void OnTick()
    {
        if (_source is null) return;
        RefreshPosition();
        if (!double.IsNaN(_pauseAtSeconds) && Position.TotalSeconds >= _pauseAtSeconds)
        {
            Pause();
            FireTrackEndedOnce();
            return;
        }
        if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(200) && IsPlaying)
        {
            Pause();
            FireTrackEndedOnce();
            return;
        }
        // Publish here too: the dispatcher tick keeps the startup-gate
        // bookkeeping (StartGateSettled decides when the fast 20ms gate is over
        // and re-periods BOTH timers to the 250ms rebase), so the anchor
        // cadence the skew learner expects is preserved.
        if (IsPlaying && HasAudio && !_disposed)
        {
            if (_startGate && !StartGateSettled())
                PublishFlatClock();
            else
                PublishClock(PlaybackClockState.Playing);
        }
        PositionChanged?.Invoke();
    }

    // The clock snapshot publisher. Runs on a DEDICATED timer (not the UI
    // dispatcher) so that UI work cannot delay it: the extrapolated display
    // clock is only as good as the cadence of these anchors, and when they
    // shared the dispatcher with the UI thread a busy display measurably
    // degraded the 30s drift. PositionClock.Publish and ReadFedSeconds are
    // both thread-safe (volatile swap + provider locks), which is exactly why
    // the publish is the part that can move; every UI-observable write
    // (Position, IsPlaying, PositionChanged, pause decisions) still happens on
    // the dispatcher in OnTick.
    private void PublishFromAnyThread()
    {
        if (_source is null || !IsPlaying || !HasAudio || _disposed) return;
        // While the startup gate is armed the anchor is deliberately flat:
        // the device may not be pulling yet, so a slope would run ahead.
        if (_startGate) PublishFlatClock();
        else PublishClock(PlaybackClockState.Playing);
    }

    private void OnPublishTimer(object? state)
    {
        try { PublishFromAnyThread(); }
        catch { }
    }

    private void ArmStartGate()
    {
        _startGate = true;
        _startGateFed = ReadFedSeconds();
        _startGateSinceQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        _startGateBurstWall = 0;
        // DispatcherQueueTimer ignores an Interval change while running:
        // restart it so the fast 20ms detection ticks actually happen.
        _clock.Stop();
        _clock.Interval = TimeSpan.FromMilliseconds(20);
        _clock.Start();
        StartPublishTimer(20);
    }

    private void DisarmStartGate()
    {
        _startGate = false;
        if (_clock.Interval.TotalMilliseconds < 250)
        {
            var running = IsPlaying && HasAudio && !_disposed;
            _clock.Stop();
            _clock.Interval = TimeSpan.FromMilliseconds(250);
            if (running) _clock.Start();
        }
        // Both timers return to the 250ms rebase cadence.
        if (IsPlaying && HasAudio && !_disposed) StartPublishTimer(250);
        else StopPublishTimer();
    }

    // Fast-tick (20ms) startup detection, UI thread only. Fed advance is
    // measured in wall-equivalent seconds (divided by rate) so the
    // thresholds hold at every tempo: a preroll burst is ~a device buffer
    // (>=60ms wall-equiv), gradual pulling is ~a tick (2-60ms), and a
    // stalled device shows ~0. Settles when the device provably pulls AND
    // the unclamped heard time is non-negative (settling earlier would
    // anchor the line at the clamp floor and the first rebase would step
    // back by the clamped amount), or after 500ms (broken-device fallback).
    private bool StartGateSettled()
    {
        var fed = ReadFedSeconds();
        var rate = Math.Max(0.1, PlaybackRate);
        var wall = (System.Diagnostics.Stopwatch.GetTimestamp() - _startGateSinceQpc)
            / (double)System.Diagnostics.Stopwatch.Frequency;
        var advanceWall = (fed - _startGateFed) / rate;
        if (advanceWall > _startGateBurstWall) _startGateBurstWall = advanceWall;
        _startGateFed = fed;
        var heardNow = fed - OutputLatencySeconds * PlaybackRate;
        string? note = null;
        if (advanceWall >= 0.002 && advanceWall <= 0.060 && heardNow >= 0)
            note = "started";
        else if (wall >= 0.5)
            note = "TIMEOUT fallback";
        if (note is null) return false;
        _startGate = false;
        DisarmStartGate();
        LastStartGateReport =
            $"start-gate {note}: startup-lag={wall * 1000:F0}ms " +
            $"preroll-burst={_startGateBurstWall * 1000:F0}ms rate={PlaybackRate}";
        return true;
    }

    // Frozen snapshot at current fed truth with zero slope: nothing is
    // provably audible yet, so the line must not run ahead of sound.
    // Unclamped like all anchors (display clamps at read).
    private void PublishFlatClock()
    {
        var heard = ReadFedSeconds() - OutputLatencySeconds * PlaybackRate;
        PositionClock.Publish(new PlaybackSnapshot
        {
            SourceSeconds = heard,
            QpcTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            Rate = 0,
            State = PlaybackClockState.Playing,
        });
    }

    private double ReadFedSeconds()
    {
        try
        {
            return _stretch is not null ? _stretch.ReportedSourceSeconds
                : _pcm is not null ? _pcm.CurrentTime.TotalSeconds : Position.TotalSeconds;
        }
        catch
        {
            return Position.TotalSeconds;
        }
    }

    // Publishes one immutable position snapshot: provider-integrated
    // source time minus measured output latency, stamped with a Stopwatch
    // QPC read taken immediately after the fed read (microsecond-level
    // correlation). Deliberately NOT the device IAudioClock: that clock
    // belongs to a running stream, and a short-lived probe client that was
    // never started returns undefined QPC values (observed: stale stamps
    // injecting ~27ms steps and 11ms drift). The probe client's one sound
    // use is GetStreamLatency (a static device property). Thread-agnostic:
    // provider reads take their lock, the stamp is a QPC read, the swap
    // is atomic.
    private void PublishClock(PlaybackClockState state)
    {
        var raw = ReadFedSeconds();
        // Anchors stay unclamped (possibly negative while fed < latency
        // comp during startup): clamping an anchor bends the line and the
        // next rebase steps back by the clamped amount (observed 34-98ms).
        // Display clamps at read time (PositionClock.Clamp).
        var heard = raw - OutputLatencySeconds * PlaybackRate;
        PositionClock.Publish(new PlaybackSnapshot
        {
            SourceSeconds = heard,
            QpcTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            Rate = PlaybackRate,
            State = state,
        });
    }

    private void RefreshPosition()
    {
        TimeSpan pos;
        try
        {
            // Source-audio time from the stretch provider, which integrates
            // received output back through its own input/output ratio
            // (exact end-to-end, approximate mid-stream). Falls back to the
            // raw PCM clock when no stretch stage exists yet.
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
        // The gain provider wraps the chain; a stale one must not be handed to
        // the next BuildOutput's SetLevel call.
        _gain = null;
        ClearAudition();
        RunOnUi(() =>
        {
            _clock.Stop();
            IsPlaying = false;
            DisarmStartGate();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _devices.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        try { _devices.Dispose(); } catch { }
        StopPublishTimer();
        _publishTimer.Dispose();
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
                // Resume re-arms the startup gate inside Play(); only the
                // paused path needs its snapshot here, or an optimistic
                // line would overwrite the gate's flat anchor.
                if (!resume) RunOnUi(() => PublishClock(PlaybackClockState.Paused));
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
