using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

// Pitch-preserving playback speed over a fixed-rate stereo float source.
// Wraps the SoundTouch tempo engine (managed SoundTouch.Net port): changing
// Tempo stretches time without touching pitch, live, with no rebuild and
// no gap. At 1x the processor is bypassed (bit-exact, zero latency).
// With PreservePitch = false the provider instead resamples varispeed
// (tape-style: faster = higher pitch, shorter; linear interpolation,
// exact source-time accounting, zero priming latency), also live with no
// rebuild: the mode switch only drops already-buffered audio.
// Source-time accounting for lyric sync: the engine buffers internally
// (~100ms), so the reported position is (fed - still-buffered) source
// frames, i.e. the source time of the last sample handed downstream.
// Device output buffering beyond that (~10-50ms shared-mode) is outside
// what any provider can see and inside the UI clock tolerance.
// Threading: NAudio pulls Read on the device thread while Tempo is set
// from the UI thread; all processor access is locked.
internal sealed class TimeStretchProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouch.SoundTouchProcessor _st = new();
    private readonly object _gate = new();
    private double _tempo = 1.0;
    private bool _bypass = true;
    private bool _preservePitch = true;
    private long _fedFrames;
    // Source-time clock: every received output frame is integrated back
    // through the processor's own input/output ratio
    // (SoundTouchProcessor.GetInputOutputSampleRatio). The ratio is exact
    // end-to-end and approximate (±tens of ms) mid-stream, which beats
    // (fed - buffered): the input-side pipeline latency is invisible to us
    // and runs to hundreds of ms at extreme tempos.
    private double _sourceClockFrames;
    private bool _sourceEnded;
    private float[] _recv = new float[8192 * 2];
    private int _recvCount;
    private int _recvOffset;
    // Varispeed (tape-style) cursor: _vsBuf holds pulled-but-unconsumed
    // source frames, _vsPos/_vsFrac is the fractional read position in it.
    // All accounting stays in absolute source frames (_sourceClockFrames)
    // so the clock is exact at every tempo.
    private float[] _vsBuf = new float[4096 * 2];
    private int _vsBuffered;
    private int _vsPos;
    private double _vsFrac;
    private bool _vsSourceEnded;
    private bool _vsLastHeld;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public TimeStretchProvider(ISampleProvider source)
    {
        _source = source;
        _st.SampleRate = source.WaveFormat.SampleRate;
        _st.Channels = source.WaveFormat.Channels;
        _st.Tempo = 1.0;
    }

    public double Tempo
    {
        get => _tempo;
        set
        {
            lock (_gate)
            {
                _tempo = value;
                var bypass = Math.Abs(value - 1.0) < 0.001;
                if (bypass != _bypass)
                {
                    _st.Clear();
                    _recvCount = 0;
                    _recvOffset = 0;
                    _bypass = bypass;
                }
                if (!bypass) _st.Tempo = value;
            }
        }
    }

    public long SourceFramesFed
    {
        get { lock (_gate) return _fedFrames; }
    }

    // Tape-style varispeed on/off. Live: only buffered audio is dropped,
    // the source-time clock is preserved. Tempo changes need no handling
    // here (the resample ratio reads _tempo live); the engine re-anchors
    // its snapshot for the changed priming latency.
    public bool PreservePitch
    {
        get { lock (_gate) return _preservePitch; }
        set
        {
            lock (_gate)
            {
                if (value == _preservePitch) return;
                _preservePitch = value;
                _st.Clear();
                _recvCount = 0;
                _recvOffset = 0;
                _vsBuffered = 0;
                _vsPos = 0;
                _vsFrac = 0;
                _vsSourceEnded = false;
                _vsLastHeld = false;
            }
        }
    }

    public double ReportedSourceSeconds
    {
        get
        {
            lock (_gate) return Math.Max(0, _sourceClockFrames) / (double)_source.WaveFormat.SampleRate;
        }
    }

    // Drops buffered audio and re-anchors the clock (seek / load).
    public void Reset(long sourceFrame)
    {
        lock (_gate)
        {
            _st.Clear();
            _fedFrames = sourceFrame;
            _sourceClockFrames = sourceFrame;
            _sourceEnded = false;
            _recvCount = 0;
            _recvOffset = 0;
            _vsBuffered = 0;
            _vsPos = 0;
            _vsFrac = 0;
            _vsSourceEnded = false;
            _vsLastHeld = false;
        }
    }

    // Stretcher priming latency in frames (0 in bypass and in varispeed
    // mode, where nothing is buffered). -1 when unreadable; callers must
    // ignore it then.
    public int GetPrimeLatencyFrames()
    {
        lock (_gate)
        {
            if (_bypass || !_preservePitch) return 0;
            try { return Math.Max(0, _st.GetSetting(SoundTouch.SettingId.InitialLatency)); }
            catch { return -1; }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_bypass)
            {
                var read = _source.Read(buffer, offset, count);
                var frames = read / _source.WaveFormat.Channels;
                _fedFrames += frames;
                _sourceClockFrames += frames; // ratio is 1.0 in bypass
                return read;
            }
            if (!_preservePitch) return ReadVarispeedLocked(buffer, offset, count);
            var channels = _source.WaveFormat.Channels;
            var got = 0;
            var ratio = _st.GetInputOutputSampleRatio();
            if (!(ratio > 0)) ratio = 1.0;
            while (got < count)
            {
                if (_recvOffset >= _recvCount)
                {
                    if (!FillReceiveBuffer()) break;
                }
                var take = Math.Min(count - got, _recvCount - _recvOffset);
                // BlockCopy, not Array.Copy: see the WaveBuffer note on
                // FfmpegPcmProvider.Read — downstream may hand us a
                // type-punned array.
                Buffer.BlockCopy(_recv, _recvOffset * 4, buffer, (offset + got) * 4, take * 4);
                _recvOffset += take;
                got += take;
                _sourceClockFrames += ((double)take / channels) / ratio;
            }
            return got;
        }
    }

    // Pulls decoded frames through the stretcher until output is available
    // (or the source is drained and flushed). Returns false at true EOF.
    private bool FillReceiveBuffer()
    {
        var channels = _source.WaveFormat.Channels;
        if (_recv.Length < 8192 * channels) _recv = new float[8192 * channels];
        while (true)
        {
            var n = _st.ReceiveSamples(new Span<float>(_recv), _recv.Length / channels);
            if (n > 0)
            {
                _recvCount = n * channels;
                _recvOffset = 0;
                return true;
            }
            if (_sourceEnded) return false;
            const int feedFrames = 2048;
            if (_feed.Length < feedFrames * channels) _feed = new float[feedFrames * channels];
            var read = _source.Read(_feed, 0, feedFrames * channels);
            if (read <= 0)
            {
                _sourceEnded = true;
                _st.Flush();
                continue;
            }
            _st.PutSamples(new ReadOnlySpan<float>(_feed, 0, read), read / channels);
            _fedFrames += read / channels;
        }
    }

    private float[] _feed = new float[2048 * 2];

    // Tape-style varispeed: output advances the fractional source cursor
    // by Tempo source frames per output frame, linear-interpolated.
    // Faster reads more source per output (shorter, higher pitch) and vice
    // versa. Caller holds _gate. Exact clock: every consumed integer source
    // frame advances _sourceClockFrames by exactly one.
    private int ReadVarispeedLocked(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var rate = _tempo;
        if (!(rate > 0)) rate = 1.0;
        if (_vsBuf.Length < 4096 * channels) _vsBuf = new float[4096 * channels];
        var outFrames = count / channels;
        var got = 0;
        for (var i = 0; i < outFrames; i++)
        {
            while (!_vsSourceEnded && _vsPos + 1 >= _vsBuffered && !FillVarispeedLocked())
            {
                // Source drained below the interpolation pair: emit what we
                // have; the cursor is past the last full frame.
                break;
            }
            if (_vsPos + 1 >= _vsBuffered)
            {
                if (_vsBuffered == 0 || _vsPos >= _vsBuffered || _vsLastHeld) break;
                // Last frame with no successor: hold it once (no pitch
                // artifact, EOF is one frame away), then report EOF.
                _vsLastHeld = true;
                for (var c = 0; c < channels; c++)
                    buffer[offset + got + c] = _vsBuf[_vsPos * channels + c];
            }
            else
            {
                var f = (float)_vsFrac;
                for (var c = 0; c < channels; c++)
                {
                    var a = _vsBuf[_vsPos * channels + c];
                    var b = _vsBuf[(_vsPos + 1) * channels + c];
                    buffer[offset + got + c] = a + (b - a) * f;
                }
            }
            got += channels;
            _vsFrac += rate;
            var step = (int)_vsFrac;
            if (step > 0)
            {
                _vsFrac -= step;
                _vsPos += step;
                _fedFrames += step;
                _sourceClockFrames += step;
            }
        }
        // Compact consumed frames so the pair invariant (_vsPos+1 <
        // _vsBuffered) holds with bounded memory.
        if (_vsPos > 0)
        {
            var remain = _vsBuffered - _vsPos;
            if (remain > 0)
                Array.Copy(_vsBuf, _vsPos * channels, _vsBuf, 0, remain * channels);
            _vsBuffered = Math.Max(0, remain);
            _vsPos = 0;
        }
        return got;
    }

    // Pulls source frames into _vsBuf. Returns false at true EOF.
    private bool FillVarispeedLocked()
    {
        var channels = _source.WaveFormat.Channels;
        if (_vsSourceEnded) return false;
        if (_vsBuffered >= _vsBuf.Length / channels)
        {
            var remain = _vsBuffered - _vsPos;
            if (remain > 0)
                Array.Copy(_vsBuf, _vsPos * channels, _vsBuf, 0, remain * channels);
            _vsBuffered = Math.Max(0, remain);
            _vsPos = 0;
            if (_vsBuffered >= _vsBuf.Length / channels) return true;
        }
        var read = _source.Read(_vsBuf, _vsBuffered * channels, (_vsBuf.Length / channels - _vsBuffered) * channels);
        if (read <= 0)
        {
            _vsSourceEnded = true;
            return false;
        }
        _vsBuffered += read / channels;
        _vsLastHeld = false;
        return true;
    }
}
