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
