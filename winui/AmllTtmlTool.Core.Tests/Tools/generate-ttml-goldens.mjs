// generate-ttml-goldens.mjs — offline parity goldens for the C# TTML engine.
// Runs the ORIGINAL Rust/WASM parser+serializer (no network: repo
// node_modules only) over the committed fixtures and D:\Lyrics originals,
// saving paired goldens next to them:
//
//   Fixtures/<name>.xml        input TTML (committed)
//   Fixtures/<name>.wasm.json  ttmlToAmll + TS-wrapper transforms + legacy
//                             repair of <name>.xml (i.e. the exact app path)
//   Fixtures/<name>.wasm.xml   amllToTtml of the postprocessed JSON (committed)
//
// The TS-wrapper transforms (ttml-processor/index.ts) are reimplemented
// below and marked WRAPPER so any drift is visible:
//   WRAPPER-1 trailing-space word split (/(\s+)$/)
//   WRAPPER-2 metadata strip (timingMode, language) + rename
//             (songwriters->songwriter, title->musicName)
//   WRAPPER-3 export postprocess (Math.round all times, drop emptyBeat
//             null/0, drop blank romanWord)
// The C# tests compare TtmlParser/TtmlNormalize output against .wasm.json
// and TtmlGenerator output (flags all false) against .wasm.xml byte-for-byte
// (config passed here: lineTiming:false, useAppleFormatRules:false,
// format:false — same as the C# test call).
// Run: node winui/AmllTtmlTool.Core.Tests/Tools/generate-ttml-goldens.mjs
// (Working directory is irrelevant; paths resolve from this file.)

import { readdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = join(here, "..", "Fixtures");
const wasm = await import(
	"file:///C:/Users/caake/amll-fork/amll-ttml-tool/src/modules/ttml-processor/wasm/ttml_processor_wasm.js"
);

const IGNORED_METADATA = new Set(["timingMode", "language"]);
const RENAMED_METADATA = { songwriters: "songwriter", title: "musicName" };
// Export direction (normalizeExportMetadata): only songwriter->songwriters.
// (title/musicName both become amll:meta musicName inside the engine.)
const EXPORT_RENAMED_METADATA = { songwriter: "songwriters" };

function wrapperSplitTrailingSpaces(line) {
	// WRAPPER-1 (ttml-processor/index.ts ttmlToAmll).
	const newWords = [];
	for (const word of line.words) {
		const m = word.word.match(/(\s+)$/);
		if (m && word.word !== m[0]) {
			newWords.push({ ...word, word: word.word.slice(0, -m[0].length) });
			newWords.push({ startTime: 0, endTime: 0, word: m[0] });
		} else {
			newWords.push(word);
		}
	}
	return { ...line, words: newWords };
}

function wrapperImport(result) {
	// WRAPPER-2.
	return {
		...result,
		lyricLines: result.lyricLines.map(wrapperSplitTrailingSpaces),
		metadata: result.metadata
			.filter((m) => !IGNORED_METADATA.has(m.key))
			.map((m) => ({ ...m, key: RENAMED_METADATA[m.key] ?? m.key })),
	};
}

function wrapperPostProcess(result) {
	// WRAPPER-3 (postProcessLyricLines).
	const round = (w) => ({
		...w,
		startTime: Math.round(w.startTime),
		endTime: Math.round(w.endTime),
	});
	return {
		...result,
		lyricLines: result.lyricLines.map((line) => ({
			...line,
			startTime: Math.round(line.startTime),
			endTime: Math.round(line.endTime),
			words: line.words.map((word) => {
				const w = round(word);
				if (w.ruby) w.ruby = w.ruby.map(round);
				if (w.emptyBeat == null || w.emptyBeat === 0) delete w.emptyBeat;
				if (!w.romanWord || w.romanWord.trim() === "") delete w.romanWord;
				return w;
			}),
		})),
	};
}

// repairLegacyImport port (legacyCompat.ts, verbatim logic). agentIds come
// from raw parseTtml (TTMLResult lines), exactly like the TS version.
function repairLegacyImport(amllResult, rawText) {
	const fixes = [];
	const lines = amllResult.lyricLines.map((l) => ({
		...l,
		words: l.words.map((w) => ({ ...w })),
	}));
	const result = {
		...amllResult,
		lyricLines: lines,
		metadata: amllResult.metadata.map((m) => ({ ...m, value: [...m.value] })),
	};
	try {
		const parsed = wasm.parseTtml(rawText);
		if (parsed.success) {
			const agentIds = parsed.data.lines.map((l) => l.agentId ?? null);
			let pIndex = 0;
			let currentAgent = agentIds[0] ?? null;
			let duetFixed = 0;
			for (const line of lines) {
				if (!line.isBG) {
					currentAgent = agentIds[pIndex] ?? null;
					pIndex++;
				}
				const shouldBeDuet =
					currentAgent !== null &&
					currentAgent !== "v1" &&
					currentAgent !== "";
				if (shouldBeDuet && !line.isBG && !line.isDuet) {
					line.isDuet = true;
					duetFixed++;
				}
			}
			if (duetFixed > 0) fixes.push(`restored duet flag on ${duetFixed} line(s)`);
		}
	} catch { /* TS swallows too */ }
	const isBlank = (w) => w.word.trim() === "";
	let parenFixed = 0;
	for (const line of lines) {
		if (!line.isBG) continue;
		const timing = line.words.filter((w) => !isBlank(w));
		if (timing.length === 0) continue;
		const first = timing[0];
		const last = timing[timing.length - 1];
		if (!/^[(（]/.test(first.word)) {
			first.word = `(${first.word}`;
			parenFixed++;
		}
		if (!/[)）]$/.test(last.word)) {
			last.word = `${last.word})`;
			parenFixed++;
		}
	}
	if (parenFixed > 0) fixes.push("restored BG parentheses");
	let emptied = 0;
	for (const line of lines) {
		if (line.words.length === 0) continue;
		if (line.words.every((w) => w.word === "")) {
			line.words = [];
			emptied++;
		}
	}
	if (emptied > 0) fixes.push(`preserved ${emptied} empty interlude(s)`);
	return { result, fixes };
}

function processFile(xmlPath, jsonPath, regenPath, config) {
	const xml = readFileSync(xmlPath, "utf8");
	const parsed = wasm.ttmlToAmll(xml, {});
	if (!parsed.success) {
		console.log(`SKIP ${basename(xmlPath)}: ttmlToAmll failed: ${JSON.stringify(parsed.error).slice(0, 160)}`);
		return false;
	}
	const wrapped = repairLegacyImport(wrapperImport(parsed.data), xml);
	writeFileSync(jsonPath, JSON.stringify(wrapped.result, null, 1) + "\n");
	// WRAPPER-4 (importer backfill, useFileOpener.ts): xml:lang becomes
	// language metadata before generation (the app path, not the raw
	// wrapper path).
	const regenInput = wrapperPostProcess(wrapped.result);
	const lang =
		/<tt\b[^>]*\bxml:lang\s*=\s*"([^"]+)"/i.exec(xml)?.[1]?.trim() ??
		/<tt\b[^>]*\bxml:lang\s*=\s*'([^']+)'/i.exec(xml)?.[1]?.trim() ??
		"";
	if (lang && !regenInput.metadata.some((m) => m.key.toLowerCase() === "language")) {
		regenInput.metadata.push({ key: "language", value: [lang] });
	}
	// Export rename (what ttmlLyricToAmllResult does before amllToTtml;
	// C# TtmlGenerator.NormalizeForExport does the same internally).
	regenInput.metadata = regenInput.metadata.map((m) => ({
		...m,
		key: EXPORT_RENAMED_METADATA[m.key] ?? m.key,
	}));
	const regen = wasm.amllToTtml(regenInput, {}, config);
	if (!regen.success) {
		console.log(`SKIP regen ${basename(xmlPath)}: ${JSON.stringify(regen.error).slice(0, 160)}`);
		return true;
	}
	writeFileSync(regenPath, regen.data);
	return true;
}

// Generator flags per fixture (must match the C# test call per golden).
const CONFIGS = {
	"gen-rich-pretty": { lineTiming: false, useAppleFormatRules: false, format: true },
	"gen-apple-rules": { lineTiming: false, useAppleFormatRules: true, format: false },
	"gen-linetiming": { lineTiming: true, useAppleFormatRules: false, format: false },
};
const defaultConfig = { lineTiming: false, useAppleFormatRules: false, format: false };

let ok = 0;
for (const f of readdirSync(fixtures).filter((f) => f.endsWith(".xml") && !f.endsWith(".wasm.xml")).sort()) {
	const base = f.slice(0, -".xml".length);
	if (processFile(join(fixtures, f), join(fixtures, base + ".wasm.json"), join(fixtures, base + ".wasm.xml"), CONFIGS[base] ?? defaultConfig)) ok++;
}

// BG-only groups do not survive a WASM parse (bare spans read back as main
// lines — verified WASM behavior, C# matches it). Their .wasm.xml goldens
// above are therefore regen-from-main-flags and do NOT match the committed
// fixtures. Regenerate those two from hand models mirroring the C# test
// models (L([W(200,300,"bg")], "","", bg, duet, 0, 900), Compact flags).
for (const [base, duet] of [["gen-lead-bg", false], ["gen-lead-duetbg", true]]) {
	const model = {
		lyricLines: [{
			words: [{ startTime: 200, endTime: 300, word: "bg" }],
			translatedLyric: "", romanLyric: "", isBG: true, isDuet: duet,
			startTime: 0, endTime: 900,
		}],
		metadata: [],
	};
	const regen = wasm.amllToTtml(model, {}, defaultConfig);
	if (!regen.success) {
		console.log(`SKIP hand regen ${base}`);
		continue;
	}
	writeFileSync(join(fixtures, base + ".wasm.xml"), regen.data);
	const committed = readFileSync(join(fixtures, base + ".xml"), "utf8");
	console.log(`hand-regen ${base}: matches committed fixture? ${regen.data === committed}`);
}

const lyricsDir = "D:/Lyrics";
const realFiles = ["apple_No Poi!.ttml", "broken_Down.ttml"];
for (const name of realFiles) {
	const src = join(lyricsDir, name);
	if (!existsSync(src)) {
		console.log(`SKIP real file (absent): ${name}`);
		continue;
	}
	const base = "real-" + (name.startsWith("apple_") ? "apple" : "broken");
	if (processFile(src, join(fixtures, base + ".wasm.json"), join(fixtures, base + ".wasm.xml"), defaultConfig)) ok++;
}

console.log(`goldens written for ${ok} file(s)`);
