using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

// Maps any input channel count to the output device channel count without
// changing the sample rate. The FFmpeg decode path always delivers stereo
// (its CLI downmix handles 5.1/7.1 beds), so the live cases are stereo in
// and N device channels out; the wider matrix below is defensive.
// Assumed channel orders: mono [M]; stereo [L R]; 5.1 [L R C LFE Ls Rs];
// 7.1 [L R C LFE BL BR SL SR]. Surround folds use ITU-R BS.775 gains
// (center 0.707, surrounds 0.707/0.5); LFE is dropped. Output is softly
// clamped to [-1, 1] so a hot downmix clips instead of wrapping.
internal sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[,] _matrix;
    private float[]? _temp;

    public WaveFormat WaveFormat { get; }

    public ChannelMapSampleProvider(ISampleProvider source, int outChannels)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate, outChannels);
        _matrix = BuildMatrix(source.WaveFormat.Channels, outChannels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var inCh = _source.WaveFormat.Channels;
        var outCh = WaveFormat.Channels;
        var frames = count / outCh;
        if (frames == 0) return 0;
        if (_temp is null || _temp.Length < frames * inCh) _temp = new float[frames * inCh];
        var read = _source.Read(_temp, 0, frames * inCh);
        var inFrames = read / inCh;
        var k = offset;
        for (var f = 0; f < inFrames; f++)
        {
            for (var o = 0; o < outCh; o++)
            {
                var s = 0f;
                for (var i = 0; i < inCh; i++) s += _temp[f * inCh + i] * _matrix[i, o];
                buffer[k++] = s > 1f ? 1f : s < -1f ? -1f : s;
            }
        }
        return inFrames * outCh;
    }

    private static float[,] BuildMatrix(int inCh, int outCh)
    {
        var m = new float[inCh, outCh];
        if (inCh == outCh)
        {
            for (var i = 0; i < inCh; i++) m[i, i] = 1f;
            return m;
        }
        if (outCh == 1)
        {
            // Average (mono from anything).
            for (var i = 0; i < inCh; i++) m[i, 0] = 1f / inCh;
            return m;
        }
        if (inCh == 1)
        {
            // Dual mono.
            for (var o = 0; o < outCh; o++) m[0, o] = 1f;
            return m;
        }
        if (inCh == 2)
        {
            // Stereo to N: L/R pass through, extra channels silent.
            m[0, 0] = 1f;
            if (outCh > 1) m[1, 1] = 1f;
            return m;
        }
        if (outCh == 2)
        {
            // N-channel bed to stereo (ITU-R BS.775-style fold).
            // L' = L + 0.707*C + 0.707*sideL + 0.5*backL (same mirrored).
            m[0, 0] = 1f;
            m[1, 1] = 1f;
            if (inCh > 2) { m[2, 0] = 0.7071f; m[2, 1] = 0.7071f; }
            if (inCh > 4) { m[4, 0] = 0.7071f; m[5, 1] = 0.7071f; }
            if (inCh > 6) { m[6, 0] = 0.5f; m[7, 1] = 0.5f; }
            return m;
        }
        // N to M (M > 2): pass through the overlap, silence the rest.
        for (var i = 0; i < Math.Min(inCh, outCh); i++) m[i, i] = 1f;
        return m;
    }
}
