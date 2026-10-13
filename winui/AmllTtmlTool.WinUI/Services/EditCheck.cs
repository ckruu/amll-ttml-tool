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
            await OnUiAsync(window, () => page.Test_AddLineAfterSelected());
            var nAdd = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var addFocus = await OnUiAsync(window, () => page.Test_InsertBoxOpen());
            Check("addline-focus", nAdd == n0 + 1 && addFocus, $"lines={nAdd}");
            await OnUiAsync(window, () => page.Test_EscapeInsertBox());
            // Remove the added line to restore the baseline for later sections.
            await OnUiAsync(window, () => page.Test_DeleteLineAt(nAdd - 1));
            var nRestored = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("addline-cleanup", nRestored == n0, $"lines={nRestored}");

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

            Write(report, "  note stress + perf + edge use fresh docs (see below)");

            // --- 6. stress: 200 seeded ops vs reference --------------------
            await RunStressAsync(window, page, report, Check);

            // --- 7. perf: 300x10 doc ---------------------------------------
            await OnUiAsync(window, () => page.Test_BuildPerfDoc());
            var perfLines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("perf-doc", perfLines == 300, $"lines={perfLines}");
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(150));
            await Task.Delay(400);
            var lat = new List<double>();
            for (var i = 0; i < 100; i++)
            {
                var ms = await OnUiAsync(window, () =>
                    page.Test_TypeChipChar(150, 5, false, (char)('a' + (i % 26))));
                if (ms >= 0) lat.Add(ms);
            }
            lat.Sort();
            // Drop the first 10 (template/JIT/layout warmup) before stats.
            var warm = lat.Count > 10 ? lat.Skip(10).ToList() : lat;
            lat = warm;
            var breakdown = await OnUiAsync(window, () => page.Test_TypeBreakdown());
            Write(report, $"  note type-breakdown setWall/layout/setCpu ms of last key: [{breakdown}]");
            var p50 = lat.Count > 0 ? lat[lat.Count / 2] : -1;
            var p95 = lat.Count > 0 ? lat[(int)(lat.Count * 0.95)] : -1;
            var max = lat.Count > 0 ? lat[^1] : -1;
            Write(report, $"  note typing-latency n={lat.Count} p50={p50:F2} p95={p95:F2} max={max:F2}");
            Check("typing-budget", p95 >= 0 && p95 < 16, $"p95={p95:F2}ms");
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
