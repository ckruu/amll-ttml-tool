// System Media Transport Controls push side: metadata, playback status,
// and a live timeline off the millisecond position clock. One owner per
// window; all members must be called on the UI thread (SMTC objects are
// agile but the view binding is not).
//
// Product decisions: Play/Pause only. Next/Previous stay disabled — this
// is a single-track lyric tool with no playlist, and a Next button that
// seeks would lie about universal transport semantics. Stop stays
// disabled (Pause covers it). Timeline position comes from
// PositionClock.ExtrapolateNow (latency-compensated ms clock), not the
// raw provider counter, so the OS surface agrees with the in-app text.
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media;
using Windows.Storage.Streams;

namespace AmllTtmlTool.WinUI.Services;

public sealed class SmtcService
{
    private SystemMediaTransportControls? _smtc;
    private DispatcherQueue? _dispatcher;
    private MediaPlaybackStatus? _lastStatus;
    private long _lastTimelineMs;
    private string _lastTrackKey = "";
    // The thumbnail reference reads lazily (when the OSD opens, possibly
    // long after Update), so the backing stream must stay alive: one held
    // per loaded track, replaced (never leaked unboundedly) on next load.
    private InMemoryRandomAccessStream? _thumbStream;

    public bool IsAttached => _smtc is not null;

    public string AttachDetail { get; private set; } = "not attached";

    public void Attach(DispatcherQueue dispatcher, nint hwnd, Action play, Action pause, Action refreshChrome)
    {
        _dispatcher = dispatcher;
        try
        {
            // Packaged path first; unpackaged WinUI 3 has no ApplicationView
            // and must bind SMTC to the window HWND instead.
            _smtc = SystemMediaTransportControls.GetForCurrentView();
            AttachDetail = "GetForCurrentView";
        }
        catch
        {
            _smtc = null;
        }
        if (_smtc is null)
        {
            try
            {
                _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
                AttachDetail = "Interop.GetForWindow";
            }
            catch (Exception ex)
            {
                _smtc = null;
                AttachDetail = $"both failed: {ex.GetType().Name}";
            }
        }
        if (_smtc is null) return;
        try
        {
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            _smtc.DisplayUpdater.Update();
            _smtc.ButtonPressed += (_, args) =>
            {
                if (args.Button == SystemMediaTransportControlsButton.Play) play();
                else if (args.Button == SystemMediaTransportControlsButton.Pause) pause();
                _dispatcher?.TryEnqueue(() => refreshChrome());
            };
        }
        catch (Exception ex)
        {
            _smtc = null;
            AttachDetail = $"setup failed: {ex.GetType().Name}";
        }
    }

    // Track metadata push, once per load. Thumbnail is best-effort (cover
    // may be absent or undecodable); title/artist/album always land.
    public async Task RefreshMetadataAsync(string title, string artist, string album, byte[]? coverBytes)
    {
        if (_smtc is null) return;
        _lastTrackKey = $"{title}\0{artist}\0{album}";
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = title;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            updater.Thumbnail = null;
            try { _thumbStream?.Dispose(); } catch { }
            _thumbStream = null;
            if (coverBytes is { Length: > 0 })
            {
                try
                {
                    var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(coverBytes.AsBuffer());
                    stream.Seek(0);
                    _thumbStream = stream;
                    updater.Thumbnail =
                        RandomAccessStreamReference.CreateFromStream(stream);
                }
                catch { }
            }
            updater.Update();
        }
        catch { }
    }

    // Playback push: status immediately on change, timeline throttled to
    // ~1Hz (the OSD interpolates nothing; 1Hz is live without COM churn).
    // Safe to call on every engine tick.
    public void RefreshPlayback(bool hasAudio, bool isPlaying, TimeSpan position, TimeSpan duration)
    {
        if (_smtc is null) return;
        try
        {
            var status = !hasAudio
                ? MediaPlaybackStatus.Closed
                : isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (status != _lastStatus)
            {
                _smtc.PlaybackStatus = status;
                _lastStatus = status;
            }
            if (!hasAudio || duration <= TimeSpan.Zero) return;
            var nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000
                / System.Diagnostics.Stopwatch.Frequency;
            var posMs = (long)position.TotalMilliseconds;
            if (nowMs - _lastTimelineMs < 1000 && posMs != 0) return;
            _lastTimelineMs = nowMs;
            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                : position > duration ? duration : position;
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = duration,
                Position = clamped,
            });
        }
        catch { }
    }
}
