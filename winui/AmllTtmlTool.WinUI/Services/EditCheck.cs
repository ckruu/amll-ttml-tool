// --edit-test <file> [report]: self-driving Phase-3 verification.
// Imports through the real funnel, then drives the REAL UI-thread paths
// (chip commits, structure ops, ribbon, undo routing, perf, stress, edge)
// via the ShellPage Test_ hooks. Self-closes. DEBUG-only.
#if DEBUG
using Microsoft.UI.Xaml;
using AmllTtmlTool.Core.Lyrics;

namespace AmllTtmlTool.WinUI.Services;

internal sealed class RefWord
{
    public string Text = "";
    public string Roman = "";
    public int Start;
    public int End;
    public bool Obscene;
    public int Beats;
}

internal sealed class RefLine
{
    public List<RefWord> Words = new();
    public string Trans = "";
    public string Roman = "";
    public int Start;
    public int End;
    public bool Bg;
    public bool Duet;
    public bool Ignore;
}

internal static class EditCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "edit-test-report.txt");

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        return tcs.Task;
    }

    private static Task<T> OnUiAsync<T>(Window window, Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(func()); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        return tcs.Task;
    }

    private static string DocFingerprint(Views.ShellPage page)
    {
        // Built from the model hooks (same fields the Core fingerprint uses).
        var parts = new List<string>();
        var n = page.Test_LyricLineCount();
        for (var li = 0; li < n; li++)
        {
            var nw = page.Test_LyricWordCount(li);
            parts.Add($"L[{page.Test_LineTimes(li)},{page.Test_LineFlags(li)},{page.Test_LineTranslation(li)},{page.Test_LineRomanization(li)}]");
            for (var wi = 0; wi < nw; wi++)
                parts.Add($"W[{page.Test_WordTimes(li, wi)},{page.Test_WordText(li, wi)},{page.Test_WordRoman(li, wi)},{page.Test_WordFlags(li, wi)}]");
            parts.Add("/");
        }
        return string.Join("|", parts);
    }

    public static async Task RunAsync(
        Window window, Views.ShellPage page, LyricStore store, FileIntake intake,
        string lyricFile, string? report)
    {
        report ??= DefaultReportFile;
        var fails = 0;
        void Check(string name, bool cond, string detail)
        {
            Write(report, $"  {(cond ? "ok" : "FAIL")} {name} {detail}");
            if (!cond) fails++;
        }
        Write(report, $"run edit-test file='{lyricFile}'");
        try
        {
            intake.ErrorRecorder = (title, message, details) => { };
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(lyricFile);
            await intake.ImportFileAsync(storage);
            intake.ErrorRecorder = null;
            var lines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("import", lines >= 3, $"lines={lines}");
            var depth0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("import-one-step", depth0 >= 1, $"depth={depth0}");

            // --- 1. chip commits -----------------------------------------
            await OnUiAsync(window, () => page.Test_SelectLine(0));
            var orig0 = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "edited"));
            var d0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var d1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var model0 = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var chip0 = await OnUiAsync(window, () => page.Test_ChipBoxText(0, 0, false));
            Check("chip-commit-one-step", d1 == d0 + 1 && model0 == "edited" && chip0 == "edited",
                $"depth {d0}->{d1} model='{model0}' chip='{chip0}'");
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var d2 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("chip-noop-no-step", d2 == d1, $"depth={d2}");
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "scratch"));
            await OnUiAsync(window, () => page.Test_EscapeChip(0, 0, false));
            var esc0 = await OnUiAsync(window, () => page.Test_ChipBoxText(0, 0, false));
            var d3 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("chip-esc-reverts", esc0 == "edited" && d3 == d1, $"chip='{esc0}' depth={d3}");

            // Roman box + line fields.
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, true, "rom"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, true));
            var rom0 = await OnUiAsync(window, () => page.Test_WordRoman(0, 0));
            Check("chip-roman", rom0 == "rom", $"roman='{rom0}'");
            var dR = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_SetLineFieldText(0, true, "trans0"));
            await OnUiAsync(window, () => page.Test_CommitLineField(0, true));
            var tr0 = await OnUiAsync(window, () => page.Test_LineTranslation(0));
            var dT = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("line-trans", tr0 == "trans0" && dT == dR + 1, $"trans='{tr0}'");

            // IME-sim: composing Enter must not commit; next Enter commits.
            var dI = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_SetChipComposing(0, 0, false, true));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "ime-draft"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var dI2 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var mI = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_SetChipComposing(0, 0, false, false));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var dI3 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("ime-guard", dI2 == dI && mI == "edited" && dI3 == dI + 1, $"depths {dI}/{dI2}/{dI3}");

            // word| seed strips; empty text commits as "" (no auto-delete).
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "seed|"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var seed = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, ""));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var empty = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var emptyCount = await OnUiAsync(window, () => page.Test_LyricWordCount(0));
            Check("seed-and-empty", seed == "seed" && empty == "" && emptyCount > 0,
                $"seed='{seed}' empty='{empty}' count={emptyCount}");
            // Restore readable state for later sections.
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, orig0));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            await NoteShape(window, page, report, "post-s1", Check);

            // --- 1b. space chips + hug + pads ------------------------------
            // Self-contained: appends 3 scratch words to line 0, drives them
            // through spaces/glyphs, then undoes everything back.
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(0));
            var scDepth = await OnUiAsync(window, () => page.Test_UndoDepth());
            var scLineId = await OnUiAsync(window, () => page.Test_LineId(0));
            await OnUiAsync(window, () => page.Test_AddWordToLine(scLineId, "x"));
            await OnUiAsync(window, () => page.Test_AddWordToLine(scLineId, "x"));
            await OnUiAsync(window, () => page.Test_AddWordToLine(scLineId, "x"));
            var scCount = await OnUiAsync(window, () => page.Test_LyricWordCount(0));
            var s0 = scCount - 3;
            var s1 = scCount - 2;
            var s2 = scCount - 1;
            // 1/2/5 spaces render as dim "Space xN" labels (model keeps raw).
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s0, false, " "));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s0, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s1, false, "  "));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s1, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s2, false, "     "));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s2, false));
            var lab0 = await OnUiAsync(window, () => page.Test_SpaceLabel(0, s0));
            var lab1 = await OnUiAsync(window, () => page.Test_SpaceLabel(0, s1));
            var lab2 = await OnUiAsync(window, () => page.Test_SpaceLabel(0, s2));
            var mod1 = await OnUiAsync(window, () => page.Test_WordText(0, s1));
            Check("space-labels", lab0 == "Space x1" && lab1 == "Space x2" && lab2 == "Space x5" && mod1 == "  ",
                $"labels='{lab0}'/'{lab1}'/'{lab2}' model='{mod1}'");
            // The count follows edits live: 5 -> 3 spaces.
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s2, false, "   "));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s2, false));
            var lab2b = await OnUiAsync(window, () => page.Test_SpaceLabel(0, s2));
            Check("space-recount", lab2b == "Space x3", $"label='{lab2b}'");
            // Visual evidence (RenderTargetBitmap, headless-safe): card with
            // the three space chips, viewed by the operator after the run.
            var shotDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "edit-ui-artifacts");
            var shotPrefix = System.Environment.GetEnvironmentVariable("AMLL_SHOT_PREFIX");
            if (string.IsNullOrWhiteSpace(shotPrefix)) shotPrefix = "step1";
            var shotSpace = await await OnUiAsync(window, () =>
                page.Test_ShotCard(0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-space-chips.png")));
            Check("shot-space", shotSpace.StartsWith("ok:"), shotSpace);
            var shotChip = await await OnUiAsync(window, () =>
                page.Test_ShotChip(0, s0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-space-chipunit.png")));
            Check("shot-chipunit", shotChip.StartsWith("ok:"), shotChip);
            // Recounted chip (x3) + a normal word chip, for pixel reading.
            var shotChip2 = await await OnUiAsync(window, () =>
                page.Test_ShotChip(0, s2, System.IO.Path.Combine(shotDir, $"{shotPrefix}-space-x3-chipunit.png")));
            Check("shot-chipunit2", shotChip2.StartsWith("ok:"), shotChip2);
            var shotWord = await await OnUiAsync(window, () =>
                page.Test_ShotChip(0, 0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-word-chipunit.png")));
            Check("shot-wordchip", shotWord.StartsWith("ok:"), shotWord);
            // Hug: single glyph + long kana + long romaji units. Box widths
            // equal within 1px; the single glyph stays compact (< 64px, the
            // old TextBox theme floor).
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s0, false, "キ"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s0, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s1, false, "ハッピーバースデー"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s1, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s2, false, "alongromajiword"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s2, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, s2, true, "ruby-for-width"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, s2, true));
            var wS0 = await OnUiAsync(window, () => page.Test_ChipWidths(0, s0));
            var wS1 = await OnUiAsync(window, () => page.Test_ChipWidths(0, s1));
            var wS2 = await OnUiAsync(window, () => page.Test_ChipWidths(0, s2));
            var uS0 = await OnUiAsync(window, () => page.Test_ChipUnitWidth(0, s0));
            var hugOk = WidthsEqual(wS0) && WidthsEqual(wS1) && WidthsEqual(wS2)
                && double.TryParse(uS0, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var unitW)
                && unitW < 64;
            Check("chip-hug", hugOk, $"widths=[{wS0}][{wS1}][{wS2}] unit={uS0}");
            var shotHug = await await OnUiAsync(window, () =>
                page.Test_ShotCard(0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-hug.png")));
            Check("shot-hug", shotHug.StartsWith("ok:"), shotHug);
            // Gaps: chip main<->romaji == card Trans<->Roman (shared 6px).
            var chipGap = await OnUiAsync(window, () => page.Test_ChipGap(0, s2));
            var rowGap = await OnUiAsync(window, () => page.Test_RowGap(0));
            var gapsOk = chipGap == rowGap
                && double.TryParse(chipGap, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var gapV)
                && Math.Abs(gapV - 6) <= 1.0;
            Check("romaji-gap", gapsOk, $"chip={chipGap} row={rowGap}");
            // Card top pad == bottom pad, romaji on and off; "+" centered.
            var padsOn = await OnUiAsync(window, () => page.Test_CardPads(0));
            var dyOn = await OnUiAsync(window, () => page.Test_AddButtonDy(0));
            var padsOk = PadsEqual(padsOn) && DyOk(dyOn);
            Check("card-pads-on", padsOk, $"pads=[{padsOn}] dy={dyOn}");
            var wrOn = await OnUiAsync(window, () => page.Test_ToggleState("wordroman"));
            if (wrOn) await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
            await Task.Delay(400);
            var padsOff = await OnUiAsync(window, () => page.Test_CardPads(0));
            var dyOff = await OnUiAsync(window, () => page.Test_AddButtonDy(0));
            var gapOff = await OnUiAsync(window, () => page.Test_ChipGap(0, s2));
            var offOk = PadsEqual(padsOff) && DyOk(dyOff) && gapOff == "collapsed";
            Check("card-pads-off", offOk, $"pads=[{padsOff}] dy={dyOff} gap={gapOff}");
            await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
            await Task.Delay(400);
            var padsBack = await OnUiAsync(window, () => page.Test_CardPads(0));
            Check("card-pads-restore", PadsEqual(padsBack), $"pads=[{padsBack}]");
            // Undo everything back to the imported doc.
            var guard = 0;
            while (await OnUiAsync(window, () => page.Test_UndoDepth()) > scDepth && guard < 30)
            {
                await OnUiAsync(window, () => page.Test_Undo());
                guard++;
            }
            var scCountBack = await OnUiAsync(window, () => page.Test_LyricWordCount(0));
            Check("scratch-undone", scCountBack == scCount - 3, $"words={scCountBack}");

            // --- 2. width hug + caret ------------------------------------
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 1, false,
                "a much longer word for width hug"));
            var widths = await OnUiAsync(window, () => page.Test_ChipWidths(0, 1));
            var wparts = widths.Split(',');
            var hug = wparts.Length == 2
                && double.TryParse(wparts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var tw)
                && double.TryParse(wparts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rw)
                && Math.Abs(tw - rw) <= 1.0;
            Check("width-hug", hug, $"[{widths}]");
            await OnUiAsync(window, () => page.Test_CommitChip(0, 1, false));
            var focused = await OnUiAsync(window, () => page.Test_FocusChip(0, 0, false));
            var caretBefore = await OnUiAsync(window, () => page.Test_ChipCaret(0, 0, false));
            await OnUiAsync(window, () => page.Test_SetLineFieldText(1, true, "caret-probe"));
            await OnUiAsync(window, () => page.Test_CommitLineField(1, true));
            var caretAfter = await OnUiAsync(window, () => page.Test_ChipCaret(0, 0, false));
            var stillFocused = await OnUiAsync(window, () => page.Test_ChipIsFocused(0, 0, false));
            Check("caret-focus", focused && caretBefore == caretAfter && stillFocused,
                $"focus={focused} caret {caretBefore}->{caretAfter} still={stillFocused}");

            // --- 3. structure --------------------------------------------
            var n0 = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var opened = await OnUiAsync(window, () => page.Test_OpenInsertBox(0));
            await OnUiAsync(window, () => page.Test_SetInsertText("newword"));
            var di0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var committed = await OnUiAsync(window, () => page.Test_CommitInsertBox());
            var wAfter = await OnUiAsync(window, () => page.Test_LyricWordCount(0));
            var di1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var insertStill = await OnUiAsync(window, () => page.Test_InsertBoxOpen());
            Check("insert-word", opened && committed && di1 == di0 + 1 && insertStill,
                $"words={wAfter} depth {di0}->{di1}");
            await OnUiAsync(window, () => page.Test_EscapeInsertBox());
            var insertClosed = !(await OnUiAsync(window, () => page.Test_InsertBoxOpen()));
            Check("insert-esc", insertClosed, "closed");

            await OnUiAsync(window, () => page.Test_DeleteLineAt(1));
            var nDel = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var dd = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_Undo());
            var nUndo = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Redo());
            var nRedo = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Undo());
            var nUndo2 = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("delete-undo-redo", nDel == n0 - 1 && nUndo == n0 && nRedo == n0 - 1 && nUndo2 == n0,
                $"counts {n0}/{nDel}/{nUndo}/{nRedo}/{nUndo2} depth={dd}");

            await OnUiAsync(window, () => page.Test_SelectLine(Math.Min(2, n0 - 1)));
            var lastW0 = await OnUiAsync(window, () => page.Test_LyricWordCount(n0 - 1));
            await OnUiAsync(window, () => page.Test_AddLineAfterSelected());
            var nAdd = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var addFocus = await OnUiAsync(window, () => page.Test_InsertBoxOpen());
            Check("addline-focus", nAdd == n0 + 1 && addFocus, $"lines={nAdd}");
            await OnUiAsync(window, () => page.Test_EscapeInsertBox());
            // Remove THE added line (inserted right after the selected one),
            // not the last line: nAdd-1 only coincides when appending at the
            // end. Deleting the wrong line passed the count check while
            // silently dropping an original line (caught by docshape notes);
            // the last line's word count proves it survived.
            var addedAt = Math.Min(2, n0 - 1) + 1;
            await OnUiAsync(window, () => page.Test_DeleteLineAt(addedAt));
            var nRestored = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var lastW1 = await OnUiAsync(window, () => page.Test_LyricWordCount(n0 - 1));
            Check("addline-cleanup", nRestored == n0 && lastW1 == lastW0,
                $"lines={nRestored} lastwords={lastW0}->{lastW1}");

            // Split + merge with undo symmetry.
            var splitLine = -1; var splitWord = -1; var splitText = "";
            var total = await OnUiAsync(window, () => page.Test_LyricLineCount());
            for (var li = 0; li < total && splitLine < 0; li++)
            {
                var nw = await OnUiAsync(window, () => page.Test_LyricWordCount(li));
                for (var wi = 0; wi < nw; wi++)
                {
                    var t = await OnUiAsync(window, () => page.Test_WordText(li, wi));
                    if (t.Length >= 4) { splitLine = li; splitWord = wi; splitText = t; break; }
                }
            }
            if (splitLine >= 0)
            {
                var lid = await OnUiAsync(window, () => page.Test_LineId(splitLine));
                var wid = await OnUiAsync(window, () => page.Test_WordId(splitLine, splitWord));
                var wc0 = await OnUiAsync(window, () => page.Test_LyricWordCount(splitLine));
                var sok = await OnUiAsync(window, () => page.Test_SplitWordAt(lid, wid, new[] { 2 }));
                var wc1 = await OnUiAsync(window, () => page.Test_LyricWordCount(splitLine));
                var joined = await OnUiAsync(window, () =>
                    page.Test_WordText(splitLine, splitWord) + page.Test_WordText(splitLine, splitWord + 1));
                await OnUiAsync(window, () => page.Test_Undo());
                var wcU = await OnUiAsync(window, () => page.Test_LyricWordCount(splitLine));
                Check("split", sok && wc1 == wc0 + 1 && joined == splitText && wcU == wc0,
                    $"words {wc0}->{wc1}->{wcU} joined='{joined}'");
                // Merge the two halves back via word multi-select.
                await OnUiAsync(window, () => page.Test_SplitWordAt(lid, wid, new[] { 2 }));
                await OnUiAsync(window, () => page.Test_ClearEditSelection());
                await OnUiAsync(window, () => page.Test_ToggleChipWord(splitLine, splitWord));
                await OnUiAsync(window, () => page.Test_ToggleChipWord(splitLine, splitWord + 1));
                var mok = await OnUiAsync(window, () => page.Test_MergeSelectedWords(lid));
                var wcM = await OnUiAsync(window, () => page.Test_LyricWordCount(splitLine));
                var merged = await OnUiAsync(window, () => page.Test_WordText(splitLine, splitWord));
                await OnUiAsync(window, () => page.Test_Undo());
                var wcMU = await OnUiAsync(window, () => page.Test_LyricWordCount(splitLine));
                Check("merge", mok && wcM == wc0 && merged == splitText && wcMU == wc0 + 1,
                    $"words {wc0 + 1}->{wcM}->{wcMU} merged='{merged}'");
                await OnUiAsync(window, () => page.Test_Undo());
                await OnUiAsync(window, () => page.Test_ClearEditSelection());
            }
            else Check("split", false, "no splittable word found");

            // Move + duplicate + combine.
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0 }));
            var first0 = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_MovePrimaryLine(1));
            var moved0 = await OnUiAsync(window, () => page.Test_WordText(1, 0));
            await OnUiAsync(window, () => page.Test_Undo());
            var back0 = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            Check("moveline", moved0 == first0 && back0 == first0, $"'{first0}'");
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0 }));
            var nd0 = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_DuplicateSelectedLines());
            var nd1 = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Undo());
            var ndU = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("duplicate", nd1 == nd0 + 1 && ndU == nd0, $"{nd0}->{nd1}->{ndU}");
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0, 1 }));
            var cok = await OnUiAsync(window, () => page.Test_CombineSelectedLines());
            var nc = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Undo());
            var ncU = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("combine", cok && nc == nd0 - 1 && ncU == nd0, $"{nd0}->{nc}->{ncU}");
            await OnUiAsync(window, () => page.Test_ClearEditSelection());

            // List shortcuts through the real KeyDown dispatch (TextBox
            // focus guard included; real key routing is framework behavior
            // covered manually). VirtualKey: Delete=46, D=68, A=65,
            // Up=38, Down=40, Escape=27.
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0 }));
            var kLines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var kDel = await OnUiAsync(window, () => page.Test_ListKey(46, false, false));
            var kDelCount = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Undo());
            Check("key-delete", kDel && kDelCount == kLines - 1, $"lines={kDelCount}");
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0 }));
            await OnUiAsync(window, () => page.Test_ListKey(68, true, false));
            var kDupCount = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_Undo());
            Check("key-ctrl-d", kDupCount == kLines + 1, $"lines={kDupCount}");
            var kFirst = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0 }));
            await OnUiAsync(window, () => page.Test_ListKey(40, false, true));
            var kMoved = await OnUiAsync(window, () => page.Test_WordText(1, 0));
            await OnUiAsync(window, () => page.Test_Undo());
            Check("key-alt-down", kMoved == kFirst, $"'{kFirst}'");
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_ListKey(65, true, false));
            var kSelAll = await OnUiAsync(window, () => page.Test_SelectedLineCount());
            await OnUiAsync(window, () => page.Test_ListKey(27, false, false));
            var kSelClear = await OnUiAsync(window, () => page.Test_SelectedLineCount());
            Check("key-ctrla-esc", kSelAll == kLines && kSelClear == 0,
                $"sel={kSelAll}->{kSelClear}");
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await NoteShape(window, page, report, "post-s3", Check);

            // --- 4. ribbon ------------------------------------------------
            // (Direct word select: focusing an already-focused box would not
            // re-fire GotFocus, so selection setup never relies on focus.)
            await OnUiAsync(window, () => page.Test_SelectWord(0, 0));
            var rlStart = await OnUiAsync(window, () => page.Test_RibbonText("linestart"));
            var rlContent = await OnUiAsync(window, () => page.Test_RibbonText("content"));
            var mTimes0 = await OnUiAsync(window, () => page.Test_LineTimes(0));
            var expectStart = AmllTtmlTool.Core.Lyrics.LyricTime.Format(
                int.Parse(mTimes0.Split(',')[0]));
            Check("ribbon-follows", rlStart == expectStart && rlContent == orig0,
                $"ribbon='{rlStart}'/'{rlContent}' expect='{expectStart}'/'{orig0}'");
            var dr0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_SetRibbonText("content", "ribbon-edit"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("content"));
            var dr1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var mContent = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var cContent = await OnUiAsync(window, () => page.Test_ChipBoxText(0, 0, false));
            Check("ribbon-commit", dr1 == dr0 + 1 && mContent == "ribbon-edit" && cContent == "ribbon-edit",
                $"depth {dr0}->{dr1}");
            await OnUiAsync(window, () => page.Test_SetRibbonText("linestart", "bogus!!"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("linestart"));
            var dr2 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var rlStart2 = await OnUiAsync(window, () => page.Test_RibbonText("linestart"));
            Check("ribbon-invalid", dr2 == dr1 && rlStart2 == rlStart,
                $"depth={dr2} reverted='{rlStart2}'");
            var df0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var flagsBefore = await OnUiAsync(window, () =>
                page.Test_LineFlags(0) + ";" + page.Test_WordFlags(0, 0));
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("bg"));
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("duet"));
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("ignoresync"));
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("obscene"));
            var df1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var flags = await OnUiAsync(window, () =>
                page.Test_LineFlags(0) + ";" + page.Test_WordFlags(0, 0));
            var fb = flagsBefore.Split(';');
            var lb = fb[0].Split(','); var wb = fb[1].Split(',');
            var expectFlags = $"{!bool.Parse(lb[0])},{!bool.Parse(lb[1])},{!bool.Parse(lb[2])};" +
                $"{!bool.Parse(wb[0])},{wb[1]}";
            Check("ribbon-flags", df1 == df0 + 4 && flags == expectFlags,
                $"depth {df0}->{df1} flags={flags} expect={expectFlags}");
            for (var i = 0; i < 4; i++) await OnUiAsync(window, () => page.Test_Undo());
            await OnUiAsync(window, () => page.Test_SetRibbonText("beats", "3"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("beats"));
            var beats = await OnUiAsync(window, () => page.Test_WordEmptyBeat(0, 0));
            await OnUiAsync(window, () => page.Test_SetRibbonText("beats", "abc"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("beats"));
            var beats2 = await OnUiAsync(window, () => page.Test_WordEmptyBeat(0, 0));
            Check("ribbon-beats", beats == 3 && beats2 == 0, $"{beats}->{beats2}");
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("duration"));
            var durMode = await OnUiAsync(window, () => page.Test_DurationMode());
            var durText = await OnUiAsync(window, () => page.Test_RibbonText("lineend"));
            var mTimes = await OnUiAsync(window, () => page.Test_LineTimes(0));
            var mt = mTimes.Split(',');
            var expectDur = (int.Parse(mt[1]) - int.Parse(mt[0])).ToString();
            Check("ribbon-duration", durMode && durText == expectDur, $"dur='{durText}'");
            await OnUiAsync(window, () => page.Test_SetRibbonText("lineend", "+500"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("lineend"));
            var mTimes2 = await OnUiAsync(window, () => page.Test_LineTimes(0));
            Check("ribbon-delta", mTimes2.Split(',')[1] ==
                (int.Parse(mt[1]) + 500).ToString(), $"times={mTimes2}");
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("duration"));
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            var enAll = await OnUiAsync(window, () =>
                page.Test_RibbonEnabled("linestart") || page.Test_RibbonEnabled("content")
                || page.Test_RibbonEnabled("trans") || page.Test_RibbonEnabled("bg"));
            Check("ribbon-disabled-empty", !enAll, "all disabled");
            await NoteShape(window, page, report, "post-s4", Check);

            // --- 4b. ribbon word section: 2 rows, roman collapse, multi ---
            var wgRows = await OnUiAsync(window, () => page.Test_WordGroupRows());
            Check("ribbon-2row", wgRows == 2, $"rows={wgRows}");
            // Roman visible with the toggle on; collapsed (Beats keeps the
            // columns: group shrinks by about the roman pair) with it off.
            var wrWas = await OnUiAsync(window, () => page.Test_ToggleState("wordroman"));
            if (!wrWas) await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
            var wOn = await OnUiAsync(window, () => page.Test_WordGroupWidth());
            var romanOn = await OnUiAsync(window, () => page.Test_WordRomanVisible());
            await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
            await Task.Delay(200);
            var romanOff = await OnUiAsync(window, () => page.Test_WordRomanVisible());
            var wOff = await OnUiAsync(window, () => page.Test_WordGroupWidth());
            await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
            await Task.Delay(200);
            var romanBack = await OnUiAsync(window, () => page.Test_WordRomanVisible());
            // Beats shares Roman's columns, so hiding Roman only sheds the
            // roman-over-beats excess (~15-20px: label 42->35, box 88->80);
            // ~0 with roman still visible would mean a leftover column (the
            // visibility asserts already pin the collapsed state).
            var romanOk = romanOn && !romanOff && romanBack
                && wOn - wOff >= 10 && wOn - wOff <= 40;
            Check("ribbon-roman-toggle", romanOk,
                $"vis={romanOn}/{romanOff}/{romanBack} w={wOn:F2}->{wOff:F2}");
            // Multi-select: caption "3 selected"; short placeholder + full
            // tooltip on every word field; placeholder text fits its box.
            // (Beats needs a differing value first: the fixture has all 0.)
            await OnUiAsync(window, () => page.Test_SelectWord(0, 2));
            await OnUiAsync(window, () => page.Test_SetRibbonText("beats", "3"));
            await OnUiAsync(window, () => page.Test_CommitRibbon("beats"));
            await OnUiAsync(window, () => page.Test_SelectWord(0, 0));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 1));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 2));
            var caption = await OnUiAsync(window, () => page.Test_WordCaption());
            Check("ribbon-count", caption == "3 selected|Visible", $"'{caption}'");
            var multiOk = true;
            var multiDetail = "";
            foreach (var which in new[] { "wordstart", "wordend", "content", "romanword", "beats" })
            {
                var st = await OnUiAsync(window, () => page.Test_MultiState(which));
                var parts = st.Split('|');
                if (parts.Length != 4 || parts[0] != "Multiple\u2026" || parts[1] != "Multiple values"
                    || !TryParseDouble(parts[2], out var textW)
                    || !TryParseDouble(parts[3], out var boxW)
                    || textW > boxW - 6)
                {
                    multiOk = false;
                    multiDetail += $"{which}=[{st}] ";
                }
            }
            Check("ribbon-multi", multiOk, multiDetail);
            // Same-row baselines align.
            var baselines = await OnUiAsync(window, () => page.Test_BarBaselines());
            var bParts = baselines.Split(',');
            var baseOk = bParts.Length == 2
                && TryParseDouble(bParts[0], out var r0dev) && r0dev <= 1.0
                && TryParseDouble(bParts[1], out var r1dev) && r1dev <= 1.0;
            Check("ribbon-baselines", baseOk, $"dev=[{baselines}]");
            // Width tiers: no clipping at 1400/1116/900/750/672.
            var tierOk = true;
            var tierDetail = "";
            foreach (var w in new[] { 1400, 1116, 900, 750, 672 })
            {
                var setW = await OnUiAsync(window, () => page.Test_SetWindowWidth(w));
                await Task.Delay(500);
                var rep = await OnUiAsync(window, () => page.Test_BarReport());
                Write(report, $"  note tier w={w} [{rep}] set=[{setW}]");
                if (!rep.StartsWith("ok:"))
                {
                    tierOk = false;
                    tierDetail += $"{w}=[{rep}] set=[{setW}] ";
                }
            }
            Check("ribbon-tiers", tierOk, tierDetail);
            await OnUiAsync(window, () => page.Test_SetWindowWidth(1280));
            await Task.Delay(300);
            // Single selection hides the caption again (no leftover column).
            await OnUiAsync(window, () => page.Test_SelectWord(0, 0));
            var caption1 = await OnUiAsync(window, () => page.Test_WordCaption());
            Check("ribbon-count-single", caption1.EndsWith("|Collapsed"), $"'{caption1}'");

            // --- 4c. selection model, visible selection, right-click -------
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(0));
            await Task.Delay(200);
            // Word gestures: single / ctrl-toggle / shift-range; the line
            // follows every word selection (original).
            await OnUiAsync(window, () => page.Test_WordGesture(0, 1, false, false));
            var g0w = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            var g0l = await OnUiAsync(window, () => page.Test_SelectedIndices());
            var g0p = await OnUiAsync(window, () => page.Test_Primary());
            Check("gesture-single", g0w == "0:1" && g0l == "0" && g0p == "0,1",
                $"words=[{g0w}] lines=[{g0l}] primary=[{g0p}]");
            await OnUiAsync(window, () => page.Test_WordGesture(0, 2, true, false));
            var g1w = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            await OnUiAsync(window, () => page.Test_WordGesture(0, 1, true, false));
            var g2w = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            Check("gesture-ctrl", g1w == "0:1,0:2" && g2w == "0:2",
                $"add=[{g1w}] toggle-off=[{g2w}]");
            await OnUiAsync(window, () => page.Test_WordGesture(0, 0, false, false));
            await OnUiAsync(window, () => page.Test_WordGesture(0, 2, false, true));
            var g3w = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            var g3p = await OnUiAsync(window, () => page.Test_Primary());
            var g3l = await OnUiAsync(window, () => page.Test_SelectedIndices());
            Check("gesture-shift", g3w == "0:0,0:1,0:2" && g3p == "0,2" && g3l == "0",
                $"range=[{g3w}] primary=[{g3p}] lines=[{g3l}]");
            // Lines: single / ctrl / shift-range / Ctrl+A / Esc.
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_LineGesture(1, false, false));
            var l0 = await OnUiAsync(window, () => page.Test_SelectedIndices());
            await OnUiAsync(window, () => page.Test_LineGesture(2, true, false));
            var l1 = await OnUiAsync(window, () => page.Test_SelectedIndices());
            await OnUiAsync(window, () => page.Test_LineGesture(1, true, false));
            var l2 = await OnUiAsync(window, () => page.Test_SelectedIndices());
            Check("line-ctrl", l0 == "1" && l1 == "1,2" && l2 == "2",
                $"single=[{l0}] add=[{l1}] off=[{l2}]");
            await OnUiAsync(window, () => page.Test_LineGesture(0, false, true));
            var l3 = await OnUiAsync(window, () => page.Test_SelectedIndices());
            Check("line-shift", l3 == "0,1,2", $"range=[{l3}]");
            await OnUiAsync(window, () => page.Test_ListKey(65, true, false));
            var lAll = await OnUiAsync(window, () => page.Test_SelectedLineCount());
            var lc = await OnUiAsync(window, () => page.Test_LyricLineCount());
            await OnUiAsync(window, () => page.Test_ListKey(27, false, false));
            var lCleared = await OnUiAsync(window, () => page.Test_SelectedLineCount());
            Check("line-ctrla-esc", lAll == lc && lCleared == 0,
                $"all={lAll}/{lc} cleared={lCleared}");
            // Visible selection: outlines + fills + ribbon caption agree.
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 1, 2 }));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 0));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 1));
            await Task.Delay(150);
            var vLines = await OnUiAsync(window, () => page.Test_SelectedIndices());
            var vWords = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            var vInd = await OnUiAsync(window, () => page.Test_IndicatedIndices());
            var vWind = await OnUiAsync(window, () => page.Test_WordIndicated());
            var vCap = await OnUiAsync(window, () => page.Test_WordCaption());
            var vFill1 = await OnUiAsync(window, () => page.Test_LineFill(1));
            var vFill3 = await OnUiAsync(window, () => page.Test_LineFill(3));
            var vOut00 = await OnUiAsync(window, () => page.Test_ChipOutline(0, 0));
            var vOut02 = await OnUiAsync(window, () => page.Test_ChipOutline(0, 2));
            var visOk = vLines == "0,1,2" && vWords == "0:0,0:1"
                && vInd == "0,1,2" && vWind == "0:0,0:1"
                && vCap == "2 selected|Visible"
                && vFill1 == "selected" && vFill3 == "default"
                && vOut00 == "shown" && vOut02 == "hidden";
            Check("visible-selection", visOk,
                $"lines=[{vLines}] words=[{vWords}] ind=[{vInd}] wind=[{vWind}] cap=[{vCap}] fill={vFill1}/{vFill3} out={vOut00}/{vOut02}");
            var shotSel = await await OnUiAsync(window, () =>
                page.Test_ShotCard(0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-selected-card.png")));
            Check("shot-selected", shotSel.StartsWith("ok:"), shotSel);
            var shotOut = await await OnUiAsync(window, () =>
                page.Test_ShotChip(0, 0, System.IO.Path.Combine(shotDir, $"{shotPrefix}-selected-chipunit.png")));
            Check("shot-outline", shotOut.StartsWith("ok:"), shotOut);
            // Focus preserves the set, moves the primary (tab-friendly).
            // The GotFocus handler logic is driven directly (deterministic):
            // this headless run proves Focus() moves XAML focus state
            // without routing the event (got=0 below), so event delivery
            // itself is a manual active-window step.
            await OnUiAsync(window, () => page.Test_AnchorBox(0, 2, false));
            var fWords = await OnUiAsync(window, () => page.Test_SelectedWordIdx());
            var fPrim = await OnUiAsync(window, () => page.Test_Primary());
            await OnUiAsync(window, () => page.Test_FocusChip(0, 2, false));
            var fsWait = "";
            for (var w = 0; w < 20; w++)
            {
                fsWait = await OnUiAsync(window, () => page.Test_FocusState());
                if (fsWait == "chipbox 0:2:M") break;
                await Task.Delay(100);
            }
            var fGot = await OnUiAsync(window, () => page.Test_GotFocusCount());
            Check("focus-anchor", fWords == "0:0,0:1" && fPrim == "0,2",
                $"words=[{fWords}] primary=[{fPrim}]");
            Write(report, $"  note focus-delivery focus=[{fsWait}] got={fGot}");
            // EDIT entry: double-click path via hook + focus state.
            await OnUiAsync(window, () => page.Test_EnterWordEdit(0, 0, false));
            var fState = await OnUiAsync(window, () => page.Test_FocusState());
            Check("edit-entry", fState == "chipbox 0:0:M", $"focus=[{fState}]");
            // F2 enters EDIT on the primary word from list focus (113=F2).
            await OnUiAsync(window, () => page.Test_WordGesture(0, 1, false, false));
            var f2ok = await OnUiAsync(window, () => page.Test_ListKey(113, false, false));
            var f2State = await OnUiAsync(window, () => page.Test_FocusState());
            Check("edit-f2", f2ok && f2State == "chipbox 0:1:M",
                $"ok={f2ok} focus=[{f2State}]");
            // Right-click: six card zones on an unselected line select it
            // and open the same line menu; hit-testable backgrounds.
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            var hit = await OnUiAsync(window, () => page.Test_CardHittestable(0));
            var ctxOk = hit == "card:1;panel:1";
            var ctxDetail = $"hit=[{hit}] ";
            for (var zone = 0; zone < 6; zone++)
            {
                await OnUiAsync(window, () => page.Test_ClearEditSelection());
                var ctx = await OnUiAsync(window, () => page.Test_LineContext(3, zone));
                if (!ctx.Contains("sel:3;") || !ctx.Contains("Delete line"))
                {
                    ctxOk = false;
                    ctxDetail += $"zone{zone}=[{ctx}] ";
                }
            }
            Check("line-context", ctxOk, ctxDetail);
            // Inside a multi-selection the set is kept with count labels.
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 1, 2, 3 }));
            var ctxM = await OnUiAsync(window, () => page.Test_LineContext(2, 1));
            Check("line-context-multi",
                ctxM.Contains("sel:1,2,3") && ctxM.Contains("Delete 3 lines")
                && ctxM.Contains("Duplicate 3 lines") && ctxM.Contains("Move 3 lines up"),
                $"[{ctxM}]");
            // Word menu on an unselected word selects it first.
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            var ctxW = await OnUiAsync(window, () => page.Test_WordContext(0, 2));
            Check("word-context",
                ctxW.StartsWith("sel:0:2;") && ctxW.Contains("Split word...")
                && ctxW.Contains("Delete selected word"),
                $"[{ctxW}]");
            // Multi-selection ops (each exactly one history step).
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_WordGesture(0, 0, false, false));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 1));
            var mDepth0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("obscene"));
            var mOb0 = await OnUiAsync(window, () => page.Test_WordFlags(0, 0));
            var mOb1 = await OnUiAsync(window, () => page.Test_WordFlags(0, 1));
            var mDepth1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("multi-obscene", mOb0.StartsWith("True,") && mOb1.StartsWith("True,")
                && mDepth1 == mDepth0 + 1, $"flags={mOb0}/{mOb1} depth {mDepth0}->{mDepth1}");
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 0, 1 }));
            var mDepth2 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_ClickRibbonToggle("bg"));
            var mBg0 = await OnUiAsync(window, () => page.Test_LineFlags(0));
            var mBg1 = await OnUiAsync(window, () => page.Test_LineFlags(1));
            var mDepth3 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("multi-bg", mBg0.StartsWith("True,") && mBg1.StartsWith("True,")
                && mDepth3 == mDepth2 + 1, $"flags={mBg0}/{mBg1} depth {mDepth2}->{mDepth3}");
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_WordGesture(0, 0, false, false));
            await OnUiAsync(window, () => page.Test_ToggleChipWord(0, 1));
            await OnUiAsync(window, () => page.Test_SetRibbonText("content", "multi-word"));
            var mDepth4 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_CommitRibbon("content"));
            var mTx0 = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var mTx1 = await OnUiAsync(window, () => page.Test_WordText(0, 1));
            var mDepth5 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("multi-content", mTx0 == "multi-word" && mTx1 == "multi-word"
                && mDepth5 == mDepth4 + 1, $"'{mTx0}'/'{mTx1}' depth {mDepth4}->{mDepth5}");
            await OnUiAsync(window, () => page.Test_SetRibbonText("wordstart", "00:01.000"));
            var mDepth6 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_CommitRibbon("wordstart"));
            var mTs0 = await OnUiAsync(window, () => page.Test_WordTimes(0, 0));
            var mTs1 = await OnUiAsync(window, () => page.Test_WordTimes(0, 1));
            var mDepth7 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("multi-times", mTs0.StartsWith("1000,") && mTs1.StartsWith("1000,")
                && mDepth7 == mDepth6 + 1, $"times={mTs0}/{mTs1} depth {mDepth6}->{mDepth7}");
            // Block move down by one (one step, undone below). Verified
            // through lines known non-empty (line 0 has words, line 3 is the
            // untouched duet line): moving {1,2} down yields [L0,L3,L1,L2].
            var shapeParts = new List<string>();
            var shapeN = await OnUiAsync(window, () => page.Test_LyricLineCount());
            for (var si = 0; si < shapeN; si++)
                shapeParts.Add(await OnUiAsync(window, () => page.Test_LyricWordCount(si)) + "w");
            Write(report, $"  note docshape lines={shapeN} words=[{string.Join(",", shapeParts)}]");
            var bAnchor = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var bThird = await OnUiAsync(window, () => page.Test_WordText(3, 0));
            await OnUiAsync(window, () => page.Test_SelectLines(new[] { 1, 2 }));
            var mSvc = await OnUiAsync(window, () => page.Test_ServiceLineIdx());
            var mLst = await OnUiAsync(window, () => page.Test_SelectedIndices());
            var mDepth8 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_MoveSelectedLines(1));
            var bStayed = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            var bShifted = await OnUiAsync(window, () => page.Test_WordText(1, 0));
            var mDepth9 = await OnUiAsync(window, () => page.Test_UndoDepth());
            Check("multi-move", bStayed == bAnchor && bShifted == bThird
                && mDepth9 == mDepth8 + 1, $"'{bStayed}'/'{bShifted}' anchor='{bAnchor}' third='{bThird}' svc=[{mSvc}] lst=[{mLst}] depth {mDepth8}->{mDepth9}");
            await OnUiAsync(window, () => page.Test_Undo());
            var bBack = await OnUiAsync(window, () => page.Test_WordText(1, 0));
            var bBack3 = await OnUiAsync(window, () => page.Test_WordText(3, 0));
            Check("multi-move-undo", bBack != bThird && bBack3 == bThird,
                $"'{bBack}'/'{bBack3}'");
            // Restore the words/flags touched above (times/flags/content).
            await OnUiAsync(window, () => page.Test_Undo());
            await OnUiAsync(window, () => page.Test_Undo());
            await OnUiAsync(window, () => page.Test_Undo());
            await OnUiAsync(window, () => page.Test_Undo());
            await OnUiAsync(window, () => page.Test_ClearEditSelection());

            // --- 5. undo routing + reveal ---------------------------------
            await OnUiAsync(window, () => page.Test_FocusChip(0, 0, false));
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "route-probe"));
            var route1 = await OnUiAsync(window, () => page.Test_UndoRoute());
            var du0 = await OnUiAsync(window, () => page.Test_UndoDepth());
            var routed1 = await OnUiAsync(window, () => page.Test_RouteUndo());
            var du1 = await OnUiAsync(window, () => page.Test_UndoDepth());
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var route2 = await OnUiAsync(window, () => page.Test_UndoRoute());
            var routed2 = await OnUiAsync(window, () => page.Test_RouteUndo());
            var selAfter = await OnUiAsync(window, () => page.Test_SelectedLine());
            Check("undo-route", route1 == "textbox" && !routed1 && du1 == du0
                && route2 == "document" && routed2 && selAfter == 0,
                $"routes {route1}/{route2} routed {routed1}/{routed2} sel={selAfter}");
            await OnUiAsync(window, () => page.Test_Redo());
            var redoModel = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            Check("redo-reveal", redoModel == "route-probe", $"'{redoModel}'");
            await OnUiAsync(window, () => page.Test_Undo());

            // --- 5b. card layout + selection indicator ---------------------
            // 80-line doc cycling plain/BG/V2/both with identical content
            // rows (3 words + trans/roman each).
            await OnUiAsync(window, () => page.Test_BuildSelectDoc());
            var selN = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("select-doc", selN == 80, $"lines={selN}");
            // Random selection changes + scrolling (seeded): realized cards
            // showing the bar must equal the selected set EXACTLY (restricted
            // to realized lines — virtualized lines have no container).
            var sRng = new Random(20261014);
            var selOk = true;
            var selDetail = "";
            for (var round = 0; round < 30; round++)
            {
                var mode = sRng.Next(3);
                if (mode == 0)
                {
                    var want = new HashSet<int>();
                    var count = sRng.Next(1, 6);
                    while (want.Count < count) want.Add(sRng.Next(80));
                    var arr = want.ToArray();
                    await OnUiAsync(window, () => page.Test_SelectLines(arr));
                }
                else if (mode == 1)
                {
                    await OnUiAsync(window, () => page.Test_ClearEditSelection());
                }
                else
                {
                    var one = sRng.Next(80);
                    await OnUiAsync(window, () => page.Test_SelectLines(new[] { one }));
                }
                await OnUiAsync(window, () => page.Test_ScrollLineIntoView(sRng.Next(80)));
                await Task.Delay(120);
                var selCsv = await OnUiAsync(window, () => page.Test_SelectedIndices());
                var indCsv = await OnUiAsync(window, () => page.Test_IndicatedIndices());
                var selIdx = selCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var expected = new List<string>();
                foreach (var s in selIdx)
                {
                    if (int.TryParse(s, out var si)
                        && await OnUiAsync(window, () => page.Test_IsLineRealized(si)))
                        expected.Add(s);
                }
                expected.Sort(StringComparer.Ordinal);
                var indParts = indCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (!expected.SequenceEqual(indParts))
                {
                    selOk = false;
                    selDetail = $"round={round} sel=[{selCsv}] realized-expected=[{string.Join(",", expected)}] indicated=[{indCsv}]";
                    break;
                }
            }
            Check("selection-indicator", selOk, selDetail);
            // First-chip x identical for plain/BG/V2/both + selected lines;
            // card height unchanged by selection and badges.
            var xs = new List<double>();
            var hs = new List<double>();
            var geoOk = true;
            var geoDetail = "";
            for (var li = 0; li < 8; li++)
            {
                await OnUiAsync(window, () => page.Test_ScrollLineIntoView(li));
                await Task.Delay(120);
                var hUn = await OnUiAsync(window, () => page.Test_CardHeight(li));
                await OnUiAsync(window, () => page.Test_SelectLine(li));
                await Task.Delay(60);
                var x = await OnUiAsync(window, () => page.Test_FirstChipX(li));
                var hSel = await OnUiAsync(window, () => page.Test_CardHeight(li));
                if (!TryParseDouble(x, out var xv) || !TryParseDouble(hUn, out var hUv)
                    || !TryParseDouble(hSel, out var hSv))
                {
                    geoOk = false;
                    geoDetail = $"line={li} x=[{x}] hUn=[{hUn}] hSel=[{hSel}]";
                    break;
                }
                xs.Add(xv);
                hs.Add(hUv);
                hs.Add(hSv);
            }
            if (geoOk)
            {
                xs.Sort();
                hs.Sort();
                geoOk = xs[^1] - xs[0] <= 1.0 && hs[^1] - hs[0] <= 1.0;
                geoDetail = $"x {xs[0]:F2}..{xs[^1]:F2} h {hs[0]:F2}..{hs[^1]:F2}";
            }
            Check("card-geometry", geoOk, geoDetail);
            // Consecutive card gaps identical on a scrolled long doc.
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(40));
            await Task.Delay(250);
            var gaps = await OnUiAsync(window, () => page.Test_CardGaps());
            var gapParts = gaps.Split(',');
            var cardGapsOk = gapParts.Length == 3
                && TryParseDouble(gapParts[0], out var gMin)
                && TryParseDouble(gapParts[1], out var gMax)
                && int.TryParse(gapParts[2], out var gN) && gN >= 3
                && gMax - gMin <= 1.0;
            Check("card-gaps", cardGapsOk, $"gaps=[{gaps}]");
            // Visual evidence: selected BG+V2 card + list strip.
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(3));
            await OnUiAsync(window, () => page.Test_SelectLine(3));
            await Task.Delay(200);
            var shotBadge = await await OnUiAsync(window, () =>
                page.Test_ShotCard(3, System.IO.Path.Combine(shotDir, $"{shotPrefix}-badges-card.png")));
            Check("shot-badges", shotBadge.StartsWith("ok:"), shotBadge);
            var shotList = await await OnUiAsync(window, () =>
                page.Test_ShotList(System.IO.Path.Combine(shotDir, $"{shotPrefix}-cards-list.png")));
            Check("shot-cards", shotList.StartsWith("ok:"), shotList);

            Write(report, "  note stress + perf + edge use fresh docs (see below)");

            // --- 6. stress: 200 seeded ops vs reference --------------------
            await RunStressAsync(window, page, report, Check);

            // --- 7. perf: 300x10 doc ---------------------------------------
            await OnUiAsync(window, () => page.Test_BuildPerfDoc());
            var perfLines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("perf-doc", perfLines == 300, $"lines={perfLines}");
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(150));
            await Task.Delay(400);
            // Median-of-3 rounds (same load-tolerance pattern as the toggle
            // budget): a single congested sample with system stalls must not
            // fail the run; each round drops its first 10 (warmup) as before.
            var p95s = new List<double>();
            for (var round = 0; round < 3; round++)
            {
                var lat = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    var ms = await OnUiAsync(window, () =>
                        page.Test_TypeChipChar(150, 5, false, (char)('a' + ((i + round * 7) % 26))));
                    if (ms >= 0) lat.Add(ms);
                }
                lat.Sort();
                var warm = lat.Count > 10 ? lat.Skip(10).ToList() : lat;
                lat = warm;
                var rp50 = lat.Count > 0 ? lat[lat.Count / 2] : -1;
                var rp95 = lat.Count > 0 ? lat[(int)(lat.Count * 0.95)] : -1;
                var rmax = lat.Count > 0 ? lat[^1] : -1;
                Write(report, $"  note typing-latency r{round} n={lat.Count} p50={rp50:F2} p95={rp95:F2} max={rmax:F2}");
                if (rp95 >= 0) p95s.Add(rp95);
            }
            var breakdown = await OnUiAsync(window, () => page.Test_TypeBreakdown());
            Write(report, $"  note type-breakdown setWall/layout/setCpu ms of last key: [{breakdown}]");
            p95s.Sort();
            var p95med = p95s.Count > 0 ? p95s[p95s.Count / 2] : -1;
            Check("typing-budget", p95med >= 0 && p95med < 16,
                $"median-p95={p95med:F2}ms rounds=[{string.Join(",", p95s.Select(v => v.ToString("F2")))}]");
            var wEq = await OnUiAsync(window, () => page.Test_ChipWidths(150, 5));
            Check("perf-width-hug", WidthsEqual(wEq), $"[{wEq}]");
            var topReal = await OnUiAsync(window, () => page.Test_RealizedLineCount());
            var topBoxes = await OnUiAsync(window, () => page.Test_RealizedTextBoxCount());
            // Median-of-5 (first-op JIT/layout warmup inflates singles).
            var insSamples = new List<double>();
            var addSamples = new List<double>();
            var delSamples = new List<double>();
            for (var r = 0; r < 5; r++)
            {
                insSamples.Add(await OnUiAsync(window, () => page.Test_TimedInsertLineAfter(150)));
                delSamples.Add(await OnUiAsync(window, () => page.Test_TimedDeleteLineAt(150)));
                addSamples.Add(await OnUiAsync(window, () => page.Test_TimedAddWord(150)));
                await OnUiAsync(window, () => page.Test_DeleteLineAt(150));
                await OnUiAsync(window, () => page.Test_Undo());
            }
            static double Med(List<double> xs)
            {
                xs.Sort();
                return xs[xs.Count / 2];
            }
            var insMs = Med(insSamples);
            var addWordMs = Med(addSamples);
            var delMs = Med(delSamples);
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(299));
            await Task.Delay(400);
            var botReal = await OnUiAsync(window, () => page.Test_RealizedLineCount());
            var botBoxes = await OnUiAsync(window, () => page.Test_RealizedTextBoxCount());
            Write(report, $"  note realized top={topReal}/{topBoxes} bottom={botReal}/{botBoxes} insertMs={insMs:F2}(median5, incl scroll+open+focus) addWordMs={addWordMs:F2}(median5, 10-word line) deleteMs={delMs:F2}(median5, empty line)");
            Check("perf-virtualized", botReal < 300, $"realized={botReal}");
            var front = await OnUiAsync(window, () => page.Test_BringToFront());
            Write(report, $"  note foreground={front} (frame sampling needs a composing window)");
            foreach (var op in new[] { "addword", "removeword", "addline", "deleteline" })
            {
                var stats = await await OnUiAsync(window, () => page.Test_StructuralFrames(op, 1200));
                Write(report, $"  note frames-{op} [{stats}]");
            }
            Check("perf-frames", true, "stats above");

            // --- 8. edge: no crash ------------------------------------------
            await OnUiAsync(window, () => page.Test_NewDocument());
            var emptyLines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var emptyVisible = await OnUiAsync(window, () => page.Test_EmptyStateVisible());
            await OnUiAsync(window, () => page.Test_InsertLineAfter(-1));
            var oneLine = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var openedEdge = await OnUiAsync(window, () => page.Test_OpenInsertBox(0));
            if (!openedEdge)
            {
                await Task.Delay(500);
                openedEdge = await OnUiAsync(window, () => page.Test_OpenInsertBox(0));
            }
            Check("edge-insert-open", oneLine == 1 && openedEdge, $"lines={oneLine} opened={openedEdge}");
            await OnUiAsync(window, () => page.Test_SetInsertText("solo"));
            var soloCommitted = await OnUiAsync(window, () => page.Test_CommitInsertBox());
            var oneWord = oneLine == 1
                ? await OnUiAsync(window, () => page.Test_LyricWordCount(0)) : -1;
            Check("edge-solo", soloCommitted && oneWord == 1, $"committed={soloCommitted} words={oneWord}");
            await OnUiAsync(window, () => page.Test_DeleteSelected());
            var wordless = await OnUiAsync(window, () => page.Test_LyricWordCount(0));
            Check("edge-empty-oneword", emptyLines == 0 && emptyVisible && oneLine == 1 && oneWord == 1 && wordless == 0,
                $"empty={emptyLines}/{emptyVisible} one={oneLine}/{oneWord} wordless={wordless}");
            // Re-add into the wordless line (insert-into-empty path), then
            // the huge/emoji commits.
            await OnUiAsync(window, () => page.Test_ClearEditSelection());
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(0));
            var reReal = await OnUiAsync(window, () => page.Test_IsLineRealized(0));
            var reOpened = await OnUiAsync(window, () => page.Test_OpenInsertBox(0));
            await OnUiAsync(window, () => page.Test_SetInsertText("back"));
            var reCommitted = await OnUiAsync(window, () => page.Test_CommitInsertBox());
            await OnUiAsync(window, () => page.Test_EscapeInsertBox());
            var reReal2 = await OnUiAsync(window, () => page.Test_IsLineRealized(0));
            var reCount = reReal2
                ? await OnUiAsync(window, () => page.Test_LyricWordCount(0)) : -1;
            Check("edge-readd", reReal && reOpened && reCommitted && reCount == 1,
                $"real={reReal}/{reReal2} opened={reOpened} committed={reCommitted} words={reCount}");
            var huge = new string('x', 5000);
            var hugeSet = await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, huge));
            var hugeStepped = await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var hugeBack = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_Undo());
            Check("edge-huge", hugeBack.Length == 5000, $"set={hugeSet} stepped={hugeStepped} len={hugeBack.Length}");
            await OnUiAsync(window, () => page.Test_SetChipBoxText(0, 0, false, "👨‍👩‍👧‍👦 é🎵"));
            await OnUiAsync(window, () => page.Test_CommitChip(0, 0, false));
            var emoji = await OnUiAsync(window, () => page.Test_WordText(0, 0));
            await OnUiAsync(window, () => page.Test_Undo());
            Check("edge-emoji", emoji.Length > 0, $"len={emoji.Length}");

            Write(report, fails == 0 ? "edit-test | OK" : $"edit-test | FAIL {fails}");
        }
        catch (Exception ex)
        {
            Write(report, "HARNESS-ERROR: " + ex);
        }
    }

    private static string FirstDiff(string doc, string reference)
    {
        var d = doc.Split('|');
        var r = reference.Split('|');
        var n = Math.Min(d.Length, r.Length);
        var diffs = new List<string>();
        var first = -1;
        for (var i = 0; i < n; i++)
        {
            if (d[i] != r[i])
            {
                if (first < 0) first = i;
                if (diffs.Count < 10) diffs.Add($"seg{i}:doc=[{d[i]}] ref=[{r[i]}]");
            }
        }
        if (first < 0) return $"prefix-equal len={d.Length}/{r.Length}";
        return $"ndiff={diffs.Count}+ len={d.Length}/{r.Length} " + string.Join(" ", diffs);
    }

    private static bool WidthsEqual(string box)
    {
        var parts = box.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var tw)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rw)
            && Math.Abs(tw - rw) <= 1.0;
    }

    private static bool PadsEqual(string pads)
    {
        var parts = pads.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var top)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bottom)
            && Math.Abs(top - bottom) <= 1.0;
    }

    private static bool DyOk(string dy) =>
        double.TryParse(dy, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
        && v <= 1.0;

    private static bool TryParseDouble(string s, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v);

    // One-line doc-shape note (line count + per-line word counts) to
    // pinpoint where a shape change happens.
    private static async Task NoteShape(
        Window window, Views.ShellPage page, string report, string tag,
        Action<string, bool, string> check)
    {
        try
        {
            var n = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var parts = new List<string>();
            for (var i = 0; i < n; i++)
            {
                var nw = await OnUiAsync(window, () => page.Test_LyricWordCount(i));
                var first = nw > 0
                    ? await OnUiAsync(window, () => page.Test_WordText(i, 0))
                    : "-";
                parts.Add($"{nw}w:{first}");
            }
            Write(report, $"  note docshape-{tag} lines={n} words=[{string.Join(",", parts)}]");
        }
        catch (Exception ex)
        {
            Write(report, $"  note docshape-{tag} ERROR {ex.GetType().Name}");
        }
    }

    // 200 deterministic random ops against a simple reference model.
    // Reference mirrors texts/counts/flags/times by index (split times via
    // the unit-tested SplitWeights); undo pops the snapshot stack, redo
    // re-applies the logged op — mirroring document history semantics.
    private sealed record Op(int Kind, int A, int B, string Text, int Extra);

    private static async Task RunStressAsync(
        Window window, Views.ShellPage page, string report,
        Action<string, bool, string> check)
    {
        const int seed = 20261013;
        const int maxHistory = 100; // EditDocument.MaxHistoryDepth
        var rng = new Random(seed);
        var journal = new List<string>();
        var refs = new List<RefLine>();
        // Lists used as stacks (back = top) so the history-cap drop can
        // evict the FRONT, mirroring EditDocument's RemoveAt(0).
        var snapshots = new List<List<RefLine>>();
        var opHist = new List<Op>();
        var redoOps = new Stack<Op>();
        var applied = 0;
        // Pre-stress (section) steps in doc history, untracked by opHist.
        var untracked = await OnUiAsync(window, () => page.Test_UndoDepth());

        List<RefLine> Clone(List<RefLine> src) => src.Select(l => new RefLine
        {
            Words = l.Words.Select(w => new RefWord
            {
                Text = w.Text, Roman = w.Roman, Start = w.Start, End = w.End,
                Obscene = w.Obscene, Beats = w.Beats,
            }).ToList(),
            Trans = l.Trans, Roman = l.Roman, Start = l.Start, End = l.End,
            Bg = l.Bg, Duet = l.Duet, Ignore = l.Ignore,
        }).ToList();

        string RefPrint(List<RefLine> rs)
        {
            var parts = new List<string>();
            foreach (var l in rs)
            {
                parts.Add($"L[{l.Start},{l.End},{l.Bg},{l.Duet},{l.Ignore},{l.Trans},{l.Roman}]");
                foreach (var w in l.Words)
                    parts.Add($"W[{w.Start},{w.End},{w.Text},{w.Roman},{w.Obscene},{w.Beats}]");
                parts.Add("/");
            }
            return string.Join("|", parts);
        }

        // Seed the reference from the live doc (imported fixture).
        var n0 = await OnUiAsync(window, () => page.Test_LyricLineCount());
        for (var li = 0; li < n0; li++)
        {
            var rl = new RefLine
            {
                Trans = await OnUiAsync(window, () => page.Test_LineTranslation(li)),
                Roman = await OnUiAsync(window, () => page.Test_LineRomanization(li)),
            };
            var lt = (await OnUiAsync(window, () => page.Test_LineTimes(li))).Split(',');
            rl.Start = int.Parse(lt[0]); rl.End = int.Parse(lt[1]);
            var lf = (await OnUiAsync(window, () => page.Test_LineFlags(li))).Split(',');
            rl.Bg = bool.Parse(lf[0]); rl.Duet = bool.Parse(lf[1]); rl.Ignore = bool.Parse(lf[2]);
            var nw = await OnUiAsync(window, () => page.Test_LyricWordCount(li));
            for (var wi = 0; wi < nw; wi++)
            {
                var wt = (await OnUiAsync(window, () => page.Test_WordTimes(li, wi))).Split(',');
                var wf = (await OnUiAsync(window, () => page.Test_WordFlags(li, wi))).Split(',');
                rl.Words.Add(new RefWord
                {
                    Text = await OnUiAsync(window, () => page.Test_WordText(li, wi)),
                    Roman = await OnUiAsync(window, () => page.Test_WordRoman(li, wi)),
                    Start = int.Parse(wt[0]), End = int.Parse(wt[1]),
                    Obscene = bool.Parse(wf[0]), Beats = int.Parse(wf[1]),
                });
            }
            refs.Add(rl);
        }

        async Task<string> DocPrint() => await OnUiAsync(window, () => DocFingerprint(page));

        // Applies op to the doc (via Test_ hooks), the reference, or both.
        // Redo must NOT touch the doc (Test_Redo already re-applied); it
        // only re-applies to the reference. Returns false for invalid ops.
        // UI-bound hooks need a realized container: scroll the target in.
        async Task Realize(int line)
        {
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(line));
        }

        async Task<bool> ApplyOp(Op op, bool skipDoc)
        {
            switch (op.Kind)
            {
                case 0: // set word text
                    if (op.A >= refs.Count || op.B >= refs[op.A].Words.Count) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        await OnUiAsync(window, () => page.Test_SetChipBoxText(op.A, op.B, false, op.Text));
                        await OnUiAsync(window, () => page.Test_CommitChip(op.A, op.B, false));
                    }
                    refs[op.A].Words[op.B].Text = op.Text.EndsWith("|") ? op.Text[..^1] : op.Text;
                    return true;
                case 1: // set roman
                    if (op.A >= refs.Count || op.B >= refs[op.A].Words.Count) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        await OnUiAsync(window, () => page.Test_SetChipBoxText(op.A, op.B, true, op.Text));
                        await OnUiAsync(window, () => page.Test_CommitChip(op.A, op.B, true));
                    }
                    refs[op.A].Words[op.B].Roman = op.Text;
                    return true;
                case 2: // set trans
                    if (op.A >= refs.Count) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        await OnUiAsync(window, () => page.Test_SetLineFieldText(op.A, true, op.Text));
                        await OnUiAsync(window, () => page.Test_CommitLineField(op.A, true));
                    }
                    refs[op.A].Trans = op.Text;
                    return true;
                case 3: // add word
                    if (op.A >= refs.Count) return false;
                    if (!skipDoc)
                    {
                        var lid = await OnUiAsync(window, () => page.Test_LineId(op.A));
                        await OnUiAsync(window, () => page.Test_AddWordToLine(lid, op.Text));
                    }
                    var t3 = op.Text.EndsWith("|") ? op.Text[..^1] : op.Text;
                    refs[op.A].Words.Add(new RefWord { Text = t3 });
                    return true;
                case 4: // remove word (select single + delete)
                    if (op.A >= refs.Count || op.B >= refs[op.A].Words.Count) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        await OnUiAsync(window, () => page.Test_ClearEditSelection());
                        await OnUiAsync(window, () => page.Test_ToggleChipWord(op.A, op.B));
                        await OnUiAsync(window, () => page.Test_DeleteSelected());
                    }
                    refs[op.A].Words.RemoveAt(op.B);
                    // Mirror normalizeLineTime (RemoveWordsCommand): empty
                    // words -> no-op; nonzero word bounds win.
                    var rl = refs[op.A];
                    if (rl.Words.Count > 0)
                    {
                        if (rl.Words[0].Start != 0) rl.Start = rl.Words[0].Start;
                        if (rl.Words[^1].End != 0) rl.End = rl.Words[^1].End;
                    }
                    return true;
                case 5: // add line after
                    if (op.A >= refs.Count) return false;
                    if (!skipDoc)
                    {
                        await OnUiAsync(window, () => page.Test_ClearEditSelection());
                        await OnUiAsync(window, () => page.Test_InsertLineAfter(op.A));
                    }
                    refs.Insert(op.A + 1, new RefLine());
                    if (!skipDoc)
                        await OnUiAsync(window, () => page.Test_EscapeInsertBox());
                    return true;
                case 6: // delete line
                    if (op.A >= refs.Count || refs.Count <= 1) return false;
                    if (!skipDoc)
                    {
                        await OnUiAsync(window, () => page.Test_ClearEditSelection());
                        await OnUiAsync(window, () => page.Test_DeleteLineAt(op.A));
                    }
                    refs.RemoveAt(op.A);
                    return true;
                case 7: // split word (k chosen at op creation: redo-safe)
                    if (op.A >= refs.Count || op.B >= refs[op.A].Words.Count) return false;
                    var sw = refs[op.A].Words[op.B];
                    var k = op.Extra;
                    if (sw.Text.Length < 3 || k <= 0 || k >= sw.Text.Length) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        var slid = await OnUiAsync(window, () => page.Test_LineId(op.A));
                        var swid = await OnUiAsync(window, () => page.Test_WordId(op.A, op.B));
                        var ok = await OnUiAsync(window, () => page.Test_SplitWordAt(slid, swid, new[] { k }));
                        if (!ok) return false;
                    }
                    var scratch = new EditWord { Text = sw.Text, StartTimeMs = sw.Start, EndTimeMs = sw.End };
                    var parts = AmllTtmlTool.Core.Lyrics.SplitWeights.DistributeTime(
                        scratch, new[] { sw.Text[..k], sw.Text[k..] },
                        AmllTtmlTool.Core.Lyrics.SplitWeights.DefaultPunctuationWeight);
                    refs[op.A].Words.RemoveAt(op.B);
                    var idx = op.B;
                    foreach (var p in parts)
                        refs[op.A].Words.Insert(idx++, new RefWord
                            { Text = p.Text, Start = p.StartTimeMs, End = p.EndTimeMs });
                    return true;
                case 8: // merge two words
                    if (op.A >= refs.Count || refs[op.A].Words.Count < 2) return false;
                    if (!skipDoc)
                    {
                        await Realize(op.A);
                        var mlid = await OnUiAsync(window, () => page.Test_LineId(op.A));
                        await OnUiAsync(window, () => page.Test_ClearEditSelection());
                        await OnUiAsync(window, () => page.Test_ToggleChipWord(op.A, 0));
                        await OnUiAsync(window, () => page.Test_ToggleChipWord(op.A, 1));
                        var mok = await OnUiAsync(window, () => page.Test_MergeSelectedWords(mlid));
                        if (!mok) return false;
                        await OnUiAsync(window, () => page.Test_ClearEditSelection());
                    }
                    var m0 = refs[op.A].Words[0];
                    var m1 = refs[op.A].Words[1];
                    refs[op.A].Words.RemoveRange(0, 2);
                    refs[op.A].Words.Insert(0, new RefWord
                        { Text = m0.Text + m1.Text, Start = m0.Start, End = m1.End });
                    return true;
                case 9: // toggle bg flag via ribbon
                    if (op.A >= refs.Count) return false;
                    if (!skipDoc)
                    {
                        await OnUiAsync(window, () => page.Test_SelectLine(op.A));
                        await OnUiAsync(window, () => page.Test_ClickRibbonToggle("bg"));
                    }
                    refs[op.A].Bg = !refs[op.A].Bg;
                    return true;
                default: return false;
            }
        }

        for (var i = 0; i < 200; i++)
        {
            var roll = rng.Next(100);
            if (roll < 12)
            {
                // undo
                var undone = await OnUiAsync(window, () => page.Test_Undo());
                if (undone && opHist.Count == 0)
                {
                    journal.Add($"u{i}:section-undo");
                    untracked--;
                }
                if (undone && opHist.Count > 0)
                {
                    var uop = opHist[^1];
                    opHist.RemoveAt(opHist.Count - 1);
                    journal.Add($"u{i}:undo kind={uop.Kind} a={uop.A} b={uop.B}");
                    redoOps.Push(uop);
                    refs = Clone(snapshots[^1]);
                    snapshots.RemoveAt(snapshots.Count - 1);
                    var fp = await DocPrint();
                    var rp = RefPrint(refs);
                    if (fp != rp)
                    {
                        var dd = await OnUiAsync(window, () => page.Test_UndoDepth());
                        check("stress-undo", false, $"iter={i} undid kind={uop.Kind} a={uop.A} b={uop.B} depth={dd} hist={opHist.Count}/snap={snapshots.Count}/untrk={untracked} {FirstDiff(fp, rp)}");
                        Write(report, $"seed={seed}");
                        Write(report, "  journal-tail: " + string.Join(" | ", journal.TakeLast(20)));
                        return;
                    }
                    applied++;
                }
            }
            else if (roll < 22)
            {
                // redo
                var redone = await OnUiAsync(window, () => page.Test_Redo());
                if (redone && redoOps.Count == 0)
                {
                    journal.Add($"u{i}:section-redo");
                    untracked++;
                }
                if (redone && redoOps.Count > 0)
                {
                    var op = redoOps.Pop();
                    journal.Add($"u{i}:redo kind={op.Kind} a={op.A} b={op.B}");
                    snapshots.Add(Clone(refs));
                    opHist.Add(op);
                    if (!await ApplyOp(op, skipDoc: true))
                    {
                        check("stress-redo", false, $"iter={i} kind={op.Kind} reapply failed");
                        Write(report, $"seed={seed}");
                        return;
                    }
                    var fp = await DocPrint();
                    var rp = RefPrint(refs);
                    if (fp != rp)
                    {
                        var dd = await OnUiAsync(window, () => page.Test_UndoDepth());
                        check("stress-redo", false, $"iter={i} kind={op.Kind} a={op.A} b={op.B} depth={dd} hist={opHist.Count}/snap={snapshots.Count}/untrk={untracked} {FirstDiff(fp, rp)}");
                        Write(report, $"seed={seed}");
                        Write(report, "  journal-tail: " + string.Join(" | ", journal.TakeLast(20)));
                        return;
                    }
                    applied++;
                }
            }
            else
            {
                var kind = rng.Next(10);
                var a = refs.Count == 0 ? 0 : rng.Next(refs.Count);
                var b = 0;
                if (a < refs.Count && refs[a].Words.Count > 0) b = rng.Next(refs[a].Words.Count);
                var extra = 0;
                if (kind == 7 && a < refs.Count && b < refs[a].Words.Count
                    && refs[a].Words[b].Text.Length >= 3)
                    extra = 1 + rng.Next(refs[a].Words[b].Text.Length - 2);
                var op = new Op(kind, a, b, "s" + rng.Next(100), extra);
                var depthBefore = await OnUiAsync(window, () => page.Test_UndoDepth());
                snapshots.Add(Clone(refs));
                var okOp = await ApplyOp(op, skipDoc: false);
                // Invalid ops AND no-step ops (equal-text sets, unrealized
                // boxes) must restore the reference: ApplyOp may already
                // have mutated it, and entering history would misalign undo.
                // NOTE: at MaxHistoryDepth the depth does NOT advance on a
                // real step (oldest is dropped) — that is a valid step, not
                // a no-op.
                var depthAfter = await OnUiAsync(window, () => page.Test_UndoDepth());
                if (!okOp || (depthAfter == depthBefore && depthBefore < maxHistory))
                {
                    refs = Clone(snapshots[^1]);
                    snapshots.RemoveAt(snapshots.Count - 1);
                    journal.Add($"u{i}:noop kind={kind} a={a} b={b} ok={okOp}");
                    continue;
                }
                if (depthAfter != depthBefore + 1 && depthBefore < maxHistory)
                    journal.Add($"u{i}:MULTISTEP kind={kind} a={a} b={b} {depthBefore}->{depthAfter}");
                if (depthBefore == maxHistory)
                {
                    // The doc dropped its oldest step; mirror the eviction
                    // (oldest-first: untracked section steps go first).
                    if (untracked > 0) untracked--;
                    else
                    {
                        opHist.RemoveAt(0);
                        snapshots.RemoveAt(0);
                    }
                    journal.Add($"u{i}:cap-drop untracked={untracked}");
                }
                journal.Add($"u{i}:do kind={kind} a={a} b={b} t='{op.Text}' x={op.Extra}");
                opHist.Add(op);
                redoOps.Clear();
                var fp = await DocPrint();
                var rp = RefPrint(refs);
                if (fp != rp)
                {
                    var dd = await OnUiAsync(window, () => page.Test_UndoDepth());
                    var recent = string.Join(";", opHist.TakeLast(5).Reverse().Select(o => $"{o.Kind}/{o.A}/{o.B}"));
                    check("stress-op", false, $"iter={i} kind={kind} a={a} b={b} depth={dd} hist={opHist.Count}/snap={snapshots.Count}/untrk={untracked} recent=[{recent}] {FirstDiff(fp, rp)}");
                    Write(report, $"seed={seed}");
                    Write(report, "  journal-tail: " + string.Join(" | ", journal.TakeLast(15)));
                    return;
                }
                applied++;
            }
        }
        Write(report, $"seed={seed}");
        check("stress-200", true, $"seed={seed} applied={applied}");
    }
}
#endif
