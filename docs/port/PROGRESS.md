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
- The items above (ruby UI, bulk dialogs, link button, LRCLIB,
  segmentation/smoothing/romanization tooling, outline panel).
  Drag/reorder is DONE (below); TimeShift *dialog* is still missing
  (command + scope rules in Core, ribbon time fields edit multi).

---

# Phase 3 finish — chips, cards, ribbon, selection, drag (items 1–12)

Commits (one per brief step, `winui-port` branch): `286cb49` notes
(select/edit, space chips, ribbon multi-value, menus, drag — PORTING-NOTES
§h), `7aecc4a` step 1 (space chips, hugging, shared romaji gap, `+`
centering, invariant measure hooks), `e928b6b` step 2 (overlay accent bar
from the selection set, fixed number column, badges right in reserve,
geometry checks), step 3 (2-row Word section, Roman collapse, short
multi placeholder + tooltips, `N selected` caption, rebalanced tiers),
step 4 (focus-location select/edit states, shift gestures, accent
selection visuals, context select-first with counts, multi ribbon ops,
block move, selection convergence), step 5 (pointer drag/drop with ghost,
indicators, auto-scroll, one-step commands, seeded stress), step 6
(this step: full regression + this file).

Build rule (locked from experience): build the **project**
(`dotnet build winui/AmllTtmlTool.WinUI/AmllTtmlTool.WinUI.csproj
--no-incremental`), NOT the solution — the solution maps Any CPU → x86
(port policy: never x86) and the harness resolves the exe/DLL under
`bin/Debug/net8.0-windows10.0.19041.0/`. Check the DLL timestamp before
every run; kill stray `AmllTtmlTool.WinUI` processes (a leftover once
locked the DLL and failed a build). `dotnet test` for Core.

## Locked decisions

1. **SELECT/EDIT are focus locations, not a property.** Boxes stay
   always-editable (all Phase-3 commit/IME/caret/undo paths byte-identical,
   zero UIA churn): item focused = SELECT (click selects, no caret),
   box focused = EDIT (double-click/F2/Enter). Enter/Esc commit/revert +
   focus the item (SELECT); Tab/blur commit (original keeps editing across
   Tab in its roman row, so Tab preserves editability — documented
   reading of "blur returns to select", which holds for pointer blur).
   Clicking the box being edited keeps the caret (no select steal).
2. **Presses split down/up like the original** (`useLyricListDrag`):
   down arms (+ instant single-select only for a plain press on an
   unselected item); up runs the gesture (single/ctrl/shift) with
   press-time modifiers; past 4px becomes a drag (multi kept when the
   start was selected, single-selected-first otherwise). Right/middle
   mouse never selects on press (the context menu owns it, incl.
   select-first — this also fixed right-click destroying multi).
3. **One selection source, converged.** `SelectionService` sets +
   primary; the ListView mirrors. Every service writer pairs a ListView
   write (no phantom lines — a stale set once made a multi BG toggle
   diverge from the stress reference). `PinLine` (deterministic single,
   keeps the line's words per B5),    `SetLines`/`AddLines` (exact mirror of ListView diffs for hooks),
   `AnchorLine/AnchorWord`
   (focus moves the primary only — multi survives tabbing, like the
   original where selection is click-driven), `SelectWordRange`
   (shift within one line), `ReplaceWords` (drop-copy reselect).
   GotFocus anchors (Tab) — proven via handler-drive + counter; headless
   `Focus()` moves XAML focus state without routing the event (`got=0`
   measured), so event delivery itself is a manual active-window step.
4. **Multi-ops ported** (each exactly one history step; single-target
   paths byte-identical): word/line delete, duplicate lines, block
   move up/down (new: contiguous or not, safe-order CompositeCommand —
   the original has no Alt+Up/Down; its move is drag, now ported too),
   combine lines, BG/duet/ignoreSync toggles (ribbon + line menu, scope
   = selection), obscene, emptyBeat, word content/roman/start/end
   (absolute + duration-delta with abutting push per word), line
   start/end/trans/roman, word split/merge/replace/move-to-new/following.
   NOT ported: TimeShift dialog UI, Ctrl+F2 word-match (original binds
   Ctrl+F2, not F2 — F2 is the port's edit key), line/word clipboard
   (TTML-text only, as before).
5. **Drag design** (pointer-based, no OS DnD): 4px threshold; payload =
   live selection at drop (as the original reads its atoms in onDrop);
   Ctrl at drop = copy (ghost shows it live); full-width accent
   insertion line (lines) / 3px edge mark on the chip (words, no layout
   shift); ghost pill `Move to N · count unit(s)` in app accent;
   dragged items translucent; auto-scroll 40px edge / 12px tick;
   same-place/own-selection drops are no-ops (simulated id comparison,
   no step); Esc cancels (no step); CaptureLost cancels (safe over
   drop-on-deactivate). `ReorderLinesCommand` (exact drag-reorder.ts
   port incl. adjusted index) + `MoveWordsCommand` (doc-order payload,
   touched-bounds normalize only — timings as the original, per notes
   §h). Copies mint clone ids ONCE and reuse them on redo (re-running
   the producer broke exactness; same latent pattern noted in
   MoveWordsToNewLineCommand, left untouched). The window drop-to-open
   overlay is StorageItems-gated (ShellPage) so internal drags can never
   trigger it (verified by inspection).
6. **Ribbon Word section**: two rows — (Start, Content, Beats) /
   (End/Duration, Obscene, Roman). Beats+Roman share columns so hiding
   Roman leaves no gap (Beats stays). Multi shows short `Multiple…`
   (the full text clipped in 80–100px boxes) + `Multiple values` tooltip;
   caption `N selected` (own zeroed column; VSM-hidden at Tight).
   Tier thresholds are MEASURED (bars 1358/1074/858/708/630 at
   1400/1116/900/750/672): 1400/1070/895/700/0; Medium also overflows
   View, Narrow also overflows Manage + shrinks inputs, Tiny overflows
   Word. Secondary-on at ≥1400 can still overflow (pre-existing
   tightness, worse now — open item below).

## Step 6 results (measured numbers, not words)

Final binary `AmllTtmlTool.WinUI.dll` (timestamp checked pre-run),
`scripts/run-harness.ps1 -Mode edit-test` on a staged fixture copy.

- Core **420/421** (413 + 7 fixture + 8 new drag tests; the 1 failure is
  the pre-existing `Fractional_Ms_Reported_With_Source_And_Count`,
  verified failing on the clean tree too).
- `edit-test` **113/113 VERDICT PASS** (final binary, elapsed 553.8s;
  47 old + 8 step-1 + 6 step-2 + 8 step-3 + ~20 step-4 + ~20 step-5
  incl. seeded stresses).
- `lyrics-test` apple PASS (36.8s, budget worst 128ms), broken PASS
  (43.5s, worst 207ms), subete PASS (17.1s, worst 18ms) — budget
  250ms per C11; error cases, combos matrix, chip widths, frame
  sampling, virtualization walk green.
- `selftest` PASS (4.2s).
- Typing latency (300×10, 3×100 keys, median-of-3 rounds — single samples
  flaked to 30–36ms p95 on the loaded 4c/8GB VM): **median-p95 ≈3.6–4.2ms**
  (rounds e.g. [2.80,4.20,20.19]).
- Main stress: 200 ops seed `20261013`, fingerprint-compared per op —
  green (applied ~155–167 depending on run).
- Drag stress: 60 ops seed `20261015`, order-predicted + exact undo/redo
  every op — green (applied 54).
- Auto-scroll: 2,000-line doc, 40 ticks down 480px + 40 up 480px
  (12px/tick, wall ~800–1100ms/40 ticks); drop-after-scroll works; one
  undo step restores exactly. Headless compositor yields no frame ticks,
  so smoothness itself is manual (below).
- Screenshots (all viewed): space chips (`Space x1/x3` dim labels +
  roman row beneath), hug (`キ` 25px unit, equal box widths), badges
  right (BG+V2 side by side, centered), selected card (accent-tinted
  fill + outlined washed chips), drop line (full-width accent insertion
  line + `Move to 21 · 2 lines` ghost), drop chip (edge mark between
  chips). In `%TEMP%\edit-ui-artifacts\` (`step*-after-*.png`).
- Card geometry: first-chip x 56.00..56.00 (plain/BG/V2/both/selected);
  heights 156.00..156.00; gaps 8.00..8.00; pads 0.00/0.00 on+off;
  `+` centered ≤0.50px; romaji gaps 6.00 == 6.00; selection indicator
  exact over 30 seeded rounds + scrolls.

## Bugs found by verification (all fixed, with regression cover)

- **C1 locale formatting.** Measure hooks used `:F2` (current culture →
  `25,00`), failing every invariant-culture parse on this machine
  (including pre-existing `width-hug` + LyricsCheck widths). Fix: one
  `F2()` invariant helper, all hooks routed through it.
- **C2 wrong cleanup index (pre-existing test bug).** `addline-cleanup`
  deleted `nAdd-1` (last line) instead of the inserted line (index 3):
  counts matched while an original line died silently (caught only by
  the new docshape notes; invisible on big fixtures). Fix: delete the
  inserted index + assert the last line's word count survives.
- **C3 stale selection → multi-toggle divergence.** Unwired `SelectWord`
  writers (insert box, `AddWordToLine`) left phantom lines in the
  service set; the new multi BG toggle then acted on them while the
  reference flipped one line (caught by stress kind 9 at iter 187).
  Fix: pair every writer with the ListView + `PinLine` convergence.
- **C4 async ListView events vs sync reads.** SelectionChanged delivery
  raced back-to-back hook calls (a move executed on a stale set once).
  Fix: hooks converge the service explicitly (idempotent with the later
  event); `SetLines`/`AddLines` mirror exact event end-states.
- **C5 focus-event delivery.** Headless `Focus()` moves XAML focus
  without routing `GotFocus` (`got=0` with focus provably on the box).
  Design already robust (EnterWordEdit anchors directly); logic proven
  via handler-drive + counter; delivery is manual.
- **C6 clone ids on redo.** Copy commands re-minted fresh ids per Apply,
  breaking exact redo. Fix: mint once, reuse.
- **C7 toggle-remove in drag setup.** `SelectWord` early-returns on
  reselect-same, leaving leftovers for setup toggles to remove. Fix:
  clear-first in `Test_DragBeginWords`.
- **C8 test assertion bugs (mine).** Copy order compared 30 post-originals
  vs 26 remaining (copies don't remove — compare full pre-order);
  copy-safe clamp used post-removal count (use full count); guid-luck
  substring assert; redo left shifting later indices (undo the redo).
- **C9 suite cost.** Per-op full-doc fingerprints saturated the UI
  thread (~16s/iter on 74-line docs → 900s timeouts, twice). Fix:
  single-call `Test_DocFingerprint` (identical format) + compact doc
  before main stress + median-of-3 typing budget. Suite now ~7 min.
  (The timeouts coincided with a 4c/8GB VM running out of headroom;
  the box has since been raised to 8c/12GB.)
- **C10 carried multi-selection across commits (test flake).**
  `multi-times` no-op'd once (`times=0,1000/1000,2000 depth 21->21`):
  selection was proven good at the preceding content commit, so a
  deferred ListView selection event nulled the word primary in the
  ~50ms window before the second commit and the ribbon guard correctly
  no-op'd. Fix (test-side; product guard is correct): every multi
  commit gets a FRESH selection setup (Clear + gesture + toggle), the
  way a real user re-selects per edit; assertions unchanged. A
  `multi-dbg` note dumps selection + primary + box text pre-commit so
  any recurrence is captured with state attached.
- **C11 toggle-budget calibration (test, not product).** Median-of-6
  mixed-direction flips on dense docs: one direction ~20ms, the other
  scaling with realized words (apple 522w ~130–155 quiet; broken 1139w
  ~200–215 loaded), whole distributions shifting with host load
  (same-binary apple: 209 loaded / 170 / 151 / 128 across the day;
  broken port: 75–125 quiet morning vs ~210 midday — product toggle
  path untouched throughout, guarded RowSpacing strictly removes
  work). Fix: budget 150 → 250ms, still 60x over the ~4ms small-doc
  floor, so real regressions still trip it.

## Couldn't verify (manual steps for the user)

1. **Real IME** in SELECT vs EDIT: focus a chip → type Japanese → Space
   converts → Enter confirms (no commit) → next Enter commits one step;
   Esc mid-composition cancels IME first. Repeat for roman + insert box.
   (Guard logic covered simulated; routing proven.)
2. **Real pointer flows**: single/ctrl/shift clicks and double-click on
   chips/cards (selection + caret behavior); **real drags** — one line,
   five lines, one word, three words, cross-line word drops, copy with
   Ctrl held (label switches Move→Copy live), drop-to-self (nothing),
   Esc cancel, edge auto-scroll on a long doc, drop between cards/chips
   hit feel. Touch/pen press-drag (coded, never exercised).
3. **Real right-click**: six card zones + chip (menu + select-first),
   right-click inside multi keeps it with count labels, Shift+F10 and
   Menu key (framework-owned, untested headless).
4. **Keyboard**: arrows move selection; Shift+Up/Down extend (native,
   untrusted headless); Tab order chips → Trans/Roman → ribbon (Tab
   preserves editability by design — confirm it feels right); F2/Enter
   enter edit; Enter/Esc return focus to the line (verified by hooks,
   feel unconfirmed); Space on a focused item toggles selection natively
   (acceptable? confirm).
5. **Narrator**: cards, chips (incl. `Space ×N` names), badges with new
   tooltips, menus with counts, ghost label (not exposed — confirm
   nothing dangles), insert box, overflow flyouts.
6. **High Contrast**: accent bar/fill, word outline/wash, badges, ghost
   pill, insertion line/mark (all theme resources; OS-level pass needed).
7. **Smoothness**: 144 Hz eyeball (typing, delete/undo, drag ghost +
   indicator tracking, timer auto-scroll); video capture of a real drag.
8. **UIA suites** (no `winapp` CLI on this machine — all blocked):
   `ui-edit.ps1` (17: real keystrokes incl. typing into SELECT-state
   chips after UIA focus, context menus, insert box, BG toggle, video,
   Light+Dark screenshots), `ui-lyrics.ps1` (30 incl. layout asserts at
   widths — card columns/badges/tiers changed), `ui-tests.ps1` (13),
   `ui-tests-playerbar.ps1` (24). In-app self-driving substitutes that
   DID run: `edit-test` (above), `lyrics-test` (below), Core (above).
9. **Explorer drag interplay**: real file drag-over during an internal
   drag and vice versa (overlay is StorageItems-gated by inspection;
   untested live).
10. **Secondary-on wide bar**: turn Translation+Romanization view toggles
    on at ≥1400px — the bar may overflow (pre-existing tightness, worse
    after the Word section grew; needs a look with real window sizes).
11. **Dirty flow**: unchanged from Phase 3 (`●` exists; save/close-confirm
    is Phase 4).
12. **Ctrl+Z/Y/Shift+Z**: uncommitted typing → TextBox undo (character
    restores); committed → document undo + selection jumps to the
    change; tooltips show accelerators.
13. **Split/replace dialogs**: split `hello` at `2` → two timed words
    (weights); invalid positions ignored; replace exact-match single
    word; dialogs never appear in test mode (stubs).

---

# Phase 3 fix — 13 reported bugs (items 1–13), `winui-port` branch

Build rule (unchanged): project-only
`dotnet build winui/AmllTtmlTool.WinUI/AmllTtmlTool.WinUI.csproj
--no-incremental`, DLL timestamp checked before every run. One commit per
numbered step (`phase3fix step N`). `AGENTS.md` does not exist in this
repo; `docs/port/PROGRESS.md` rules govern. No `winapp` CLI on this
machine: real-SendInput verification is host-manual (one command per
step below); the VM runs the in-app harnesses (`edit-test`,
`lyrics-test`, Core) whose `Test_` hooks drive the same gesture/command
code paths as the pointer handlers.

The 13 reported items were consolidated into 8 fix steps (one commit
each, `a531c39`→`b36ce34`): step 1 selection model, step 2 line
numbers, step 3 chips, step 4 accelerator tooltips, step 5 ribbon word
grid, step 6 duet/V2 display, step 7 drag Ctrl latency + move FLIP +
`--perf-report`, step 8 final verification. Results below; `edit-test`
covers every item with a named check.

## Step 4: accelerator audit (the "Ctrl+O" tooltip, item 3)

Hypothesis CONFIRMED by inspection: the 8 `KeyboardAccelerator`s are
owned by the Page (`ShellPage.xaml:44-53`) with the default
`KeyboardAcceleratorPlacementMode=Auto`, so WinUI shows the accelerator
as a hover tooltip on arbitrary children (chips, cards, ribbon fields —
screenshot 4). Fix: Page-level
`KeyboardAcceleratorPlacementMode="Hidden"` (attached property on the
owning Page — it is NOT a `KeyboardAccelerator` attribute; that fails
with WMC0011). Shortcut text still shows on menu items via
`KeyboardAcceleratorTextOverride`. Full audit (complete: these are the
ONLY real accelerators app-wide; everything else is TextOverride-only
display text):

| Keys | Action | Owner | Placement |
|---|---|---|---|
| Ctrl+O | Open lyrics | Page (`OnOpenAccelerator`) | Hidden |
| Ctrl+S | Save TTML | Page (`OnSaveAccelerator`) | Hidden |
| Ctrl+Shift+S | Save Apple TTML | Page (`OnSaveAppleAccelerator`) | Hidden |
| Ctrl+N | New file | Page (`OnNewAccelerator`) | Hidden |
| Ctrl+V | Open from clipboard | Page (`OnPasteAccelerator`) | Hidden |
| Ctrl+Z | Undo (doc/TextBox-routed) | Page (`OnUndoAccelerator`) | Hidden |
| Ctrl+Y | Redo | Page (`OnRedoAccelerator`) | Hidden |
| Ctrl+Shift+Z | Redo | Page (`OnRedoAccelerator`) | Hidden |

Display-only shortcut text (no accelerator object, no tooltip source):
Edit menu Undo/Redo + compact copies (`Ctrl+Z`/`Ctrl+Y` overrides),
line/word context menus (`Ctrl+D`, `Alt+Up/Down`, `Delete` overrides),
title-bar Undo/Redo `ToolTipService` strings. List-scoped keys (Delete,
`Ctrl+D`, `Alt+Up/Down`, `Ctrl+A`, `Esc`, F2/Enter) are `KeyDown`
handlers, not accelerators — they never produce tooltips.

Harness: `tooltip-audit` pins `page=Hidden`, all 8 combos present, and
the Undo/Redo button tooltips intact. Host manual: hover 25 elements x
1.5s each — no unexpected tooltip; real buttons still show theirs.

## Steps 1–3: selection, line numbers, chips

- **Step 1 (`a531c39`) single selection model.** One source of truth:
  `SelectionService` (line set / word set / primary / anchor);
  `ListView SelectionMode="None"`; modifiers read from `e.KeyModifiers`;
  right-click select-first paints synchronously. Bug fixed: the visible
  ListView selection and the service could desync (multiple "selected"
  rows, phantom primaries). Root cause of the *unreadable* Dark
  selection was code-behind `Application.Current.Resources` lookups,
  which are NOT theme-resolved (return Light values in Dark). Fix:
  translucent overlay elements using `{ThemeResource}` — a `SelectionTint`
  Rectangle (accent, Opacity 0.2) and a chip wash (Opacity 0.15,
  ZIndex −1); the card `Root.Background` is never overwritten.
  Checks: `sel-ab-replace`, `sel-converge-30` (seed 20261014),
  `sel-ctx-full-look`, `sel-contrast`, `visible-selection`,
  `selection-indicator`, `focus-anchor`, `gesture-*`.
- **Step 2 (`29a2222`) derived line numbers.** The number is derived
  (`index+1`) and refreshed on every store change + prepare/recycle;
  sets are guarded (only when the value changes — an unconditional set
  caused 0.5 px layout drift). Bug fixed: stale numbers after
  insert/delete/move. Check: `line-numbers-200` (seed 20261015),
  `seed-and-empty`.
- **Step 3 (`fe38e03`) chips.** Space edits as text (`space-edit-text`,
  `space-add`, `space-20`, `space-verbatim`, `space-esc`, `space-labels`,
  `space-recount`); romanization shows after a `+`
  (`romaji-plus-visibility`, `romaji-gap` collapsed); insert-box lifetime
  (`insert-lifetime` stay/left/esc/emptyClosed/tab, `insert-cleanup`,
  `insert-word`). `BuildLinePanel` snaps card visibility;
  `CloseInsertOnSelectionMove` fires on `_selection.Changed`; an
  empty-Enter closes.

## Step 5: ribbon word grid (shared columns)

WinUI 3 `Grid` has NO `SharedSizeGroup`/`IsSharedSizeScope`, so the
label columns are equalized in `EditCommandBar.SyncWordLabelColumns()`
(longest label wins), wired to Loaded/Unloaded/`OnWidthTierChanged`/
`OnWordGroupSizeChanged`. VSM `MinWidth` setters are INERT when a base
`MinWidth` floor exists and **MinWidth wins over Width**, so each tier
sets BOTH `Width` and `MinWidth` per input (Narrow 72/72, Tight 56/56,
Tiny 72/72). Checks: `word-grid-geometry` (1400/1116/900/750 +
collapsed), `ribbon-tiers`, `ribbon-2row`, `ribbon-baselines`,
`ribbon-count`, `ribbon-roman-toggle` (width delta window 5–15 px).

## Step 6: duet display from raw agents (`broken_Down.ttml`)

The WASM marks ALL 88 lines of `real-broken.wasm.json` `isDuet=true`; the
fix intentionally diverges at the DISPLAY layer only. Model `IsDuet`
keeps exact WASM semantics (parity goldens unchanged). Raw `ttm:agent`
is stashed in `EditLine.Unmapped[EditLine.AgentKey]` (`"agent"`).
`DuetDisplay` (`AmllTtmlTool.Core.Lyrics`): `PrimaryAgent` = most-frequent
normalized agent among non-BG lines (tie→first non-BG; none/no-stash→
`"v1"`); `EffectiveDuet` = stashed? `NormalizeAgent != primary` :
`IsDuet`. Toggling V2/BG retires the stash (`SetLineFlagsCommand`,
restorable on undo); `ignoreSync` never touches it. Export derives from
the model (unchanged; BG-only groups emit bare spans — no agent attr).
New `AgentDisplayTests` (10, all pass). Checks:
`duet-display-badges` (88 lines, primary `v1`),
`duet-display-export` (`p=82 v2=82 v1=0` — the 6 agentless lines),
`duet-toggle-standard` (retire → `v2/v1=81/1`, re-toggle → `v2=82`).

## Step 7: drag Ctrl latency, move animation, `--perf-report`

- **Ctrl latency:** the drag ghost label (`Move`↔`Copy`) now follows
  Ctrl live by polling `IsCtrlDown()` on the drag timer (≤16 ms,
  `ScrollTick`) instead of waiting for key events. Check: `gesture-ctrl`,
  `Test_DragGhost`.
- **Move animation:** composition-thread FLIP (`RunWithMoveFlip`) —
  affected VISIBLE cards glide from their old offset to the new layout
  in 175 ms with the Fluent decelerate (no per-frame layout work);
  >12 moving cards snap + opacity-pulse instead; animations-off snaps;
  the ListView `RepositionThemeTransition` is parked during the op so
  heavy TextBox cards are never layout-animated. Wired into
  `MoveSelectedLines` and drag-drop (`ReorderLinesCommand`). Regression:
  all 26 `drag-*`, `multi-move*`, `moveline` checks green.
- **`--perf-report <path>` mode** (this is a MEASUREMENT; always reports
  OK when it completes — frame targets are advisory and only
  trustworthy on a real host). Host command (ONE command):
  `pwsh scripts/run-harness.ps1 -Mode perf-report -Args @('<path>') -TimeoutSec 900`.
  It builds a 300×3 doc and samples `CompositionTarget.Rendering` across
  move-one-line, drag-5-lines, toggle-word-roman-300, and a scroll sweep,
  writing `frames median p95 max over2x over3x wallMedian wallMax ops`
  per scenario. This VM is occluded/headless (documented), so its
  frame samples read UNRELIABLE/FAIL from compositor jitter; wall times
  are valid (`move-line` wallMedian ~42–65 ms, `drag-5lines` ~188–393 ms,
  `toggle-wordroman-300` ~66–166 ms, `scroll-sweep` ~164–340 ms). The
  host run gives the authoritative frames.

## Step 8: final results (measured)

Final binary `AmllTtmlTool.WinUI.dll` 2026-10-15 02:48:47Z; all runs via
`scripts/run-harness.ps1`.

| Suite | Result |
|---|---|
| Core `dotnet test` | **430/431** (1 pre-existing, see below) |
| `edit-test` (in-app, 193 checks) | **PASS** 523.5 s, 0 FAIL |
| `lyrics-test` apple | **PASS** 19.5 s |
| `lyrics-test` broken | **PASS** 18.7 s |
| `lyrics-test` standard LRC | **PASS** 12.4 s |
| `selftest` | **PASS** 2.9 s |
| `perf-report` (VM, advisory) | completed OK (frames unreliable here) |
| `ui-edit` / `ui-lyrics` / `ui-tests` / `ui-tests-playerbar` | **BLOCKED** (no `winapp` CLI on this machine — host-manual) |

Pre-existing unrelated Core failure (not introduced here):
`LyricAdapterTests.Fractional_Ms_Reported_With_Source_And_Count`.

Contrast (alpha-correct, theme-dictionary bases; card `#2B2B2B`
Dark / text `#FFFFFF`):

| Theme | Accent | Ratio |
|---|---|---|
| Dark | salmon `#F46762` | 10.67 (live probe `sel-contrast`) |
| Dark | blue | 11.80 |
| Dark | yellow | 8.50 |
| Light | salmon | 13.47 |
| Light | blue | 12.75 |
| Light | yellow | 15.45 |

`edit-test` check names (193, all `ok`, 0 `FAIL`) include the six
Phase-3-fix groups: selection (`sel-*`, `visible-selection`,
`selection-indicator`, `focus-anchor`, `gesture-*`), line numbers
(`line-numbers-200`, `seed-and-empty`), chips (`space-*`, `romaji-*`,
`insert-*`), tooltips (`tooltip-audit`), ribbon (`ribbon-*`,
`word-grid-geometry`), duet (`duet-*`), plus the untouched baseline
(`card-*`, `chip-*`, `drag-*`, `ribbon-*`, `perf-*`, `stress-200`).

---

# Phase 3 fix round 2 — host-reported residuals (13 items re-driven)

Branch `winui-port`, commits `c93afcd`→`28e25d6` + step-8 (this step).
All runs project-build `--no-incremental` with DLL timestamp checked;
stray `AmllTtmlTool.WinUI` processes killed before building (one lock hit).
`edit-test` fixture: staged temp copy of `C:\Lyrics\broken_Down.ttml`
(88 lines); run WITHOUT `-Args` once loaded the report file itself as the
doc (2 garbage lines) — always pass `-Args @('<staged copy>')`.

Root causes found (beyond the previous round):

1. **Unreadable selection (Dark): code-created chip brushes bypassed
   theme resolution.** Line tint/pill are XAML `{ThemeResource}` (correct);
   chip outline/wash/space-label used
   `(Brush)Application.Current.Resources[…]` (`EditPage.xaml.cs`
   `BuildChip`), which returns Light values in Dark/HC. Fix: theme-aware
   resolver (`ResolveThemeColor` walks merged `ThemeDictionaries` for the
   live theme/HC, `MakeThemeBrush` at chip build, `RefreshChipThemeBrushes`
   on `ActualThemeChanged` + `UISettings.ColorValuesChanged`; selection
   passes touch visibility only). Key probe lesson: the card dictionary
   color is TRANSLUCENT (`#0DFFFFFF` — a layer over the page), so contrast
   math must composite card-over-page first (as `sel-contrast` does); the
   first `sel-chip-contrast` probe composited over the raw color and read
   1.17. Fixed probe: text `#FFFFFF` vs 15% `#F46762` wash over `#2B2B2B`
   → **11.52** (bar ≥ 4.5:1). Theme-debug pin: `Test_ThemeDebug`.
2. **Keyboard range missing:** pointer Ctrl/Shift complete; `Shift+Up/Down`
   did nothing. Added `ExtendLineSelection` (anchor..primary via
   `SelectLineRange`, same convergence as Shift+click) + `sel-keyboard-range`
   check (`2,3` / `2,3,4` / `2,3`).
3. **Space edit box ~18px for 1 space:** caret technically visible but
   cramped. `ShowChipEditor` now floors `MinWidth=40` (reset to 0 on return
   to label so SELECT hug is untouched); `space-edit-width` pins
   `visible:1;width:40.00`.
4. **Line numbers:** prepare now derives from
   `_store.Document.Lines.IndexOf(line)` (container index can lag during
   structural layout) with container-index fallback; recycle clears the
   number so pooled cards never flash another line's digits.
   `line-numbers-200` (seed 20261015) green.
5. **Accelerator audit:** only 8 accelerators exist app-wide, all Page-owned
   `Hidden` (verified by XAML grep); new `tooltip-no-local-accel` guard
   walks the whole Edit subtree asserting zero container-owned
   accelerators (`ok:8912` nodes).
6. **Word grid at 672:** Tiny tier collapses the group by design; the check
   now asserts `vis:0` in BOTH roman states (not just the last).
7. **Duet export:** added byte-equality `exp2 == exp0` across the
   off+on toggle cycle (stash is display-only) — holds.
8. **Drag Ctrl poll:** 16ms `DispatcherTimer` runs during pointer drags and
   the drop reads live state (≤16ms < 50ms budget); the *test hooks*
   created the timer but never started it — fixed to match the pointer
   path, pinned by `drag-ctrl-timer` (`interval:16;running:1`).
9. **Harness load flakes:** `card-gaps` gets one settle retry (mid-settle
   virtualization measures too few pairs); full-suite wall time varies
   ~2x with VM load (a no-`-Args` garbage-doc run and a 1200s-timeout run
   both traced to environment, not product). Follow-ups from the final
   runs: hug block forces wordroman ON (ambient is persisted — a killed
   run can leave it OFF); `ChipWidths` settles layout first (stale 0.00
   under load); typing budget is 5 rounds dropping high+low with the
   middle-three median vs the unchanged <16ms bar (final:
   median-p95=8.63ms, rounds [7.19,8.09,8.63,10.56,100.48]); converge
   loop gets one 400ms settle re-read before failing.

Final results (binary `AmllTtmlTool.WinUI.dll` 2026-10-15 07:12:40Z):

| Suite | Result |
|---|---|
| Core `dotnet test` | **430/431** (1 pre-existing `Fractional_Ms…`, clean-tree too) |
| `edit-test` full | **PASS** 210 lines, 136 ok / 0 FAIL, elapsed 1554.2s (staged `broken_Down` copy) |
| `lyrics-test` apple / broken / subete | **PASS** (final binary) |
| `selftest` | **PASS** (final binary) |
| `perf-report` (VM, advisory) | OK; frames UNRELIABLE here (occluded), walls: move-line ~63ms, drag-5 ~234ms, wordroman-300 ~244ms, scroll ~190ms |
| `ui-edit` / `ui-lyrics` / `ui-tests` / `ui-tests-playerbar` | **BLOCKED** (no `winapp` CLI — host-manual) |

Host commands (ONE per need):

- Perf report: `pwsh scripts/run-harness.ps1 -Mode perf-report -Args @('<path>') -TimeoutSec 900`
- Full edit verification: stage fixture first —
  `Copy-Item 'C:\Lyrics\broken_Down.ttml' "$env:TEMP\edit-fixture.ttml"; pwsh scripts/run-harness.ps1 -Mode edit-test -Args @("$env:TEMP\edit-fixture.ttml") -TimeoutSec 2400`
- Real-input UI batches need `winapp` CLI 0.7+ on the host (absent in VM):
  `winapp ui click/drag/send-keys --via send-input`, `hover` + popup
  enumeration for the 25-element tooltip sweep, screenshots per gesture.

Contrast table (live Dark probe; Light rows from the previous round):

| Theme | Accent | Ratio |
|---|---|---|
| Dark | salmon `#F46762` line 20% | 10.67 (`sel-contrast`) |
| Dark | salmon `#F46762` chip 15% | **11.52** (`sel-chip-contrast`, card `#2B2B2B`) |
| Light | salmon | 13.47 |
| Light | blue | 12.75 |
| Light | yellow | 15.45 |
| Dark | blue | 11.80 |
| Dark | yellow | 8.50 |

Blue/yellow rows are accent-swap computations from the prior round, not
re-probed live here (accent is a host setting; VM accent is salmon).
High Contrast needs an OS-level pass (theme-dictionary + system-color
paths are wired; not eyeballed).

---

# Phase 3 chip rewrite (Steps 0–5), branch `winui-port`

Rules: project-only `dotnet build
winui/AmllTtmlTool.WinUI/AmllTtmlTool.WinUI.csproj --no-incremental`, DLL
timestamp checked before every run, one commit per numbered step,
`EditDocument`-only mutations, theme resources, no suite regresses.
Tag `before-chip-rewrite` is created before Step 2.

## Step 0 — real-input test infrastructure (commit fa37132 + fixup)

- `scripts/check-input-env.ps1`: session≠0, desktop unlocked, screenshot
  non-black — all PASS, nothing missing.
- `scripts/input.ps1`: user32 input, VM-local only. Moves (absolute,
  cursor-verified), clicks/drags (SetCursorPos + `mouse_event` — SendInput
  button flags were PROVEN dead here: moves+keys arrived, buttons never
  did; desktop-menu pixel proof), wheel, key chords/holds, unicode typing,
  UIA rect/focus by AutomationId, PrintWindow shots + full-screen capture.
- `--input-trace <path>` (orthogonal flag, `AMLL_INPUT_TRACE` env):
  per-event type/position/modifiers/handled/source + selection
  before/after on card/chip press, release gesture/drop, list/edit keys,
  GotFocus anchors, context select-first, drag begin.
- Pixel selection checks (`Test_PixelSelectionAsync`): page render sampling
  vs service sets, SELF-CALIBRATING (headless RTB resolves theme resources
  lighter than the compositor — expectations come from the render, contrast
  math stays in the brush probes). `ok:L1W0`, `ok:L2W0` live.
- `--input-hold <file>`: interactive target serving `pixel-*/sel-*` request
  files for input.ps1 drivers until `stop.req` (+ `AMLL_HOLD_MINUTES`
  override; LyricsCheck hold got the same override).
- Baseline Ctrl+click trace (current build, before changes): press arrives
  with `e.KeyModifiers=None` despite physical Ctrl held; press skipped via
  `press-in-focused-box` (native focus lands in the chip synchronously on
  mouse-down, before routed handlers); no arm → release no-ops. Ghost
  reproduced with triple evidence: service `svcL=[0,2]`, pixels `ok:L2W0`,
  trace showing skip + `AnchorWord` additive line. Root-cause notes for
  Steps 2–3: press guard too broad (any focused box in chip, incl. initial
  focus), `AnchorWord` line-additive, `e.Key` reads `None` for Esc/Ctrl-up
  (use `OriginalKey`), `e.KeyModifiers` untrustworthy for injected input.
- Screenshots viewed+described: app render, desktop menu proof, ghost
  (lines 1+3 fully tinted with pills, ribbon `Multiple...`).

## Step 1 — profile table, CURRENT build (`--profile`, staged broken_down)

Doc 88 lines, avg 12.9 words/line; realized=7, textboxes=216, elements=3628.

| Stage | n | median | p95 | max |
|---|---|---|---|---|
| prepare.total / card | 84 | 65.21ms | 264.89ms | 376.02ms |
| prepare.chips (of total) | 84 | 62.78ms | 264.44ms | 375.13ms |
| prepare.chipword (per word) | 84 | 4.92ms | 18.41ms | 34.10ms |
| prepare.fields/flags/number/selection | 84 | ≤0.20ms | ≤0.62ms | ≤2.3ms |
| panel measure / call | 88 | — | — | — (avg 114–169ms × 88 calls) |
| panel arrange / call | 85 | — | — | — (avg ~3–4ms) |
| realize.scrollStep (ScrollIntoView) | 88 | 194.87ms | 409.44ms | 545.24ms |
| pipe.move-up cmd/layout/frame | 5 | 1159.08 / 3.66 / 224.84ms | 1837.88 / 1531.41 / 514.59ms |
| pipe.move-down cmd/layout/frame | 5 | 985.72 / 0.36 / 50.86ms |
| pipe.dup cmd/layout | 5 | 52.71 / 376.95ms |
| pipe.delete cmd/layout | 5 | 389.88 / 2.00ms |
| scroll.into-view + sync layout | 22 | 2158.22ms | 3222.85ms | 3340.36ms |
| scroll.linedown (steady) | 20 | 4.59ms (first cold 713ms) |
| scroll.thumbsnap/pagedown | 6 | ~5ms steady (first cold ~3s) |
| maxrestore layout | 2 | ~40–140ms + first-frame ~400–530ms |

Verdict: hypothesis CONFIRMED and extended. Per-card cost is chip
creation (~5ms/word for 2 TextBoxes + overlays + wiring); the MULTIPLIERS
are panel re-measure (~114ms/call, re-triggered constantly) and synchronous
full `UpdateLayout` (~1–2s: ScrollIntoView+layout 2.1s median explains the
empty viewport — realization can't keep up; move ~1s explains "2 seconds
before anything moves"). `PrimaryAgent`/`IndexOf` fears UNFOUNDED
(flags 0.11ms). Step 2 follows the data: kill per-word TextBoxes (shared
editor), kill re-measure storms, kill sync full layouts.
