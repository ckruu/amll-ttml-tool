# Porting notes — Lyric core (Phase 1, Step 0 study)

Reference: the original web app under `src/` + `@applemusic-like-lyrics/*`
npm packages + the Rust→WASM TTML engine. All original sources are READ-ONLY.
Behavior is ported, not code structure. No GUI in this phase.
Paths below are repo-relative; `src/...` means the original web app.

Study method: full reads of the files listed in §g plus live node probes
against the real JS/WASM code (offline, `node v22.23.1`, repo
`node_modules`). Probe scripts were temp-only, never committed. Verbatim
quotes are marked with file:line.

Locked decisions from the task brief + Q&A (not re-argued here):
- New `AmllTtmlTool.Core.Lyrics` namespace, mutable int-ms model; existing
  immutable records/parser/generator stay untouched as import/export
  interchange. Adapters both directions; `records → edit → records` equality
  on every fixture; serialized output byte-identical to WASM goldens.
- No time-based undo coalescing (the original has none). History depth 100
  (named constant). Composite ops = one step.
- `D:\Lyrics` (3 files) is READ-ONLY input; copy to temp first.
- YRC/QRC/LYS/ESLRC plug into the registry in Phase 4 (studied here anyway).

## a. Data model as it really is

App model — `src/types/ttml.ts` (verbatim):

```ts
export interface TTMLMetadata { key: string; value: string[]; error?: boolean; }
export interface TTMLLyric { metadata: TTMLMetadata[]; lyricLines: LyricLine[]; }
export interface LyricWordBase { startTime: number; endTime: number; word: string; emptyBeat?: number; }
export interface LyricWord extends AMLLLyricWord {
    id: string; startTime: number; endTime: number; word: string;
    obscene: boolean; emptyBeat: number;
    romanWarning?: boolean; ruby?: LyricWordBase[];
}
export const newLyricWord = (): LyricWord => ({
    id: uid(), startTime: 0, endTime: 0, word: "",
    obscene: false, emptyBeat: 0, romanWord: "",
});
export interface LyricLine extends AMLLLyricLine {
    id: string; words: LyricWord[];
    startTime: number; endTime: number; ignoreSync: boolean;
    endTimeLink?: { originalEndTime: number; originalNextStartTime: number | null; };
}
export const newLyricLine = (): LyricLine => ({
    id: uid(), words: [], translatedLyric: "", romanLyric: "",
    isBG: false, isDuet: false, startTime: 0, endTime: 0, ignoreSync: false,
});
```

Inherited from `@applemusic-like-lyrics/lyric` (`LyricWord`: +`romanWord?`;
`LyricLine`: +`words/translatedLyric/romanLyric/isBG/isDuet/startTime/endTime`).
User's recollection is confirmed, plus three fields it missed:
`romanWarning?` (set by romanization tooling, not studied — Phase 2+),
`ruby?` (furigana segments, same shape as a word), `endTimeLink?`
(sync-chaining snapshot, §d). `emptyBeat` is a COUNT (integer semantics,
typed `number`); `obscene` is per-word bool.

IDs: `uid()` per line AND per word, assigned fresh on EVERY import
(`normalizeLyricLines`/`normalizeAmllLyricResult`, `useFileOpener.ts:136-174`).
IDs are never stable across import; selection sets reference them.

Time units: **milliseconds, JS `number`**, everywhere (model, WASM boundary,
lib parsers). Whole-ms in practice but NOT enforced by types:
- Export rounds: `postProcessLyricLines` (`ttml-processor/index.ts:162-195`)
  `Math.round`s line/word/ruby start+end; drops `emptyBeat` when
  null/0 (`→ undefined`); drops blank `romanWord` (`→ undefined`).
- LRC: `Math.round(totalSeconds * 1000)` (`parse-lrc.ts:28`); fraction
  `0.${digits}` parsed as DECIMAL (`[00:01.1]` = 100ms, NOT 1ms — probe P4).
- YRC: integer ms observed (`[1,2000]` → line start=1, end=2001; words 0/500/1000).
- WASM `ttmlToAmll` output observed all-integer on the duet/BG probe.
- Serializers round again (`formatTime`: `Math.round`; `msToTimestamp`:
  `Math.round`). So fractional ms can only enter via hand-built docs or
  exotic lib outputs — the C# adapter asserts whole-ms and reports the
  format instead of silently rounding (none found yet; see §f).

No Unicode normalization anywhere (text compared/stored verbatim; LRC
`.trim()`s the line text only — leading/trailing whitespace of the whole
line text is stripped, inner spacing kept).

## b. Import formats and exact parsing rules

Dispatch: `useFileOpener.ts:46-52,176-262` — by lowercase extension:
`lrc → local parseLrc` (NOT the lib's), `eslrc/qrc/yrc/lys → lib`,
`ttml → ttmlToAmll` (WASM + wrapper). Anything else → error toast, no throw.
Text via `file.text()` = **UTF-8, no BOM handling, no legacy fallback**
(no encoding detection in the original — C# adds BOM/UTF-16 handling per
brief §Step 4, original behavior = UTF-8).

TTML pipeline (`performOpenFile`, `useFileOpener.ts:219-250`):
1. `ttmlToAmll(text)` (WASM + trailing-space split + metadata strip/rename).
2. `repairLegacyImport` (duet restore via raw `agentId`, BG parens, empty
   interludes — see `legacyCompat.ts` verbatim in §c).
3. `normalizeAmllLyricResult`: fresh `uid()`s, `obscene ?? false`,
   `emptyBeat ?? 0`, `ignoreSync: false` (forced — TTML cannot set it).
4. Backfill `language` metadata from `xml:lang` if absent.
5. Legacy detection → info toast only, never blocks.
6. Parse failure → `TtmlErrorDialog` (rustc-style log with byte-offset caret,
   `message.ts`), document untouched.
7. Then: project-match (autosave IDB), author-metadata defaults, save-name
   suggestion (`{artists} - {musicName}.ttml`, else original name),
   dirty-confirm BEFORE all of it if `isDirty`.

LRC (local, `src/utils/parse-lrc.ts`, verbatim logic):
- Split `/\r?\n/`; tag regex `/\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g`.
- Line text = all tags removed, then `.trim()`. One event per tag
  (multi-timestamp → multiple events sharing text+index). Tag-less lines
  ignored. No metadata/offset tags (lib LRC differs — original does NOT use
  them; `[offset:]` would be ignored as tag-less text).
- Sort by (time, globalIndex) — stable for equal times.
- Group equal times; endTime = next group's time, else **+10000ms**.
  Empty-text groups dropped entirely.
- 1st text → main word/line; 2nd → `translatedLyric`; 3rd → `romanLyric`;
  4th+ → each its OWN single-word line, same span. Never throws.

ESLRC/YRC/QRC/LYS (lib v1.1.0, probed live):
- ESLRC supports word timing: `[00:01.00]<00:01.00>He<00:02.00>llo` →
  words He[1000,2000] + llo[2000,5000(next line)]; last word runs to next
  line; final line endTime = **59999999** sentinel (lib behavior, differs
  from local LRC's +10s — port faithfully per format).
- YRC: `[startMs,durMs](wStart,wDur,?)text...` line + word tuples, integers.
- QRC needs its (decrypted) text form; encrypted `.qrc` handling was not
  observed in-app (no decrypt call in `useFileOpener`) — Phase 4.
- LYS sample shape not yet established — Phase 4 (see §f).
- Lib `parseLrc` ≠ local `parseLrc`; the app deliberately uses the local one
  for `.lrc`. `stringifyLrc` emits `[mm:ss.mmm]text` + same-stamp translation
  line; `stringifyEslrc` inline word stamps + end stamp; `stringifyYrc`
  `[s,e](ws,wd,0)text` tuples (probe outputs in §g).

Plain text (`ImportFromText.tsx:46-76 defaults, :135-268` loop, verified
verbatim): modes `lyric | lyric-trans | lyric-roman | lyric-trans-roman`;
separators `interleaved-line | same-line-separator` (default `|`); word
separator default `\`; prefixes off by default (`<` BG, `>` duet, repeatable
in any order, both flags possible); empty-beat off by default (symbol `^`,
suffix-stripped repeatedly, count accumulates). `split("\n")` ONLY (`\r`
retained!); blank lines produce blank one-empty-word lines (no skip);
interleaved tail overrun yields `undefined` subs (assigned as-is — C#
normalizes to `""`, behavior-equal on all outputs); word-split even on empty
string; empty separator disables. Overwrites whole doc with `metadata: []`
via DIRECT `set(lyricLinesAtom)` (one undo step, selection NOT cleared),
behind dirty-confirm.

## c. TTML dialect

Apple TTML (what the ecosystem reads; `D:\Lyrics\apple_No Poi!.ttml` is one):
`xmlns="http://www.w3.org/ns/ttml"`, `xmlns:itunes="...lyric-ttml-internal"`,
`xmlns:ttm="...#metadata"`, `itunes:timing="Word"`, `xml:lang`. `<head>`
agents (`<ttm:agent type="person" xml:id="v1|v2"/>`, only when duet).
`<body><div><p begin end [ttm:agent]>` per line; `<span begin end>` per
word/timing unit; BG as `<span ttm:role="x-bg" begin end>` INSIDE the `<p>`
(own begin/end = span extent); translations/romanizations as WASM-side
`SubLyricContent{language?,text,words?}` blocks; ruby as `RubyTag[]`;
per-syllable `endsWithSpace/obscene/emptyBeat`. Empty interludes = `<p>`
with empty/all-empty words. BG text convention: parentheses (ASCII or
fullwidth — repair/export both accept `(` `（` `)` `）`).

Agent/duet mapping (WASM `ttmlToAmll`, probed): `ttm:agent="v1"` (or absent)
→ main line `isDuet:false`; any other agent id → `isDuet:true`. BG spans
become SEPARATE lines (`isBG:true`, inheriting the `<p>` agent's duet flag).
Metadata out: `language`, `timingMode` (+ file-specific keys); the TS
wrapper then STRIPS `timingMode,language` and renames
`songwriters→songwriter`, `title→musicName` (export reverses
`songwriter→songwriters`).

Serializer writes:
- WASM `amllToTtml` (probed): `<tt … xmlns:tts … xmlns:amll …>` +
  agents head + `<body dur="7.000"><div begin end>` + `<p begin="1.000"
  end="4.000" itunes:key="L1" ttm:agent="v1">` + word spans +
  `<span ttm:role="x-bg" …>(bg)</span>` (parens ADDED). Times = seconds,
  3 decimals. `itunes:key="L{n}"` line ids. `lineTiming` flag auto-set when
  every non-BG line has exactly 1 word (`isLineTimingLyrics`).
- Pure-TS `toAppleTTML` (`appleTtml.ts:141-230`, read in full): groups
  main+following-BGs (leading-BG runs form agent-only groups); empty lines
  emit bare `<p>`; `<p>` span = min/max over main+BG timing words (else line
  times); main content gets exactly one trailing space before the x-bg
  wrapper (added/collapsed); `ttm:agent` only when duet and not empty;
  `xml:lang` = opt > `language` meta > `lang` meta > `"en"`;
  `formatTime` = `hh:mm:ss.mmm` rounded. Warnings (not errors): single-span
  full-line coverage (Cider can't animate), untimed single words.
- `TranslationOutputMode`: `apple-music` (default) vs `amll` — feeds
  `useAppleFormatRules` generator config.

Legacy repair (`legacyCompat.ts`, verbatim): detection substrings =
`xmlns:amll`, `xmlns:tts`, `amll:meta`, `itunes:key`, `iTunesMetadata`/
`songwriters`, body `dur`, div begin/end, untimed x-bg, glued x-bg, short
timestamps. Repairs: duet restore (non-v1/non-empty agentId on non-BG
lines), BG paren restore, all-empty-word lines → `words: []` (preserved
interludes). Notice text points at Copy/Save Apple TTML for Cider.

## d. Edit-mode behavior

Mutation plumbing: everything via `useSetImmerAtom(lyricLinesAtom)`
(`editLyricLines(draft => …)`); ONE atom set = ONE history step
(`jotai-history@0.5.1`, depth 100, `states/main.ts:53-60`). NO debounce/
coalescing anywhere (searched). Text commits on Enter/blur from local field
state — typing itself never touches history. `isDirty = canUndo`.
Import / new-file / text-import / paste-TTML = one step each (plain sets);
history is NEVER cleared (no reset API used). Dirty-confirm guards open,
new-file, text-import. New-file resets selection; file-import resets
selection; TEXT import keeps selection (direct set, `ImportFromText:265`).

Word ops:
- Split (manual char indices, `split-word.tsx:161-233`): slice text at
  indices → tokens → `distributeTime` (weights: Latin/Numeric/CJK char =
  token length, punctuation = `punctuationWeight` config, whitespace = 0;
  `durationPerWeight = total/weight`, `Math.round` per token, LAST token
  snaps to original end; zero-duration → clones share span with new ids;
  zero weight → no words). New words are FRESH (`newLyricWord()` — splits
  LOSE emptyBeat/obscene/romanWord/ruby). Tip text confirms.
- Merge (`lyric-word-menu.tsx:71-112`, verified): enabled iff >1 word
  selected in ONE line; text `join("")` (NO space); span first.start→
  last.end; new id; others discarded; splice at first index.
- Add: trailing insert (optionally with empty ruby stub); insert-mode
  before-word / after-line; all `newLyricWord()` (0/0/"").
- Delete words: filter + `normalizeLineTime` iff length changed (may leave
  `words: []`).

Line ops:
- Add: `splice(idx,0,newLyricLine())` everywhere (append, above/below,
  ribbon New). Times/flags empty.
- Delete: no selection → splice line (words lost); else filter selected
  (words stripped globally, empty lines may remain); clears selection.
- Split line (`lyric-word-menu.tsx:160-210` per prior read): selected-words
  → new line AFTER source (inherits isBG/isDuet, `normalizeLineTime` both);
  "following words" variant moves clicked word + rest; whitespace-only
  untimed word deleted first.
- Combine (`lyric-line-menu.tsx:124-135`): requires ≥2 CONTIGUOUS selected;
  words appended into lowest index, `endTime` = last line's end (start
  kept), others spliced out; non-target translations discarded. Copy =
  clone with fresh ids inserted after each selected.
- Replace dialog: exact (or case-insensitive) whole-word match, text only.

Time propagation: NONE on plain edits. `normalizeLineTime` (verbatim,
7 lines): empty words → return; else
`start = words[0].start || line.start`, `end = words[last].end || line.end`
— called only on structural bound changes + word delete. Ribbon
duration-mode word edit: `end = start+delta` (or `end+delta`), clamp
`end ≥ start`, push into next word's start when abutting (iff within its
end or selected). `onSyncLineTimestamps` (bulk, behind confirm):
`line = first-word.start → last-word.end`, UNIONED with following BG line's
span. `endTimeLink` chaining (`lyric-line-view.tsx:387-478`, verified):
linking snaps next.start = this.end (and this.end = next.start at link
time), snapshots both originals; every this.end change re-snaps next.start
until unlink, which restores both snapshots; last line can't link (link
auto-removed). Sync-gated button only.

Flags: `isBG/isDuet/ignoreSync` line checks; `obscene` word check;
`emptyBeat` word int field (`parseInt → 0` on NaN); `romanWord` word text
(`""` preserved); `translatedLyric/romanLyric` line texts (20em fields);
NO ribbon ruby field (ruby via RubyEditor; `word|` shortcut seeds
`ruby: []`; ruby `|` splits segments). `romanWarning` read-only indicator.

Ribbon field→model map (`edit-mode.tsx`): New→push line; line
start/end(+duration toggle); BG/対唱/ignore-sync checks; word start/end,
emptyBeat, word text, romanWord, obscene check; translation/romanization;
rest view-only. Multi-select differing → `多个值...` placeholder; time
error red when start>end (display only, still commits). Empty selection →
disabled.

Undo/redo triggers: Edit menu + `Ctrl+Z` / `Ctrl+Y` (macOS `Shift+Ctrl+Z`),
global but suppressed while typing (TextBox gets its own undo — UI-phase
note). `Ctrl+A` = select-all AND unselect-all on the same atom (both
handlers bound; unselect defined on unreachable `Ctrl+Esc`);
`Ctrl+I` / `Ctrl+F2` = stubs (`()=>{}`); `Delete` = deleteSelection.
`Ctrl+N/O/S/M` file ops; `Shift+1/2/3` mode switch; Space/arrows/`[]'`
audio (global); D/A/S/W,R/Y,F/G/H,Home/End = SYNC-only; Q/S/W =
spectrogram audition (collide with sync keys by design).

Clipboard: NO line/word clipboard. Clipboard carries TTML TEXT only:
open-from-clipboard, save/copy-TTML, copy-Apple-TTML, paste-TTML (all route
through the same TTML import/export path; paste replaces the WHOLE doc via
`openFile(file,"ttml")`, one step). Error-log copy in the TTML error dialog.

Drag: line/word reorder = move (Ctrl = copy with fresh ids);
`normalizeLineTime` only at touched bounds. Scope selector for bulk tools:
all / selected / selected-following (first-selected → end) / custom
1-based inclusive; defaults to selected iff selection exists.
TimeShift: ±ms (delay/advance, default 100), clamps every shifted
line/word/ruby time at ≥ 0, one step.

## e. SYNC-mode / PREVIEW-mode (OUT — port later)

Pure sync: `RibbonBar/sync-mode.tsx`, `lyric-editor/components/
sync-keybinding.tsx` (F/G/H/D/A/S/W/R/Y/Home/End stamping + judge-time
offsets), `TouchSyncPanel/`, `lyric-editor/utils/lyric-states.ts` (cursor/
units) + `sync-progress.ts`, `modules/spectrogram/**`, `modules/audio/**`,
`Sidebar/BpmPanel.tsx`, `TopMenu/SyncProgressBar.tsx`, sync branches in
word/line views + `index.tsx:102-116`, `endTimeLink` BUTTON (field +
restore logic in §d still ports as data + core chaining op).
Pure preview/render: `RibbonBar/preview-mode.tsx` + `PreviewViewFields`,
`AMLLWrapper/**`, `Sidebar/PreviewPanel.tsx`, `PreviewFullscreenHost`,
`settings/states/preview.ts`, background/theme modules. Preview READ models
only — no mutations to port.

## f. UNCERTAIN / NEEDS CONFIRMATION

1. `JsError.kind` vocabulary + which malformed inputs yield which kind /
   byteOffset — enumerable in Step 3 via node probes (parse-failure inputs).
2. LYS exact syntax (probe sample failed) and QRC text form (words came back
   empty) — needed only for Phase 4; lib source is in-repo to read then.
3. `romanWarning` producers (romanization tooling) — not studied; UI-phase.
4. `translationLanguage/romanizationLanguage` options: which TTML blocks
   they select when several exist — WASM behavior, probe in Step 3.
5. `xml:lang` on EXPORT when no language metadata exists (probe kept `en` —
   default or passthrough? Step 3).
6. BOM/UTF-16 TTML/LRC inputs: original has no handling (`file.text()`
   keeps BOM → likely WASM error or BOM-in-text); C# MUST handle per brief
   — decided: strip BOM, detect UTF-16, UTF-8 fallback (deviation noted).
7. Interleaved-import `undefined` subs assigned into string fields —
   C# normalizes to `""` (output-equal; noted in §b).
8. `getCharType` CJK ranges + `punctuationWeight` default +
   `punctuationMode` merge/split config surface — read `segmentation.ts`
   head in Step 2 (split command needs exact weights).
9. Whether `timingMode` other than `Word` ever reaches the editor
   (`isLineTimingLyrics` only ADDS the flag on export; import of
   line-timing files observed fine — single-word lines).
10. Autosave/IDB, project-match, LRCLIB network, audio-metadata merge —
    app shell concerns, NOT lyric core (excluded by design, listed so nobody
    ports them here).

## g. Source map + probe log (what was actually read/run)

Fully read: `types/ttml.ts`, `states/main.ts`, `utils/timestamp.ts`,
`utils/parse-lrc.ts`, `ttml-processor/index.ts`, `types/{amll,ttml,error}.ts`,
`legacyCompat.ts`, `appleTtml.ts` (230 lines, the dialect reference),
`hooks/useFileOpener.ts`, `keyboard/commands.ts`, `ImportFromText.tsx`
(core loop), `lyric-word-menu.tsx` (merge/split/delete),
`lyric-line-view.tsx` (endTimeLink block), `normalize-line-time.ts`,
`segmentation.ts` (weights/distributeTime), `TimeShift.tsx` (shift block),
`useDialogScope.tsx` (scope), `useTopMenuActions.ts` (new/delete/sync-lines/
ruby/clipboard regions), `default-metadata.ts`, `metadata-filename.ts`,
`lyric-editor/components/index.tsx` (paste/handleNewLine), wasm `.d.ts`,
lib `.d.mts` export list (Lrc/Eslrc/LrcA2/SPL/Lyl/Lys/Qrc/TTML/Yrc/Ass/Lqe
parse+stringify, LrcLike pair, QRC hex crypt).
Live node probes (offline, temp scripts): lib ESLRC words + `59999999`
sentinel; YRC ints; LRC `0.1→100ms`; stringify Lrc/Eslrc/Yrc shapes;
WASM `ttmlToAmll` duet/BG/agents mapping + metadata keys
(`language,timingMode`); WASM `amllToTtml` output shape (namespaces,
`itunes:key`, `body dur`, BG parens). Lib `parseTTML` needs a DOMParser
(absent offline) — irrelevant: the app path is WASM.
Real files seen (NOT opened for edit): `D:\Lyrics\apple_No Poi!.ttml`
(35KB, Apple TTML), `broken_Down.ttml` (37KB, legacy/Cider-stripped),
`lrc_Wakamonono Subete.lrc` (1.5KB, standard LRC). Zero lyric fixtures in
the original repo itself (searched `*.ttml/.lrc/.yrc/.qrc/.lys/.eslrc`).
Rust/Tauri: no lyric logic (69-line lib.rs: file-open arg plumbing only).
