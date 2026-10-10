using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

// Pitch-preserving playback speed over a fixed-rate stereo float source.
// Wraps the SoundTouch tempo engine (managed SoundTouch.Net port): changing
// Tempo stretches time without touching pitch, live, with no rebuild and
// no gap. At 1x the processor is bypassed (bit-exact, zero latency).
//
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
}
