# Phase 1 progress — Lyric core (no UI)

How to pick this up: everything below is in `winui/AmllTtmlTool.Core`
(`Lyrics/` = new edit model) plus the untouched interchange engine
(`TtmlParser`, `TtmlGenerator`, `TtmlNormalize`, `LegacyTtml`, `LrcParser`,
`Models/`). Tests in `winui/AmllTtmlTool.Core.Tests` (`dotnet test`,
no app launch ever). Study notes: `docs/port/PORTING-NOTES.md` (read it
before touching behavior).

## Done (commits, one per brief step)

- `0daa210` step 0: study notes (model, formats, dialect, edit behavior).
- `0db9899` step 1: `Lyrics/` namespace, `CommunityToolkit.Mvvm` 8.4.2 on
  Core (offline from local NuGet cache), int-ms `LyricTime` helper.
- `efa662b` step 2: edit model + 20 command classes + undo/redo + split
  weights.
- `1fc987d` step 3: adapters, WASM parity goldens, 4 parser fixes.
- `9515d23` step 4: LRC parity, plain-text import, encoding detection,
  importer registry.
- (this step) step 5: this file.

Test counts: 192 pre-existing → **390 total** (+34 step 1, +64 step 2,
+69 step 3, +31 step 4). Parity goldens: **18 node-generated, 0
hand-derived** — 14 TTML fixtures + 2 real TTML files (`.wasm.json` /
`.wasm.xml` via `Tools/generate-ttml-goldens.mjs`), 1 synthetic + 1 real
LRC (`.lrc.json` via `Tools/generate-lrc-goldens.mjs`, which runs the
ORIGINAL `src/utils/parse-lrc.ts` in node with only imports rewired).

## Key decisions (locked, with why)

1. **Two models, strict roles.** `EditDocument/EditLine/EditWord`
   (mutable, int ms, `ObservableObject`) is the ONLY thing app/UI use.
   `Models` records are import/export interchange only. Adapters
   (`LyricAdapters`) are total both directions; `records→edit→records`
   equality holds on every fixture and adapters are invisible to serialized
   output (both tested).
2. **No undo coalescing.** The original has none (commit-on-Enter/blur, one
   atom-set = one step). `EditDocument.MaxHistoryDepth = 100`; one
   `Execute` = one step; import/new-file push a step each; history is never
   cleared; `IsDirty` == canUndo. Composite ops (split/merge/shift/paste/
   import) are single commands.
3. **Whole-ms asserted, never silently rounded.** Adapters throw
   `LyricAdapterException` (source + count + locations) beyond 1e-6 float
   noise. No format has produced one yet.
4. **Timestamps truncate toward zero** (WASM-probed: 1.9ms→1, 0.9ms→0) via
   integer/decimal arithmetic in `TryParseTimestamp`, so float noise can't
   straddle. Export rounding is half-away-from-zero everywhere (incl.
   `SplitWeights`, where .NET banker's rounding would diverge from JS).
5. **Duet = agent ≠ first-`<p>`'s agent** (5 probe rounds; majority,
   time-order and tie hypotheses all died on real files). Missing agent
   defaults to `"v1"`, explicit `""` stays distinct (both probed). Legacy
   repair restores non-v1 flags on top (its agent list is per-`<p>` with
   nulls — a flat regex desyncs after an agentless `<p>`).
6. **BG parens are transport, not content**: parser strips one layer per end
   (ASCII+fullwidth, BG words only), generator/repair re-add. BG-only
   groups emit bare spans and do NOT round-trip (WASM-faithful quirk —
   verified WASM→WASM loses it too).
7. **Wordless mains still get one `[pBegin,pEnd]` word** (empty/whitespace,
   x-bg-only, translation-only, text+BG all probed); timed words present →
   direct text is dropped except inter-span-space joining.
8. **Per-word romanization** attaches from `<transliteration><text for>`
   on exact span match (first wins, consumed); x-roman siblings are always
   line-level (probed).
9. **LRC = local rules**: no offset/metadata support, no word timing,
   decimal fractions, +10s tail, trans/roman/extra split. `[0-9]`, FEFF-aware
   trim (two one-line fixes from parity).
10. **Plain text = dialog rules verbatim** (`\n`-only split, `\r` kept,
    blanks kept, overrun→`""`), except: empty prefixes skipped (original
    would hang), UTF layer exceeds original (BOM sniff 8/16/32 LE+BE, NUL
    heuristic, UTF-8 fallback — original has zero detection).
11. **Lossless scope = records scope.** Custom agent ids, `songPart`,
    `blockIndex` collapse exactly like the WASM App-model does (verified);
    `Unmapped` bags on document/line/word are tested extension points,
    currently empty by design (Phase 4 formats will need them).
12. `.txt` maps to the text importer (documented addition — original only
    takes text via dialog).

## Core API summary (`AmllTtmlTool.Core.Lyrics`)

- `LyricTime`: `Parse(string)→int`, `Format(int, bool withMs)` — mm:ss.mmm.
- `EditDocument`: `Lines`, `Metadata`, `Unmapped`, `Execute/Undo/Redo`,
  `CanUndo/CanRedo/IsDirty/UndoDepth`, `RequireLine`, `MaxHistoryDepth`.
- `EditLine`: id, `Words`, texts, `IsBg/IsDuet`, times, `IgnoreSync`,
  `EndTimeLink`, `Unmapped`; `Clone/CloneWithNewIds/NormalizeTime`.
- `EditWord`: id, times, `Text/RomanWord`, `Obscene/EmptyBeat/RomanWarning`,
  `Ruby`, `Unmapped`; `Clone/CloneWithNewId`.
- `EditRubySegment`, `EditMetadata`, `EditEndTimeLink` (record).
- `ILyricCommand` + `LyricCommand` base; `Commands/`: `LineCommands`
  (Add/Remove/Move/Combine/Duplicate), `WordCommands`
  (Add/Remove/Merge/Split/MoveToNew/MoveFollowing), `FieldCommands`
  (9 setters + Link/Unlink + `CompositeCommand`), `BulkCommands`
  (BulkReplace/TimeShift/ReplaceWordText).
- `SplitWeights`: `GetCharType/CalculateWeight/DistributeTime` (+0.2 default).
- `LyricAdapters`: `FromRecords(records, sourceName)`,
  `ToRecords(doc)`; `LyricAdapterException`.
- `Import/`: `ILyricImporter`, `TtmlImporter`, `LrcImporter`,
  `PlainTextImporter`, `LyricImporterRegistry` (`.Default()`,
  `ByExtension/Sniff/ImportFile/Register`), `TextImporter` +
  `TextImportOptions`, `LyricEncoding.Decode`, `LyricImportException`,
  `LyricImportResult(Document, Notices)`.

## Known gaps (not in Phase 1 scope)

- Phase 4 formats: YRC/QRC/LYS/ESLRC (register via `Register`; ESLRC word
  timing + `59999999` sentinel and YRC tuples already probed, notes §b).
- Unported edit tools: auto-segmentation, ruby editor, syllable smoothing,
  merge-syllables dialog, romanization distributor/predictor, LRCLIB import,
  drag-reorder, outline panel.
- App-shell concerns (Phase 2+): project match/autosave, author-metadata
  defaults, filename suggestion, audio-metadata merge, dirty-confirm flows,
  file dialogs, TTML error dialog (rustc-style log exists as
  `ttml-processor/message.ts`), clipboard TTML in/out, save actions.
- Selection model (`selectedLines/WordsAtom`) is UI state — deliberately
  not in Core; commands take ids so the UI can batch multi-selections into
  `CompositeCommand` (one step).

## Deliberate divergences from the original

- Open-tags-only truncation returns an empty doc (locked by a pre-existing
  test); WASM errors `UnexpectedEof`. Importer must treat empty results
  accordingly (see Phase 2).
- Transliteration span matching consumes first-match (WASM duplicate-span
  behavior unprobed); BG paren strip is per-word (multi-word split-paren
  forms unprobed); `<p>` inside XML comments would fool the repair's tag
  scan (malformed-adjacent edge, unprobed).
- Ruby-base text inside BG wrappers is not paren-stripped (unprobed either
  way); ruby `emptyBeat` is model-only (no record/WASM counterpart).
- Interleaved-import `undefined` subs normalize to `""` (output-equal).

## What Phase 2 needs

1. Bind `EditDocument` to the editor; route EVERY mutation through
   `Execute` (field commits on Enter/blur, never per-keystroke).
2. `Ctrl+Z` while a TextBox has focus is the TextBox's own undo; after
   commit it walks document history (original suppresses global keys while
   typing — `utils/keybindings.ts`).
3. Multi-select edits → one `CompositeCommand`. Delete behavior differs for
   empty vs non-empty word selection (notes §d).
4. Dialog scope selector (all/selected/selected-following[from first]/custom
   1-based) for TimeShift-class tools; shift clamps at ≥ 0.
5. Import flows must dirty-confirm first (except audio-metadata merge),
   push one `BulkReplaceCommand`, surface `Notices` (repair fixes) and the
   legacy toast; failures leave the doc untouched (`LyricImportException`).
6. Link/unlink end-time is sync-gated in the original but the chaining ops
   are in Core already.
7. Keep Unicode byte-identical (no normalization); times display via
   `LyricTime.Format`; red-start>end is display-only in the original.
8. Shortcut map: PORTING-NOTES §d table (Ctrl+Z/Y, Ctrl+A, Del, Ctrl+N/O/S/M,
   Shift+1/2/3; sync keys + spectrogram Q/S/W are later phases).

## Couldn't verify (acceptance)

- WASM duplicate-span transliteration attach; multi-word split-paren strip;
  `<p>`-in-comment repair scan; negative-timestamp handling (no samples).
- Whether `translationLanguage/romanizationLanguage` options select among
  multiple blocks (no multi-block samples; options plumbed but untested).
- `xml:lang` on export with zero language metadata (engines omit; app path
  always backfills first).
- Real-file coverage is machine-local (`D:\Lyrics`): apple (84 lines),
  broken legacy (88 lines, `itunes:key` reason), standard LRC (22 lines).
  Elsewhere those 4 tests vacuous-pass; the 18 committed goldens still pin
  behavior everywhere.

---

# Phase 2 progress — Edit tab (import + render + toggles + tests)

Commits (one per brief step): `de60011` M1 (Core `LyricStore` facade over
`EditDocument`), `b0f727e` M2 (consumers onto Core store, old store deleted),
`39b0b6d` step 1 (import funnel, empty state, `--load-lyrics`),
`ea4376c` step 2 (line list, custom `LyricWrapPanel`, `SelectionService`),
`338ef8a` step 3 (persisted `LyricViewStateService`, pills, animations,
anchor), `12ddf7c` step 4 (ribbon mirrors selection, read-only mm:ss.mmm),
`1cf380e` step 5 (this step: `ui-lyrics.ps1` driver + harness/test fixes).

Results (all green, same binaries): Core **406/406**, selftest PASS,
`ui-tests` **13/13**, `ui-tests-playerbar` **24/24**, layout-test PASS
(~300 s), `ui-lyrics.ps1` **30/30** (9 self-driving incl. real spaced-name
files, 21 hold/UIA asserts). Test data stays read-only (`D:\Lyrics`,
`D:\Audio`); runners stage temp copies only.

## Step-5 findings (locked)

1. **Spaced argv splits.** `Start-Process -ArgumentList` arrays join with
   bare spaces and `powershell -File` re-parses, so `a b.ttml` arrives as
   two argv (observed: `report=Poi!.ttml`, stray `name.ttml`/`Subete.lrc` in
   repo root, 600 s silent timeouts). File paths now travel via
   `AMLL_HARNESS_FILE` env (exact); `-Args` is fallback, both ends quote.
2. **Dual-tree UIA reads.** `inspect --interactive` drops containers
   (`LineList`), TextBlocks (`FileNameText`, empty state, dialog titles);
   full dumps omit data-bound chip names. Driver uses full tree for
   structure + interactive tree for `Word N:`/`Romanization of word N`.
3. **Per-line row IDs.** Card `TransBox`/`RomanBox` have no stable tree
   presence (visible on screen, missing from dumps), so
   `OnLineContainerChanging` now sets `TransBoxN`/`RomanBoxN` (+ explicit
   Names); combo asserts use direct lookup.
4. **Pill Checked mirror.** UIA TogglePattern flips raise Checked/Unchecked
   but not Click; `OnPillCheckChanged` now also writes the view-state
   service (idempotent, loop-free), so rows follow every flip path.
5. **Anchor needs snapshot-first + scroll slack.** Measuring after mutation
   sees a steady state (dy=32); `ApplyToggleStates` snapshots before
   mutating and `HideAfterAsync` compensates the deferred collapse. At
   scroll offset 0 a hide-above cannot be compensated (geometry, not a
   bug), so the anchor test uses a 40-line doc with line 20 selected.
6. **Budgets tolerate load.** Toggle budget is median-of-3 per toggle
   (single congested sample flaked at 137 ms); driver budgets are 120/180/
   300 s per fixture size with a 15 s heartbeat line (waits are never
   blank); app `HarnessHost` watchdog follows the same timeout.
7. **Dialog content is UIA-invisible.** Only Title + buttons surface, so the
   malformed test asserts title + screenshot (`error-dialog.png`) +
   dismiss flow; message/Details stay covered by self-driving error-cases
   (typed exceptions logged). Dialog nodes got explicit
   `AutomationProperties` names regardless (accessibility).
8. **No-foreground driving.** Mouse click / send-keys need foreground
   (flaky while the machine is in use); driver selects via
   SelectionItemPattern slugs and invokes via TogglePattern. Real arrow-key
   routing is framework behavior, covered manually (below).

## Manual checks remaining

- Arrow keys move selection end-to-end on a loaded doc (driver covers the
  selection→ribbon pipeline via pattern-select).
- Narrator spot-check: line cards, pills, error dialog.
- `error-dialog.png` + `combo-all-on.png` eyeball in
  `%TEMP%\lyrics-ui-artifacts\`.

---

# Phase 3 progress — Editing in the Edit tab

Model rule (from Phase 1, holds): `EditDocument` is the only model.
EVERY mutation goes through its commands. One committed edit = one
history step. Composite ops (split/merge/shift/paste of many lines) are
ONE step (`CompositeCommand` / single command class).

## Step 0: operation map (original → port)

Sources: PORTING-NOTES §d + `keyboard/commands.ts`, `states/main.ts`,
`TopMenu/useTopMenuActions.ts`, `Menus/lyric-line-menu.tsx`,
`lyric-editor/components/lyric-word-menu.tsx`, `lyric-word-view.tsx`,
`lyric-line-view.tsx`, `RibbonBar/edit-mode.tsx`, `utils/timestamp.ts`.

| # | Original op | EditDocument command (one step) | UI surface | Accelerator |
|---|---|---|---|---|
| 1 | Edit word text (Enter/blur commit, no-op if unchanged; `word\|` seeds `ruby=[]`) | `SetWordTextCommand` | chip text box | Enter commit, Esc revert, Tab/Shift+Tab commit+move |
| 2 | Edit per-word romanization | `SetWordRomanCommand` | chip roman box + ribbon romanWord | same commit keys |
| 3 | Edit line translation / romanization | `SetTranslationCommand` / `SetRomanizationCommand` | card Trans/Roman boxes + ribbon fields | same commit keys |
| 4 | Edit line start/end; word start/end; end-as-duration mode (δ or absolute, clamp end≥start, push abutting next start) | `SetLineTimesCommand` / `SetWordTimesCommand` | ribbon time boxes + duration toggle | Enter/blur commit; invalid → red + revert |
| 5 | Toggle BG / duet / ignoreSync; obscene; emptyBeat int (`parseInt→0`) | `SetLineFlagsCommand` / `SetWordFlagsCommand` | ribbon toggles + new emptyBeat field + line context checkboxes | immediate, one step each |
| 6 | Add word (trailing `+`, insert-mode before, `Insert word here…` box; `newLyricWord()` 0/0/`""`) | `AddWordCommand` | chip-row `+`, inline insert box | Enter commits + keep focus, Esc cancels |
| 7 | Delete words (filter + `normalizeLineTime` iff changed) | `RemoveWordsCommand` | word context menu, Edit menu, Del | `Delete` |
| 8 | Split word at char indices (weight distribution) | `SplitWordCommand` (+ `SplitWeights`) | word context menu → small dialog | menu text only |
| 9 | Merge words (>1 in one line, join `""`, fresh id) | `MergeWordsCommand` | word context menu | menu text only |
| 10 | Replace word text (exact / ignore-case) | `ReplaceWordTextCommand` | word context menu → small dialog | menu text only |
| 11 | Move selected words / following words to new line (inherit BG/duet, normalize both; whitespace-only untimed clicked word deleted first) | `MoveWordsToNewLineCommand` / `MoveFollowingWordsToNewLineCommand` | word context menu | menu text only |
| 12 | Add line (append / above / below / ribbon New; `newLyricLine()`) | `AddLineCommand` | ribbon `+ Line`, line context menu | menu only |
| 13 | Delete line(s) (no selection → this line; else selected; clears selection; NO confirm, undo restores) | `RemoveLinesCommand` | card `X`, line context menu, Del | `Delete` |
| 14 | Combine lines (≥2 contiguous; words→lowest index, end=last end, absorbed translations discarded) | `CombineLinesCommand` | line context menu | menu text only |
| 15 | Duplicate lines (fresh ids after each) | `DuplicateLinesCommand` | line context menu | `Ctrl+D` (list scope) |
| 16 | Move line up/down | `MoveLineCommand` | line context menu | `Alt+Up/Down` |
| 17 | Select all / unselect / invert / word-matches; delete selection (words nonempty → strip words globally, else splice lines) | selection + `CompositeCommand` batch | Edit menu | `Ctrl+A`, `Esc` (list scope), `Ctrl+I`/`Ctrl+F2` stubs |
| 18 | Undo / redo | `Document.Undo/Redo` via `LyricStore` | title-bar buttons, Edit menu | `Ctrl+Z` / `Ctrl+Y` + `Ctrl+Shift+Z` via `KeyboardAccelerators` |
| 19 | TimeShift ±ms (clamp ≥0; scope all/selected/following/custom) | `TimeShiftCommand` | Edit menu `Shift time…` dialog | menu only |
| 20 | Sync line timestamps (bulk, confirm); link/unlink end-time | core chaining in `SetLineTimesCommand` / `Link/UnlinkEndTimeCommand` | Tools menu; link UI stays Sync-phase | menu only |
| — | SKIP (Sync/Preview-only): Space/arrows/`[]'` audio, D/A/S/W R/Y F/G/H Home/End stamping, Q/S/W spectrogram, preview/render, drag-reorder, auto-segment/ruby/smoothing dialogs, LRCLIB, outline panel | — | later phases | Space NEVER bound globally (Sync stamping) |
| — | SKIP (no line/word clipboard): TTML clipboard in/out replaces whole doc, one step | `BulkReplaceCommand` via import funnel | File menu only | existing `Ctrl+V` path |

Decisions locked for Phase 3 (Q&A): ribbon gains `romanWord` +
`emptyBeat` + end-as-duration toggle (full parity); `SelectionService`
extends to multi-select; split/replace go through small dialogs (stubs
in test mode). Empty word text commits as `""` (original `onEnter`
sets it verbatim; blank style covers display) — no auto-delete.

## Steps 1–6: what was built (commits `f0f3a99`…`ac3a0c3`)

- Step 1 (chip editing): chip text/roman + card Trans/Roman boxes are
  real editable TextBoxes (code-managed text, not `x:Bind`, so model
  notify never steals caret/focus — caret restored clamped). Commit on
  Enter/blur as ONE step via commands, no-op when unchanged, Esc
  reverts to model, Tab commits on the way out. IME composition guard
  (`TextCompositionStarted/Ended`; composing Enter never commits).
  Chips hug width (grid = max child, both stretch — instant, no
  animation). `word|` suffix strips (C# `Ruby.Count==0` ≡ absent, so no
  Core change needed). Raw text in boxes (alias never committed);
  `Space ×N` survives as automation name + placeholder + dim cue.
  Spellcheck/prediction off on all edit boxes. Focus selects line+word;
  keyboard focus scrolls into view; uncommitted edits commit before any
  scroll/recycle removes the box.
- Step 2 (structure): multi-select `SelectionService` (sets + primary;
  list `Extended` mode synced both ways; primary anchors on re-select).
  `+` opens inline `Insert word here…` (Enter commits + keeps focus for
  repeat adds, Esc cancels). `X` deletes (one step, no confirm,
  neighbor selected). Ribbon `+ Line` inserts after selection + focuses
  the insert box. `MenuFlyout` context menus on chips (word ops) and
  cards (line ops: before/after, duplicate `Ctrl+D`, combine, move
  `Alt+Up/Down`, BG/duet checks, delete `Delete`) with accelerator text;
  one flyout instance per element. Word panels rebuild on
  `Words.CollectionChanged` with focus restore. List-scoped `KeyDown`
  dispatch (`Delete`, `Ctrl+D`, `Alt+Up/Down`, `Ctrl+A` scope toggle,
  `Esc` clear) — NOT global accelerators, so TextBox keys never leak;
  `Ctrl+I`/`Ctrl+F2` stay stubs exactly like the original.
  Edit menu: select-all/deselect/delete-selection wired.
- Step 3 (ribbon): all Edit-tab fields editable — line/word start/end,
  content, new `romanWord` + `emptyBeat` fields, Trans/Roman; toggles
  apply immediately (one step each). Times parse `mm:ss.mmm`
  (`ss.mmm`/plain ints accepted, same regex as the original);
  invalid → system-error border + revert; start>end red display-only
  but still commits (original). Duration mode (shared, like
  `showEndTimeAsDurationAtom`): delta/absolute, ignore-NaN,
  ignore-non-positive-absolute, clamp end≥start, delta pushes abutting
  next word start. Multi-select differing → `Multiple values...`
  placeholder. Disabled with no selection. BG/duet tinted-border badges
  + automation suffixes on cards.
- Step 4 (undo/redo/shortcuts): `Ctrl+Z`/`Ctrl+Y` + `Ctrl+Shift+Z`
  `KeyboardAccelerators`; menu items show shortcut text + follow
  `CanUndo/CanRedo` (both menu copies); title-bar buttons keep
  `Undo (Ctrl+Z)`/`Redo (Ctrl+Y)` tooltips. Focus semantics: dirty
  (uncommitted, incl. IME-composing; untouched multi-placeholders
  excluded) TextBox owns the keystroke, else document undo/redo runs
  handled. Post-undo/redo selects + scrolls to the change (primary line
  if alive, else same index). Dirty `●` flag unchanged (existing chrome).
- Step 5 (animation): `AddDeleteThemeTransition` + `RepositionTheme`
  on the list; FLIP glide + 150 ms fade/rise entrance for word chips
  (same decelerate as Phase-2 toggles; `Storyboard`s stay in fields —
  only composition animations are created per run). No animation while
  typing; realized lines only; animations-off snaps; focused chip
  excluded; undo/redo reuse the same paths.
- Step 6 (tests): `--edit-test` self-driving harness (47 checks) +
  `ui-edit.ps1` UIA (17 tests, real `send-input` keys) +
  `EditCommandFixtureTests` (7: every command apply/undo/redo on real
  TTML+LRC fixtures).

## Step 6 results (measured numbers, not words)

Final binary `AmllTtmlTool.WinUI.dll` 2026-10-13 10:42:47Z, launched via
`scripts/run-harness.ps1 -Mode edit-test` (Start-Process/WaitForExit
pattern) and `pwsh ui-edit.ps1` / `pwsh ui-lyrics.ps1`.

- Core **413/413** (406 + 7 new). `edit-test` **47/47 + VERDICT PASS**.
- UIA `ui-edit` **17/17** (type+Enter/blur/Esc/undo-button/width-hug/
  menu/insert/delete/BG-toggle/video/6 screenshots).
- Regression: `ui-lyrics` **30/30**, `ui-tests` **13/13**,
  `ui-tests-playerbar` **24/24** — no regressions.
- Stress: 200 seeded ops (seed `20261013`, printed) vs reference model,
  161 applied (rest undo/redo/no-op/invalid), fingerprint-compared after
  every step — green. Reference mirrors texts/counts/flags/times by
  index (split times via unit-tested `SplitWeights`); undo pops
  snapshots, redo re-applies; history-cap (100) oldest-drops mirrored.
- Typing latency (300×10 doc, focused chip, 100 keys drop-first-10):
  **p50=1.48ms, p95=2.06ms, max=1959ms** (single system stall; p95
  budget <16ms MET). Breakdown last key: set=1.46ms, layout=0.12ms,
  thread-CPU=0.00ms (15.6ms quantum — sub-tick). Series across runs:
  2.5–3.3ms quiet, 11–68ms loaded (2 GHz clamp observed,
  `MaxClockSpeed=2000`, 50+ background processes incl. global hooks,
  `textinputframework.dll` in-process). Cost drivers fixed along the
  way: panel-level (not list-wide) layout, spellcheck/prediction off,
  minimal sets per key; a caret move pumping bring-into-view storms was
  found by the breakdown and removed from the probe.
- Structural commits (median-of-5, 300-line doc, Debug): insert-line
  ~250–460ms (incl. scroll+open+focus), add-word ~80–310ms (10-word
  line), delete-line ~13–43ms (empty line).
- Virtualization: 5–7 realized lines / ~110–135 boxes top and bottom of
  the 300-line doc during editing+scrolling.
- Frames: in-app `CompositionTarget.Rendering` ticks **0 headless even
  foregrounded** (occluded compositor) — instead, UIA-captured video of
  delete+undo (foreground, 30 fps): **210/210 frames on time,
  median=p95=max=33.33ms, 0 over 33.4ms**. `structural.mp4` kept in
  `%TEMP%\edit-ui-artifacts\`.
- Edge (no crash): empty doc, one-word line, 5000-char word commit +
  undo, emoji + combining chars + undo — all green.
- Screenshots (Light + Dark, eyeballed): focused chip + selected line,
  BG line (+toggled), duet line (scrolled), context menu (mouse AND
  Shift+F10 paths), insert box. In `%TEMP%\edit-ui-artifacts\`.

## Bugs found by verification (all fixed, with regression cover)

- **B1 stale-box commit (data loss + history desync).** Pooled
  containers keep live sessions; model changes while pooled
  (undo/redo/ops); rebind then committed stale text (+1 stray step,
  desyncing all later undos — caught by the stress journal at u129).
  Fix: `BoxSession.LastSeenModel` — commit only genuine local
  divergence (text ≠ model AND ≠ snapshot); recycle-time commit
  preserved (typing-on-scroll-away safe). Container-`Content` currency
  was tried first and rejected (lags the model; skipped a legit
  5000-char commit).
- **B2 history-cap (100) divergence.** Depth stuck at cap misread as
  no-op while the doc kept changing (caught by journal MULTISTEP at
  u155+). Harness mirrors oldest-drops now; app behavior already
  matches the original (drop oldest, depth 100).
- **B3 TextBox native menu swallowed chip right-clicks.** Chip
  `ContextFlyout` assigned to the chip AND both boxes (one instance
  each); verified by screenshot.
- **B4 emptyBeat alias regression** (Phase-2 `Word 2: Space ×2` name
  assert): alias restored in automation name + placeholder; raw text
  still commits (no model corruption).
- **B5 SelectLineAt left a stale ribbon primary** (same-item reselect
  fires no event): anchors when diverged, preserves surviving word
  selection otherwise.
- **B6 commit for a deleted line crashed the UI thread** (Unloaded
  race): stale-target commits are no-ops (`InvalidOperationException`
  swallowed).

## Shortcut table (as implemented)

| Shortcut | Scope | Action |
|---|---|---|
| Enter | chip/ribbon/insert box (not IME-composing) | commit (one history step) |
| Esc | chip/ribbon/insert box | revert / cancel insert |
| Tab / Shift+Tab | edit boxes | commit on the way out, move in reading order |
| Delete | line list (not in TextBox) | delete selection (words else lines) |
| Ctrl+D | line list | duplicate selected lines |
| Alt+Up / Alt+Down | line list | move primary line |
| Ctrl+A | line list | all words of selected lines else all lines |
| Esc | line list | clear selection |
| Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z | global accelerators | TextBox-own when dirty, else document undo/redo (handled) |
| (Space never bound) | — | reserved for Sync word stamping |

## Known gaps (not in Phase 3 scope)

- Ruby editor UI (model carries ruby; `word|` seeds empty list only).
- TimeShift dialog, metadata editor, replace-all-dialog UI (all commands
  in Core and unit-tested).
- Drag reorder (original has line/word drag; port defers to later).
- Sync-mode end-time-link BUTTON (data ops in Core).
- `+0`-style no-change duration deltas still push a history step
  (original's immer-set path behaves the same; harmless).
- Delete-key word path normalizes line times via Core (matches the
  word-menu path; the original TopMenu path skips normalize — cosmetic).

## What Phase 4 needs

- Import formats YRC/QRC/LYS/ESLRC (registry `Register`; probes in
  notes §b/f); save/export flows; close-with-unsaved-changes confirm
  (dirty flag + history exist; dialog flow missing).
- The items above (ruby UI, bulk dialogs, drag, link button, LRCLIB,
  segmentation/smoothing/romanization tooling, outline panel).

## Couldn't verify (manual steps for the user)

1. **Real IME** (harness can't drive one; guard logic tested
   simulated): focus a chip → type Japanese → Space converts (candidate
   window) → Enter confirms (no commit, no newline, text stays
   converting) → next Enter commits one step; Esc mid-composition
   cancels IME first. Repeat for ribbon content + insert box.
2. **Keyboard**: arrows move selection end-to-end; Tab order chips →
   Trans/Roman → ribbon; Shift+F10 opens chip menu (screenshot proves
   it opens; keyboard-select an item with arrows+Enter: Split dialog).
3. **Ctrl+Z/Y/Shift+Z**: uncommitted typing → TextBox undo (character
   restores); committed → document undo + selection jumps to the
   change; tooltips show accelerators.
4. **Split/replace dialogs**: split `hello` at `2` → two timed words
   (weights); invalid positions ignored; replace exact-match single
   word; dialogs never appear in test mode (stubs).
5. **Duration mode**: ribbon End→Duration toggle; `+500` extends,
   `1000` sets absolute, `abc` ignored; abutting next-word push.
6. **Narrator**: line cards, chips, badges, menus, dialogs, insert box.
7. **High Contrast**: badges/toggles/cards (Light/Dark eyeballed via
   screenshots; HC needs the OS-level pass).
8. **Smoothness eyeball**: `%TEMP%\edit-ui-artifacts\structural.mp4`
   + live delete/undo/typing on the 300×10 doc.
9. **Dirty flow**: `●` appears next to file name on first edit
   (exists); save/close-confirm arrives in Phase 4.
