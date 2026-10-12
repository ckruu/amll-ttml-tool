// --playerbar-test <folder> [reportPath] harness: self-driving
// verification of the player bar, both TeachingTips, volume and speed
// through the REAL controls on the UI thread (same handlers the
// pointer/keyboard paths use, via ShellPage test hooks). Pointer-only
// paths (right/middle click, Esc/X, layout screenshots) belong to the
// external ui-tests-playerbar.ps1 driver against --playerbar-ui-hold.
// Self-closing. DEBUG-only.
using AmllTtmlTool.Core.Audio;

namespace AmllTtmlTool.WinUI.Services;

internal static class PlayerbarTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playerbar-test-report.txt");

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "m4a", "aac", "flac", "mp3", "wav", "ogg", "opus", "wma", "alac",
    };

    public static async Task RunAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string folder, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(12), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: playerbar-test exceeded 12 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        var fails = 0;
        // Isolate the harness from the user's real settings: volume tests
        // swap in a temp store and restore the original afterwards.
        var savedStore = AppSettings.Store;
        var savedSilent = AppSettings.SilentMode;
        try
        {
            Write(report, "=== PLAYERBAR TEST ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            await OnUiAsync(window, () => audio.SilentMode = true);
            AppSettings.ResetForTests(new Core.SettingsStore(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "playerbar-test-settings-" + Guid.NewGuid().ToString("N") + ".json")));

            fails += await EmptyStateAsync(window, page, report);
            fails += await VolumeMatrixAsync(window, audio, page, report);
            fails += await SettingsMatrixAsync(report);
            fails += await FilesMatrixAsync(window, audio, page, folder, report);
            fails += await ThumbMatrixAsync(window, audio, page, folder, report);
            fails += await TipBoundsMatrixAsync(window, audio, page, report);
            fails += await SpeedMatrixAsync(window, audio, page, report);
            fails += await LayoutMatrixAsync(window, page, report);
            fails += await BrushReportAsync(window, page, report);

            Write(report, fails == 0 ? "playerbar | OK" : $"playerbar | FAIL fails={fails}");
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(report, "HARNESS-ERROR: " + ex + "\n"); } catch { }
            Write(report, "playerbar | FAIL harness-error");
        }
        finally
        {
            try
            {
                AppSettings.ResetForTests(savedStore);
                AppSettings.SilentMode = savedSilent;
            }
            catch { }
            watchdogCts.Cancel();
        }
    }

    // Passive synthetic hold for screenshots: loads synthetic case <arg>
    // (index into SyntheticCases, clamped) and leaves the track tip open.
    public static async Task RunSynthHoldAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string arg)
    {
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            if (!int.TryParse(arg, out var index)) index = 0;
            var cases = SyntheticCases();
            index = Math.Clamp(index, 0, cases.Count - 1);
            var c = cases[index];
            await OnUiAsync(window, () => audio.SilentMode = true);
            await OnUiAsync(window, () =>
            {
                audio.Test_LoadSynthetic(c.Title, c.Artist, c.Codec, c.File, c.Cover ? CoverPng() : null, 200);
                page.Test_RefreshChrome();
                page.Test_OpenTrackTip();
            });
            System.Diagnostics.Debug.WriteLine($"PLAYERBAR-SYNTH-HOLD READY case={index}");
            await Task.Delay(TimeSpan.FromMinutes(9));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PLAYERBAR-SYNTH-HOLD-ERROR " + AudioErrorText.FullReport(ex));
        }
    }

    private sealed record SynthCase(string Label, string Title, string Artist, string Codec, string File, bool Cover);

    private static byte[] CoverPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // Shared by the bounds matrix and the screenshot hold: indices 0-2
    // artist sweep, 3 CJK title, 4 cover, 5+ codec sweep.
    private static List<SynthCase> SyntheticCases()
    {
        var cases = new List<SynthCase>
        {
            new("artist-short", "T", "A", "alac", "synth.m4a", false),
            new("artist-cjk", "T", "ココア(佐倉綾音)", "alac", "synth.m4a", false),
            new("artist-80", "T", new string('x', 80), "alac", "synth.m4a", false),
            new("title-cjk", "テストタイトル日本語の長い曲名", "A", "flac", "synth.flac", false),
            new("cover", "T", "Petit Rabbit's", "alac", "synth.m4a", true),
        };
        foreach (var (codec, ext) in new[]
        {
            ("alac", "m4a"), ("flac", "flac"), ("eac3", "m4a"), ("ac3", "ac3"),
            ("truehd", "thd"), ("aac", "m4a"), ("mp3", "mp3"), ("opus", "opus"),
            ("vorbis", "ogg"), ("pcm_s16le", "wav"), ("wmav2", "wma"), ("frobnicate", "xyz"),
        })
            cases.Add(new($"codec-{codec}", "T", "A", codec, $"synth.{ext}", false));
        return cases;
    }

    // Passive UI target for the EXTERNAL driver (real OS mouse/keyboard):
    // loads one file paused at 0 and sits still until the driver finishes
    // or the 10-minute watchdog closes it.
    public static async Task RunHoldAsync(Window window, AudioEngine audio, string file)
    {
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            await OnUiAsync(window, () => audio.SilentMode = true);
            await audio.LoadAsync(file);
            await OnUiAsync(window, () => { audio.SeekTo(0); audio.Pause(); });
            System.Diagnostics.Debug.WriteLine($"PLAYERBAR-HOLD READY file={file}");
            await Task.Delay(TimeSpan.FromMinutes(9));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PLAYERBAR-HOLD-ERROR " + AudioErrorText.FullReport(ex));
        }
    }

    private static void Check(string report, string name, bool ok, string detail, ref int fails)
    {
        Write(report, $"  {(ok ? "ok" : "FAIL")} {name} {detail}");
        if (!ok) fails++;
    }

    // --- seek-thumb matrix (item 1) ---------------------------------------
    // Plays at in-app gain 0 (SilentMode is already on), seeks to fixed
    // fractions, plays 1.5s, then asserts the VISIBLE bar slider Value and
    // the live thumb-center x against the position clock. The thumb center
    // travels [trackLeft+halfW, trackRight-halfW], so:
    //   thumbX == trackLeft + halfW + (posMs/durMs) * (trackWidth - thumbW)
    // within 1.5px, and |Value - posMs| within the same 1.5px in ms.
    private static async Task<int> ThumbMatrixAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string folder, string report)
    {
        Write(report, "---- seek-thumb matrix ----");
        var fails = 0;
        var files = System.IO.Directory.Exists(folder)
            ? System.IO.Directory.GetFiles(folder)
                .Where(f => AudioExtensions.Contains(
                    System.IO.Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        if (files.Length == 0)
        {
            Write(report, "  SKIP no audio files");
            return fails;
        }
        var file = files[0];
        try
        {
            await audio.LoadAsync(file);
        }
        catch (Exception ex)
        {
            Check(report, "thumb-load", false, AudioErrorText.MessageOf(ex), ref fails);
            return fails;
        }
        await OnUiAsync(window, () => page.Test_RefreshChrome());
        double durMs = 0;
        await OnUiAsync(window, () => durMs = audio.Duration.TotalMilliseconds);
        if (!(durMs > 0))
        {
            Check(report, "thumb-duration", false, $"durMs={durMs}", ref fails);
            return fails;
        }
        await OnUiAsync(window, () => audio.Play());
        foreach (var frac in new[] { 0.10, 0.50, 0.90 })
        {
            await OnUiAsync(window, () => audio.SeekTo(frac * durMs / 1000.0));
            await Task.Delay(1500);
            fails += await CheckThumbAsync(window, page, durMs, $"seek {frac:P0}", report);
        }
        // After a speed change (slope + re-anchor).
        await OnUiAsync(window, () => audio.SetPlaybackRate(1.5));
        await OnUiAsync(window, () => audio.SeekTo(0.50 * durMs / 1000.0));
        await Task.Delay(1500);
        fails += await CheckThumbAsync(window, page, durMs, "rate 1.5x @50%", report);
        // Unfocused interplay at the new rate: hammer the background-only
        // text path while the per-frame painter runs; Value and thumb must
        // still track the clock (no fighting between the two paths).
        await Task.Delay(1000);
        for (var i = 0; i < 5; i++)
        {
            await OnUiAsync(window, () => page.Test_PaintUnfocused());
            await Task.Delay(250);
        }
        fails += await CheckThumbAsync(window, page, durMs, "unfocused-path @1.5x", report);
        await OnUiAsync(window, () => audio.SetPlaybackRate(1.0));
        // Second file: Maximum updates, thumb resets to the start.
        var file2 = files.Length > 1 ? files[1] : files[0];
        try
        {
            await audio.LoadAsync(file2);
        }
        catch (Exception ex)
        {
            Check(report, "thumb-reload", false, AudioErrorText.MessageOf(ex), ref fails);
            return fails;
        }
        await OnUiAsync(window, () => page.Test_RefreshChrome());
        await Task.Delay(500);
        double dur2Ms = 0;
        string geo = "";
        await OnUiAsync(window, () =>
        {
            dur2Ms = audio.Duration.TotalMilliseconds;
            geo = page.Test_ThumbGeometry();
        });
        if (TryParseGeo(geo, out var g2))
        {
            Check(report, "reload-maximum", Math.Abs(g2.Max - dur2Ms) < 1.0,
                $"max={g2.Max:F1} dur={dur2Ms:F1} [{geo}]", ref fails);
            var tolMs = g2.TrackWidth > 0 ? 1.5 * dur2Ms / g2.TrackWidth : double.MaxValue;
            Check(report, "reload-value-reset", Math.Abs(g2.Value) <= tolMs,
                $"value={g2.Value:F1} tol={tolMs:F1}ms [{geo}]", ref fails);
            Check(report, "reload-thumb-reset",
                Math.Abs(g2.ThumbX - (g2.TrackLeft + g2.ThumbW / 2)) <= 1.5,
                $"thumbX={g2.ThumbX:F1} trackLeft={g2.TrackLeft:F1} thumbW={g2.ThumbW:F1} [{geo}]", ref fails);
        }
        else
        {
            Check(report, "reload-geometry", false, geo, ref fails);
        }
        await OnUiAsync(window, () => audio.Pause());
        return fails;
    }

    // --- tip-bounds matrix (item 4, synthetic, no files) ----------------------
    // Reads until the content has laid out (rootW > 0) AND settled with the
    // artist cap applied. Rationale: UpdateLayout() does not force an
    // arrange inside a freshly opened popup (entrance animation gardens
    // it), so synchronous reads right after open see the pre-cap arrange
    // (e.g. artist 514px with MaxWidth already 120). Production converges
    // one compositor pass later (screenshots prove the final state); this
    // poll waits for the settled state instead of trusting first numbers.
    // Reopens the tip if a background-window dismissal closed it mid-matrix
    // (counted honestly); the read and the ensure-open run in one UI block
    // so they can't race.
    private static async Task<(bool Ok, TipBounds B, string Raw, int Reopens)> ReadTipBoundsAsync(
        Window window, Views.ShellPage page, string report)
    {
        string raw = "";
        var reopens = 0;
        var pollLog = new System.Text.StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            var opened = 0;
            await OnUiAsync(window, () =>
            {
                if (!page.Test_TrackTipOpen()) { page.Test_OpenTrackTip(); opened++; }
                raw = page.Test_TipBounds();
            });
            reopens += opened;
            var parsed = TryParseBounds(raw, out var b);
            pollLog.Append($"[{i}:parse={parsed} rootW={(parsed ? b.RootW : -1):F0} open={opened}]");
            if (parsed && b.RootW > 0) return (true, b, raw, reopens);
            await Task.Delay(200);
        }
        TryParseBounds(raw, out var last);
        Write(report, $"  polltrace {pollLog}");
        return (false, last, raw, reopens);
    }

    private static async Task<int> TipBoundsMatrixAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string report)
    {
        Write(report, "---- tip-bounds matrix (synthetic) ----");
        var fails = 0;
        foreach (var c in SyntheticCases())
        {
            await OnUiAsync(window, () =>
            {
                audio.Test_LoadSynthetic(c.Title, c.Artist, c.Codec, c.File,
                    c.Cover ? CoverPng() : null, 200);
                page.Test_RefreshChrome();
                page.Test_OpenTrackTip();
            });
            // Poll for laid-out bounds: the artist fit runs one layout pass
            // after the texts land, so the first read may predate it.
            var (laidOut, b, bounds, reopens0) = await ReadTipBoundsAsync(window, page, report);
            if (reopens0 > 0) Write(report, $"  note {c.Label} reopened x{reopens0} (background dismissal)");
            if (!laidOut)
            {
                Check(report, $"tip {c.Label}", false, bounds, ref fails);
                continue;
            }
            var rootR = b.RootL + b.RootW;
            // Content fits the 336px-capped presenter with ~13px slack per
            // side: (336-310)/2. TrackTip.ActualWidth measures the inline
            // anchor, not the popup, so it is not asserted.
            Check(report, $"tip {c.Label} fits-cap",
                b.RootW <= 310 && b.RootL >= 11,
                $"rootW={b.RootW:F1} rootL={b.RootL:F1} [{bounds}]", ref fails);
            var worst = "";
            foreach (var (name, l, r) in new[]
            {
                // NOTE: artist excluded — its cap needs an arrange pass that
                // background windows defer; the set property (asserted below)
                // plus the foregrounded driver geometry assert cover it.
                ("title", b.TitleL, b.TitleR),
                ("codec", b.CodecL, b.CodecR), ("slider", b.SliderL, b.SliderR),
                ("open", b.OpenL, b.OpenR), ("explorer", b.ExplorerL, b.ExplorerR),
                ("state", b.StateL, b.StateR),
            })
                if (r > rootR + 0.5) worst += $" {name} clipped";
            Check(report, $"tip {c.Label} unclipped", worst.Length == 0,
                (worst.Length == 0 ? "all inside content" : worst.Trim()) + $" [{bounds}]", ref fails);
            // Subpixel arrange rounding can shave a fraction of a pixel off
            // the measured desired width; 1.0px separates that from real
            // ellipsis clipping.
            Check(report, $"tip {c.Label} codec-visible",
                (b.CodecR - b.CodecL) >= b.CodecDesired - 1.0,
                $"codecW={b.CodecR - b.CodecL:F1} desired={b.CodecDesired:F1} [{bounds}]", ref fails);
            // Exact fit: the SET cap must equal the computed cap (the box
            // itself needs an arrange pass that background windows defer;
            // the foregrounded driver asserts the rendered artist box).
            var cap = Math.Max(40, b.RootW - 64.0 - 12.0 - 6.0 - b.CodecDesired);
            var maxW = 0.0;
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    bounds, @"maxW=(-?[0-9.]+|Infinity)");
                double.TryParse(m.Success ? m.Groups[1].Value : "",
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out maxW);
            }
            Check(report, $"tip {c.Label} artist-fit",
                Math.Abs(maxW - cap) <= 1.0,
                $"maxW={maxW:F1} want={cap:F1} true={b.ArtistTrue:F1} [{bounds}]", ref fails);
            // Button text must fit its button (no ellipsis inside buttons).
            Check(report, $"tip {c.Label} buttons-fit",
                (b.OpenR - b.OpenL) >= b.OpenDesired - 1.0
                    && (b.ExplorerR - b.ExplorerL) >= b.ExplorerDesired - 1.0,
                $"openW={b.OpenR - b.OpenL:F1}/{b.OpenDesired:F1} explorerW={b.ExplorerR - b.ExplorerL:F1}/{b.ExplorerDesired:F1} [{bounds}]", ref fails);
            if (c.Label == "artist-short")
                Check(report, $"tip {c.Label} hug", (b.CodecL - b.ArtistR) <= 8,
                    $"gap={b.CodecL - b.ArtistR:F1} [{bounds}]", ref fails);
            foreach (var v in new[] { 0.0, 50.0, 100.0 })
            {
                await OnUiAsync(window, () => page.Test_SetTipSlider(v));
                await Task.Delay(150);
                var (vok, bv, vb, reopensV) = await ReadTipBoundsAsync(window, page, report);
                if (reopensV > 0) Write(report, $"  note {c.Label} vol@{v} reopened x{reopensV}");
                if (!vok || double.IsNaN(bv.VolL))
                {
                    Check(report, $"tip {c.Label} vol@{v}", false, vb, ref fails);
                    continue;
                }
                Check(report, $"tip {c.Label} vol@{v}",
                    bv.VolL >= bv.SliderL - 0.5 && bv.VolR <= bv.SliderR + 0.5,
                    $"thumb=[{bv.VolL:F1}-{bv.VolR:F1}] slider=[{bv.SliderL:F1}-{bv.SliderR:F1}] [{vb}]", ref fails);
            }
            await OnUiAsync(window, () =>
            {
                page.Test_SetTipSlider(50);
                page.Test_CloseTips();
            });
        }
        return fails;
    }

    private sealed record TipBounds(
        double TipW, double RootL, double RootW,
        double TitleL, double TitleR, double ArtistL, double ArtistR,
        double CodecL, double CodecR, double CodecDesired, double ArtistTrue,
        double SliderL, double SliderR, double VolL, double VolR,
        double OpenL, double OpenR, double OpenDesired,
        double ExplorerL, double ExplorerR, double ExplorerDesired,
        double StateL, double StateR);

    private static bool TryParseBounds(string s, out TipBounds b)
    {
        b = new TipBounds(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            var box = new Dictionary<string, (double L, double R)>(StringComparer.OrdinalIgnoreCase);
            var num = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                var m = System.Text.RegularExpressions.Regex.Match(
                    kv[1], @"^\[(-?[0-9.]+)-(-?[0-9.]+)\]$");
                if (m.Success
                    && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var l)
                    && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var r))
                {
                    box[kv[0]] = (l, r);
                    continue;
                }
                if (double.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    num[kv[0]] = v;
                }
            }
            (double L, double R) Box(string k) => box.TryGetValue(k, out var t) ? t : (double.NaN, double.NaN);
            var root = Box("root"); var title = Box("title"); var artist = Box("artist");
            var codec = Box("codec"); var slider = Box("slider"); var open = Box("open");
            var explorer = Box("explorer"); var state = Box("state");
            var vol = Box("volThumb");
            b = new TipBounds(
                num["tipW"], root.L, root.L == root.R ? 0 : root.R - root.L,
                title.L, title.R, artist.L, artist.R, codec.L, codec.R,
                num.GetValueOrDefault("codecDesired", double.NaN),
                num.GetValueOrDefault("artistTrue", double.NaN),
                slider.L, slider.R,
                vol.L, vol.R,
                open.L, open.R, num.GetValueOrDefault("openDesired", double.NaN),
                explorer.L, explorer.R, num.GetValueOrDefault("explorerDesired", double.NaN),
                state.L, state.R);
            return b.TipW > 0 && b.RootW > 0 && !double.IsNaN(b.CodecDesired);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> CheckThumbAsync(        Window window, Views.ShellPage page, double durMs, string label, string report)
    {
        var fails = 0;
        string geo = "";
        double posMs = 0;
        await OnUiAsync(window, () =>
        {
            geo = page.Test_ThumbGeometry();
            posMs = PositionClock.ExtrapolateNow().TotalMilliseconds;
        });
        if (!TryParseGeo(geo, out var g))
        {
            Check(report, $"thumb {label}", false, geo, ref fails);
            return fails;
        }
        var expectedX = g.TrackLeft + g.ThumbW / 2
            + (durMs > 0 ? posMs / durMs * (g.TrackWidth - g.ThumbW) : 0);
        Check(report, $"thumb {label}", Math.Abs(g.ThumbX - expectedX) <= 1.5,
            $"thumbX={g.ThumbX:F1} want={expectedX:F1} pos={posMs:F0}ms [{geo}]", ref fails);
        var tolMs = g.TrackWidth > 0 && durMs > 0 ? 1.5 * durMs / g.TrackWidth : double.MaxValue;
        Check(report, $"value {label}", Math.Abs(g.Value - posMs) <= tolMs,
            $"value={g.Value:F1} pos={posMs:F0}ms tol={tolMs:F1}ms [{geo}]", ref fails);
        return fails;
    }

    private sealed record ThumbGeo(
        double Value, double Max, double TrackLeft, double TrackWidth, double ThumbX, double ThumbW);

    private static bool TryParseGeo(string geo, out ThumbGeo g)
    {
        g = new ThumbGeo(0, 0, 0, 0, double.NaN, double.NaN);
        try
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in geo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && double.TryParse(kv[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                    map[kv[0]] = v;
            }
            g = new ThumbGeo(map["value"], map["max"], map["trackleft"],
                map["trackwidth"], map["thumbx"], map["thumbw"]);
            return !(double.IsNaN(g.ThumbX) || double.IsNaN(g.ThumbW)
                || g.TrackWidth <= 0 || g.ThumbW <= 0);
        }
        catch
        {
            return false;
        }
    }

    // --- empty state (no track) -------------------------------------------
    private static async Task<int> EmptyStateAsync(Window window, Views.ShellPage page, string report)
    {
        Write(report, "---- empty state (no track) ----");
        var fails = 0;
        var playEnabled = false; var playName = "";
        var rateEnabled = true;
        await OnUiAsync(window, () =>
        {
            playEnabled = page.Test_PlayEnabled();
            playName = page.Test_PlayName();
            rateEnabled = page.Test_RateEnabled();
        });
        Check(report, "play-disabled-no-file", !playEnabled, $"enabled={playEnabled}", ref fails);
        Check(report, "play-name-no-file", playName == "Play", $"name='{playName}'", ref fails);
        Check(report, "speed-disabled-no-file", !rateEnabled, $"enabled={rateEnabled}", ref fails);

        string title = "", artist = "", codec = "";
        var sliderOn = true; var sliderVal = -1.0;
        var openOn = false; var explorerOn = true; var stateOn = true;
        var coverShown = true;
        await OnUiAsync(window, () =>
        {
            page.Test_OpenTrackTip();
            title = page.Test_TrackTitle();
            artist = page.Test_TrackArtist();
            codec = page.Test_TrackCodec();
            sliderOn = page.Test_TipSliderEnabled();
            sliderVal = page.Test_TipSlider();
            openOn = page.Test_OpenFileEnabled();
            explorerOn = page.Test_ExplorerEnabled();
            stateOn = page.Test_VolumeStateEnabled();
            coverShown = page.Test_CoverShown();
            page.Test_CloseTips();
        });
        Check(report, "track-empty-lines", title == "-" && artist == "-" && codec == "-",
            $"title='{title}' artist='{artist}' codec='{codec}'", ref fails);
        Check(report, "track-empty-slider", !sliderOn && sliderVal == 0,
            $"enabled={sliderOn} value={sliderVal}", ref fails);
        Check(report, "track-empty-buttons", openOn && !explorerOn && !stateOn,
            $"open={openOn} explorer={explorerOn} state={stateOn}", ref fails);
        Check(report, "track-empty-cover", !coverShown, $"coverShown={coverShown}", ref fails);

        var speedOn = true; var keepChecked = true; var keepOn = true;
        var presetOn = true;
        await OnUiAsync(window, () =>
        {
            page.Test_OpenSpeedTip();
            speedOn = page.Test_SpeedSliderEnabled();
            keepChecked = page.Test_KeepPitchChecked();
            keepOn = page.Test_KeepPitchEnabled();
            presetOn = page.Test_PresetEnabled(0) || page.Test_PresetEnabled(1)
                || page.Test_PresetEnabled(2) || page.Test_PresetEnabled(3)
                || page.Test_PresetEnabled(4);
            page.Test_CloseTips();
        });
        Check(report, "speed-empty-slider", !speedOn, $"enabled={speedOn}", ref fails);
        Check(report, "speed-empty-keep", !keepChecked && !keepOn,
            $"checked={keepChecked} enabled={keepOn}", ref fails);
        Check(report, "speed-empty-presets", !presetOn, $"anyEnabled={presetOn}", ref fails);
        return fails;
    }

    // --- volume matrix ------------------------------------------------------
    private static async Task<int> VolumeMatrixAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string report)
    {
        Write(report, "---- volume matrix ----");
        var fails = 0;
        // The empty-state paint parks the slider at 0; start from 50 so the
        // first matrix step (0) is a real change that fires the handler.
        await OnUiAsync(window, () => page.Test_SetTipSlider(50));
        var glyphWant = new (double Level, string Glyph)[]
        {
            (0, "\uE993"), (1, "\uE994"), (33, "\uE994"), (34, "\uE995"),
            (66, "\uE995"), (67, "\uE992"), (100, "\uE992"),
        };
        foreach (var (level, glyph) in glyphWant)
        {
            string got = ""; var svc = -1.0;
            await OnUiAsync(window, () =>
            {
                page.Test_SetTipSlider(level);
                got = page.Test_VolumeGlyph();
                svc = audio.VolumePercent;
            });
            Check(report, $"glyph@{level}", got == glyph && Math.Abs(svc - level) < 1e-9,
                $"glyph='{Esc(got)}' want='{Esc(glyph)}' service={svc}", ref fails);
        }
        // Mute -> unmute restores exactly.
        await OnUiAsync(window, () => page.Test_SetTipSlider(50));
        await OnUiAsync(window, () => page.Test_ClickVolumeState());
        var muted = false; var g1 = "";
        await OnUiAsync(window, () => { muted = audio.IsMuted; g1 = page.Test_VolumeGlyph(); });
        Check(report, "mute", muted && g1 == "\uE993", $"muted={muted} glyph='{Esc(g1)}'", ref fails);
        await OnUiAsync(window, () => page.Test_ClickVolumeState());
        var vol = -1.0; var stillMuted = true;
        await OnUiAsync(window, () => { vol = audio.VolumePercent; stillMuted = audio.IsMuted; });
        Check(report, "unmute-restores", !stillMuted && Math.Abs(vol - 50) < 1e-9,
            $"muted={stillMuted} volume={vol}", ref fails);
        // Slider-to-0 then click restores the last non-zero level.
        AppSettings.LastNonZeroVolume = 37;
        await OnUiAsync(window, () => page.Test_SetTipSlider(0));
        await OnUiAsync(window, () => page.Test_ClickVolumeState());
        await OnUiAsync(window, () => { vol = audio.VolumePercent; stillMuted = audio.IsMuted; });
        Check(report, "zero-then-click-restores", !stillMuted && Math.Abs(vol - 37) < 1e-9,
            $"muted={stillMuted} volume={vol}", ref fails);
        // Dragging while muted unmutes at that level.
        await OnUiAsync(window, () => page.Test_ClickVolumeState()); // mute at 37
        await OnUiAsync(window, () => page.Test_SetTipSlider(80));
        await OnUiAsync(window, () => { vol = audio.VolumePercent; stillMuted = audio.IsMuted; });
        Check(report, "drag-while-muted-unmutes", !stillMuted && Math.Abs(vol - 80) < 1e-9,
            $"muted={stillMuted} volume={vol}", ref fails);
        // Slider writes flow into LastNonZeroVolume (temp store, silence off
        // for persistence only; output stays silent via audio.SilentMode).
        AppSettings.SilentMode = false;
        await OnUiAsync(window, () => page.Test_SetTipSlider(42));
        AppSettings.SilentMode = true;
        Check(report, "last-nonzero-persisted",
            Math.Abs(AppSettings.LastNonZeroVolume - 42) < 1e-9,
            $"lastNonZero={AppSettings.LastNonZeroVolume}", ref fails);
        return fails;
    }

    private static Task<int> SettingsMatrixAsync(string report)
    {
        // Fresh-store defaults on a second temp store (the volume matrix
        // already wrote to the first one). The RunAsync finally restores the
        // user's real store either way.
        Write(report, "---- last-nonzero setting ----");
        var fails = 0;
        AppSettings.ResetForTests(new Core.SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "playerbar-fresh-settings-" + Guid.NewGuid().ToString("N") + ".json")));
        Check(report, "default-50", Math.Abs(AppSettings.LastNonZeroVolume - 50) < 1e-9,
            $"value={AppSettings.LastNonZeroVolume}", ref fails);
        AppSettings.LastNonZeroVolume = 0;
        Check(report, "nonpositive-coerces-50", Math.Abs(AppSettings.LastNonZeroVolume - 50) < 1e-9,
            $"value={AppSettings.LastNonZeroVolume}", ref fails);
        AppSettings.LastNonZeroVolume = 37;
        Check(report, "roundtrip-37", Math.Abs(AppSettings.LastNonZeroVolume - 37) < 1e-9,
            $"value={AppSettings.LastNonZeroVolume}", ref fails);
        return Task.FromResult(fails);
    }

    // --- files matrix ---------------------------------------------------------
    private static async Task<int> FilesMatrixAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string folder, string report)
    {
        Write(report, "---- files matrix ----");
        var fails = 0;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Write(report, $"  SKIP folder not found: {folder}");
            return fails;
        }
        var files = Directory.GetFiles(folder)
            .Where(f => AudioExtensions.Contains(
                System.IO.Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Write(report, $"  files={files.Length}");
        var first = true;
        foreach (var file in files)
        {
            try
            {
                await audio.LoadAsync(file);
            }
            catch (Exception ex)
            {
                Check(report, $"load {Name(file)}", false, AudioErrorText.MessageOf(ex), ref fails);
                continue;
            }
            string title = "", artist = "", codec = "";
            var coverShown = false; var hasCoverBytes = false;
            var playName = ""; var playOn = false;
            await OnUiAsync(window, () =>
            {
                // A load raises no engine event by itself; the production
                // open paths refresh the chrome explicitly. Drive the same.
                page.Test_RefreshChrome();
                page.Test_OpenTrackTip();
                title = page.Test_TrackTitle();
                artist = page.Test_TrackArtist();
                codec = page.Test_TrackCodec();
                coverShown = page.Test_CoverShown();
                hasCoverBytes = (audio.Cover?.Bytes.Length ?? 0) > 0;
                playName = page.Test_PlayName();
                playOn = page.Test_PlayEnabled();
                page.Test_CloseTips();
            });
            var codecOk = System.Text.RegularExpressions.Regex.IsMatch(codec, @"^- \S+ \S+$");
            Check(report, $"tip {Name(file)}",
                title.Length > 0 && artist.Length > 0 && codecOk && coverShown == hasCoverBytes,
                $"title='{title}' artist='{artist}' codec='{codec}' coverShown={coverShown} coverBytes={hasCoverBytes}", ref fails);
            Check(report, $"play-enabled {Name(file)}", playOn && playName == "Play",
                $"enabled={playOn} name='{playName}'", ref fails);
            if (first)
            {
                first = false;
                fails += await PlayStatesAsync(window, audio, page, file, report);
            }
        }
        return fails;
    }

    private static async Task<int> PlayStatesAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string file, string report)
    {
        Write(report, "---- play/pause states ----");
        var fails = 0;
        await OnUiAsync(window, () => audio.Play());
        await Task.Delay(700);
        var name = "";
        await OnUiAsync(window, () => name = page.Test_PlayName());
        Check(report, "playing-shows-pause", name == "Pause", $"name='{name}'", ref fails);
        await OnUiAsync(window, () => audio.Pause());
        await Task.Delay(300);
        await OnUiAsync(window, () => name = page.Test_PlayName());
        Check(report, "paused-shows-play", name == "Play", $"name='{name}'", ref fails);
        // Eye hook must not crash with no subscribers.
        try
        {
            await OnUiAsync(window, () => page.Test_ClickEye());
            var eyeName = "";
            var eyeOn = false;
            await OnUiAsync(window, () => { eyeName = page.Test_EyeName(); eyeOn = page.Test_EyeEnabled(); });
            Check(report, "eye-hook", eyeName == "Jump to current lyric line" && eyeOn,
                $"name='{eyeName}' enabled={eyeOn}", ref fails);
        }
        catch (Exception ex)
        {
            Check(report, "eye-hook", false, ex.GetType().Name, ref fails);
        }
        // Ended state: seek to the end and let the tick finish the track.
        await OnUiAsync(window, () => { audio.SeekTo(audio.Duration.TotalSeconds); audio.Play(); });
        await Task.Delay(1200);
        var playing = true;
        await OnUiAsync(window, () => { name = page.Test_PlayName(); playing = audio.IsPlaying; });
        Check(report, "ended-shows-play", !playing && name == "Play",
            $"playing={playing} name='{name}'", ref fails);
        _ = file;
        return fails;
    }

    // --- speed matrix -----------------------------------------------------------
    private static async Task<int> SpeedMatrixAsync(
        Window window, AudioEngine audio, Views.ShellPage page, string report)
    {
        Write(report, "---- speed matrix ----");
        var fails = 0;
        var rates = new[] { 0.25, 0.5, 0.85, 1.0, 1.25, 1.5, 2.0 };
        var widths = new List<double>();
        foreach (var rate in rates)
        {
            await OnUiAsync(window, () => page.Test_SetSpeedSlider(rate));
            await Task.Delay(250);
            var text = "";
            var w = 0.0;
            await OnUiAsync(window, () => { text = page.Test_SpeedText(); w = page.Test_SpeedWidth(); });
            widths.Add(w);
            Check(report, $"speed-text {rate}", text == SpeedDisplay.Format(rate),
                $"text='{text}' want='{SpeedDisplay.Format(rate)}' width={w:F1}", ref fails);
        }
        var spread = widths.Max() - widths.Min();
        Check(report, "speed-width-identical", spread < 1.0,
            $"widths={string.Join(",", widths.Select(w => w.ToString("F1")))}", ref fails);
        // Presets: exactly one on per preset, none for 0.85.
        for (var i = 0; i < 5; i++)
        {
            await OnUiAsync(window, () => page.Test_ClickPreset(i));
            await Task.Delay(200);
            var states = new bool[5];
            await OnUiAsync(window, () =>
            {
                for (var j = 0; j < 5; j++) states[j] = page.Test_PresetChecked(j);
            });
            var onCount = states.Count(s => s);
            Check(report, $"preset-{i}-only", onCount == 1 && states[i],
                $"states={string.Join(",", states.Select(s => s ? "1" : "0"))}", ref fails);
        }
        await OnUiAsync(window, () => page.Test_SetSpeedSlider(0.85));
        await Task.Delay(200);
        var anyOn = true;
        await OnUiAsync(window, () =>
        {
            anyOn = page.Test_PresetChecked(0) || page.Test_PresetChecked(1)
                || page.Test_PresetChecked(2) || page.Test_PresetChecked(3)
                || page.Test_PresetChecked(4);
        });
        Check(report, "preset-none-for-085", !anyOn, $"anyOn={anyOn}", ref fails);
        // Right-click cycle from 1x, six steps.
        await OnUiAsync(window, () => page.Test_SetSpeedSlider(1.0));
        var seq = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            await OnUiAsync(window, () => page.Test_CycleSpeed());
            double r = 0;
            await OnUiAsync(window, () => r = audio.PlaybackRate);
            seq.Add(r);
        }
        Check(report, "cycle-sequence",
            seq.SequenceEqual([1.5, 1.25, 0.25, 0.5, 1.0, 1.5]),
            $"seq={string.Join(",", seq)}", ref fails);
        await OnUiAsync(window, () => page.Test_SetSpeedSlider(0.85));
        await OnUiAsync(window, () => page.Test_CycleSpeed());
        double after = 0;
        await OnUiAsync(window, () => after = audio.PlaybackRate);
        Check(report, "cycle-from-nonpreset", Math.Abs(after - 1.0) < 1e-9,
            $"rate={after}", ref fails);
        // Middle reset from every preset; the tip stays closed throughout.
        for (var i = 0; i < 5; i++)
        {
            await OnUiAsync(window, () => page.Test_ClickPreset(i));
            await OnUiAsync(window, () => page.Test_MiddleReset());
            double r = 0; var open = true;
            await OnUiAsync(window, () => { r = audio.PlaybackRate; open = page.Test_SpeedTipOpen(); });
            Check(report, $"middle-reset-from-{i}", Math.Abs(r - 1.0) < 1e-9 && !open,
                $"rate={r} tipOpen={open}", ref fails);
        }
        // Keep-pitch checkbox drives the engine.
        await OnUiAsync(window, () => page.Test_SetKeepPitch(false));
        var keep = true;
        await OnUiAsync(window, () => keep = audio.KeepPitch);
        Check(report, "keep-pitch-off", !keep, $"keep={keep}", ref fails);
        await OnUiAsync(window, () => page.Test_SetKeepPitch(true));
        await OnUiAsync(window, () => keep = audio.KeepPitch);
        Check(report, "keep-pitch-on", keep, $"keep={keep}", ref fails);
        return fails;
    }

    // --- layout matrix ------------------------------------------------------------
    private static async Task<int> LayoutMatrixAsync(Window window, Views.ShellPage page, string report)
    {
        Write(report, "---- layout matrix ----");
        var fails = 0;
        foreach (var width in new[] { 1400, 1116, 900, 750, 672, 600 })
        {
            await OnUiAsync(window, () => page.Test_ResizeWindow(width));
            await Task.Delay(900);
            var verdict = "";
            await OnUiAsync(window, () => verdict = page.Test_BarLayout());
            Check(report, $"layout@{width}", verdict.StartsWith("OK", StringComparison.Ordinal),
                verdict, ref fails);
            var metrics = "";
            await OnUiAsync(window, () => metrics = page.Test_BarMetrics());
            Check(report, $"metrics@{width}", metrics.StartsWith("OK", StringComparison.Ordinal),
                metrics, ref fails);
        }
        return fails;
    }

    private static async Task<int> BrushReportAsync(Window window, Views.ShellPage page, string report)
    {
        Write(report, "---- brushes ----");
        var fails = 0;
        var brush = "";
        await OnUiAsync(window, () => brush = page.Test_BarBrush());
        Check(report, "bar-brush", brush.StartsWith("AcrylicBrush", StringComparison.Ordinal), brush, ref fails);
        Write(report, "  tips: default TeachingTip style (TeachingTipTransientBackground -> AcrylicInAppFillColorDefaultBrush); "
            + "no TeachingTip.SystemBackdrop in SDK 1.8; see screenshots for the over-content look.");
        return fails;
    }

    private static string Name(string path) => System.IO.Path.GetFileName(path);

    // Glyphs are single PUA codepoints: render them as U+XXXX so the report
    // is readable and greppable without font rendering.
    private static string Esc(string s) =>
        string.Concat(s.Select(c => $"U+{(int)c:X4}"));

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
    }
}
