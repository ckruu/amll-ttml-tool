// App-owned playback gain. Sits in the float sample chain BEFORE the
// WASAPI write, so the app controls its own loudness entirely and never
// reads or writes the audio endpoint (OS) volume or mute state.
//
// Design constraints:
//  * No gain above 1.0, so the stage can never clip.
//  * Every gain change is an exponential ramp (~15ms time constant)
//    rather than a step: an instant jump on a square wave produces an
//    audible click, and a mid-stream change would otherwise show
//    sample-to-sample deltas far larger than the signal itself.
//  * Mute is a target of exactly 0.0, so "muted" is true digital silence
//    and the previous level is kept callerside for unmute.
//  * Mapped from a 0-100 slider as amplitude=(v/100)^2. A linear 0-1 map
//    makes 50 sound far too loud (half amplitude = -6dB) and pushes the
//    whole useful range into the bottom third of the slider; a -60dB
//    curve makes the entire lower half inaudible. The squared curve gives
//    100=0dB, 50=-12dB, 25=-24dB.
//
// Threading: the device thread calls Read, the UI thread calls SetLevel.
// All state is under one lock and the ramp is coefficient-based, so a late
// Read simply lands further along the same ramp.
using NAudio.Wave;

namespace AmllTtmlTool.WinUI.Services;

/// <summary>
/// Scales float samples by an app-owned, ramped gain. Last float stage
/// before the device converter; mute produces exact zeros.
/// </summary>
public sealed class GainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _gate = new();
    private double _level;
    private double _target;
    private double _coeff = 1.0;

    public GainSampleProvider(ISampleProvider source, double level = 0.5)
    {
        _source = source;
        _level = Clamp(level);
        _target = _level;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Current (possibly mid-ramp) gain; diagnostic only.</summary>
    public double CurrentLevel
    {
        get { lock (_gate) return _level; }
    }

    /// <summary>
    /// Sets the gain target with the given time constant in seconds.
    /// Default 15ms: inaudible on a level change, and the worst-case
    /// per-sample delta stays well under 0.1 full scale.
    /// </summary>
    public void SetLevel(double target, double tauSeconds = 0.015)
    {
        lock (_gate)
        {
            _target = Clamp(target);
            var rate = _source.WaveFormat.SampleRate > 0
                ? _source.WaveFormat.SampleRate
                : 48000;
            var tau = tauSeconds > 0.0005 && !double.IsNaN(tauSeconds) ? tauSeconds : 0.015;
            _coeff = 1.0 - Math.Exp(-1.0 / (tau * rate));
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;
        lock (_gate)
        {
            for (var i = 0; i < read; i++)
            {
                var b = offset + i;
                if (_level != _target)
                {
                    _level += (_target - _level) * _coeff;
                    if (Math.Abs(_target - _level) < 1e-4) _level = _target;
                }
                if (_level == _target && _target <= 0.0)
                {
                    // Exact digital silence once the ramp has settled: mute
                    // is zeros, not -0.0 or a denormal.
                    buffer[b] = 0f;
                }
                else
                {
                    buffer[b] = (float)(buffer[b] * _level);
                }
            }
        }
        return read;
    }

    private static double Clamp(double v) =>
        double.IsNaN(v) ? 0.0 : v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
