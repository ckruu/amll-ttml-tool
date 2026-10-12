// generate-lrc-goldens.mjs — offline parity goldens for the C# LRC importer.
// Runs the ORIGINAL local parser (src/utils/parse-lrc.ts, NOT the lib's)
// directly in node (type stripping; only module plumbing is rewired into a
// temp harness — zero logic changes) over committed .lrc fixtures plus the
// D:\Lyrics original, saving field goldens (ids excluded: uid-counter vs
// Guids differ by construction):
//   Fixtures/<name>.lrc.json  [{startTime,endTime,word,translatedLyric,romanLyric}]
// The C# tests compare LrcParser.ParseLrc field-by-field (incl. the real
// file when D:\Lyrics is present).
// Run: node winui/AmllTtmlTool.Core.Tests/Tools/generate-lrc-goldens.mjs

import { readdirSync, readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..", "..");
const fixtures = join(here, "..", "Fixtures");

const harness = join(tmpdir(), "amll-lrc-harness");
mkdirSync(harness, { recursive: true });

const parseSrc = readFileSync(join(repo, "src", "utils", "parse-lrc.ts"), "utf8")
	.replace('from "$/types/ttml"', 'from "./ttml-shim.ts"');
writeFileSync(join(harness, "parse-lrc.ts"), parseSrc);

const ttmlSrc = readFileSync(join(repo, "src", "types", "ttml.ts"), "utf8")
	.replace('from "@applemusic-like-lyrics/lyric"', 'from "./lyric-types-shim.ts"')
	.replace('"uid"', '"./uid-stub.mjs"');
writeFileSync(join(harness, "ttml-shim.ts"), ttmlSrc);

writeFileSync(join(harness, "lyric-types-shim.ts"), `export interface LyricWord {
	startTime: number; endTime: number; word: string; romanWord?: string; }
export interface LyricLine { words: LyricWord[]; translatedLyric: string;
	romanLyric: string; isBG: boolean; isDuet: boolean;
	startTime: number; endTime: number; }
`);
writeFileSync(join(harness, "uid-stub.mjs"),
	`let n = 0; export function uid() { return "u" + (++n); }\n`);
writeFileSync(join(harness, "package.json"), `{ "type": "module" }\n`);

const { parseLrc } = await import("file:///" + join(harness, "parse-lrc.ts").replace(/\\/g, "/"));

function slim(lines) {
	return lines.map((l) => ({
		startTime: l.startTime,
		endTime: l.endTime,
		word: l.words[0]?.word ?? "",
		translatedLyric: l.translatedLyric,
		romanLyric: l.romanLyric,
	}));
}

let ok = 0;
for (const f of readdirSync(fixtures).filter((f) => f.endsWith(".lrc")).sort()) {
	const base = f.slice(0, -".lrc".length);
	const lines = parseLrc(readFileSync(join(fixtures, f), "utf8"));
	writeFileSync(join(fixtures, base + ".lrc.json"), JSON.stringify(slim(lines), null, 1) + "\n");
	console.log(`${f}: ${lines.length} line(s)`);
	ok++;
}

const realSrc = "D:/Lyrics/lrc_Wakamonono Subete.lrc";
if (existsSync(realSrc)) {
	const lines = parseLrc(readFileSync(realSrc, "utf8"));
	writeFileSync(join(fixtures, "lrc-real.lrc.json"), JSON.stringify(slim(lines), null, 1) + "\n");
	console.log(`lrc_Wakamonono Subete.lrc: ${lines.length} line(s)`);
	ok++;
} else {
	console.log("SKIP real file (absent)");
}
console.log(`goldens written for ${ok} file(s)`);
